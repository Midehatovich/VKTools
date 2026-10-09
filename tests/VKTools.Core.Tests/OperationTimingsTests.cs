using System;
using VKTools.Core.Logging;
using VKTools.Core.Progress;
using Xunit;

namespace VKTools.Core.Tests
{
    public class OperationTimingsTests
    {
        [Fact]
        public void SummaryKeepsOrderAndCounts()
        {
            var t = new OperationTimings();
            t.Add("анализ", TimeSpan.FromSeconds(12.34));
            t.Add("удаление", TimeSpan.FromMilliseconds(200));
            t.Add("удаление", TimeSpan.FromMilliseconds(250));
            Assert.Equal("анализ 12 с; удаление ×2 0,5 с", t.Summary());
        }

        [Fact]
        public void MeasureAddsOnce()
        {
            var t = new OperationTimings();
            IDisposable scope = t.Measure("x");
            scope.Dispose();
            scope.Dispose();
            Assert.StartsWith("x ", t.Summary(), StringComparison.Ordinal);
            Assert.DoesNotContain("×", t.Summary(), StringComparison.Ordinal);
        }

        [Fact]
        public void EmptySummary() => Assert.Equal("—", new OperationTimings().Summary());

        [Fact]
        public void NullProgressNeverCancels() => NullProgress.Instance.ThrowIfCancelled();
    }
}
