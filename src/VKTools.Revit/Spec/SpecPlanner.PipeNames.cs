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
    /// «Имена труб» (Р5): ADSK_Наименование жёстких труб по правилу типа из таблицы «Правила» (Ду×S, Dнар×S…), числа — из сегмента.
    /// По умолчанию только проверка (REQ-PIPE-005): пока BIM2B пишет имена труб, расхождения — замечания, а не изменения.
    /// Обобщение VgpNameDuS v1.1.
    /// </summary>
    internal sealed partial class SpecPlanner
    {
        private void PlanPipeNames(SpecOptions options, SpecDefaults defaults)
        {
            const SpecModule M = SpecModule.PipeNames;
            PipeNameSettings pn = options.PipeNames;
            if (options.IsDefault(defaults))
            {
                _plan.RuleSources.Add("Имена труб: по умолчанию (стиль бюро: ВГП ГОСТ 3262 и чугун — ø Ду x S, остальные — ø Dнар x S; только проверка)");
            }
            else
            {
                _plan.RuleSources.Add("Имена труб: настройка проекта (" + (!options.SavedInProject ? "изменена в окне, в модели не сохранена" : options.SavedStamp ?? "сохранена в модели") + ")");
            }
            if (defaults.ProjectBlocked != null) Block(defaults.ProjectBlocked);

            var tableIssues = new List<ConfigIssue>();
            List<PipeNameRule> rules = PipeNameSettings.ParseRules(pn.Rules, tableIssues);
            foreach (ConfigIssue issue in tableIssues) AddConfigIssue(M, issue);
            if (tableIssues.Any(i => i.Severity == Severity.Error)) Block("в таблице «Правила» ошибки.");

            _progress.Report("Имена труб: собираю трубы…");
            List<Element> pipes = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_PipeCurves).WhereElementIsNotElementType().ToList();

            var candidates = new Dictionary<ParamKey, ParamCandidate>();
            foreach (Element sample in pipes.Take(CandidateSamples))
            {
                foreach (Parameter p in sample.Parameters)
                {
                    if (p.StorageType != StorageType.String || p.IsReadOnly) continue;
                    ParamKey? key = _access.KeyOf(p);
                    if (key == null || key.Kind == ParamKeyKind.BuiltIn) continue;
                    if (!candidates.TryGetValue(key, out ParamCandidate? c)) candidates[key] = c = new ParamCandidate(key, ParamAccess.DataTypeLabel(p));
                    c.Nested++;
                }
            }
            if (pn.NameParam != null && !candidates.ContainsKey(pn.NameParam)) candidates[pn.NameParam] = new ParamCandidate(pn.NameParam, "—");
            _plan.Candidates.AddRange(candidates.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));

            if (pn.NameParam == null)
            {
                _plan.AddIssue(M, Severity.Error, string.Empty, "не выбран параметр наименования", string.Empty)
                    .Action = "Выберите параметр на вкладке «Настройка» (обычно ADSK_Наименование).";
                Block("не выбран параметр наименования.");
                return;
            }
            ParamKey nameKey = pn.NameParam;
            bool write = pn.WriteName;
            if (!write)
            {
                _plan.AddIssue(M, Severity.Info, nameKey.Name, "только проверка: имена труб не пишутся (в моделях работает BIM2B)", string.Empty)
                    .Action = "Чтобы плагин записал имена, включите «Записывать имена труб в этом проекте» на вкладке «Настройка» — после того как BIM2B перестанет писать имена труб.";
            }

            var notTouched = new SortedDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            void Skip(string why) => notTouched[why] = notTouched.TryGetValue(why, out int c) ? c + 1 : 1;
            var preview = new SortedDictionary<string, (int Count, string Text)>(StringComparer.CurrentCultureIgnoreCase);
            // Одна труба по правилу — сколько разных имён у неё сейчас («двойные строки» спецификации)
            var namesByTarget = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            int wrong = 0;
            int done = 0;

            for (int i = 0; i < pipes.Count; i++)
            {
                if (i % 500 == 0)
                {
                    _progress.Report($"Имена труб: {i + 1} из {pipes.Count}", i, pipes.Count);
                    _progress.ThrowIfCancelled();
                }
                Element pipe = pipes[i];
                long id = pipe.Id.ToLong();
                Element? type = TypeOf(pipe);
                string typeName = type?.Name ?? string.Empty;
                string comment = type?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString()?.Trim() ?? string.Empty;
                string subject = "Трубы : " + typeName;

                Parameter? p = _access.Find(pipe, nameKey);
                if (p == null)
                {
                    bool typeParam = type != null && _access.Find(type, nameKey) != null;
                    _plan.AddIssue(M, Severity.Error, nameKey.Name, typeParam ? "параметр ТИПА, нужен параметр экземпляра" : "нет параметра у труб", subject, id);
                    continue;
                }
                if (p.StorageType != StorageType.String)
                {
                    _plan.AddIssue(M, Severity.Error, nameKey.Name, $"параметр не текстовый ({ParamAccess.DataTypeLabel(p)})", subject, id);
                    continue;
                }
                string current = p.AsString() ?? string.Empty;
                if (PipeNameText.IsExcluded(current))
                {
                    Skip("имя с «!» (не учитывать)");
                    continue;
                }
                PipeNameRule? rule = PipeNameSettings.Match(rules, typeName, comment);
                if (rule == null || rule.Format.Length == 0)
                {
                    if (!preview.TryGetValue(typeName, out (int Count, string Text) skipped))
                    {
                        skipped = (0, rule == null ? "нет правила — не называется" : $"строка «{rule.Mask}», формат пуст — не называется");
                    }
                    preview[typeName] = (skipped.Count + 1, skipped.Text);
                    if (rule == null)
                    {
                        _plan.AddIssue(M, Severity.Info, string.Empty, "нет правила в таблице «Правила» — имя не проверяется", subject, id)
                            .Action = "Если тип нужно называть — добавьте строку с его маской.";
                    }
                    continue;
                }
                string baseName = rule.BaseName.Length > 0 ? rule.BaseName : comment;
                if (baseName.Length == 0 && rule.Format.IndexOf("{base}", StringComparison.Ordinal) >= 0)
                {
                    _plan.AddIssue(M, Severity.Warning, nameKey.Name, "у типа пусто в «Комментариях к типоразмеру», а «Основа имени» в таблице не задана", subject, id)
                        .Action = "Заполните «Комментарии к типоразмеру» типа трубы или «Основу имени» в таблице «Правила».";
                    continue;
                }
                PipeSize size = SizeOf(pipe);
                string name = PipeNameText.Render(rule.Format, baseName, size, out string? error);
                if (error != null)
                {
                    _plan.AddIssue(M, Severity.Warning, nameKey.Name, error, subject, id)
                        .Action = "Проверьте таблицу сегментов трубы (Управление → Настройки MEP → Механические → Сегменты и размеры).";
                    continue;
                }
                done++;
                if (!preview.TryGetValue(typeName, out (int Count, string Text) shown)) shown = (0, $"строка «{rule.Mask}» → «{name}»");
                preview[typeName] = (shown.Count + 1, shown.Text);
                if (!namesByTarget.TryGetValue(name, out Dictionary<string, int>? seen)) namesByTarget[name] = seen = new Dictionary<string, int>(StringComparer.Ordinal);
                seen[current] = seen.TryGetValue(current, out int n) ? n + 1 : 1;

                if (string.Equals(current, name, StringComparison.Ordinal))
                {
                    _plan.CountUnchanged(M);
                    continue;
                }
                wrong++;
                List<string> lost = PipeNameText.LostCalcWords(current, name);
                if (lost.Count > 0)
                {
                    _plan.AddIssue(M, Severity.Warning, nameKey.Name,
                        $"в новом имени нет слов «{string.Join("», «", lost)}» — «Расчётные позиции» подбирают по ним окраску", subject, id)
                        .Action = "Проверьте «Основу имени» или «Комментарии к типоразмеру»: слова нужны, пока окраска ищется по имени.";
                }
                if (!write)
                {
                    _plan.AddIssue(M, Severity.Warning, nameKey.Name, $"имя не по правилу: «{current}» → «{name}»", subject, id)
                        .Action = "Только проверка. Исправит запись имён (галка на вкладке «Настройка») или VgpNameDuS / BIM2B.";
                    continue;
                }
                if (p.IsReadOnly)
                {
                    _plan.AddIssue(M, Severity.Error, nameKey.Name, "только для чтения: задан формулой или связан в семействе", subject, id);
                    continue;
                }
                if (Guard(M, pipe, p, nameKey.Name, subject)) continue;
                AddChange(M, pipe, nameKey, current, ParamValue.OfText(name), $"строка {rule.Line} «{rule.Mask}»: {rule.Format}", null);
            }

            _plan.Notes.Add($"Имена труб: проверено {done} из {pipes.Count}, не по правилу {wrong}; {(write ? "имена пишутся" : "только проверка")}; правил в таблице {rules.Count}.");
            if (notTouched.Count > 0) _plan.Notes.Add("Имена труб: не трогаются — " + string.Join("; ", notTouched.Select(x => $"{x.Key}: {x.Value}")));
            List<string> doubles = namesByTarget.Where(x => x.Value.Count > 1).OrderBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => $"«{x.Key}» сейчас под {x.Value.Count} именами: " + string.Join(", ", x.Value.OrderByDescending(v => v.Value).Select(v => $"«{v.Key}» {v.Value} шт.")))
                .ToList();
            if (doubles.Count > 0) _plan.Notes.Add($"Имена труб: одна труба — несколько строк спецификации ({doubles.Count}): " + string.Join("; ", doubles));
            foreach (KeyValuePair<string, (int Count, string Text)> x in preview) _plan.Preview.Add($"{x.Key} ({x.Value.Count} шт.): {x.Value.Text}");

            foreach (UpdaterInfo updater in SystemMergeExecutor.RegisteredUpdaters(_doc).Where(u => (u.ApplicationName + " " + u.UpdaterName).IndexOf("BIM2B", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                _plan.AddIssue(M, write ? Severity.Warning : Severity.Info, nameKey.Name,
                    $"в модели зарегистрирован апдейтер «{updater.UpdaterName}» ({updater.ApplicationName}): при создании и правке трубы он соберёт имя по своей вкладке", string.Empty)
                    .Action = "Пока BIM2B пишет имена труб — запускать последним перед выпуском. Вкладку BIM2B выбирает по «Маркировке типоразмера» типа трубы.";
            }
        }

        private static PipeSize SizeOf(Element pipe)
        {
            const double feetToMm = 304.8;
            double? Mm(BuiltInParameter bip)
            {
                Parameter? p = pipe.get_Parameter(bip);
                return p != null && p.HasValue && p.AsDouble() > 1e-9 ? p.AsDouble() * feetToMm : (double?)null;
            }
            return new PipeSize(Mm(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM), Mm(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER), Mm(BuiltInParameter.RBS_PIPE_INNER_DIAM_PARAM));
        }
    }
}
