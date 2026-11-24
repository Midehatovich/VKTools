using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>0.15.0 «Марки и поставщики»: таблица правил (Пушкарев 2026-11-23).</summary>
    public class VendorTests
    {
        private const string Table =
            "﻿#schema=1\r\n"
            + "# комментарий\r\n"
            + "Маска наименования;Марка;Код изделия;Завод-изготовитель;Примечание\r\n"
            + "Гильза*водогазопровод*;ГОСТ 3262-75;;Россия;КрМа3\r\n"
            + "*водогазопроводная*;ГОСТ 3262-75;;Россия;\r\n"
            + "*SML*;DIN EN 877;;Smartex;решение 2026-11-23\r\n"
            + "Хомут соединительный*;DIN EN 877;;Smartex;\r\n"
            + "Металлоизделия*;;;Альтеза;\r\n"
            + "!*;;;;исключение\r\n";

        [Fact]
        public void Разбор_таблицы_и_первое_совпадение_сверху()
        {
            var issues = new List<ConfigIssue>();
            List<VendorRule> rules = VendorSettings.ParseRules(Table, issues);
            Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);
            Assert.Equal(6, rules.Count);

            VendorRule? sleeve = VendorSettings.Match(rules, "Гильза, материал: сталь, тип трубы: водогазопроводный, Ø40x3.5", "Гильза : 40");
            Assert.NotNull(sleeve);
            Assert.Equal("Гильза*водогазопровод*", sleeve!.Mask);

            VendorRule? sml = VendorSettings.Match(rules, "Труба чугунная SML, ø100x3,5", "Трубы : SML");
            Assert.Equal("Smartex", sml!.Value(ParamRole.Maker));
            Assert.Equal("DIN EN 877", sml.Value(ParamRole.Mark));
            Assert.Equal(string.Empty, sml.Value(ParamRole.Code));

            VendorRule? metal = VendorSettings.Match(rules, "Металлоизделия для крепления трубопроводов", "_SYS : METAL");
            Assert.Equal(string.Empty, metal!.Mark);
            Assert.Equal("Альтеза", metal.Maker);
        }

        [Fact]
        public void Маска_по_семейству_и_типу_если_имени_нет()
        {
            List<VendorRule> rules = VendorSettings.ParseRules(Table, new List<ConfigIssue>());
            Assert.NotNull(VendorSettings.Match(rules, string.Empty, "Влж_Трубы_SML : DN100"));
            Assert.Null(VendorSettings.Match(rules, "Труба RAUTITAN", "Трубы : PE-X"));
        }

        [Fact]
        public void Пустая_маска_ошибка_нет_колонки_завода_предупреждение()
        {
            var issues = new List<ConfigIssue>();
            VendorSettings.ParseRules("#schema=1\nМаска наименования;Марка\n;ГОСТ\n", issues);
            Assert.Contains(issues, i => i.Severity == Severity.Error);
            Assert.Contains(issues, i => i.Severity == Severity.Warning && i.Message.Contains("Завод-изготовитель"));
        }

        [Fact]
        public void Настройка_сохраняется_в_модели_и_читается_обратно()
        {
            var settings = new SpecProjectSettings();
            VendorSettings vd = VendorSettings.Default();
            vd.Overwrite = true;
            vd.Rules = Table;
            vd.StoreTo(settings, isDefault: false);
            Assert.True(VendorSettings.IsStored(settings));
            VendorSettings? back = VendorSettings.Parse(settings, new List<ConfigIssue>());
            Assert.NotNull(back);
            Assert.True(back!.Overwrite);
            Assert.Equal(vd.Signature(), back.Signature());

            vd.StoreTo(settings, isDefault: true);
            Assert.False(VendorSettings.IsStored(settings));
        }

        [Fact]
        public void Токен_пусто_значит_очистить()
        {
            Assert.True(VendorSettings.IsClear("<пусто>"));
            Assert.True(VendorSettings.IsClear(" <ПУСТО> "));
            Assert.False(VendorSettings.IsClear(""));
            Assert.False(VendorSettings.IsClear("ГОСТ 6948-75*"));
        }

        [Fact]
        public void По_умолчанию_пустая_таблица_и_только_пустые()
        {
            VendorSettings vd = VendorSettings.Default();
            Assert.False(vd.Overwrite);
            Assert.Empty(VendorSettings.ParseRules(vd.Rules, new List<ConfigIssue>()));
        }
    }
}
