using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Progress;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;
using VKTools.Revit.Mep;

namespace VKTools.Revit.Spec
{
    /// <summary>
    /// «Запас труб» (Р5): порт PipeQtyReserve v1.0.2. Количество = длина × k жёстким трубам и изоляции на жёстких трубах,
    /// k — по таблице «Коэффициенты» проекта. Гибкие трубы и изоляция фитингов не трогаются. Единицы количества — по типу данных
    /// и по самим значениям в модели; расходятся — запись запрещена (урок бага ×3,28 в v1.0).
    /// </summary>
    internal sealed partial class SpecPlanner
    {
        private const int MaxLastChangedLookups = 3000;

        private sealed class ReserveItem
        {
            public ReserveItem(Element element, ReserveTarget target, ReserveRule rule, double lengthM, Parameter parameter, string system, string subject)
            {
                Element = element;
                Target = target;
                Rule = rule;
                LengthM = lengthM;
                Parameter = parameter;
                System = system;
                Subject = subject;
            }

            public Element Element { get; }

            public ReserveTarget Target { get; }

            public ReserveRule Rule { get; }

            public double LengthM { get; }

            public Parameter Parameter { get; }

            public string System { get; }

            public string Subject { get; }
        }

        private void PlanReserve(SpecOptions options, SpecDefaults defaults)
        {
            const SpecModule M = SpecModule.PipeReserve;
            PipeReserveSettings rsv = options.Reserve;
            if (options.IsDefault(defaults))
            {
                _plan.RuleSources.Add("Запас труб: по умолчанию (ADSK_Количество; трубы и изоляция на трубах × 1,1)");
            }
            else
            {
                _plan.RuleSources.Add("Запас труб: настройка проекта (" + (!options.SavedInProject ? "изменена в окне, в модели не сохранена" : options.SavedStamp ?? "сохранена в модели") + ")");
            }
            if (defaults.ProjectBlocked != null) Block(defaults.ProjectBlocked);

            var tableIssues = new List<ConfigIssue>();
            List<ReserveRule> rules = PipeReserveSettings.ParseRules(rsv.Table, tableIssues);
            foreach (ConfigIssue issue in tableIssues) AddConfigIssue(M, issue);
            if (tableIssues.Any(i => i.Severity == Severity.Error)) Block("в таблице «Коэффициенты» ошибки.");

            long rigid = CategoryId(BuiltInCategory.OST_PipeCurves);
            _progress.Report("Запас: собираю трубы и изоляцию…");
            List<Element> pipes = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_PipeCurves).WhereElementIsNotElementType().ToList();
            int flex = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_FlexPipeCurves).WhereElementIsNotElementType().GetElementCount();
            List<InsulationLiningBase> insulations = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_PipeInsulations)
                .WhereElementIsNotElementType().OfType<InsulationLiningBase>().ToList();

            // Варианты параметра количества для окна: числа и тексты экземпляра труб
            var candidates = new Dictionary<ParamKey, ParamCandidate>();
            foreach (Element sample in pipes.Take(CandidateSamples))
            {
                foreach (Parameter p in sample.Parameters)
                {
                    if ((p.StorageType != StorageType.String && p.StorageType != StorageType.Double) || p.IsReadOnly) continue;
                    ParamKey? key = _access.KeyOf(p);
                    if (key == null || key.Kind == ParamKeyKind.BuiltIn) continue;
                    if (!candidates.TryGetValue(key, out ParamCandidate? c)) candidates[key] = c = new ParamCandidate(key, ParamAccess.DataTypeLabel(p));
                    c.Nested++;
                }
            }
            if (rsv.QtyParam != null && !candidates.ContainsKey(rsv.QtyParam)) candidates[rsv.QtyParam] = new ParamCandidate(rsv.QtyParam, "—");
            _plan.Candidates.AddRange(candidates.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));

            if (rsv.QtyParam == null)
            {
                _plan.AddIssue(M, Severity.Error, string.Empty, "не выбран параметр количества — запас не пишется", string.Empty)
                    .Action = "Выберите параметр количества на вкладке «Настройка» (обычно ADSK_Количество).";
                Block("не выбран параметр количества.");
                return;
            }
            ParamKey qtyKey = rsv.QtyParam;
            ParamKey nameKey = ParamKey.Shared(KnownParameters.AdskName.Name, KnownParameters.AdskName.Guid);

            var items = new List<ReserveItem>();
            var notTouched = new SortedDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            void Skip(string why, int n = 1) => notTouched[why] = notTouched.TryGetValue(why, out int c) ? c + n : n;
            if (flex > 0) Skip("гибкие трубы (считаются штуками)", flex);

            void Collect(Element e, ReserveTarget target, double? lengthM, string system)
            {
                Element? type = TypeOf(e);
                string typeName = type?.Name ?? string.Empty;
                string comment = type?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString()?.Trim() ?? string.Empty;
                string what = target == ReserveTarget.Pipe ? PipeReserveSettings.PipeLabel : PipeReserveSettings.InsulationLabel;
                string subject = what + " : " + typeName;
                long id = e.Id.ToLong();
                string? name = _access.Find(e, nameKey)?.AsString() ?? (type == null ? null : _access.Find(type, nameKey)?.AsString());
                if (PipeNameText.IsExcluded(name))
                {
                    Skip("наименование с «!» (не учитывать)");
                    return;
                }
                ReserveRule? rule = PipeReserveSettings.Match(rules, target, typeName, comment, system);
                if (rule == null)
                {
                    _plan.AddIssue(M, Severity.Info, string.Empty, "нет строки в таблице «Коэффициенты» — не трогается", subject, id)
                        .Action = "Если нужен запас — добавьте строку для этого типа или «*».";
                    return;
                }
                if (rule.K == null)
                {
                    Skip($"строка {rule.Line} «{what} · {rule.Mask}» — не трогать");
                    return;
                }
                if (lengthM == null || lengthM.Value < 1e-6)
                {
                    _plan.AddIssue(M, Severity.Warning, string.Empty, "нет длины — запас не посчитать", subject, id);
                    return;
                }
                Parameter? p = _access.Find(e, qtyKey);
                if (p == null)
                {
                    bool typeParam = type != null && _access.Find(type, qtyKey) != null;
                    _plan.AddIssue(M, Severity.Error, qtyKey.Name, typeParam ? "параметр ТИПА, нужен параметр экземпляра" : "нет параметра у категории", subject, id)
                        .Action = $"Управление → Параметры проекта → {qtyKey.Name} → категории «Трубы» и «Материалы изоляции трубопроводов», экземпляр.";
                    return;
                }
                if (p.StorageType != StorageType.Double && p.StorageType != StorageType.String)
                {
                    _plan.AddIssue(M, Severity.Error, qtyKey.Name, "параметр не число и не текст", subject, id);
                    return;
                }
                items.Add(new ReserveItem(e, target, rule, lengthM.Value, p, system, subject));
            }

            for (int i = 0; i < pipes.Count; i++)
            {
                if (i % 500 == 0)
                {
                    _progress.Report($"Запас: труба {i + 1} из {pipes.Count}", i, pipes.Count);
                    _progress.ThrowIfCancelled();
                }
                Element pipe = pipes[i];
                Collect(pipe, ReserveTarget.Pipe, CurveLengthM(pipe), SystemAbbreviation(pipe));
            }
            for (int i = 0; i < insulations.Count; i++)
            {
                if (i % 500 == 0)
                {
                    _progress.Report($"Запас: изоляция {i + 1} из {insulations.Count}", i, insulations.Count);
                    _progress.ThrowIfCancelled();
                }
                InsulationLiningBase insulation = insulations[i];
                Element? host = _doc.GetElement(insulation.HostElementId);
                if (host == null)
                {
                    Skip("изоляция без основы");
                    continue;
                }
                if ((host.Category?.Id.ToLong() ?? -1) != rigid)
                {
                    Skip("изоляция на фитингах, арматуре и гибких трубах (её покрывает запас)");
                    continue;
                }
                Collect(insulation, ReserveTarget.Insulation, CurveLengthM(insulation) ?? CurveLengthM(host), SystemAbbreviation(host));
            }

            // Как хранится количество: тип данных + проверка по значениям в модели
            List<ReserveItem> numeric = items.Where(x => x.Parameter.StorageType == StorageType.Double).ToList();
            QuantityKind kind = QuantityKind.Unknown;
            if (numeric.Count > 0)
            {
                Parameter p0 = numeric[0].Parameter;
                (string spec, string unit) = SpecAndUnit(p0);
                QuantityKind bySpec = ReserveMath.Classify(spec, unit);
                QuantityKind byData = ReserveMath.Calibrate(
                    numeric.Where(x => x.Parameter.HasValue).Select(x => (x.Parameter.AsDouble(), x.LengthM)),
                    rules.Where(r => r.K.HasValue).Select(r => r.K!.Value), out int hitsM, out int hitsFt, out int checkedCount);
                QuantityKind? decided = ReserveMath.Decide(bySpec, byData, out string? blocked);
                _plan.Notes.Add($"Запас: {qtyKey.Name} — тип данных «{spec}», единица «{unit}» → {ReserveMath.Text(bySpec)}; "
                    + $"по значениям {hitsM} из {checkedCount} похожи на метры, {hitsFt} — на футы → {ReserveMath.Text(byData)}. Итог: {(decided.HasValue ? ReserveMath.Text(decided.Value) : "НЕ ПОНЯТНО")}.");
                if (decided == null)
                {
                    _plan.AddIssue(M, Severity.Error, qtyKey.Name, "!!! " + blocked + " — запись запрещена", string.Empty)
                        .Action = "Сохраните отчёт (CSV) и пришлите разработчику: нужно понять, в каких единицах хранится количество.";
                    Block(blocked + ".");
                    items.RemoveAll(x => x.Parameter.StorageType == StorageType.Double);
                }
                else
                {
                    kind = decided.Value;
                }
            }

            var sums = new SortedDictionary<(string System, string What), (double Length, double Before, double After, int Count)>();
            var who = new Dictionary<(string What, string Who, string K), int>();
            bool workshared = _doc.IsWorkshared;
            int lookups = 0;
            foreach (ReserveItem item in items)
            {
                double k = item.Rule.K!.Value;
                double qty = ReserveMath.Quantity(item.LengthM, k);
                Parameter p = item.Parameter;
                double? oldM;
                ParamValue value;
                bool same;
                if (p.StorageType == StorageType.Double)
                {
                    oldM = p.HasValue ? ReserveMath.Meters(p.AsDouble(), kind) : (double?)null;
                    double raw = ReserveMath.Raw(qty, kind);
                    value = ParamValue.OfDouble(raw, ReserveMath.Number(qty, 3));
                    same = p.HasValue && ReserveMath.Same(p.AsDouble(), raw);
                }
                else
                {
                    string oldText = p.AsString() ?? string.Empty;
                    oldM = double.TryParse(oldText.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) ? parsed : (double?)null;
                    value = ParamValue.OfText(ReserveMath.Text(qty));
                    same = oldText == value.Text;
                }
                string what = item.Target == ReserveTarget.Pipe ? PipeReserveSettings.PipeLabel : PipeReserveSettings.InsulationLabel;
                var sumKey = (item.System.Length == 0 ? "(без системы)" : item.System, what);
                sums.TryGetValue(sumKey, out (double Length, double Before, double After, int Count) s);
                sums[sumKey] = (s.Length + item.LengthM, s.Before + (oldM ?? 0), s.After + (same ? oldM ?? 0 : qty), s.Count + 1);
                if (same)
                {
                    _plan.CountUnchanged(M);
                    continue;
                }
                if (p.IsReadOnly)
                {
                    _plan.AddIssue(M, Severity.Error, qtyKey.Name, "только для чтения: задан формулой или связан в семействе", item.Subject, item.Element.Id.ToLong());
                    continue;
                }
                if (Guard(M, item.Element, p, qtyKey.Name, item.Subject)) continue;
                string kNow = ReserveMath.KLabel(oldM, item.LengthM);
                string by = string.Empty;
                if (workshared && lookups++ < MaxLastChangedLookups) by = LastChangedBy(item.Element);
                var whoKey = (what, by.Length == 0 ? "—" : by, kNow);
                who[whoKey] = who.TryGetValue(whoKey, out int n) ? n + 1 : 1;
                string old = oldM.HasValue ? ReserveMath.Number(oldM.Value, 3) : string.Empty;
                AddChange(M, item.Element, qtyKey, old, value,
                    $"длина {ReserveMath.Number(item.LengthM, 3)} м × {ReserveMath.Number(k, 3)} (строка {item.Rule.Line}); сейчас {kNow}", null);
            }

            int pipesDone = items.Count(x => x.Target == ReserveTarget.Pipe);
            int insDone = items.Count(x => x.Target == ReserveTarget.Insulation);
            _plan.Notes.Add($"Запас: труб {pipesDone} из {pipes.Count}, изоляции на трубах {insDone} из {insulations.Count}; параметр {qtyKey.Name}; строк в таблице {rules.Count}.");
            if (notTouched.Count > 0) _plan.Notes.Add("Запас: не трогаются — " + string.Join("; ", notTouched.Select(p => $"{p.Key}: {p.Value}")));
            if (who.Count > 0)
            {
                _plan.Notes.Add("Запас: кто последним менял элементы, которые изменятся, и какой у них запас сейчас (k ≈ — пересчитал BIM2B или другой скрипт): "
                    + string.Join("; ", who.OrderByDescending(p => p.Value).Select(p => $"{p.Key.What} · {p.Key.Who} · сейчас {p.Key.K} · {p.Value} шт.")));
            }
            foreach (KeyValuePair<(string System, string What), (double Length, double Before, double After, int Count)> s in sums)
            {
                _plan.Preview.Add($"{s.Key.System} · {s.Key.What}: {s.Value.Count} шт., длина {ReserveMath.Number(s.Value.Length, 1)} м, "
                    + $"количество {ReserveMath.Number(s.Value.Before, 1)} → {ReserveMath.Number(s.Value.After, 1)}");
            }

            foreach (UpdaterInfo updater in SystemMergeExecutor.RegisteredUpdaters(_doc).Where(u => (u.ApplicationName + " " + u.UpdaterName).IndexOf("BIM2B", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                _plan.AddIssue(M, Severity.Warning, qtyKey.Name,
                    $"в модели зарегистрирован апдейтер «{updater.UpdaterName}» ({updater.ApplicationName}): при правке трубы или изоляции он пересчитает количество по своим настройкам", string.Empty)
                    .Action = "У всех, кто правит трубы ВК, в BIM2B на вкладках труб и изоляции должен стоять тот же запас (1,1). «Обновить все элементы типа» не нажимать.";
            }
        }

        /// <summary>Длина кривой, м: «Длина» элемента (у изоляции трубы она тоже есть).</summary>
        private static double? CurveLengthM(Element e)
        {
            Parameter? p = e.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
            if (p != null && p.HasValue && p.AsDouble() > 1e-9) return p.AsDouble() * ReserveMath.FeetToMeters;
            return (e as MEPCurve)?.Location is LocationCurve lc ? lc.Curve.Length * ReserveMath.FeetToMeters : (double?)null;
        }

        /// <summary>«Сокращение для системы» трубы (у экземпляра — RBS_DUCT_PIPE_…; RBS_SYSTEM_… — запасной вариант).</summary>
        private static string SystemAbbreviation(Element e)
        {
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.RBS_DUCT_PIPE_SYSTEM_ABBREVIATION_PARAM, BuiltInParameter.RBS_SYSTEM_ABBREVIATION_PARAM })
            {
                string value = e.get_Parameter(bip)?.AsString()?.Trim() ?? string.Empty;
                if (value.Length > 0) return value;
            }
            return string.Empty;
        }

        private static (string Spec, string Unit) SpecAndUnit(Parameter p)
        {
            string spec = string.Empty;
            string unit = string.Empty;
            try
            {
#if REVIT2022_OR_GREATER
                spec = p.Definition.GetDataType().TypeId ?? string.Empty;
#else
                spec = p.Definition.GetSpecTypeId().TypeId ?? string.Empty;
#endif
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // тип данных не прочитать — останется проверка по значениям
            }
            try
            {
                unit = p.GetUnitTypeId().TypeId ?? string.Empty;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // у «Числа» без единиц бывает исключение — это нормально
            }
            return (spec, unit);
        }

        private string LastChangedBy(Element e)
        {
            try
            {
                return WorksharingUtils.GetWorksharingTooltipInfo(_doc, e.Id).LastChangedBy ?? string.Empty;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return string.Empty;
            }
        }
    }
}
