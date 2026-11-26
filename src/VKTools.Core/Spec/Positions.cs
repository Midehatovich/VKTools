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

        /// <summary>Вложенные детали одной строки-родителя: «номер родителя.N» (39.1, 76.3).</summary>
        public bool SubNumbers { get; set; } = true;

        public string Rules { get; set; } = DefaultRules;

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

        /// <summary>Первое подходящее правило сверху; null — спецификация не упомянута (не трогается).</summary>
        public static PositionRule? Match(IEnumerable<PositionRule> rules, string scheduleName) => rules.FirstOrDefault(r => r.Matches(scheduleName));
    }

    /// <summary>Строка спецификации для нумерации: в порядке, как она стоит в спецификации.</summary>
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

        /// <summary>Строка не нумеруется («!Не учитывать», параметр только для чтения…): номер не занимает.</summary>
        public bool Skip { get; set; }

        /// <summary>Все элементы строки — вложенные детали элементов одной строки-родителя (индекс в той же спецификации).</summary>
        public int? Parent { get; set; }
    }

    /// <summary>Номера строк одной спецификации: 1, 2, 3… по порядку; вложенные детали — «номер родителя.N».</summary>
    public static class PositionNumbering
    {
        public const string Variants = "‹варианты›";

        /// <returns>Номер для каждой строки; null — строка не нумеруется.</returns>
        public static string?[] Number(IReadOnlyList<PositionRow> rows, bool subNumbers)
        {
            var result = new string?[rows.Count];
            bool IsSub(int i) => subNumbers && rows[i].Parent is int p && p >= 0 && p < rows.Count && p != i && !rows[p].Skip && rows[p].Parent == null;
            int n = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Skip || IsSub(i)) continue;
                result[i] = (++n).ToString(CultureInfo.InvariantCulture);
            }
            var subCount = new Dictionary<int, int>();
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Skip || !IsSub(i)) continue;
                int parent = rows[i].Parent!.Value;
                int k = subCount.TryGetValue(parent, out int c) ? c + 1 : 1;
                subCount[parent] = k;
                result[i] = result[parent] + "." + k.ToString(CultureInfo.InvariantCulture);
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
