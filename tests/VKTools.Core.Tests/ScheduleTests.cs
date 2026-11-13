using System;
using System.Globalization;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VKTools.Core.Config;
using VKTools.Core.Schedules;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>0.13.0 «Создать спецификацию»: фильтры без «ИЛИ», этажи, автоимя, хранение выбора.</summary>
    public class ScheduleTests
    {
        private static readonly string[] Systems = { "В1", "В2", "Т3", "Т4", "К1", "К2", "К3", "К4.1" };

        private static readonly List<SystemGroup> Groups = new List<SystemGroup>
        {
            new SystemGroup("Водоснабжение", new[] { "В", "Т" }, "систем водоснабжения"),
            new SystemGroup("Канализация", new[] { "К" }, "систем канализации"),
        };

        private static void AssertExact(FilterPlan plan, IEnumerable<string> known, IEnumerable<string> chosen)
        {
            Assert.Null(plan.Error);
            foreach (string v in known)
            {
                Assert.Equal(chosen.Contains(v), ScheduleFilterPlanner.Matches(plan.Conditions, v));
            }
            Assert.False(ScheduleFilterPlanner.Matches(plan.Conditions, string.Empty));
        }

        [Fact]
        public void Одна_система_равно()
        {
            FilterPlan plan = ScheduleFilterPlanner.Plan(Systems, new[] { "Т3" });
            Assert.Single(plan.Conditions);
            Assert.Equal(ConditionKind.Equal, plan.Conditions[0].Kind);
            AssertExact(plan, Systems, new[] { "Т3" });
        }

        [Fact]
        public void Канализация_одним_условием_начинается_с_К()
        {
            string[] chosen = { "К1", "К2", "К3", "К4.1" };
            FilterPlan plan = ScheduleFilterPlanner.Plan(Systems, chosen);
            Assert.Single(plan.Conditions);
            Assert.Equal(ConditionKind.BeginsWith, plan.Conditions[0].Kind);
            Assert.Equal("К", plan.Conditions[0].Value);
            AssertExact(plan, Systems, chosen);
            // Новая система канализации попадёт сама
            Assert.True(ScheduleFilterPlanner.Matches(plan.Conditions, "К5"));
        }

        [Fact]
        public void Водоснабжение_исключением_канализации()
        {
            string[] chosen = { "В1", "В2", "Т3", "Т4" };
            FilterPlan plan = ScheduleFilterPlanner.Plan(Systems, chosen);
            Assert.Equal(2, plan.Conditions.Count);
            Assert.Contains(plan.Conditions, c => c.Kind == ConditionKind.NotBeginsWith && c.Value == "К");
            Assert.Contains(plan.Conditions, c => c.Kind == ConditionKind.HasValue);
            AssertExact(plan, Systems, chosen);
            Assert.False(ScheduleFilterPlanner.Matches(plan.Conditions, "К5"));
        }

        [Fact]
        public void В1_Т3_Т4_без_ИЛИ()
        {
            string[] chosen = { "В1", "Т3", "Т4" };
            FilterPlan plan = ScheduleFilterPlanner.Plan(Systems, chosen);
            Assert.True(plan.Conditions.Count <= 3, string.Join("; ", plan.Conditions));
            AssertExact(plan, Systems, chosen);
        }

        [Fact]
        public void К1_и_К1_1_различаются_точным_равенством()
        {
            string[] known = { "К1", "К1.1", "К2" };
            FilterPlan plan = ScheduleFilterPlanner.Plan(known, new[] { "К1.1", "К2" });
            AssertExact(plan, known, new[] { "К1.1", "К2" });
            Assert.Contains(plan.Conditions, c => c.Kind == ConditionKind.NotEqual && c.Value == "К1");
        }

        [Fact]
        public void Все_системы_без_условий_и_с_ограничением_категорий()
        {
            FilterPlan plan = ScheduleFilterPlanner.Plan(Systems, Systems);
            Assert.True(plan.All);
            ScheduleFilterSet set = ScheduleFilterSet.Build(Systems, Systems, Array.Empty<string>(), Array.Empty<string>(), 0);
            Assert.Null(set.Error);
            Assert.Single(set.SystemConditions);
            Assert.Equal(ConditionKind.HasParameter, set.SystemConditions[0].Kind);
        }

        [Fact]
        public void Регистр_букв_не_различить()
        {
            FilterPlan plan = ScheduleFilterPlanner.Plan(new[] { "В1", "в1", "К1" }, new[] { "В1" });
            Assert.NotNull(plan.Error);
        }

        [Fact]
        public void Больше_8_фильтров_ошибка()
        {
            var known = Enumerable.Range(1, 20).Select(i => "Z" + i.ToString("00", CultureInfo.InvariantCulture) + "x" + (char)('a' + i)).ToList();
            // Каждая вторая — префиксов, общих для двух исключаемых, нет
            var chosen = known.Where((v, i) => i % 2 == 0).ToList();
            ScheduleFilterSet set = ScheduleFilterSet.Build(known, chosen, Array.Empty<string>(), Array.Empty<string>(), 0);
            Assert.NotNull(set.Error);
            Assert.Contains("не больше 8", set.Error);
        }

        private static List<FloorInfo> Floors() => FloorNames.Order(new[]
        {
            new FloorInfo("02 этаж", 3.3, 10),
            new FloorInfo("-1 этаж", -3.3, 5),
            new FloorInfo("01 этаж", 0, 12),
            new FloorInfo("10 этаж", 29.7, 3),
            new FloorInfo("Кровля", 33.0, 1),
            new FloorInfo("Техподполье", null, 2),
        });

        [Fact]
        public void Этажи_по_отметке_и_первый_этаж()
        {
            List<FloorInfo> floors = Floors();
            Assert.Equal(new[] { "-1 этаж", "01 этаж", "02 этаж", "10 этаж", "Кровля", "Техподполье" }, floors.Select(f => f.Value));
            Assert.Equal("01 этаж", FloorNames.GuessFirst(floors));
            Assert.Equal(-1, FloorNames.Number("-1 этаж"));
            Assert.Equal(1, FloorNames.Number("Этаж 01"));
            Assert.Equal(3, FloorNames.Number("3-й этаж"));
            Assert.Null(FloorNames.Number("Кровля"));
        }

        [Fact]
        public void Ниже_и_выше_первого_этажа()
        {
            List<FloorInfo> floors = Floors();
            var below = new ScheduleRequest { FloorPreset = ScheduleRequest.PresetBelow, FirstFloor = "01 этаж" };
            var above = new ScheduleRequest { FloorPreset = ScheduleRequest.PresetAbove, FirstFloor = "01 этаж" };
            Assert.Equal(new[] { "-1 этаж" }, below.ResolveFloors(floors));
            Assert.Equal(new[] { "01 этаж", "02 этаж", "10 этаж", "Кровля" }, above.ResolveFloors(floors));
            // «Техподполье» без отметки и номера — ни туда ни сюда: пользователь решает сам

            List<string> known = floors.Select(f => f.Value).ToList();
            ScheduleFilterSet set = ScheduleFilterSet.Build(Systems, Systems, known, above.ResolveFloors(floors), 0);
            Assert.Null(set.Error);
            foreach (string f in known)
            {
                Assert.Equal(above.ResolveFloors(floors).Contains(f), ScheduleFilterPlanner.Matches(set.Floors.Conditions, f));
            }
        }

        [Fact]
        public void Автоимя_как_в_ТЗ()
        {
            var r = new ScheduleRequest { SystemPreset = ScheduleRequest.PresetManual, FloorPreset = ScheduleRequest.PresetManual };
            Assert.Equal("Спецификация систем В1, Т3 и Т4 в уровнях 1, 2 и 3 этажа",
                r.AutoName(new[] { "В1", "Т3", "Т4" }, new[] { "01 этаж", "02 этаж", "03 этаж" }, Groups));
            Assert.Equal("Спецификация системы К1 в уровне «Кровля»", r.AutoName(new[] { "К1" }, new[] { "Кровля" }, Groups));

            var all = new ScheduleRequest();
            Assert.Equal("Спецификация по всем системам", all.AutoName(Systems, Array.Empty<string>(), Groups));

            var sewerBelow = new ScheduleRequest { SystemPreset = "group:Канализация", FloorPreset = ScheduleRequest.PresetBelow, FirstFloor = "01 этаж" };
            Assert.Equal("Спецификация систем канализации ниже 1 этажа", sewerBelow.AutoName(Array.Empty<string>(), Array.Empty<string>(), Groups));

            var waterAbove = new ScheduleRequest { SystemPreset = "group:Водоснабжение", FloorPreset = ScheduleRequest.PresetAbove, FirstFloor = "01 этаж" };
            Assert.Equal("Спецификация систем водоснабжения выше 1 этажа включительно", waterAbove.AutoName(Array.Empty<string>(), Array.Empty<string>(), Groups));

            Assert.Equal("Спецификация А-Б", ScheduleRequest.CleanName("Спецификация  А:Б "));
        }

        [Fact]
        public void Быстрый_выбор_узнаётся_по_галкам()
        {
            Assert.Equal("group:Канализация", ScheduleRequest.DetectSystemPreset(new[] { "К1", "К2", "К3", "К4.1" }, Systems, Groups));
            Assert.Equal(ScheduleRequest.PresetAll, ScheduleRequest.DetectSystemPreset(Systems, Systems, Groups));
            Assert.Equal(ScheduleRequest.PresetManual, ScheduleRequest.DetectSystemPreset(new[] { "К1" }, Systems, Groups));
            Assert.Equal(ScheduleRequest.PresetBelow, ScheduleRequest.DetectFloorPreset(new[] { "-1 этаж" }, Floors(), "01 этаж"));
        }

        [Fact]
        public void Группа_подхватывает_новую_систему_при_обновлении()
        {
            var r = new ScheduleRequest { SystemPreset = "group:Канализация" };
            List<string> now = Systems.Concat(new[] { "К5" }).ToList();
            Assert.Contains("К5", r.ResolveSystems(now, Groups));
            var manual = new ScheduleRequest { SystemPreset = ScheduleRequest.PresetManual, Systems = new List<string> { "В1", "Т3" } };
            Assert.Equal(new[] { "В1", "Т3" }, manual.ResolveSystems(now, Groups));
        }

        [Fact]
        public void Выбор_сохраняется_и_читается()
        {
            var r = new ScheduleRequest
            {
                SystemPreset = ScheduleRequest.PresetManual,
                Systems = new List<string> { "В1", "Т3" },
                FloorPreset = ScheduleRequest.PresetAbove,
                FirstFloor = "01 этаж",
                Name = "Спецификация",
                Template = "Эталон",
                GroupBySystem = false,
                GroupByFloor = true,
                HeaderInTitleBlock = false,
                KnownSystems = new List<string> { "В1", "Т3", "К1" },
                SystemParam = "0cc98b64-809e-45f3-9ea7-553b2d6f21ca",
            };
            ScheduleRequest back = ScheduleRequest.FromMap(r.ToMap())!;
            Assert.Equal(r.Systems, back.Systems);
            Assert.Equal(r.FloorPreset, back.FloorPreset);
            Assert.Equal(r.FirstFloor, back.FirstFloor);
            Assert.Equal("Эталон", back.Template);
            Assert.False(back.GroupBySystem);
            Assert.True(back.GroupByFloor);
            Assert.False(back.HeaderInTitleBlock);
            Assert.Equal(r.KnownSystems, back.KnownSystems);
            Assert.Equal(r.SystemParam, back.SystemParam);
            Assert.Null(ScheduleRequest.FromMap(new Dictionary<string, string>()));
        }

        [Fact]
        public void Встроенные_форма_и_группы_разбираются()
        {
            var loader = new ConfigLoader(new ConfigLocations(RepoPaths.ConfigDirectory(), null));
            foreach (RuleFileSpec spec in ScheduleRules.All)
            {
                LoadedTable loaded = loader.Load(spec);
                Assert.Empty(loaded.Issues);
                Assert.Equal("UTF-8 BOM", loaded.EncodingName);
                string wxi = Path.Combine(Path.GetDirectoryName(RepoPaths.ConfigDirectory())!, "installer", "RevitYear.wxi");
                Assert.Contains("/config/" + spec.FileName + "\"", File.ReadAllText(wxi));
            }
            var issues = new List<ConfigIssue>();
            List<FormColumn> form = ScheduleRules.ParseForm(loader.Load(ScheduleRules.Form).Table!, issues);
            Assert.Empty(issues);
            // ГОСТ 21.110-2013, форма 1: 20+130+60+35+45+20+20+25+40 = 395 мм
            Assert.Equal(395, form.Where(c => !c.Hidden).Sum(c => c.WidthMm));
            Assert.Single(form, c => c.Total && c.Parameter.Name == "ADSK_Количество");
            List<SystemGroup> groups = ScheduleRules.ParseGroups(loader.Load(ScheduleRules.Groups).Table!, issues);
            Assert.Empty(issues);
            Assert.Contains(groups, g => g.Name == "Канализация" && g.Contains("К4.1"));
            Assert.Contains(groups, g => g.Name == "Водоснабжение" && g.Contains("Т3") && !g.Contains("К1"));
        }
    }
}
