using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Mep;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>Случаи из test_PipingSystemMerge_names.py (эталон PipingSystemMerge v1.2) и пакетный режим.</summary>
    public class SystemNamesTests
    {
        [Theory]
        [InlineData("В1", "В1", null)]
        [InlineData("В1 1", "В1", 1)]
        [InlineData("В1 15", "В1", 15)]
        [InlineData("В1.1", "В1.1", null)]
        [InlineData("В1.1 2", "В1.1", 2)]
        [InlineData("В1.20", "В1.20", null)]
        [InlineData("К1н", "К1н", null)]
        [InlineData("К1н 1", "К1н", 1)]
        [InlineData("В2-П", "В2-П", null)]
        [InlineData("В10", "В10", null)]
        [InlineData("Т3.1", "Т3.1", null)]
        [InlineData("В1.2 3", "В1.2", 3)]
        [InlineData("В1  1", "В1  1", null)]
        [InlineData("В1 01", "В1 01", null)]
        [InlineData("В1 1", "В1 1", null)]
        [InlineData("В1\t1", "В1\t1", null)]
        [InlineData("В1 1 ", "В1 1 ", null)]
        [InlineData("В1_1", "В1_1", null)]
        [InlineData("В1-1", "В1-1", null)]
        [InlineData("В1 1 1", "В1 1", 1)]
        [InlineData("123", "123", null)]
        public void Split_MatchesReference(string name, string baseName, int? number)
        {
            SystemNameParts parts = SystemNames.Split(name);
            Assert.Equal(baseName, parts.BaseName);
            Assert.Equal(number, parts.Number);
        }

        [Theory]
        [InlineData("В1  1", "несколько пробелов")]
        [InlineData("В1 01", "ведущим нулём")]
        [InlineData("В1 1", "U+00A0")]
        [InlineData("В1 1 ", "пробелы в начале или конце")]
        [InlineData("", "пустое имя")]
        [InlineData("В1 99999999999", "слишком большой")]
        public void Split_ExplainsNonRevitNumbering(string name, string issuePart)
        {
            SystemNameParts parts = SystemNames.Split(name);
            Assert.False(parts.IsCopy);
            Assert.Contains(issuePart, parts.Issue);
        }

        [Theory]
        [InlineData("В1.2 3 1", "В1.2")]
        [InlineData("В1  04", "В1")]
        [InlineData("В1.2", "В1.2")]
        public void RootBase_StripsAllNumberTails(string name, string expected) =>
            Assert.Equal(expected, SystemNames.RootBase(name));

        // ---- Пример 1: В1 и В1.1 ----
        [Fact]
        public void Example1_DotIsPartOfName()
        {
            var s = Systems("В1", "В1 1", "В1 2", "В1 3", "В1.1", "В1.1 1", "В1.1 2");

            var r = Classify("В1", s);
            Assert.Equal(new[] { "В1 1", "В1 2", "В1 3" }, Names(r.Merge));
            Assert.Contains("В1.1 1", r.Ignored["В1.1"]);

            r = Classify("В1.1", s);
            Assert.Equal(new[] { "В1.1 1", "В1.1 2" }, Names(r.Merge));
            Assert.Contains("В1 1", r.Ignored["В1"]);
        }

        // ---- Пример 2: зоны В1.1/В1.2/В1.3 ----
        [Fact]
        public void Example2_ZonesMergeOnlyTheirOwnCopies()
        {
            var s = Systems("В1.1", "В1.1 1", "В1.1 2", "В1.2", "В1.2 1", "В1.2 2",
                "В1.3", "В1.3 1", "В1.3 2", "В1.20", "В1.2а", "В1.2-П", "В1");

            foreach (string zone in new[] { "В1.1", "В1.2", "В1.3" })
            {
                var r = Classify(zone, s);
                Assert.Equal(new[] { zone + " 1", zone + " 2" }, Names(r.Merge));
                Assert.Empty(r.Rejected);
                Assert.Empty(r.Pending);
            }

            var b = Classify("В1", s);
            Assert.Empty(b.Merge);
            Assert.Empty(b.Pending);
            Assert.True(b.Ignored.ContainsKey("В1.1") && b.Ignored.ContainsKey("В1.2") && b.Ignored.ContainsKey("В1.3"));
        }

        // ---- Пример 3: К1/К1.1/К1н, К1н другого типа ----
        [Fact]
        public void Example3_TypeNamedTargetNeedsConfirmation()
        {
            var s = new List<SystemRecord>
            {
                Rec("К1", 2, "К1"), Rec("К1 1", 2, "К1"), Rec("К1 2", 2, "К1"),
                Rec("К1.1", 2, "К1"), Rec("К1.1 1", 2, "К1"),
                Rec("К1н", 3, "К1н напорная", "Other"), Rec("К1н 1", 3, "К1н напорная", "Other"),
            };

            var r = Classify("К1", s);
            Assert.Empty(r.Merge);
            Assert.Equal(new[] { "К1 1", "К1 2" }, Names(r.Pending));
            Assert.True(r.Ignored.ContainsKey("К1н") && r.Ignored.ContainsKey("К1.1"));

            r = Classify("К1", s, trust: true);
            Assert.Equal(new[] { "К1 1", "К1 2" }, Names(r.Merge));

            Assert.Equal(new[] { "К1н 1" }, Names(Classify("К1н", s).Merge));
            Assert.Equal(new[] { "К1.1 1" }, Names(Classify("К1.1", s).Merge));
        }

        // ---- Вопрос 8: имя совпадает, тип или классификация другие ----
        [Fact]
        public void Question8_DifferentTypeOrClassificationIsRejected()
        {
            var s = new List<SystemRecord> { Rec("В1"), Rec("В1 1", typeId: 9, typeName: "Другой"), Rec("В1 2", cls: "Other") };
            var r = Classify("В1", s);
            Assert.Empty(r.Merge);
            Assert.Equal(new[] { "В1 1", "В1 2" }, Names(r.Rejected));
        }

        // ---- Почти совпадения ----
        [Fact]
        public void NearMatchesAreRejectedWithReason()
        {
            var s = Systems("В1", "В1  1", "В1 01", "B1 1", "в1 1", "В1 1 1", "В10", "В1а");
            var r = Classify("В1", s);
            string[] rejected = Names(r.Rejected);
            foreach (string n in new[] { "В1  1", "В1 01", "B1 1", "в1 1", "В1 1 1" }) Assert.Contains(n, rejected);
            Assert.Empty(r.Merge);
            Assert.True(r.Ignored.ContainsKey("В10") && r.Ignored.ContainsKey("В1а"));
            Assert.Contains("латиница", r.Candidates.Single(c => c.System.Name == "B1 1").Reason);
        }

        // ---- Вопрос 4: выбрана копия ----
        [Fact]
        public void Question4_SelectedCopyResolvesToItsBase()
        {
            var s = Systems("В1.2", "В1.2 4", "В1.3 1");
            Assert.Same(s[0], MergeGrouping.ResolveTarget(s[1], s, out _, out string? e1));
            Assert.Null(e1);

            Assert.Null(MergeGrouping.ResolveTarget(s[2], s, out _, out string? e2));
            Assert.NotNull(e2);

            var s4b = new List<SystemRecord> { Rec("В1.2", typeId: 5), Rec("В1.2 4") };
            Assert.Null(MergeGrouping.ResolveTarget(s4b[1], s4b, out _, out string? e3));
            Assert.NotNull(e3);
        }

        // ---- Пакетный режим ----
        [Fact]
        public void Batch_GroupsEveryBaseWithItsCopies()
        {
            var s = Systems("В1", "В1 1", "В1 2", "В1.1", "В1.1 1", "К1н", "Т3");
            var groups = MergeGrouping.BuildGroups(s);

            Assert.Equal(new[] { "В1", "В1.1" }, groups.Select(g => g.BaseName).ToArray());
            Assert.Equal(new[] { "В1 1", "В1 2" }, Names(groups[0].Candidates.Select(c => c.System)));
            Assert.All(groups, g => Assert.Null(g.Problem));
        }

        [Fact]
        public void Batch_CopiesWithoutBaseAreReportedNotMerged()
        {
            var s = Systems("В1", "В1 1", "В1.3 1", "В1.3 2");
            var groups = MergeGrouping.BuildGroups(s);

            MergeGroup orphan = groups.Single(g => g.BaseName == "В1.3");
            Assert.Null(orphan.Target);
            Assert.True(orphan.BaseMissing);
            Assert.NotNull(orphan.Problem);
            Assert.All(orphan.Candidates, c => Assert.Equal(CandidateStatus.Rejected, c.Status));
            Assert.Equal(2, orphan.Candidates.Count);
        }

        [Fact]
        public void Batch_DuplicateBaseIsAmbiguous()
        {
            var s = Systems("В1", "В1", "В1 1");
            MergeGroup g = Assert.Single(MergeGrouping.BuildGroups(s));
            Assert.Null(g.Target);
            Assert.False(g.BaseMissing);
            Assert.Contains("неоднозначна", g.Problem);
            Assert.Equal(CandidateStatus.Rejected, Assert.Single(g.Candidates).Status);
        }

        [Fact]
        public void Batch_EachSystemBelongsToOneGroupOnly()
        {
            var s = Systems("В1", "В1 1", "В1 1 1", "В1.1", "В1.1 1", "В1  2", "B1 3");
            var groups = MergeGrouping.BuildGroups(s);
            var all = groups.SelectMany(g => g.Candidates.Select(c => c.System.Id)).ToList();
            Assert.Equal(all.Count, all.Distinct().Count());
        }

        [Fact]
        public void Batch_NoCopiesNoGroups()
        {
            Assert.Empty(MergeGrouping.BuildGroups(Systems("В1", "В1.1", "К1", "Т3")));
        }

        private long _next;

        private SystemRecord Rec(string name, long typeId = 1, string typeName = "Хоз-питьевой", string cls = "DomesticColdWater") =>
            new SystemRecord(++_next, name, typeId, typeName, null, cls);

        private List<SystemRecord> Systems(params string[] names) => names.Select(n => Rec(n)).ToList();

        private static TargetClassification Classify(string target, List<SystemRecord> systems, bool trust = false) =>
            MergeGrouping.ClassifyForTarget(systems.First(r => r.Name == target), systems, trust);

        private static string[] Names(IEnumerable<SystemRecord> records) =>
            records.Select(r => r.Name).OrderBy(n => n, System.StringComparer.Ordinal).ToArray();
    }
}
