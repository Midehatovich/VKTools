using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>0.6.0: комплекты проекта — составы, номера K, поиск кандидатов, значения ADSK_Группирование.</summary>
    public class ProjectKitTests
    {
        private const string Crane = "469_Кран поливочный_ПЛК";

        private static readonly GroupRule Fittings = new GroupRule("4", "Арматура", "OST_PipeAccessory", string.Empty, false);

        private static KitSettings CraneKit()
        {
            var kits = new KitSettings();
            kits.SetRule(new KitRule(Crane, string.Empty, new[]
            {
                new KitMember("Кран шаровой муфтовый"),
                new KitMember("Рукав резинотканевый"),
                new KitMember("Штуцер с резьбой"),
            }));
            return kits;
        }

        private static ProjectKitElement Parent(long id, string type) =>
            new ProjectKitElement { Id = id, TopId = id, Depth = 0, Family = Crane, Type = type };

        private static ProjectKitElement Child(long id, long top, string family, string type = "", int depth = 1) =>
            new ProjectKitElement { Id = id, TopId = top, Depth = depth, Family = family, Type = type };

        [Fact]
        public void Кран_родитель_00_вложенные_по_порядку_состава()
        {
            ProjectKitPlan plan = ProjectKitGrouping.Plan(new[]
            {
                Parent(1, "d25"),
                Child(2, 1, "Кран шаровой муфтовый", "Ду25"),
                Child(3, 1, "Рукав резинотканевый", "L=20 м"),
                Child(4, 1, "Штуцер с резьбой", "Ду25", depth: 2),
                Child(5, 1, "Кронштейн настенный"),
            }, CraneKit(), id => Fittings, Array.Empty<(string, int)>());

            Assert.Equal("4.000000001.00", plan.Values[1]);
            Assert.Equal("4.000000001.01", plan.Values[2]);
            Assert.Equal("4.000000001.02", plan.Values[3]);
            Assert.Equal("4.000000001.03", plan.Values[4]);
            // Не отмеченное вложенное — по таблице групп, как раньше
            Assert.False(plan.Values.ContainsKey(5));
            Assert.Equal(1, plan.Parents);
            Assert.Equal(3, plan.Members);
            Assert.Single(plan.NewNumbers);
            Assert.Empty(plan.Issues);
        }

        [Fact]
        public void K_по_типоразмеру_в_числовом_порядке_мимо_номеров_шкафов()
        {
            ProjectKitPlan plan = ProjectKitGrouping.Plan(new[]
            {
                Parent(1, "DN100"), Parent(2, "DN32"), Parent(3, "DN15"), Parent(4, "DN32"),
                Child(5, 2, "Рукав резинотканевый"),
            }, CraneKit(), id => Fittings, new[] { ("1", 1), ("1", 2) });

            Assert.Equal(new[] { "DN15:3", "DN32:4", "DN100:5" }, plan.NewNumbers.Select(n => $"{n.Type}:{n.K}"));
            Assert.Equal("4.000000004.00", plan.Values[2]);
            Assert.Equal("4.000000004.00", plan.Values[4]);
            Assert.Equal("4.000000004.02", plan.Values[5]);
            Assert.Equal("4.000000005.00", plan.Values[1]);
        }

        [Fact]
        public void Сохранённые_K_не_меняются_новый_типоразмер_получает_свободный()
        {
            KitSettings kits = CraneKit();
            kits.Numbers.Add(new KitNumber(Crane, "d25", 2));
            kits.Numbers.Add(new KitNumber(Crane, "d20", 1));
            ProjectKitPlan plan = ProjectKitGrouping.Plan(new[] { Parent(1, "d15"), Parent(2, "d20"), Parent(3, "d25") },
                kits, id => Fittings, Array.Empty<(string, int)>());

            KitNumber added = Assert.Single(plan.NewNumbers);
            Assert.Equal("d15", added.Type);
            Assert.Equal(3, added.K);
            Assert.Equal("4.000000001.00", plan.Values[2]);
            Assert.Equal("4.000000002.00", plan.Values[3]);
            Assert.Equal(3, plan.Numbers.Count);

            // После «Применить» номер сохранён: следующий анализ новых номеров не даёт и значения те же
            KitSettings saved = kits.WithNumbers(plan.NewNumbers);
            ProjectKitPlan again = ProjectKitGrouping.Plan(new[] { Parent(1, "d15"), Parent(2, "d20"), Parent(3, "d25") },
                saved, id => Fittings, Array.Empty<(string, int)>());
            Assert.Empty(again.NewNumbers);
            Assert.Equal(plan.Values, again.Values);
        }

        [Fact]
        public void Свой_состав_типоразмера_перекрывает_состав_семейства()
        {
            KitSettings kits = CraneKit();
            kits.SetRule(new KitRule(Crane, "d15", new[] { new KitMember("Штуцер с резьбой"), new KitMember("Кран шаровой муфтовый") }));
            ProjectKitPlan plan = ProjectKitGrouping.Plan(new[]
            {
                Parent(1, "d15"), Child(2, 1, "Кран шаровой муфтовый"), Child(3, 1, "Рукав резинотканевый"), Child(4, 1, "Штуцер с резьбой"),
                Parent(5, "d25"), Child(6, 5, "Кран шаровой муфтовый"),
            }, kits, id => Fittings, Array.Empty<(string, int)>());

            Assert.Equal("4.000000001.02", plan.Values[2]);
            Assert.False(plan.Values.ContainsKey(3));
            Assert.Equal("4.000000001.01", plan.Values[4]);
            Assert.Equal("4.000000002.01", plan.Values[6]);
        }

        [Fact]
        public void Вложенный_тип_в_составе_ограничивает_совпадение()
        {
            var kits = new KitSettings();
            kits.SetRule(new KitRule(Crane, string.Empty, new[] { new KitMember("Муфта", "20x15"), new KitMember("Муфта") }));
            ProjectKitPlan plan = ProjectKitGrouping.Plan(new[] { Parent(1, "d20"), Child(2, 1, "Муфта", "20x15"), Child(3, 1, "МУФТА", "25x20") },
                kits, id => Fittings, Array.Empty<(string, int)>());
            Assert.Equal("4.000000001.01", plan.Values[2]);
            Assert.Equal("4.000000001.02", plan.Values[3]);
        }

        [Fact]
        public void Не_трогать_и_нет_группы_комплект_не_применяют()
        {
            var skip = new GroupRule(string.Empty, string.Empty, string.Empty, Crane, true);
            ProjectKitPlan plan = ProjectKitGrouping.Plan(new[] { Parent(1, "d25"), Child(2, 1, "Рукав резинотканевый"), Parent(3, "d20") },
                CraneKit(), id => id == 1 ? skip : null, Array.Empty<(string, int)>());

            Assert.Empty(plan.Values);
            Assert.Empty(plan.NewNumbers);
            Assert.Contains(plan.Issues, i => i.Severity == Severity.Warning && i.Message.Contains("не трогать") && i.Ids.SequenceEqual(new long[] { 1 }));
            Assert.Contains(plan.Issues, i => i.Severity == Severity.Warning && i.Message.Contains("нет группы") && i.Ids.SequenceEqual(new long[] { 3 }));
        }

        [Fact]
        public void Совпадение_K_со_шкафом_в_той_же_группе_замечание()
        {
            KitSettings kits = CraneKit();
            kits.Numbers.Add(new KitNumber(Crane, "d25", 3));
            ProjectKitPlan plan = ProjectKitGrouping.Plan(new[] { Parent(1, "d25") }, kits, id => Fittings, new[] { ("4", 3), ("1", 1) });
            Assert.Contains(plan.Issues, i => i.Message.StartsWith("ПРОВЕРЬ K: в группе 4 K = 3", StringComparison.Ordinal));

            // В другой группе тот же K не мешает
            ProjectKitPlan other = ProjectKitGrouping.Plan(new[] { Parent(1, "d25") }, kits, id => Fittings, new[] { ("1", 3) });
            Assert.DoesNotContain(other.Issues, i => i.Message.StartsWith("ПРОВЕРЬ K", StringComparison.Ordinal));
        }

        [Fact]
        public void Правило_без_семейства_в_модели_и_пропавшее_вложенное_подсказки()
        {
            KitSettings kits = CraneKit();
            kits.SetRule(new KitRule("Умывальник_компл", string.Empty, new[] { new KitMember("Смеситель") }));
            ProjectKitPlan plan = ProjectKitGrouping.Plan(new[] { Parent(1, "d25"), Child(2, 1, "Кран шаровой муфтовый") },
                kits, id => Fittings, Array.Empty<(string, int)>());
            Assert.Contains(plan.Issues, i => i.Severity == Severity.Info && i.Message.Contains("«Умывальник_компл» нет в модели"));
            Assert.Contains(plan.Issues, i => i.Severity == Severity.Info && i.Message.Contains("«Рукав резинотканевый» (ранг .02)"));
            Assert.Contains(plan.Issues, i => i.Severity == Severity.Info && i.Message.Contains("«Штуцер с резьбой» (ранг .03)"));
        }

        [Fact]
        public void Комплекты_в_тексте_туда_и_обратно()
        {
            KitSettings kits = CraneKit();
            kits.SetRule(new KitRule(Crane, "d15", new[] { new KitMember("Штуцер; с резьбой", "Ду15") }));
            kits.Numbers.Add(new KitNumber(Crane, "d25", 1));
            kits.Numbers.Add(new KitNumber(Crane, "d15", 2));
            string text = kits.Format(KitSettings.FileComments);

            var issues = new List<ConfigIssue>();
            KitSettings? back = KitSettings.Parse(text, "тест", issues);
            Assert.Empty(issues);
            Assert.NotNull(back);
            Assert.Equal(kits.Signature(), back!.Signature());
            Assert.Equal(2, back.Rules.Count);
            Assert.Equal("Ду15", back.TypeRule(Crane, "D15")!.Members[0].Type);
            Assert.Equal(2, back.NumberFor(Crane.ToUpperInvariant(), "d15"));
        }

        [Fact]
        public void Комплекты_битые_строки_ошибки()
        {
            var issues = new List<ConfigIssue>();
            KitSettings? kits = KitSettings.Parse("#schema=1\nСемейство;Тип;Вложенное семейство;Вложенный тип;K\n"
                + ";;Муфта;;\n"            // нет семейства
                + "Кран;;Муфта;;3\n"       // и вложенное, и K
                + "Кран;;;;\n"             // ни того ни другого
                + "Кран;;;;1\n"            // K без типа
                + "Кран;d15;;;0\n"         // K не от 1
                + "Кран;d20;;;2\n"
                + "Кран;d25;;;2\n"         // K повторяется
                + "Кран;;Муфта;;\n"
                + "Кран;;муфта;;\n",       // повтор вложенного — предупреждение
                "тест", issues);
            Assert.NotNull(kits);
            Assert.Equal(6, issues.Count(i => i.Severity == Severity.Error));
            Assert.Single(issues, i => i.Severity == Severity.Warning);
            Assert.Single(kits!.Rules);
            Assert.Single(kits.Numbers);

            Assert.Null(KitSettings.Parse("Семейство;Тип\nКран;d15\n", "тест", issues));
        }

        [Fact]
        public void Пустой_состав_удаляет_правило_и_номера_семейства()
        {
            KitSettings kits = CraneKit();
            kits.SetRule(new KitRule(Crane, "d15", new[] { new KitMember("Муфта") }));
            kits.Numbers.Add(new KitNumber(Crane, "d15", 1));
            kits.SetRule(new KitRule(Crane, string.Empty, Array.Empty<KitMember>()));
            Assert.Single(kits.Rules);
            Assert.Single(kits.Numbers);
            kits.SetRule(new KitRule(Crane, "d15", Array.Empty<KitMember>()));
            Assert.Empty(kits.Rules);
            Assert.Empty(kits.Numbers);
        }

        [Fact]
        public void Поиск_кандидатов_по_словам()
        {
            var crane = new KitFamilyInfo(Crane);
            crane.Types.Add(new KitTypeInfo("d25"));
            crane.Texts.Add(new KitText("ADSK_Наименование", "Кран поливочный d25, компл.:"));
            IReadOnlyList<string> words = new KitSettings().WordList();
            Assert.Equal(5, words.Count);
            Assert.Equal("ADSK_Наименование: «Кран поливочный d25, компл.:»", KitSearch.Find(crane, words));

            var basin = new KitFamilyInfo("Умывальник_Комплект");
            Assert.Equal("имя семейства", KitSearch.Find(basin, words));
            var fitting = new KitFamilyInfo("ADSK_ЧугунБезраструбный_Отвод");
            fitting.Types.Add(new KitTypeInfo("DN100"));
            Assert.Null(KitSearch.Find(fitting, words));
            Assert.Null(KitSearch.Find(crane, Array.Empty<string>()));
        }

        [Fact]
        public void Комплекты_в_настройке_проекта_отдельно_от_таблицы_групп()
        {
            var defaults = new SpecDefaults();
            defaults.GroupRules.Add(Fittings);
            var settings = new SpecProjectSettings();
            var options = new SpecOptions(SpecModule.Grouping) { GroupRules = defaults.GroupRules.ToList(), Kits = CraneKit() };
            options.Kits.Words = "компл";
            // Таблица по умолчанию: «По умолчанию» не трогает комплекты
            Assert.True(options.IsDefault(defaults));
            options.StoreTo(settings, defaults, "Ivanov", new DateTime(2026, 10, 17, 13, 0, 0));
            Assert.Null(settings.GroupingTable);
            Assert.NotNull(settings.KitsTable);
            Assert.Equal("компл", settings.KitsWords);
            Assert.Equal("сохранил Ivanov 17.10.2026 13:00", settings.Stamp(SpecProjectSettings.KitsSection));

            var issues = new List<ConfigIssue>();
            SpecOptions back = SpecOptions.Load(SpecModule.Grouping, new SpecProjectSettings(settings.Values), defaults, issues, out string? blocked);
            Assert.Null(blocked);
            Assert.Empty(issues);
            Assert.False(back.SavedInProject);
            Assert.Equal("сохранил Ivanov 17.10.2026 13:00", back.KitsStamp);
            Assert.Equal(options.SettingsSignature(), back.SettingsSignature());

            // Без изменений «Сохранить» не переписывает подпись; номер K меняет отпечаток анализа
            back.StoreTo(settings, defaults, "Petrov", new DateTime(2026, 10, 18, 9, 0, 0));
            Assert.Equal("сохранил Ivanov 17.10.2026 13:00", settings.Stamp(SpecProjectSettings.KitsSection));
            SpecOptions numbered = back.Clone();
            numbered.Kits = back.Kits.WithNumbers(new[] { new KitNumber(Crane, "d25", 1) });
            Assert.NotEqual(back.Signature(), numbered.Signature());
            numbered.StoreTo(settings, defaults, "Petrov", new DateTime(2026, 10, 18, 9, 0, 0));
            Assert.Equal("сохранил Petrov 18.10.2026 09:00", settings.Stamp(SpecProjectSettings.KitsSection));

            // Слова поиска не влияют на анализ
            SpecOptions words = numbered.Clone();
            words.Kits.Words = KitSettings.DefaultWords;
            Assert.Equal(numbered.Signature(), words.Signature());
            Assert.NotEqual(numbered.SettingsSignature(), words.SettingsSignature());

            // Нет правил — раздел удаляется вместе с номерами
            words.Kits = new KitSettings();
            words.StoreTo(settings, defaults, "Petrov", DateTime.Now);
            Assert.Empty(settings.Values);
        }

        [Fact]
        public void Повреждённые_комплекты_в_модели_запрещают_запись()
        {
            var defaults = new SpecDefaults();
            var settings = new SpecProjectSettings { KitsTable = "#schema=1\nСемейство;Тип;Вложенное семейство;Вложенный тип;K\nКран;;;;\n" };
            var issues = new List<ConfigIssue>();
            SpecOptions options = SpecOptions.Load(SpecModule.Grouping, settings, defaults, issues, out string? blocked);
            Assert.NotNull(blocked);
            Assert.Empty(options.Kits.Rules);
            Assert.Null(options.KitsStamp);
        }
    }
}
