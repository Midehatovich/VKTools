using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using VKTools.Core.Mep;
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
                foreach (string line in LoopLines(doc, ids)) yield return "    " + line;
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

        /// <summary>Где кольца: элементы и соединение в верхней точке (D-021, только лог — кольца зависание не объясняют).</summary>
        private static IEnumerable<string> LoopLines(Document doc, HashSet<long> ids)
        {
            var edges = new List<(long A, long B)>();
            foreach (long id in ids)
            {
                Element? e = doc.GetElement(id.ToElementId());
                if (e == null) continue;
                foreach (long n in Connectors.NeighbourIds(e))
                {
                    if (ids.Contains(n)) edges.Add((id, n));
                }
            }
            foreach (NetworkLoop loop in NetworkLoops.Find(ids, edges, (a, b) => JunctionZ(doc, a, b), 10))
            {
                yield return $"кольцо: {loop.Ids.Count} эл., верхняя точка {loop.BreakA} ↔ {loop.BreakB}; элементы {string.Join(", ", loop.Ids.Take(15))}{(loop.Ids.Count > 15 ? " …" : string.Empty)}";
            }
        }

        private static double JunctionZ(Document doc, long a, long b)
        {
            double z = double.MinValue;
            foreach (Connector connector in Connectors.Piping(doc.GetElement(a.ToElementId())))
            {
                try
                {
                    if (!connector.IsConnected) continue;
                    foreach (Connector other in connector.AllRefs)
                    {
                        if (other.Owner != null && other.Owner.Id.ToLong() == b) z = Math.Max(z, connector.Origin.Z);
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Недоступный коннектор пропускаем
                }
            }
            return z;
        }

        /// <summary>
        /// Сеть вокруг каждой основной — при анализе, без изменений модели (0.3.12). На КрМа3 MEPSystem.Add в «К1» зависал
        /// на первом же терминале (умывальник «К1 1», воронка «К1 2»), а в остальные основные шёл доли секунды. Ищем, чем «К1»
        /// отличается: что связано с ней через стыки и оборудование, на каких элементах предупреждения Revit, какие классификации
        /// коннекторов, группы и варианты конструкции.
        /// </summary>
        public static IEnumerable<string> Networks(Document doc, MergePlan plan)
        {
            Dictionary<long, List<string>> warnings = WarningsByElement(doc);
            foreach (PlannedGroup g in plan.Groups.Where(x => x.TargetId > 0 && x.Candidates.Count > 0))
            {
                SystemMembers members = plan.Model.MembersOf(g.TargetId);
                var target = new HashSet<long>(members.All);
                string baseEquipment = members.BaseEquipmentId > 0 ? Describe(doc, members.BaseEquipmentId) : "нет";
                yield return $"сеть «{g.BaseName}» (Id {g.TargetId}): элементов {target.Count}, терминалов {members.Terminals.Count}, базовое оборудование: {baseEquipment}";

                // Всё, что физически связано с основной (через стыки и оборудование), и в каких системах
                var seen = new HashSet<long>(target);
                var queue = new Queue<long>(target);
                var border = new Dictionary<string, List<long>>(StringComparer.Ordinal);
                var reachedFrom = new Dictionary<long, long>();
                bool capped = false;
                while (queue.Count > 0)
                {
                    long id = queue.Dequeue();
                    Element? e = doc.GetElement(id.ToElementId());
                    if (e == null) continue;
                    foreach (long n in Connectors.NeighbourIds(e))
                    {
                        if (!seen.Add(n)) continue;
                        reachedFrom[n] = id;
                        if (seen.Count > 60000)
                        {
                            capped = true;
                            break;
                        }
                        queue.Enqueue(n);
                        if (target.Contains(id))
                        {
                            Element? other = doc.GetElement(n.ToElementId());
                            string key = other == null ? "?" : SystemNames(doc, other);
                            if (!border.TryGetValue(key, out List<long>? list)) border[key] = list = new List<long>();
                            list.Add(id);
                        }
                    }
                    if (capped) break;
                }
                var systemsAround = new Dictionary<string, int>(StringComparer.Ordinal);
                var withoutSystem = new List<long>();
                foreach (long id in seen.Where(x => !target.Contains(x)))
                {
                    Element? e = doc.GetElement(id.ToElementId());
                    string key = e == null ? "?" : SystemNames(doc, e);
                    systemsAround.TryGetValue(key, out int c);
                    systemsAround[key] = c + 1;
                    if (e != null && Connectors.SystemsOf(e).Count == 0) withoutSystem.Add(id);
                }
                yield return $"  связано с ней через стыки и оборудование: ещё {seen.Count - target.Count} эл.{(capped ? " (обход остановлен на 60000)" : string.Empty)}"
                    + (systemsAround.Count > 0 ? ": " + string.Join("; ", systemsAround.OrderByDescending(x => x.Value).Take(8).Select(x => $"{x.Key} {x.Value}")) : string.Empty);
                foreach (KeyValuePair<string, List<long>> b in border.OrderByDescending(x => x.Value.Count).Take(8))
                {
                    yield return $"    граница с {b.Key}: {b.Value.Count} соед., например {string.Join("; ", b.Value.Distinct().Take(3).Select(id => Describe(doc, id)))}";
                }

                // Оборудование и приборы основной, чьи коннекторы в разных системах
                int bridges = 0;
                foreach (long id in target)
                {
                    if (!(doc.GetElement(id.ToElementId()) is FamilyInstance fi)) continue;
                    HashSet<long> systems = Connectors.SystemsOf(fi);
                    if (systems.Count < 2) continue;
                    if (++bridges > 8) continue;
                    yield return $"    в нескольких системах: {Describe(doc, id)} — {SystemNames(doc, fi)}";
                }
                if (bridges > 8) yield return $"    в нескольких системах: и ещё {bridges - 8}";

                // Классификации коннекторов, группы, варианты, предупреждения Revit
                var classes = new Dictionary<string, int>(StringComparer.Ordinal);
                int grouped = 0, options = 0;
                var warned = new Dictionary<string, int>(StringComparer.Ordinal);
                int warnedElements = 0;
                foreach (long id in target)
                {
                    Element? e = doc.GetElement(id.ToElementId());
                    if (e == null) continue;
                    foreach (Connector c in Connectors.Piping(e))
                    {
                        string cls;
                        try
                        {
                            cls = c.PipeSystemType.ToString();
                        }
                        catch (Autodesk.Revit.Exceptions.ApplicationException)
                        {
                            cls = "?";
                        }
                        classes.TryGetValue(cls, out int k);
                        classes[cls] = k + 1;
                    }
                    if (e.GroupId != null && e.GroupId != ElementId.InvalidElementId) grouped++;
                    if (e.DesignOption != null) options++;
                    if (warnings.TryGetValue(id, out List<string>? texts))
                    {
                        warnedElements++;
                        foreach (string t in texts.Distinct())
                        {
                            warned.TryGetValue(t, out int k);
                            warned[t] = k + 1;
                        }
                    }
                }
                yield return $"  коннекторы: {string.Join(", ", classes.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}"))}; в группах {grouped}, в вариантах {options}";
                yield return $"  предупреждения Revit на элементах: {warnedElements}"
                    + (warned.Count > 0 ? " — " + string.Join("; ", warned.OrderByDescending(x => x.Value).Take(5).Select(x => $"{x.Value} × {Short(x.Key)}")) : string.Empty);

                foreach (long id in withoutSystem.Take(20))
                {
                    string via = reachedFrom.TryGetValue(id, out long prev) ? Describe(doc, prev) : "?";
                    yield return $"    без системы: {Describe(doc, id)} ← {via}";
                }
                if (withoutSystem.Count > 20) yield return $"    без системы: и ещё {withoutSystem.Count - 20}";
                yield return "  " + Families(doc, target);
                var scan = new HashSet<long>(target);
                scan.UnionWith(withoutSystem);
                foreach (string line in ConnectorFindings(doc, scan)) yield return "  " + line;
            }
        }

        /// <summary>Из каких семейств собрана сеть (кроме труб): «К1» висла, остальные нет — ищем отличие в составе (0.3.14).</summary>
        private static string Families(Document doc, HashSet<long> ids)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (long id in ids)
            {
                if (!(doc.GetElement(id.ToElementId()) is FamilyInstance fi)) continue;
                string key = $"{fi.Category?.Name ?? "?"}: {fi.Symbol?.FamilyName ?? "?"}";
                counts.TryGetValue(key, out int c);
                counts[key] = c + 1;
            }
            return $"семейства (без труб): {counts.Count} разных"
                + (counts.Count > 0 ? " — " + string.Join("; ", counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Take(25).Select(x => $"{x.Key} {x.Value}")) : string.Empty);
        }

        /// <summary>
        /// Необычные коннекторы сети (0.3.14, только чтение): связанные коннекторы (проходное оборудование), коннектор с несколькими
        /// присоединениями, односторонняя связь, два соединения между одной парой элементов, соединение с собой, стык разных
        /// классификаций, открытые концы. Кольца по элементам (D-021) двойные соединения и проходное оборудование не видят.
        /// </summary>
        private static IEnumerable<string> ConnectorFindings(Document doc, HashSet<long> ids)
        {
            var linked = new List<long>();
            var multiRef = new List<long>();
            var oneSided = new List<string>();
            var self = new List<long>();
            var mismatch = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var pairs = new Dictionary<(long, long), HashSet<(int, int)>>();
            var open = new Dictionary<string, int>(StringComparer.Ordinal);
            int notEnd = 0;
            foreach (long id in ids)
            {
                Element? e = doc.GetElement(id.ToElementId());
                if (e == null) continue;
                foreach (Connector c in Connectors.Piping(e))
                {
                    try
                    {
                        if (c.ConnectorType != ConnectorType.End) notEnd++;
                        if (e is FamilyInstance && c.GetMEPConnectorInfo()?.LinkedConnector != null && !linked.Contains(id)) linked.Add(id);
                        if (!c.IsConnected)
                        {
                            string cat = e.Category?.Name ?? "?";
                            open.TryGetValue(cat, out int k);
                            open[cat] = k + 1;
                            continue;
                        }
                        var physical = new List<Connector>();
                        foreach (Connector other in c.AllRefs)
                        {
                            if (other.Owner is MEPCurve || other.Owner is FamilyInstance) physical.Add(other);
                        }
                        if (physical.Count > 1 && !multiRef.Contains(id)) multiRef.Add(id);
                        foreach (Connector other in physical)
                        {
                            long n = other.Owner.Id.ToLong();
                            if (n == id)
                            {
                                if (!self.Contains(id)) self.Add(id);
                                continue;
                            }
                            bool back = false;
                            foreach (Connector r in other.AllRefs)
                            {
                                if (r.Owner != null && r.Owner.Id.ToLong() == id && r.Id == c.Id) back = true;
                            }
                            if (!back && oneSided.Count < 50) oneSided.Add($"{id}:{c.Id} → {n}:{other.Id}");
                            (long, long) key = id < n ? (id, n) : (n, id);
                            (int, int) link = id < n ? (c.Id, other.Id) : (other.Id, c.Id);
                            if (!pairs.TryGetValue(key, out HashSet<(int, int)>? links)) pairs[key] = links = new HashSet<(int, int)>();
                            links.Add(link);
                            PipeSystemType a = c.PipeSystemType, b = other.PipeSystemType;
                            if (a != b && Classified(a) && Classified(b))
                            {
                                string kind = a < b ? $"{a}↔{b}" : $"{b}↔{a}";
                                if (!mismatch.TryGetValue(kind, out List<string>? list)) mismatch[kind] = list = new List<string>();
                                if (list.Count < 200) list.Add(id < n ? $"{id} ↔ {n}" : $"{n} ↔ {id}");
                            }
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        // Недоступный коннектор пропускаем
                    }
                }
            }
            List<(long, long)> doubled = pairs.Where(x => x.Value.Count > 1).Select(x => x.Key).ToList();
            yield return $"коннекторы сети: проходное оборудование (связанные коннекторы) {linked.Count}, несколько присоединений на коннекторе {multiRef.Count}, "
                + $"односторонних связей {oneSided.Count}, пар с двумя и более соединениями {doubled.Count}, соединений с собой {self.Count}, "
                + $"стыков разных классификаций {mismatch.Sum(x => x.Value.Distinct().Count())}, не торцевых {notEnd}; "
                + "открытые концы: " + (open.Count == 0 ? "нет" : string.Join(", ", open.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}")));
            foreach (long id in linked.Take(10)) yield return $"  проходное: {Describe(doc, id)}";
            foreach (long id in multiRef.Take(10)) yield return $"  несколько присоединений: {Describe(doc, id)}";
            foreach (string s in oneSided.Take(10)) yield return $"  односторонняя связь: {s}";
            foreach ((long a, long b) in doubled.Take(10)) yield return $"  два соединения: {Describe(doc, a)} ↔ {Describe(doc, b)}";
            foreach (long id in self.Take(10)) yield return $"  соединён с собой: {Describe(doc, id)}";
            foreach (KeyValuePair<string, List<string>> m in mismatch)
            {
                List<string> distinct = m.Value.Distinct().ToList();
                yield return $"  стык {m.Key}: {distinct.Count}, например {string.Join("; ", distinct.Take(5))}";
            }
        }

        private static bool Classified(PipeSystemType type) =>
            type != PipeSystemType.UndefinedSystemType && type != PipeSystemType.Fitting && type != PipeSystemType.Global && type != PipeSystemType.OtherPipe;

        private static Dictionary<long, List<string>> WarningsByElement(Document doc)
        {
            var result = new Dictionary<long, List<string>>();
            try
            {
                foreach (FailureMessage w in doc.GetWarnings())
                {
                    string text = w.GetDescriptionText() ?? "?";
                    foreach (ElementId id in w.GetFailingElements())
                    {
                        long key = id.ToLong();
                        if (!result.TryGetValue(key, out List<string>? list)) result[key] = list = new List<string>();
                        list.Add(text);
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Без предупреждений — меньше сведений, анализ идёт дальше
            }
            return result;
        }

        private static string SystemNames(Document doc, Element e)
        {
            HashSet<long> ids = Connectors.SystemsOf(e);
            return ids.Count == 0 ? "без системы" : string.Join("+", ids.OrderBy(x => x).Select(id => "«" + (doc.GetElement(id.ToElementId())?.Name ?? "?") + "»"));
        }

        private static string Describe(Document doc, long id)
        {
            Element? e = doc.GetElement(id.ToElementId());
            if (e == null) return $"{id} (нет в модели)";
            string name = e is FamilyInstance fi && fi.Symbol != null ? $"{fi.Symbol.FamilyName}: {fi.Symbol.Name}" : e.Name;
            return $"{e.Category?.Name ?? "?"} {id} «{name}»";
        }

        private static string Short(string text) => text.Length <= 90 ? text : text.Substring(0, 90) + "…";

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
