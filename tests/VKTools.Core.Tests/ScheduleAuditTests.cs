using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Schedules;
using VKTools.Core.Spec;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>0.17.0: «Проверить спецификацию» и «всё или ничего» при занятых элементах (случаи «Пушкарева», ПТ).</summary>
    public class ScheduleAuditTests
    {
        // Графы как у копии эталона КрМа3 на «Р_»: сортировка по системе, марке, наименованию; позиция и количество не в сортировке
        private static AuditSchedule Schedule(string name = "Спецификация ниже 0,000", bool itemized = false) =>
            new AuditSchedule(1, name, new List<AuditField>
            {
                new AuditField("Поз.", AuditRole.Position),
                new AuditField("Наименование", AuditRole.Name) { Sort = true },
                new AuditField("Марка", AuditRole.Mark) { Sort = true },
                new AuditField("Ед.", AuditRole.Unit),
                new AuditField("Кол.", AuditRole.Quantity) { Totals = true, Accuracy = 0.1 },
                new AuditField("Примечание", AuditRole.Note),
                new AuditField("Система", AuditRole.Other) { Sort = true, Hidden = true },
            })
            { Itemized = itemized };

        private static long _id = 1000;

        private static AuditElement El(string pos, string name, string mark, string unit, double qty, string note = "", string system = "В21",
            string family = "Отвод", string type = "Ду100", string size = "100 мм")
        {
            var e = new AuditElement(++_id, new[] { pos, name, mark, unit, qty.ToString(System.Globalization.CultureInfo.InvariantCulture), note, system })
            {
                Numbers = new[] { double.NaN, double.NaN, double.NaN, double.NaN, qty, double.NaN, double.NaN },
                Family = family,
                Type = type,
                Size = size,
                System = system,
                Category = "Соединительные детали трубопроводов",
            };
            return e;
        }

        private static List<CheckFinding> Of(List<CheckFinding> all, string check) => all.Where(f => f.Check == check).ToList();

        [Fact]
        public void Роли_граф_по_имени_параметра_без_префикса()
        {
            Assert.Equal(AuditRole.Position, ScheduleAudit.RoleOf("ADSK_Позиция"));
            Assert.Equal(AuditRole.Position, ScheduleAudit.RoleOf("Р_Позиция"));
            Assert.Equal(AuditRole.Other, ScheduleAudit.RoleOf("ADSK_Позиция_Сортировка"));
            Assert.Equal(AuditRole.Name, ScheduleAudit.RoleOf("Р_Наименование"));
            Assert.Equal(AuditRole.Unit, ScheduleAudit.RoleOf("ADSK_Единица измерения"));
            Assert.Equal(AuditRole.Quantity, ScheduleAudit.RoleOf("Р_Количество"));
            Assert.Equal(AuditRole.Other, ScheduleAudit.RoleOf("UNI_Сортировка"));
        }

        [Fact]
        public void Варианты_позиции_у_отвода_Ду100_145_и_179_с_причиной_и_редкими_элементами()
        {
            AuditSchedule s = Schedule();
            for (int i = 0; i < 12; i++) s.Elements.Add(El("145", "Отвод 90° Ду100", "ГОСТ 17375", "шт.", 1));
            AuditElement odd1 = El("179", "Отвод 90° Ду100", "ГОСТ 17375", "шт.", 1);
            AuditElement odd2 = El("179", "Отвод 90° Ду100", "ГОСТ 17375", "шт.", 1);
            s.Elements.Add(odd1);
            s.Elements.Add(odd2);

            List<CheckFinding> v = Of(ScheduleAudit.Run(new[] { s }), ScheduleAudit.Variants);
            CheckFinding f = Assert.Single(v);
            Assert.Contains("«Поз.»", f.Message);
            Assert.Contains("«145» — 12 шт.", f.Current);
            Assert.Contains("«179» — 2 шт.", f.Current);
            Assert.Equal(new[] { odd1.Id, odd2.Id }, f.RelatedElementIds);
            Assert.Contains("разные позиции", f.Action);
        }

        [Fact]
        public void Количество_с_итогами_не_дает_вариантов_а_пустое_примечание_дает()
        {
            AuditSchedule s = Schedule();
            s.Elements.Add(El("80", "Отвод 90° Ду80", "ГОСТ 17375", "шт.", 3, "или аналог"));
            s.Elements.Add(El("80", "Отвод 90° Ду80", "ГОСТ 17375", "шт.", 5, ""));
            List<CheckFinding> v = Of(ScheduleAudit.Run(new[] { s }), ScheduleAudit.Variants);
            CheckFinding f = Assert.Single(v);
            Assert.Contains("«Примечание»", f.Message);
            Assert.Contains("(пусто)", f.Current);
        }

        [Fact]
        public void Для_каждого_экземпляра_вариантов_не_бывает()
        {
            AuditSchedule s = Schedule(itemized: true);
            s.Elements.Add(El("145", "Отвод", "ГОСТ", "шт.", 1));
            s.Elements.Add(El("179", "Отвод", "ГОСТ", "шт.", 1));
            Assert.Empty(Of(ScheduleAudit.Run(new[] { s }), ScheduleAudit.Variants));
        }

        [Fact]
        public void Один_типоразмер_два_наименования_неоцинкованная_и_повтор_позиции()
        {
            AuditSchedule s = Schedule();
            for (int i = 0; i < 20; i++) s.Elements.Add(El("95", "Труба стальная электросварная прямошовная неоцинкованная ø219x6", "ГОСТ 10704", "м", 0.2, family: "Труба", type: "Сталь", size: "219 мм"));
            AuditElement odd = El("95", "Труба стальная электросварная прямошовная ø219x6", "ГОСТ 10704", "м", 0.15, family: "Труба", type: "Сталь", size: "219 мм");
            s.Elements.Add(odd);

            List<CheckFinding> all = ScheduleAudit.Run(new[] { s });
            CheckFinding split = Assert.Single(Of(all, ScheduleAudit.NameSplit));
            Assert.Equal(new[] { odd.Id }, split.RelatedElementIds);
            CheckFinding dup = Assert.Single(Of(all, ScheduleAudit.Numbering), f => f.Message.Contains("позиция 95"));
            Assert.Contains("2 разных строк", dup.Message);
        }

        [Fact]
        public void Отвод_90_и_45_одного_типоразмера_не_считается_разъехавшимся_именем()
        {
            // «Пушкарев», 25.11: угол и Ду в имени при одинаковом типоразмере — разные изделия
            AuditSchedule s = Schedule();
            for (int i = 0; i < 3; i++) s.Elements.Add(El("1", "Отвод 90° стальной крутоизогнутый бесшовный, Ду150, исп. 2", "ГОСТ 17375-2001", "шт.", 1));
            s.Elements.Add(El("2", "Отвод 45° стальной крутоизогнутый бесшовный, Ду150, исп. 2", "ГОСТ 17375-2001", "шт.", 1));
            s.Elements.Add(El("3", "Муфта грувлочная жесткая Ду200", "", "шт.", 1, family: "Муфта", type: "Муфта", size: ""));
            s.Elements.Add(El("4", "Муфта грувлочная жесткая Ду150", "", "шт.", 1, family: "Муфта", type: "Муфта", size: ""));
            s.Elements.Add(El("5", "Гибкая антивибрационная вставка ZKV EPDM PN16, DN50", "", "шт.", 1, family: "Вставка", type: "Вставка", size: "50 мм"));
            s.Elements.Add(El("6", "Гибкая антивибрационная вставка PN16, DN50", "", "шт.", 1, family: "Вставка", type: "Вставка", size: "50 мм"));

            CheckFinding split = Assert.Single(Of(ScheduleAudit.Run(new[] { s }), ScheduleAudit.NameSplit));
            Assert.Equal("Вставка", split.Family);
        }

        [Fact]
        public void У_труб_отличие_только_в_числах_остаётся_ошибкой_имени()
        {
            // ВГП Ду15: «ø15x2,8» (по Ду) и «ø21.3x2,8» (по Dнар) — одна труба, два имени
            AuditSchedule s = Schedule();
            AuditElement a = El("1", "Труба ВГП ø15x2,8", "ГОСТ 3262-75", "м", 3, family: "Типы трубопроводов", type: "ВГП", size: "15 мм");
            AuditElement b = El("2", "Труба ВГП ø21.3x2,8", "ГОСТ 3262-75", "м", 2, family: "Типы трубопроводов", type: "ВГП", size: "15 мм");
            a.Category = b.Category = ScheduleAudit.PipeCategory;
            s.Elements.Add(a);
            s.Elements.Add(b);

            Assert.Single(Of(ScheduleAudit.Run(new[] { s }), ScheduleAudit.NameSplit));
        }

        [Fact]
        public void Элементы_на_восклицательный_знак_не_дают_повтор_позиции_но_видны_отдельно()
        {
            AuditSchedule s = Schedule();
            s.Elements.Add(El("83", "Кран шаровой PN40 DN50", "", "шт.", 1, family: "Кран", type: "Std", size: "50 мм"));
            AuditElement hidden = El("83", "!Не учитывать", "", "шт.", 1, family: "Кран", type: "Std", size: "50 мм");
            s.Elements.Add(hidden);

            List<CheckFinding> all = ScheduleAudit.Run(new[] { s });
            Assert.DoesNotContain(all, f => f.Check == ScheduleAudit.Numbering && f.Message.Contains("позиция 83"));
            Assert.Empty(Of(all, ScheduleAudit.NameSplit));
            CheckFinding excluded = Assert.Single(Of(all, ScheduleAudit.Excluded));
            Assert.Equal(new[] { hidden.Id }, excluded.RelatedElementIds);
        }

        [Fact]
        public void Система_в_отчёте_берётся_из_шапки_спецификации()
        {
            // Начинка шкафа В21.1: своя система элемента «В21.2», а в спецификации она под шапкой «В21.1»
            var s = new AuditSchedule(1, "Ниже 0,000", new List<AuditField>
            {
                new AuditField("Сокращение", AuditRole.Other) { Sort = true },
                new AuditField("Поз.", AuditRole.Position),
                new AuditField("Наименование", AuditRole.Name) { Sort = true },
            });
            for (int i = 1; i <= 2; i++)
            {
                s.Elements.Add(new AuditElement(5000 + i, new[] { "В21.1", "76." + i, "— огнетушитель порошковый" })
                {
                    Family = "Шкаф", Type = "ОП-4", System = "В21.2",
                });
            }
            CheckFinding v = Assert.Single(Of(ScheduleAudit.Run(new[] { s }), ScheduleAudit.Variants));
            Assert.Contains("«В21.1, — огнетушитель", v.Message);
        }

        [Theory]
        [InlineData("Отвод 90° Ду150", "Отвод #° Ду#")]
        [InlineData("Труба ø21.3x2,8", "Труба ø#x#")]
        [InlineData("Кран, DN50, PN16", "Кран, DN#, PN#")]
        public void Наименование_без_чисел(string name, string expected) => Assert.Equal(expected, ScheduleAudit.WithoutNumbers(name));

        [Fact]
        public void Пустое_наименование_и_нулевое_количество_у_спринклерных_труб_ошибка()
        {
            AuditSchedule s = Schedule("Спецификация выше 0,000");
            for (int i = 0; i < 5; i++) s.Elements.Add(El("", "", "ГОСТ 3262-75", "м", 0, system: "В21.1", family: "Труба", type: "ВГП", size: "20 мм"));
            s.Elements.Add(El("1", "Труба ВГП ø20x2,8", "ГОСТ 3262-75", "м", 4, system: "В21.1", family: "Труба", type: "ВГП", size: "25 мм"));

            List<CheckFinding> empty = Of(ScheduleAudit.Run(new[] { s }), ScheduleAudit.Empty);
            Assert.Contains(empty, f => f.Severity == Severity.Error && f.Message.Contains("«Наименование»") && f.Message.Contains("5 эл.") && f.Message.Contains("В21.1"));
            Assert.Contains(empty, f => f.Severity == Severity.Error && f.Message.Contains("«Кол.»"));
            Assert.Contains(empty, f => f.Message.Contains("«Поз.»"));
        }

        [Fact]
        public void Изоляция_в_м3_округляется_в_ноль()
        {
            AuditSchedule s = Schedule("Насосная");
            s.Elements.Add(El("30", "Цилиндры K-Flex 9 мм ⌀50", "K-Flex", "м3", 0.02, family: "Изоляция", type: "K-Flex"));
            s.Elements.Add(El("30", "Цилиндры K-Flex 9 мм ⌀50", "K-Flex", "м3", 0.009, family: "Изоляция", type: "K-Flex"));
            CheckFinding f = Assert.Single(Of(ScheduleAudit.Run(new[] { s }), ScheduleAudit.Zero));
            Assert.Equal(Severity.Error, f.Severity);
            Assert.Contains("метрах", f.Action);
        }

        [Fact]
        public void Дыры_в_нумерации_57_58_и_подпозиции_шкафов()
        {
            AuditSchedule s = Schedule();
            int[] used = Enumerable.Range(1, 60).Where(n => n != 57 && n != 58).ToArray();
            foreach (int n in used) s.Elements.Add(El(n.ToString(System.Globalization.CultureInfo.InvariantCulture), "Изделие " + n, "М" + n, "шт.", 1, type: "T" + n));
            s.Elements.Add(El("38.1", "Клапан пожарный", "КПК", "шт.", 1, type: "Клапан"));
            CheckFinding gap = Assert.Single(Of(ScheduleAudit.Run(new[] { s }), ScheduleAudit.Numbering));
            Assert.Equal("нет: 57, 58", gap.Current);
        }

        [Fact]
        public void Диапазоны_номеров()
        {
            Assert.Equal("1–3, 5, 7, 8", ScheduleAudit.Ranges(new[] { 1, 2, 3, 5, 7, 8 }));
            Assert.Equal(38, ScheduleAudit.LeadingInt("38.7"));
            Assert.Equal(4, ScheduleAudit.LeadingInt("4.1.00"));
            Assert.Null(ScheduleAudit.LeadingInt("-"));
        }

        [Fact]
        public void Метры_и_погонные_метры_вперемешку()
        {
            Assert.True(ScheduleAudit.IsMeter("пог.м"));
            Assert.True(ScheduleAudit.IsMeter("м"));
            Assert.False(ScheduleAudit.IsMeter("м3"));
            AuditSchedule s = Schedule();
            s.Elements.Add(El("1", "Труба PP-M Комфорт Плюс ø110x3,4", "Комфорт Плюс", "пог.м", 10, type: "PP-M"));
            for (int i = 0; i < 3; i++) s.Elements.Add(El("2", "Труба SML ø100x3,5", "SML", "м", 5, type: "SML"));
            CheckFinding f = Assert.Single(Of(ScheduleAudit.Run(new[] { s }), ScheduleAudit.Units));
            Assert.Single(f.RelatedElementIds);
        }

        [Fact]
        public void Текст_не_найдены_из_таблицы_семейства()
        {
            AuditSchedule s = Schedule();
            s.Elements.Add(El("12", "Не найдены переходники чугунные", "", "шт.", 1, system: "К4"));
            CheckFinding f = Assert.Single(Of(ScheduleAudit.Run(new[] { s }), ScheduleAudit.FamilyText));
            Assert.Equal(Severity.Error, f.Severity);
        }

        [Fact]
        public void Элемент_в_двух_спецификациях_и_вне_всех()
        {
            AuditSchedule a = Schedule("Насосная");
            AuditSchedule b = Schedule("Тихонова общая");
            AuditElement shared = El("1", "Насос", "Wilo", "шт.", 1, system: "В2.2", family: "Насос", type: "Wilo");
            a.Elements.Add(shared);
            b.Elements.Add(shared);
            var lost = new List<AuditElement> { El("", "", "", "", 0, system: "", family: "Тройник", type: "50") };
            List<CheckFinding> all = ScheduleAudit.Run(new[] { a, b }, lost);
            Assert.Single(Of(all, ScheduleAudit.Twice));
            CheckFinding outside = Assert.Single(Of(all, ScheduleAudit.Outside));
            Assert.Contains("без системы", outside.Message);
            Assert.Equal(Severity.Info, all.Last().Severity);
        }

        [Fact]
        public void Чистая_спецификация_одна_строка_итога()
        {
            AuditSchedule s = Schedule();
            s.Elements.Add(El("1", "Отвод 90° Ду100", "ГОСТ 17375", "шт.", 2));
            s.Elements.Add(El("2", "Отвод 90° Ду80", "ГОСТ 17375", "шт.", 2, type: "Ду80", size: "80 мм"));
            CheckFinding f = Assert.Single(ScheduleAudit.Run(new[] { s }));
            Assert.Contains("Замечаний нет", f.Message);
        }

        [Fact]
        public void Занятые_элементы_запрещают_запись_целиком_если_не_разрешено()
        {
            var busy = new BusySummary();
            Assert.Null(busy.BlockReason(partialAllowed: false));
            busy.AddOwned(1, "Kondratyev_DA");
            busy.AddOwned(2, "Kondratyev_DA");
            busy.AddOwned(2, "Kondratyev_DA");
            busy.AddOutdated(3);
            string? reason = busy.BlockReason(partialAllowed: false);
            Assert.NotNull(reason);
            Assert.Contains("Kondratyev_DA — 2", reason);
            Assert.Contains("изменены в хранилище 1", reason);
            Assert.Contains("Обновить до последней версии", reason);
            Assert.Null(busy.BlockReason(partialAllowed: true));
            Assert.Equal(3, busy.ElementIds().Count());
        }

        [Fact]
        public void Настройка_частичной_записи_сохраняется()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vk-settings-" + System.Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                var settings = new Core.Settings.UserSettings { PartialWriteWhenBusy = true };
                settings.Save(path);
                Assert.True(Core.Settings.UserSettings.Load(path).PartialWriteWhenBusy);
                Assert.False(new Core.Settings.UserSettings().PartialWriteWhenBusy);
            }
            finally
            {
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
        }
    }
}
