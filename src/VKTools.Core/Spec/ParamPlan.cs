using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;

namespace VKTools.Core.Spec
{
    /// <summary>Модули записи параметров спецификации (Р2). Порядок значений — порядок записи.</summary>
    public enum SpecModule
    {
        /// <summary>Родитель → общие вложенные (NestedParamSync v2.1, шаг 3).</summary>
        Nested = 0,

        /// <summary>ADSK_Группирование по категории, спринклеры, комплекты шкафов.</summary>
        Grouping = 1,
    }

    public static class SpecModuleText
    {
        public static string Title(this SpecModule module) => module == SpecModule.Nested ? "Вложенные" : "Группирование";
    }

    /// <summary>Одно изменение: элемент, параметр (по GUID), было → станет. Только данные, без объектов Revit.</summary>
    public sealed class ParamChange
    {
        public ParamChange(SpecModule module, long elementId, string parameter, Guid guid, string oldValue, string newValue)
        {
            Module = module;
            ElementId = elementId;
            Parameter = parameter;
            Guid = guid;
            Old = oldValue;
            New = newValue;
        }

        public SpecModule Module { get; }

        public long ElementId { get; }

        public string Parameter { get; }

        public Guid Guid { get; }

        public string Old { get; }

        public string New { get; }

        public string Category { get; set; } = string.Empty;

        public string Family { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        /// <summary>Откуда значение: «родитель 123 (Шкаф : ШПК-310)», «категория → 5», «комплект шкафа».</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>Элемент-источник (родитель) — выделяется вместе с элементом.</summary>
        public long? SourceId { get; set; }
    }

    /// <summary>
    /// Замечание, сгруппированное по причине и семейству: одна строка вместо тысячи одинаковых
    /// («у вложенного нет параметра» у всех кранов одного семейства).
    /// </summary>
    public sealed class PlanIssue
    {
        public PlanIssue(SpecModule module, Severity severity, string parameter, string reason, string subject)
        {
            Module = module;
            Severity = severity;
            Parameter = parameter;
            Reason = reason;
            Subject = subject;
        }

        public SpecModule Module { get; }

        public Severity Severity { get; }

        public string Parameter { get; }

        public string Reason { get; }

        /// <summary>Семейство или «родитель → вложенное».</summary>
        public string Subject { get; }

        public string Action { get; set; } = string.Empty;

        /// <summary>Элементы для «Показать в модели» (могут повторяться — выделение их схлопнет).</summary>
        public List<long> Ids { get; } = new List<long>();

        /// <summary>Сколько элементов (или пар элемент–параметр) за строкой.</summary>
        public int Count { get; set; }
    }

    /// <summary>Что будет записано и что пропущено. Строится заново перед каждым пробным прогоном и записью.</summary>
    public sealed class ParamPlan
    {
        private readonly Dictionary<(long, Guid), ParamChange> _byKey = new Dictionary<(long, Guid), ParamChange>();
        private readonly Dictionary<(SpecModule, Severity, string, string, string), PlanIssue> _issues =
            new Dictionary<(SpecModule, Severity, string, string, string), PlanIssue>();
        private readonly List<PlanIssue> _issueOrder = new List<PlanIssue>();
        private readonly Dictionary<SpecModule, int> _unchanged = new Dictionary<SpecModule, int>();

        public ParamPlan(IEnumerable<SpecModule> modules)
        {
            Modules = modules.Distinct().OrderBy(m => m).ToList();
        }

        public IReadOnlyList<SpecModule> Modules { get; }

        public List<ParamChange> Changes { get; } = new List<ParamChange>();

        public IReadOnlyList<PlanIssue> Issues => _issueOrder;

        /// <summary>Откуда взяты правила — в шапку отчёта.</summary>
        public List<string> RuleSources { get; } = new List<string>();

        /// <summary>Строки сводки по модулю: «родителей с вложенными: 120» и т. п.</summary>
        public List<string> Notes { get; } = new List<string>();

        /// <summary>Правила отдела недоступны или с ошибками: запись запрещена (REQ-CFG-003).</summary>
        public string? WriteBlockedReason { get; set; }

        public int Unchanged(SpecModule module) => _unchanged.TryGetValue(module, out int n) ? n : 0;

        public void CountUnchanged(SpecModule module, int count = 1) => _unchanged[module] = Unchanged(module) + count;

        public int ChangeCount(SpecModule module) => Changes.Count(c => c.Module == module);

        public int IssueCount(SpecModule module, Severity severity) => _issueOrder.Where(i => i.Module == module && i.Severity == severity).Sum(i => i.Count);

        /// <summary>
        /// Добавить изменение. Тот же параметр того же элемента уже получает значение:
        /// то же — повтор не нужен; другое — конфликт правил, второе не пишется (замечание).
        /// </summary>
        public bool AddChange(ParamChange change)
        {
            var key = (change.ElementId, change.Guid);
            if (_byKey.TryGetValue(key, out ParamChange? existing))
            {
                if (existing.New == change.New) return false;
                PlanIssue issue = AddIssue(change.Module, Severity.Error, change.Parameter,
                    $"конфликт правил: уже получает «{existing.New}» ({existing.Module.Title()}), второе значение «{change.New}» не пишется",
                    change.Family, change.ElementId);
                issue.Action = "Уберите параметр из одного из модулей (nested_params.csv или ADSK_Группирование).";
                return false;
            }
            _byKey[key] = change;
            Changes.Add(change);
            return true;
        }

        /// <summary>Замечание: одна строка на (модуль, важность, параметр, причина, семейство), элементы копятся в ней.</summary>
        public PlanIssue AddIssue(SpecModule module, Severity severity, string parameter, string reason, string subject, long? elementId = null, int count = 1)
        {
            var key = (module, severity, parameter, reason, subject);
            if (!_issues.TryGetValue(key, out PlanIssue? issue))
            {
                issue = new PlanIssue(module, severity, parameter, reason, subject);
                _issues[key] = issue;
                _issueOrder.Add(issue);
            }
            issue.Count += count;
            if (elementId.HasValue) issue.Ids.Add(elementId.Value);
            return issue;
        }
    }
}
