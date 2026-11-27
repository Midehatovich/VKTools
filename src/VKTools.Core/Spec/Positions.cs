using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Text;

namespace VKTools.Core.Spec
{
    /// <summary>Строка таблицы «Спецификации»: маска имени спецификации → нумеровать с 1 или не трогать.</summary>
    public sealed class PositionRule
    {
        public PositionRule(string mask, bool number, int line)
        {
            Mask = mask;
            Number = number;
            Line = line;
        }

        public string Mask { get; }

        /// <summary>true — «с 1», false — «не трогать».</summary>
        public bool Number { get; }

        public int Line { get; }

        public bool Matches(string scheduleName) => MaskText.Matches(Mask, scheduleName, string.Empty);
    }

    /// <summary>
    /// «Позиции» (0.17.1, решение 2026-11-24): своя нумерация в каждой оформленной спецификации, с 1, без дыр и повторов, в порядке строк.
    /// Номер пишется в параметр позиции, который показывает графа «Позиция» спецификации. Настройка хранится в модели (ключи pos.*).
    /// </summary>
    public sealed class PositionSettings
    {
        public const string MaskColumn = "Спецификация";
        public const string ModeColumn = "Нумерация";
        public const string NoteColumn = "Примечание";
        public const string NumberMode = "с 1";
        public const string KeepMode = "не трогать";

        public static readonly IReadOnlyList<string> Columns = new[] { MaskColumn, ModeColumn, NoteColumn };

        /// <summary>По умолчанию нумеруются все оформленные спецификации.</summary>
        public const string DefaultRules =
            "#schema=1\n"
            + "Спецификация;Нумерация;Примечание\n"
            + "*;с 1;все оформленные спецификации\n";

        private const string FlagsKey = "pos.flags";
        private const string ParamKeyName = "pos.param";
        private const string RulesKey = "pos.rules";

        /// <summary>Параметр позиции. Спецификация, графа «Позиция» которой показывает другой параметр, не нумеруется (замечание).</summary>
        public ParamKey? PosParam { get; set; }

        /// <summary>
        /// «Позиции вложенных через точку» (по умолчанию, решение 2026-11-26): детали сразу под родителем, «номер родителя.N» (39.1, 76.3),
        /// порядок держит скрытая графа «Р_Ключ позиции». false — «по порядку»: детали там, куда их ставит сортировка, свой номер.
        /// </summary>
        public bool SubNumbers { get; set; } = true;

        public string Rules { get; set; } = DefaultRules;

        /// <summary>Только эти спецификации (шаг «Позиции» в «Создать спецификацию» — новая). Не хранится в модели; null — все по таблице.</summary>
        public IReadOnlyCollection<string>? OnlySchedules { get; set; }

        public static PositionSettings Default() => new PositionSettings { PosParam = ParamProfile.TargetKey(ParamRole.Position) };

        public PositionSettings Clone() => (PositionSettings)MemberwiseClone();

        public string Signature() => $"sub={(SubNumbers ? 1 : 0)}|{PosParam?.Id}|" + InsulationSettings.Normalize(Rules);

        public void StoreTo(SpecProjectSettings settings, bool isDefault)
        {
            if (isDefault)
            {
                foreach (string key in new[] { FlagsKey, ParamKeyName, RulesKey }) settings.Set(key, null);
                return;
            }
            settings.Set(FlagsKey, $"sub={(SubNumbers ? 1 : 0)}");
            settings.Set(ParamKeyName, PosParam == null ? string.Empty : PosParam.Id + ";" + PosParam.Name);
            settings.Set(RulesKey, InsulationSettings.Normalize(Rules) == InsulationSettings.Normalize(DefaultRules) ? null : Rules);
        }

        public static bool IsStored(SpecProjectSettings settings) => settings.Get(FlagsKey) != null;

        public static PositionSettings? Parse(SpecProjectSettings settings, List<ConfigIssue> issues)
        {
            PositionSettings result = Default();
            result.SubNumbers = !(settings.Get(FlagsKey) ?? string.Empty).Split(';').Any(p => p.Trim() == "sub=0");
            bool ok = true;
            string? param = settings.Get(ParamKeyName);
            if (param != null) result.PosParam = StoredKey.Parse(param, "настройка проекта: позиции", "параметр позиции", issues, ref ok);
            string? rules = settings.Get(RulesKey);
            if (rules != null)
            {
                result.Rules = rules;
                var tableIssues = new List<ConfigIssue>();
                ParseRules(rules, tableIssues);
                if (tableIssues.Any(i => i.Severity == Severity.Error))
                {
                    issues.AddRange(tableIssues);
                    ok = false;
                }
            }
            return ok ? result : null;
        }

