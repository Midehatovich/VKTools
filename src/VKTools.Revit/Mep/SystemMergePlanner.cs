using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using VKTools.Core.Mep;
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
    }

    internal sealed class MergePlan
    {
        public MergePlan(Document document, PipingSnapshot snapshot, Dictionary<long, SystemRecord> records)
        {
            Document = document;
            Snapshot = snapshot;
            Records = records;
        }

        public Document Document { get; }

        public PipingSnapshot Snapshot { get; }

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
        public static MergePlan Build(Document doc)
        {
            PipingSnapshot snapshot = PipingSnapshot.Take(doc);
            Dictionary<long, SystemRecord> records = snapshot.Records(doc);
            var plan = new MergePlan(doc, snapshot, records);
            var worksharing = new WorksharingGuard(doc);

            // Проектное имя каждой системы: для проверки связей с чужими группами
            var baseOf = new Dictionary<long, string>();
            foreach (SystemRecord r in records.Values)
            {
                SystemNameParts parts = SystemNames.Split(r.Name);
                baseOf[r.Id] = parts.Issue == null ? parts.BaseName : r.Name;
            }

            foreach (MergeGroup core in MergeGrouping.BuildGroups(records.Values.ToList()))
            {
                PipingSystem? target = core.Target != null && snapshot.Systems.TryGetValue(core.Target.Id, out PipingSystem? t) ? t : null;
                var group = new PlannedGroup(core, target);
                plan.Groups.Add(group);

                string? targetBlock = null;
                if (target != null)
                {
                    FamilyInstance? baseEquipment = target.BaseEquipment;
                    group.TargetBaseEquipmentId = baseEquipment?.Id.ToLong() ?? -1;
                    targetBlock = worksharing.Block(target);
                }

                foreach (Candidate candidate in core.Candidates)
                {
                    var planned = new PlannedCandidate(group, candidate.System);
                    group.Candidates.Add(planned);
                    if (target == null || candidate.Status == CandidateStatus.Rejected)
                    {
                        // Не объединяется по имени — но элементы нужны, чтобы найти систему в модели
                        CollectElements(doc, plan, planned);
                    }
                    if (target == null)
                    {
                        planned.State = CandidateState.Rejected;
                        planned.Reasons.Add(core.Problem ?? candidate.Reason ?? "нет основной системы");
                        continue;
                    }
                    if (candidate.Status == CandidateStatus.Rejected)
                    {
                        planned.State = CandidateState.Rejected;
                        planned.Reasons.Add(candidate.Reason ?? "отклонена по имени");
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
            }
            return plan;
        }

        private static void AnalyzeInModel(Document doc, MergePlan plan, PlannedGroup group, PlannedCandidate planned,
            Dictionary<long, string> baseOf, WorksharingGuard worksharing)
        {
            PipingSnapshot snapshot = plan.Snapshot;
            long sid = planned.Record.Id;
            if (!snapshot.Systems.TryGetValue(sid, out PipingSystem? system))
            {
                planned.Reasons.Add("система не найдена в модели");
                return;
            }

            (List<Element> terminals, List<Element> elements, int extra) = CollectElements(doc, plan, planned);
            FamilyInstance? baseEquipment = system.BaseEquipment;
            if (extra > 0) planned.Notes.Add($"по коннекторам ещё {extra} эл. вне состава системы");

            // Базовое оборудование: у системы оно одно, переносить чужое нельзя
            if (baseEquipment != null)
            {
                long beId = baseEquipment.Id.ToLong();
                planned.Reasons.Add(group.TargetBaseEquipmentId > 0 && beId != group.TargetBaseEquipmentId
                    ? $"своё базовое оборудование (Id {beId}), у основной — Id {group.TargetBaseEquipmentId}"
                    : $"есть базовое оборудование (Id {beId}): плагин его не переносит");
            }
            if (terminals.Count == 0)
            {
                planned.Reasons.Add("нет терминалов (приборов, оборудования): систему из одних труб через API не перенести, нужно соединить трубы физически");
            }

            // Коннекторы терминалов: классификация и что переносим
            string targetClass = group.Core.Target!.Classification;
            foreach (Element owner in terminals)
            {
                List<Connector> connectors = Connectors.Piping(owner);
                List<Connector> mine = connectors.Where(c => Connectors.SystemId(c) == sid).ToList();
                if (mine.Count == 0)
                {
                    planned.Notes.Add($"у элемента {owner.Id.ToLong()} нет трубных коннекторов этой системы");
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
                        planned.Moves.Add((owner.Id.ToLong(), c.Id));
                        continue;
                    }
                    if (!IsNeutral(pst) && pst.ToString() != targetClass)
                    {
                        planned.Reasons.Add($"коннектор {c.Id} элемента {owner.Id.ToLong()} классифицирован как {pst}, а основная — {targetClass}");
                        continue;
                    }
                    planned.Moves.Add((owner.Id.ToLong(), c.Id));
                }
                var others = new HashSet<long>(connectors.Select(Connectors.SystemId));
                others.Remove(sid);
                others.Remove(-1);
                if (others.Count > 0)
                {
                    planned.Notes.Add($"{owner.Category?.Name} {owner.Id.ToLong()}: другие коннекторы в "
                        + string.Join(", ", others.Select(k => plan.Records.TryGetValue(k, out SystemRecord? r) ? r.Name : k.ToString(CultureInfo.InvariantCulture)))
                        + " — не трогаются");
                }
            }

            // Физические связи с системами других проектных имён того же типа
            var foreign = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Element e in elements)
            {
                var linked = new HashSet<long>(snapshot.SystemsOf(e.Id.ToLong()));
                foreach (long neighbour in Connectors.NeighbourIds(e)) linked.UnionWith(snapshot.SystemsOf(neighbour));
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

            // Группы, варианты конструкции, совместная работа
            foreach (Element e in elements)
            {
                long id = e.Id.ToLong();
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
                string? block = worksharing.Block(e);
                if (block != null)
                {
                    planned.Reasons.Add($"элемент {id}: {block}");
                    break;
                }
            }
            string? systemBlock = worksharing.Block(system);
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

        /// <summary>
        /// Состав системы: терминалы (MEPSystem.Elements), сеть (PipingNetwork) и всё, что связано с ней коннекторами.
        /// Заполняет ElementIds, TerminalIds и Levels. extra — элементы по коннекторам вне Elements и PipingNetwork.
        /// </summary>
        private static (List<Element> Terminals, List<Element> Elements, int Extra) CollectElements(Document doc, MergePlan plan, PlannedCandidate planned)
        {
            PipingSnapshot snapshot = plan.Snapshot;
            long sid = planned.Record.Id;
            if (!snapshot.Systems.TryGetValue(sid, out PipingSystem? system)) return (new List<Element>(), new List<Element>(), 0);

            List<Element> terminals = ToList(system.Elements);
            List<Element> network = ToList(system.PipingNetwork);
            HashSet<long> members = snapshot.MembersOf(sid);
            var terminalIds = new HashSet<long>(terminals.Select(e => e.Id.ToLong()));
            var networkIds = new HashSet<long>(network.Select(e => e.Id.ToLong()));
            planned.ElementIds.UnionWith(members);
            planned.ElementIds.UnionWith(terminalIds);
            planned.ElementIds.UnionWith(networkIds);
            planned.TerminalIds.AddRange(terminalIds.OrderBy(x => x));

            var extra = new HashSet<long>(members);
            extra.ExceptWith(networkIds);
            extra.ExceptWith(terminalIds);
            FamilyInstance? baseEquipment = system.BaseEquipment;
            if (baseEquipment != null) extra.Remove(baseEquipment.Id.ToLong());

            var elements = new List<Element>();
            foreach (long id in planned.ElementIds)
            {
                Element? e = snapshot.Elements.TryGetValue(id, out Element? known) ? known : doc.GetElement(id.ToElementId());
                if (e != null) elements.Add(e);
            }
            planned.Levels = LevelNames(doc, terminals.Count > 0 ? terminals : elements);
            return (terminals, elements, extra.Count);
        }

        private static bool IsNeutral(PipeSystemType type) =>
            type == PipeSystemType.Global || type == PipeSystemType.Fitting || type == PipeSystemType.UndefinedSystemType;

        private static List<Element> ToList(ElementSet? set)
        {
            var result = new List<Element>();
            if (set == null) return result;
            foreach (Element e in set)
            {
                if (e != null) result.Add(e);
            }
            return result;
        }

        private static string LevelNames(Document doc, IEnumerable<Element> elements, int limit = 3)
        {
            var names = new List<string>();
            foreach (Element e in elements)
            {
                ElementId levelId = e.LevelId;
                if ((levelId == null || levelId.ToLong() < 1) && e is MEPCurve curve) levelId = curve.ReferenceLevel?.Id ?? ElementId.InvalidElementId;
                string? name = levelId != null && levelId.ToLong() > 0 ? doc.GetElement(levelId)?.Name : null;
                if (!string.IsNullOrEmpty(name) && !names.Contains(name!)) names.Add(name!);
            }
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

        public string? Block(Element element)
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
