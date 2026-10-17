using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Text;

namespace VKTools.Core.Spec
{
    /// <summary>
    /// Строка таблицы групп ADSK_Группирование: номер, название и что в неё входит — категория (имя BuiltInCategory,
    /// «*» — все остальные) и/или маска имени семейства. «Не трогать» — элементы строки плагин не пишет.
    /// </summary>
    public sealed class GroupRule
    {
        public GroupRule(string group, string title, string category, string family, bool skip, int? line = null)
        {
            Group = (group ?? string.Empty).Trim();
            Title = (title ?? string.Empty).Trim();
            Category = (category ?? string.Empty).Trim();
            Family = (family ?? string.Empty).Trim();
            Skip = skip;
            Line = line;
        }

        /// <summary>Номер группы (текст: «4», «04», «4.1»); у «не трогать» может быть пустым.</summary>
        public string Group { get; }

        public string Title { get; }

        /// <summary>Имя BuiltInCategory, «*» или пусто (для строки семейства — любая категория).</summary>
        public string Category { get; }

        /// <summary>Маска имени семейства («Кран поливочн*»); пусто — строка категории.</summary>
        public string Family { get; }

        public bool Skip { get; }

        public int? Line { get; }

        public bool IsFamilyRule => Family.Length > 0;

        public bool IsDefaultRule => Family.Length == 0 && Category == GroupingTable.AnyCategory;

        public string ActionText => Skip ? GroupingTable.SkipAction : GroupingTable.WriteAction;
    }

    /// <summary>
    /// Таблица групп ADSK_Группирование (grouping.csv или настройка проекта). Правило выбирается от точного к общему:
    /// семейство верхнего родителя (строка семейства действует на него и всё, что в него вложено) → своё семейство →
    /// категория → «*». Нет подходящей строки — элемент не трогаем.
    /// </summary>
    public sealed class GroupingTable
    {
        public const string AnyCategory = "*";
        public const string WriteAction = "писать";
        public const string SkipAction = "не трогать";

        private readonly List<GroupRule> _rules = new List<GroupRule>();
        private readonly List<GroupRule> _familyRules = new List<GroupRule>();
        private readonly Dictionary<string, GroupRule> _byCategory = new Dictionary<string, GroupRule>(StringComparer.Ordinal);
        private GroupRule? _default;

        public GroupingTable()
        {
        }

        public GroupingTable(IEnumerable<GroupRule> rules)
        {
            foreach (GroupRule rule in rules) Add(rule, null);
        }

        /// <summary>Строки в порядке таблицы (с повторами, как в файле).</summary>
        public IReadOnlyList<GroupRule> Rules => _rules;

        /// <summary>Группа категорий, которых нет в таблице; null — такие элементы не трогаем.</summary>
        public string? DefaultGroup => _default == null || _default.Skip ? null : _default.Group;

        /// <summary>Группа для категории (имя BuiltInCategory) без учёта семейств; null — не трогать.</summary>
        public string? GroupFor(string? builtInCategory)
        {
            GroupRule? rule = Resolve(builtInCategory, null, null, null);
            return rule == null || rule.Skip ? null : rule.Group;
        }

        /// <summary>Категория названа в таблице явно (не через «*»).</summary>
        public bool IsListed(string? builtInCategory) => builtInCategory != null && _byCategory.ContainsKey(builtInCategory);

        /// <summary>
        /// Правило для элемента. <paramref name="topFamily"/> и <paramref name="topCategory"/> — верхний родитель
        /// вложенного элемента (для элемента верхнего уровня — null). null — элемент не трогаем.
        /// </summary>
        public GroupRule? Resolve(string? category, string? family, string? topCategory, string? topFamily)
        {
            if (topFamily != null)
            {
                foreach (GroupRule rule in _familyRules)
                {
                    if (FamilyMask.Matches(rule.Family, topFamily) && CategoryFits(rule, topCategory)) return rule;
                }
            }
            if (family != null)
            {
                foreach (GroupRule rule in _familyRules)
                {
                    if (FamilyMask.Matches(rule.Family, family) && CategoryFits(rule, category)) return rule;
                }
            }
            if (category != null && _byCategory.TryGetValue(category, out GroupRule? byCategory)) return byCategory;
            return _default;
        }

