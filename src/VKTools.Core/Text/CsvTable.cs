using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VKTools.Core.Text
{
    /// <summary>Строка CSV с доступом по имени колонки.</summary>
    public sealed class CsvRow
    {
        private readonly IReadOnlyDictionary<string, int> _columnIndex;
        private readonly string[] _values;

        internal CsvRow(int lineNumber, string[] values, IReadOnlyDictionary<string, int> columnIndex)
        {
            LineNumber = lineNumber;
            _values = values;
            _columnIndex = columnIndex;
        }

        /// <summary>Номер строки в файле (с 1), чтобы сообщение об ошибке указывало на место.</summary>
        public int LineNumber { get; }

        public IReadOnlyList<string> Values => _values;

        /// <summary>Значение колонки без пробелов по краям; пустая строка, если колонки или значения нет.</summary>
        public string Get(string column)
        {
            if (!_columnIndex.TryGetValue(column, out int index)) return string.Empty;
            return index < _values.Length ? _values[index] : string.Empty;
        }
    }

    /// <summary>
    /// Таблица правил в формате отдела: разделитель «;», строки с «#» — комментарии,
    /// первая непустая некомментарная строка — заголовок.
    /// Директива «#schema=N» в комментарии задаёт версию схемы файла.
    /// </summary>
    public sealed class CsvTable
    {
        private readonly Dictionary<string, int> _columnIndex;

        private CsvTable(IReadOnlyList<string> columns, IReadOnlyList<CsvRow> rows, int? schemaVersion,
            Dictionary<string, int> columnIndex)
        {
            Columns = columns;
            Rows = rows;
            SchemaVersion = schemaVersion;
            _columnIndex = columnIndex;
        }

        public IReadOnlyList<string> Columns { get; }

        public IReadOnlyList<CsvRow> Rows { get; }

        public int? SchemaVersion { get; }

        public bool HasColumn(string column) => _columnIndex.ContainsKey(column);

        public static CsvTable Parse(string text, char separator = ';')
        {
            if (text == null) throw new ArgumentNullException(nameof(text));

            int? schema = null;
            string[]? header = null;
            var columnIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<CsvRow>();

            foreach (var (lineNumber, fields, isComment, rawLine) in ReadRecords(text, separator))
            {
                if (isComment)
                {
                    int? s = TryReadSchemaDirective(rawLine);
                    if (s.HasValue) schema = s;
                    continue;
                }

                if (fields.All(f => f.Length == 0)) continue;

                if (header == null)
                {
                    header = TrimTrailingEmpty(fields);
                    for (int i = 0; i < header.Length; i++)
                    {
                        if (header[i].Length > 0 && !columnIndex.ContainsKey(header[i]))
                            columnIndex.Add(header[i], i);
                    }
                    continue;
                }

                rows.Add(new CsvRow(lineNumber, fields, columnIndex));
            }

            return new CsvTable(header ?? Array.Empty<string>(), rows, schema, columnIndex);
        }

        private static string[] TrimTrailingEmpty(string[] fields)
        {
            int n = fields.Length;
            while (n > 0 && fields[n - 1].Length == 0) n--;
            return fields.Take(n).ToArray();
        }

        private static int? TryReadSchemaDirective(string rawLine)
        {
            // «#schema=1» или «# schema = 1;;;;» (Excel дописывает разделители)
            string s = rawLine.TrimStart('#').Trim().TrimEnd(';', ',').Trim();
            if (!s.StartsWith("schema", StringComparison.OrdinalIgnoreCase)) return null;
            string rest = s.Substring("schema".Length).Trim();
            if (!rest.StartsWith("=", StringComparison.Ordinal)) return null;
            string value = rest.Substring(1).Trim().TrimEnd(';').Trim();
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : (int?)null;
        }

        /// <summary>Разбор записей с учётом кавычек и переводов строк внутри кавычек.</summary>
        private static IEnumerable<(int LineNumber, string[] Fields, bool IsComment, string RawLine)> ReadRecords(
            string text, char separator)
        {
            int i = 0;
            int line = 1;
            while (i < text.Length)
            {
                int recordLine = line;
                int lineStart = i;

                // Комментарий: строка, первый непробельный символ которой «#»
                int j = i;
                while (j < text.Length && (text[j] == ' ' || text[j] == '\t')) j++;
                if (j < text.Length && text[j] == '#')
                {
                    int end = text.IndexOf('\n', j);
                    if (end < 0) end = text.Length;
                    string raw = text.Substring(lineStart, end - lineStart).TrimEnd('\r');
                    i = end + 1;
                    line++;
                    yield return (recordLine, Array.Empty<string>(), true, raw);
                    continue;
                }

                var fields = new List<string>();
                var field = new StringBuilder();
                bool inQuotes = false;
                bool endOfRecord = false;
                while (i < text.Length && !endOfRecord)
                {
                    char c = text[i];
                    if (inQuotes)
                    {
                        if (c == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"')
                            {
                                field.Append('"');
                                i += 2;
                                continue;
                            }
                            inQuotes = false;
                            i++;
                            continue;
                        }
                        if (c == '\n') line++;
                        field.Append(c);
                        i++;
                        continue;
                    }

                    if (c == '"' && field.ToString().Trim().Length == 0)
                    {
                        field.Clear();
                        inQuotes = true;
                        i++;
                    }
                    else if (c == separator)
                    {
                        fields.Add(field.ToString().Trim());
                        field.Clear();
                        i++;
                    }
                    else if (c == '\r')
                    {
                        i++;
                    }
                    else if (c == '\n')
                    {
                        line++;
                        i++;
                        endOfRecord = true;
                    }
                    else
                    {
                        field.Append(c);
                        i++;
                    }
                }
                fields.Add(field.ToString().Trim());
                yield return (recordLine, fields.ToArray(), false, string.Empty);
            }
        }
    }
}
