using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Mep
{
    /// <summary>
    /// Сведения о модели для лога перед записью: размер основных систем, уровень расчёта типа системы,
    /// апдейтеры других надстроек. Вместе со временем этапов показывает, что тормозит.
    /// </summary>
    internal static class MergeDiagnostics
    {
        public static IEnumerable<string> Describe(Document doc, MergePlan plan, IReadOnlyCollection<PlannedCandidate> selected)
        {
            yield return $"модель «{doc.Title}»: совместная работа {(doc.IsWorkshared ? "да" : "нет")}, систем трубопроводов {plan.Model.Systems.Count}, отмечено копий {selected.Count}";
            foreach (IGrouping<PlannedGroup, PlannedCandidate> g in selected.GroupBy(c => c.Group))
            {
                var type = doc.GetElement(g.Key.TargetTypeId.ToElementId()) as MEPSystemType;
                string calculation;
                try
                {
                    calculation = type?.CalculationLevel.ToString() ?? "?";
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    calculation = "?";
                }
                yield return $"основная «{g.Key.BaseName}» (Id {g.Key.TargetId}): элементов {plan.Model.MembersOf(g.Key.TargetId).All.Count}, "
                    + $"тип «{type?.Name}», расчёт {calculation}; копий {g.Count()}, в них элементов {g.Sum(c => c.ElementIds.Count)}, терминалов {g.Sum(c => c.TerminalIds.Count)}";
                var ids = new HashSet<long>(plan.Model.MembersOf(g.Key.TargetId).All);
                foreach (PlannedCandidate c in g) ids.UnionWith(c.ElementIds);
                yield return "  сеть основной с копиями: " + Graph(doc, ids);
                if (doc.IsWorkshared) yield return "  совместная работа: " + Ownership(doc, ids);
            }
            List<string> updaters;
            try
            {
                updaters = UpdaterRegistry.GetRegisteredUpdaterInfos(doc).Select(u => $"{u.UpdaterName} ({u.ApplicationName})").ToList();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                updaters = new List<string> { "не удалось прочитать" };
            }
            yield return $"апдейтеры документа ({updaters.Count}): " + string.Join("; ", updaters);
        }

        /// <summary>
        /// Связность: элементы, соединения между ними, связные части и кольца (соединения − элементы + части).
        /// Проверяем догадку, что MEPSystem.Add тормозит на сетях с кольцами (на КрМа3 одиночный Add шёл от 0 с до «не дождались»).
        /// </summary>
        private static string Graph(Document doc, HashSet<long> ids)
        {
            var edges = new HashSet<(long, long)>();
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
                    if (!ids.Contains(n)) continue;
                    if (edges.Add(id < n ? (id, n) : (n, id))) parent[Find(id)] = Find(n);
                }
            }
            int parts = ids.Count(id => Find(id) == id);
            int loops = edges.Count - ids.Count + parts;
            return $"элементов {ids.Count}, соединений {edges.Count}, связных частей {parts}, колец {loops}";
        }

        /// <summary>Кто владеет элементами и устарели ли они относительно центральной (локальный кэш Revit, без обращения к серверу).</summary>
        private static string Ownership(Document doc, HashSet<long> ids)
        {
            int mine = 0, free = 0, others = 0, outdated = 0;
            var owners = new SortedSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (long id in ids)
                {
                    ElementId eid = id.ToElementId();
                    CheckoutStatus status = WorksharingUtils.GetCheckoutStatus(doc, eid, out string owner);
                    if (status == CheckoutStatus.OwnedByCurrentUser) mine++;
                    else if (status == CheckoutStatus.OwnedByOtherUser)
                    {
                        others++;
                        if (!string.IsNullOrEmpty(owner)) owners.Add(owner);
                    }
                    else free++;
                    ModelUpdatesStatus updates = WorksharingUtils.GetModelUpdatesStatus(doc, eid);
                    if (updates == ModelUpdatesStatus.UpdatedInCentral || updates == ModelUpdatesStatus.DeletedInCentral) outdated++;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                return "не удалось прочитать: " + ex.Message;
            }
            return $"заняты мной {mine}, свободны {free}, заняты другими {others}{(owners.Count > 0 ? " (" + string.Join(", ", owners.Take(3)) + ")" : string.Empty)}, "
                + $"изменены в центральной после моего обновления {outdated}";
        }
    }
}
