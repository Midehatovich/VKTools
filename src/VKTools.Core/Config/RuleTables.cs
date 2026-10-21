using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Text;

namespace VKTools.Core.Config
{
    /// <summary>Какой привязки ждём у параметра проекта.</summary>
    public enum BindingKind
    {
        Any,
        Instance,
        Type,
    }

    /// <summary>Параметр из ФОП, который нужен плагину и спецификациям ВК (parameters.csv).</summary>
    public sealed class RequiredParameter
    {
        public RequiredParameter(string name, Guid guid, bool mandatory, BindingKind binding,
            IReadOnlyList<string> categories, string purpose, int line)
        {
            Name = name;
            Guid = guid;
            Mandatory = mandatory;
            Binding = binding;
            Categories = categories;
            Purpose = purpose;
            Line = line;
        }

        public string Name { get; }

        public Guid Guid { get; }

        /// <summary>Нет в проекте → ошибка (иначе — информация: параметр нужен не во всех проектах).</summary>
        public bool Mandatory { get; }

        public BindingKind Binding { get; }

        /// <summary>Имена BuiltInCategory (OST_PipeCurves…), не зависят от языка Revit.</summary>
        public IReadOnlyList<string> Categories { get; }

        public string Purpose { get; }

        public int Line { get; }
    }

    /// <summary>Ссылка на параметр: имя для отчёта и GUID для поиска.</summary>
    public sealed class ParameterRef
    {
        public ParameterRef(string name, Guid guid)
        {
            Name = name;
            Guid = guid;
        }

        public string Name { get; }

        public Guid Guid { get; }
    }

    /// <summary>Категория из списка категорий ВК (vk_categories.csv).</summary>
    public sealed class CategoryRef
    {
        public CategoryRef(string builtInName, string displayName)
        {
            BuiltInName = builtInName;
            DisplayName = displayName;
        }

        public string BuiltInName { get; }

        public string DisplayName { get; }
    }

    /// <summary>Апдейтер, о котором надо предупредить (updaters.csv).</summary>
    public sealed class UpdaterRule
    {
        public UpdaterRule(string pattern, Severity severity, string message)
        {
            Pattern = pattern;
            Severity = severity;
            Message = message;
        }

        /// <summary>Подстрока имени апдейтера или приложения, без учёта регистра.</summary>
        public string Pattern { get; }

        public Severity Severity { get; }

        public string Message { get; }

        public bool Matches(string? updaterName, string? applicationName) =>
            Contains(updaterName) || Contains(applicationName);

