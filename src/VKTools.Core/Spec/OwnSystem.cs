using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Text;

namespace VKTools.Core.Spec
{
    /// <summary>
    /// «Своя система у вложенных» (0.12.2): вложенное семейство, имя которого подходит под маску, при переносе берёт
    /// параметры системы не у родителя, а по своему параметру-сокращению. Пример: смеситель вложен в умывальник, родитель
    /// в К1, а смесителю нужна В1 (его «СокращениеСистемы»). Первый параметр списка получает само сокращение, остальные —
    /// значение, которое в модели стоит у труб этой системы (LSR_Тип системы и т. п.).
    /// </summary>
    public sealed class OwnSystemRule
    {
        public OwnSystemRule(string mask, string source, IEnumerable<string> targets)
        {
            Mask = (mask ?? string.Empty).Trim();
            Source = (source ?? string.Empty).Trim();
            Targets = (targets ?? Enumerable.Empty<string>()).Select(t => (t ?? string.Empty).Trim()).Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Маска имени вложенного семейства: «*» — любые символы, «?» — один символ, без учёта регистра.</summary>
        public string Mask { get; }

        /// <summary>Параметр вложенного с сокращением системы (по имени: общий, параметр семейства или проекта).</summary>
        public string Source { get; }

        /// <summary>Какие переносимые параметры брать «по своей системе»; первый получает само сокращение.</summary>
        public IReadOnlyList<string> Targets { get; }

        public string TargetsText => string.Join(", ", Targets);

        public bool Matches(string familyName) => FamilyMask.Matches(Mask, familyName ?? string.Empty);

        /// <summary>Номер параметра в списке целей; -1 — параметр правилом не затронут.</summary>
        public int TargetIndex(string parameterName)
        {
            for (int i = 0; i < Targets.Count; i++)
            {
                if (string.Equals(Targets[i], parameterName, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }
    }

    public static class OwnSystemRules
    {
        public const string DefaultSource = "СокращениеСистемы";

        private static readonly string[] Header = { "Маска семейства", "Параметр-источник", "Параметры системы" };

        private static readonly string[] Comments =
        {
            "VK Tools: своя система у вложенных (настройка проекта). Первый параметр системы получает сокращение из параметра-источника,",
            "остальные — значение, которое стоит у труб этой системы. Параметры системы — через запятую.",
        };

        /// <summary>По умолчанию: смесители берут систему (В1, В1.1) из своего «СокращениеСистемы», а не от прибора (К1).</summary>
        public static List<OwnSystemRule> Defaults() => new List<OwnSystemRule>
        {
            new OwnSystemRule("*Смеситель*", DefaultSource, new[] { KnownParameters.LsrSystemName.Name, "LSR_Тип системы" }),
        };

        /// <summary>Первое правило, под маску которого подходит семейство; null — переносится как обычно.</summary>
        public static OwnSystemRule? Find(IEnumerable<OwnSystemRule> rules, string familyName) => rules.FirstOrDefault(r => r.Matches(familyName));

        public static string Format(IEnumerable<OwnSystemRule> rules) =>
            CsvWriter.Table(Comments, 1, Header, rules.Select(r => (IReadOnlyList<string>)new[] { r.Mask, r.Source, r.TargetsText }));

        public static string Signature(IEnumerable<OwnSystemRule> rules) =>
            string.Join("|", rules.Select(r => r.Mask + ";" + r.Source + ";" + r.TargetsText));

        public static List<string> SplitTargets(string? text) =>
            (text ?? string.Empty).Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).Where(t => t.Length > 0).ToList();

        /// <summary>Разбор настройки проекта; ошибки — в <paramref name="issues"/>, строка с ошибкой пропускается.</summary>
        public static List<OwnSystemRule> Parse(string text, List<ConfigIssue> issues)
        {
            const string file = "настройка проекта: своя система у вложенных";
            var rules = new List<OwnSystemRule>();
            CsvTable table = CsvTable.Parse(text);
            if (!ConfigLoader.Validate(table, new RuleFileSpec(file, 1, Header), issues)) return rules;
            foreach (CsvRow row in table.Rows)
            {
                string? problem = Problem(row.Get(Header[0]), row.Get(Header[1]), SplitTargets(row.Get(Header[2])));
                if (problem != null)
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, problem));
                    continue;
                }
                rules.Add(new OwnSystemRule(row.Get(Header[0]), row.Get(Header[1]), SplitTargets(row.Get(Header[2]))));
            }
            return rules;
        }

        /// <summary>Что не так со строкой правила; null — строка годится.</summary>
        public static string? Problem(string mask, string source, IReadOnlyCollection<string> targets)
        {
            if ((mask ?? string.Empty).Trim().Length == 0) return "пустая маска семейства";
            if ((source ?? string.Empty).Trim().Length == 0) return $"«{mask}»: не указан параметр-источник";
            if (targets.Count == 0) return $"«{mask}»: не указаны параметры системы";
            return null;
        }
    }
}