        private static bool CategoryFits(GroupRule rule, string? category) =>
            rule.Category.Length == 0 || rule.Category == AnyCategory || string.Equals(rule.Category, category, StringComparison.Ordinal);

        /// <summary>Добавить строку; повтор категории или «*» — замечание, действует первая.</summary>
        internal void Add(GroupRule rule, Action<string>? duplicate)
        {
            _rules.Add(rule);
            if (rule.IsFamilyRule)
            {
                _familyRules.Add(rule);
                return;
            }
            if (rule.IsDefaultRule)
            {
                if (_default != null) duplicate?.Invoke("строка «*» повторяется, взята первая");
                else _default = rule;
                return;
            }
            if (_byCategory.ContainsKey(rule.Category))
            {
                duplicate?.Invoke($"категория {rule.Category} повторяется, взята первая");
                return;
            }
            _byCategory[rule.Category] = rule;
        }

        /// <summary>Номера групп в порядке текстовой сортировки — так их расставит спецификация.</summary>
        public IReadOnlyList<string> GroupOrder() =>
            _rules.Where(r => !r.Skip && r.Group.Length > 0).Select(r => r.Group).Distinct(StringComparer.Ordinal)
                .OrderBy(g => g, StringComparer.Ordinal).ToList();

        /// <summary>
        /// Пары номеров, где текстовый порядок расходится с числовым («10» встанет перед «2»).
        /// Пусто — номера сортируются как задуманы.
        /// </summary>
        public IReadOnlyList<(string First, string Second)> SortSurprises()
        {
            IReadOnlyList<string> order = GroupOrder();
            var result = new List<(string, string)>();
            for (int i = 0; i < order.Count; i++)
            {
                for (int j = i + 1; j < order.Count; j++)
                {
                    if (NaturalCompare(order[i], order[j]) > 0) result.Add((order[i], order[j]));
                }
            }
            return result;
        }

        /// <summary>Сравнение «как числа»: «2» &lt; «10», «4.1» &lt; «4.10».</summary>
        internal static int NaturalCompare(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;
                    string na = a.Substring(si, i - si).TrimStart('0');
                    string nb = b.Substring(sj, j - sj).TrimStart('0');
                    if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                    int c = string.CompareOrdinal(na, nb);
                    if (c != 0) return c;
                    continue;
                }
                if (a[i] != b[j]) return a[i].CompareTo(b[j]);
                i++;
                j++;
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }

    /// <summary>Маска имени семейства: «*» — любые символы, «?» — один символ, без учёта регистра.</summary>
    public static class FamilyMask
    {
        public static bool Matches(string mask, string name)
        {
            mask = mask.Trim();
            if (mask.Length == 0) return false;
            return Match(mask.ToUpperInvariant(), 0, name.Trim().ToUpperInvariant(), 0);
        }

        private static bool Match(string mask, int m, string text, int t)
        {
            while (m < mask.Length)
            {
                char c = mask[m];
                if (c == '*')
                {
                    while (m < mask.Length && mask[m] == '*') m++;
                    if (m == mask.Length) return true;
                    for (int k = t; k <= text.Length; k++)
                    {
                        if (Match(mask, m, text, k)) return true;
                    }
                    return false;
                }
                if (t >= text.Length) return false;
                if (c != '?' && c != text[t]) return false;
                m++;
                t++;
            }
            return t == text.Length;
        }
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

        public const string ColumnGroup = "Группа";
        public const string ColumnTitle = "Название";
        public const string ColumnCategory = "Категория";
        public const string ColumnFamily = "Семейство";
        public const string ColumnAction = "Действие";

