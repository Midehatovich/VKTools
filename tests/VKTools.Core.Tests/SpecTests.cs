using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using VKTools.Core.Text;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>Р2: правила группирования, комплекты шкафов (GOLDEN-CASES GRP, KIT), план записи.</summary>
    public class SpecTests
    {
        private static GroupingTable BuiltInGrouping()
        {
            LoadedTable loaded = new ConfigLoader(new ConfigLocations(RepoPaths.ConfigDirectory(), null)).Load(SpecRuleTables.Grouping);
            var issues = new List<ConfigIssue>();
            GroupingTable table = SpecRuleTables.ParseGrouping(loaded.Table!, issues);
            Assert.Empty(issues);
            return table;
        }

        [Fact]
        public void GRP_001_002_003_группы_по_категориям()
        {
            GroupingTable t = BuiltInGrouping();
            Assert.Equal("0", t.GroupFor("OST_Sprinklers"));
            Assert.Equal("5", t.GroupFor("OST_PipeCurves"));
            Assert.Equal("5", t.GroupFor("OST_FlexPipeCurves"));
            Assert.Equal("6", t.GroupFor("OST_PipeInsulations"));
            Assert.Equal("7", t.GroupFor("OST_PipeFitting"));
            Assert.Equal("4", t.GroupFor("OST_PipeAccessory"));
            Assert.Equal("8", t.GroupFor("OST_Walls"));
            Assert.Equal("8", t.GroupFor(null));
            Assert.False(t.IsListed("OST_Walls"));
            Assert.True(t.IsListed("OST_PipeCurves"));
        }

        [Fact]
        public void Группирование_без_строки_звёздочки_не_трогает_прочие()
        {
            var issues = new List<ConfigIssue>();
            GroupingTable t = SpecRuleTables.ParseGrouping(CsvTable.Parse("#schema=1\nКатегория;Группа\nOST_PipeCurves;5\nOST_PipeCurves;9\n;3\n"), issues);
            Assert.Null(t.GroupFor("OST_Walls"));
            Assert.Equal("5", t.GroupFor("OST_PipeCurves"));
            Assert.Equal(2, issues.Count);
        }

        [Fact]
        public void Параметры_комплектов_встроенные_и_неполные()
        {
            LoadedTable loaded = new ConfigLoader(new ConfigLocations(RepoPaths.ConfigDirectory(), null)).Load(SpecRuleTables.KitParameters);
            var issues = new List<ConfigIssue>();
            KitParameterNames? names = SpecRuleTables.ParseKitParameters(loaded.Table!, issues);
            Assert.Empty(issues);
            Assert.Equal("Комплект_Номер", names!.CabinetNumber);
            Assert.Equal("Сортировка_Целое", names.ComponentFlag);
            Assert.Equal("ADSK_Позиция_Комплект", names.ComponentKey);

            issues.Clear();
            Assert.Null(SpecRuleTables.ParseKitParameters(CsvTable.Parse("#schema=1\nРоль;Параметр\nНомер комплекта шкафа;K\nЧто-то;X\n"), issues));
            Assert.Equal(2, issues.Count);
        }

        [Fact]
        public void Вложенные_параметры_по_умолчанию_и_повтор()
        {
            LoadedTable loaded = new ConfigLoader(new ConfigLocations(RepoPaths.ConfigDirectory(), null)).Load(SpecRuleTables.NestedParameters);
            ParsedRules<ParameterRef> parsed = SpecRuleTables.ParseNestedParameters(loaded.Table!);
            Assert.Empty(parsed.Issues);
            Assert.Equal(new[] { "ADSK_Этаж", "LSR_Имя системы", "LSR_Тип системы" }, parsed.Items.Select(p => p.Name));
            Assert.Equal(KnownParameters.AdskLevel.Guid, parsed.Items[0].Guid);
            Assert.Equal(KnownParameters.LsrSystemName.Guid, parsed.Items[1].Guid);

            ParsedRules<ParameterRef> twice = SpecRuleTables.ParseNestedParameters(CsvTable.Parse(
                "#schema=1\nПараметр;GUID\nA;9eabf56c-a6cd-4b5c-a9d0-e9223e19ea3f\nB;9eabf56c-a6cd-4b5c-a9d0-e9223e19ea3f\n"));
            Assert.Single(twice.Items);
            Assert.Single(twice.Issues);
        }

        [Theory]
        [InlineData(null, true)]
        [InlineData("", true)]
        [InlineData("   ", true)]
        [InlineData("  \t", true)]
        [InlineData("03 этаж", false)]
        public void NEST_002_пустое_значение(string? value, bool blank) => Assert.Equal(blank, ValueText.IsBlank(value));

        // --- Комплекты шкафов ---

        private static KitElement Cabinet(long id, double? k, string type = "Шкаф : ШПК-310") =>
            new KitElement { Id = id, TopId = id, Depth = 0, Number = k, TopNumber = k, TypeLabel = type };

        private static KitElement Component(long id, long top, double? key, double? topK, int depth = 1, string type = "Начинка : Кран") =>
            new KitElement { Id = id, TopId = top, Depth = depth, IsComponent = true, Number = key, TopNumber = topK, TypeLabel = type };

        [Fact]
        public void KIT_001_002_шкаф_и_комплектующее()
        {
            KitPlan plan = KitGrouping.Plan(new[] { Cabinet(1, 3), Component(2, 1, 3.01, 3), Component(3, 1, 3.02, 3, depth: 2) }, top => "1");
            Assert.Equal("1.000000003.00", plan.Values[1]);
            Assert.Equal("1.000000003.01", plan.Values[2]);
            Assert.Equal("1.000000003.02", plan.Values[3]);
            Assert.Empty(plan.Issues);
            Assert.Equal(1, plan.Cabinets);
            Assert.Equal(2, plan.Components);
        }

        [Fact]
        public void KIT_003_ключ_не_совпадает_с_K()
        {
            KitPlan plan = KitGrouping.Plan(new[] { Cabinet(1, 3), Component(2, 1, 4.01, 3) }, top => "1");
            Assert.False(plan.Values.ContainsKey(2));
            KitIssue issue = Assert.Single(plan.Issues);
            Assert.Contains("не совпадает с K шкафа", issue.Message);
            Assert.Equal(new long[] { 2 }, issue.Ids);
        }

        [Fact]
        public void KIT_004_комплектующее_без_шкафа()
        {
            var alone = new KitElement { Id = 5, TopId = 5, Depth = 0, IsComponent = true, Number = null, TypeLabel = "Начинка : Кран" };
            KitPlan plan = KitGrouping.Plan(new[] { alone }, top => "4");
            Assert.Empty(plan.Values);
            Assert.Contains("без шкафа", Assert.Single(plan.Issues).Message);
        }

        [Fact]
        public void KIT_005_один_K_у_разных_типоразмеров()
        {
            KitPlan plan = KitGrouping.Plan(new[] { Cabinet(1, 3, "Шкаф : ШПК-310"), Cabinet(2, 3, "Шкаф : ШПК-315"), Cabinet(3, 3, "Шкаф : ШПК-310") }, top => "1");
            Assert.Equal(3, plan.Values.Count);
            KitIssue issue = Assert.Single(plan.Issues);
            Assert.StartsWith("ПРОВЕРЬ K", issue.Message);
            Assert.Equal(new long[] { 1, 2, 3 }, issue.Ids.OrderBy(x => x));
        }

        [Fact]
        public void Не_шкаф_и_не_комплектующее_не_трогаются()
        {
            var plain = new KitElement { Id = 7, TopId = 7, Depth = 0, Number = null, TypeLabel = "Кран : DN15" };
            var nestedPlain = new KitElement { Id = 8, TopId = 7, Depth = 1, IsComponent = false, TypeLabel = "УГО" };
            KitPlan plan = KitGrouping.Plan(new[] { plain, nestedPlain, Cabinet(9, 0) }, top => "1");
            Assert.Empty(plan.Values);
            Assert.Empty(plan.Issues);
        }

        [Fact]
        public void Ключ_0_x_и_нет_группы_шкафа()
        {
            KitPlan plan = KitGrouping.Plan(new[] { Cabinet(1, 3), Component(2, 1, 0.01, 3), Component(3, 1, null, 3), Cabinet(4, 5), Component(5, 4, 5.01, 5) },
                top => top == 4 ? null : "1");
            Assert.True(plan.Values.ContainsKey(1));
            Assert.False(plan.Values.ContainsKey(2));
            Assert.False(plan.Values.ContainsKey(4));
            Assert.False(plan.Values.ContainsKey(5));
            Assert.Contains(plan.Issues, i => i.Message.Contains("ключ комплекта 0") && i.Ids.Count == 2);
            Assert.Contains(plan.Issues, i => i.Message.Contains("нет группы") && i.Ids.SequenceEqual(new long[] { 4 }));
        }

        [Theory]
        [InlineData(3.01, 3.0, true)]
        [InlineData(3.0, 3.0, true)]
        [InlineData(2.9999999, 3.0, true)]
        [InlineData(4.01, 3.0, false)]
        public void Совпадение_ключа_с_K(double key, double k, bool matches) => Assert.Equal(matches, KitGrouping.KeyMatches(key, k));

        [Fact]
        public void Значение_с_большим_ключом_не_обрезается() => Assert.Equal("1.000000012.10", KitGrouping.Value("1", 12.1));

        // --- План ---

        [Fact]
        public void План_конфликт_и_повтор()
        {
            var plan = new ParamPlan(new[] { SpecModule.Grouping, SpecModule.Nested });
            Assert.Equal(new[] { SpecModule.Nested, SpecModule.Grouping }, plan.Modules);
            Guid g = KnownParameters.AdskGrouping.Guid;
            Assert.True(plan.AddChange(new ParamChange(SpecModule.Nested, 1, "ADSK_Группирование", g, "", "5")));
            Assert.False(plan.AddChange(new ParamChange(SpecModule.Grouping, 1, "ADSK_Группирование", g, "", "5")));
            Assert.Empty(plan.Issues);
            Assert.False(plan.AddChange(new ParamChange(SpecModule.Grouping, 1, "ADSK_Группирование", g, "", "7")));
            PlanIssue issue = Assert.Single(plan.Issues);
            Assert.Equal(Severity.Error, issue.Severity);
            Assert.Single(plan.Changes);
            Assert.Equal("5", plan.Changes[0].New);
        }

        [Fact]
        public void План_замечания_схлопываются_по_семейству()
        {
            var plan = new ParamPlan(new[] { SpecModule.Nested });
            plan.AddIssue(SpecModule.Nested, Severity.Info, "ADSK_Этаж", "у вложенного нет параметра", "Кран", 10);
            plan.AddIssue(SpecModule.Nested, Severity.Info, "ADSK_Этаж", "у вложенного нет параметра", "Кран", 11);
            plan.AddIssue(SpecModule.Nested, Severity.Info, "ADSK_Этаж", "у вложенного нет параметра", "Муфта", 12);
            Assert.Equal(2, plan.Issues.Count);
            Assert.Equal(2, plan.Issues[0].Count);
            Assert.Equal(new long[] { 10, 11 }, plan.Issues[0].Ids);
            Assert.Equal(3, plan.IssueCount(SpecModule.Nested, Severity.Info));
            plan.CountUnchanged(SpecModule.Nested, 5);
            Assert.Equal(5, plan.Unchanged(SpecModule.Nested));
            Assert.Equal(0, plan.Unchanged(SpecModule.Grouping));
        }
    }
}
