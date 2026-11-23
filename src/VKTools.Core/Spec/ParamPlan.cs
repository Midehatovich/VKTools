using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;

namespace VKTools.Core.Spec
{
    /// <summary>Модули записи параметров спецификации (Р2). Порядок значений — порядок записи.</summary>
    public enum SpecModule
    {
        /// <summary>Родитель → общие вложенные: выбранные параметры (NestedParamSync v2.1, шаг 3, — частный случай).</summary>
        Nested = 0,

        /// <summary>ADSK_Группирование по категории, спринклеры, комплекты шкафов.</summary>
        Grouping = 1,

        /// <summary>Р4: параметр системы из «Сокращения для системы», этаж из уровня, изоляция от основы (NestedParamSync v2.1, шаги 1, 1б, 2).</summary>
        SystemFloor = 2,

        /// <summary>Р4: изоляция — Dнар трубы, наименование и параметры изделия (InsulationPipeOD v1.1.2).</summary>
        Insulation = 3,

        /// <summary>Р5: запас — ADSK_Количество = длина × k трубам и изоляции на трубах (PipeQtyReserve v1.0.2).</summary>
        PipeReserve = 4,

        /// <summary>Р5: ADSK_Наименование труб по правилу типа (Ду×S / Dнар×S), по умолчанию только проверка (VgpNameDuS v1.1).</summary>
        PipeNames = 5,

        /// <summary>0.15.0: марка, код изделия и завод-изготовитель по маске наименования (таблица «Марки и поставщики»).</summary>
        Vendor = 6,
    }

    public static class SpecModuleText
    {
        public static string Title(this SpecModule module) => module switch
        {
            SpecModule.Nested => "Перенос во вложенные",
            SpecModule.SystemFloor => "Система и этаж",
            SpecModule.Insulation => "Изоляция",
            SpecModule.PipeReserve => "Запас труб",
            SpecModule.PipeNames => "Имена труб",
            SpecModule.Vendor => "Марки и поставщики",
            _ => "Группирование",
        };
    }

    /// <summary>Одно изменение: элемент, параметр, было → станет. Только данные, без объектов Revit.</summary>
    public sealed class ParamChange
    {
        public ParamChange(SpecModule module, long elementId, ParamKey key, string oldValue, ParamValue value)
        {
            Module = module;
            ElementId = elementId;
            Key = key;
            Old = oldValue;
            Value = value;
        }

        public SpecModule Module { get; }

        public long ElementId { get; }

        public ParamKey Key { get; }

        /// <summary>Имя параметра для отчёта.</summary>
        public string Parameter => Key.Name;

        /// <summary>Было — как показывает Revit.</summary>
        public string Old { get; }

        /// <summary>Что записать.</summary>
        public ParamValue Value { get; }

        /// <summary>Станет — как показывает Revit.</summary>
        public string New => Value.Display;

        public string Category { get; set; } = string.Empty;

        public string Family { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        /// <summary>Откуда значение: «родитель 123 (Шкаф : ШПК-310)», «категория → 5», «комплект шкафа».</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>Элемент-источник (родитель) — выделяется вместе с элементом.</summary>
        public long? SourceId { get; set; }
    }

    /// <summary>
    /// Параметр, который можно переносить из родителя во вложенные: есть у родителя и хотя бы у одного вложенного
    /// (или выбран раньше). Счётчики — для окна выбора: сколько вложенных изменится и сколько с замечаниями.
    /// </summary>
    public sealed class ParamCandidate
    {
        public ParamCandidate(ParamKey key, string dataType)
        {
            Key = key;
            DataType = dataType;
        }

        public ParamKey Key { get; }

        public string Name => Key.Name;

        public string KindText => Key.KindText;

        /// <summary>Тип данных Revit: «Текст», «Длина», «Да/Нет»…</summary>
        public string DataType { get; set; }

        /// <summary>Вложенных, у которых параметр есть (пар родитель → вложенное).</summary>
        public int Nested { get; set; }

        /// <summary>Сколько вложенных получили бы новое значение, если параметр отмечен.</summary>
        public int WouldChange { get; set; }

        /// <summary>Вложенных, которым значение не перенести (тип, только чтение, занят…), без «у родителя пусто».</summary>
        public int Problems { get; set; }

        /// <summary>У родителя пусто — вложенные не трогаем.</summary>
        public int ParentEmpty { get; set; }

        public bool Selected { get; set; }
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
        private readonly Dictionary<(long, string), ParamChange> _byKey = new Dictionary<(long, string), ParamChange>();
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

        /// <summary>«Перенос во вложенные»: параметры для выбора в окне.</summary>
        public List<ParamCandidate> Candidates { get; } = new List<ParamCandidate>();

        /// <summary>«Группирование»: семейства элементов с ADSK_Группирование — подсказки для таблицы групп.</summary>
        public SortedSet<string> FamilyNames { get; } = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);

        /// <summary>«Группирование»: категории (имя BuiltInCategory → название в Revit) — для таблицы групп.</summary>
        public SortedDictionary<string, string> Categories { get; } = new SortedDictionary<string, string>(StringComparer.Ordinal);

        /// <summary>«Группирование»: семейства верхнего уровня с общими вложенными — для вкладки «Комплекты».</summary>
        public List<KitFamilyInfo> KitFamilies { get; } = new List<KitFamilyInfo>();

        /// <summary>«Группирование»: K типоразмеров комплектов проекта в модели (сохранённые и новые).</summary>
        public List<KitNumber> KitNumbers { get; } = new List<KitNumber>();

        /// <summary>«Группирование»: новые K — «Применить» сохранит их в модели той же операцией.</summary>
        public List<KitNumber> NewKitNumbers { get; } = new List<KitNumber>();

        /// <summary>«Система и этаж»: уровни модели (имя → элементов на нём) — для таблицы этажей.</summary>
        public SortedDictionary<string, int> Levels { get; } = new SortedDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);

        /// <summary>«Изоляция»: по типу — сколько штук, какая строка «Изделий» и какое имя получится (пример).</summary>
        public List<string> Preview { get; } = new List<string>();

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
            var key = (change.ElementId, change.Key.Id);
            if (_byKey.TryGetValue(key, out ParamChange? existing))
            {
                if (existing.New == change.New) return false;
                PlanIssue issue = AddIssue(change.Module, Severity.Error, change.Parameter,
                    $"конфликт правил: уже получает «{existing.New}» ({existing.Module.Title()}), второе значение «{change.New}» не пишется",
                    change.Family, change.ElementId);
                issue.Action = "Уберите параметр из одного из правил.";
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
