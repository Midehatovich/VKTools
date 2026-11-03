using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>0.10.0 «Система и этаж»: сокращения систем, этажи, настройка в модели.</summary>
    public class SystemFloorTests
    {
        [Fact]
        public void Сокращение_несколько_систем_через_запятую()
        {
            Assert.Equal(new[] { "В1", "Т3" }, SystemCodes.Split("В1, Т3"));
            Assert.Equal(new[] { "К1" }, SystemCodes.Split(" К1 ;К1 "));
            Assert.Empty(SystemCodes.Split(null));
            Assert.Empty(SystemCodes.Split(" , "));
        }

        [Fact]
        public void Этаж_имя_уровня_как_есть_или_своё_значение()
        {
            var sf = SystemFloorSettings.Default();
            Assert.Equal("Этаж 01", sf.FloorValue(" Этаж 01 "));
            sf.Floors.Add(new FloorRename("Этаж 01 (+0.000)", "01 этаж"));
            sf.Floors.Add(new FloorRename("Кровля", ""));
            Assert.Equal("01 этаж", sf.FloorValue("Этаж 01 (+0.000)"));
            Assert.Equal("Кровля", sf.FloorValue("Кровля"));
            // Регистр имени уровня важен: «этаж 01 (+0.000)» — другой уровень
            Assert.Equal("этаж 01 (+0.000)", sf.FloorValue("этаж 01 (+0.000)"));
        }

        [Fact]
        public void Умолчание_LSR_и_ADSK_Этаж_и_категории_v21()
        {
            var sf = SystemFloorSettings.Default();
            Assert.Equal(KnownParameters.LsrSystemName.Guid, sf.SystemParam!.Guid);
            Assert.Equal(KnownParameters.AdskLevel.Guid, sf.FloorParam!.Guid);
            Assert.True(sf.WriteSystem && sf.WriteFloor && sf.Insulation && sf.ChainNested);
            Assert.Equal(7, sf.Categories.Count);
            Assert.Contains("OST_Sprinklers", sf.Categories);
        }

        [Fact]
        public void Настройка_проекта_туда_и_обратно()
        {
            var defaults = new SpecDefaults();
            var options = new SpecOptions(SpecModule.SystemFloor) { SystemFloor = SystemFloorSettings.Default() };
            Assert.True(options.IsDefault(defaults));

            options.SystemFloor.FloorParam = ParamKey.Project("Этаж_проект");
            options.SystemFloor.ChainNested = false;
            options.SystemFloor.Categories.Remove("OST_Sprinklers");
            options.SystemFloor.Floors.Add(new FloorRename("Этаж 01; отм. 0", "01 этаж"));
            options.SystemFloor.Floors.Add(new FloorRename("Подвал", ""));
            Assert.False(options.IsDefault(defaults));

            var settings = new SpecProjectSettings();
            options.StoreTo(settings, defaults, "Ivanov", new DateTime(2026, 11, 3, 10, 0, 0));
            var issues = new List<ConfigIssue>();
            SpecOptions loaded = SpecOptions.Load(SpecModule.SystemFloor, settings, defaults, issues, out string? blocked);
            Assert.Empty(issues);
            Assert.Null(blocked);
            Assert.True(loaded.SavedInProject);
            Assert.Equal("сохранил Ivanov 03.11.2026 10:00", loaded.SavedStamp);
            Assert.Equal(options.SettingsSignature(), loaded.SettingsSignature());
            Assert.Equal("Этаж_проект", loaded.SystemFloor.FloorParam!.Name);
            Assert.False(loaded.SystemFloor.ChainNested);
            Assert.DoesNotContain("OST_Sprinklers", loaded.SystemFloor.Categories);
            Assert.Equal("01 этаж", loaded.SystemFloor.FloorValue("Этаж 01; отм. 0"));
            // Пустое значение не хранится
            Assert.Single(loaded.SystemFloor.Floors);

            // Обратно к умолчанию — ключи модуля удаляются, чужие разделы не трогаются
            settings.NestedParams = "x";
            loaded.SystemFloor = SystemFloorSettings.Default();
            loaded.StoreTo(settings, defaults, "Ivanov", DateTime.Now);
            Assert.DoesNotContain(settings.Values.Keys, k => k.StartsWith("sf.", StringComparison.Ordinal));
            Assert.Equal("x", settings.NestedParams);
        }

        [Fact]
        public void Перенос_во_вложенные_берёт_настройку_модуля_переноса()
        {
            var defaults = new SpecDefaults();
            defaults.NestedParams.Add(ParamKey.Shared("ADSK_Этаж", KnownParameters.AdskLevel.Guid));
            var settings = new SpecProjectSettings();
            var nested = new SpecOptions(SpecModule.Nested) { NestedParams = new List<ParamKey> { ParamKey.Shared("LSR_Имя системы", KnownParameters.LsrSystemName.Guid) } };
            nested.StoreTo(settings, defaults, "Ivanov", DateTime.Now);

            SpecOptions sf = SpecOptions.Load(SpecModule.SystemFloor, settings, defaults, new List<ConfigIssue>(), out _);
            Assert.False(sf.SavedInProject);
            Assert.True(sf.NestedSavedInProject);
            Assert.Equal("LSR_Имя системы", sf.NestedParams.Single().Name);

            // Сменили параметры переноса — анализ «Системы и этажа» с переносом устарел, без переноса — нет
            string with = sf.Signature();
            sf.NestedParams = defaults.NestedParams.ToList();
            Assert.NotEqual(with, sf.Signature());
            sf.SystemFloor.ChainNested = false;
            string without = sf.Signature();
            sf.NestedParams = new List<ParamKey>();
            Assert.Equal(without, sf.Signature());
        }

        [Fact]
        public void Повреждённая_настройка_запрещает_запись()
        {
            var settings = new SpecProjectSettings();
            settings.Set("sf.flags", "system=1;floor=1;insulation=1;nested=1");
            settings.Set("sf.systemParam", "не-guid;Что-то");
            var issues = new List<ConfigIssue>();
            SpecOptions loaded = SpecOptions.Load(SpecModule.SystemFloor, settings, new SpecDefaults(), issues, out string? blocked);
            Assert.NotNull(blocked);
            Assert.NotEmpty(issues);
            Assert.False(loaded.SavedInProject);
            Assert.Equal(KnownParameters.LsrSystemName.Guid, loaded.SystemFloor.SystemParam!.Guid);
        }
    }
}
