using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using VKTools.Core.Logging;
using VKTools.Core.Mep;
using VKTools.Core.Progress;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Mep
{
    internal enum CandidateState
    {
        /// <summary>Прошла все проверки, объединяется по отметке.</summary>
        Ready,

        /// <summary>Проверки пройдены, но имя неоднозначное (REQ-MEP-005): только с отметкой пользователя.</summary>
        NeedsConfirmation,

        /// <summary>Не объединяется.</summary>
        Rejected,
    }

    /// <summary>Копия системы и всё, что нужно для её переноса в основную.</summary>
    internal sealed class PlannedCandidate
    {
        public PlannedCandidate(PlannedGroup group, SystemRecord record)
        {
            Group = group;
            Record = record;
        }

        public PlannedGroup Group { get; }

        public SystemRecord Record { get; }

        public CandidateState State { get; set; }

        /// <summary>Почему не объединяется (или почему нужна отметка).</summary>
        public List<string> Reasons { get; } = new List<string>();

        /// <summary>Что важно знать, но не мешает.</summary>
        public List<string> Notes { get; } = new List<string>();

        /// <summary>Коннекторы терминалов, которые переходят в основную систему: (Id элемента, Id коннектора).</summary>
        public List<(long OwnerId, int ConnectorId)> Moves { get; } = new List<(long, int)>();

        /// <summary>Все элементы системы: терминалы, трубы, фитинги.</summary>
        public HashSet<long> ElementIds { get; } = new HashSet<long>();

        public List<long> TerminalIds { get; } = new List<long>();

        public string Levels { get; set; } = string.Empty;

        /// <summary>Ключ для отметок в окне: переживает повторный анализ, пока системы те же.</summary>
        public string Key => MergeKeys.Of(Group.TargetId, Record.Id);

        public string Explanation => string.Join("; ", Reasons.Concat(Notes));
    }

    internal static class MergeKeys
    {
        public static string Of(long targetId, long candidateId) =>
            targetId.ToString(CultureInfo.InvariantCulture) + ":" + candidateId.ToString(CultureInfo.InvariantCulture);
    }

    internal sealed class PlannedGroup
    {
        public PlannedGroup(MergeGroup core, PipingSystem? target)
        {
            Core = core;
            Target = target;
            TargetId = target?.Id.ToLong() ?? -1;
        }

        public MergeGroup Core { get; }

        public PipingSystem? Target { get; }

        public long TargetId { get; }

        public long TargetTypeId => Core.Target?.TypeId ?? -1;

        public long TargetBaseEquipmentId { get; set; } = -1;

        public string BaseName => Core.BaseName;

        public List<PlannedCandidate> Candidates { get; } = new List<PlannedCandidate>();

        /// <summary>Стыки сети основной с сетями других систем по трубам и фитингам (не через приборы), 0.3.13.</summary>
        public List<NetworkJunction> Junctions { get; } = new List<NetworkJunction>();
    }

    /// <summary>Соединение элемента основной (A) с элементом другой системы (B).</summary>
    internal sealed class NetworkJunction
    {
        public NetworkJunction(long a, long b, string otherSystem, string describeA, string describeB)
        {
            A = a;
            B = b;
            OtherSystem = otherSystem;
            DescribeA = describeA;
            DescribeB = describeB;
        }

        public long A { get; }

        public long B { get; }

        public string OtherSystem { get; }

        public string DescribeA { get; }

        public string DescribeB { get; }
    }

    internal sealed class MergePlan
    {
        public MergePlan(Document document, PipingModel model, Dictionary<long, SystemRecord> records, bool partial)
        {
            Document = document;
            Model = model;
            Records = records;
            IsPartial = partial;
        }

        public Document Document { get; }

        public PipingModel Model { get; }

        /// <summary>Проанализированы только отмеченные копии (перед записью), а не весь проект.</summary>
        public bool IsPartial { get; }

        public Dictionary<long, SystemRecord> Records { get; }

        public List<PlannedGroup> Groups { get; } = new List<PlannedGroup>();

        public IEnumerable<PlannedCandidate> Candidates => Groups.SelectMany(g => g.Candidates);

        public int Count(CandidateState state) => Candidates.Count(c => c.State == state);
    }

    /// <summary>
    /// Анализ без изменений модели: группы «основная ← копии» и проверки каждой копии по модели
    /// (раздел 4 PipingSystemMerge v1.2). Параметр-свидетель не используется: в моделях отдела
    /// LSR_Имя системы заполняется от типа и ничего не подтверждает; неоднозначные копии
    /// объединяются только по явной отметке пользователя (docs/ARCHITECTURE.md, D-012).
    /// </summary>
    internal static class SystemMergePlanner
    {
        public const string StageAnalysis = "анализ";

        /// <summary>
        /// Стыки основной с сетями других систем: элемент только основной соединён с элементом только другой системы.
        /// Приборы и оборудование с коннекторами разных систем (умывальник: К1 и В1) — норма, они сюда не попадают.
        /// Revit держит одну систему на связную сеть, поэтому при объединении он может перенести элементы через такой стык
        /// (КрМа3: «К4.2н» ушла в «К4.1 1»). У «К1» на КрМа3 такой стык с «К1.1», и только добавление в «К1» зависало.
        /// </summary>
        private static void FindJunctions(Document doc, PipingModel model, PlannedGroup group)
        {
            HashSet<long> members = model.MembersOf(group.TargetId).All;
            var copies = new HashSet<long>(group.Candidates.Select(c => c.Record.Id));
            var seen = new HashSet<(long, long)>();
            foreach (long a in members.OrderBy(x => x))
            {
                HashSet<long> own = model.SystemsOf(a);
                if (own.Count != 1 || !own.Contains(group.TargetId)) continue;
                Element? ea = model.Element(a);
                if (ea == null) continue;
                foreach (long b in Connectors.NeighbourIds(ea))
                {
                    if (members.Contains(b) || !seen.Add((a, b))) continue;
                    HashSet<long> other = model.SystemsOf(b);
                    if (other.Count != 1) continue;
                    long system = other.First();
                    if (system == group.TargetId || copies.Contains(system)) continue;
                    string otherName = model.Systems.TryGetValue(system, out PipingSystem? s) ? s.Name : system.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    group.Junctions.Add(new NetworkJunction(a, b, otherName, Describe(ea), Describe(model.Element(b))));
                    if (group.Junctions.Count >= 20) return;
                }
            }
        }

        private static string Describe(Element? e)
        {
            if (e == null) return "?";
            string name = e is FamilyInstance fi && fi.Symbol != null ? $"{fi.Symbol.FamilyName}: {fi.Symbol.Name}" : e.Name;
            return $"{e.Category?.Name ?? "?"} {e.Id.ToLong()} «{name}»";
        }

        /// <param name="onlyKeys">Перед записью — только отмеченные копии: остальные не анализируются и в план не входят.</param>
        public static MergePlan Build(Document doc, IOperationProgress progress, OperationTimings timings, ICollection<string>? onlyKeys = null)
        {
            using (timings.Measure(StageAnalysis))
            {
                progress.Report("Чтение систем трубопроводов…");
                PipingModel model = PipingModel.Take(doc);
                Dictionary<long, SystemRecord> records = model.Records();
                var plan = new MergePlan(doc, model, records, onlyKeys != null);
                var worksharing = new WorksharingGuard(doc);

                // Проектное имя каждой системы: для проверки связей с чужими группами
                var baseOf = new Dictionary<long, string>();
                foreach (SystemRecord r in records.Values)
                {
                    SystemNameParts parts = SystemNames.Split(r.Name);
                    baseOf[r.Id] = parts.Issue == null ? parts.BaseName : r.Name;
                }

                IReadOnlyList<MergeGroup> groups = MergeGrouping.BuildGroups(records.Values.ToList());
                int total = onlyKeys?.Count ?? groups.Sum(g => g.Candidates.Count);
                int done = 0;
                foreach (MergeGroup core in groups)
                {
                    PipingSystem? target = core.Target != null && model.Systems.TryGetValue(core.Target.Id, out PipingSystem? t) ? t : null;
                    var group = new PlannedGroup(core, target);

                    string? targetBlock = null;
                    if (target != null)
                    {
                        group.TargetBaseEquipmentId = target.BaseEquipment?.Id.ToLong() ?? -1;
                        targetBlock = worksharing.Block(target, checkCentral: true);
                    }

                    foreach (Candidate candidate in core.Candidates)
                    {
                        if (onlyKeys != null && !onlyKeys.Contains(MergeKeys.Of(group.TargetId, candidate.System.Id))) continue;
                        progress.Report($"Анализ: «{candidate.System.Name}» → «{core.BaseName}»", done++, total);
                        progress.ThrowIfCancelled();

                        var planned = new PlannedCandidate(group, candidate.System);
                        group.Candidates.Add(planned);
                        CollectElements(doc, model, planned);

                        if (target == null || candidate.Status == CandidateStatus.Rejected)
                        {
                            planned.State = CandidateState.Rejected;
                            planned.Reasons.Add(target == null ? core.Problem ?? candidate.Reason ?? "нет основной системы" : candidate.Reason ?? "отклонена по имени");
                            if (planned.TerminalIds.Count == 0 && planned.ElementIds.Count > 0)
                            {
                                planned.Notes.Add("к тому же нет терминалов: систему из одних труб не перенести и после исправления имени, нужно соединить трубы");
                            }
                            continue;
                        }
                        if (targetBlock != null) planned.Reasons.Add("основная система: " + targetBlock);

                        AnalyzeInModel(doc, plan, group, planned, baseOf, worksharing);

                        if (planned.Reasons.Count > 0) planned.State = CandidateState.Rejected;
                        else if (candidate.Status == CandidateStatus.NeedsConfirmation)
                        {
                            planned.State = CandidateState.NeedsConfirmation;
                            planned.Reasons.Add(candidate.Reason ?? "нужно подтверждение");
                        }
                        else planned.State = CandidateState.Ready;
                    }
                    if (group.Candidates.Count > 0)
                    {
                        if (target != null) FindJunctions(doc, model, group);
                        plan.Groups.Add(group);
                    }
                }
                return plan;
            }
        }

        private static void AnalyzeInModel(Document doc, MergePlan plan, PlannedGroup group, PlannedCandidate planned,
            Dictionary<long, string> baseOf, WorksharingGuard worksharing)
        {
            PipingModel model = plan.Model;
            long sid = planned.Record.Id;
            if (!model.Systems.TryGetValue(sid, out PipingSystem? system))
            {
                planned.Reasons.Add("система не найдена в модели");
                return;
            }
            SystemMembers members = model.MembersOf(sid);

            // Базовое оборудование: у системы оно одно, переносить чужое нельзя
            if (members.BaseEquipmentId > 0)
            {
                long beId = members.BaseEquipmentId;
                planned.Reasons.Add(group.TargetBaseEquipmentId > 0 && beId != group.TargetBaseEquipmentId
                    ? $"своё базовое оборудование (Id {beId}), у основной — Id {group.TargetBaseEquipmentId}"
                    : $"есть базовое оборудование (Id {beId}): плагин его не переносит");
            }
            if (members.Terminals.Count == 0)
            {
                planned.Reasons.Add("нет терминалов (приборов, оборудования): систему из одних труб через API не перенести, нужно соединить трубы физически");
            }

            // Коннекторы терминалов: классификация и что переносим
            string targetClass = group.Core.Target!.Classification;
            foreach (long terminalId in members.Terminals)
            {
                Element? owner = model.Element(terminalId);
                if (owner == null) continue;
                List<Connector> connectors = Connectors.Piping(owner);
                List<Connector> mine = connectors.Where(c => Connectors.SystemId(c) == sid).ToList();
                if (mine.Count == 0)
                {
                    planned.Notes.Add($"у элемента {terminalId} нет трубных коннекторов этой системы");
                    continue;
                }
                foreach (Connector c in mine)
                {
                    PipeSystemType pst;
                    try
                    {
                        pst = c.PipeSystemType;
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        planned.Moves.Add((terminalId, c.Id));
                        continue;
                    }
                    if (!IsNeutral(pst) && pst.ToString() != targetClass)
                    {
                        planned.Reasons.Add($"коннектор {c.Id} элемента {terminalId} классифицирован как {pst}, а основная — {targetClass}");
                        continue;
                    }
                    planned.Moves.Add((terminalId, c.Id));
                }
                var others = new HashSet<long>(connectors.Select(Connectors.SystemId));
                others.Remove(sid);
                others.Remove(-1);
                if (others.Count > 0)
                {
                    planned.Notes.Add($"{owner.Category?.Name} {terminalId}: другие коннекторы в "
                        + string.Join(", ", others.Select(k => plan.Records.TryGetValue(k, out SystemRecord? r) ? r.Name : k.ToString(CultureInfo.InvariantCulture)))
                        + " — не трогаются");
                }
            }

            // Физические связи с системами других проектных имён того же типа
            var foreign = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (long id in planned.ElementIds)
            {
                Element? e = model.Element(id);
                if (e == null) continue;
                var linked = new HashSet<long>(model.SystemsOf(id));
                foreach (long neighbour in Connectors.NeighbourIds(e)) linked.UnionWith(model.SystemsOf(neighbour));
                foreach (long other in linked)
                {
                    if (other == sid || other == group.TargetId || !plan.Records.TryGetValue(other, out SystemRecord? r)) continue;
                    if (r.TypeId == group.TargetTypeId && baseOf.TryGetValue(other, out string? b) && b != group.BaseName)
                    {
                        foreign[r.Name] = foreign.TryGetValue(r.Name, out int n) ? n + 1 : 1;
                    }
                }
            }
            if (foreign.Count > 0)
            {
                planned.Reasons.Add("физически связана с системами другой группы: "
                    + string.Join(", ", foreign.OrderByDescending(p => p.Value).Take(5).Select(p => $"«{p.Key}» ×{p.Value}")));
            }

            // Группы, варианты конструкции, совместная работа. Состояние в хранилище — только у терминалов и самой системы:
            // по трубам его проверит Revit при фиксации (ошибка → откат всего)
            var terminalSet = new HashSet<long>(members.Terminals);
            foreach (long id in planned.ElementIds)
            {
                Element? e = model.Element(id);
                if (e == null) continue;
                if (e.GroupId != null && e.GroupId.ToLong() > 0)
                {
                    planned.Reasons.Add($"элементы в группах модели (например, Id {id})");
                    break;
                }
                DesignOption? option = e.DesignOption;
                if (option != null && !option.IsPrimary)
                {
                    planned.Reasons.Add($"элементы во вторичном варианте конструкции (Id {id})");
                    break;
                }
                string? block = worksharing.Block(e, checkCentral: terminalSet.Contains(id));
                if (block != null)
                {
                    planned.Reasons.Add($"элемент {id}: {block}");
                    break;
                }
            }
            string? systemBlock = worksharing.Block(system, checkCentral: true);
            if (systemBlock != null) planned.Reasons.Add("сама система: " + systemBlock);

            if (planned.Reasons.Count == 0 && planned.Moves.Count == 0) planned.Reasons.Add("нет коннекторов для переноса");

            List<string> distinct = planned.Reasons.Distinct().ToList();
            planned.Reasons.Clear();
            planned.Reasons.AddRange(distinct);

            try
            {
                int networks = system.GetPhysicalNetworksNumber();
                if (networks > 1) planned.Notes.Add($"физических сетей: {networks}");
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Число сетей — только для справки
            }
        }

        /// <summary>Состав копии: ElementIds, TerminalIds, Levels.</summary>
        private static void CollectElements(Document doc, PipingModel model, PlannedCandidate planned)
        {
            SystemMembers members = model.MembersOf(planned.Record.Id);
            planned.ElementIds.UnionWith(members.All);
            planned.TerminalIds.AddRange(members.Terminals);
            IEnumerable<long> forLevels = members.Terminals.Count > 0 ? members.Terminals : (IEnumerable<long>)members.All;
            planned.Levels = LevelNames(doc, forLevels.Select(model.Element).Where(e => e != null)!);
        }

        private static bool IsNeutral(PipeSystemType type) =>
            type == PipeSystemType.Global || type == PipeSystemType.Fitting || type == PipeSystemType.UndefinedSystemType;

        private static string LevelNames(Document doc, IEnumerable<Element> elements, int limit = 3)
        {
            var levelIds = new HashSet<long>();
            foreach (Element e in elements)
            {
                ElementId levelId = e.LevelId;
                if ((levelId == null || levelId.ToLong() < 1) && e is MEPCurve curve) levelId = curve.ReferenceLevel?.Id ?? ElementId.InvalidElementId;
                if (levelId != null && levelId.ToLong() > 0) levelIds.Add(levelId.ToLong());
            }
            List<string> names = levelIds.Select(id => doc.GetElement(id.ToElementId())?.Name).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).Distinct().ToList();
            names.Sort(StringComparer.CurrentCulture);
            if (names.Count > limit) return string.Join(", ", names.Take(limit)) + $" и ещё {names.Count - limit}";
            return names.Count == 0 ? "—" : string.Join(", ", names);
        }
    }

    /// <summary>Совместная работа: закрытый рабочий набор, занят другим, устарел относительно хранилища (REQ-SAFE-004).</summary>
    internal sealed class WorksharingGuard
    {
        private readonly Document _doc;
        private readonly Dictionary<int, bool> _openWorksets = new Dictionary<int, bool>();

        public WorksharingGuard(Document doc)
        {
            _doc = doc;
        }

        /// <param name="checkCentral">Проверять ли «изменён в хранилище» (кэш Revit, но для тысяч труб лишнее).</param>
        public string? Block(Element element, bool checkCentral)
        {
            if (!_doc.IsWorkshared) return null;
            try
            {
                WorksetId wid = element.WorksetId;
                if (!_openWorksets.TryGetValue(wid.IntegerValue, out bool open))
                {
                    open = _doc.GetWorksetTable().GetWorkset(wid).IsOpen;
                    _openWorksets[wid.IntegerValue] = open;
                }
                if (!open) return "рабочий набор закрыт";
                if (WorksharingUtils.GetCheckoutStatus(_doc, element.Id) == CheckoutStatus.OwnedByOtherUser) return "занят другим пользователем";
                if (!checkCentral) return null;
                ModelUpdatesStatus status = WorksharingUtils.GetModelUpdatesStatus(_doc, element.Id);
                if (status == ModelUpdatesStatus.UpdatedInCentral || status == ModelUpdatesStatus.DeletedInCentral)
                {
                    return "изменён в хранилище — синхронизируйтесь";
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Состояние неизвестно — не блокируем: Revit сам не даст записать занятое, это поймает транзакция
            }
            return null;
        }
    }
}
