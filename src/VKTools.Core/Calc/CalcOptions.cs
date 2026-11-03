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

        public string CordText { get; set; } = string.Empty;

        public string ReserveText { get; set; } = string.Empty;

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

        public List<CordRow> Cord { get; } = new List<CordRow>();

        /// <summary>Запас на штучные позиции (calc_reserve.csv).</summary>
        public List<ReserveRow> Reserve { get; } = new List<ReserveRow>();

        /// <summary>Шнур: добавка к Dнар и шаг витка, мм.</summary>
        public double CordAddMm { get; set; } = 10;

        public double CordPitchMm { get; set; } = 10;

        /// <summary>Есть ресурсы шнура: нужны гильзы.</summary>
        public bool NeedsSleeves => Resources.Any(r => r.Base == CalcBase.Cord);

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
        public const string DefaultSleeveFamily = "*ильз*";

        /// <summary>Длина гильзы — габарит её геометрии вдоль оси трубы (у семейства нет параметра длины).</summary>
        public const string SleeveLengthGeometry = "(по геометрии вдоль трубы)";
        public const string DefaultCordAdd = "10";
        public const string DefaultCordPitch = "10";
        public const string SleevesTab = "Гильзы и шнур";
        public const string ReserveTab = "Запас на штучные";

        private const string SchedulesKey = "calc.schedules";
        private const string SystemKey = "calc.systemParam";
        private const string GroupKey = "calc.group";
        private const string ResourcesKey = "calc.resources";
        private const string PipeRulesKey = "calc.pipeRules";
        private const string SupportsKey = "calc.supports";
        private const string CordKey = "calc.cord";
        private const string ReserveKey = "calc.reserve";
        private const string SleeveFamilyKey = "calc.sleeveFamily";
        private const string SleeveLengthKey = "calc.sleeveLength";
        private const string CordAddKey = "calc.cordAdd";
        private const string CordPitchKey = "calc.cordPitch";
        private const string SleeveAllKey = "calc.sleeveAll";

        /// <summary>Имена спецификаций — областей расчёта. Пусто — вся модель (область «МОДЕЛЬ»).</summary>
        public List<string> Schedules { get; set; } = new List<string>();

        /// <summary>Параметр системы у труб и позиций: по нему спецификация делит строки.</summary>
        public string SystemParameter { get; set; } = DefaultSystemParameter;

        /// <summary>ADSK_Группирование позиций: «9» → 9.1, 9.2… по «Порядку» ресурсов.</summary>
        public string Group { get; set; } = DefaultGroup;

        public string ResourcesText { get; set; } = string.Empty;

        public string PipeRulesText { get; set; } = string.Empty;

        public string SupportsText { get; set; } = string.Empty;

        public string CordText { get; set; } = string.Empty;

        public string ReserveText { get; set; } = string.Empty;

        /// <summary>Семейство гильз: маска имени (* — любые символы).</summary>
        public string SleeveFamily { get; set; } = DefaultSleeveFamily;

        /// <summary>Параметр длины гильзы (экземпляра или типа); пусто — найти по имени «Длина».</summary>
        public string SleeveLength { get; set; } = string.Empty;

        public string CordAdd { get; set; } = DefaultCordAdd;

        public string CordPitch { get; set; } = DefaultCordPitch;

        /// <summary>Шнур только в гильзах наружных (подпорных) стен и фундаментов модели и связей; false — во всех гильзах.</summary>
        public bool SleeveOuterOnly { get; set; } = true;

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
            CordText = CordText,
            ReserveText = ReserveText,
            SleeveFamily = SleeveFamily,
            SleeveLength = SleeveLength,
            CordAdd = CordAdd,
            CordPitch = CordPitch,
            SleeveOuterOnly = SleeveOuterOnly,
            SavedInProject = SavedInProject,
            SavedStamp = SavedStamp,
        };

        /// <summary>Отпечаток: те же настройки — тот же анализ (пробный прогон засчитывается только для них).</summary>
        public string Signature() => string.Join("\u0001", new[]
        {
            string.Join("\u0002", Schedules), SystemParameter.Trim(), Group.Trim(),
            Canonical(ResourcesText), Canonical(PipeRulesText), Canonical(SupportsText),
            Canonical(CordText), Canonical(ReserveText), SleeveFamily.Trim(), SleeveLength.Trim(), CordAdd.Trim(), CordPitch.Trim(),
            SleeveOuterOnly ? "outer" : "all",
        });

        public bool IsDefault(CalcDefaults defaults) => Signature() == FromDefaults(defaults).Signature();

        public static CalcOptions FromDefaults(CalcDefaults defaults) => new CalcOptions
        {
            ResourcesText = defaults.ResourcesText,
            PipeRulesText = defaults.PipeRulesText,
            SupportsText = defaults.SupportsText,
            CordText = defaults.CordText,
            ReserveText = defaults.ReserveText,
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
            if (set.NeedsSleeves)
            {
                CsvTable? cord = CalcTables.ParseText(CordText, CalcTables.Cord, SleevesTab, set.Issues);
                if (cord != null) set.Cord.AddRange(CalcTables.ParseCord(cord, SleevesTab, set.Resources, set.Issues));
                set.CordAddMm = Positive(CordAdd, "добавка к Dнар", allowZero: true, set.Issues) ?? 10;
                set.CordPitchMm = Positive(CordPitch, "шаг витка", allowZero: false, set.Issues) ?? 10;
                if (SleeveFamily.Trim().Length == 0) set.Issues.Add(new ConfigIssue(Severity.Error, SleevesTab, null, "не задано семейство гильз"));
            }
            if (ReserveText.Trim().Length > 0)
            {
                CsvTable? reserve = CalcTables.ParseText(ReserveText, CalcTables.Reserve, ReserveTab, set.Issues);
                if (reserve != null) set.Reserve.AddRange(CalcTables.ParseReserve(reserve, ReserveTab, set.Issues));
            }
            if (SystemParameter.Trim().Length == 0) set.Issues.Add(new ConfigIssue(Severity.Error, "Спецификации", null, "не задан параметр системы"));
            if (Group.IndexOf('|') >= 0) set.Issues.Add(new ConfigIssue(Severity.Error, "Спецификации", null, "в номере группы недопустим «|»"));
            foreach (string schedule in Schedules.Where(s => s.IndexOf('|') >= 0))
            {
                set.Issues.Add(new ConfigIssue(Severity.Error, "Спецификации", null, $"«{schedule}»: символ «|» в имени спецификации недопустим (он разделяет ключ позиции)"));
            }
            return set;
        }

        private static double? Positive(string text, string what, bool allowZero, List<ConfigIssue> issues)
        {
            try
            {
                double? value = CalcTables.ParseNumber(text, null);
                if (value.HasValue && (value.Value > 0 || (allowZero && value.Value == 0))) return value;
            }
            catch (FormatException)
            {
                // ниже — общее сообщение
            }
            issues.Add(new ConfigIssue(Severity.Error, SleevesTab, null, $"{what}: «{text}» — нужно число {(allowZero ? "≥ 0" : "больше нуля")}, мм"));
            return null;
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
            settings.Set(ReserveKey, isDefault || Canonical(ReserveText) == Canonical(defaults.ReserveText) ? null : Normalize(ReserveText, "запас на штучные"));
            settings.Set(CordKey, isDefault || Canonical(CordText) == Canonical(defaults.CordText) ? null : Normalize(CordText, "шнур в гильзы"));
            settings.Set(SleeveFamilyKey, isDefault || SleeveFamily.Trim() == DefaultSleeveFamily ? null : SleeveFamily.Trim());
            settings.Set(SleeveLengthKey, isDefault || SleeveLength.Trim().Length == 0 ? null : SleeveLength.Trim());
            settings.Set(CordAddKey, isDefault || CordAdd.Trim() == DefaultCordAdd ? null : CordAdd.Trim());
            settings.Set(CordPitchKey, isDefault || CordPitch.Trim() == DefaultCordPitch ? null : CordPitch.Trim());
            settings.Set(SleeveAllKey, isDefault || SleeveOuterOnly ? null : "1");
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
            options.SleeveFamily = settings.Get(SleeveFamilyKey) ?? DefaultSleeveFamily;
            options.SleeveLength = settings.Get(SleeveLengthKey) ?? string.Empty;
            options.CordAdd = settings.Get(CordAddKey) ?? DefaultCordAdd;
            options.CordPitch = settings.Get(CordPitchKey) ?? DefaultCordPitch;
            options.SleeveOuterOnly = settings.Get(SleeveAllKey) != "1";
            bool broken = false;
            foreach ((string key, string label, Action<string> set) in new (string, string, Action<string>)[]
            {
                (ResourcesKey, "ресурсы", v => options.ResourcesText = v),
                (PipeRulesKey, "правила для труб", v => options.PipeRulesText = v),
                (SupportsKey, "крепления", v => options.SupportsText = v),
                (CordKey, "шнур в гильзы", v => options.CordText = v),
                (ReserveKey, "запас на штучные", v => options.ReserveText = v),
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
