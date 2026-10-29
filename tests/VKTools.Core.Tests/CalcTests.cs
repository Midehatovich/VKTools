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
                CordText = File.ReadAllText(Path.Combine(dir, CalcTables.CordFile)),
                ReserveText = File.ReadAllText(Path.Combine(dir, CalcTables.ReserveFile)),
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
            CalcEngine.Calculate(new[] { new KeyValuePair<string, IReadOnlyList<PipeSample>>("Спец", pipes) }, set.Resources, set.Rules, set.Supports, Options(set));

        private static CalcEngineOptions Options(CalcRuleSet set) => new CalcEngineOptions { Cord = set.Cord, CordAddMm = set.CordAddMm, CordPitchMm = set.CordPitchMm };

        [Fact]
        public void Встроенные_таблицы_разбираются_и_группы_по_порядку()
        {
            CalcRuleSet set = Rules();
            Assert.Equal(new[] { "PRIMER", "PAINT", "METAL", "CORD6", "CORD8", "CORD10", "CORD12H", "CORD12S" }, set.Resources.Select(r => r.Code));
            Assert.Equal(new[] { "9.1", "9.2", "9.3", "9.4", "9.5", "9.6", "9.7", "9.8" }, set.Resources.Select(r => r.Group));
            Assert.Equal(4, set.Cord.Count);
            Assert.Equal(new[] { "ADSK_Марка", "ADSK_Техническая характеристика", "ADSK_Завод-изготовитель" }, set.ExtraParameters);
            Assert.Equal(12, set.Supports.Count);
            Assert.Equal(29, set.Rules.Count);
            PipeRule pvc = set.Rules.Single(r => r.NameMask == "*НПВХ*" && r.Matches("Сталь", "Труба НПВХ 110", 110, false));
            Assert.Equal(new[] { "METAL" }, pvc.Codes);
            Assert.Equal(1.1, pvc.StepHorizontal);
            Assert.Equal(2, pvc.StepVertical);
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
            CalcOptions options = CalcOptions.FromDefaults(BuiltIn());
            options.PipeRulesText = "#schema=1\nСегмент;Наименование;Ресурсы\n*;*НПВХ*;-\n";
            CalcResult r = Run(Rules(options),
                Pipe(1, "К1", "Труба НПВХ 110", 100, 110, 5),
                Pipe(2, "К1", "Труба медная 15", 15, 15, 5));
            Assert.Empty(r.Targets);
            Assert.Equal(1, r.PipesWithoutRule);
            CalcIssue issue = Assert.Single(r.Issues.Items, i => i.Message.StartsWith("нет правила", StringComparison.Ordinal));
            Assert.Equal(new long[] { 2 }, issue.Ids);
        }

        [Fact]
        public void Пластик_шаг_по_правилу_масса_по_ближайшему_DN()
        {
            // PP Dн110 10 м гор. и 4 м верт.: шаг 1,1 и 2 м по правилу; масса — строка DN100 неизол. 2,23 кг
            PipeSample h = Pipe(1, "К1", "Труба полипропиленовая канализационная 110", 110, 110, 10);
            PipeSample v = Pipe(2, "К1", "Труба полипропиленовая канализационная 110", 110, 110, 4);
            v.Vertical = true;
            CalcResult r = Run(Rules(), h, v);
            CalcTarget metal = r.Targets.Single(t => t.Code == "METAL");
            Assert.Equal((10 + 2) * 2.23, metal.Base, 6);                      // ⌈10/1,1⌉ = 10, ⌈4/2⌉ = 2
            Assert.Contains(r.Systems.Single().SupportLines, l => l.Contains("шаг по правилу", StringComparison.Ordinal));
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

            // Шнур во всех гильзах — отличие от умолчания (только наружные стены и фундамент)
            Assert.True(loaded.SleeveOuterOnly);
            options.SleeveOuterOnly = false;
            options.StoreTo(settings, defaults, "user", DateTime.Now);
            Assert.Equal("1", settings.Get("calc.sleeveAll"));
            Assert.False(CalcOptions.Load(settings, defaults, issues, out _).SleeveOuterOnly);

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
        [Fact]
        public void Шнур_как_в_Excel_по_длине_гильз_и_DN()
        {
            CalcRuleSet set = Rules();
            // Excel, строка ф25: Dнар 33,7, гильзы 20 000 мм → (33,7 + 10) · 3,14 / 1000 × 2000 = 274,4 м (у нас π: 274,58); ×4 из Excel ведущий снял
            PipeSample a = Pipe(1, "В1", "Труба стальная водогазопроводная ø25x3,2", 25, 33.7, 10);
            a.SleeveMm = 12000;
            a.Sleeves = 48;
            PipeSample b = Pipe(2, "В1", "Труба стальная водогазопроводная ø25x3,2", 25, 33.7, 10);
            b.SleeveMm = 8000;
            b.Sleeves = 32;
            PipeSample c = Pipe(3, "В1", "Труба стальная водогазопроводная ø50x3,5", 50, 60.3, 5);
            c.SleeveMm = 3300;
            CalcResult r = Run(set, a, b, c);
            CalcTarget cord8 = r.Targets.Single(t => t.Code == "CORD8");
            Assert.Equal(Math.PI * 43.7 / 1000 * 2000, cord8.Base, 6);
            Assert.Equal(cord8.Base, cord8.Quantity, 6);
            Assert.Equal(274.6, cord8.Auto, 6);                                  // 274,58 → вверх до 0,1
            Assert.Contains("Lгильз", set.Resources.Single(x => x.Code == "CORD8").BasisText);
            CalcTarget soft = r.Targets.Single(t => t.Code == "CORD12S");
            Assert.Equal(Math.PI * 70.3 / 1000 * 330, soft.Quantity, 6);        // Excel ф50: 72,8 м при 3,14 (без ×4)
            Assert.Equal(80, r.Systems.Single().Sleeves);
            Assert.Contains(r.Systems.Single().CordLines, l => l.StartsWith("CORD8 DN25", StringComparison.Ordinal));
        }

        [Fact]
        public void Шнур_DN_вне_таблицы_в_отчёт_и_шнур_в_правиле_трубы_ошибка()
        {
            CalcRuleSet set = Rules();
            PipeSample p = Pipe(7, "К1", "Труба стальная", 10, 17, 3);
            p.SleeveMm = 250;
            CalcResult r = Run(set, p);
            Assert.DoesNotContain(r.Targets, t => t.Resource.Base == CalcBase.Cord);
            Assert.Contains(r.Issues.Items, i => i.Severity == Severity.Warning && i.Message.Contains("таблице шнура нет", StringComparison.Ordinal) && i.Ids.Contains(7));

            CalcDefaults d = BuiltIn();
            CalcOptions options = CalcOptions.FromDefaults(d);
            options.PipeRulesText = "#schema=1\nСегмент;Ресурсы\n*;PRIMER,CORD8\n";
            options.CordText = "#schema=1\nDN_от;DN_до;Ресурс\n15;25;CORD6\n20;32;CORD8\n40;50;PRIMER\n";
            options.CordPitch = "0";
            List<ConfigIssue> errors = options.Parse().Issues.Where(i => i.Severity == Severity.Error).ToList();
            Assert.Contains(errors, e => e.FileName == "Правила для труб" && e.Message.Contains("по гильзам", StringComparison.Ordinal));
            Assert.Contains(errors, e => e.FileName == CalcOptions.SleevesTab && e.Message.Contains("пересекается", StringComparison.Ordinal));
            Assert.Contains(errors, e => e.FileName == CalcOptions.SleevesTab && e.Message.Contains("нужна cord", StringComparison.Ordinal));
            Assert.Contains(errors, e => e.FileName == CalcOptions.SleevesTab && e.Message.Contains("шаг витка", StringComparison.Ordinal));
        }
        [Theory]
        [InlineData(1, 2)]
        [InlineData(2, 3)]
        [InlineData(4, 5)]
        [InlineData(5, 6)]
        [InlineData(7, 9)]
        [InlineData(10, 12)]
        public void Запас_20_процентов_вверх_по_строке_как_в_ClampReserve(double n, double total)
        {
            CalcRuleSet set = Rules();
            ReserveRow row = set.Reserve.First(r => r.Matches("Влж_Чугунные безраструбные_Хомуты", "Rapid"));
            Assert.Equal(total, row.Total(n), 6);
        }

        [Fact]
        public void Запас_на_Rapid_Kralle_CV_грувлок_и_ошибки_таблицы()
        {
            CalcRuleSet set = Rules();
            Assert.Equal(4, set.Reserve.Count);
            Assert.Contains(set.Reserve, r => r.Matches("Влж_Чугунные безраструбные_Хомуты", "CV"));
            Assert.Contains(set.Reserve, r => r.Matches("Влж_Муфта грувлочная", "Жесткая Ду100"));
            Assert.DoesNotContain(set.Reserve, r => r.Matches("Влж_Чугунные безраструбные_Хомуты", "Хомут-переход"));
            Assert.DoesNotContain(set.Reserve, r => r.Matches("Влж_Отвод", "Rapid"));

            CalcOptions options = CalcOptions.FromDefaults(BuiltIn());
            options.ReserveText = "#schema=1\nСемейство;Тип;Запас_%;Округление\n;Rapid;20;1\n*Хомут*;;0;1\n*Хомут*;;20;0\n*Муфта*;;10;\n";
            CalcRuleSet parsed = options.Parse();
            Assert.Equal(3, parsed.Issues.Count(i => i.Severity == Severity.Error && i.FileName == CalcOptions.ReserveTab));
            ReserveRow muff = Assert.Single(parsed.Reserve);
            Assert.Equal(1, muff.Round);
            Assert.True(muff.Matches("Влж_Муфта ремонтная", "DN100"));
        }
    }
}
