using System;
using System.Collections.Generic;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Text;

namespace VKTools.Core.Spec
{
    /// <summary>Группа ADSK_Группирование по категории (grouping.csv). «*» — все остальные категории.</summary>
    public sealed class GroupingTable
    {
        public const string AnyCategory = "*";

        private readonly Dictionary<string, string> _byCategory = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Группа категорий, которых нет в таблице; null — такие элементы не трогаем.</summary>
        public string? DefaultGroup { get; internal set; }

        public IReadOnlyDictionary<string, string> ByCategory => _byCategory;

        /// <summary>Группа для категории (имя BuiltInCategory); null — не трогать.</summary>
        public string? GroupFor(string? builtInCategory)
        {
            if (builtInCategory != null && _byCategory.TryGetValue(builtInCategory, out string? group)) return group;
            return DefaultGroup;
        }

        /// <summary>Категория названа в таблице явно (не через «*»).</summary>
        public bool IsListed(string? builtInCategory) => builtInCategory != null && _byCategory.ContainsKey(builtInCategory);

        internal void Add(string category, string group) => _byCategory[category] = group;
    }

    /// <summary>Имена служебных параметров комплектов пожарных шкафов (kit_params.csv): в проектах разных лет свои.</summary>
    public sealed class KitParameterNames
    {
        public const string RoleCabinetNumber = "Номер комплекта шкафа";
        public const string RoleComponentFlag = "Признак комплектующего";
        public const string RoleComponentKey = "Ключ комплектующего";

        public KitParameterNames(string cabinetNumber, string componentFlag, string componentKey)
        {
            CabinetNumber = cabinetNumber;
            ComponentFlag = componentFlag;
            ComponentKey = componentKey;
        }

        /// <summary>Параметр типа шкафа: K комплекта (Комплект_Номер).</summary>
        public string CabinetNumber { get; }

        /// <summary>Параметр, по наличию которого вложенное считается комплектующим (Сортировка_Целое).</summary>
        public string ComponentFlag { get; }

        /// <summary>Параметр экземпляра комплектующего: K шкафа + ранг (ADSK_Позиция_Комплект).</summary>
        public string ComponentKey { get; }
    }

    /// <summary>Файлы правил Р2 и их разбор.</summary>
    public static class SpecRuleTables
    {
        public static readonly RuleFileSpec NestedParameters =
            new RuleFileSpec("nested_params.csv", 1, "Параметр", "GUID");

        public static readonly RuleFileSpec Grouping =
            new RuleFileSpec("grouping.csv", 1, "Категория", "Группа");

        public static readonly RuleFileSpec KitParameters =
            new RuleFileSpec("kit_params.csv", 1, "Роль", "Параметр");

        public static IReadOnlyList<RuleFileSpec> All { get; } = new[] { NestedParameters, Grouping, KitParameters };

        public static ParsedRules<ParameterRef> ParseNestedParameters(CsvTable table)
        {
            ParsedRules<ParameterRef> parsed = RuleTables.ParseParameterRefs(table, NestedParameters.FileName);
            var seen = new HashSet<Guid>();
            var unique = new List<ParameterRef>();
            foreach (ParameterRef p in parsed.Items)
            {
                if (seen.Add(p.Guid)) unique.Add(p);
                else parsed.Issues.Add(new ConfigIssue(Severity.Warning, NestedParameters.FileName, null, $"параметр «{p.Name}» указан дважды, второй раз пропущен"));
            }
            parsed.Items.Clear();
            parsed.Items.AddRange(unique);
            return parsed;
        }

        public static GroupingTable ParseGrouping(CsvTable table, List<ConfigIssue> issues)
        {
            var result = new GroupingTable();
            string file = Grouping.FileName;
            foreach (CsvRow row in table.Rows)
            {
                string category = row.Get("Категория");
                string group = row.Get("Группа");
                if (category.Length == 0 || group.Length == 0)
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, "пустая категория или группа"));
                    continue;
                }
                if (category == GroupingTable.AnyCategory)
                {
                    if (result.DefaultGroup != null) issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, "строка «*» повторяется, взята первая"));
                    else result.DefaultGroup = group;
                    continue;
                }
                if (result.ByCategory.ContainsKey(category))
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, $"категория {category} повторяется, взята первая"));
                    continue;
                }
                result.Add(category, group);
            }
            return result;
        }

        /// <summary>Имена параметров комплектов; null — какой-то роли нет, комплекты не обрабатываются.</summary>
        public static KitParameterNames? ParseKitParameters(CsvTable table, List<ConfigIssue> issues)
        {
            string file = KitParameters.FileName;
            var byRole = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (CsvRow row in table.Rows)
            {
                string role = row.Get("Роль");
                string name = row.Get("Параметр");
                if (role.Length == 0 || name.Length == 0)
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, "пустая роль или имя параметра"));
                    continue;
                }
                if (role != KitParameterNames.RoleCabinetNumber && role != KitParameterNames.RoleComponentFlag && role != KitParameterNames.RoleComponentKey)
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber,
                        $"роль «{role}» неизвестна; нужны «{KitParameterNames.RoleCabinetNumber}», «{KitParameterNames.RoleComponentFlag}», «{KitParameterNames.RoleComponentKey}»"));
                    continue;
                }
                if (!byRole.ContainsKey(role)) byRole[role] = name;
            }
            var missing = new List<string>();
            foreach (string role in new[] { KitParameterNames.RoleCabinetNumber, KitParameterNames.RoleComponentFlag, KitParameterNames.RoleComponentKey })
            {
                if (!byRole.ContainsKey(role)) missing.Add(role);
            }
            if (missing.Count > 0)
            {
                issues.Add(new ConfigIssue(Severity.Error, file, null, "нет строк: " + string.Join(", ", missing) + ". Комплекты шкафов не обрабатываются."));
                return null;
            }
            return new KitParameterNames(byRole[KitParameterNames.RoleCabinetNumber], byRole[KitParameterNames.RoleComponentFlag], byRole[KitParameterNames.RoleComponentKey]);
        }
    }

    public static class ValueText
    {
        /// <summary>Пусто: null, "" или только пробелы, табуляции, неразрывные пробелы (как в NestedParamSync v2.1).</summary>
        public static bool IsBlank(string? value) => value == null || value.Replace(' ', ' ').Trim().Length == 0;
    }
}
