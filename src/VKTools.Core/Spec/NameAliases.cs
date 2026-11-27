using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VKTools.Core.Spec
{
    /// <summary>
    /// «Одно наименование» (0.17.4): правила «вариант → основное» для наименования, выбранные инженером в отчёте проверки.
    /// Хранятся в модели (ключ <see cref="Key"/>), применяются ко всем записям наименования шагами плагина
    /// («Система и этаж», «Имена труб», «Изоляция»…), чтобы прежний вариант не возвращался при следующем прогоне.
    /// Формат: строка на правило, «вариант» TAB «основное» (в наименованиях бывают «;»).
    /// </summary>
    public static class NameAliases
    {
        public const string Key = "names.aliases";

        public static Dictionary<string, string> Parse(string? text)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return result;
            foreach (string raw in text!.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int tab = line.IndexOf('\t');
                if (tab <= 0 || tab == line.Length - 1) continue;
                string from = line.Substring(0, tab);
                string to = line.Substring(tab + 1);
                if (!string.Equals(from, to, StringComparison.Ordinal)) result[from] = to;
            }
            return result;
        }

        public static string? Format(IReadOnlyDictionary<string, string> aliases)
        {
            if (aliases.Count == 0) return null;
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in aliases.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Добавить «варианты → основное». Основное само перестаёт быть вариантом (выбор перевернули), прежние правила
        /// на варианты перенаправляются на новое основное — цепочек не бывает.
        /// </summary>
        public static void Add(Dictionary<string, string> aliases, IEnumerable<string> variants, string main)
        {
            if (string.IsNullOrEmpty(main)) throw new ArgumentException("Пустое основное наименование.", nameof(main));
            aliases.Remove(main);
            var set = new HashSet<string>(variants.Where(v => !string.IsNullOrEmpty(v) && !string.Equals(v, main, StringComparison.Ordinal)), StringComparer.Ordinal);
            foreach (string key in aliases.Keys.ToList())
            {
                if (set.Contains(aliases[key]) || string.Equals(aliases[key], main, StringComparison.Ordinal)) aliases[key] = main;
            }
            foreach (string v in set) aliases[v] = main;
        }

        /// <summary>Наименование после правил; нет правила — как есть.</summary>
        public static string Map(IReadOnlyDictionary<string, string> aliases, string value) =>
            value != null && aliases.TryGetValue(value, out string? to) ? to : value!;
    }
}
