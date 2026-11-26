using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>0.17.1 «Позиции»: своя нумерация с 1 в каждой спецификации (решение 2026-11-24), подномера вложенных.</summary>
    public class PositionsTests
    {
        // Строка r — один элемент с id = 100 + r (у строки деталей — несколько: 100 + r, 1000 + r…)
        private static PositionRow Row(int r, bool skip = false, string group = "", params long[] extra)
        {
            var ids = new List<long> { 100 + r };
            ids.AddRange(extra);
            return new PositionRow(ids, string.Empty) { Skip = skip, Group = group };
        }

        private static List<PositionUnit> Layout(PositionRow[] rows, Dictionary<long, int> parents, bool sub = true) =>
            PositionNumbering.Layout(rows, id => parents.TryGetValue(id, out int p) ? p : (int?)null, sub);

        private static string[] Numbers(List<PositionUnit> units) => units.Select(u => u.Number ?? "-").ToArray();

        [Fact]
        public void Нумерация_с_1_по_порядку_строк_без_пропусков()
        {
            var rows = new[] { Row(0), Row(1), Row(2), Row(3) };
            List<PositionUnit> units = Layout(rows, new Dictionary<long, int>());
            Assert.Equal(new[] { "1", "2", "3", "4" }, Numbers(units));
            Assert.Equal(new[] { "0001", "0002", "0003", "0004" }, units.Select(u => u.Key));
        }

        [Fact]
        public void Строка_с_восклицательным_знаком_номер_не_занимает_но_место_держит()
        {
            var rows = new[] { Row(0), Row(1, skip: true), Row(2) };
            List<PositionUnit> units = Layout(rows, new Dictionary<long, int>());
            Assert.Equal(new[] { "1", "-", "2" }, Numbers(units));
            Assert.Equal("0002", units[1].Key);
        }

        [Fact]
        public void Детали_встают_сразу_под_родителем_через_точку()
        {
            // Фланцы (строка 0) и шпильки (строка 3) затвора (строка 1): по категории фланцы выше затвора, шпильки ниже отвода
            var rows = new[] { Row(0), Row(1), Row(2), Row(3) };
            var parents = new Dictionary<long, int> { [100] = 1, [103] = 1 };
            List<PositionUnit> units = Layout(rows, parents);
            Assert.Equal(new[] { 1, 0, 3, 2 }, units.Select(u => u.Source));
            Assert.Equal(new[] { "1", "1.1", "1.2", "2" }, Numbers(units));
            Assert.Equal(new[] { "0001", "0001.01", "0001.02", "0002" }, units.Select(u => u.Key));
        }

        [Fact]
        public void По_порядку_детали_остаются_где_стоят_со_своим_номером()
        {
            var rows = new[] { Row(0), Row(1), Row(2), Row(3) };
            var parents = new Dictionary<long, int> { [100] = 1, [103] = 1 };
            List<PositionUnit> units = Layout(rows, parents, sub: false);
            Assert.Equal(new[] { 0, 1, 2, 3 }, units.Select(u => u.Source));
            Assert.Equal(new[] { "1", "2", "3", "4" }, Numbers(units));
        }

        [Fact]
        public void Одинаковые_фланцы_двух_затворов_делятся_на_две_строки()
        {
            // Строка 0 — фланцы: элемент 100 от затвора в строке 1, элемент 500 — от затвора в строке 2
            var rows = new[] { Row(0, extra: 500), Row(1), Row(2) };
            var parents = new Dictionary<long, int> { [100] = 1, [500] = 2 };
            List<PositionUnit> units = Layout(rows, parents);
            Assert.Equal(new[] { "1", "1.1", "2", "2.1" }, Numbers(units));
            Assert.Equal(new long[] { 100 }, units[1].Ids);
            Assert.Equal(new long[] { 500 }, units[3].Ids);
        }

        [Fact]
        public void Родитель_из_другого_блока_системы_деталь_нумеруется_обычно()
        {
            var rows = new[] { Row(0, group: ""), Row(1, group: "В21.2") };
            var parents = new Dictionary<long, int> { [101] = 0 };
            Assert.Equal(new[] { "1", "2" }, Numbers(Layout(rows, parents)));
        }

        [Fact]
        public void Родитель_без_номера_деталь_нумеруется_как_обычная_строка()
        {
            var rows = new[] { Row(0, skip: true), Row(1), Row(2) };
            var parents = new Dictionary<long, int> { [101] = 0 };
            Assert.Equal(new[] { "-", "1", "2" }, Numbers(Layout(rows, parents)));
        }

        [Fact]
        public void Строка_где_не_все_элементы_детали_остаётся_обычной()
        {
            var rows = new[] { Row(0, extra: 500), Row(1) };
            var parents = new Dictionary<long, int> { [100] = 1 };
            Assert.Equal(new[] { "1", "2" }, Numbers(Layout(rows, parents)));
        }

        [Fact]
        public void Общее_значение_строки_или_варианты()
        {
            Assert.Equal("5", PositionNumbering.Common(new[] { "5", " 5" }));
            Assert.Equal(PositionNumbering.Variants, PositionNumbering.Common(new[] { "5", "6" }));
            Assert.Equal(string.Empty, PositionNumbering.Common(System.Array.Empty<string>()));
        }

        [Fact]
        public void Таблица_спецификаций_первая_подходящая_строка()
        {
            var issues = new List<ConfigIssue>();
            List<PositionRule> rules = PositionSettings.ParseRules(
                "Спецификация;Нумерация;Примечание\n*Тихонова*;не трогать;общая\n*;с 1;\n", issues);
            Assert.Empty(issues);
            Assert.False(PositionSettings.Match(rules, "О_ВК_Спека Тихонова Насосная")!.Number);
            Assert.True(PositionSettings.Match(rules, "Спецификация систем пожаротушения ниже 0,000")!.Number);
        }

        [Fact]
        public void Неизвестный_режим_ошибка()
        {
            var issues = new List<ConfigIssue>();
            PositionSettings.ParseRules("Спецификация;Нумерация\n*;с 2\n", issues);
            Assert.Contains(issues, i => i.Severity == Checks.Severity.Error);
        }

        [Fact]
        public void Настройка_сохраняется_в_модели_и_читается_обратно()
        {
            var options = new SpecOptions(SpecModule.Positions);
            options.Positions.SubNumbers = false;
            options.Positions.Rules = "Спецификация;Нумерация\n*ниже*;с 1\n";
            var defaults = new SpecDefaults();
            var stored = new SpecProjectSettings();
            options.StoreTo(stored, defaults, "Ilyasov", new System.DateTime(2026, 11, 26));
            SpecOptions loaded = SpecOptions.Load(SpecModule.Positions, stored, defaults, new List<ConfigIssue>(), out string? blocked);
            Assert.Null(blocked);
            Assert.True(loaded.SavedInProject);
            Assert.False(loaded.Positions.SubNumbers);
            Assert.Equal(options.Positions.Signature(), loaded.Positions.Signature());
        }

        [Fact]
        public void По_умолчанию_все_оформленные_с_1()
        {
            List<PositionRule> rules = PositionSettings.ParseRules(PositionSettings.DefaultRules, new List<ConfigIssue>());
            Assert.True(Assert.Single(rules).Number);
            Assert.True(new SpecOptions(SpecModule.Positions).IsDefault(new SpecDefaults()));
        }
    }
}
