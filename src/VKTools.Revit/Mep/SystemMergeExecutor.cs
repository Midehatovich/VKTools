using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using VKTools.Core.Logging;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Mep
{
    internal enum MergeMode
    {
        /// <summary>Выполнить и откатить: модель не меняется, видно, пройдёт ли запись.</summary>
        Trial,

        /// <summary>Записать одной операцией в журнале отмены.</summary>
        Apply,
    }

    internal enum OutcomeState
    {
        Merged,
        TrialPassed,
        Failed,
        RolledBack,
        NotRun,
    }

    internal sealed class CandidateOutcome
    {
        public CandidateOutcome(PlannedCandidate candidate)
        {
            Candidate = candidate;
        }

        public PlannedCandidate Candidate { get; }

        public OutcomeState State { get; set; } = OutcomeState.NotRun;

        public string Detail { get; set; } = string.Empty;

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case OutcomeState.Merged: return "Объединена";
                    case OutcomeState.TrialPassed: return "Пробно: объединяется (откачено)";
                    case OutcomeState.Failed: return "Ошибка, откат";
                    case OutcomeState.RolledBack: return "Откачена вместе со всей операцией";
                    default: return "Не выполнялась";
                }
            }
        }
    }

    internal sealed class MergeRunResult
    {
        public MergeRunResult(MergeMode mode)
        {
            Mode = mode;
        }

        public MergeMode Mode { get; }

        public List<CandidateOutcome> Outcomes { get; } = new List<CandidateOutcome>();

        /// <summary>Итоговая проверка по каждой основной системе; строки с «!!!» критичны.</summary>
        public List<string> PostCheck { get; } = new List<string>();

        public List<string> Warnings { get; } = new List<string>();

        public List<string> Errors { get; } = new List<string>();

        public bool Committed { get; set; }

        public string Status { get; set; } = string.Empty;

        /// <summary>Пробный прогон прошёл без ошибок: можно применять.</summary>
        public bool Passed => Errors.Count == 0 && Outcomes.Count > 0 && Outcomes.All(o => o.State == OutcomeState.TrialPassed || o.State == OutcomeState.Merged);
    }

    /// <summary>
    /// Перенос копий в основные системы (механизм PipingSystemMerge v1.2):
    /// удалить объект системы-копии → добавить коннекторы её терминалов в основную (MEPSystem.Add),
    /// при отказе — повтор после Regenerate; проверка внутри транзакции; итоговая проверка.
    /// Всё в одной TransactionGroup: в журнале отмены одна запись; любая ошибка — откат всего.
    /// </summary>
    internal static class SystemMergeExecutor
    {
        public const string OperationName = "VK Tools: объединение систем";

        public static MergeRunResult Run(Document doc, MergePlan plan, IReadOnlyCollection<PlannedCandidate> selected, MergeMode mode, ILog log)
        {
            var result = new MergeRunResult(mode);
            foreach (PlannedCandidate c in selected) result.Outcomes.Add(new CandidateOutcome(c));
            if (selected.Count == 0)
            {
                result.Status = "Ничего не отмечено.";
                return result;
            }

            PipingSnapshot before = plan.Snapshot;
            var tracked = new HashSet<long>();
            foreach (PlannedGroup group in selected.Select(c => c.Group).Distinct()) tracked.UnionWith(before.MembersOf(group.TargetId));
            foreach (PlannedCandidate c in selected) tracked.UnionWith(c.ElementIds);
            var geometryBefore = new Dictionary<long, GeometryKey>();
            foreach (long id in tracked)
            {
                if (before.Elements.TryGetValue(id, out Element? e)) geometryBefore[id] = GeometryKey.Of(e);
            }
            var systemsBefore = new HashSet<long>(before.Systems.Keys);
            var mergedIds = new HashSet<long>();
            bool fatal = false;

            using (var operation = new TransactionGroup(doc, OperationName))
            {
                operation.Start();
                try
                {
                    foreach (CandidateOutcome outcome in result.Outcomes)
                    {
                        PlannedCandidate c = outcome.Candidate;
                        string name = c.Record.Name;
                        var failures = new FailureCollector();
                        Transaction? transaction = null;
                        try
                        {
                            PipingSystem target = c.Group.Target ?? throw new InvalidOperationException("нет основной системы");
                            transaction = FailureCollector.Start(doc, $"Объединение: {name} → {c.Group.BaseName}", failures);

                            ICollection<ElementId> deleted = doc.Delete(c.Record.Id.ToElementId());
                            var deletedIds = new HashSet<long>(deleted.Select(x => x.ToLong()));
                            deletedIds.Remove(c.Record.Id);
                            var lost = deletedIds.Where(tracked.Contains).OrderBy(x => x).Take(10).ToList();
                            if (lost.Count > 0)
                            {
                                throw new InvalidOperationException("Revit удаляет вместе с системой элементы: " + string.Join(", ", lost));
                            }
                            if (deletedIds.Count > 0)
                            {
                                result.Warnings.Add($"{name}: вместе с системой удалены служебные объекты {string.Join(", ", deletedIds.OrderBy(x => x).Take(10))}");
                            }

                            string path = "без Regenerate";
                            try
                            {
                                AddConnectors(doc, target, c);
                            }
                            catch (Exception first) when (first is Autodesk.Revit.Exceptions.ApplicationException || first is InvalidOperationException)
                            {
                                doc.Regenerate();
                                List<string> busy = ReassignedTerminals(doc, c);
                                if (busy.Count > 0)
                                {
                                    throw new InvalidOperationException("после удаления Revit сам назначил терминалам новую систему: "
                                        + string.Join("; ", busy.Take(5)) + $" (первая ошибка: {first.Message})");
                                }
                                AddConnectors(doc, target, c);
                                path = "после Regenerate";
                            }
                            doc.Regenerate();

                            List<string> bad = VerifyOne(doc, c, systemsBefore, geometryBefore);
                            if (bad.Count > 0) throw new InvalidOperationException(string.Join("; ", bad));

                            TransactionStatus status = transaction.Commit();
                            if (status != TransactionStatus.Committed)
                            {
                                throw new InvalidOperationException($"Revit отклонил фиксацию ({status}): "
                                    + (failures.Errors.Count > 0 ? string.Join("; ", failures.Errors) : "—"));
                            }
                            mergedIds.Add(c.Record.Id);
                            outcome.State = mode == MergeMode.Apply ? OutcomeState.Merged : OutcomeState.TrialPassed;
                            outcome.Detail = $"{c.ElementIds.Count} эл., {c.TerminalIds.Count} терм.; путь: {path}";
                        }
                        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException || ex is ArgumentException)
                        {
                            if (transaction != null && transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
                            outcome.State = OutcomeState.Failed;
                            outcome.Detail = ex.Message;
                            result.Errors.Add($"{name}: {ex.Message}");
                            fatal = true;
                        }
                        finally
                        {
                            transaction?.Dispose();
                        }
                        foreach (string w in failures.Warnings) result.Warnings.Add($"{name}: предупреждение Revit: {w}");
                        foreach (string e in failures.Errors) result.Errors.Add($"{name}: ошибка Revit: {e}");
                        if (fatal) break; // всё или ничего: дальше не идём, ниже — откат всей операции
                    }

                    if (!fatal)
                    {
                        PipingSnapshot after = PipingSnapshot.Take(doc);
                        foreach (PlannedGroup g in selected.Select(c => c.Group).Distinct())
                        {
                            PostCheckGroup(before, after, g, mergedIds, result.PostCheck);
                        }
                        PostCheckModel(doc, before, after, tracked, geometryBefore, result.PostCheck);
                        List<string> critical = result.PostCheck.Where(line => line.Contains("!!!")).ToList();
                        if (critical.Count > 0)
                        {
                            fatal = true;
                            result.Errors.AddRange(critical);
                        }
                    }

                    if (mode == MergeMode.Trial || fatal)
                    {
                        operation.RollBack();
                        foreach (CandidateOutcome o in result.Outcomes)
                        {
                            if (fatal && (o.State == OutcomeState.TrialPassed || o.State == OutcomeState.Merged)) o.State = OutcomeState.RolledBack;
                        }
                        result.Status = mode == MergeMode.Trial
                            ? (fatal ? "Пробный прогон: есть ошибки, всё откачено. Модель не изменена." : "Пробный прогон прошёл, всё откачено. Модель не изменена.")
                            : "Ошибка — откат всей операции. Модель не изменена.";
                    }
                    else
                    {
                        operation.Assimilate();
                        result.Committed = true;
                        result.Status = $"Записано одной операцией «{OperationName}»: Ctrl+Z отменяет её целиком.";
                    }
                }
                catch (Exception ex)
                {
                    if (operation.HasStarted() && !operation.HasEnded()) operation.RollBack();
                    log.Error("Объединение систем прервано, всё откачено.", ex);
                    result.Errors.Add("прервано: " + ex.Message);
                    result.Status = "Ошибка — откат всей операции. Модель не изменена.";
                    foreach (CandidateOutcome o in result.Outcomes)
                    {
                        if (o.State == OutcomeState.TrialPassed || o.State == OutcomeState.Merged) o.State = OutcomeState.RolledBack;
                    }
                }
            }
            return result;
        }

        /// <summary>MEPSystem.Add только для коннекторов, которых ещё нет в основной: занятый коннектор ломает весь набор.</summary>
        private static void AddConnectors(Document doc, PipingSystem target, PlannedCandidate c)
        {
            long targetId = target.Id.ToLong();
            var set = new ConnectorSet();
            foreach ((long ownerId, int connectorId) in c.Moves)
            {
                Element owner = doc.GetElement(ownerId.ToElementId())
                    ?? throw new InvalidOperationException($"терминал {ownerId} исчез после удаления системы");
                Connector connector = Connectors.Find(owner, connectorId)
                    ?? throw new InvalidOperationException($"у элемента {ownerId} не найден коннектор {connectorId}");
                if (Connectors.SystemId(connector) == targetId) continue;
                set.Insert(connector);
            }
            if (!set.IsEmpty) target.Add(set);
        }

        private static List<string> ReassignedTerminals(Document doc, PlannedCandidate c)
        {
            var busy = new List<string>();
            foreach ((long ownerId, int connectorId) in c.Moves)
            {
                Connector? connector = Connectors.Find(doc.GetElement(ownerId.ToElementId()), connectorId);
                long systemId = Connectors.SystemId(connector);
                if (systemId > 0 && systemId != c.Group.TargetId)
                {
                    busy.Add($"{ownerId} → «{doc.GetElement(systemId.ToElementId())?.Name}»");
                }
            }
            return busy;
        }

        /// <summary>Проверка внутри транзакции до фиксации. Непустой список — откат.</summary>
        private static List<string> VerifyOne(Document doc, PlannedCandidate c, HashSet<long> systemsBefore, Dictionary<long, GeometryKey> geometryBefore)
        {
            var bad = new List<string>();
            PlannedGroup g = c.Group;
            var target = doc.GetElement(g.TargetId.ToElementId()) as PipingSystem;
            if (target == null || !target.IsValidObject) return new List<string> { "основная система исчезла" };
            if (target.GetTypeId().ToLong() != g.TargetTypeId) bad.Add("изменился тип основной системы");
            long baseEquipment = target.BaseEquipment?.Id.ToLong() ?? -1;
            if (baseEquipment != g.TargetBaseEquipmentId) bad.Add("изменилось базовое оборудование основной системы");

            foreach ((long ownerId, int connectorId) in c.Moves)
            {
                Connector? connector = Connectors.Find(doc.GetElement(ownerId.ToElementId()), connectorId);
                if (Connectors.SystemId(connector) != g.TargetId) bad.Add($"терминал {ownerId} не в основной системе");
            }
            int notInTarget = 0;
            foreach (long id in c.ElementIds)
            {
                Element? e = doc.GetElement(id.ToElementId());
                if (e == null)
                {
                    bad.Add($"элемент {id} удалён");
                    continue;
                }
                if (!Connectors.SystemsOf(e).Contains(g.TargetId)) notInTarget++;
                if (geometryBefore.TryGetValue(id, out GeometryKey g0) && !g0.SameAs(GeometryKey.Of(e))) bad.Add($"изменилось положение элемента {id}");
            }
            if (notInTarget > 0) bad.Add($"{notInTarget} элементов сети не перешли в основную систему");

            var created = new FilteredElementCollector(doc).OfClass(typeof(PipingSystem))
                .Select(s => s.Id.ToLong()).Where(id => !systemsBefore.Contains(id)).ToList();
            if (created.Count > 0)
            {
                bad.Add("Revit создал новые системы: " + string.Join(", ", created.Take(10).Select(id => doc.GetElement(id.ToElementId())?.Name ?? id.ToString(CultureInfo.InvariantCulture))));
            }
            return bad;
        }

        /// <summary>Итоговая проверка по основной системе группы. Строки с «!!!» ведут к откату.</summary>
        private static void PostCheckGroup(PipingSnapshot before, PipingSnapshot after, PlannedGroup g, HashSet<long> merged, List<string> output)
        {
            string head = $"«{g.BaseName}»: ";
            if (!after.Systems.TryGetValue(g.TargetId, out PipingSystem? target))
            {
                output.Add(head + "!!! основная система не существует после операции");
                return;
            }
            List<long> still = g.Candidates.Select(c => c.Record.Id).Where(id => merged.Contains(id) && after.Systems.ContainsKey(id)).ToList();
            long baseEquipment = target.BaseEquipment?.Id.ToLong() ?? -1;
            output.Add(head + string.Join("; ", new[]
            {
                $"элементов в основной было {before.MembersOf(g.TargetId).Count}, стало {after.MembersOf(g.TargetId).Count}",
                still.Count == 0 ? "объединённые системы исчезли" : "!!! не исчезли: " + string.Join(", ", still),
                target.GetTypeId().ToLong() == g.TargetTypeId ? "тип сохранён" : "!!! изменился тип основной системы",
                baseEquipment == g.TargetBaseEquipmentId ? "базовое оборудование без изменений" : "!!! изменилось базовое оборудование",
            }));
        }

        /// <summary>Итоговая проверка по всем затронутым элементам.</summary>
        private static void PostCheckModel(Document doc, PipingSnapshot before, PipingSnapshot after, HashSet<long> tracked,
            Dictionary<long, GeometryKey> geometryBefore, List<string> output)
        {
            var orphans = tracked.Where(id => after.ElementSystems.TryGetValue(id, out HashSet<long>? now) && now.Count == 0
                && before.ElementSystems.TryGetValue(id, out HashSet<long>? was) && was.Count > 0).ToList();
            output.Add(orphans.Count == 0 ? "Элементов, потерявших систему, нет" : $"!!! Потеряли систему {orphans.Count} эл. (например, {string.Join(", ", orphans.Take(10))})");

            var created = after.Systems.Keys.Where(id => !before.Systems.ContainsKey(id)).ToList();
            output.Add(created.Count == 0 ? "Новых систем нет" : "!!! Revit создал новые системы: " + string.Join(", ", created.Select(id => after.Systems[id].Name)));

            var gone = tracked.Where(id => before.Elements.ContainsKey(id) && !after.Elements.ContainsKey(id) && doc.GetElement(id.ToElementId()) == null).ToList();
            output.Add(gone.Count == 0 ? "Элементы на месте" : "!!! Удалены элементы: " + string.Join(", ", gone.Take(10)));

            var moved = geometryBefore.Where(p => after.Elements.TryGetValue(p.Key, out Element? e) && !p.Value.SameAs(GeometryKey.Of(e))).Select(p => p.Key).ToList();
            output.Add(moved.Count == 0 ? $"Геометрия без изменений ({geometryBefore.Count} эл.)" : "!!! Сдвинуты элементы: " + string.Join(", ", moved.Take(10)));

            output.Add($"Трубопроводных систем в модели: было {before.Systems.Count}, стало {after.Systems.Count}");
        }
    }
}
