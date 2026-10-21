using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using VKTools.Core.Text;

namespace VKTools.Core.Calc
{
    /// <summary>Таблицы расчёта по умолчанию: папка отдела или встроенные (calc_*.csv).</summary>
    public sealed class CalcDefaults
    {
        public string ResourcesText { get; set; } = string.Empty;

        public string PipeRulesText { get; set; } = string.Empty;

        public string SupportsText { get; set; } = string.Empty;

        /// <summary>«calc_resources.csv: встроенные, путь (UTF-8 BOM)» — в шапку отчёта.</summary>
        public List<string> Sources { get; } = new List<string>();

        /// <summary>Файлы по умолчанию не загрузились как положено: запись по умолчанию запрещена (REQ-CFG-003).</summary>
        public string? Blocked { get; set; }

        /// <summary>Настройка проекта в модели повреждена: запись запрещена, пока её не сохранят заново.</summary>
        public string? ProjectBlocked { get; set; }

        public List<ConfigIssue> Issues { get; } = new List<ConfigIssue>();
    }

    /// <summary>Разобранные таблицы: то, с чем считает расчёт.</summary>
    public sealed class CalcRuleSet
    {
        public List<CalcResource> Resources { get; } = new List<CalcResource>();

        public List<PipeRule> Rules { get; } = new List<PipeRule>();

        public List<SupportRow> Supports { get; } = new List<SupportRow>();

        /// <summary>Колонки ресурсов после «Основание» — параметры позиции.</summary>
        public List<string> ExtraParameters { get; } = new List<string>();

        public List<ConfigIssue> Issues { get; } = new List<ConfigIssue>();

        public bool HasErrors => Issues.Any(i => i.Severity == Severity.Error);
    }

    /// <summary>
    /// Настройки «Расчётных позиций» проекта: спецификации, параметр системы, номер группы и три таблицы (как файлы
    /// Dynamo-эталона). Хранятся в модели рядом с настройками Р2 (D-033); нет раздела — по умолчанию.
    /// </summary>
    public sealed class CalcOptions
    {
        public const string Section = "calc";
        public const string DefaultSystemParameter = "LSR_Имя системы";
        public const string DefaultGroup = "9";
        public const string ModelScope = "МОДЕЛЬ";

        private const string SchedulesKey = "calc.schedules";
        private const string SystemKey = "calc.systemParam";
        private const string GroupKey = "calc.group";
        private const string ResourcesKey = "calc.resources";
        private const string PipeRulesKey = "calc.pipeRules";
        private const string SupportsKey = "calc.supports";

        /// <summary>Имена спецификаций — областей расчёта. Пусто — вся модель (область «МОДЕЛЬ»).</summary>
        public List<string> Schedules { get; set; } = new List<string>();

        /// <summary>Параметр системы у труб и позиций: по нему спецификация делит строки.</summary>
        public string SystemParameter { get; set; } = DefaultSystemParameter;

        /// <summary>ADSK_Группирование позиций: «9» → 9.1, 9.2… по «Порядку» ресурсов.</summary>
        public string Group { get; set; } = DefaultGroup;

        public string ResourcesText { get; set; } = string.Empty;

        public string PipeRulesText { get; set; } = string.Empty;

        public string SupportsText { get; set; } = string.Empty;

        public bool SavedInProject { get; set; }

        public string? SavedStamp { get; set; }

        public CalcOptions Clone() => new CalcOptions
        {
            Schedules = Schedules.ToList(),
            SystemParameter = SystemParameter,
            Group = Group,
            ResourcesText = ResourcesText,
            PipeRulesText = PipeRulesText,
            SupportsText = SupportsText,
            SavedInProject = SavedInProject,
            SavedStamp = SavedStamp,
        };

        /// <summary>Отпечаток: те же настройки — тот же анализ (пробный прогон засчитывается только для них).</summary>
        public string Signature() => string.Join("\u0001", new[]
        {
            string.Join("\u0002", Schedules), SystemParameter.Trim(), Group.Trim(),
            Canonical(ResourcesText), Canonical(PipeRulesText), Canonical(SupportsText),
        });

        public bool IsDefault(CalcDefaults defaults) => Signature() == FromDefaults(defaults).Signature();

        public static CalcOptions FromDefaults(CalcDefaults defaults) => new CalcOptions
        {
            ResourcesText = defaults.ResourcesText,
            PipeRulesText = defaults.PipeRulesText,
            SupportsText = defaults.SupportsText,
        };

