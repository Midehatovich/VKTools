using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using VKTools.Core.Logging;
using VKTools.Core.Progress;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Mep
{
    /// <summary>Итог пробной пересборки одной основной системы (всегда откачена).</summary>
    internal sealed class RebuildProbeResult
    {
        public RebuildProbeResult(string target)
        {
            Target = target;
        }

        public string Target { get; }

        /// <summary>Все части сети добавлены в новую систему, фиксация прошла.</summary>
        public bool Passed { get; set; }

        /// <summary>Строки для окна и лога: по части сети, итог.</summary>
        public List<string> Lines { get; } = new List<string>();

        public string Summary { get; set; } = string.Empty;
    }

    /// <summary>
    /// Пробная пересборка основной (D-023): удалить объекты основной и её копий, создать новую систему того же типа
    /// и добавлять в неё терминалы по связным частям сети, от меньшей к большей, с замером каждого MEPSystem.Add.
    /// Затем фиксация и откат всего: модель не меняется.
    /// На КрМа3 любое добавление в «К1» висло и в самом Revit (проверено вручную 2026-10-10), удаление из неё — нет.
    /// Проба отвечает: сломан сам объект «К1» (тогда новая система соберётся) или что-то в части сети (тогда видно, в какой).
    /// Параметры старой системы здесь не переносятся: это только проверка.
    /// </summary>
    internal static class SystemRebuildProbe
    {
        /// <summary>Шаг дольше — дальше не идём: следующий может не вернуться вовсе.</summary>
        private static readonly TimeSpan SlowStep = TimeSpan.FromSeconds(20);

        public static RebuildProbeResult Run(Document doc, PipingModel model, PlannedGroup group, List<PlannedCandidate> candidates,
            IOperationProgress progress, OperationTimings timings, ILog log)
        {
            var result = new RebuildProbeResult(group.BaseName);
            PipingSystem? old = group.Target;
            if (old == null || !old.IsValidObject)
            {
                result.Summary = $"«{group.BaseName}»: основной системы нет в модели — пересборка не проверялась.";
                return result;
            }
            if (group.TargetBaseEquipmentId > 0)
            {
                // Как Revit назначит базовое оборудование новой системе, не проверено: не гадаем
                result.Summary = $"«{group.BaseName}»: у основной есть базовое оборудование ({group.TargetBaseEquipmentId}) — пересборку для таких систем плагин пока не проверяет.";
                return result;
            }

            // Терминалы основной (коннекторы этой системы) и копий
            SystemMembers members = model.MembersOf(group.TargetId);
            var moves = new List<(long OwnerId, int ConnectorId)>();
            foreach (long terminalId in members.Terminals)
            {
                foreach (Connector c in Connectors.Piping(model.Element(terminalId)))
                {
                    if (Connectors.SystemId(c) == group.TargetId) moves.Add((terminalId, c.Id));
                }
            }
            foreach (PlannedCandidate c in candidates) moves.AddRange(c.Moves);
            var tracked = new HashSet<long>(members.All);
            foreach (PlannedCandidate c in candidates) tracked.UnionWith(c.ElementIds);
            foreach ((long ownerId, int _) in moves) tracked.Add(ownerId);

            List<Part> parts = Parts(doc, tracked, moves);
            log.Info($"  пересборка «{group.BaseName}» (Id {group.TargetId}) с копиями {string.Join(", ", candidates.Select(c => $"«{c.Record.Name}»"))}: "
                + $"элементов {tracked.Count}, коннекторов терминалов {moves.Count}, связных частей с терминалами {parts.Count}");

            string name = old.Name;
            ElementId typeId = old.GetTypeId();
            var names = new HashSet<string>(model.Systems.Values.Select(s => s.Name ?? string.Empty), StringComparer.Ordinal);
            string tempName = name + " (VK Tools)";
            for (int n = 2; names.Contains(tempName); n++) tempName = $"{name} (VK Tools {n.ToString(CultureInfo.InvariantCulture)})";
            var systemsBefore = new HashSet<long>(model.Systems.Keys);
            var failures = new FailureCollector(SystemMergeExecutor.RegisteredUpdaters(doc), SystemMergeExecutor.DisableOnFailureApplications);

            using (var operation = new TransactionGroup(doc, "VK Tools: пересборка системы (проба)"))
            {
                operation.Start();
                Transaction? transaction = null;
                try
                {
                    transaction = FailureCollector.Start(doc, $"Пересборка «{name}» (проба)", failures);
                    using (timings.Measure("пересборка: удаление систем"))
                    {
                        var systems = candidates.Select(c => c.Record.Id).Append(group.TargetId).ToList();
                        foreach (long systemId in systems)
                        {
                            var deleted = new HashSet<long>(doc.Delete(systemId.ToElementId()).Select(x => x.ToLong()));
                            deleted.Remove(systemId);
                            List<long> lost = deleted.Where(tracked.Contains).OrderBy(x => x).Take(10).ToList();
                            if (lost.Count > 0) throw new InvalidOperationException($"Revit удаляет вместе с системой {systemId} элементы: {string.Join(", ", lost)}");
                        }
                    }
                    PipingSystem created;
                    using (timings.Measure("пересборка: создание системы")) created = PipingSystem.Create(doc, typeId, tempName);
                    long createdId = created.Id.ToLong();
                    log.Info($"    создана «{tempName}» (Id {createdId}), тип «{doc.GetElement(typeId)?.Name}»");

                    for (int p = 0; p < parts.Count; p++)
                    {
                        Part part = parts[p];
                        string head = $"часть {p + 1} из {parts.Count} ({part.Elements} эл., терминалов {part.Owners.Count}: {part.Composition})";
                        progress.ThrowIfCancelled();
                        log.Info($"    {head}");
                        var watch = Stopwatch.StartNew();
                        TimeSpan slowest = TimeSpan.Zero;
                        int added = 0, pulled = 0;
                        foreach ((long ownerId, int connectorId) in part.Moves)
                        {
                            progress.ThrowIfCancelled();
                            Connector connector = FindConnector(doc, ownerId, connectorId);
                            if (Connectors.SystemId(connector) == createdId)
                            {
                                pulled++;
                                continue;
                            }
                            string what = Describe(doc, ownerId);
                            progress.Report($"Пересборка «{name}» (проба): {head} — Revit добавляет {what}. Пока Revit не закончит этот шаг, «Отменить» не сработает.",
                                p, parts.Count);
                            var step = Stopwatch.StartNew();
                            HangMarker.Begin(doc.Title, ownerId, $"Пересборка «{name}», часть {p + 1} из {parts.Count}: MEPSystem.Add {what}");
                            try
                            {
                                var set = new ConnectorSet();
                                set.Insert(connector);
                                created.Add(set);
                                added++;
                            }
                            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                            {
                                doc.Regenerate();
                                long now = Connectors.SystemId(FindConnector(doc, ownerId, connectorId));
                                if (now != createdId)
                                {
                                    throw new InvalidOperationException($"{head}: Revit не добавляет {what} ({ex.Message.Replace(Environment.NewLine, " ")}); сейчас коннектор "
                                        + (now < 1 ? "без системы" : $"в «{doc.GetElement(now.ToElementId())?.Name}»"));
                                }
                                pulled++;
                            }
                            finally
                            {
                                HangMarker.End();
                            }
                            if (step.Elapsed > slowest) slowest = step.Elapsed;
                            if (step.Elapsed >= TimeSpan.FromSeconds(1)) log.Info($"      медленно: {what} — MEPSystem.Add {OperationTimings.Seconds(step.Elapsed)}");
                        }
                        string line = $"{head}: добавлено {added}, подтянул сам {pulled}, время {OperationTimings.Seconds(watch.Elapsed)}";
                        result.Lines.Add(line);
                        log.Info("      " + line);
                        if (slowest >= SlowStep && p + 1 < parts.Count)
                        {
                            throw new InvalidOperationException($"остановлено после части {p + 1}: одно добавление заняло {OperationTimings.Seconds(slowest)}, "
                                + "следующее может не вернуться вовсе");
                        }
                    }

                    progress.Report($"Пересборка «{name}» (проба): пересчёт модели (Regenerate)");
                    using (timings.Measure("пересборка: Regenerate")) doc.Regenerate();
                    result.Lines.Add("после Regenerate: " + Census(doc, tracked, createdId, systemsBefore));

                    progress.Report($"Пересборка «{name}» (проба): фиксация (здесь же работают апдейтеры других надстроек)");
                    TransactionStatus status;
                    using (timings.Measure("пересборка: фиксация")) status = transaction.Commit();
                    foreach (string note in failures.Notes) log.Warn("    " + note);
                    SystemMergeExecutor.RememberDisabled(failures.DisabledUpdaters);
                    if (status != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException($"Revit отклонил фиксацию ({status}): " + (failures.Errors.Count > 0 ? string.Join("; ", failures.Errors) : "—"));
                    }
                    result.Lines.Add("после фиксации: " + Census(doc, tracked, createdId, systemsBefore));
                    result.Passed = true;
                    result.Summary = $"«{name}»: пересборка (проба) прошла — все {parts.Count} частей сети добавились в новую систему. Откачено, модель не изменена.";
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException
                    || ex is ArgumentException || ex is OperationCanceledException)
                {
                    bool cancelled = ex is OperationCanceledException || progress.IsCancellationRequested;
                    result.Summary = cancelled
                        ? $"«{name}»: пересборка (проба) отменена. Откачено, модель не изменена."
                        : $"«{name}»: пересборка (проба) не прошла: {ex.Message}. Откачено, модель не изменена.";
                    foreach (string e in failures.Errors) result.Lines.Add("ошибка Revit: " + e);
                }
                finally
                {
                    if (transaction != null)
                    {
                        if (transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
                        transaction.Dispose();
                    }
                    progress.Report("Откат пробы…");
                    using (timings.Measure("пересборка: откат")) operation.RollBack();
                }
            }
            foreach (string w in failures.Warnings) result.Lines.Add("предупреждение Revit: " + w);
            log.Info("    " + result.Summary);
            return result;
        }

        /// <summary>Где элементы основной и копий: в новой системе, без системы, в других; какие системы Revit создал сам.</summary>
        private static string Census(Document doc, HashSet<long> tracked, long createdId, HashSet<long> systemsBefore)
        {
            int inCreated = 0, none = 0, other = 0;
            foreach (long id in tracked)
            {
                Element? e = doc.GetElement(id.ToElementId());
                if (e == null) continue;
                HashSet<long> systems = Connectors.SystemsOf(e);
                if (systems.Contains(createdId)) inCreated++;
                else if (systems.Count == 0) none++;
                else other++;
            }
            List<string> made = new FilteredElementCollector(doc).OfClass(typeof(PipingSystem))
                .Where(s => s.Id.ToLong() != createdId && !systemsBefore.Contains(s.Id.ToLong()))
                .Select(s => $"«{s.Name}»").Take(10).ToList();
            return $"в новой системе {inCreated} эл., без системы {none}, в других системах {other}"
                + (made.Count > 0 ? "; Revit сам создал системы: " + string.Join(", ", made) : "; других новых систем нет");
        }

        /// <summary>Связные части сети (по физическим соединениям внутри набора), в каждой — её коннекторы терминалов. От меньшей к большей.</summary>
        private static List<Part> Parts(Document doc, HashSet<long> ids, List<(long OwnerId, int ConnectorId)> moves)
        {
            var parent = ids.ToDictionary(id => id, id => id);
            long Find(long x)
            {
                while (parent[x] != x) x = parent[x] = parent[parent[x]];
                return x;
            }
            foreach (long id in ids)
            {
                Element? e = doc.GetElement(id.ToElementId());
                if (e == null) continue;
                foreach (long n in Connectors.NeighbourIds(e))
                {
                    if (ids.Contains(n)) parent[Find(id)] = Find(n);
                }
            }
            var size = new Dictionary<long, int>();
            foreach (long id in ids)
            {
                long root = Find(id);
                size.TryGetValue(root, out int k);
                size[root] = k + 1;
            }
            var parts = new Dictionary<long, Part>();
            foreach ((long ownerId, int connectorId) in moves)
            {
                long root = Find(ownerId);
                if (!parts.TryGetValue(root, out Part? part)) parts[root] = part = new Part(size[root]);
                part.Moves.Add((ownerId, connectorId));
                part.Owners.Add(ownerId);
            }
            foreach (Part part in parts.Values)
            {
                part.Composition = string.Join(", ", part.Owners
                    .Select(id => doc.GetElement(id.ToElementId()))
                    .GroupBy(e => e is FamilyInstance fi && fi.Symbol != null ? fi.Symbol.FamilyName : e?.Name ?? "?")
                    .OrderByDescending(g => g.Count())
                    .Take(4)
                    .Select(g => $"{g.Key} {g.Count()}"));
            }
            return parts.Values.OrderBy(p => p.Elements).ThenBy(p => p.Owners.Min()).ToList();
        }

        private static Connector FindConnector(Document doc, long ownerId, int connectorId)
        {
            Element owner = doc.GetElement(ownerId.ToElementId()) ?? throw new InvalidOperationException($"терминал {ownerId} исчез после удаления системы");
            return Connectors.Find(owner, connectorId) ?? throw new InvalidOperationException($"у элемента {ownerId} не найден коннектор {connectorId}");
        }

        private static string Describe(Document doc, long id)
        {
            Element? e = doc.GetElement(id.ToElementId());
            if (e == null) return $"{id} (нет в модели)";
            string name = e is FamilyInstance fi && fi.Symbol != null ? $"{fi.Symbol.FamilyName}: {fi.Symbol.Name}" : e.Name;
            return $"{e.Category?.Name ?? "?"} {id} «{name}»";
        }

        private sealed class Part
        {
            public Part(int elements)
            {
                Elements = elements;
            }

            public int Elements { get; }

            public List<(long OwnerId, int ConnectorId)> Moves { get; } = new List<(long, int)>();

            public HashSet<long> Owners { get; } = new HashSet<long>();

            public string Composition { get; set; } = string.Empty;
        }
    }
}
