using System.Collections.Generic;

namespace VKTools.Core.Checks
{
    /// <summary>
    /// Строка отчёта: что найдено, у какого элемента, что сейчас и что ожидается.
    /// Поля повторяют колонки отчёта из требований (REQ-REP-001).
    /// </summary>
    public sealed class CheckFinding
    {
        public CheckFinding(string check, Severity severity, string message)
        {
            Check = check;
            Severity = severity;
            Message = message;
        }

        /// <summary>Id элемента в модели; null — замечание к модели целиком (параметр, апдейтер).</summary>
        public long? ElementId { get; set; }

        /// <summary>Все элементы, к которым относится строка (например, все элементы с одним написанием значения).</summary>
        public List<long> RelatedElementIds { get; } = new List<long>();

        /// <summary>
        /// Второй элемент стыка двух сетей разных систем: отчёт предлагает «Разорвать стык» —
        /// разъединить коннекторы между <see cref="ElementId"/> и им, не сдвигая элементы (D-020).
        /// </summary>
        public long? JunctionWith { get; set; }

        /// <summary>Что выделить в модели: <see cref="ElementId"/> и <see cref="RelatedElementIds"/> без повторов.</summary>
        public IEnumerable<long> ElementsToShow()
        {
            var seen = new HashSet<long>();
            if (ElementId.HasValue && seen.Add(ElementId.Value)) yield return ElementId.Value;
            foreach (long id in RelatedElementIds)
            {
                if (seen.Add(id)) yield return id;
            }
        }

        public string Category { get; set; } = string.Empty;

        public string Family { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        /// <summary>Название проверки или правила.</summary>
        public string Check { get; }

        public Severity Severity { get; }

        public string Message { get; }

        public string Current { get; set; } = string.Empty;

        public string Expected { get; set; } = string.Empty;

        /// <summary>Что сделать пользователю.</summary>
        public string Action { get; set; } = string.Empty;

        public string SeverityText => Severity.ToRussian();

        public string ElementIdText => ElementId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }
}
