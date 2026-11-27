using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Config;
using VKTools.Core.Schedules;
using VKTools.Core.Spec;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>0.17.6: «Р_Зона» и фильтр спецификации по зонам (ПТ Пушкарева: насосная, ниже/выше 0,000, паркинг).</summary>
    public class ZoneTests
    {
        private static readonly string[] Zones = { "Насосная", "Ниже 0,000", "Выше 0,000", "Паркинг" };

        [Fact]
        public void Параметр_зоны_один_в_обоих_наборах()
        {
            Assert.Equal(ParamProfile.ZoneGuid, ParamProfile.Own(ParamRole.Zone).Guid);
            Assert.Equal(ParamProfile.ZoneName, ParamProfile.Of(ParamRole.Zone, ParamSetKind.Adsk).Name);
            Assert.Equal('Р', ParamProfile.ZoneName[0]);
        }

        [Fact]
        public void Источник_зоны_сохраняется_и_читается()
        {
            var sf = SystemFloorSettings.Default();
            Assert.False(sf.WritesZone);
            sf.Zone = ZoneSource.Param;
            Assert.False(sf.WritesZone);   // параметр не выбран
            sf.ZoneParam = ParamKey.Shared(KnownParameters.AdskGrouping.Name, KnownParameters.AdskGrouping.Guid);
            Assert.True(sf.WritesZone);
            Assert.True(sf.NeedsVkParameters);
            var settings = new SpecProjectSettings();
            sf.StoreTo(settings, false);
            var issues = new List<ConfigIssue>();
            SystemFloorSettings? back = SystemFloorSettings.Parse(settings, issues);
            Assert.NotNull(back);
            Assert.Empty(issues);
            Assert.Equal(ZoneSource.Param, back!.Zone);
            Assert.Equal(sf.ZoneParam, back.ZoneParam);
            Assert.Equal(sf.Signature(), back.Signature());
            Assert.Equal(sf.Signature(), sf.Clone().Signature());

            sf.Zone = ZoneSource.Floor;
            sf.StoreTo(settings, false);
            Assert.Equal(ZoneSource.Floor, SystemFloorSettings.Parse(settings, issues)!.Zone);
            sf.Zone = ZoneSource.None;
            sf.StoreTo(settings, false);
            Assert.Null(settings.Get("sf.zone"));
        }

        [Fact]
        public void Без_зон_подпись_фильтров_прежняя()
        {
            string[] systems = { "В1", "К1" };
            ScheduleFilterSet old = ScheduleFilterSet.Build(systems, systems, Array.Empty<string>(), Array.Empty<string>(), 0);
            ScheduleFilterSet all = ScheduleFilterSet.Build(systems, systems, Array.Empty<string>(), Array.Empty<string>(), 0, null, Zones, Zones);
            Assert.True(all.Zones.All);
            Assert.Equal(old.Describe("С", "Э", "П"), all.Describe("С", "Э", "П", "З"));
            Assert.Equal(old.Count, all.Count);
        }

        [Fact]
        public void Ниже_нуля_и_паркинг_исключают_остальные_зоны()
        {
            string[] systems = { "В1" };
            ScheduleFilterSet set = ScheduleFilterSet.Build(systems, systems, Array.Empty<string>(), Array.Empty<string>(), 0, null,
                Zones, new[] { "Ниже 0,000", "Паркинг" });
            Assert.Null(set.Error);
            Assert.False(set.Zones.All);
            foreach (string z in Zones) Assert.Equal(z == "Ниже 0,000" || z == "Паркинг", ScheduleFilterPlanner.Matches(set.Zones.Conditions, z));
            Assert.False(ScheduleFilterPlanner.Matches(set.Zones.Conditions, string.Empty));
            Assert.Contains(set.Describe("С", "Э", "П", "З"), l => l.StartsWith("«З»", StringComparison.Ordinal));
        }

        [Fact]
        public void Выбор_зон_хранится_в_спецификации_и_попадает_в_название()
        {
            var request = new ScheduleRequest { SystemPreset = ScheduleRequest.PresetAll, Zones = { "Ниже 0,000", "Паркинг" }, KnownZones = Zones.ToList() };
            ScheduleRequest back = ScheduleRequest.FromMap(request.ToMap())!;
            Assert.Equal(request.Zones, back.Zones);
            Assert.Equal(request.KnownZones, back.KnownZones);
            Assert.Equal(new[] { "Ниже 0,000", "Паркинг" }, back.ResolveZones(Zones));
            Assert.Equal(Zones, new ScheduleRequest().ResolveZones(Zones));
            Assert.Equal("Спецификация по всем системам в зонах «Ниже 0,000» и «Паркинг»", back.AutoName(new[] { "В1" }, Array.Empty<string>(), Array.Empty<SystemGroup>()));
        }
    }
}