        private static readonly string[] GroupingHeader = { ColumnGroup, ColumnTitle, ColumnCategory, ColumnFamily, ColumnAction };
        private static readonly string[] NestedSelectionHeader = { "Параметр", "Ключ" };
        private static readonly string[] NestedSelectionComments = { "VK Tools: параметры, которые переносятся из родителя во вложенные (настройка проекта)." };

        public static readonly RuleFileSpec Grouping =
            new RuleFileSpec("grouping.csv", 1, ColumnCategory, ColumnGroup);

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
                string category = row.Get(ColumnCategory);
                string group = row.Get(ColumnGroup);
                string family = row.Get(ColumnFamily);
                string action = row.Get(ColumnAction);
                bool skip;
                if (action.Length == 0 || string.Equals(action, GroupingTable.WriteAction, StringComparison.OrdinalIgnoreCase)) skip = false;
                else if (string.Equals(action, GroupingTable.SkipAction, StringComparison.OrdinalIgnoreCase)) skip = true;
                else
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, $"действие «{action}» неизвестно; нужно «{GroupingTable.WriteAction}» или «{GroupingTable.SkipAction}»"));
                    continue;
                }
                if (category.Length == 0 && family.Length == 0)
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, "нет ни категории, ни семейства"));
                    continue;
                }
                if (!skip && group.Length == 0)
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, "пустой номер группы"));
                    continue;
                }
                int line = row.LineNumber;
                result.Add(new GroupRule(group, row.Get(ColumnTitle), category, family, skip, line),
                    message => issues.Add(new ConfigIssue(Severity.Error, file, line, message)));
            }
            return result;
        }

        /// <summary>Таблица групп из текста (настройка проекта, импорт файла): схема, колонки, строки. null — не разобрать.</summary>
        public static GroupingTable? ParseGroupingText(string text, string source, List<ConfigIssue> issues)
        {
            CsvTable table = CsvTable.Parse(text);
            var spec = new RuleFileSpec(source, Grouping.SchemaVersion, Grouping.RequiredColumns.ToArray());
            if (!ConfigLoader.Validate(table, spec, issues)) return null;
            var rowIssues = new List<ConfigIssue>();
            GroupingTable result = ParseGrouping(table, rowIssues);
            issues.AddRange(rowIssues.Select(i => new ConfigIssue(i.Severity, source, i.Line, i.Message)));
            return result;
        }

        /// <summary>Таблица групп в формате grouping.csv: настройка проекта, экспорт, файл для папки отдела.</summary>
        public static string FormatGrouping(IEnumerable<GroupRule> rules, IEnumerable<string> comments) =>
            CsvWriter.Table(comments, Grouping.SchemaVersion, GroupingHeader,
                rules.Select(r => (IReadOnlyList<string>)new[] { r.Group, r.Title, r.Category, r.Family, r.Skip ? GroupingTable.SkipAction : string.Empty }));

        /// <summary>Выбор параметров переноса во вложенные (настройка проекта): «Параметр;Ключ».</summary>
        public static string FormatNestedSelection(IEnumerable<ParamKey> keys) =>
            CsvWriter.Table(NestedSelectionComments, 1, NestedSelectionHeader, keys.Select(k => (IReadOnlyList<string>)new[] { k.Name, k.Id }));

        public static List<ParamKey> ParseNestedSelection(string text, List<ConfigIssue> issues)
        {
            const string file = "настройка проекта: перенос во вложенные";
            var keys = new List<ParamKey>();
            CsvTable table = CsvTable.Parse(text);
            if (!ConfigLoader.Validate(table, new RuleFileSpec(file, 1, "Параметр", "Ключ"), issues)) return keys;
            foreach (CsvRow row in table.Rows)
            {
                ParamKey? key = ParamKey.Parse(row.Get("Ключ"), row.Get("Параметр"));
                if (key == null)
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, $"непонятный ключ «{row.Get("Ключ")}»"));
                    continue;
                }
                if (!keys.Contains(key)) keys.Add(key);
            }
            return keys;
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
