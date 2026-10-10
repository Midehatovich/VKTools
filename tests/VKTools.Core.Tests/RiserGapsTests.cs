using System.Linq;
using VKTools.Core.Mep;
using Xunit;

namespace VKTools.Core.Tests
{
    public class RiserGapsTests
    {
        private static OpenEnd Up(long id, double x, double y, double z, long type = 1) =>
            new OpenEnd(id, 1, x, y, z, 1) { SystemTypeId = type, DiameterMm = 50 };

        private static OpenEnd Down(long id, double x, double y, double z, long type = 1, double d = 50) =>
            new OpenEnd(id, 2, x, y, z, -1) { SystemTypeId = type, DiameterMm = d };

        [Fact]
        public void CoincidentEndsAreAGap()
        {
            RiserGap gap = Assert.Single(RiserGaps.Find(new[] { Up(1, 1000, 2000, 3000), Down(2, 1000, 2000, 3000) }));
            Assert.Equal(1, gap.Lower.ElementId);
            Assert.Equal(2, gap.Upper.ElementId);
            Assert.Equal(0, gap.GapMm, 3);
            Assert.True(gap.SameDiameter);
        }

        [Fact]
        public void SmallOffsetInPlanIsAccepted_LargeIsNot()
        {
            Assert.Single(RiserGaps.Find(new[] { Up(1, 0, 0, 0), Down(2, 15, 0, 50) }));
            Assert.Empty(RiserGaps.Find(new[] { Up(1, 0, 0, 0), Down(2, 25, 0, 50) }));
        }

        [Fact]
        public void OverlapAndGapLimits()
        {
            Assert.Single(RiserGaps.Find(new[] { Up(1, 0, 0, 1000), Down(2, 0, 0, 800) }));   // заходят на 200 мм
            Assert.Empty(RiserGaps.Find(new[] { Up(1, 0, 0, 1000), Down(2, 0, 0, 500) }));    // заходят на 500 мм
            Assert.Single(RiserGaps.Find(new[] { Up(1, 0, 0, 0), Down(2, 0, 0, 3000) }));     // пропущен кусок этажа
            Assert.Empty(RiserGaps.Find(new[] { Up(1, 0, 0, 0), Down(2, 0, 0, 4000) }));
        }

        [Fact]
        public void DifferentSystemTypesAreNotPaired()
        {
            Assert.Empty(RiserGaps.Find(new[] { Up(1, 0, 0, 0, type: 1), Down(2, 0, 0, 0, type: 2) }));
        }

        [Fact]
        public void BothEndsOfOneVerticalPipeAreNotAGap()
        {
            // Короткая вертикальная труба: верх смотрит вверх, низ — вниз, ниже верха на 100 мм
            Assert.Empty(RiserGaps.Find(new[] { Up(1, 0, 0, 100), Down(1, 0, 0, 0) }));
        }

        [Fact]
        public void HorizontalEndsAreIgnored()
        {
            var a = new OpenEnd(1, 1, 0, 0, 0, 0) { SystemTypeId = 1 };
            var b = new OpenEnd(2, 1, 0, 0, 0, 0) { SystemTypeId = 1 };
            Assert.Empty(RiserGaps.Find(new[] { a, b }));
        }

        [Fact]
        public void EachEndJoinsOnlyTheNearestPartner()
        {
            // Один стояк, три этажа: 0–3000 (верх открыт), 3000–6000 (оба конца открыты), 6000– (низ открыт)
            var ends = new[]
            {
                Up(1, 0, 0, 3000), Down(2, 0, 0, 3000),
                Up(2, 0, 0, 6000), Down(3, 0, 0, 6000),
            };
            var gaps = RiserGaps.Find(ends);
            Assert.Equal(2, gaps.Count);
            Assert.Contains(gaps, g => g.Lower.ElementId == 1 && g.Upper.ElementId == 2);
            Assert.Contains(gaps, g => g.Lower.ElementId == 2 && g.Upper.ElementId == 3);
        }

        [Fact]
        public void DifferentDiameterIsReported()
        {
            RiserGap gap = Assert.Single(RiserGaps.Find(new[] { Up(1, 0, 0, 0), Down(2, 0, 0, 0, d: 40) }));
            Assert.False(gap.SameDiameter);
        }

        [Fact]
        public void PointsNearCellBorderAreFound()
        {
            // Соседние ячейки сетки поиска
            Assert.Single(RiserGaps.Find(new[] { Up(1, 19.9, 19.9, 0), Down(2, 20.1, 20.1, 0) }));
        }
    }
}
