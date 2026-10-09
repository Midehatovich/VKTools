using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using VKTools.Core.Checks;
using VKTools.Core.Logging;
using VKTools.Core.Progress;
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

        /// <summary>Обновления других надстроек, отключённые до перезапуска Revit (в этом прогоне или раньше в сеансе), D-017.</summary>
        public List<string> DisabledUpdaters { get; } = new List<string>();

        /// <summary>Строки отчёта с элементами («Показать в модели»): участки сети, оставшиеся без системы после фиксации.</summary>
        public List<CheckFinding> ElementFindings { get; } = new List<CheckFinding>();

        public bool Committed { get; set; }

        public string Status { get; set; } = string.Empty;

        public bool Cancelled { get; set; }

        /// <summary>Время этапов одной строкой (анализ, удаление, Regenerate, фиксация…).</summary>
        public string Timings { get; set; } = string.Empty;

        public static string DisabledUpdatersText(IEnumerable<string> names) =>
            $"Отключено до перезапуска Revit: обновление {string.Join(", ", names)}. Оно упало при фиксации объединения, "
            + "VK Tools отключил его (как кнопка в окне Revit), чтобы запись прошла. Пока Revit не перезапущен, эта надстройка "
            + "не обновляет свои данные ни на этих, ни на других элементах; отключение не отменяется откатом пробного прогона.";

        /// <summary>Пробный прогон прошёл без ошибок: можно применять.</summary>
        public bool Passed => Errors.Count == 0 && Outcomes.Count > 0 && Outcomes.All(o => o.State == OutcomeState.TrialPassed || o.State == OutcomeState.Merged);
    }

    /// <summary>
    /// Перенос копий в основные системы (механизм PipingSystemMerge v1.2):
    /// удалить объекты систем-копий → добавить коннекторы их терминалов в основную (MEPSystem.Add),
    /// при отказе — повтор после Regenerate; проверка внутри транзакции; итоговая проверка.
    /// Одна транзакция на основную систему (не на копию): Revit пересчитывает большую систему
    /// и запускает апдейтеры других надстроек один раз на группу, а не на каждую копию.
    /// Всё в одной TransactionGroup: в журнале отмены одна запись; ошибка или отмена — откат всего.
    /// </summary>
    internal static class SystemMergeExecutor
    {
        public const string OperationName = "VK Tools: объединение систем";

        /// <summary>
        /// Надстройки, чьё упавшее при фиксации обновление плагин отключает до перезапуска Revit (D-017).
        /// Citrus [BIM] «MEPUpdater» срывал фиксацию на КрМа3 (2026-10-09); отключать его так решил пользователь.
        /// Список расширять только решением пользователя.
        /// </summary>
        private static readonly HashSet<string> DisableOnFailureApplications = new HashSet<string>(StringComparer.Ordinal) { "Citrus [BIM]" };

        /// <summary>Что уже отключено в этом сеансе Revit — напоминать при каждом прогоне.</summary>
        private static readonly List<string> SessionDisabledUpdaters = new List<string>();

        public static MergeRunResult Run(Document doc, MergePlan plan, IReadOnlyCollection<PlannedCandidate> selected, MergeMode mode,
            IOperationProgress progress, OperationTimings timings, ILog log)
        {
            var result = new MergeRunResult(mode);
            foreach (PlannedCandidate c in selected) result.Outcomes.Add(new CandidateOutcome(c));
            if (selected.Count == 0)
            {
                result.Status = "Ничего не отмечено.";
                return result;
            }

            PipingModel model = plan.Model;
            List<IGrouping<PlannedGroup, CandidateOutcome>> groups = result.Outcomes.GroupBy(o => o.Candidate.Group).ToList();

            // Состояние до записи: системы и положение всех затронутых элементов (основные + копии)
            var tracked = new HashSet<long>();
            var trackedSystems = new Dictionary<long, HashSet<long>>();
            var geometryBefore = new Dictionary<long, GeometryKey>();
            var membersBefore = new Dictionary<long, int>();
            using (timings.Measure("подготовка"))
            {
                foreach (IGrouping<PlannedGroup, CandidateOutcome> g in groups)
                {
                    SystemMembers targetMembers = model.MembersOf(g.Key.TargetId);
                    membersBefore[g.Key.TargetId] = targetMembers.All.Count;
                    tracked.UnionWith(targetMembers.All);
                }
                foreach (PlannedCandidate c in selected) tracked.UnionWith(c.ElementIds);
                int i = 0;
                foreach (long id in tracked)
                {
                    if (i++ % 500 == 0)
                    {
                        progress.Report($"Подготовка: запоминаю положение и системы {tracked.Count} элементов", i, tracked.Count);
                        progress.ThrowIfCancelled();
                    }
                    trackedSystems[id] = new HashSet<long>(model.SystemsOf(id));
                    Element? e = model.Element(id);
                    if (e != null) geometryBefore[id] = GeometryKey.Of(e);
                }
            }
            var systemsBefore = new HashSet<long>(model.Systems.Keys);
            IReadOnlyList<UpdaterInfo> updaters = RegisteredUpdaters(doc);
            var mergedIds = new HashSet<long>();
            bool fatal = false;
            bool cancelled = false;

            using (var operation = new TransactionGroup(doc, OperationName))
            {
                operation.Start();
                try
                {
                    int gi = 0;
                    foreach (IGrouping<PlannedGroup, CandidateOutcome> g in groups)
                    {
                        PlannedGroup group = g.Key;
                        List<PlannedCandidate> candidates = g.Select(o => o.Candidate).ToList();
                        string label = $"«{group.BaseName}» ← {string.Join(", ", candidates.Take(4).Select(c => $"«{c.Record.Name}»"))}{(candidates.Count > 4 ? $" и ещё {candidates.Count - 4}" : string.Empty)}";
                        progress.Report($"Объединение {gi + 1} из {groups.Count}: {label}", gi, groups.Count);
                        log.Info($"  группа {gi + 1} из {groups.Count}: {label}; элементов в основной {membersBefore[group.TargetId]}, "
                            + $"в копиях {candidates.Sum(c => c.ElementIds.Count)}, коннекторов терминалов к переносу {candidates.Sum(c => c.Moves.Count)}");
                        gi++;

                        var failures = new FailureCollector(updaters, DisableOnFailureApplications);
                        Transaction? transaction = null;
                        try
                        {
                            progress.ThrowIfCancelled();
                            PipingSystem target = group.Target ?? throw new InvalidOperationException("нет основной системы");
                            transaction = FailureCollector.Start(doc, $"Объединение: {label}", failures);

                            using (timings.Measure("удаление систем-копий"))
                            {
                                foreach (PlannedCandidate c in candidates)
                                {
                                    ICollection<ElementId> deleted = doc.Delete(c.Record.Id.ToElementId());
                                    var deletedIds = new HashSet<long>(deleted.Select(x => x.ToLong()));
                                    deletedIds.Remove(c.Record.Id);
                                    var lost = deletedIds.Where(tracked.Contains).OrderBy(x => x).Take(10).ToList();
                                    if (lost.Count > 0)
                                    {
                                        throw new InvalidOperationException($"«{c.Record.Name}»: Revit удаляет вместе с системой элементы: " + string.Join(", ", lost));
                                    }
                                    if (deletedIds.Count > 0)
                                    {
                                        result.Warnings.Add($"{c.Record.Name}: вместе с системой удалены служебные объекты {string.Join(", ", deletedIds.OrderBy(x => x).Take(10))}");
                                    }
                                }
                            }
                            progress.ThrowIfCancelled();

                            string path = "без Regenerate";
                            List<(long OwnerId, int ConnectorId)> moves = candidates.SelectMany(c => c.Moves).ToList();
                            try
                            {
                                AddByCopy(doc, target, group, candidates, gi, groups.Count, progress, timings, log);
                            }
                            catch (Exception first) when (first is Autodesk.Revit.Exceptions.ApplicationException || first is InvalidOperationException)
                            {
                                log.Info($"    MEPSystem.Add отказал: {first.Message.Replace(Environment.NewLine, " ")}; повтор после Regenerate");
                                using (timings.Measure("Regenerate")) doc.Regenerate();
                                List<string> busy = ReassignedTerminals(doc, group, moves);
                                if (busy.Count > 0)
                                {
                                    throw new InvalidOperationException("после удаления Revit сам назначил терминалам новую систему: "
                                        + string.Join("; ", busy.Take(5)) + $" (первая ошибка: {first.Message})");
                                }
                                AddByCopy(doc, target, group, candidates, gi, groups.Count, progress, timings, log);
                                path = "после Regenerate";
                            }
                            progress.Report($"Объединение {gi} из {groups.Count}: пересчёт модели (Regenerate)", gi - 1, groups.Count);
                            using (timings.Measure("Regenerate")) doc.Regenerate();
                            progress.ThrowIfCancelled();

                            progress.Report($"Объединение {gi} из {groups.Count}: проверка результата", gi - 1, groups.Count);
                            List<string> bad;
                            using (timings.Measure("проверка внутри транзакции")) bad = VerifyGroup(doc, group, candidates, moves, geometryBefore);
                            if (bad.Count > 0) throw new InvalidOperationException(string.Join("; ", bad));

                            progress.Report($"Объединение {gi} из {groups.Count}: фиксация (здесь же работают апдейтеры других надстроек)", gi - 1, groups.Count);
                            TransactionStatus status;
                            using (timings.Measure("фиксация транзакции")) status = transaction.Commit();
                            foreach (string note in failures.Notes) log.Warn("    " + note);
                            foreach (string name in failures.DisabledUpdaters)
                            {
                                if (!SessionDisabledUpdaters.Contains(name)) SessionDisabledUpdaters.Add(name);
                            }
                            if (status != TransactionStatus.Committed)
                            {
                                throw new InvalidOperationException($"Revit отклонил фиксацию ({status}): "
                                    + (failures.Errors.Count > 0 ? string.Join("; ", failures.Errors) : "—") + ThirdPartyHint(doc, failures.Errors));
                            }

                            // Систему сети (трубам и фитингам) Revit может окончательно назначать только при фиксации — проверяем после неё.
                            // Плохо → исключение ниже, всё откатывает TransactionGroup (D-014).
                            progress.Report($"Объединение {gi} из {groups.Count}: проверка сети после фиксации", gi - 1, groups.Count);
                            List<string> network;
                            using (timings.Measure("проверка сети после фиксации")) network = VerifyNetworkAfterCommit(doc, group, candidates, model.MembersOf(group.TargetId).All, systemsBefore, tracked, result.ElementFindings, log);
                            if (network.Count > 0) throw new InvalidOperationException(string.Join("; ", network));
                            foreach (CandidateOutcome o in g)
                            {
                                mergedIds.Add(o.Candidate.Record.Id);
                                o.State = mode == MergeMode.Apply ? OutcomeState.Merged : OutcomeState.TrialPassed;
                                o.Detail = $"{o.Candidate.ElementIds.Count} эл., {o.Candidate.TerminalIds.Count} терм.; путь: {path}";
                            }
                        }
                        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException
                            || ex is ArgumentException || ex is OperationCanceledException)
                        {
                            if (transaction != null && transaction.HasStarted() && !transaction.HasEnded())
                            {
                                progress.Report("Откат изменений…");
                                using (timings.Measure("откат транзакции")) transaction.RollBack();
                            }
                            fatal = true;
                            if (ex is OperationCanceledException || progress.IsCancellationRequested)
                            {
                                cancelled = true;
                            }
                            else
                            {
                                foreach (CandidateOutcome o in g)
                                {
                                    o.State = OutcomeState.Failed;
                                    o.Detail = ex.Message;
                                }
                                result.Errors.Add($"«{group.BaseName}»: {ex.Message}");
                            }
                        }
                        finally
                        {
                            transaction?.Dispose();
                        }
                        foreach (string w in failures.Warnings) result.Warnings.Add($"«{group.BaseName}»: предупреждение Revit: {w}");
                        if (!cancelled)
                        {
                            foreach (string e in failures.Errors) result.Errors.Add($"«{group.BaseName}»: ошибка Revit: {e}");
                        }
                        if (fatal) break; // всё или ничего: дальше не идём, ниже — откат всей операции
                    }

                    if (!fatal)
                    {
                        using (timings.Measure("итоговая проверка"))
                        {
                            PostCheck(doc, groups.Select(g => g.Key).ToList(), mergedIds, tracked, trackedSystems, geometryBefore, membersBefore,
                                systemsBefore, progress, result.PostCheck);
                        }
                        List<string> critical = result.PostCheck.Where(line => line.Contains("!!!")).ToList();
                        if (critical.Count > 0)
                        {
                            fatal = true;
                            result.Errors.AddRange(critical);
                        }
                    }

                    if (mode == MergeMode.Trial || fatal)
                    {
                        progress.Report("Откат всей операции…");
                        using (timings.Measure("откат операции")) operation.RollBack();
                        MarkRolledBack(result, fatal);
                        result.Status = cancelled
                            ? "Отменено: всё откачено, модель не изменена."
                            : mode == MergeMode.Trial
                                ? (fatal ? "Пробный прогон: есть ошибки, всё откачено. Модель не изменена." : "Пробный прогон прошёл, всё откачено. Модель не изменена.")
                                : "Ошибка — откат всей операции. Модель не изменена.";
                    }
                    else
                    {
                        progress.Report("Запись в журнал отмены…");
                        using (timings.Measure("запись (Assimilate)")) operation.Assimilate();
                        result.Committed = true;
                        result.Status = $"Записано одной операцией «{OperationName}»: Ctrl+Z отменяет её целиком.";
                    }
                }
                catch (Exception ex)
                {
                    if (operation.HasStarted() && !operation.HasEnded()) operation.RollBack();
                    bool wasCancel = ex is OperationCanceledException || progress.IsCancellationRequested;
                    if (!wasCancel) log.Error("Объединение систем прервано, всё откачено.", ex);
                    if (!wasCancel) result.Errors.Add("прервано: " + ex.Message);
                    result.Status = wasCancel ? "Отменено: всё откачено, модель не изменена." : "Ошибка — откат всей операции. Модель не изменена.";
                    cancelled = wasCancel;
                    MarkRolledBack(result, true);
                }
            }
            result.Cancelled = cancelled;
            result.DisabledUpdaters.AddRange(SessionDisabledUpdaters);
            return result;
        }

        private static IReadOnlyList<UpdaterInfo> RegisteredUpdaters(Document doc)
        {
            try
            {
                return UpdaterRegistry.GetRegisteredUpdaterInfos(doc).ToList();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return Array.Empty<UpdaterInfo>(); // без списка ни один апдейтер не отключаем
            }
        }

        private static void MarkRolledBack(MergeRunResult result, bool fatal)
        {
            if (!fatal) return;
            foreach (CandidateOutcome o in result.Outcomes)
            {
                if (o.State == OutcomeState.TrialPassed || o.State == OutcomeState.Merged) o.State = OutcomeState.RolledBack;
            }
        }

        /// <summary>
        /// MEPSystem.Add по одному коннектору; коннектор, уже попавший в основную, пропускается.
        /// Набор из нескольких терминалов одной сети Revit не принял («Some connectors … have been used», КрМа3, «К1.1 2», 0.3.3,
        /// все коннекторы перед вызовом были без системы). По всей видимости, добавив первый, он тянет за ним сеть и соседние
        /// терминалы, и остальные для него уже заняты; одиночные добавления на той же модели проходили.
        /// Возвращает: сколько добавлено и сколько Revit подтянул сам.
        /// </summary>
        private static (int added, int pulled) AddConnectors(Document doc, PipingSystem target, IReadOnlyList<(long OwnerId, int ConnectorId)> moves,
            IOperationProgress progress, ILog log, string stage)
        {
            long targetId = target.Id.ToLong();
            int added = 0;
            int pulled = 0;
            for (int i = 0; i < moves.Count; i++)
            {
                (long ownerId, int connectorId) = moves[i];
                if (moves.Count > 1) progress.Report($"{stage}: терминал {i + 1} из {moves.Count}", i, moves.Count);
                progress.ThrowIfCancelled();
                Connector connector = FindConnector(doc, ownerId, connectorId);
                if (Connectors.SystemId(connector) == targetId)
                {
                    pulled++;
                    continue;
                }
                var watch = Stopwatch.StartNew();
                try
                {
                    var set = new ConnectorSet();
                    set.Insert(connector);
                    target.Add(set);
                    added++;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException first)
                {
                    // Возможно, Revit уже включает его в основную вместе с сетью соседнего терминала — смотрим после пересчёта
                    doc.Regenerate();
                    long now = Connectors.SystemId(FindConnector(doc, ownerId, connectorId));
                    if (now == targetId)
                    {
                        pulled++;
                        continue;
                    }
                    throw new InvalidOperationException($"{Describe(doc, ownerId)}: Revit не добавляет коннектор {connectorId} в основную "
                        + $"({first.Message.Replace(Environment.NewLine, " ")}); сейчас коннектор "
                        + (now < 1 ? "без системы" : $"в «{doc.GetElement(now.ToElementId())?.Name}»"));
                }
                if (watch.Elapsed >= TimeSpan.FromSeconds(1)) log.Info($"      медленно: {Describe(doc, ownerId)} — MEPSystem.Add {OperationTimings.Seconds(watch.Elapsed)}");
            }
            return (added, pulled);
        }

        private static Connector FindConnector(Document doc, long ownerId, int connectorId)
        {
            Element owner = doc.GetElement(ownerId.ToElementId())
                ?? throw new InvalidOperationException($"терминал {ownerId} исчез после удаления системы");
            return Connectors.Find(owner, connectorId)
                ?? throw new InvalidOperationException($"у элемента {ownerId} не найден коннектор {connectorId}");
        }

        private static List<string> ReassignedTerminals(Document doc, PlannedGroup group, IEnumerable<(long OwnerId, int ConnectorId)> moves)
        {
            var busy = new List<string>();
            foreach ((long ownerId, int connectorId) in moves)
            {
                Connector? connector = Connectors.Find(doc.GetElement(ownerId.ToElementId()), connectorId);
                long systemId = Connectors.SystemId(connector);
                if (systemId > 0 && systemId != group.TargetId)
                {
                    busy.Add($"{ownerId} → «{doc.GetElement(systemId.ToElementId())?.Name}»");
                }
            }
            return busy;
        }

        /// <summary>
        /// Проверка внутри транзакции до фиксации, непустой список — откат. Основная на месте, тип и базовое оборудование те же, терминалы в основной, ничего не удалено и не сдвинуто.
        /// Систему труб и фитингов здесь не проверяем: Revit может назначать её только при фиксации (<see cref="VerifyNetworkAfterCommit"/>).
        /// </summary>
        private static List<string> VerifyGroup(Document doc, PlannedGroup g, List<PlannedCandidate> candidates, List<(long OwnerId, int ConnectorId)> moves,
            Dictionary<long, GeometryKey> geometryBefore)
        {
            var bad = new List<string>();
            var target = doc.GetElement(g.TargetId.ToElementId()) as PipingSystem;
            if (target == null || !target.IsValidObject) return new List<string> { "основная система исчезла" };
            if (target.GetTypeId().ToLong() != g.TargetTypeId) bad.Add("изменился тип основной системы");
            long baseEquipment = target.BaseEquipment?.Id.ToLong() ?? -1;
            if (baseEquipment != g.TargetBaseEquipmentId) bad.Add("изменилось базовое оборудование основной системы");

            foreach ((long ownerId, int connectorId) in moves)
            {
                Connector? connector = Connectors.Find(doc.GetElement(ownerId.ToElementId()), connectorId);
                if (Connectors.SystemId(connector) != g.TargetId) bad.Add($"терминал {ownerId} не в основной системе");
            }
            foreach (PlannedCandidate c in candidates)
            {
                foreach (long id in c.ElementIds)
                {
                    Element? e = doc.GetElement(id.ToElementId());
                    if (e == null)
                    {
                        bad.Add($"элемент {id} удалён");
                        continue;
                    }
                    if (geometryBefore.TryGetValue(id, out GeometryKey g0) && !g0.SameAs(GeometryKey.Of(e))) bad.Add($"изменилось положение элемента {id}");
                }
            }
            return bad.Distinct().Take(20).ToList();
        }

        /// <summary>
        /// После фиксации группы: все элементы копий в основной и Revit не создал новых систем.
        /// Если нет — куда ушли элементы (в какую систему или без системы): это ответ на Q-201 / MEP-004.
        /// </summary>
        private static List<string> VerifyNetworkAfterCommit(Document doc, PlannedGroup g, List<PlannedCandidate> candidates, HashSet<long> targetMembers,
            HashSet<long> systemsBefore, HashSet<long> tracked, List<CheckFinding> findings, ILog log)
        {
            var bad = new List<string>();
            var names = new Dictionary<long, string>();
            string SystemName(long id)
            {
                if (!names.TryGetValue(id, out string? name))
                {
                    name = doc.GetElement(id.ToElementId())?.Name ?? id.ToString(CultureInfo.InvariantCulture);
                    name = systemsBefore.Contains(id) ? $"«{name}»" : $"«{name}» (новая, создал Revit)";
                    names[id] = name;
                }
                return name;
            }

            var orphans = new Dictionary<long, PlannedCandidate>();
            var strays = new List<(long Id, string Owner, HashSet<long> Systems)>();
            foreach (PlannedCandidate c in candidates)
            {
                var where = new Dictionary<string, List<long>>(StringComparer.Ordinal);
                foreach (long id in c.ElementIds)
                {
                    Element? e = doc.GetElement(id.ToElementId());
                    if (e == null) continue;
                    HashSet<long> systems = Connectors.SystemsOf(e);
                    if (systems.Contains(g.TargetId)) continue;
                    if (systems.Count == 0) orphans[id] = c;
                    else strays.Add((id, $"копии «{c.Record.Name}»", systems));
                    string key = systems.Count == 0 ? "без системы" : "в " + string.Join(" и ", systems.OrderBy(x => x).Select(SystemName));
                    if (!where.TryGetValue(key, out List<long>? ids)) where[key] = ids = new List<long>();
                    ids.Add(id);
                }
                if (where.Count == 0) continue;
                int total = where.Values.Sum(x => x.Count);
                bad.Add($"«{c.Record.Name}»: {total} элементов после фиксации не в основной системе: "
                    + string.Join("; ", where.OrderByDescending(x => x.Value.Count).Select(x => $"{x.Value.Count} {x.Key} (например, {string.Join(", ", x.Value.OrderBy(id => id).Take(5))})")));
            }

            if (orphans.Count > 0)
            {
                List<OrphanPiece> pieces = OrphanPieces(doc, orphans, g.TargetId, SystemName);
                bad.Add($"без системы осталось {orphans.Count} эл. в {pieces.Count} участках сети — какие и с чем соединены, см. «Отчёт…» и лог");
                foreach (OrphanPiece piece in pieces)
                {
                    log.Info($"    без системы: участок «{piece.Copy}» из {piece.Ids.Count} эл. ({string.Join(", ", piece.Ids.Take(20))}{(piece.Ids.Count > 20 ? " …" : string.Empty)}); "
                        + $"соединён с: {(piece.Borders.Count > 0 ? string.Join("; ", piece.Borders) : "ничем")}");
                    foreach (long id in piece.Ids.Take(5)) log.Info("      " + Describe(doc, id));
                    findings.Add(piece.ToFinding(doc));
                }
            }

            // Элементы самой основной, ушедшие в чужую систему: стык может быть и через них
            var leftTarget = new List<long>();
            foreach (long id in targetMembers.OrderBy(x => x))
            {
                Element? e = doc.GetElement(id.ToElementId());
                if (e == null) continue;
                HashSet<long> systems = Connectors.SystemsOf(e);
                if (systems.Count == 0 || systems.Contains(g.TargetId)) continue;
                leftTarget.Add(id);
                strays.Add((id, $"основной «{g.BaseName}»", systems));
            }
            if (leftTarget.Count > 0)
            {
                bad.Add($"{leftTarget.Count} элементов самой основной «{g.BaseName}» после фиксации ушли в другие системы (например, {string.Join(", ", leftTarget.Take(5))})");
            }

            if (strays.Count > 0) ReportJunctions(doc, strays, tracked, SystemName, bad, findings, log);

            var created = new FilteredElementCollector(doc).OfClass(typeof(PipingSystem))
                .Select(s => s.Id.ToLong()).Where(id => !systemsBefore.Contains(id)).ToList();
            if (created.Count > 0) bad.Add("Revit создал новые системы: " + string.Join(", ", created.Take(10).Select(SystemName)));
            return bad.Distinct().Take(20).ToList();
        }

        /// <summary>
        /// Терминалы каждой копии — отдельным MEPSystem.Add с замером и строкой в лог: видно, на какой копии и каком терминале
        /// Revit тратит время. Между копиями можно отменить.
        /// </summary>
        private static void AddByCopy(Document doc, PipingSystem target, PlannedGroup group, List<PlannedCandidate> candidates, int groupNumber, int groupCount,
            IOperationProgress progress, OperationTimings timings, ILog log)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                PlannedCandidate c = candidates[i];
                progress.ThrowIfCancelled();
                progress.Report($"Объединение {groupNumber} из {groupCount}: добавляю терминалы «{c.Record.Name}» в «{group.BaseName}» "
                    + $"(копия {i + 1} из {candidates.Count}, коннекторов {c.Moves.Count})", groupNumber - 1, groupCount);
                string owners = string.Join(", ", c.Moves.Select(m => m.OwnerId).Distinct().Take(5).Select(id => Describe(doc, id)));
                log.Info($"    «{c.Record.Name}» → «{group.BaseName}»: коннекторов {c.Moves.Count}, элементов в копии {c.ElementIds.Count}; терминалы: {owners}"
                    + (c.Moves.Select(m => m.OwnerId).Distinct().Count() > 5 ? " …" : string.Empty));
                try
                {
                    (int added, int pulled) stats;
                    string stage = $"Объединение {groupNumber} из {groupCount}: «{c.Record.Name}» → «{group.BaseName}»";
                    using (timings.Measure("добавление в основную (MEPSystem.Add)")) stats = AddConnectors(doc, target, c.Moves, progress, log, stage);
                    log.Info($"      добавлено коннекторов {stats.added}, уже оказались в основной (Revit подтянул по сети) {stats.pulled}");
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException)
                {
                    // Почему Revit не принял коннекторы: в какой они системе и с чем соединены
                    log.Info($"    MEPSystem.Add «{c.Record.Name}»: {ex.Message.Replace(Environment.NewLine, " ")}");
                    foreach (string line in DescribeConnectors(doc, c.Moves, group.TargetId)) log.Info("      " + line);
                    throw;
                }
            }
        }

        /// <summary>«Оборудование 4309526 «Семейство: Тип», трубных коннекторов 3».</summary>
        private static string Describe(Document doc, long id)
        {
            Element? e = doc.GetElement(id.ToElementId());
            if (e == null) return $"{id} (нет в модели)";
            return $"{e.Category?.Name ?? "?"} {id} {ElementName(e)}, трубных коннекторов {Connectors.Piping(e).Count}";
        }

        /// <summary>Состояние каждого переносимого коннектора: система, соединён ли и с чем (элемент и его системы).</summary>
        private static IEnumerable<string> DescribeConnectors(Document doc, IEnumerable<(long OwnerId, int ConnectorId)> moves, long targetId)
        {
            foreach ((long ownerId, int connectorId) in moves)
            {
                Connector? connector = Connectors.Find(doc.GetElement(ownerId.ToElementId()), connectorId);
                if (connector == null)
                {
                    yield return $"{ownerId}/{connectorId}: коннектор не найден";
                    continue;
                }
                long systemId = Connectors.SystemId(connector);
                string system = systemId < 1 ? "без системы" : systemId == targetId ? "уже в основной" : $"в «{doc.GetElement(systemId.ToElementId())?.Name}» (Id {systemId})";
                var refs = new List<string>();
                try
                {
                    foreach (Connector other in connector.AllRefs)
                    {
                        if (other?.Owner == null || other.Owner.Id.ToLong() == ownerId) continue;
                        string otherSystems = string.Join(", ", Connectors.SystemsOf(other.Owner).Select(x => doc.GetElement(x.ToElementId())?.Name ?? x.ToString(CultureInfo.InvariantCulture)));
                        refs.Add($"{other.Owner.Category?.Name} {other.Owner.Id.ToLong()} ({(otherSystems.Length > 0 ? otherSystems : "без системы")})");
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    refs.Add("связи не прочитать");
                }
                string domain;
                try
                {
                    domain = connector.PipeSystemType.ToString();
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    domain = "?";
                }
                yield return $"{ownerId}/{connectorId}: {system}, классификация {domain}, соединён: {(connector.IsConnected ? string.Join("; ", refs) : "нет")}";
            }
        }

        /// <summary>
        /// Элементы копий без системы после фиксации — связные участки (по физическим соединениям между ними же)
        /// и то, с чем каждый участок граничит: по этому видно, разрыв это, лишний отрезок или переход через оборудование.
        /// </summary>
        private static List<OrphanPiece> OrphanPieces(Document doc, Dictionary<long, PlannedCandidate> orphans, long targetId, Func<long, string> systemName)
        {
            var pieces = new List<OrphanPiece>();
            var seen = new HashSet<long>();
            foreach (long start in orphans.Keys.OrderBy(x => x))
            {
                if (!seen.Add(start)) continue;
                var ids = new List<long>();
                var borders = new SortedSet<long>();
                var queue = new Queue<long>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    long id = queue.Dequeue();
                    ids.Add(id);
                    Element? e = doc.GetElement(id.ToElementId());
                    if (e == null) continue;
                    foreach (long n in Connectors.NeighbourIds(e))
                    {
                        if (orphans.ContainsKey(n))
                        {
                            if (seen.Add(n)) queue.Enqueue(n);
                        }
                        else
                        {
                            borders.Add(n);
                        }
                    }
                }
                ids.Sort();
                var borderText = new List<string>();
                bool touchesTarget = false;
                foreach (long n in borders)
                {
                    Element? other = doc.GetElement(n.ToElementId());
                    HashSet<long> systems = other != null ? Connectors.SystemsOf(other) : new HashSet<long>();
                    if (systems.Contains(targetId)) touchesTarget = true;
                    if (borderText.Count >= 10) continue;
                    string where = systems.Count == 0 ? "без системы" : string.Join(", ", systems.OrderBy(x => x).Select(systemName));
                    borderText.Add($"{other?.Category?.Name ?? "?"} {n} {ElementName(other)} ({where})");
                }
                if (borders.Count > 10) borderText.Add($"и ещё {borders.Count - 10}");
                pieces.Add(new OrphanPiece(orphans[start].Record.Name, ids, borderText, borders.Count, touchesTarget));
            }
            return pieces;
        }

        /// <summary>
        /// Элементы копий ушли в чужую систему (на КрМа3: «К4.2н 1…5» → «К4.1 1»): сеть копии физически соединена с сетью
        /// другой системы, и Revit, пересчитав сеть после удаления копии, отдал её той. Ищем стык — соседей этих элементов,
        /// которые до запуска не были ни в основной, ни в копиях: по строке на стык, «Показать в модели» — оба элемента.
        /// </summary>
        private static void ReportJunctions(Document doc, List<(long Id, string Owner, HashSet<long> Systems)> strays, HashSet<long> tracked,
            Func<long, string> systemName, List<string> bad, List<CheckFinding> findings, ILog log)
        {
            var seen = new HashSet<(long, long)>();
            var perCopy = new Dictionary<string, int>(StringComparer.Ordinal);
            int found = 0;
            foreach ((long id, string owner, HashSet<long> systems) in strays)
            {
                Element? e = doc.GetElement(id.ToElementId());
                if (e == null) continue;
                foreach (long n in Connectors.NeighbourIds(e))
                {
                    if (tracked.Contains(n) || !seen.Add((id, n))) continue;
                    perCopy.TryGetValue(owner, out int count);
                    perCopy[owner] = ++count;
                    found++;
                    if (count > 5) continue; // на копию (основную) — первые 5 стыков, остальные только считаем
                    Element? other = doc.GetElement(n.ToElementId());
                    HashSet<long> otherSystems = other != null ? Connectors.SystemsOf(other) : new HashSet<long>();
                    string where = string.Join(", ", systems.OrderBy(x => x).Select(systemName));
                    string otherWhere = otherSystems.Count == 0 ? "без системы" : string.Join(", ", otherSystems.OrderBy(x => x).Select(systemName));
                    string text = $"Сеть {owner} соединена с сетью {otherWhere}: {Describe(doc, id)} ↔ {Describe(doc, n)}. "
                        + $"После удаления копий Revit отдал её элементы {where}";
                    log.Info("    стык: " + text);
                    var finding = new CheckFinding(MergeReportBuilder.CheckMerge, Severity.Error, text)
                    {
                        ElementId = id,
                        Category = e.Category?.Name ?? string.Empty,
                        Family = e is FamilyInstance fi && fi.Symbol != null ? fi.Symbol.FamilyName : string.Empty,
                        Type = e is FamilyInstance fi2 && fi2.Symbol != null ? fi2.Symbol.Name : e.Name,
                        Current = "в " + where,
                        Expected = "в основной системе",
                        Action = "Если соединение ошибочное — разъедините сети в этом месте и повторите пробный прогон. "
                            + "Если так задумано (одна физическая сеть с разными системами), эту копию так не объединить: снимите отметку.",
                    };
                    finding.RelatedElementIds.Add(n);
                    findings.Add(finding);
                }
            }
            if (found > 0)
            {
                bad.Add($"сеть соединена с сетями других систем ({found} стыков: "
                    + string.Join(", ", perCopy.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key} {x.Value}")) + ") — где, см. «Отчёт…» и лог");
            }
            else
            {
                bad.Add("стык с сетью другой системы не найден ни у копий, ни у основной — пришлите лог");
            }
        }

        private static string ElementName(Element? e) =>
            e == null ? string.Empty : e is FamilyInstance fi && fi.Symbol != null ? $"«{fi.Symbol.FamilyName}: {fi.Symbol.Name}»" : $"«{e.Name}»";

        private sealed class OrphanPiece
        {
            public OrphanPiece(string copy, List<long> ids, List<string> borders, int borderCount, bool touchesTarget)
            {
                Copy = copy;
                Ids = ids;
                Borders = borders;
                BorderCount = borderCount;
                TouchesTarget = touchesTarget;
            }

            public string Copy { get; }

            public List<long> Ids { get; }

            public List<string> Borders { get; }

            public int BorderCount { get; }

            public bool TouchesTarget { get; }

            public CheckFinding ToFinding(Document doc)
            {
                Element? first = doc.GetElement(Ids[0].ToElementId());
                string action = BorderCount == 0
                    ? "Участок ни с чем не соединён: соедините его с сетью (разрыв) или удалите, если он лишний. Затем повторите пробный прогон."
                    : TouchesTarget
                        ? "Участок соединён с элементами основной системы, но Revit не включил его в неё. Посмотрите соединение на границе участка и пришлите лог."
                        : "Участок соединён только с элементами без системы или других систем (часто — через оборудование без коннектора этой классификации). "
                            + "Проверьте соединения на границе участка, затем повторите пробный прогон.";
                var finding = new CheckFinding(MergeReportBuilder.CheckMerge, Severity.Error,
                    $"После фиксации без системы: участок копии «{Copy}» из {Ids.Count} эл.; соединён с: {(Borders.Count > 0 ? string.Join("; ", Borders) : "ничем")}")
                {
                    ElementId = Ids[0],
                    Category = first?.Category?.Name ?? string.Empty,
                    Family = first is FamilyInstance fi && fi.Symbol != null ? fi.Symbol.FamilyName : string.Empty,
                    Type = first is FamilyInstance fi2 && fi2.Symbol != null ? fi2.Symbol.Name : first?.Name ?? string.Empty,
                    Current = "без системы",
                    Expected = "в основной системе",
                    Action = action,
                };
                finding.RelatedElementIds.AddRange(Ids);
                return finding;
            }
        }

        /// <summary>Если фиксацию сорвало обновление другой надстройки — назвать её: это не ошибка VK Tools, модель не изменена.</summary>
        private static string ThirdPartyHint(Document doc, IEnumerable<string> errors)
        {
            string all = string.Join(" ", errors);
            if (all.Length == 0) return string.Empty;
            try
            {
                foreach (UpdaterInfo info in UpdaterRegistry.GetRegisteredUpdaterInfos(doc))
                {
                    if (!string.IsNullOrEmpty(info.UpdaterName) && all.Contains("«" + info.UpdaterName + "»"))
                    {
                        return $". Фиксацию сорвало обновление «{info.UpdaterName}» надстройки «{info.ApplicationName}»: оно упало на этом изменении, "
                            + "и Revit отменил всю транзакцию. Это не ошибка VK Tools, модель не изменена";
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Список апдейтеров — только для подсказки
            }
            return string.Empty;
        }

        /// <summary>Итоговая проверка по основным системам и всем затронутым элементам. Строки с «!!!» ведут к откату.</summary>
        private static void PostCheck(Document doc, List<PlannedGroup> groups, HashSet<long> merged, HashSet<long> tracked,
            Dictionary<long, HashSet<long>> trackedSystems, Dictionary<long, GeometryKey> geometryBefore, Dictionary<long, int> membersBefore,
            HashSet<long> systemsBefore, IOperationProgress progress, List<string> output)
        {
            progress.Report("Итоговая проверка…");
            PipingModel after = PipingModel.Take(doc);
            foreach (PlannedGroup g in groups)
            {
                string head = $"«{g.BaseName}»: ";
                if (!after.Systems.TryGetValue(g.TargetId, out PipingSystem? target))
                {
                    output.Add(head + "!!! основная система не существует после операции");
                    continue;
                }
                List<long> still = g.Candidates.Select(c => c.Record.Id).Where(id => merged.Contains(id) && after.Systems.ContainsKey(id)).ToList();
                long baseEquipment = target.BaseEquipment?.Id.ToLong() ?? -1;
                output.Add(head + string.Join("; ", new[]
                {
                    $"элементов в основной было {(membersBefore.TryGetValue(g.TargetId, out int n) ? n : 0)}, стало {after.MembersOf(g.TargetId).All.Count}",
                    still.Count == 0 ? "объединённые системы исчезли" : "!!! не исчезли: " + string.Join(", ", still),
                    target.GetTypeId().ToLong() == g.TargetTypeId ? "тип сохранён" : "!!! изменился тип основной системы",
                    baseEquipment == g.TargetBaseEquipmentId ? "базовое оборудование без изменений" : "!!! изменилось базовое оборудование",
                }));
            }

            var orphans = new List<long>();
            var gone = new List<long>();
            var moved = new List<long>();
            int i = 0;
            foreach (long id in tracked)
            {
                if (i++ % 500 == 0) progress.Report($"Итоговая проверка: {tracked.Count} элементов", i, tracked.Count);
                Element? e = after.Element(id);
                if (e == null)
                {
                    gone.Add(id);
                    continue;
                }
                if (trackedSystems.TryGetValue(id, out HashSet<long>? was) && was.Count > 0 && after.SystemsOf(id).Count == 0) orphans.Add(id);
                if (geometryBefore.TryGetValue(id, out GeometryKey g0) && !g0.SameAs(GeometryKey.Of(e))) moved.Add(id);
            }
            output.Add(orphans.Count == 0 ? "Элементов, потерявших систему, нет" : $"!!! Потеряли систему {orphans.Count} эл. (например, {string.Join(", ", orphans.Take(10))})");

            var created = after.Systems.Keys.Where(id => !systemsBefore.Contains(id)).ToList();
            output.Add(created.Count == 0 ? "Новых систем нет" : "!!! Revit создал новые системы: " + string.Join(", ", created.Select(id => after.Systems[id].Name)));
            output.Add(gone.Count == 0 ? "Элементы на месте" : "!!! Удалены элементы: " + string.Join(", ", gone.Take(10)));
            output.Add(moved.Count == 0 ? $"Геометрия без изменений ({geometryBefore.Count} эл.)" : "!!! Сдвинуты элементы: " + string.Join(", ", moved.Take(10)));
            output.Add($"Трубопроводных систем в модели: было {systemsBefore.Count}, стало {after.Systems.Count}");
        }
    }
}
