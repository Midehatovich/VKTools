using System.Collections.Generic;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>0.17.1 «Позиции»: своя нумерация с 1 в каждой спецификации (решение 2026-11-24), подномера вложенных.</summary>
    public class PositionsTests
    {
        private static PositionRow Row(string current = "", bool skip = false, int? parent = null) =>
            new PositionRow(new long[] { 1 }, current) { Skip = skip, Parent = parent };

        [Fact]
        public void Нумерация_с_1_по_порядку_строк_без_пропусков()
        {
            var rows = new[] { Row("40"), Row("39"), Row(""), Row("‹варианты›") };
            Assert.Equal(new[] { "1", "2", "3", "4" }, PositionNumbering.Number(rows, true));
        }

        [Fact]
        public void Строка_с_восклицательным_знаком_номер_не_занимает()
        {
            var rows = new[] { Row(), Row(skip: true), Row() };
            Assert.Equal(new string?[] { "1", null, "2" }, PositionNumbering.Number(rows, true));
        }

        [Fact]
        public void Детали_затвора_получают_номер_родителя_и_подномер()
        {
            // Фланцы и шпильки стоят в таблице выше затвора: номер затвора всё равно 1-й по порядку основных строк
            var rows = new[] { Row(parent: 2), Row(parent: 2), Row(), Row() };
            Assert.Equal(new[] { "1.1", "1.2", "1", "2" }, PositionNumbering.Number(rows, true));
        }

        [Fact]
        public void Без_подномеров_все_строки_подряд()
        {
            var rows = new[] { Row(parent: 2), Row(parent: 2), Row(), Row() };
            Assert.Equal(new[] { "1", "2", "3", "4" }, PositionNumbering.Number(rows, false));
        }

        [Fact]
        public void Родитель_без_номера_деталь_нумеруется_как_обычная_строка()
        {
            var rows = new[] { Row(skip: true), Row(parent: 0), Row() };
            Assert.Equal(new string?[] { null, "1", "2" }, PositionNumbering.Number(rows, true));
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
