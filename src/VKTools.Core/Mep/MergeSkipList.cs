using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VKTools.Core.Mep
{
    public enum SkipDecision
    {
        /// <summary>Решения нет: копия по умолчанию (не объединять, только если Revit на ней зависал).</summary>
        None,

        /// <summary>Пользователь отметил «Не объединять».</summary>
        Skip,

        /// <summary>Пользователь снял «Не объединять» с копии, на которой Revit зависал, — сознательно.</summary>
        Allow,
    }

    /// <summary>
    /// Копии, которые не объединять (D-025): по модели, основной и копии — по именам, а не по Id, чтобы отметка пережила
    /// пересоздание системы. Revit, зависший на MEPSystem.Add, плагин прервать не может (КрМа3, «К1», 2026-10-10),
    /// поэтому такая копия по умолчанию «не объединять», пока пользователь сам не снимет отметку.
    /// </summary>
    public sealed class MergeSkipList
    {
        private const string Header = "VKTools.MergeSkip 1";

        private readonly Dictionary<string, SkipDecision> _items = new Dictionary<string, SkipDecision>(StringComparer.Ordinal);

        public int Count => _items.Count;

        public static bool IsSkipped(SkipDecision stored, bool hung) =>
            stored == SkipDecision.Skip || (stored == SkipDecision.None && hung);

        /// <summary>Что записать, когда пользователь поставил или снял «Не объединять».</summary>
        public static SkipDecision DecisionFor(bool skip, bool hung) =>
            skip ? (hung ? SkipDecision.None : SkipDecision.Skip) : (hung ? SkipDecision.Allow : SkipDecision.None);

        public SkipDecision Get(string document, string target, string copy) =>
            _items.TryGetValue(Key(document, target, copy), out SkipDecision d) ? d : SkipDecision.None;

        public void Set(string document, string target, string copy, SkipDecision decision)
        {
            string key = Key(document, target, copy);
            if (decision == SkipDecision.None) _items.Remove(key);
            else _items[key] = decision;
        }

        /// <summary>Разбор файла; чужие и испорченные строки пропускаются (список — подстраховка, а не данные проекта).</summary>
        public static MergeSkipList Parse(string? text)
        {
            var list = new MergeSkipList();
            if (string.IsNullOrEmpty(text)) return list;
            string[] lines = text!.Replace("\r", string.Empty).Split('\n');
            if (lines[0] != Header) return list;
            foreach (string line in lines.Skip(1))
            {
                string[] parts = line.Split('\t');
                if (parts.Length != 4 || !Enum.TryParse(parts[3], out SkipDecision d) || d == SkipDecision.None) continue;
                if (!Enum.IsDefined(typeof(SkipDecision), d)) continue;
                list._items[string.Join("\t", parts[0], parts[1], parts[2])] = d;
            }
            return list;
        }

        public string Format()
        {
            var text = new StringBuilder();
            text.AppendLine(Header);
            foreach (KeyValuePair<string, SkipDecision> item in _items.OrderBy(i => i.Key, StringComparer.Ordinal))
            {
                text.AppendLine(item.Key + "\t" + item.Value.ToString());
            }
            return text.ToString();
        }

        private static string Key(string document, string target, string copy) => string.Join("\t", Clean(document), Clean(target), Clean(copy));

        private static string Clean(string text) => (text ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