        private bool Contains(string? text) =>
            !string.IsNullOrEmpty(text) && text!.IndexOf(Pattern, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Результат разбора таблицы правил в объекты.</summary>
    public sealed class ParsedRules<T>
    {
        public List<T> Items { get; } = new List<T>();

        public List<ConfigIssue> Issues { get; } = new List<ConfigIssue>();
    }

    /// <summary>Описания файлов правил первого релиза и их разбор.</summary>
    public static class RuleTables
    {
        private static readonly char[] ListSeparators = { ',' };

        public static readonly RuleFileSpec Parameters =
            new RuleFileSpec("parameters.csv", 1, "Параметр", "GUID", "Обязательный", "Привязка", "Категории", "Назначение");

        public static readonly RuleFileSpec VkCategories =
            new RuleFileSpec("vk_categories.csv", 1, "Категория", "Название");

        public static readonly RuleFileSpec Updaters =
            new RuleFileSpec("updaters.csv", 1, "Шаблон", "Важность", "Сообщение");

        public static readonly RuleFileSpec InsulationFromHost =
            new RuleFileSpec("insulation_from_host.csv", 1, "Параметр", "GUID");

        public static readonly RuleFileSpec LookalikeValues =
            new RuleFileSpec("lookalike_values.csv", 1, "Параметр", "GUID");

        /// <summary>Все файлы правил: проверка в настройках и тест встроенных правил. Файлы Р2 описаны в <see cref="Spec.SpecRuleTables"/>, Р3 — в <see cref="Calc.CalcTables"/>.</summary>
        public static IReadOnlyList<RuleFileSpec> All { get; } = new[]
        {
            Parameters, VkCategories, Updaters, InsulationFromHost, LookalikeValues,
            Spec.SpecRuleTables.NestedParameters, Spec.SpecRuleTables.Grouping, Spec.SpecRuleTables.KitParameters,
            Calc.CalcTables.Resources, Calc.CalcTables.PipeRules, Calc.CalcTables.Supports, Calc.CalcTables.Cord, Calc.CalcTables.Reserve,
        };

        public static ParsedRules<RequiredParameter> ParseParameters(CsvTable table, string fileName)
        {
            var result = new ParsedRules<RequiredParameter>();
            var seen = new HashSet<Guid>();
            foreach (CsvRow row in table.Rows)
            {
                string name = row.Get("Параметр");
                if (!TryGuid(row, fileName, result.Issues, out Guid guid)) continue;
                if (name.Length == 0)
                {
                    result.Issues.Add(new ConfigIssue(Severity.Error, fileName, row.LineNumber, "пустое имя параметра"));
                    continue;
                }
                if (!TryYesNo(row.Get("Обязательный"), out bool mandatory))
                {
                    result.Issues.Add(new ConfigIssue(Severity.Error, fileName, row.LineNumber,
                        $"«Обязательный» = «{row.Get("Обязательный")}», нужно «да» или «нет»"));
                    continue;
                }
                if (!TryBinding(row.Get("Привязка"), out BindingKind binding))
                {
                    result.Issues.Add(new ConfigIssue(Severity.Error, fileName, row.LineNumber,
                        $"«Привязка» = «{row.Get("Привязка")}», нужно «Экземпляр», «Тип» или «Любая»"));
                    continue;
                }
                if (!seen.Add(guid))
                {
                    result.Issues.Add(new ConfigIssue(Severity.Error, fileName, row.LineNumber, $"GUID {guid} повторяется"));
                    continue;
                }
                List<string> categories = SplitList(row.Get("Категории"));
                result.Items.Add(new RequiredParameter(name, guid, mandatory, binding, categories, row.Get("Назначение"), row.LineNumber));
            }
            return result;
        }

        public static ParsedRules<ParameterRef> ParseParameterRefs(CsvTable table, string fileName)
        {
            var result = new ParsedRules<ParameterRef>();
            foreach (CsvRow row in table.Rows)
            {
                string name = row.Get("Параметр");
                if (!TryGuid(row, fileName, result.Issues, out Guid guid)) continue;
                result.Items.Add(new ParameterRef(name.Length == 0 ? guid.ToString() : name, guid));
            }
            return result;
        }

        public static ParsedRules<CategoryRef> ParseCategories(CsvTable table, string fileName)
        {
            var result = new ParsedRules<CategoryRef>();
            foreach (CsvRow row in table.Rows)
            {
                string key = row.Get("Категория");
                if (key.Length == 0)
                {
                    result.Issues.Add(new ConfigIssue(Severity.Error, fileName, row.LineNumber, "пустая категория"));
                    continue;
                }
                string display = row.Get("Название");
                result.Items.Add(new CategoryRef(key, display.Length == 0 ? key : display));
            }
            return result;
        }

        public static ParsedRules<UpdaterRule> ParseUpdaters(CsvTable table, string fileName)
        {
            var result = new ParsedRules<UpdaterRule>();
            foreach (CsvRow row in table.Rows)
            {
                string pattern = row.Get("Шаблон");
                if (pattern.Length == 0)
                {
                    result.Issues.Add(new ConfigIssue(Severity.Error, fileName, row.LineNumber, "пустой шаблон"));
                    continue;
                }
                if (!SeverityText.TryParse(row.Get("Важность"), out Severity severity))
                {
                    result.Issues.Add(new ConfigIssue(Severity.Error, fileName, row.LineNumber,
                        $"«Важность» = «{row.Get("Важность")}», нужно «Ошибка», «Предупреждение» или «Инфо»"));
                    continue;
                }
                result.Items.Add(new UpdaterRule(pattern, severity, row.Get("Сообщение")));
            }
            return result;
        }

        internal static List<string> SplitList(string text) =>
            text.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();

        private static bool TryGuid(CsvRow row, string fileName, List<ConfigIssue> issues, out Guid guid)
        {
            string text = row.Get("GUID");
            if (Guid.TryParse(text, out guid)) return true;
            issues.Add(new ConfigIssue(Severity.Error, fileName, row.LineNumber, $"«{text}» — не GUID"));
            return false;
        }

        private static bool TryYesNo(string text, out bool value)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "да":
                    value = true;
                    return true;
                case "нет":
                    value = false;
                    return true;
                default:
                    value = false;
                    return false;
            }
        }

        private static bool TryBinding(string text, out BindingKind binding)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "экземпляр":
                    binding = BindingKind.Instance;
                    return true;
                case "тип":
                    binding = BindingKind.Type;
                    return true;
                case "любая":
                case "":
                    binding = BindingKind.Any;
                    return true;
                default:
                    binding = BindingKind.Any;
                    return false;
            }
        }
    }
}
