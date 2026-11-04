using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>Р5: «Запас труб» (эталон PipeQtyReserve v1.0.2) и «Имена труб» (эталон VgpNameDuS v1.1, стиль бюро — решение 2026-11-04).</summary>
    public class PipeReserveAndNamesTests
    {
        // ---------------------------------------------------------------- запас

        [Fact]
        public void Запас_по_умолчанию_1_1_трубам_и_изоляции()
        {
            var issues = new List<ConfigIssue>();
            List<ReserveRule> rules = PipeReserveSettings.ParseRules(PipeReserveSettings.DefaultTable, issues);
            Assert.Empty(issues);
            Assert.Equal(1.1, PipeReserveSettings.Match(rules, ReserveTarget.Pipe, "ADSK_Полиэтилен сшитый", "", "Т3")!.K);
            Assert.Equal(1.1, PipeReserveSettings.Match(rules, ReserveTarget.Insulation, "Цилиндры ROCKWOOL", "", "В1")!.K);
        }

        [Fact]
        public void Запас_свои_строки_системы_и_не_трогать()
        {
            var issues = new List<ConfigIssue>();
            List<ReserveRule> rules = PipeReserveSettings.ParseRules(
                "Категория;Маска типа;Системы;Запас k\n"
                + "Трубы;*;К1, К2;\n"
                + "Трубы;ПЭ100;;1,05\n"
                + "Изоляция труб;;;1,1\n"
                + "Трубы;*;;1,1\n", issues);
            Assert.Empty(issues);
            Assert.Null(PipeReserveSettings.Match(rules, ReserveTarget.Pipe, "ADSK_НПВХ_PN16", "", "к2")!.K);
            Assert.Equal(1.05, PipeReserveSettings.Match(rules, ReserveTarget.Pipe, "ADSK_ПЭ100 SDR11_Сварной", "", "В0")!.K);
            Assert.Equal(1.1, PipeReserveSettings.Match(rules, ReserveTarget.Pipe, "ADSK_Полипропилен_PN20", "", "Т3")!.K);
            Assert.Equal(4, PipeReserveSettings.Match(rules, ReserveTarget.Insulation, "Трубки", "", "К1")!.Line);
        }

        [Fact]
        public void Запас_ошибки_таблицы()
        {
            var issues = new List<ConfigIssue>();
            PipeReserveSettings.ParseRules("Категория;Маска типа;Системы;Запас k\nФитинги;*;;1,1\nТрубы;*;;abc\nТрубы;*;;0,9\n", issues);
            Assert.Equal(2, issues.Count(i => i.Severity == Severity.Error));
            Assert.Single(issues, i => i.Severity == Severity.Warning);
        }

        [Theory]
        [InlineData("autodesk.spec.aec:number-2.0.0", "", QuantityKind.Number)]
        [InlineData("autodesk.spec:number-2.0.0", "", QuantityKind.Number)]
        [InlineData("", "autodesk.unit.unit:general-1.0.1", QuantityKind.Number)]
        [InlineData("autodesk.spec.aec:length-2.0.0", "autodesk.unit.unit:millimeters-1.0.1", QuantityKind.Length)]
        [InlineData("autodesk.spec.aec.piping:pipeSize-2.0.0", "", QuantityKind.Length)]
        [InlineData("", "", QuantityKind.Unknown)]
        public void Запас_единицы_по_типу_данных(string spec, string unit, QuantityKind expected) =>
            Assert.Equal(expected, ReserveMath.Classify(spec, unit));

        [Fact]
        public void Запас_единицы_по_значениям_и_запрет_при_расхождении()
        {
            // Как в КрМа3 2026-10-20: в модели количество = длина × 1 / × 1,2 в метрах
            var meters = Enumerable.Range(1, 20).Select(i => (Raw: i * 1.2, LengthM: (double)i)).ToList();
            Assert.Equal(QuantityKind.Number, ReserveMath.Calibrate(meters, new[] { 1.1 }, out int hm, out int hf, out int total));
            Assert.Equal(20, hm);
            Assert.Equal(0, hf);
            Assert.Equal(20, total);
            var feet = Enumerable.Range(1, 20).Select(i => (Raw: i / ReserveMath.FeetToMeters, LengthM: (double)i)).ToList();
            Assert.Equal(QuantityKind.Length, ReserveMath.Calibrate(feet, Array.Empty<double>(), out _, out _, out _));
            Assert.Equal(QuantityKind.Unknown, ReserveMath.Calibrate(meters.Take(5), Array.Empty<double>(), out _, out _, out _));

            Assert.Null(ReserveMath.Decide(QuantityKind.Length, QuantityKind.Number, out string? blocked));
            Assert.NotNull(blocked);
            Assert.Null(ReserveMath.Decide(QuantityKind.Unknown, QuantityKind.Unknown, out _));
            Assert.Equal(QuantityKind.Number, ReserveMath.Decide(QuantityKind.Unknown, QuantityKind.Number, out _));
            Assert.Equal(QuantityKind.Number, ReserveMath.Decide(QuantityKind.Number, QuantityKind.Unknown, out _));
        }

        [Fact]
        public void Запас_числа_и_подписи()
        {
            Assert.Equal(13.5751, ReserveMath.Quantity(12.341, 1.1), 6);
            Assert.Equal(10 / ReserveMath.FeetToMeters, ReserveMath.Raw(10, QuantityKind.Length), 9);
            Assert.Equal(10, ReserveMath.Raw(10, QuantityKind.Number));
            Assert.Equal("×1,1", ReserveMath.KLabel(ReserveMath.Quantity(5, 1.1), 5));
            Assert.Equal("×1", ReserveMath.KLabel(5, 5));
            Assert.Equal("≈×1,1", ReserveMath.KLabel(5.502, 5));
            Assert.Equal("пусто", ReserveMath.KLabel(null, 5));
            Assert.Equal("12,35", ReserveMath.Text(12.345));
        }

        [Fact]
        public void Запас_настройка_в_модели()
        {
            var defaults = new SpecDefaults();
            var options = new SpecOptions(SpecModule.PipeReserve) { Reserve = defaults.Reserve.Clone() };
            Assert.True(options.IsDefault(defaults));
            options.Reserve.Table = PipeReserveSettings.DefaultTable + "Трубы;*;К*;\n";
            var settings = new SpecProjectSettings();
            options.StoreTo(settings, defaults, "Ivanov", new DateTime(2026, 11, 4, 9, 0, 0));
            SpecOptions loaded = SpecOptions.Load(SpecModule.PipeReserve, settings, defaults, new List<ConfigIssue>(), out string? blocked);
            Assert.Null(blocked);
            Assert.True(loaded.SavedInProject);
            Assert.Equal(options.Reserve.Signature(), loaded.Reserve.Signature());
            Assert.Equal("сохранил Ivanov 04.11.2026 09:00", loaded.SavedStamp);

            loaded.ResetModule(defaults);
            loaded.StoreTo(settings, defaults, "Ivanov", DateTime.Now);
            Assert.DoesNotContain(settings.Values.Keys, k => k.StartsWith("rsv.", StringComparison.Ordinal));
        }

        // ---------------------------------------------------------------- имена труб

        private static string Name(string typeName, string comment, PipeSize size)
        {
            List<PipeNameRule> rules = PipeNameSettings.ParseRules(PipeNameSettings.DefaultRules, new List<ConfigIssue>());
            PipeNameRule rule = PipeNameSettings.Match(rules, typeName, comment)!;
            string name = PipeNameText.Render(rule.Format, rule.BaseName.Length > 0 ? rule.BaseName : comment, size, out string? error);
            Assert.Null(error);
            return name;
        }

        [Fact]
        public void Имена_ВГП_по_Ду_как_VgpNameDuS()
        {
            const string type = "ADSK_Сталь водогазопроводная_Оцинкованная_ГОСТ 3262-75 (Подвал)";
            const string comment = "Труба стальная водогазопроводная оцинкованная";
            Assert.Equal("Труба стальная водогазопроводная оцинкованная ø15x2,8", Name(type, comment, new PipeSize(15, 21.3, 15.7)));
            Assert.Equal("Труба стальная водогазопроводная оцинкованная ø25x3,2", Name(type, comment, new PipeSize(25, 33.5, 27.1)));
            Assert.Equal("Труба стальная водогазопроводная оцинкованная ø32x3,2", Name(type, comment, new PipeSize(32, 42.3, 35.9)));
        }

        [Fact]
        public void Имена_пластик_по_Dнар_с_запятой()
        {
            Assert.Equal("Труба PE-Xa ø32x4,4", Name("ADSK_Полиэтилен сшитый", "Труба PE-Xa", new PipeSize(25, 32, 23.2)));
            Assert.Equal("Труба PN20 ø20x3,4", Name("ADSK_Полипропилен_PN20", "Труба PN20", new PipeSize(15, 20, 13.2)));
            Assert.Equal("Труба электросварная ø76,1x3", Name("ADSK_Сталь электросварная_Прямошовная_ГОСТ 10704-91", "Труба электросварная", new PipeSize(65, 76.1, 70.1)));
        }

        [Fact]
        public void Имена_чугун_SML_по_DN()
        {
            Assert.Equal("Труба SML ø100x3,5", Name("ADSK_Чугун_Безраструбный SML_DIN EN 877", "Труба SML", new PipeSize(100, 110, 103)));
        }

        [Theory]
        [InlineData(15.0, "15")]
        [InlineData(21.3, "21,3")]
        [InlineData(2.7999999, "2,8")]
        [InlineData(3.0000001, "3")]
        [InlineData(2.25, "2,25")]
        public void Имена_числа(double mm, string expected) => Assert.Equal(expected, PipeNameText.Mm(mm));

        [Fact]
        public void Имена_ошибки_размера_и_формата()
        {
            PipeNameText.Render("{base} ø{od}x{s}", "Труба", new PipeSize(15, 21.3, null), out string? error);
            Assert.Contains("{s}", error);
            var issues = new List<ConfigIssue>();
            PipeNameSettings.ParseRules("Маска типа;Основа имени;Формат имени\n*;;{base} ø{D}\n;;{base}\n", issues);
            Assert.Equal(2, issues.Count(i => i.Severity == Severity.Error));
        }

        [Fact]
        public void Имена_слова_окраски_и_исключения()
        {
            Assert.Equal(new[] { "оцинкован" }, PipeNameText.LostCalcWords("Труба водогазопроводная оцинкованная ø15x2,8", "Труба водогазопроводная ø15x2,8"));
            Assert.Empty(PipeNameText.LostCalcWords("Труба ø21.3x2,8", "Труба ø15x2,8"));
            Assert.True(PipeNameText.IsExcluded(" !Не учитывать"));
            Assert.False(PipeNameText.IsExcluded("Труба"));
        }

        [Fact]
        public void Имена_настройка_в_модели_по_умолчанию_только_проверка()
        {
            var defaults = new SpecDefaults();
            Assert.False(defaults.PipeNames.WriteName);
            var options = new SpecOptions(SpecModule.PipeNames) { PipeNames = defaults.PipeNames.Clone() };
            options.PipeNames.WriteName = true;
            var settings = new SpecProjectSettings();
            options.StoreTo(settings, defaults, "Ivanov", DateTime.Now);
            Assert.Null(settings.Get("pn.rules"));   // таблица по умолчанию не дублируется в модели
            SpecOptions loaded = SpecOptions.Load(SpecModule.PipeNames, settings, defaults, new List<ConfigIssue>(), out string? blocked);
            Assert.Null(blocked);
            Assert.True(loaded.PipeNames.WriteName);
            Assert.Equal(options.PipeNames.Signature(), loaded.PipeNames.Signature());

            settings.Set("pn.rules", "Маска типа;Формат имени\n;x\n");
            SpecOptions broken = SpecOptions.Load(SpecModule.PipeNames, settings, defaults, new List<ConfigIssue>(), out blocked);
            Assert.NotNull(blocked);
            Assert.False(broken.PipeNames.WriteName);
        }
    }
}