        /// <summary>Разобрать таблицы. Ошибки — в <see cref="CalcRuleSet.Issues"/> с именем вкладки.</summary>
        public CalcRuleSet Parse()
        {
            var set = new CalcRuleSet();
            CsvTable? resources = CalcTables.ParseText(ResourcesText, CalcTables.Resources, "Ресурсы", set.Issues);
            if (resources != null)
            {
                set.Resources.AddRange(CalcTables.ParseResources(resources, "Ресурсы", Group.Trim(), set.Issues));
                set.ExtraParameters.AddRange(CalcTables.ExtraColumns(resources));
            }
            CsvTable? rules = CalcTables.ParseText(PipeRulesText, CalcTables.PipeRules, "Правила для труб", set.Issues);
            if (rules != null) set.Rules.AddRange(CalcTables.ParsePipeRules(rules, "Правила для труб", set.Resources, set.Issues));
            if (set.Resources.Any(r => r.Base == CalcBase.SupportMass))
            {
                CsvTable? supports = CalcTables.ParseText(SupportsText, CalcTables.Supports, "Крепления", set.Issues);
                if (supports != null) set.Supports.AddRange(CalcTables.ParseSupports(supports, "Крепления", set.Issues));
            }
            if (SystemParameter.Trim().Length == 0) set.Issues.Add(new ConfigIssue(Severity.Error, "Спецификации", null, "не задан параметр системы"));
            if (Group.IndexOf('|') >= 0) set.Issues.Add(new ConfigIssue(Severity.Error, "Спецификации", null, "в номере группы недопустим «|»"));
            foreach (string schedule in Schedules.Where(s => s.IndexOf('|') >= 0))
            {
                set.Issues.Add(new ConfigIssue(Severity.Error, "Спецификации", null, $"«{schedule}»: символ «|» в имени спецификации недопустим (он разделяет ключ позиции)"));
            }
            return set;
        }

        /// <summary>Положить в настройки проекта; совпадает с умолчанием — раздел удаляется.</summary>
        public void StoreTo(SpecProjectSettings settings, CalcDefaults defaults, string user, DateTime when)
        {
            bool isDefault = IsDefault(defaults);
            settings.Set(SchedulesKey, Schedules.Count == 0 || isDefault ? null : string.Join("\n", Schedules));
            settings.Set(SystemKey, isDefault || SystemParameter.Trim() == DefaultSystemParameter ? null : SystemParameter.Trim());
            settings.Set(GroupKey, isDefault || Group.Trim() == DefaultGroup ? null : Group.Trim());
            settings.Set(ResourcesKey, isDefault || Canonical(ResourcesText) == Canonical(defaults.ResourcesText) ? null : Normalize(ResourcesText, "ресурсы"));
            settings.Set(PipeRulesKey, isDefault || Canonical(PipeRulesText) == Canonical(defaults.PipeRulesText) ? null : Normalize(PipeRulesText, "правила для труб"));
            settings.Set(SupportsKey, isDefault || Canonical(SupportsText) == Canonical(defaults.SupportsText) ? null : Normalize(SupportsText, "крепления"));
            if (isDefault) settings.ClearStamp(Section);
            else settings.SetStamp(Section, user, when);
        }

        /// <summary>Настройки из модели, а чего там нет — по умолчанию. Повреждённая таблица — ошибка и запрет записи.</summary>
        public static CalcOptions Load(SpecProjectSettings settings, CalcDefaults defaults, List<ConfigIssue> issues, out string? blocked)
        {
            blocked = null;
            CalcOptions options = FromDefaults(defaults);
            string? schedules = settings.Get(SchedulesKey);
            if (schedules != null) options.Schedules = schedules.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            options.SystemParameter = settings.Get(SystemKey) ?? DefaultSystemParameter;
            options.Group = settings.Get(GroupKey) ?? DefaultGroup;
            bool broken = false;
            foreach ((string key, string label, Action<string> set) in new (string, string, Action<string>)[]
            {
                (ResourcesKey, "ресурсы", v => options.ResourcesText = v),
                (PipeRulesKey, "правила для труб", v => options.PipeRulesText = v),
                (SupportsKey, "крепления", v => options.SupportsText = v),
            })
            {
                string? stored = settings.Get(key);
                if (stored == null) continue;
                if (CsvTable.Parse(stored).Columns.Count == 0)
                {
                    issues.Add(new ConfigIssue(Severity.Error, "настройка проекта: " + label, null, "таблица в модели повреждена"));
                    broken = true;
                    continue;
                }
                set(stored);
            }
            options.SavedStamp = settings.Stamp(Section);
            options.SavedInProject = settings.Values.Keys.Any(k => k.StartsWith(Section + ".", StringComparison.Ordinal) && !k.StartsWith(Section + ".saved", StringComparison.Ordinal));
            if (broken) blocked = "настройка расчёта в модели повреждена — показаны таблицы по умолчанию. Сохраните настройку заново.";
            return options;
        }

        /// <summary>Таблица без комментариев и пробелов по краям: одинаковое по смыслу сравнивается как одинаковое.</summary>
        public static string Canonical(string text)
        {
            CsvTable table = CsvTable.Parse((text ?? string.Empty).TrimStart('\uFEFF'));
            IReadOnlyList<string> header = table.Columns;
            return CsvWriter.Table(Enumerable.Empty<string>(), 1, header,
                table.Rows.Select(r => (IReadOnlyList<string>)Enumerable.Range(0, header.Count).Select(i => i < r.Values.Count ? r.Values[i] : string.Empty).ToList()));
        }

        /// <summary>Текст таблицы для модели и экспорта: комментарий, «#schema=1», заголовок и строки.</summary>
        public static string Normalize(string text, string what) =>
            "# VK Tools: расчётные позиции, " + what + " (настройка проекта).\n" + Canonical(text);
    }
}