        public static List<PositionRule> ParseRules(string text, List<ConfigIssue> issues)
        {
            const string file = "таблица «Спецификации»";
            var result = new List<PositionRule>();
            CsvTable table;
            try
            {
                table = CsvTable.Parse((text ?? string.Empty).TrimStart('﻿'));
            }
            catch (FormatException ex)
            {
                issues.Add(new ConfigIssue(Severity.Error, file, null, ex.Message));
                return result;
            }
            foreach (string column in new[] { MaskColumn, ModeColumn })
            {
                if (!table.HasColumn(column))
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, null, $"нет колонки «{column}»"));
                    return result;
                }
            }
            foreach (CsvRow row in table.Rows)
            {
                string mask = row.Get(MaskColumn).Trim();
                string mode = row.Get(ModeColumn).Trim();
                if (mask.Length == 0)
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, "пустое имя спецификации (для всех — «*»)"));
                    continue;
                }
                bool number;
                if (string.Equals(mode, NumberMode, StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "1", StringComparison.Ordinal)) number = true;
                else if (string.Equals(mode, KeepMode, StringComparison.OrdinalIgnoreCase) || mode.Length == 0) number = false;
                else
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, $"«{mode}»: в «Нумерации» — «{NumberMode}» или «{KeepMode}»"));
                    continue;
                }
                result.Add(new PositionRule(mask, number, row.LineNumber));
            }
            return result;
        }

        /// <summary>
        /// Галка спецификации в окне: убрать строки с её точным именем и, если по оставшимся правилам режим другой, вставить строку
        /// «имя;с 1» или «имя;не трогать» сразу под заголовком (первой — она главнее масок).
        /// </summary>
        public static string Toggle(string rules, string scheduleName, bool number)
        {
            string text = (rules ?? string.Empty).TrimStart('\uFEFF').Replace("\r\n", "\n");
            var lines = text.Split('\n').ToList();
            int header = lines.FindIndex(l => l.TrimStart().StartsWith(MaskColumn + ";", StringComparison.Ordinal));
            if (header < 0)
            {
                lines = DefaultRules.TrimEnd('\n').Split('\n').ToList();
                header = lines.FindIndex(l => l.StartsWith(MaskColumn + ";", StringComparison.Ordinal));
            }
            string quoted = (scheduleName.IndexOf(';') >= 0 || scheduleName.IndexOf('"') >= 0) ? "\"" + scheduleName.Replace("\"", "\"\"") + "\"" : scheduleName;
            lines = lines.Where((l, i) => i <= header || !(l.StartsWith(quoted + ";", StringComparison.Ordinal))).ToList();
            string rest = string.Join("\n", lines);
            PositionRule? now = Match(ParseRules(rest, new List<ConfigIssue>()), scheduleName);
            bool current = now != null && now.Number;
            if (current != number) lines.Insert(header + 1, $"{quoted};{(number ? NumberMode : KeepMode)};галка в окне");
            return string.Join("\n", lines).TrimEnd('\n') + "\n";
        }

        /// <summary>Первое подходящее правило сверху; null — спецификация не упомянута (не трогается).</summary>
        public static PositionRule? Match(IEnumerable<PositionRule> rules, string scheduleName) => rules.FirstOrDefault(r => r.Matches(scheduleName));
    }

    /// <summary>Строка спецификации (как её собирает Revit без ключа позиции) в порядке таблицы.</summary>
    public sealed class PositionRow
    {
        public PositionRow(IReadOnlyList<long> ids, string current)
        {
            Ids = ids;
            Current = current;
        }

        public IReadOnlyList<long> Ids { get; }

        /// <summary>Позиция сейчас: общая у строки или «‹варианты›».</summary>
        public string Current { get; }

        /// <summary>Строка не нумеруется («!Не учитывать», без наименования): номер не занимает, но место (ключ) у неё есть.</summary>
        public bool Skip { get; set; }

        /// <summary>Блок (шапка системы), под которым стоит строка. Деталь получает подномер только от родителя из того же блока.</summary>
        public string Group { get; set; } = string.Empty;
    }

    /// <summary>Итоговая строка: основная или часть строки деталей одного родителя («номер.N» сразу под ним).</summary>
    public sealed class PositionUnit
    {
        public PositionUnit(int source, List<long> ids, bool skip)
        {
            Source = source;
            Ids = ids;
            Skip = skip;
        }

        /// <summary>Индекс исходной строки.</summary>
        public int Source { get; }

        public List<long> Ids { get; }

        public bool Skip { get; }

        /// <summary>Индекс строки-родителя в итоговом списке; null — основная строка.</summary>
        public int? Parent { get; set; }

        /// <summary>Номер; null — строка без номера.</summary>
        public string? Number { get; set; }

        /// <summary>Ключ сортировки «Р_Ключ позиции»: «0012», деталь — «0012.01». Ставит строку сразу под родителем, какой бы ни была категория.</summary>
        public string Key { get; set; } = string.Empty;
    }

    /// <summary>Номера и ключи строк одной спецификации: 1, 2, 3… по порядку; вложенные детали — «номер родителя.N» сразу под родителем.</summary>
    public static class PositionNumbering
    {
        public const string Variants = "‹варианты›";

        /// <param name="rows">Строки в порядке таблицы (без ключа позиции).</param>
        /// <param name="parentRowOf">Элемент → индекс строки его верхнего родителя в этой спецификации; null — не вложенный или родителя нет.</param>
        /// <param name="subNumbers">true — «через точку» (детали под родителем), false — «по порядку» (детали там, куда их ставит сортировка).</param>
        public static List<PositionUnit> Layout(IReadOnlyList<PositionRow> rows, Func<long, int?> parentRowOf, bool subNumbers)
        {
            // Строка деталей: все элементы вложенные, родители — в других не пропускаемых строках того же блока
            var split = new Dictionary<int, List<(int Parent, long Id)>>();
            if (subNumbers)
            {
                for (int r = 0; r < rows.Count; r++)
                {
                    if (rows[r].Skip || rows[r].Ids.Count == 0) continue;
                    var parts = new List<(int, long)>();
                    bool ok = true;
                    foreach (long id in rows[r].Ids)
                    {
                        int? p = parentRowOf(id);
                        if (!(p is int pr) || pr == r || pr < 0 || pr >= rows.Count || rows[pr].Skip
                            || !string.Equals(rows[pr].Group, rows[r].Group, StringComparison.Ordinal))
                        {
                            ok = false;
                            break;
                        }
                        parts.Add((pr, id));
                    }
                    if (ok) split[r] = parts;
                }
                // Родитель сам деталь (цепочка) — такая строка остаётся обычной
                foreach (int r in split.Keys.ToList())
                {
                    if (split[r].Any(x => split.ContainsKey(x.Parent))) split.Remove(r);
                }
            }

            var children = new Dictionary<int, List<PositionUnit>>();
            foreach (KeyValuePair<int, List<(int Parent, long Id)>> pair in split.OrderBy(p => p.Key))
            {
                foreach (IGrouping<int, (int Parent, long Id)> byParent in pair.Value.GroupBy(x => x.Parent))
                {
                    if (!children.TryGetValue(byParent.Key, out List<PositionUnit>? list)) children[byParent.Key] = list = new List<PositionUnit>();
                    list.Add(new PositionUnit(pair.Key, byParent.Select(x => x.Id).ToList(), false));
                }
            }

            var result = new List<PositionUnit>();
            int n = 0;
            int rank = 0;
            for (int r = 0; r < rows.Count; r++)
            {
                if (split.ContainsKey(r)) continue;
                var main = new PositionUnit(r, rows[r].Ids.ToList(), rows[r].Skip);
                rank++;
                main.Key = rank.ToString("D4", CultureInfo.InvariantCulture);
                if (!main.Skip) main.Number = (++n).ToString(CultureInfo.InvariantCulture);
                int mainIndex = result.Count;
                result.Add(main);
                if (!children.TryGetValue(r, out List<PositionUnit>? subs)) continue;
                for (int k = 0; k < subs.Count; k++)
                {
                    PositionUnit sub = subs[k];
                    sub.Parent = mainIndex;
                    sub.Key = main.Key + "." + (k + 1).ToString("D2", CultureInfo.InvariantCulture);
                    sub.Number = main.Number + "." + (k + 1).ToString(CultureInfo.InvariantCulture);
                    result.Add(sub);
                }
            }
            return result;
        }

        /// <summary>Общее значение строки или «‹варианты›» (пусто, если строка пустая у всех).</summary>
        public static string Common(IEnumerable<string> values)
        {
            string? first = null;
            foreach (string v in values)
            {
                string t = (v ?? string.Empty).Trim();
                if (first == null) first = t;
                else if (!string.Equals(first, t, StringComparison.Ordinal)) return Variants;
            }
            return first ?? string.Empty;
        }
    }
}
