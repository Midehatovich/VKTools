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
    /// <summary>0.5.0: таблица групп с семействами и «не трогать», выбор параметров переноса, настройки проекта в модели.</summary>
    public class SpecSettingsTests
    {
        private static readonly Guid Floor = new Guid("9eabf56c-a6cd-4b5c-a9d0-e9223e19ea3f");
        private static readonly Guid SystemName = new Guid("e2c7a8b0-5b8e-4c1c-9a35-0f6a1b2c3d4e");

        private static GroupingTable Table(string body)
        {
            var issues = new List<ConfigIssue>();
            GroupingTable? table = SpecRuleTables.ParseGroupingText("#schema=1\n" + body, "тест", issues);
            Assert.Empty(issues);
            return table!;
        }

        private static GroupingTable Taps() => Table(
            "Группа;Название;Категория;Семейство;Действие\n"
            + "4;Арматура;OST_PipeAccessory;;\n"
            + "7;Фитинги;OST_PipeFitting;;\n"
            + "8;Прочее;*;;\n"
            + "4.1;Краны поливочные;;Кран поливочн*;\n"
            + ";Комплект вручную;;Кран поливочный d25*;не трогать\n"
            + ";Осевая линия;OST_PipeAccessory;Осевая линия;не трогать\n");

        [Fact]
        public void Группы_семейство_родителя_важнее_своего_и_категории()
        {
            GroupingTable t = Table(
                "Группа;Название;Категория;Семейство;Действие\n"
                + "4;Арматура;OST_PipeAccessory;;\n"
                + "8;Прочее;*;;\n"
                + "4.1;Краны;;Кран поливочн*;\n"
                + "4.2;Шаровые;;Кран шаров*;\n");
            // Кран шаровой сам по себе — по своему семейству
            Assert.Equal("4.2", t.Resolve("OST_PipeAccessory", "Кран шаровой муфтовый", null, null)!.Group);
            // Тот же кран внутри поливочного — по семейству верхнего родителя
            Assert.Equal("4.1", t.Resolve("OST_PipeAccessory", "Кран шаровой муфтовый", "OST_PipeAccessory", "Кран поливочный d25")!.Group);
            // Без семейных правил — категория, потом «*»
            Assert.Equal("4", t.Resolve("OST_PipeAccessory", "Клапан", null, null)!.Group);
            Assert.Equal("8", t.Resolve("OST_Walls", "Стена", null, null)!.Group);
            Assert.Equal("8", t.Resolve(null, null, null, null)!.Group);
        }

        [Fact]
        public void Группы_первая_подходящая_строка_семейства()
        {
            GroupingTable t = Taps();
            // «Кран поливочн*» стоит выше «Кран поливочный d25*»: действует первая
            GroupRule rule = t.Resolve("OST_PipeAccessory", "Кран поливочный d25, компл.", null, null)!;
            Assert.Equal("4.1", rule.Group);
            Assert.False(rule.Skip);
        }

        [Fact]
        public void Группы_не_трогать_действует_на_вложенные()
        {
            GroupingTable t = Table(
                "Группа;Название;Категория;Семейство;Действие\n"
                + "4;Арматура;OST_PipeAccessory;;\n"
                + "7;Фитинги;OST_PipeFitting;;\n"
                + "8;Прочее;*;;\n"
                + ";Комплект вручную;;Кран поливочный*;не трогать\n");
            Assert.True(t.Resolve("OST_PipeAccessory", "Кран поливочный d25", null, null)!.Skip);
            // Штуцер и рукав внутри крана: правило родителя
            Assert.True(t.Resolve("OST_PipeFitting", "Штуцер", "OST_PipeAccessory", "Кран поливочный d25")!.Skip);
            // Тот же штуцер отдельно — по категории
            Assert.Equal("7", t.Resolve("OST_PipeFitting", "Штуцер", null, null)!.Group);
            // GroupFor (без семейств) не видит семейных правил
            Assert.Equal("4", t.GroupFor("OST_PipeAccessory"));
        }

        [Fact]
        public void Группы_категория_у_строки_семейства_сужает_её()
        {
            GroupingTable t = Taps();
            // «Осевая линия» с категорией «Арматура»: в другой категории строка не действует
            Assert.True(t.Resolve("OST_PipeAccessory", "Осевая линия", null, null)!.Skip);
            Assert.Equal("7", t.Resolve("OST_PipeFitting", "Осевая линия", null, null)!.Group);
        }

        [Fact]
        public void Группы_не_трогать_для_звёздочки_оставляет_прочие()
        {
            GroupingTable t = Table("Группа;Название;Категория;Семейство;Действие\n5;Трубы;OST_PipeCurves;;\n;Прочее;*;;не трогать\n");
            Assert.Null(t.DefaultGroup);
            Assert.Null(t.GroupFor("OST_Walls"));
            Assert.Equal("5", t.GroupFor("OST_PipeCurves"));
        }

        [Fact]
        public void Маска_семейства()
        {
            Assert.True(FamilyMask.Matches("Кран поливочн*", "кран ПОЛИВОЧНЫЙ d25"));
            Assert.True(FamilyMask.Matches("*шкаф*", "ШПК-310 Шкаф пожарный"));
            Assert.True(FamilyMask.Matches("Кран d2?", "Кран d25"));
            Assert.False(FamilyMask.Matches("Кран d2?", "Кран d250"));
            Assert.True(FamilyMask.Matches("Осевая линия", " Осевая линия "));
            Assert.False(FamilyMask.Matches("Осевая линия", "Осевая линия 2"));
            Assert.False(FamilyMask.Matches("", "что угодно"));
            Assert.True(FamilyMask.Matches("*", "что угодно"));
        }

        [Fact]
        public void Таблица_групп_ошибки_строк()
        {
            var issues = new List<ConfigIssue>();
            GroupingTable? t = SpecRuleTables.ParseGroupingText(
                "#schema=1\nГруппа;Название;Категория;Семейство;Действие\n"
                + "4;Арматура;OST_PipeAccessory;;удалить\n"
                + "4;Пусто;;;\n"
                + ";Без номера;OST_PipeFitting;;\n"
                + "5;Трубы;OST_PipeCurves;;\n"
                + "6;Трубы ещё раз;OST_PipeCurves;;\n"
                + ";Не писать;;Осевая*;не трогать\n", "тест", issues);
            Assert.NotNull(t);
            List<string> errors = issues.Where(i => i.Severity == Severity.Error).Select(i => i.Message).ToList();
            Assert.Equal(4, errors.Count);
            Assert.Contains(errors, m => m.Contains("удалить"));
            Assert.Contains(errors, m => m.Contains("ни категории, ни семейства"));
            Assert.Contains(errors, m => m.Contains("пустой номер"));
            Assert.Contains(errors, m => m.Contains("повторяется"));
            // «не трогать» без номера — допустимо
            Assert.Contains(t!.Rules, r => r.Skip && r.Family == "Осевая*");
            Assert.All(issues, i => Assert.Equal("тест", i.FileName));
        }

        [Fact]
        public void Таблица_групп_старый_формат_без_новых_колонок()
        {
            // grouping.csv 0.4.0: только Группа;Название;Категория — читается как раньше
            GroupingTable t = Table("Группа;Название;Категория\n0;Спринклеры;OST_Sprinklers\n8;Прочее;*\n");
            Assert.Equal("0", t.GroupFor("OST_Sprinklers"));
            Assert.Equal("8", t.GroupFor("OST_Walls"));
            Assert.All(t.Rules, r => Assert.False(r.Skip));
        }

        [Fact]
        public void Таблица_групп_без_схемы_не_читается()
        {
            var issues = new List<ConfigIssue>();
            Assert.Null(SpecRuleTables.ParseGroupingText("Группа;Категория\n0;OST_Sprinklers\n", "тест", issues));
            Assert.Contains(issues, i => i.Severity == Severity.Error);
        }

        [Fact]
        public void Таблица_групп_запись_и_чтение_без_потерь()
        {
            var rules = new List<GroupRule>
            {
                new GroupRule("0", "Спринклеры", "OST_Sprinklers", string.Empty, false),
                new GroupRule("4.1", "Краны; поливочные \"комплект\"", string.Empty, "Кран поливочн*", false),
                new GroupRule(string.Empty, " #Осевая", "OST_PipeAccessory", "Осевая линия", true),
                new GroupRule("8", "Прочее", "*", string.Empty, false),
            };
            string text = SpecRuleTables.FormatGrouping(rules, new[] { "комментарий", "ещё; один" });
            var issues = new List<ConfigIssue>();
            GroupingTable? back = SpecRuleTables.ParseGroupingText(text, "тест", issues);
            Assert.Empty(issues);
            Assert.Equal(rules.Count, back!.Rules.Count);
            for (int i = 0; i < rules.Count; i++)
            {
                Assert.Equal(rules[i].Group, back.Rules[i].Group);
                Assert.Equal(rules[i].Title, back.Rules[i].Title);
                Assert.Equal(rules[i].Category, back.Rules[i].Category);
                Assert.Equal(rules[i].Family, back.Rules[i].Family);
                Assert.Equal(rules[i].Skip, back.Rules[i].Skip);
            }
            Assert.Equal(text, SpecRuleTables.FormatGrouping(back.Rules, new[] { "комментарий", "ещё; один" }));
        }

        [Fact]
        public void Встроенная_таблица_групп_совпадает_с_форматом()
        {
            // Встроенный grouping.csv после записи и чтения даёт ту же таблицу: «По умолчанию» сравнивается по ней
            LoadedTable loaded = new ConfigLoader(new ConfigLocations(RepoPaths.ConfigDirectory(), null)).Load(SpecRuleTables.Grouping);
            var issues = new List<ConfigIssue>();
            GroupingTable builtIn = SpecRuleTables.ParseGrouping(loaded.Table!, issues);
            Assert.Empty(issues);
            string text = SpecRuleTables.FormatGrouping(builtIn.Rules, Array.Empty<string>());
            GroupingTable? back = SpecRuleTables.ParseGroupingText(text, "тест", issues);
            Assert.Empty(issues);
            Assert.Equal(text, SpecRuleTables.FormatGrouping(back!.Rules, Array.Empty<string>()));
        }

        [Fact]
        public void Порядок_групп_и_предупреждение_о_сортировке()
        {
            GroupingTable t = Table("Группа;Название;Категория;Семейство;Действие\n2;Б;OST_PipeCurves;;\n10;В;OST_PipeFitting;;\n4.1;Г;OST_PipeAccessory;;\n;Нет;OST_Walls;;не трогать\n");
            Assert.Equal(new[] { "10", "2", "4.1" }, t.GroupOrder());
            Assert.Contains(("10", "2"), t.SortSurprises());
            // «10» встанет и перед «4.1»: ведущий ноль нужен всем однозначным номерам
            Assert.Contains(("10", "4.1"), t.SortSurprises());
            GroupingTable ok = Table("Группа;Название;Категория\n02;Б;OST_PipeCurves\n10;В;OST_PipeFitting\n04.1;Г;OST_PipeAccessory\n04.10;Д;OST_Walls\n");
            Assert.Equal(new[] { "02", "04.1", "04.10", "10" }, ok.GroupOrder());
            Assert.Empty(ok.SortSurprises());
        }

        [Fact]
        public void Ключ_параметра_разбор_и_равенство()
        {
            ParamKey shared = ParamKey.Shared("ADSK_Этаж", Floor);
            Assert.Equal(Floor.ToString("D"), shared.Id);
            Assert.Equal(shared, ParamKey.Parse(shared.Id, "другое имя"));
            ParamKey project = ParamKey.Project("Отметка низа");
            Assert.Equal("проект:Отметка низа", project.Id);
            Assert.Equal(project, ParamKey.Parse("Проект: Отметка низа ", string.Empty));
            ParamKey comments = ParamKey.OfBuiltIn("Комментарии", "ALL_MODEL_INSTANCE_COMMENTS");
            Assert.Equal(comments, ParamKey.Parse("встроенный:ALL_MODEL_INSTANCE_COMMENTS", "Комментарии"));
            Assert.Null(ParamKey.Parse("проект:", "x"));
            Assert.Null(ParamKey.Parse(Guid.Empty.ToString(), "x"));
            Assert.Null(ParamKey.Parse("ADSK_Этаж", "ADSK_Этаж"));
            // Общий и параметр проекта с одним именем — разные параметры
            Assert.NotEqual(ParamKey.Shared("Отметка низа", Floor), project);
        }

        [Fact]
        public void Значение_параметра_сравнение()
        {
            Assert.True(ParamValue.OfText("1 этаж").SameAs(ParamValue.OfText("1 этаж")));
            Assert.False(ParamValue.OfText("1 этаж").SameAs(ParamValue.OfText("1 Этаж")));
            Assert.True(ParamValue.OfDouble(3.2808398950131, "1000 мм").SameAs(ParamValue.OfDouble(3.28083989501312, null)));
            Assert.False(ParamValue.OfDouble(3.28, null).SameAs(ParamValue.OfDouble(3.29, null)));
            Assert.True(ParamValue.OfInteger(1, "Да").SameAs(ParamValue.OfInteger(1, null)));
            Assert.False(ParamValue.OfInteger(1, null).SameAs(ParamValue.OfDouble(1, null)));
            Assert.Equal("1000 мм", ParamValue.OfDouble(3.28, "1000 мм").Display);
            Assert.Equal("5", ParamValue.OfInteger(5, null).Display);
        }

        [Fact]
        public void Выбор_параметров_запись_и_чтение()
        {
            var keys = new List<ParamKey>
            {
                ParamKey.Shared("ADSK_Этаж", Floor),
                ParamKey.Project("Отметка; низа"),
                ParamKey.OfBuiltIn("Комментарии", "ALL_MODEL_INSTANCE_COMMENTS"),
            };
            string text = SpecRuleTables.FormatNestedSelection(keys);
            var issues = new List<ConfigIssue>();
            List<ParamKey> back = SpecRuleTables.ParseNestedSelection(text, issues);
            Assert.Empty(issues);
            Assert.Equal(keys, back);
            Assert.Equal("Отметка; низа", back[1].Name);
        }

        [Fact]
        public void Выбор_параметров_битая_строка_ошибка()
        {
            var issues = new List<ConfigIssue>();
            List<ParamKey> keys = SpecRuleTables.ParseNestedSelection("#schema=1\nПараметр;Ключ\nADSK_Этаж;не-guid\n", issues);
            Assert.Empty(keys);
            Assert.Single(issues, i => i.Severity == Severity.Error);
        }

        private static SpecDefaults Defaults()
        {
            var defaults = new SpecDefaults();
            defaults.NestedParams.Add(ParamKey.Shared("ADSK_Этаж", Floor));
            defaults.NestedParams.Add(ParamKey.Shared("LSR_Имя системы", SystemName));
            defaults.GroupRules.AddRange(Taps().Rules.Take(3));
            return defaults;
        }

        [Fact]
        public void Настройки_по_умолчанию_в_модели_не_хранятся()
        {
            SpecDefaults defaults = Defaults();
            var settings = new SpecProjectSettings();
            var issues = new List<ConfigIssue>();
            SpecOptions nested = SpecOptions.Load(SpecModule.Nested, settings, defaults, issues, out string? blocked);
            Assert.Null(blocked);
            Assert.False(nested.SavedInProject);
            Assert.True(nested.IsDefault(defaults));
            // Порядок выбора не важен
            nested.NestedParams.Reverse();
            Assert.True(nested.IsDefault(defaults));
            nested.StoreTo(settings, defaults, "Ivanov", new DateTime(2026, 10, 17, 13, 0, 0));
            Assert.Empty(settings.Values);
        }

        [Fact]
        public void Настройка_переноса_сохраняется_и_читается()
        {
            SpecDefaults defaults = Defaults();
            var settings = new SpecProjectSettings();
            var options = new SpecOptions(SpecModule.Nested)
            {
                NestedParams = { ParamKey.Shared("ADSK_Этаж", Floor), ParamKey.Project("Отметка низа") },
                NestedClearEmpty = true,
                Scope = NestedScope.Selection,
            };
            Assert.False(options.IsDefault(defaults));
            options.StoreTo(settings, defaults, "Ivanov", new DateTime(2026, 10, 17, 13, 0, 0));
            Assert.Equal("сохранил Ivanov 17.10.2026 13:00", settings.Stamp(SpecProjectSettings.NestedSection));
            Assert.Null(settings.GroupingTable);

            var issues = new List<ConfigIssue>();
            SpecOptions back = SpecOptions.Load(SpecModule.Nested, new SpecProjectSettings(settings.Values), defaults, issues, out string? blocked);
            Assert.Null(blocked);
            Assert.Empty(issues);
            Assert.True(back.SavedInProject);
            Assert.True(back.NestedClearEmpty);
            Assert.Equal(options.NestedParams, back.NestedParams);
            Assert.Equal(options.SettingsSignature(), back.SettingsSignature());
            // Область не сохраняется, но входит в отпечаток анализа
            Assert.Equal(NestedScope.Model, back.Scope);
            Assert.NotEqual(options.Signature(), back.Signature());

            // «По умолчанию» и «Сохранить» убирают раздел вместе с подписью
            back.NestedParams = defaults.NestedParams.ToList();
            back.NestedClearEmpty = false;
            back.StoreTo(settings, defaults, "Petrov", DateTime.Now);
            Assert.Empty(settings.Values);
        }

        [Fact]
        public void Настройка_групп_не_трогает_настройку_переноса()
        {
            SpecDefaults defaults = Defaults();
            var settings = new SpecProjectSettings { NestedParams = SpecRuleTables.FormatNestedSelection(new[] { ParamKey.Project("X") }) };
            var grouping = new SpecOptions(SpecModule.Grouping) { GroupRules = Taps().Rules.ToList() };
            grouping.StoreTo(settings, defaults, "Ivanov", new DateTime(2026, 10, 17, 13, 0, 0));
            Assert.NotNull(settings.GroupingTable);
            Assert.NotNull(settings.NestedParams);
            Assert.Null(settings.Stamp(SpecProjectSettings.NestedSection));

            var issues = new List<ConfigIssue>();
            SpecOptions back = SpecOptions.Load(SpecModule.Grouping, settings, defaults, issues, out string? blocked);
            Assert.Null(blocked);
            Assert.True(back.SavedInProject);
            Assert.Equal(grouping.SettingsSignature(), back.SettingsSignature());
            Assert.Contains(back.GroupRules, r => r.Skip && r.Family == "Кран поливочный d25*");
        }

        [Fact]
        public void Повреждённая_настройка_проекта_запрещает_запись()
        {
            SpecDefaults defaults = Defaults();
            var settings = new SpecProjectSettings { GroupingTable = "#schema=1\nГруппа;Название;Категория;Семейство;Действие\n4;Арматура;;;\n" };
            var issues = new List<ConfigIssue>();
            SpecOptions options = SpecOptions.Load(SpecModule.Grouping, settings, defaults, issues, out string? blocked);
            Assert.NotNull(blocked);
            Assert.Contains(issues, i => i.Severity == Severity.Error);
            Assert.False(options.SavedInProject);
            // Показаны правила по умолчанию: «Сохранить» с ними уберёт повреждённую таблицу
            Assert.True(options.IsDefault(defaults));
            options.StoreTo(settings, defaults, "Ivanov", DateTime.Now);
            Assert.Null(settings.GroupingTable);
        }

        [Fact]
        public void Отпечаток_переноса_зависит_от_области_и_очистки()
        {
            var a = new SpecOptions(SpecModule.Nested) { NestedParams = { ParamKey.Shared("ADSK_Этаж", Floor) } };
            SpecOptions b = a.Clone();
            Assert.Equal(a.Signature(), b.Signature());
            b.Scope = NestedScope.ActiveView;
            Assert.NotEqual(a.Signature(), b.Signature());
            Assert.Equal(a.SettingsSignature(), b.SettingsSignature());
            b.NestedClearEmpty = true;
            Assert.NotEqual(a.SettingsSignature(), b.SettingsSignature());
        }

        [Fact]
        public void Csv_поле_кавычки_по_необходимости()
        {
            Assert.Equal("ADSK_Этаж", CsvWriter.Field("ADSK_Этаж"));
            Assert.Equal("\"а;б\"", CsvWriter.Field("а;б"));
            Assert.Equal("\"кран \"\"d25\"\"\"", CsvWriter.Field("кран \"d25\""));
            Assert.Equal("\"#не комментарий\"", CsvWriter.Field("#не комментарий"));
            Assert.Equal("\" отступ\"", CsvWriter.Field(" отступ"));
            Assert.Equal(string.Empty, CsvWriter.Field(null));
            CsvTable table = CsvTable.Parse(CsvWriter.Table(new[] { "к" }, 1, new[] { "А", "Б" }, new[] { (IReadOnlyList<string>)new[] { "#1", "x;y" } }));
            Assert.Equal("#1", table.Rows[0].Get("А"));
            Assert.Equal("x;y", table.Rows[0].Get("Б"));
        }
    }
}
