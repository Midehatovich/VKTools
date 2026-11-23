using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Progress;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Spec
{
    /// <summary>
    /// «Марки и поставщики» (0.15.0): марка, код изделия, завод-изготовитель по первой подходящей строке таблицы (маска наименования или
    /// «Семейство : Тип»). Категории — как у копии ADSK_ → Р_, с вложенными. Служебные позиции _SYS_ не трогаются (их графы — в «Расчётных
    /// позициях»), изоляция — если марку и завод пишет «Изоляция». По умолчанию заполняются только пустые значения.
    /// </summary>
    internal sealed partial class SpecPlanner
    {
        private void PlanVendor(SpecOptions options, SpecDefaults defaults)
        {
            const SpecModule M = SpecModule.Vendor;
            VendorSettings vd = options.Vendor;
            if (options.IsDefault(defaults))
            {
                _plan.RuleSources.Add("Марки и поставщики: по умолчанию (таблица пустая — импортируйте правила объекта)");
            }
            else
            {
                _plan.RuleSources.Add("Марки и поставщики: настройка проекта (" + (!options.SavedInProject ? "изменена в окне, в модели не сохранена" : options.SavedStamp ?? "сохранена в модели") + ")");
            }
            if (defaults.ProjectBlocked != null) Block(defaults.ProjectBlocked);

            var tableIssues = new List<ConfigIssue>();
            List<VendorRule> rules = VendorSettings.ParseRules(vd.Rules, tableIssues);
            foreach (ConfigIssue issue in tableIssues) AddConfigIssue(M, issue);
            if (tableIssues.Any(i => i.Severity == Severity.Error)) Block("в таблице «Марки и поставщики» ошибки.");

            SpecProjectSettings stored = SpecSettingsStore.Read(_doc, out _);
            InsulationSettings ins = SpecOptions.Load(SpecModule.Insulation, stored, defaults, new List<ConfigIssue>(), out _).Insulation;
            long insCat = CategoryId(BuiltInCategory.OST_PipeInsulations);

            _progress.Report("Марки и поставщики: собираю элементы…");
            var elements = new List<Element>();
            foreach (BuiltInCategory bic in CopyCategories)
            {
                foreach (Element e in new FilteredElementCollector(_doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (IsServiceItem(e)) continue;
                    elements.Add(e);
                }
            }

            var candidates = new Dictionary<ParamKey, ParamCandidate>();
            foreach (Element sample in elements.Take(CandidateSamples))
            {
                foreach (Parameter p in sample.Parameters)
                {
                    if (p.StorageType != StorageType.String) continue;
                    ParamKey? key = _access.KeyOf(p);
                    if (key == null || key.Kind == ParamKeyKind.BuiltIn) continue;
                    if (!candidates.TryGetValue(key, out ParamCandidate? c)) candidates[key] = c = new ParamCandidate(key, ParamAccess.DataTypeLabel(p));
                    c.Nested++;
                }
            }
            if (vd.NameParam != null && !candidates.ContainsKey(vd.NameParam)) candidates[vd.NameParam] = new ParamCandidate(vd.NameParam, "—");
            _plan.Candidates.AddRange(candidates.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));

            if (vd.NameParam == null)
            {
                _plan.AddIssue(M, Severity.Error, string.Empty, "не выбран параметр наименования", string.Empty)
                    .Action = "Выберите параметр на вкладке «Настройка» (обычно Р_Наименование или ADSK_Наименование).";
                Block("не выбран параметр наименования.");
                return;
            }
            if (rules.Count == 0)
            {
                _plan.AddIssue(M, Severity.Info, string.Empty, "в таблице нет правил — ничего не пишется", string.Empty)
                    .Action = "Вкладка «Настройка» → «Импорт CSV» (правила объекта по вендор-листу) или добавьте строки вручную.";
            }

            var notTouched = new SortedDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            void Skip(string why) => notTouched[why] = notTouched.TryGetValue(why, out int n) ? n + 1 : 1;
            var missing = new SortedDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            var byRule = new Dictionary<VendorRule, int>();
            // Наименования без правила, у которых пусто хоть одно из трёх — подсказка, какие строки добавить
            var unmatched = new SortedDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            int kept = 0;

            for (int i = 0; i < elements.Count; i++)
            {
                if (i % 500 == 0)
                {
                    _progress.Report($"Марки и поставщики: {i + 1} из {elements.Count}", i, elements.Count);
                    _progress.ThrowIfCancelled();
                }
                Element e = elements[i];
                Element? type = TypeOf(e);
                string name = TextOf(e, type, vd.NameParam);
                string familyType = TypeLabel(e);
                string subject = (e.Category?.Name ?? "?") + " : " + familyType;
                if (PipeNameText.IsExcluded(name))
                {
                    Skip("наименование с «!» (не учитывать)");
                    continue;
                }
                bool isIns = e.Category?.Id.ToLong() == insCat;
                if (isIns && ins.WriteProduct)
                {
                    Skip("изоляция — марку и завод пишет «Изоляция»");
                    continue;
                }

                VendorRule? rule = VendorSettings.Match(rules, name, familyType);
                if (rule == null)
                {
                    if (VendorSettings.Roles.Any(r => TextOf(e, type, ParamProfile.TargetKey(r)).Length == 0))
                    {
                        string label = name.Length > 0 ? name : "(без наименования) " + familyType;
                        unmatched[label] = unmatched.TryGetValue(label, out int u) ? u + 1 : 1;
                    }
                    continue;
                }
                byRule[rule] = byRule.TryGetValue(rule, out int r0) ? r0 + 1 : 1;

                foreach (ParamRole role in VendorSettings.Roles)
                {
                    string value = rule.Value(role);
                    if (value.Length == 0) continue;
                    ParamKey key = ParamProfile.TargetKey(role);
                    Parameter? target = _access.Find(e, key);
                    if (target == null)
                    {
                        bool typeParam = type != null && _access.Find(type, key) != null;
                        string what = $"{key.Name} · {(typeParam ? "параметр типа (нужен экземпляра)" : "нет у категории «" + (e.Category?.Name ?? "?") + "»")}";
                        missing[what] = missing.TryGetValue(what, out int m) ? m + 1 : 1;
                        continue;
                    }
                    if (target.StorageType != StorageType.String)
                    {
                        _plan.AddIssue(M, Severity.Error, key.Name, $"параметр не текстовый ({ParamAccess.DataTypeLabel(target)})", subject, e.Id.ToLong());
                        continue;
                    }
                    string current = (target.AsString() ?? string.Empty).Trim();
                    if (string.Equals(current, value, StringComparison.Ordinal))
                    {
                        _plan.CountUnchanged(M);
                        continue;
                    }
                    if (current.Length > 0 && !vd.Overwrite)
                    {
                        kept++;
                        continue;
                    }
                    if (target.IsReadOnly)
                    {
                        _plan.AddIssue(M, Severity.Error, key.Name, "только для чтения: задан формулой или связан в семействе", subject, e.Id.ToLong());
                        continue;
                    }
                    if (Guard(M, e, target, key.Name, subject)) continue;
                    AddChange(M, e, key, current, ParamValue.OfText(value), $"строка {rule.Line} «{rule.Mask}»", null);
                }
            }

            int matched = byRule.Values.Sum();
            _plan.Notes.Add($"Марки и поставщики: элементов {elements.Count}, по правилам {matched}; {(vd.Overwrite ? "заполненные перезаписываются" : "пишутся только пустые")}"
                + (kept > 0 ? $", уже заполнено и оставлено как есть {kept}" : string.Empty) + $"; правил в таблице {rules.Count}.");
            if (notTouched.Count > 0) _plan.Notes.Add("Марки и поставщики: не трогаются — " + string.Join("; ", notTouched.Select(x => $"{x.Key}: {x.Value}")));
            foreach (KeyValuePair<string, int> m in missing)
            {
                string[] parts = m.Key.Split('·');
                _plan.AddIssue(M, Severity.Warning, parts[0].Trim(), parts[1].Trim(), string.Empty, null, m.Value)
                    .Action = "Нажмите «Применить» в «Системе и этаже»: плагин добавит свои параметры в проект (или Управление → Параметры проекта).";
            }
            foreach (VendorRule rule in rules)
            {
                byRule.TryGetValue(rule, out int n);
                string fills = string.Join(", ", VendorSettings.Roles.Where(r => rule.Value(r).Length > 0).Select(r => rule.Value(r)));
                _plan.Preview.Add($"строка {rule.Line} «{rule.Mask}» → {(fills.Length > 0 ? fills : "не трогать")}: {n} шт.");
            }
            if (unmatched.Count > 0)
            {
                _plan.Preview.Add($"— без правила и с пустой маркой/кодом/заводом ({unmatched.Count} наименований):");
                foreach (KeyValuePair<string, int> u in unmatched.OrderByDescending(x => x.Value).Take(200)) _plan.Preview.Add($"   {u.Key} — {u.Value} шт.");
                _plan.AddIssue(M, Severity.Info, string.Empty, $"без правила и с пустыми графами: {unmatched.Count} наименований (список — внизу вкладки «Настройка»)", string.Empty, null, unmatched.Values.Sum())
                    .Action = "Добавьте строки в таблицу или заполните ADSK_ в семействе.";
            }
        }

        /// <summary>Текст параметра экземпляра, а если пуст — типа.</summary>
        private string TextOf(Element e, Element? type, ParamKey key)
        {
            string value = (_access.Find(e, key)?.AsString() ?? string.Empty).Trim();
            if (value.Length == 0 && type != null) value = (_access.Find(type, key)?.AsString() ?? string.Empty).Trim();
            return value;
        }
    }
}
