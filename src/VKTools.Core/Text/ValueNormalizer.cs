using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VKTools.Core.Text
{
    /// <summary>
    /// Значения, которые в спецификации выглядят одинаково, но для Revit разные:
    /// латиница вместо кириллицы («B1» и «В1»), регистр («02 Этаж» и «02 этаж»), лишние пробелы.
    /// Спецификация разносит их в разные строки или отсекает фильтром.
    /// </summary>
    public static class ValueNormalizer
    {
        // Латинские буквы, которые на экране не отличить от кириллических.
        private static readonly Dictionary<char, char> LatinToCyrillic = new Dictionary<char, char>
        {
            ['A'] = 'А', ['B'] = 'В', ['C'] = 'С', ['E'] = 'Е', ['H'] = 'Н', ['K'] = 'К', ['M'] = 'М',
            ['O'] = 'О', ['P'] = 'Р', ['T'] = 'Т', ['X'] = 'Х', ['Y'] = 'У',
            ['a'] = 'а', ['c'] = 'с', ['e'] = 'е', ['o'] = 'о', ['p'] = 'р', ['x'] = 'х', ['y'] = 'у',
        };

        /// <summary>Ключ сравнения: без лишних пробелов, в нижнем регистре, латинские двойники заменены кириллицей.</summary>
        public static string ComparisonKey(string? value)
        {
            if (value == null) return string.Empty;
            var sb = new StringBuilder(value.Length);
            bool pendingSpace = false;
            foreach (char raw in value.Trim())
            {
                if (char.IsWhiteSpace(raw))
                {
                    pendingSpace = true;
                    continue;
                }
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                char c = LatinToCyrillic.TryGetValue(raw, out char cyr) ? cyr : raw;
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>В одном значении есть и латинские, и кириллические буквы.</summary>
        public static bool HasMixedScripts(string? value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            bool latin = false, cyrillic = false;
            foreach (char c in value!)
            {
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) latin = true;
                else if ((c >= 'А' && c <= 'я') || c == 'Ё' || c == 'ё') cyrillic = true;
                if (latin && cyrillic) return true;
            }
            return false;
        }

        /// <summary>Значение отличается от своей нормальной формы только пробелами по краям или двойными пробелами.</summary>
        public static bool HasExtraWhitespace(string? value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            if (value!.Trim().Length != value.Length) return true;
            for (int i = 1; i < value.Length; i++)
            {
                if (char.IsWhiteSpace(value[i]) && char.IsWhiteSpace(value[i - 1])) return true;
            }
            return value.Any(c => char.IsWhiteSpace(c) && c != ' ');
        }

        /// <summary>
        /// Значение для отчёта так, чтобы разница была видна глазами: пробелы как «·», табуляция как «→»,
        /// латинские буквы перечислены в скобках («B1 (латиница: B)»).
        /// </summary>
        public static string MakeVisible(string? value)
        {
            if (value == null) return string.Empty;
            var sb = new StringBuilder(value.Length + 16);
            var latin = new List<char>();
            foreach (char c in value)
            {
                if (c == ' ') sb.Append('·');
                else if (c == '\t') sb.Append('→');
                else sb.Append(c);
                if (((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) && !latin.Contains(c)) latin.Add(c);
            }
            if (latin.Count > 0) sb.Append(" (латиница: ").Append(string.Join(", ", latin)).Append(')');
            return sb.ToString();
        }

        /// <summary>
        /// Группы разных написаний одного значения. Возвращает только группы, где написаний больше одного.
        /// </summary>
        public static IReadOnlyList<LookalikeGroup> FindLookalikes(IEnumerable<string> values)
        {
            if (values == null) throw new ArgumentNullException(nameof(values));
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string v in values)
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                counts.TryGetValue(v, out int n);
                counts[v] = n + 1;
            }

            return counts
                .GroupBy(kv => ComparisonKey(kv.Key), StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => new LookalikeGroup(
                    g.Key,
                    g.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                     .Select(kv => new LookalikeVariant(kv.Key, kv.Value)).ToList()))
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToList();
        }
    }

    public sealed class LookalikeVariant
    {
        public LookalikeVariant(string value, int count)
        {
            Value = value;
            Count = count;
        }

        public string Value { get; }

        public int Count { get; }
    }

    public sealed class LookalikeGroup
    {
        public LookalikeGroup(string key, IReadOnlyList<LookalikeVariant> variants)
        {
            Key = key;
            Variants = variants;
        }

        public string Key { get; }

        /// <summary>Написания по убыванию числа элементов: первое — самое частое.</summary>
        public IReadOnlyList<LookalikeVariant> Variants { get; }
    }
}
