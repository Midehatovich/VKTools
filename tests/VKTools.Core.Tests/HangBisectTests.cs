using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Mep;
using Xunit;

namespace VKTools.Core.Tests
{
    public class HangBisectTests
    {
        /// <summary>Прогон до конца: hangs(удалённые) — завис ли Revit без них. Каждый шаг сохраняется и читается заново, как после перезапуска.</summary>
        private static HangBisect Run(IEnumerable<long> ids, Func<HashSet<long>, bool> hangs, int limit = 100)
        {
            HangBisect bisect = HangBisect.Start("doc|1|copy", ids);
            for (int i = 0; i < limit && !bisect.Finished; i++)
            {
                List<long> test = bisect.NextTest();
                bisect.Begin(test);
                bisect = HangBisect.Load(bisect.Save())!;
                Assert.NotNull(bisect.Pending);
                if (hangs(new HashSet<long>(test))) bisect.Hung();
                else bisect.Returned(0.2, cured: true);
                bisect = HangBisect.Load(bisect.Save())!;
            }
            Assert.True(bisect.Finished);
            return bisect;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(7)]
        [InlineData(500)]
        [InlineData(821)]
        public void FindsTheSingleCulprit(long culprit)
        {
            HangBisect done = Run(Enumerable.Range(1, 821).Select(x => (long)x), deleted => !deleted.Contains(culprit));
            Assert.Equal(HangBisectPhase.Found, done.Phase);
            Assert.Equal(culprit, done.Culprit);
            Assert.True(done.Step <= 13, $"шагов {done.Step}");
            Assert.Contains("контроль", done.History[0]);
        }

        [Fact]
        public void PairCulpritEndsOnOneOfThem()
        {
            // Висит, только пока в модели оба: удаление любого лечит
            HangBisect done = Run(Enumerable.Range(1, 100).Select(x => (long)x), deleted => !deleted.Contains(30) && !deleted.Contains(70));
            Assert.Equal(HangBisectPhase.Found, done.Phase);
            Assert.Contains(done.Culprit, new long[] { 30, 70 });
        }

        [Fact]
        public void HangThatNeedsManyElementsHasNoSingleCulprit()
        {
            // Висит, пока удалено меньше 10 элементов: один элемент не лечит
            HangBisect done = Run(Enumerable.Range(1, 100).Select(x => (long)x), deleted => deleted.Count < 10);
            Assert.Equal(HangBisectPhase.NoSingleCulprit, done.Phase);
        }

        [Fact]
        public void NoHangAtAllIsNotBlamedOnTheFirstElement()
        {
            // Revit отвечает всегда: без контроля каждый шаг «лечил» бы и поиск указал бы на элемент 1
            HangBisect done = Run(Enumerable.Range(1, 100).Select(x => (long)x), deleted => false);
            Assert.Equal(HangBisectPhase.NotReproduced, done.Phase);
            Assert.Equal(0, done.Culprit);
            Assert.Equal(1, done.Step);
        }

        [Fact]
        public void SlowControlCountsAsReproduced()
        {
            HangBisect bisect = HangBisect.Start("k", new long[] { 1, 2 });
            Assert.Empty(bisect.NextTest());
            bisect.Begin(bisect.NextTest());
            bisect = HangBisect.Load(bisect.Save())!;
            Assert.NotNull(bisect.Pending);
            bisect.Returned(80, cured: false);
            Assert.Equal(HangBisectPhase.Searching, bisect.Phase);
            Assert.Equal(new long[] { 1, 2 }, bisect.Candidates);
        }

        [Fact]
        public void SingleSuspectIsVerifiedAfterControl()
        {
            HangBisect bisect = HangBisect.Start("k", new long[] { 9 });
            Assert.Equal(HangBisectPhase.Control, bisect.Phase);
            bisect.Begin(bisect.NextTest());
            bisect.Hung();
            Assert.Equal(HangBisectPhase.Verifying, bisect.Phase);
            Assert.Equal(new long[] { 9 }, bisect.NextTest());
        }

        [Fact]
        public void SlowReturnCountsAsNotCured()
        {
            HangBisect bisect = HangBisect.Start("k", new long[] { 1, 2, 3, 4 });
            bisect.Begin(bisect.NextTest());
            bisect.Hung();
            List<long> test = bisect.NextTest();
            Assert.Equal(new long[] { 1, 2 }, test);
            bisect.Begin(test);
            bisect.Returned(95, cured: false);
            Assert.Equal(new long[] { 3, 4 }, bisect.Candidates);
            Assert.Contains("медленно", bisect.History.Last());
        }

        [Fact]
        public void ForgetsDeletedElements()
        {
            HangBisect bisect = HangBisect.Start("k", new long[] { 1, 2, 3 });
            bisect.Forget(new HashSet<long> { 2 });
            Assert.Equal(new long[] { 1, 3 }, bisect.Candidates);
            bisect.Forget(new HashSet<long> { 1, 3 });
            Assert.Equal(HangBisectPhase.NoSingleCulprit, bisect.Phase);
        }

        [Fact]
        public void RejectsForeignText()
        {
            Assert.Null(HangBisect.Load("что-то другое"));
            Assert.Null(HangBisect.Load("VKTools.HangBisect 1\nkey\tk\ncandidates\t1,x"));
        }

        [Fact]
        public void StepsLeftEstimate()
        {
            Assert.Equal(12, HangBisect.Start("k", Enumerable.Range(1, 821).Select(x => (long)x)).StepsLeft);
            Assert.Equal(2, HangBisect.Start("k", new long[] { 5 }).StepsLeft);
        }
    }
}
