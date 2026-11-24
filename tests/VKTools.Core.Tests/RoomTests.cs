using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Config;
using VKTools.Core.Schedules;
using VKTools.Core.Spec;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>0.16.0: спецификация по помещению АР (насосная Пушкарева).</summary>
    public class RoomTests
    {
        [Fact]
        public void Параметр_помещения_один_в_обоих_наборах_и_GUID_задан()
        {
            Assert.NotEqual(Guid.Empty, ParamProfile.RoomGuid);
            Assert.Equal(ParamProfile.RoomGuid, ParamProfile.Own(ParamRole.Room).Guid);
            Assert.Equal(ParamProfile.RoomName, ParamProfile.Of(ParamRole.Room, ParamSetKind.Adsk).Name);
            Assert.Equal(ParamProfile.RoomName, ParamProfile.Of(ParamRole.Room, ParamSetKind.Own).Name);
        }

        [Fact]
        public void Ключ_номер_если_уникален_иначе_с_именем_иначе_с_уровнем()
        {
            var rooms = new List<RoomLabel>
            {
                new RoomLabel("2", "Помещение насосных станций", "-1 этаж"),
                new RoomLabel("2", "С/у", "2 этаж"),
                new RoomLabel("2", "С/у", "3 этаж"),
                new RoomLabel("14", "Техническое помещение", "1 этаж"),
                new RoomLabel("5", "Прихожая", "4 этаж"),
                new RoomLabel("5", "Прихожая", "4 этаж"),
            };
            List<string> duplicates = RoomKeys.Assign(rooms);
            Assert.Equal("2 Помещение насосных станций", rooms[0].Key);
            Assert.Equal("2 С/у (2 этаж)", rooms[1].Key);
            Assert.Equal("2 С/у (3 этаж)", rooms[2].Key);
            Assert.Equal("14", rooms[3].Key);
            Assert.Equal(new[] { "5 Прихожая (4 этаж)" }, duplicates);
            Assert.Equal("2 «Помещение насосных станций»", rooms[0].Title);
        }

        [Fact]
        public void Точка_в_контуре_с_отверстием()
        {
            var outer = new List<(double, double)> { (0, 0), (10, 0), (10, 10), (0, 10) };
            var hole = new List<(double, double)> { (4, 4), (6, 4), (6, 6), (4, 6) };
            var loops = new List<IReadOnlyList<(double X, double Y)>> { outer, hole };
            Assert.True(RoomGeometry.Contains(loops, 1, 1));
            Assert.False(RoomGeometry.Contains(loops, 5, 5));
            Assert.False(RoomGeometry.Contains(loops, 11, 5));
        }

        [Fact]
        public void По_высоте_внутри_потом_под_перекрытием_потом_приямок()
        {
            var rooms = new List<(double, double)> { (-3.85, -1.05), (0, 3) };   // насосная и помещение 1 этажа над ней
            Assert.Equal(0, RoomGeometry.PickByHeight(rooms, -2, 1.5, 1));
            Assert.Equal(0, RoomGeometry.PickByHeight(rooms, -0.3, 1.5, 1));   // трубы под потолком насосной
            Assert.Equal(1, RoomGeometry.PickByHeight(rooms, 1, 1.5, 1));
            Assert.Equal(0, RoomGeometry.PickByHeight(rooms, -4.5, 1.5, 1));   // приямок
            Assert.Equal(-1, RoomGeometry.PickByHeight(rooms, -6, 1.5, 1));
        }

        [Fact]
        public void Автоимя_и_хранение_помещения()
        {
            var request = new ScheduleRequest { Room = "2 Помещение насосных станций", RoomTitle = "2 «Помещение насосных станций»" };
            Assert.Equal("Спецификации систем В1, К1 и К4Н в помещении 2 «Помещение насосных станций»",
                request.AutoName(new[] { "В1", "К1", "К4Н" }, Array.Empty<string>(), Array.Empty<SystemGroup>()));
            ScheduleRequest back = ScheduleRequest.FromMap(request.ToMap())!;
            Assert.Equal(request.Room, back.Room);
            Assert.Equal(request.RoomTitle, back.RoomTitle);
            Assert.True(back.RoomExclusive);
            var plain = ScheduleRequest.FromMap(new ScheduleRequest().ToMap())!;
            Assert.False(plain.IsRoom);
        }

        [Fact]
        public void Фильтры_помещения_считаются_в_предел_и_в_подпись()
        {
            string[] systems = { "В1", "К1" };
            ScheduleFilterSet plain = ScheduleFilterSet.Build(systems, systems, Array.Empty<string>(), Array.Empty<string>(), 0);
            ScheduleFilterSet excluded = ScheduleFilterSet.Build(systems, systems, Array.Empty<string>(), Array.Empty<string>(), 0,
                new[] { new FilterCondition(ConditionKind.NotEqual, "2 Помещение насосных станций") });
            Assert.Equal(plain.Count + 1, excluded.Count);
            Assert.Equal(plain.Describe("С", "Э"), plain.Describe("С", "Э", "П"));   // без помещений подпись прежняя — старые спецификации не «устаревают»
            Assert.Contains("«П» не равно «2 Помещение насосных станций»", excluded.Describe("С", "Э", "П"));
            Assert.True(ScheduleFilterPlanner.Matches(excluded.Rooms, string.Empty));   // пустое помещение «не равно» пропускает (как Revit)
        }

        [Fact]
        public void Настройка_системы_и_этажа_помнит_галку_помещений()
        {
            var settings = new SpecProjectSettings();
            var sf = SystemFloorSettings.Default();
            Assert.True(sf.WriteRoom);
            Assert.True(sf.NeedsVkParameters);
            sf.WriteRoom = false;
            sf.StoreTo(settings, false);
            Assert.False(SystemFloorSettings.Parse(settings, new List<ConfigIssue>())!.WriteRoom);
        }
    }
}
