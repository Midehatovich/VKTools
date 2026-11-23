using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Text;

namespace VKTools.Core.Spec
{
    /// <summary>Строка таблицы «Марки и поставщики»: маска наименования → марка, код изделия, завод. Пустая графа — не трогать.</summary>
    public sealed class VendorRule
    {
        public VendorRule(string mask, string mark, string code, string maker, int line)
        {
            Mask = mask;
            Mark = mark;
            Code = code;
            Maker = maker;
            Line = line;
        }

        /// <summary>Маска наименования элемента или «Семейство : Тип».</summary>
        public string Mask { get; }

        public string Mark { get; }

        public string Code { get; }

        public string Maker { get; }

        public int Line { get; }

        public bool Matches(string name, string familyType) => MaskText.Matches(Mask, name, familyType);

        /// <summary>Значение графы для роли; пусто — графа правилом не задана.</summary>
        public string Value(ParamRole role) => role switch
        {
            ParamRole.Mark => Mark,
            ParamRole.Code => Code,
            ParamRole.Maker => Maker,
            _ => string.Empty,
        };
    }

    /// <summary>
    /// «Марки и поставщики» (0.15.0, объект «Пушкарев»): марка, код изделия и завод-изготовитель по маске наименования, таблица хранится
    /// в модели (ключи vd.*). Наименования не меняются (решение 2026-11-17: в отделе свои семейства). По умолчанию заполняются только пустые
    /// значения: марка из семейства (ADSK_) и ручная правка инженера не перетираются.
    /// </summary>
    public sealed class VendorSettings
    {
        public const string MaskColumn = "Маска наименования";
        public const string MarkColumn = "Марка";
        public const string CodeColumn = "Код изделия";
        public const string MakerColumn = "Завод-изготовитель";
        public const string NoteColumn = "Примечание";

        public static readonly IReadOnlyList<string> Columns = new[] { MaskColumn, MarkColumn, CodeColumn, MakerColumn, NoteColumn };

        public static readonly IReadOnlyList<ParamRole> Roles = new[] { ParamRole.Mark, ParamRole.Code, ParamRole.Maker };

        /// <summary>Встроенная таблица пустая: поставщики у каждого объекта свои (вендор-лист), правила — импортом CSV или вручную.</summary>
        public const string DefaultRules =
            "#schema=1\n"
            + "Маска наименования;Марка;Код изделия;Завод-изготовитель;Примечание\n";

        /// <summary>Значение графы «очистить» (например, лишний код изделия из семейства): пишется пустое, даже без «Перезаписывать».</summary>
        public const string ClearToken = "<пусто>";

        public static bool IsClear(string value) => string.Equals((value ?? string.Empty).Trim(), ClearToken, StringComparison.OrdinalIgnoreCase);

        private const string FlagsKey = "vd.flags";
        private const string NameKey = "vd.nameParam";
        private const string RulesKey = "vd.rules";

        /// <summary>Перезаписывать заполненные значения (по умолчанию — только пустые).</summary>
        public bool Overwrite { get; set; }

        /// <summary>Параметр, по которому ищется маска (обычно наименование).</summary>
        public ParamKey? NameParam { get; set; }

        public string Rules { get; set; } = DefaultRules;

        public static VendorSettings Default() => new VendorSettings { NameParam = ParamProfile.TargetKey(ParamRole.Name) };

        public VendorSettings Clone() => (VendorSettings)MemberwiseClone();

        public string Signature() => $"ow={(Overwrite ? 1 : 0)}|{NameParam?.Id}|" + InsulationSettings.Normalize(Rules);

        public void StoreTo(SpecProjectSettings settings, bool isDefault)
        {
            if (isDefault)
            {
                foreach (string key in new[] { FlagsKey, NameKey, RulesKey }) settings.Set(key, null);
                return;
            }
            settings.Set(FlagsKey, $"ow={(Overwrite ? 1 : 0)}");
            settings.Set(NameKey, NameParam == null ? string.Empty : NameParam.Id + ";" + NameParam.Name);
            settings.Set(RulesKey, InsulationSettings.Normalize(Rules) == InsulationSettings.Normalize(DefaultRules) ? null : Rules);
        }

        public static bool IsStored(SpecProjectSettings settings) => settings.Get(FlagsKey) != null;

        public static VendorSettings? Parse(SpecProjectSettings settings, List<ConfigIssue> issues)
        {
            VendorSettings result = Default();
            result.Overwrite = (settings.Get(FlagsKey) ?? string.Empty).Split(';').Any(p => p.Trim() == "ow=1");
            bool ok = true;
            string? name = settings.Get(NameKey);
            if (name != null) result.NameParam = StoredKey.Parse(name, "настройка проекта: марки и поставщики", "параметр наименования", issues, ref ok);
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

        /// <summary>Разбор таблицы «Правила». Ошибки — с номером строки. Строка без марки, кода и завода — исключение (элемент не трогается).</summary>
        public static List<VendorRule> ParseRules(string text, List<ConfigIssue> issues)
        {
            const string file = "таблица «Марки и поставщики»";
            var result = new List<VendorRule>();
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
            if (!table.HasColumn(MaskColumn))
            {
                issues.Add(new ConfigIssue(Severity.Error, file, null, $"нет колонки «{MaskColumn}»"));
                return result;
            }
            foreach (string column in new[] { MarkColumn, CodeColumn, MakerColumn })
            {
                if (!table.HasColumn(column)) issues.Add(new ConfigIssue(Severity.Warning, file, null, $"нет колонки «{column}» — эта графа не заполняется"));
            }
            foreach (CsvRow row in table.Rows)
            {
                string mask = row.Get(MaskColumn).Trim();
                if (mask.Length == 0)
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, "пустая «Маска наименования» (для всех — «*»)"));
                    continue;
                }
                string Cell(string column) => table.HasColumn(column) ? row.Get(column).Trim() : string.Empty;
                result.Add(new VendorRule(mask, Cell(MarkColumn), Cell(CodeColumn), Cell(MakerColumn), row.LineNumber));
            }
            return result;
        }

        /// <summary>Первое подходящее правило сверху.</summary>
        public static VendorRule? Match(IEnumerable<VendorRule> rules, string name, string familyType) =>
            rules.FirstOrDefault(r => r.Matches(name, familyType));
    }
}
