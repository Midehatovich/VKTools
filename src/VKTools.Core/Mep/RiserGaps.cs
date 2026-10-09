using System;
using System.Collections.Generic;
using System.Linq;

namespace VKTools.Core.Mep
{
    /// <summary>Свободный (ни к чему не присоединённый) конец трубы, фитинга или арматуры. Координаты в мм.</summary>
    public sealed class OpenEnd
    {
        public OpenEnd(long elementId, int connectorId, double x, double y, double z, double directionZ)
        {
            ElementId = elementId;
            ConnectorId = connectorId;
            X = x;
            Y = y;
            Z = z;
            DirectionZ = directionZ;
        }

        public long ElementId { get; }

        public int ConnectorId { get; }

        public double X { get; }

        public double Y { get; }

        public double Z { get; }

        /// <summary>Z-составляющая направления коннектора наружу: +1 — конец смотрит вверх, −1 — вниз.</summary>
        public double DirectionZ { get; }

        /// <summary>Тип системы (PipingSystemType): разрыв ищем только между концами одного типа.</summary>
        public long SystemTypeId { get; set; } = -1;

        public string SystemTypeName { get; set; } = string.Empty;

        public string SystemName { get; set; } = string.Empty;

        public double DiameterMm { get; set; }

        public string Level { get; set; } = string.Empty;
    }

    public sealed class RiserGap
    {
        public RiserGap(OpenEnd lower, OpenEnd upper, double gapMm, double offsetMm)
        {
            Lower = lower;
            Upper = upper;
            GapMm = gapMm;
            OffsetMm = offsetMm;
        }

        /// <summary>Верхний конец нижнего куска стояка (смотрит вверх).</summary>
        public OpenEnd Lower { get; }

        /// <summary>Нижний конец верхнего куска (смотрит вниз).</summary>
        public OpenEnd Upper { get; }

        /// <summary>Расстояние по вертикали: &gt; 0 — зазор, &lt; 0 — куски заходят друг за друга.</summary>
        public double GapMm { get; }

        /// <summary>Смещение осей в плане.</summary>
        public double OffsetMm { get; }

        public bool SameDiameter => Math.Abs(Lower.DiameterMm - Upper.DiameterMm) < 0.5;
    }

    public sealed class RiserGapOptions
    {
        /// <summary>Насколько оси кусков могут разойтись в плане, мм.</summary>
        public double AxisToleranceMm { get; set; } = 20;

        /// <summary>Самый большой зазор по вертикали, мм (высота этажа с запасом: пропущенный кусок стояка).</summary>
        public double MaxGapMm { get; set; } = 3500;

        /// <summary>Насколько куски могут заходить друг за друга, мм.</summary>
        public double MaxOverlapMm { get; set; } = 300;

        /// <summary>Конец считается вертикальным, если |cos| угла с вертикалью не меньше этого.</summary>
        public double VerticalCosine { get; set; } = 0.985;
    }

    /// <summary>
    /// Разрывы стояков: верхний свободный конец нижнего куска и нижний свободный конец верхнего куска
    /// одного типа системы на одной вертикали. Каждый конец — не больше чем в одной паре: берётся ближайший.
    /// </summary>
    public static class RiserGaps
    {
        public static IReadOnlyList<RiserGap> Find(IEnumerable<OpenEnd> ends, RiserGapOptions? options = null)
        {
            options ??= new RiserGapOptions();
            List<OpenEnd> all = ends.ToList();
            List<OpenEnd> up = all.Where(e => e.DirectionZ >= options.VerticalCosine).ToList();
            List<OpenEnd> down = all.Where(e => e.DirectionZ <= -options.VerticalCosine).ToList();

            double cell = Math.Max(options.AxisToleranceMm, 1);
            var grid = new Dictionary<(long, long, long), List<OpenEnd>>();
            foreach (OpenEnd d in down)
            {
                var key = (d.SystemTypeId, Cell(d.X, cell), Cell(d.Y, cell));
                if (!grid.TryGetValue(key, out List<OpenEnd>? list))
                {
                    list = new List<OpenEnd>();
                    grid[key] = list;
                }
                list.Add(d);
            }

            var pairs = new List<RiserGap>();
            foreach (OpenEnd u in up)
            {
                long cx = Cell(u.X, cell), cy = Cell(u.Y, cell);
                for (long i = cx - 1; i <= cx + 1; i++)
                {
                    for (long j = cy - 1; j <= cy + 1; j++)
                    {
                        if (!grid.TryGetValue((u.SystemTypeId, i, j), out List<OpenEnd>? candidates)) continue;
                        foreach (OpenEnd d in candidates)
                        {
                            if (d.ElementId == u.ElementId) continue;
                            double offset = Math.Sqrt((d.X - u.X) * (d.X - u.X) + (d.Y - u.Y) * (d.Y - u.Y));
                            if (offset > options.AxisToleranceMm) continue;
                            double gap = d.Z - u.Z;
                            if (gap > options.MaxGapMm || gap < -options.MaxOverlapMm) continue;
                            pairs.Add(new RiserGap(u, d, gap, offset));
                        }
                    }
                }
            }

            // Ближайшие пары первыми: конец, уже вошедший в пару, второй раз не берём
            var usedUp = new HashSet<(long, int)>();
            var usedDown = new HashSet<(long, int)>();
            var result = new List<RiserGap>();
            foreach (RiserGap p in pairs.OrderBy(p => Math.Abs(p.GapMm)).ThenBy(p => p.OffsetMm))
            {
                if (usedUp.Contains((p.Lower.ElementId, p.Lower.ConnectorId)) || usedDown.Contains((p.Upper.ElementId, p.Upper.ConnectorId))) continue;
                usedUp.Add((p.Lower.ElementId, p.Lower.ConnectorId));
                usedDown.Add((p.Upper.ElementId, p.Upper.ConnectorId));
                result.Add(p);
            }
            return result.OrderBy(p => p.Lower.SystemTypeName, StringComparer.Ordinal)
                .ThenBy(p => Math.Round(p.Lower.X)).ThenBy(p => Math.Round(p.Lower.Y)).ThenBy(p => p.Lower.Z)
                .ToList();
        }

        private static long Cell(double value, double size) => (long)Math.Floor(value / size);
    }
}
