using System.Linq;
using VKTools.Core.Mep;
using Xunit;

namespace VKTools.Core.Tests
{
    public class NetworkLoopsTests
    {
        private static double Flat(long a, long b) => 0;

        [Fact]
        public void TreeHasNoLoops()
        {
            Assert.Empty(NetworkLoops.Find(new long[] { 1, 2, 3, 4 }, new[] { (1L, 2L), (2L, 3L), (2L, 4L) }, Flat));
        }

        [Fact]
        public void TriangleIsOneLoopWithAllElements()
        {
            NetworkLoop loop = Assert.Single(NetworkLoops.Find(new long[] { 1, 2, 3, 4 }, new[] { (1L, 2L), (2L, 3L), (3L, 1L), (3L, 4L) }, Flat));
            Assert.Equal(new long[] { 1, 2, 3 }, loop.Ids.OrderBy(x => x));
        }

        [Fact]
        public void BreaksAtHeaviestConnection()
        {
            // Два стояка 1–2–3 и 4–5–6, внизу 1–4, наверху перемычка 3–6 (вес — высота)
            var edges = new[] { (1L, 2L), (2L, 3L), (4L, 5L), (5L, 6L), (1L, 4L), (3L, 6L) };
            double Height(long a, long b) => System.Math.Max(a % 3 == 0 ? 3 : a % 3, b % 3 == 0 ? 3 : b % 3) + (a % 3 == 0 && b % 3 == 0 ? 1 : 0);
            NetworkLoop loop = Assert.Single(NetworkLoops.Find(Enumerable.Range(1, 6).Select(x => (long)x), edges, Height));
            Assert.Equal((3L, 6L), (loop.BreakA, loop.BreakB));
            Assert.Equal(6, loop.Ids.Count);
        }

        [Fact]
        public void TwoLoopsSharingAnEdgeNeedTwoBreaks()
        {
            // «Тета»: 1 и 4 соединены тремя путями
            var edges = new[] { (1L, 2L), (2L, 4L), (1L, 3L), (3L, 4L), (1L, 5L), (5L, 4L) };
            var loops = NetworkLoops.Find(Enumerable.Range(1, 5).Select(x => (long)x), edges, Flat);
            Assert.Equal(2, loops.Count);
            Assert.NotEqual((loops[0].BreakA, loops[0].BreakB), (loops[1].BreakA, loops[1].BreakB));
        }

        [Fact]
        public void DuplicateAndForeignEdgesAreIgnored()
        {
            Assert.Empty(NetworkLoops.Find(new long[] { 1, 2 }, new[] { (1L, 2L), (2L, 1L), (2L, 99L), (1L, 1L) }, Flat));
        }
    }
}
