using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Text;

namespace VKTools.Core.Spec
{
    /// <summary>Строка таблицы «Правила» имён труб (Р5, REQ-PIPE-001…007): маска типа → основа и формат.</summary>
    public sealed class PipeNameRule
    {
        public PipeNameRule(string mask, string baseName, string format, int line)
        {
            Mask = mask;
            BaseName = baseName;
            Format = format;
            Line = line;
        }

        /// <summary>Маска имени типа или «Комментариев к типоразмеру».</summary>
        public string Mask { get; }

        /// <summary>Основа имени; пусто — «Комментарии к типоразмеру» типа трубы.</summary>
        public string BaseName { get; }

        /// <summary>Формат: {base}, {dn}, {od}, {id}, {s}; пусто — тип не называется (строка-исключение).</summary>
        public string Format { get; }

        public int Line { get; }

        public bool Matches(string typeName, string typeComment) => MaskText.Matches(Mask, typeName, typeComment);
    }

    /// <summary>Размеры трубы из сегмента, мм. null — в модели нет значения.</summary>
    public readonly struct PipeSize
    {
        public PipeSize(double? dn, double? od, double? id)
        {
            Dn = dn;
            Od = od;
            Id = id;
        }

        public double? Dn { get; }

        public double? Od { get; }

        public double? Id { get; }

        /// <summary>Стенка S = (Dнар − Dвн) / 2.</summary>
        public double? S => Od.HasValue && Id.HasValue && Od.Value > Id.Value ? (Od.Value - Id.Value) / 2 : (double?)null;
    }

    /// <summary>Числа и шаблоны имён труб: десятичная запятая, без незначащих нулей (REQ-PIPE-004).</summary>
    public static class PipeNameText
    {
        public static readonly IReadOnlyList<string> Placeholders = new[] { "{base}", "{dn}", "{od}", "{id}", "{s}" };

        /// <summary>
        /// Слова, по которым «Расчётные позиции» (Р3) подбирают окраску по ADSK_Наименование (*оцинкованная*, *водогазопроводная*, *электросварная*).
        /// Пропало из имени — предупреждение (REQ-PIPE-008 ещё не сделан).
        /// </summary>
        public static readonly IReadOnlyList<string> CalcWords = new[] { "оцинкован", "водогазопровод", "электросвар" };

        /// <summary>мм → «15», «21,3», «2,8», «76,1»: до 2 знаков, запятая, без незначащих нулей.</summary>
        public static string Mm(double mm) => ReserveMath.Number(mm, 2);

        /// <summary>Подставить размеры; ошибка — текст причины.</summary>
        public static string Render(string format, string baseName, PipeSize size, out string? error)
        {
            error = null;
            var values = new Dictionary<string, double?>
            {
                ["{dn}"] = size.Dn,
                ["{od}"] = size.Od,
                ["{id}"] = size.Id,
                ["{s}"] = size.S,
            };
            string text = format;
            foreach (KeyValuePair<string, double?> v in values)
            {
                if (text.IndexOf(v.Key, StringComparison.Ordinal) < 0) continue;
                if (v.Value == null || v.Value.Value <= 1e-9)
                {
                    error = $"у трубы не определён {Describe(v.Key)} — {v.Key} не подставить";
                    return string.Empty;
                }
                text = text.Replace(v.Key, Mm(v.Value.Value));
            }
            text = text.Replace("{base}", (baseName ?? string.Empty).Trim()).Trim();
            int brace = text.IndexOf('{');
            if (brace >= 0 && text.IndexOf('}', brace) > brace)
            {
                error = $"в формате «{format}» непонятная вставка — можно {string.Join(", ", Placeholders)}";
                return string.Empty;
            }
            return text;
        }

        private static string Describe(string placeholder) => placeholder switch
        {
            "{dn}" => "номинальный диаметр (Ду)",
            "{od}" => "наружный диаметр",
            "{id}" => "внутренний диаметр",
            _ => "Dнар или Dвн (стенка S = (Dнар − Dвн)/2)",
        };

        /// <summary>Слова окраски, которые были в старом имени и пропали в новом.</summary>
        public static List<string> LostCalcWords(string oldName, string newName) =>
            CalcWords.Where(w => (oldName ?? string.Empty).IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0
                && (newName ?? string.Empty).IndexOf(w, StringComparison.OrdinalIgnoreCase) < 0).ToList();

        /// <summary>Имя-исключение: начинается с «!» (так в отделе помечают «не учитывать»).</summary>
        public static bool IsExcluded(string? name) => (name ?? string.Empty).TrimStart().StartsWith("!", StringComparison.Ordinal);
    }

