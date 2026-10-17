using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Config;

namespace VKTools.Core.Spec
{
    /// <summary>Какие родители обрабатывает перенос во вложенные.</summary>
    public enum NestedScope
    {
        Model,
        ActiveView,
        Selection,
    }

    /// <summary>Настройки по умолчанию: правила отдела или встроенные (nested_params.csv, grouping.csv).</summary>
    public sealed class SpecDefaults
    {
        public List<ParamKey> NestedParams { get; } = new List<ParamKey>();

        /// <summary>«nested_params.csv: встроенные, путь (UTF-8 BOM)» — в шапку отчёта.</summary>
        public string NestedSource { get; set; } = string.Empty;

        /// <summary>Файл не загрузился как положено: запись по умолчанию запрещена (REQ-CFG-003).</summary>
        public string? NestedBlocked { get; set; }

        public List<GroupRule> GroupRules { get; } = new List<GroupRule>();

        public string GroupingSource { get; set; } = string.Empty;

        public string? GroupingBlocked { get; set; }

        /// <summary>Настройка проекта в модели повреждена: запись запрещена, пока её не сохранят заново.</summary>
        public string? ProjectBlocked { get; set; }

        public List<ConfigIssue> Issues { get; } = new List<ConfigIssue>();
    }

    /// <summary>
    /// Настройки, с которыми строится анализ и идёт запись: то, что сейчас в окне. Источник — модель
    /// (настройка проекта) или правила по умолчанию; окно меняет копию, «Сохранить» кладёт её в модель.
    /// </summary>
    public sealed class SpecOptions
    {
        private static readonly string[] ProjectTableComments = { "VK Tools: таблица групп ADSK_Группирование (настройка проекта)." };

        public SpecOptions(SpecModule module)
        {
            Module = module;
        }

        public SpecModule Module { get; }

        public List<ParamKey> NestedParams { get; set; } = new List<ParamKey>();

        public bool NestedClearEmpty { get; set; }

        public NestedScope Scope { get; set; }

        public List<GroupRule> GroupRules { get; set; } = new List<GroupRule>();

        /// <summary>В модели сохранена своя настройка этого модуля.</summary>
        public bool SavedInProject { get; set; }

        /// <summary>«сохранил Ivanov 17.10.2026 13:00».</summary>
        public string? SavedStamp { get; set; }

        /// <summary>Строка-отпечаток: те же настройки — тот же анализ (пробный прогон засчитывается только для них).</summary>
        public string Signature() => Module == SpecModule.Nested
            ? $"{Scope}|{(NestedClearEmpty ? 1 : 0)}|" + string.Join(",", NestedParams.Select(k => k.Id).OrderBy(x => x, System.StringComparer.Ordinal))
            : SettingsSignature();

        /// <summary>Отпечаток того, что сохраняется в модели (без области обработки).</summary>
        public string SettingsSignature() => Module == SpecModule.Nested
            ? $"{(NestedClearEmpty ? 1 : 0)}|" + string.Join(",", NestedParams.Select(k => k.Id).OrderBy(x => x, System.StringComparer.Ordinal))
            : SpecRuleTables.FormatGrouping(GroupRules, Enumerable.Empty<string>());

        /// <summary>Совпадает с настройками по умолчанию: сохранять в модели нечего.</summary>
        public bool IsDefault(SpecDefaults defaults)
        {
            var asDefault = new SpecOptions(Module) { NestedParams = defaults.NestedParams.ToList(), GroupRules = defaults.GroupRules.ToList() };
            return SettingsSignature() == asDefault.SettingsSignature();
        }

        public SpecOptions Clone() => new SpecOptions(Module)
        {
            NestedParams = NestedParams.ToList(),
            NestedClearEmpty = NestedClearEmpty,
            Scope = Scope,
            GroupRules = GroupRules.ToList(),
            SavedInProject = SavedInProject,
            SavedStamp = SavedStamp,
        };

        /// <summary>Положить в настройки проекта; совпадает с умолчанием — раздел удаляется.</summary>
        public void StoreTo(SpecProjectSettings settings, SpecDefaults defaults, string user, System.DateTime when)
        {
            bool isDefault = IsDefault(defaults);
            string section = Module == SpecModule.Nested ? SpecProjectSettings.NestedSection : SpecProjectSettings.GroupingSection;
            if (Module == SpecModule.Nested)
            {
                settings.NestedParams = isDefault ? null : SpecRuleTables.FormatNestedSelection(NestedParams);
                settings.NestedClearEmpty = !isDefault && NestedClearEmpty;
            }
            else
            {
                settings.GroupingTable = isDefault ? null
                    : SpecRuleTables.FormatGrouping(GroupRules, ProjectTableComments);
            }
            if (isDefault) settings.ClearStamp(section);
            else settings.SetStamp(section, user, when);
        }

        /// <summary>
        /// Настройки модуля из модели, а если их нет или они повреждены — по умолчанию.
        /// Повреждённая настройка проекта — ошибка в <paramref name="issues"/> и запрет записи (<paramref name="blocked"/>).
        /// </summary>
        public static SpecOptions Load(SpecModule module, SpecProjectSettings settings, SpecDefaults defaults, List<ConfigIssue> issues, out string? blocked)
        {
            blocked = null;
            var options = new SpecOptions(module) { NestedParams = defaults.NestedParams.ToList(), GroupRules = defaults.GroupRules.ToList() };
            string section = module == SpecModule.Nested ? SpecProjectSettings.NestedSection : SpecProjectSettings.GroupingSection;
            string? stored = module == SpecModule.Nested ? settings.NestedParams : settings.GroupingTable;
            if (stored == null) return options;

            var parseIssues = new List<ConfigIssue>();
            if (module == SpecModule.Nested)
            {
                List<ParamKey> keys = SpecRuleTables.ParseNestedSelection(stored, parseIssues);
                if (!parseIssues.Any(i => i.Severity == Checks.Severity.Error))
                {
                    options.NestedParams = keys;
                    options.NestedClearEmpty = settings.NestedClearEmpty;
                }
            }
            else
            {
                GroupingTable? table = SpecRuleTables.ParseGroupingText(stored, "настройка проекта: таблица групп", parseIssues);
                if (table != null && !parseIssues.Any(i => i.Severity == Checks.Severity.Error)) options.GroupRules = table.Rules.ToList();
            }
            issues.AddRange(parseIssues);
            if (parseIssues.Any(i => i.Severity == Checks.Severity.Error))
            {
                blocked = "настройка проекта повреждена — показаны настройки по умолчанию. Сохраните настройку заново.";
                return options;
            }
            options.SavedInProject = true;
            options.SavedStamp = settings.Stamp(section);
            return options;
        }
    }
}
