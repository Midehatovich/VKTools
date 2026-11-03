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

        /// <summary>«Система и этаж»: встроенное умолчание (LSR_Имя системы, ADSK_Этаж, категории NestedParamSync v2.1).</summary>
        public SystemFloorSettings SystemFloor { get; } = SystemFloorSettings.Default();

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

        /// <summary>«Система и этаж»: параметры, категории, этажи, перенос во вложенные.</summary>
        public SystemFloorSettings SystemFloor { get; set; } = SystemFloorSettings.Default();

        /// <summary>«Система и этаж» с переносом: настройка переноса есть в модели (иначе — по умолчанию).</summary>
        public bool NestedSavedInProject { get; set; }

        /// <summary>«Группирование»: комплекты проекта (составы, номера K, слова поиска).</summary>
        public KitSettings Kits { get; set; } = new KitSettings();

        /// <summary>В модели сохранена своя настройка этого модуля (у «Группирования» — таблица групп).</summary>
        public bool SavedInProject { get; set; }

        /// <summary>«сохранил Ivanov 17.10.2026 13:00».</summary>
        public string? SavedStamp { get; set; }

        /// <summary>«Группирование»: кто и когда сохранил комплекты; null — комплекты в модели не сохранены.</summary>
        public string? KitsStamp { get; set; }

        /// <summary>Строка-отпечаток: те же настройки — тот же анализ (пробный прогон засчитывается только для них).</summary>
        public string Signature() => Module switch
        {
            SpecModule.Nested => $"{Scope}|" + NestedSignature(),
            SpecModule.SystemFloor => SystemFloor.Signature() + (SystemFloor.ChainNested ? "|" + NestedSignature() : string.Empty),
            _ => TableSignature() + "|" + Kits.Signature(),
        };

        /// <summary>Отпечаток того, что сохраняется в модели (без области обработки).</summary>
        public string SettingsSignature() => Module switch
        {
            SpecModule.Nested => NestedSignature(),
            SpecModule.SystemFloor => SystemFloor.Signature(),
            _ => TableSignature() + "|" + Kits.Signature() + "|" + Kits.Words,
        };

        private string NestedSignature() =>
            $"{(NestedClearEmpty ? 1 : 0)}|" + string.Join(",", NestedParams.Select(k => k.Id).OrderBy(x => x, System.StringComparer.Ordinal));

        private string TableSignature() => SpecRuleTables.FormatGrouping(GroupRules, Enumerable.Empty<string>());

        /// <summary>
        /// Совпадает с настройками по умолчанию: у переноса — выбор параметров, у группирования — таблица групп
        /// (комплекты «По умолчанию» не сбрасывает).
        /// </summary>
        public bool IsDefault(SpecDefaults defaults)
        {
            var asDefault = new SpecOptions(Module)
            {
                NestedParams = defaults.NestedParams.ToList(),
                GroupRules = defaults.GroupRules.ToList(),
                SystemFloor = defaults.SystemFloor.Clone(),
            };
            return Module == SpecModule.Grouping ? TableSignature() == asDefault.TableSignature() : SettingsSignature() == asDefault.SettingsSignature();
        }

        public SpecOptions Clone() => new SpecOptions(Module)
        {
            NestedParams = NestedParams.ToList(),
            NestedClearEmpty = NestedClearEmpty,
            Scope = Scope,
            GroupRules = GroupRules.ToList(),
            Kits = Kits.Clone(),
            SystemFloor = SystemFloor.Clone(),
            NestedSavedInProject = NestedSavedInProject,
            SavedInProject = SavedInProject,
            SavedStamp = SavedStamp,
            KitsStamp = KitsStamp,
        };

        /// <summary>
        /// Положить в настройки проекта; совпадает с умолчанием — раздел удаляется. Отметка «кто сохранил» меняется
        /// только у раздела, который изменился.
        /// </summary>
        public void StoreTo(SpecProjectSettings settings, SpecDefaults defaults, string user, System.DateTime when)
        {
            bool isDefault = IsDefault(defaults);
            if (Module == SpecModule.SystemFloor)
            {
                SystemFloor.StoreTo(settings, isDefault);
                if (isDefault) settings.ClearStamp(SpecProjectSettings.SystemFloorSection);
                else settings.SetStamp(SpecProjectSettings.SystemFloorSection, user, when);
                return;
            }
            if (Module == SpecModule.Nested)
            {
                settings.NestedParams = isDefault ? null : SpecRuleTables.FormatNestedSelection(NestedParams);
                settings.NestedClearEmpty = !isDefault && NestedClearEmpty;
                if (isDefault) settings.ClearStamp(SpecProjectSettings.NestedSection);
                else settings.SetStamp(SpecProjectSettings.NestedSection, user, when);
                return;
            }
            string? table = isDefault ? null : SpecRuleTables.FormatGrouping(GroupRules, ProjectTableComments);
            if (table != settings.GroupingTable)
            {
                settings.GroupingTable = table;
                if (table == null) settings.ClearStamp(SpecProjectSettings.GroupingSection);
                else settings.SetStamp(SpecProjectSettings.GroupingSection, user, when);
            }
            string? kits = Kits.Rules.Count == 0 ? null : Kits.Format(KitSettings.FileComments);
            if (kits != settings.KitsTable)
            {
                settings.KitsTable = kits;
                if (kits == null) settings.ClearStamp(SpecProjectSettings.KitsSection);
                else settings.SetStamp(SpecProjectSettings.KitsSection, user, when);
            }
            settings.KitsWords = Kits.Words == KitSettings.DefaultWords ? null : Kits.Words;
        }

        /// <summary>
        /// Настройки модуля из модели, а если их нет или они повреждены — по умолчанию.
        /// Повреждённая настройка проекта — ошибка в <paramref name="issues"/> и запрет записи (<paramref name="blocked"/>).
        /// </summary>
        public static SpecOptions Load(SpecModule module, SpecProjectSettings settings, SpecDefaults defaults, List<ConfigIssue> issues, out string? blocked)
        {
            blocked = null;
            var options = new SpecOptions(module)
            {
                NestedParams = defaults.NestedParams.ToList(),
                GroupRules = defaults.GroupRules.ToList(),
                SystemFloor = defaults.SystemFloor.Clone(),
            };
            var parseIssues = new List<ConfigIssue>();
            bool broken = false;
            if (module == SpecModule.SystemFloor)
            {
                if (SystemFloorSettings.IsStored(settings))
                {
                    SystemFloorSettings? parsed = SystemFloorSettings.Parse(settings, parseIssues);
                    if (parsed != null)
                    {
                        options.SystemFloor = parsed;
                        options.SavedInProject = true;
                        options.SavedStamp = settings.Stamp(SpecProjectSettings.SystemFloorSection);
                    }
                    else
                    {
                        broken = true;
                    }
                }
                // Перенос во вложенные той же операцией — с настройкой модуля «Перенос во вложенные» (только чтение)
                string? nested = settings.NestedParams;
                if (nested != null)
                {
                    var nestedIssues = new List<ConfigIssue>();
                    List<ParamKey> keys = SpecRuleTables.ParseNestedSelection(nested, nestedIssues);
                    if (!nestedIssues.Any(i => i.Severity == Checks.Severity.Error))
                    {
                        options.NestedParams = keys;
                        options.NestedClearEmpty = settings.NestedClearEmpty;
                        options.NestedSavedInProject = true;
                    }
                }
            }
            else if (module == SpecModule.Nested)
            {
                string? stored = settings.NestedParams;
                if (stored != null)
                {
                    List<ParamKey> keys = SpecRuleTables.ParseNestedSelection(stored, parseIssues);
                    broken = parseIssues.Any(i => i.Severity == Checks.Severity.Error);
                    if (!broken)
                    {
                        options.NestedParams = keys;
                        options.NestedClearEmpty = settings.NestedClearEmpty;
                        options.SavedInProject = true;
                        options.SavedStamp = settings.Stamp(SpecProjectSettings.NestedSection);
                    }
                }
            }
            else
            {
                string? stored = settings.GroupingTable;
                if (stored != null)
                {
                    var tableIssues = new List<ConfigIssue>();
                    GroupingTable? table = SpecRuleTables.ParseGroupingText(stored, "настройка проекта: таблица групп", tableIssues);
                    parseIssues.AddRange(tableIssues);
                    if (table != null && !tableIssues.Any(i => i.Severity == Checks.Severity.Error))
                    {
                        options.GroupRules = table.Rules.ToList();
                        options.SavedInProject = true;
                        options.SavedStamp = settings.Stamp(SpecProjectSettings.GroupingSection);
                    }
                    else
                    {
                        broken = true;
                    }
                }
                string? kits = settings.KitsTable;
                if (kits != null)
                {
                    var kitIssues = new List<ConfigIssue>();
                    KitSettings? parsed = KitSettings.Parse(kits, "настройка проекта: комплекты", kitIssues);
                    parseIssues.AddRange(kitIssues);
                    if (parsed != null && !kitIssues.Any(i => i.Severity == Checks.Severity.Error))
                    {
                        options.Kits = parsed;
                        options.KitsStamp = settings.Stamp(SpecProjectSettings.KitsSection) ?? "сохранены в модели";
                    }
                    else
                    {
                        broken = true;
                    }
                }
                options.Kits.Words = settings.KitsWords ?? KitSettings.DefaultWords;
            }
            issues.AddRange(parseIssues);
            if (broken) blocked = "настройка проекта повреждена — показаны настройки по умолчанию. Сохраните настройку заново.";
            return options;
        }
    }
}
