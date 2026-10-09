using System;
using System.Collections.Generic;
using System.Linq;

namespace VKTools.Core.Mep
{
    /// <summary>Кольцо сети: элементы по кругу и соединение, которое предлагается разорвать.</summary>
    public sealed class NetworkLoop
    {
        public NetworkLoop(List<long> ids, long breakA, long breakB)
        {
            Ids = ids;
            BreakA = breakA;
            BreakB = breakB;
        }

        /// <summary>Элементы кольца по порядку обхода.</summary>
        public List<long> Ids { get; }

        public long BreakA { get; }

        public long BreakB { get; }
    }

    /// <summary>
    /// Кольца в сети трубопровода (граф: элементы — вершины, соединения коннекторов — рёбра).
    /// На КрМа3 MEPSystem.Add зависал только на сети с кольцами («К1», 2 кольца, 15+ мин), все сети без колец
    /// добавлялись за доли секунды. Плагин не запускает Revit на таких сетях и показывает, где разорвать (D-021).
    /// </summary>
    public static class NetworkLoops
    {
        /// <summary>
        /// Независимые кольца по одному: находим кольцо, выбираем в нём соединение с наибольшим весом
        /// (для Revit — самая высокая точка: обычно вентиляционная перемычка), мысленно убираем его и ищем дальше.
        /// Разорвав все предложенные соединения, получаем сеть без колец; связность сети при этом не теряется.
        /// </summary>
        public static List<NetworkLoop> Find(IEnumerable<long> nodes, IEnumerable<(long A, long B)> edges, Func<long, long, double> weight, int limit = 50)
        {
            var nodeSet = new HashSet<long>(nodes);
            List<(long A, long B)> all = edges
                .Where(e => e.A != e.B && nodeSet.Contains(e.A) && nodeSet.Contains(e.B))
                .Select(e => e.A < e.B ? (e.A, e.B) : (e.B, e.A))
                .Distinct()
                .OrderBy(e => e.Item1).ThenBy(e => e.Item2)
                .ToList();
            var removed = new HashSet<(long, long)>();
            var loops = new List<NetworkLoop>();
            while (loops.Count < limit)
            {
                (long A, long B)? closing = null;
                var parent = new Dictionary<long, long>();
                long Find(long x)
                {
                    long root = x;
                    while (parent.TryGetValue(root, out long p) && p != root) root = p;
                    while (x != root && parent.TryGetValue(x, out long next))
                    {
                        parent[x] = root;
                        x = next;
                    }
                    return root;
                }
                var tree = new Dictionary<long, List<long>>();
                foreach ((long a, long b) in all)
                {
                    if (removed.Contains((a, b))) continue;
                    long ra = Find(a), rb = Find(b);
                    if (ra == rb)
                    {
                        closing = (a, b);
                        break;
                    }
                    parent[ra] = rb;
                    Link(tree, a, b);
                    Link(tree, b, a);
                }
                if (closing == null) break;

                List<long> path = PathInTree(tree, closing.Value.A, closing.Value.B);
                var cycleEdges = new List<(long, long)>();
                for (int i = 0; i + 1 < path.Count; i++) cycleEdges.Add(Normal(path[i], path[i + 1]));
                cycleEdges.Add(Normal(closing.Value.A, closing.Value.B));
                (long, long) chosen = cycleEdges
                    .OrderByDescending(e => weight(e.Item1, e.Item2))
                    .ThenBy(e => e.Item1).ThenBy(e => e.Item2)
                    .First();
                removed.Add(chosen);
                loops.Add(new NetworkLoop(path, chosen.Item1, chosen.Item2));
            }
            return loops;
        }

        private static (long, long) Normal(long a, long b) => a < b ? (a, b) : (b, a);

        private static void Link(Dictionary<long, List<long>> tree, long from, long to)
        {
            if (!tree.TryGetValue(from, out List<long>? list)) tree[from] = list = new List<long>();
            list.Add(to);
        }

        /// <summary>Путь между двумя вершинами одного дерева (обход в ширину).</summary>
        private static List<long> PathInTree(Dictionary<long, List<long>> tree, long from, long to)
        {
            var previous = new Dictionary<long, long> { [from] = from };
            var queue = new Queue<long>();
            queue.Enqueue(from);
            while (queue.Count > 0)
            {
                long x = queue.Dequeue();
                if (x == to) break;
                if (!tree.TryGetValue(x, out List<long>? next)) continue;
                foreach (long n in next)
                {
                    if (previous.ContainsKey(n)) continue;
                    previous[n] = x;
                    queue.Enqueue(n);
                }
            }
            var path = new List<long>();
            if (!previous.ContainsKey(to)) return path;
            for (long x = to; ; x = previous[x])
            {
                path.Add(x);
                if (x == from) break;
            }
            path.Reverse();
            return path;
        }
    }
}
