using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using VKTools.Core.Config;
using VKTools.Core.Text;

namespace VKTools.Core.Schedules
{
    /// <summary>Значение этажа в модели и его место по высоте.</summary>
    public sealed class FloorInfo
    {
        public FloorInfo(string value, double? elevation, int count)
        {
            Value = value ?? string.Empty;
            Elevation = elevation;
            Count = count;
            Number = FloorNames.Number(Value);
        }

        public string Value { get; }

        /// <summary>Отметка уровня (м), если значение этажа получено из уровня модели.</summary>
        public double? Elevation { get; }

        /// <summary>Номер этажа из текста («01 этаж» → 1, «-1 этаж» → −1).</summary>
        public int? Number { get; }

        /// <summary>Элементов ВК с этим значением.</summary>
        public int Count { get; }
    }

    /// <summary>Номера этажей в тексте и порядок «ниже / выше первого этажа».</summary>
    public static class FloorNames
    {
        private static readonly Regex NumberBefore = new Regex(@"(?<!\d)(-?\d{1,3})\s*(?:-?(?:й|ый|ой))?\s*этаж", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NumberAfter = new Regex(@"этаж\w*\s*(-?\d{1,3})(?!\d)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex OnlyNumber = new Regex(@"^\s*(-?\d{1,3})\s*$", RegexOptions.CultureInvariant);

        public static int? Number(string? value)
        {
            string v = value ?? string.Empty;
            Match m = NumberBefore.Match(v);
            if (!m.Success) m = NumberAfter.Match(v);
            if (!m.Success) m = OnlyNumber.Match(v);
            return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n) ? n : (int?)null;
        }

        /// <summary>Порядок снизу вверх: по отметке уровня, потом по номеру, потом по имени.</summary>
        public static List<FloorInfo> Order(IEnumerable<FloorInfo> floors) =>
            floors.OrderBy(f => f.Elevation.HasValue ? 0 : 1)
                .ThenBy(f => f.Elevation ?? 0)
                .ThenBy(f => f.Number.HasValue ? 0 : 1)
                .ThenBy(f => f.Number ?? 0)
                .ThenBy(f => f.Value, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        /// <summary>Первый этаж по умолчанию: номер 1 в имени, иначе уровень с отметкой ближе всего к нулю.</summary>
        public static string? GuessFirst(IReadOnlyList<FloorInfo> floors)
        {
            FloorInfo? byNumber = floors.FirstOrDefault(f => f.Number == 1);
            if (byNumber != null) return byNumber.Value;
            return floors.Where(f => f.Elevation.HasValue).OrderBy(f => Math.Abs(f.Elevation!.Value)).FirstOrDefault()?.Value;
        }

        /// <summary>
        /// Ниже первого этажа (true), первый и выше (false), не понять (null): у этажа нет ни отметки, ни номера,
        /// или у первого есть только одно, а у этого — только другое.
        /// </summary>
        public static bool? IsBelow(FloorInfo floor, FloorInfo first)
        {
            if (floor.Elevation.HasValue && first.Elevation.HasValue) return floor.Elevation.Value < first.Elevation.Value - 1e-6;
            if (floor.Number.HasValue && first.Number.HasValue) return floor.Number.Value < first.Number.Value;
            return null;
        }

        /// <summary>«1 этажа»: номер, если он есть, иначе «уровня «Имя»».</summary>
        public static string Genitive(string floor)
        {
            int? n = Number(floor);
            return n.HasValue ? n.Value.ToString(CultureInfo.InvariantCulture) + " этажа" : $"уровня «{floor}»";
        }
    }

    /// <summary>Группа систем для быстрого выбора: «Канализация» — системы, начинающиеся на «К».</summary>
    public sealed class SystemGroup
    {
        public SystemGroup(string name, IReadOnlyList<string> prefixes, string title)
        {
            Name = name;
            Prefixes = prefixes;
            Title = title;
        }

        /// <summary>Подпись кнопки: «Канализация».</summary>
        public string Name { get; }

        public IReadOnlyList<string> Prefixes { get; }

        /// <summary>Как в названии спецификации: «систем канализации».</summary>
        public string Title { get; }

        public bool Contains(string system) =>
            Prefixes.Any(p => (system ?? string.Empty).Trim().StartsWith(p, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>Графа формы спецификации: заголовок, параметр, ширина и что с ней делать.</summary>
    public sealed class FormColumn
    {
        public FormColumn(string heading, ParameterRef parameter, double widthMm, bool total, bool hidden, int sort, bool center)
        {
            Heading = heading;
            Parameter = parameter;
            WidthMm = widthMm;
            Total = total;
            Hidden = hidden;
            Sort = sort;
            Center = center;
        }

        public string Heading { get; }

        public ParameterRef Parameter { get; }

        public double WidthMm { get; }

        /// <summary>Суммировать в неразвёрнутой строке (количество).</summary>
        public bool Total { get; }

        /// <summary>Скрытая графа: только для сортировки.</summary>
        public bool Hidden { get; }

        /// <summary>Порядок сортировки (1, 2, …); 0 — не сортировать.</summary>
        public int Sort { get; }

        public bool Center { get; }

        /// <summary>Шаг округления числа в графе (0,1 — до десятых); null — как в проекте. Лишние нули не показываются: 2, 158,9.</summary>
        public double? Accuracy { get; set; }
    }

    /// <summary>Файлы правил кнопки «Создать спецификацию».</summary>
    public static class ScheduleRules
    {
        public const string Yes = "да";

        public static readonly RuleFileSpec Form =
            new RuleFileSpec("schedule_form.csv", 1, "Заголовок", "Параметр", "GUID", "Ширина, мм");

        public static readonly RuleFileSpec Groups =
            new RuleFileSpec("schedule_groups.csv", 1, "Группа", "Начинается с", "В названии");

        public static IReadOnlyList<RuleFileSpec> All { get; } = new[] { Form, Groups };

        public static List<FormColumn> ParseForm(CsvTable table, List<ConfigIssue> issues)
        {
            var result = new List<FormColumn>();
            foreach (CsvRow row in table.Rows)
            {
                string heading = row.Get("Заголовок");
                string name = row.Get("Параметр");
                if (!Guid.TryParse(row.Get("GUID"), out Guid guid) || guid == Guid.Empty)
                {
                    issues.Add(new ConfigIssue(Checks.Severity.Error, Form.FileName, row.LineNumber, $"«{name}»: непонятный GUID «{row.Get("GUID")}»"));
                    continue;
                }
                string widthText = row.Get("Ширина, мм").Replace(',', '.');
                if (!double.TryParse(widthText, NumberStyles.Float, CultureInfo.InvariantCulture, out double width) || width <= 0 || width > 500)
                {
                    issues.Add(new ConfigIssue(Checks.Severity.Error, Form.FileName, row.LineNumber, $"«{name}»: ширина «{row.Get("Ширина, мм")}» — нужно число мм от 1 до 500"));
                    continue;
                }
                int sort = 0;
                string sortText = row.Get("Сортировка");
                if (sortText.Length > 0 && (!int.TryParse(sortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out sort) || sort < 0))
                {
                    issues.Add(new ConfigIssue(Checks.Severity.Warning, Form.FileName, row.LineNumber, $"«{name}»: сортировка «{sortText}» — нужно целое 1, 2, 3…; графа без сортировки"));
                    sort = 0;
                }
                double? accuracy = null;
                string accuracyText = row.Get("Точность").Replace(',', '.');
                if (accuracyText.Length > 0)
                {
                    if (double.TryParse(accuracyText, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) && a > 0 && a <= 1000) accuracy = a;
                    else issues.Add(new ConfigIssue(Checks.Severity.Warning, Form.FileName, row.LineNumber, $"«{name}»: точность «{row.Get("Точность")}» — нужно число (0,1; 0,01; 1); графа как в проекте"));
                }
                result.Add(new FormColumn(heading.Length > 0 ? heading : name, new ParameterRef(name, guid), width,
                    IsYes(row.Get("Итог")), IsYes(row.Get("Скрыть")), sort, string.Equals(row.Get("Выравнивание"), "центр", StringComparison.OrdinalIgnoreCase))
                {
                    Accuracy = accuracy,
                });
            }
            if (!result.Any(c => !c.Hidden))
            {
                issues.Add(new ConfigIssue(Checks.Severity.Error, Form.FileName, null, "в форме нет ни одной видимой графы"));
            }
            return result;
        }

        public static List<SystemGroup> ParseGroups(CsvTable table, List<ConfigIssue> issues)
        {
            var result = new List<SystemGroup>();
            foreach (CsvRow row in table.Rows)
            {
                string name = row.Get("Группа");
                List<string> prefixes = row.Get("Начинается с").Split(',').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                if (name.Length == 0 || prefixes.Count == 0)
                {
                    issues.Add(new ConfigIssue(Checks.Severity.Warning, Groups.FileName, row.LineNumber, "нужны «Группа» и хотя бы одно начало имени системы — строка пропущена"));
                    continue;
                }
                string title = row.Get("В названии");
                result.Add(new SystemGroup(name, prefixes, title.Length > 0 ? title : "систем " + name.ToLowerInvariant()));
            }
            return result;
        }

        private static bool IsYes(string text) => string.Equals(text.Trim(), Yes, StringComparison.OrdinalIgnoreCase) || text.Trim() == "1";
    }
}
