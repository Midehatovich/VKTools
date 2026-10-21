using System.Collections.Generic;
using System.Text;

namespace VKTools.Core.Text
{
    /// <summary>
    /// Запись таблиц правил в тот же формат, что читает <see cref="CsvTable"/>: «;», кавычки по необходимости.
    /// Значения не экранируются от Excel (в отличие от отчёта): файл должен читаться обратно байт в байт.
    /// </summary>
    public static class CsvWriter
    {
        private static readonly char[] Special = { ';', '"', '\n', '\r' };

        public static string Field(string? value)
        {
            value ??= string.Empty;
            bool quote = value.IndexOfAny(Special) >= 0 || value.StartsWith("#", System.StringComparison.Ordinal)
                || (value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1])));
            return quote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        /// <summary>Комментарии (без «#»), строка «#schema=N», заголовок и строки.</summary>
        public static string Table(IEnumerable<string> comments, int schemaVersion, IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
        {
            var sb = new StringBuilder();
            foreach (string comment in comments) sb.Append("# ").Append(comment.Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
            sb.Append("#schema=").Append(schemaVersion).Append('\n');
            sb.Append(Line(header)).Append('\n');
            foreach (IReadOnlyList<string> row in rows) sb.Append(Line(row)).Append('\n');
            return sb.ToString();
        }

        private static string Line(IReadOnlyList<string> fields)
        {
            var parts = new string[fields.Count];
            for (int i = 0; i < fields.Count; i++) parts[i] = Field(fields[i]);
            return string.Join(";", parts);
        }
    }
}