    /// <summary>Настройка «Имена труб» (ключи pn.* в модели): писать ли, параметр и таблица «Правила».</summary>
    public sealed class PipeNameSettings
    {
        public const string MaskColumn = "Маска типа";
        public const string BaseColumn = "Основа имени";
        public const string FormatColumn = "Формат имени";

        public static readonly IReadOnlyList<string> Columns = new[] { MaskColumn, BaseColumn, FormatColumn };

        /// <summary>
        /// Стиль бюро (как BIM2B «Ду х Стенка» / «Днар х Стенка»): основа — «Комментарии к типоразмеру»;
        /// ВГП ГОСТ 3262 и чугун — по Ду (VgpNameDuS, одобрено 2026-10-20), остальные — по Dнар.
        /// </summary>
        public const string DefaultRules =
            "#schema=1\n"
            + "Маска типа;Основа имени;Формат имени\n"
            + "*3262*;;{base} ø{dn}x{s}\n"
            + "*SML*;;{base} ø{dn}x{s}\n"
            + "*Чугун*;;{base} ø{dn}x{s}\n"
            + "*;;{base} ø{od}x{s}\n";

        private const string FlagsKey = "pn.flags";
        private const string NameKey = "pn.nameParam";
        private const string RulesKey = "pn.rules";

        /// <summary>Писать ADSK_Наименование труб; по умолчанию — только проверка (REQ-PIPE-005: BIM2B ещё пишет имена труб).</summary>
        public bool WriteName { get; set; }

        public ParamKey? NameParam { get; set; }

        public string Rules { get; set; } = DefaultRules;

        /// <summary>Свой набор «Р_»: имена пишутся сразу — BIM2B пишет только ADSK_Наименование, Р_Наименование ему не мешает.</summary>
        public static PipeNameSettings Default() => new PipeNameSettings
        {
            NameParam = ParamProfile.TargetKey(ParamRole.Name),
            WriteName = ParamProfile.IsOwn,
        };

        public PipeNameSettings Clone() => (PipeNameSettings)MemberwiseClone();

        public string Signature() => $"name={(WriteName ? 1 : 0)}|{NameParam?.Id}|" + InsulationSettings.Normalize(Rules);

        public void StoreTo(SpecProjectSettings settings, bool isDefault)
        {
            if (isDefault)
            {
                foreach (string key in new[] { FlagsKey, NameKey, RulesKey }) settings.Set(key, null);
                return;
            }
            settings.Set(FlagsKey, $"name={(WriteName ? 1 : 0)}");
            settings.Set(NameKey, NameParam == null ? string.Empty : NameParam.Id + ";" + NameParam.Name);
            settings.Set(RulesKey, InsulationSettings.Normalize(Rules) == InsulationSettings.Normalize(DefaultRules) ? null : Rules);
        }

        public static bool IsStored(SpecProjectSettings settings) => settings.Get(FlagsKey) != null;

        public static PipeNameSettings? Parse(SpecProjectSettings settings, List<ConfigIssue> issues)
        {
            PipeNameSettings result = Default();
            result.WriteName = (settings.Get(FlagsKey) ?? string.Empty).Split(';').Any(p => p.Trim() == "name=1");
            bool ok = true;
            string? name = settings.Get(NameKey);
            if (name != null) result.NameParam = StoredKey.Parse(name, "настройка проекта: имена труб", "параметр наименования", issues, ref ok);
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

        /// <summary>Разбор таблицы «Правила». Ошибки — с номером строки.</summary>
        public static List<PipeNameRule> ParseRules(string text, List<ConfigIssue> issues)
        {
            const string file = "таблица «Правила»";
            var result = new List<PipeNameRule>();
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
            foreach (string column in new[] { MaskColumn, FormatColumn })
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
                if (mask.Length == 0)
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, "пустая «Маска типа» (для всех типов — «*»)"));
                    continue;
                }
                string format = row.Get(FormatColumn).Trim();
                if (format.Length > 0)
                {
                    PipeNameText.Render(format, "основа", new PipeSize(25, 33.5, 27.1), out string? error);
                    if (error != null)
                    {
                        issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, error));
                        continue;
                    }
                }
                result.Add(new PipeNameRule(mask, row.Get(BaseColumn).Trim(), format, row.LineNumber));
            }
            return result;
        }

        /// <summary>Первое подходящее правило сверху.</summary>
        public static PipeNameRule? Match(IEnumerable<PipeNameRule> rules, string typeName, string typeComment) =>
            rules.FirstOrDefault(r => r.Matches(typeName, typeComment));
    }
}
