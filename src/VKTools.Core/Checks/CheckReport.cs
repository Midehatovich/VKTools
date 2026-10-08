using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VKTools.Core.Checks
{
    /// <summary>Отчёт одного запуска: шапка (документ, версии, источники правил) и строки.</summary>
    public sealed class CheckReport
    {
        private static readonly string[] Header =
        {
            "Важность", "Проверка", "ElementId", "Категория", "Семейство", "Тип", "Сообщение", "Сейчас", "Ожидается", "Что сделать",
        };

        private static readonly char[] CsvSpecialChars = { ';', '"', '\n', '\r' };

        public CheckReport(string title, string documentTitle, DateTime createdAt)
        {
            Title = title;
            DocumentTitle = documentTitle;
            CreatedAt = createdAt;
        }

        public string Title { get; }

        public string DocumentTitle { get; }

        public DateTime CreatedAt { get; }

        public string PluginVersion { get; set; } = string.Empty;

        public string RevitVersion { get; set; } = string.Empty;

        /// <summary>Откуда взяты правила: «parameters.csv — встроенные» и т. п. Видно расхождение между рабочими местами.</summary>
        public List<string> RuleSources { get; } = new List<string>();

        public List<CheckFinding> Findings { get; } = new List<CheckFinding>();

        public int Count(Severity severity) => Findings.Count(f => f.Severity == severity);

        public string Summary =>
            $"Ошибок: {Count(Severity.Error)}, предупреждений: {Count(Severity.Warning)}, инфо: {Count(Severity.Info)}";

        /// <summary>CSV для Excel: «;», UTF-8 с BOM (иначе Excel покажет кракозябры).</summary>
        public string ToCsv()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Csv("# " + Title));
            sb.AppendLine(Csv("# Документ: " + DocumentTitle));
            sb.AppendLine(Csv("# Дата: " + CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
            sb.AppendLine(Csv($"# VK Tools {PluginVersion}, Revit {RevitVersion}"));
            foreach (string source in RuleSources) sb.AppendLine(Csv("# Правила: " + source));
            sb.AppendLine(string.Join(";", Header));
            foreach (CheckFinding f in Findings)
            {
                sb.AppendLine(string.Join(";", new[]
                {
                    Csv(f.SeverityText), Csv(f.Check), Csv(f.ElementIdText), Csv(f.Category), Csv(f.Family), Csv(f.Type),
                    Csv(f.Message), Csv(f.Current), Csv(f.Expected), Csv(f.Action),
                }));
            }
            return sb.ToString();
        }

        internal static string Csv(string? value)
        {
            value ??= string.Empty;
            bool quote = value.IndexOfAny(CsvSpecialChars) >= 0
                || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1])));
            // Формулы Excel из значений модели не исполняем: «=», «+», «-», «@» в начале экранируем апострофом.
            if (value.Length > 0 && "=+-@".IndexOf(value[0]) >= 0) value = "'" + value;
            return quote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
