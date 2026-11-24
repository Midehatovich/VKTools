using System;
using System.Collections.Generic;
using System.Linq;

namespace VKTools.Core.Spec
{
    /// <summary>Помещение АР для «Р_Номер помещения»: номер, имя, уровень АР.</summary>
    public sealed class RoomLabel
    {
        public RoomLabel(string number, string name, string level)
        {
            Number = (number ?? string.Empty).Trim();
            Name = (name ?? string.Empty).Trim();
            Level = (level ?? string.Empty).Trim();
        }

        public string Number { get; }

        public string Name { get; }

        public string Level { get; }

        /// <summary>Значение «Р_Номер помещения» (ставит <see cref="RoomKeys.Assign"/>).</summary>
        public string Key { get; internal set; } = string.Empty;

        /// <summary>Для названия спецификации: «2 «Помещение насосных станций»».</summary>
        public string Title => Number.Length == 0 ? "«" + Name + "»" : Name.Length == 0 ? Number : Number + " «" + Name + "»";
    }

    /// <summary>
    /// Значение «Р_Номер помещения». В АР номера часто повторяются (на Пушкареве нумерация по квартирам: «2» — у 20 помещений),
    /// а фильтр спецификации должен выбрать одно. Поэтому: номер, если он единственный во всех помещениях; иначе «номер имя»;
    /// повторяется и так — «номер имя (уровень АР)». Остались повторы — помещения неразличимы, вернутся в <see cref="Assign"/>.
    /// </summary>
    public static class RoomKeys
    {
        private static readonly StringComparer Ci = StringComparer.CurrentCultureIgnoreCase;

        public static List<string> Assign(IReadOnlyList<RoomLabel> rooms)
        {
            foreach (RoomLabel r in rooms) r.Key = r.Number.Length > 0 ? r.Number : r.Name;
            Refine(rooms, r => (r.Number + " " + r.Name).Trim());
            Refine(rooms, r => (r.Number + " " + r.Name).Trim() + (r.Level.Length > 0 ? " (" + r.Level + ")" : string.Empty));
            // Revit сравнивает в фильтре без учёта регистра — и повторы ищем так же
            return rooms.GroupBy(r => r.Key, Ci).Where(g => g.Count() > 1).Select(g => g.Key).OrderBy(k => k, Ci).ToList();
        }

        private static void Refine(IReadOnlyList<RoomLabel> rooms, Func<RoomLabel, string> longer)
        {
            var duplicated = new HashSet<string>(rooms.GroupBy(r => r.Key, Ci).Where(g => g.Count() > 1).Select(g => g.Key), Ci);
            foreach (RoomLabel r in rooms)
            {
                if (duplicated.Contains(r.Key)) r.Key = longer(r);
            }
        }
    }

    /// <summary>Плоская геометрия помещений: точка в контурах (чёт-нечет: внешний контур и отверстия колонн).</summary>
    public static class RoomGeometry
    {
        public static bool Contains(IEnumerable<IReadOnlyList<(double X, double Y)>> loops, double x, double y)
        {
            bool inside = false;
            foreach (IReadOnlyList<(double X, double Y)> loop in loops)
            {
                int n = loop.Count;
                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    (double xi, double yi) = loop[i];
                    (double xj, double yj) = loop[j];
                    if ((yi > y) != (yj > y) && x < ((xj - xi) * (y - yi) / (yj - yi)) + xi) inside = !inside;
                }
            }
            return inside;
        }

        /// <summary>
        /// Какое помещение по высоте из тех, в чей контур попала точка: внутри объёма — это; выше потолка (до <paramref name="ceilingZone"/>) —
        /// ближайшее снизу (трубы под перекрытием); ниже пола (до <paramref name="pitZone"/>) — ближайшее сверху (приямки). Иначе −1.
        /// </summary>
        public static int PickByHeight(IReadOnlyList<(double ZMin, double ZMax)> rooms, double z, double ceilingZone, double pitZone)
        {
            int best = -1;
            for (int i = 0; i < rooms.Count; i++)
            {
                if (z >= rooms[i].ZMin && z <= rooms[i].ZMax && (best < 0 || rooms[i].ZMax - rooms[i].ZMin < rooms[best].ZMax - rooms[best].ZMin)) best = i;
            }
            if (best >= 0) return best;
            double gap = double.MaxValue;
            for (int i = 0; i < rooms.Count; i++)
            {
                double d = z - rooms[i].ZMax;
                if (d > 0 && d <= ceilingZone && d < gap)
                {
                    gap = d;
                    best = i;
                }
            }
            if (best >= 0) return best;
            gap = double.MaxValue;
            for (int i = 0; i < rooms.Count; i++)
            {
                double d = rooms[i].ZMin - z;
                if (d > 0 && d <= pitZone && d < gap)
                {
                    gap = d;
                    best = i;
                }
            }
            return best;
        }
    }
}
