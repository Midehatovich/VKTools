using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VKTools.Core.Calc;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using VKTools.Core.Text;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>Р3 «Расчётные позиции»: таблицы и расчёт — порт spec_calc_items v0.4.1.</summary>
    public class CalcTests
    {
        private static CalcDefaults BuiltIn()
        {
            string dir = RepoPaths.ConfigDirectory();
            return new CalcDefaults
            {
                ResourcesText = File.ReadAllText(Path.Combine(dir, CalcTables.ResourcesFile)),
                PipeRulesText = File.ReadAllText(Path.Combine(dir, CalcTables.PipeRulesFile)),
                SupportsText = File.ReadAllText(Path.Combine(dir, CalcTables.SupportsFile)),
            };
        }

        private static CalcRuleSet Rules(CalcOptions? options = null)
        {
            CalcRuleSet set = (options ?? CalcOptions.FromDefaults(BuiltIn())).Parse();
            Assert.Empty(set.Issues);
            return set;
        }

        private static PipeSample Pipe(long id, string system, string name, double dn, double od, double length, bool insulated = false) => new PipeSample
        {
            Id = id,
            System = system,
            Name = name,
            Segment = "Сталь",
            DnMm = dn,
            OdMm = od,
            LengthM = length,
            Insulated = insulated,
        };

        private static CalcResult Run(CalcRuleSet set, params PipeSample[] pipes) =>
            CalcEngine.Calculate(new[] { new KeyValuePair<string, IReadOnlyList<PipeSample>>("Спец", pipes) }, set.Resources, set.Rules, set.Supports, new CalcEngineOptions());

        [Fact]
        public void Встроенные_таблицы_разбираются_и_группы_по_порядку()
        {
            CalcRuleSet set = Rules();
            Assert.Equal(new[] { "PRIMER", "PAINT", "METAL" }, set.Resources.Select(r => r.Code));
            Assert.Equal(new[] { "9.1", "9.2", "9.3" }, set.Resources.Select(r => r.Group));
            Assert.Equal(new[] { "ADSK_Марка", "ADSK_Техническая характеристика", "ADSK_Завод-изготовитель" }, set.ExtraParameters);
            Assert.Equal(12, set.Supports.Count);
            Assert.Equal(7, set.Rules.Count);
            Assert.Empty(set.Rules.Single(r => r.NameMask == "*НПВХ*").Codes);
        }

        [Fact]
        public void Окраска_по_площади_с_запасом_и_округлением_вверх()
        {
            CalcRuleSet set = Rules();
            // ВГП DN25 (33,5 мм) 10 м: S = π · 0,0335 · 10 = 1,0524 м²
            CalcResult r = Run(set, Pipe(1, "В1", "Труба стальная водогазопроводная ø25x3,2", 25, 33.5, 10));
            CalcTarget primer = r.Targets.Single(t => t.Code == "PRIMER");
            Assert.Equal(Math.PI * 0.0335 * 10, primer.Base, 6);
            Assert.Equal(primer.Base * 0.08 * 1 * 1.1, primer.Quantity, 9);
            Assert.Equal(0.1, primer.Auto, 6);                                  // 0,0926 → 0,1
            CalcTarget paint = r.Targets.Single(t => t.Code == "PAINT");
            Assert.Equal(0.4, paint.Auto, 6);                                   // 1,0524 · 0,14 · 2 · 1,1 = 0,324 → 0,4
            Assert.Equal("Спец|В1|PAINT", paint.Key);
            Assert.Contains("S=1,052 м²", paint.Basis);
        }

        [Fact]
        public void Оцинкованные_не_окрашиваются_но_крепятся()
        {
            CalcResult r = Run(Rules(), Pipe(1, "В1", "Труба стальная водогазопроводная оцинкованная ø20x2,8", 20, 26.8, 7));
            Assert.Equal(new[] { "METAL" }, r.Targets.Select(t => t.Code));
            // DN20 неизол.: шаг 3 м → ⌈7/3⌉ = 3 шт × 0,36 кг = 1,08 кг → округление 0,1 → 1,1
            Assert.Equal(1.08, r.Targets[0].Base, 9);
            Assert.Equal(1.1, r.Targets[0].Auto, 6);
        }

        [Fact]
        public void Крепления_по_сумме_длин_группы_а_не_по_кускам()
        {
            // Три куска по 1 м DN50 изол.: ΣL = 3 м / шаг 3 м = 1 крепление (по кускам было бы 3)
            CalcResult r = Run(Rules(),
                Pipe(1, "Т3", "Труба стальная электросварная ø57x3,5", 50, 57, 1, insulated: true),
                Pipe(2, "Т3", "Труба стальная электросварная ø57x3,5", 50, 57, 1, insulated: true),
                Pipe(3, "Т3", "Труба стальная электросварная ø57x3,5", 50, 57, 1, insulated: true));
            CalcTarget metal = r.Targets.Single(t => t.Code == "METAL");
            Assert.Equal(2.43, metal.Base, 9);
            Assert.Contains("⌈3/3⌉=1×2,43", metal.Basis);
        }

        [Fact]
        public void Изолированные_и_голые_считаются_отдельно_DN65_по_строке_таблицы()
        {
            CalcResult r = Run(Rules(),
                Pipe(1, "В1", "Труба стальная электросварная ø76x3,5", 65, 76, 10, insulated: true),
                Pipe(2, "В1", "Труба стальная электросварная ø76x3,5", 65, 76, 10));
            CalcTarget metal = r.Targets.Single(t => t.Code == "METAL");
            // изол.: ⌈10/4⌉ = 3 × 3,4 = 10,2; неизол.: ⌈10/6⌉ = 2 × 1,17 = 2,34
            Assert.Equal(12.54, metal.Base, 9);
            Assert.Equal(2, r.Systems.Single().SupportLines.Count);
        }

        [Fact]
        public void Короткий_обрезок_без_крепления_и_нет_DN_в_таблице()
        {
            CalcResult r = Run(Rules(),
                Pipe(1, "В1", "Труба стальная электросварная ø57x3,5", 50, 57, 0.2),
                Pipe(2, "В1", "Труба стальная электросварная ø219x6", 200, 219, 5));
            Assert.DoesNotContain(r.Targets, t => t.Code == "METAL");
            Assert.Contains(r.Issues.Items, i => i.Message.Contains("нет DN200"));
            Assert.Contains(r.Issues.Items, i => i.Message.Contains("крепление не начислено"));
        }

        [Fact]
        public void Нет_правила_в_отчёт_правило_с_минусом_молча()
        {
            CalcResult r = Run(Rules(),
                Pipe(1, "К1", "Труба НПВХ 110", 100, 110, 5),
                Pipe(2, "К1", "Труба медная 15", 15, 15, 5));
            Assert.Empty(r.Targets);
            Assert.Equal(1, r.PipesWithoutRule);
            CalcIssue issue = Assert.Single(r.Issues.Items, i => i.Message.StartsWith("нет правила", StringComparison.Ordinal));
            Assert.Equal(new long[] { 2 }, issue.Ids);
        }

        [Fact]
        public void Поле_фильтра_берётся_самое_частое_у_труб_системы()
        {
            PipeSample a = Pipe(1, "В1", "Труба стальная электросварная ø57x3,5", 50, 57, 5);
            PipeSample b = Pipe(2, "В1", "Труба стальная электросварная ø57x3,5", 50, 57, 5);
            PipeSample c = Pipe(3, "В1", "Труба стальная электросварная ø57x3,5", 50, 57, 5);
            a.Copy["ADSK_Этаж"] = "01";
            b.Copy["ADSK_Этаж"] = "02";
            c.Copy["ADSK_Этаж"] = "02";
            CalcResult r = Run(Rules(), a, b, c);
            Assert.All(r.Targets, t => Assert.Equal("02", t.Copy["ADSK_Этаж"]));
        }

        [Fact]
        public void Похожие_имена_систем_предупреждение()
        {
            CalcResult r = Run(Rules(),
                Pipe(1, "В1", "Труба стальная электросварная ø57x3,5", 50, 57, 5),
                Pipe(2, "B1", "Труба стальная электросварная ø57x3,5", 50, 57, 5));
            Assert.Contains(r.Issues.Items, i => i.Message.StartsWith("похожие имена систем", StringComparison.Ordinal));
            Assert.Equal(2, r.Targets.Select(t => t.System).Distinct().Count());
        }

        [Theory]
        [InlineData(5.0000000001, 0.1, 5.0)]
        [InlineData(5.01, 0.1, 5.1)]
        [InlineData(2.4, 1, 3)]
        [InlineData(2.4, 0, 2.4)]
        public void Округление_вверх(double value, double step, double expected) =>
            Assert.Equal(expected, CalcEngine.RoundUp(value, step), 9);

        [Theory]
        [InlineData("", 5.7, "Auto", 5.7)]
        [InlineData("6", 5.7, "Manual", 6)]
        [InlineData("0", 5.7, "Manual", 0)]
        [InlineData("шесть", 5.7, "Error", 5.7)]
        [InlineData("-1", 5.7, "Error", 5.7)]
        public void Ручное_значение(string manual, double auto, string mode, double final)
        {
            (double f, CalcMode m, string _) = CalcManual.Resolve(auto, manual);
            Assert.Equal(mode, m.ToString());
            Assert.Equal(final, f, 9);
        }

        [Fact]
        public void Ручное_больше_чем_на_20_процентов_с_пометкой()
        {
            Assert.Contains("на 75 %", CalcManual.Resolve(4, "7").Note);
            Assert.Equal(string.Empty, CalcManual.Resolve(4, "4,5").Note);
        }

        [Fact]
        public void Файл_Dynamo_без_schema_импортируется_с_доп_колонкой()
        {
            string text = "﻿# комментарий\nКод;Наименование;Ед_изм;База;Расход;Слоев;Запас;Порядок;Округление;Основание;ADSK_Завод-изготовитель\n"
                + "METAL;Металлоизделия;кг;sup_kg;1;1;1;920;0,1;калькулятор;Альтеза\n";
            var issues = new List<ConfigIssue>();
            CsvTable? table = CalcTables.ParseText(text, CalcTables.Resources, "импорт", issues);
            Assert.NotNull(table);
            List<CalcResource> resources = CalcTables.ParseResources(table!, "импорт", "9", issues);
            Assert.Empty(issues);
            Assert.Equal("Альтеза", resources.Single().Extras.Single(e => e.Key == "ADSK_Завод-изготовитель").Value);
            Assert.Equal("9.1", resources.Single().Group);
        }

        [Fact]
        public void Ошибки_таблиц_с_номером_строки()
        {
            var options = CalcOptions.FromDefaults(BuiltIn());
            options.PipeRulesText = "#schema=1\nСегмент;Наименование;Ресурсы\n*;*сталь*;PRIMER+GLUE\n*;*;-\n";
            CalcRuleSet set = options.Parse();
            ConfigIssue issue = Assert.Single(set.Issues);
            Assert.Equal(3, issue.Line);
            Assert.Contains("GLUE", issue.Message);
        }

        [Fact]
        public void Настройки_в_модели_только_отличия_от_умолчания()
        {
            CalcDefaults defaults = BuiltIn();
            var settings = new SpecProjectSettings();
            CalcOptions options = CalcOptions.FromDefaults(defaults);
            options.StoreTo(settings, defaults, "user", DateTime.Now);
            Assert.Empty(settings.Values);

            options.Schedules.Add("Спецификация В1. Надземная часть");
            options.ResourcesText = options.ResourcesText.Replace("метр", "метр").Replace("920;0,1;калькулятор slpl.ru/node/218, шаг - СП 73.13330.2016 табл. 2;;;",
                "920;0,1;калькулятор slpl.ru/node/218, шаг - СП 73.13330.2016 табл. 2;;;Альтеза");
            options.StoreTo(settings, defaults, "user", DateTime.Now);
            Assert.NotNull(settings.Get("calc.resources"));
            Assert.Null(settings.Get("calc.pipeRules"));
            Assert.Null(settings.Get("calc.supports"));

            var issues = new List<ConfigIssue>();
            CalcOptions loaded = CalcOptions.Load(settings, defaults, issues, out string? blocked);
            Assert.Null(blocked);
            Assert.Empty(issues);
            Assert.True(loaded.SavedInProject);
            Assert.Equal(options.Signature(), loaded.Signature());
            Assert.Equal("Альтеза", loaded.Parse().Resources.Single(r => r.Code == "METAL").Extras.Single(e => e.Key == "ADSK_Завод-изготовитель").Value);

            // Вернули как было — раздел удаляется целиком
            CalcOptions.FromDefaults(defaults).StoreTo(settings, defaults, "user", DateTime.Now);
            Assert.Empty(settings.Values);
        }

        [Fact]
        public void Комментарии_и_пробелы_не_делают_таблицу_другой()
        {
            CalcDefaults defaults = BuiltIn();
            CalcOptions options = CalcOptions.FromDefaults(defaults);
            options.SupportsText = CalcOptions.Normalize(defaults.SupportsText, "крепления");
            Assert.True(options.IsDefault(defaults));
        }

        [Fact]
        public void Ключ_позиции_и_область()
        {
            Assert.Equal("Спец|В1|PRIMER", CalcEngine.MakeKey("Спец", "В1", "PRIMER"));
            Assert.Equal("Спец", CalcEngine.KeyScope("Спец|В1|PRIMER"));
            Assert.Null(CalcEngine.KeyScope("ЗАПАС|Спец"));
        }
    }
}
