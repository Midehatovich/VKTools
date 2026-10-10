using VKTools.Core.Mep;
using Xunit;

namespace VKTools.Core.Tests
{
    public class MergeSkipListTests
    {
        [Theory]
        [InlineData(SkipDecision.None, false, false)]
        [InlineData(SkipDecision.None, true, true)]
        [InlineData(SkipDecision.Skip, false, true)]
        [InlineData(SkipDecision.Skip, true, true)]
        [InlineData(SkipDecision.Allow, true, false)]
        public void HungCopyIsSkippedUntilAllowed(SkipDecision stored, bool hung, bool skipped)
        {
            Assert.Equal(skipped, MergeSkipList.IsSkipped(stored, hung));
        }

        [Theory]
        [InlineData(true, false, SkipDecision.Skip)]
        [InlineData(false, false, SkipDecision.None)]
        [InlineData(false, true, SkipDecision.Allow)]
        [InlineData(true, true, SkipDecision.None)]
        public void DecisionRestoresWhatTheUserSees(bool skip, bool hung, SkipDecision expected)
        {
            SkipDecision decision = MergeSkipList.DecisionFor(skip, hung);
            Assert.Equal(expected, decision);
            Assert.Equal(skip, MergeSkipList.IsSkipped(decision, hung));
        }

        [Fact]
        public void RoundTripKeepsDecisionsByNames()
        {
            var list = new MergeSkipList();
            list.Set("КрМа3_Р_ВК", "К1", "К1 1", SkipDecision.Skip);
            list.Set("КрМа3_Р_ВК", "К1", "К1 2", SkipDecision.Allow);
            list.Set("Другая\tмодель", "В1", "В1 3", SkipDecision.Skip);
            MergeSkipList read = MergeSkipList.Parse(list.Format());
            Assert.Equal(3, read.Count);
            Assert.Equal(SkipDecision.Skip, read.Get("КрМа3_Р_ВК", "К1", "К1 1"));
            Assert.Equal(SkipDecision.Allow, read.Get("КрМа3_Р_ВК", "К1", "К1 2"));
            Assert.Equal(SkipDecision.Skip, read.Get("Другая модель", "В1", "В1 3"));
            Assert.Equal(SkipDecision.None, read.Get("КрМа3_Р_ВК", "К1.1", "К1.1 1"));
        }

        [Fact]
        public void NoneRemovesTheEntry()
        {
            var list = new MergeSkipList();
            list.Set("m", "К1", "К1 1", SkipDecision.Skip);
            list.Set("m", "К1", "К1 1", SkipDecision.None);
            Assert.Equal(0, list.Count);
        }

        [Fact]
        public void ForeignOrBrokenTextGivesEmptyOrPartialList()
        {
            Assert.Equal(0, MergeSkipList.Parse(null).Count);
            Assert.Equal(0, MergeSkipList.Parse("что-то другое\nm\tК1\tК1 1\tSkip").Count);
            MergeSkipList partial = MergeSkipList.Parse("VKTools.MergeSkip 1\nm\tК1\tК1 1\tSkip\nm\tК1\tбез решения\nm\tК1\tК1 3\tМусор\nm\tК1\tК1 4\t7");
            Assert.Equal(1, partial.Count);
            Assert.Equal(SkipDecision.Skip, partial.Get("m", "К1", "К1 1"));
        }
    }
}
