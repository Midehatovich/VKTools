using System;

namespace VKTools.Core.Mep
{
    /// <summary>Разбор имени системы на проектное имя и автономер Revit.</summary>
    public sealed class SystemNameParts
    {
        internal SystemNameParts(string baseName, int? number, string? issue)
        {
            BaseName = baseName;
            Number = number;
            Issue = issue;
        }

        /// <summary>Проектное имя: «В1.2» для «В1.2 4»; для нестандартного имени — имя целиком.</summary>
        public string BaseName { get; }

        /// <summary>Автономер Revit; null — имя проектное (не копия).</summary>
        public int? Number { get; }

        /// <summary>Имя похоже на копию, но не по правилу Revit (двойной пробел, ведущий ноль…).</summary>
        public string? Issue { get; }

        public bool IsCopy => Number != null;

        public bool IsProjectName => Number == null && Issue == null;
    }

    /// <summary>
    /// Логика имён из PipingSystemMerge v1.2. Копия системы, которую Revit создаёт при копировании
    /// разводки, называется «&lt;проектное имя&gt; + один пробел + целое без ведущего нуля».
    /// Проектное имя сравнивается целиком: В1 ≠ В1.1 ≠ В1.2 ≠ В10, К1 ≠ К1н.
    /// </summary>
    public static class SystemNames
    {
        public static SystemNameParts Split(string? name)
        {
            if (string.IsNullOrEmpty(name)) return new SystemNameParts(name ?? string.Empty, null, "пустое имя");
            string n = name!;
            if (n != n.Trim()) return new SystemNameParts(n, null, "пробелы в начале или конце имени");

            int j = n.Length;
            while (j > 0 && IsDigit(n[j - 1])) j--;
            if (j == n.Length || j == 0) return new SystemNameParts(n, null, null);

            char separator = n[j - 1];
            if (!char.IsWhiteSpace(separator)) return new SystemNameParts(n, null, null);

            string head = n.Substring(0, j - 1);
            string tail = n.Substring(j);
            if (separator != ' ')
                return new SystemNameParts(n, null, $"перед номером не обычный пробел (U+{(int)separator:X4})");
            if (head.Length == 0 || head != head.TrimEnd())
                return new SystemNameParts(n, null, "несколько пробелов перед номером");
            if (tail[0] == '0')
                return new SystemNameParts(n, null, "номер с ведущим нулём");
            if (!int.TryParse(tail, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int number))
                return new SystemNameParts(n, null, "слишком большой номер");
            return new SystemNameParts(head, number, null);
        }

        /// <summary>
        /// Имя без любых хвостов «пробел + цифры», многократно. Только для поиска почти-совпадений
        /// («В1.2 3 1», «В1  04»), не для объединения.
        /// </summary>
        public static string RootBase(string? name)
        {
            string s = name ?? string.Empty;
            while (true)
            {
                string t = s.TrimEnd();
                int j = t.Length;
                while (j > 0 && IsDigit(t[j - 1])) j--;
                if (j > 0 && j < t.Length && char.IsWhiteSpace(t[j - 1]))
                {
                    s = t.Substring(0, j);
                    continue;
                }
                return t;
            }
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        internal static bool SameIgnoringCaseAndSpaces(string a, string b) =>
            string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
