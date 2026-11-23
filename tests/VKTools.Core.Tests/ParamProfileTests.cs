using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Schedules;
using VKTools.Core.Spec;
using VKTools.Core.Text;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>0.14.0: свои параметры «Р_» вместо ADSK_ (объект «Пушкарев»).</summary>
    public class ParamProfileTests : IDisposable
    {
        public ParamProfileTests()
        {
            ParamProfile.Active = ParamSetKind.Adsk;
        }

        public void Dispose()
        {
            ParamProfile.Active = ParamSetKind.Adsk;
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void Свои_параметры_кириллица_уникальные_GUID_и_имена()
        {
            Assert.Equal(12, ParamProfile.OwnDefinitions.Count);
            Assert.All(ParamProfile.OwnDefinitions, d => Assert.StartsWith("Р_", d.Name));
            Assert.All(ParamProfile.OwnDefinitions, d => Assert.Equal('Р', d.Name[0]));   // кириллическая «Р», не латинская «P»
            Assert.Equal(12, ParamProfile.OwnDefinitions.Select(d => d.Guid).Distinct().Count());
            Assert.Equal(12, ParamProfile.OwnDefinitions.Select(d => d.Role).Distinct().Count());
            Assert.DoesNotContain(ParamProfile.OwnDefinitions, d => d.Guid == KnownParameters.VkSystem.Guid);
            Assert.True(ParamProfile.Own(ParamRole.Quantity).Number);
            Assert.True(ParamProfile.Own(ParamRole.Mass).Number);
            Assert.False(ParamProfile.Own(ParamRole.Group).Number);
        }

        [Fact]
        public void ФОП_содержит_все_свои_параметры_с_типом()
        {
            string fop = ParamProfile.SharedParameterFile();
            foreach (OwnParamDef d in ParamProfile.OwnDefinitions)
            {
                Assert.Contains($"PARAM\t{d.Guid:D}\t{d.Name}\t{(d.Number ? "NUMBER" : "TEXT")}\t", fop);
            }
            Assert.Contains(KnownParameters.VkSystem.Guid.ToString("D"), fop);
            Assert.StartsWith("# This is a Revit shared parameter file.", fop);
        }

        [Fact]
        public void Набор_модели_явный_иначе_по_наличию_настроек()
        {
            Assert.Equal(ParamSetKind.Own, ParamProfile.Resolve(new SpecProjectSettings()));
            var krma3 = new SpecProjectSettings();
            krma3.Set("grouping.table", "x");
            Assert.Equal(ParamSetKind.Adsk, ParamProfile.Resolve(krma3));
            krma3.Set(ParamProfile.SettingsKey, "own");
            Assert.Equal(ParamSetKind.Own, ParamProfile.Resolve(krma3));
            var fresh = new SpecProjectSettings();
            fresh.Set(ParamProfile.SettingsKey, "adsk");
            Assert.Equal(ParamSetKind.Adsk, ParamProfile.Resolve(fresh));
        }

        [Fact]
        public void Перевод_ADSK_в_Р_и_LSR_не_пишется()
        {
            ParamProfile.Active = ParamSetKind.Own;
            Assert.Equal("Р_Наименование", ParamProfile.MapToActive(KnownParameters.AdskName)!.Name);
            Assert.Equal("Р_Этаж", ParamProfile.MapToActive(KnownParameters.AdskLevel)!.Name);
            Assert.Equal("Р_Сокращение системы", ParamProfile.MapToActive(KnownParameters.VkSystem)!.Name);
            Assert.Null(ParamProfile.MapToActive(KnownParameters.LsrSystemName));
            Assert.Equal("Р_Завод-изготовитель", ParamProfile.MapNameToActive("ADSK_Завод-изготовитель"));
            Assert.Equal("U_Наружный диаметр", ParamProfile.MapNameToActive("U_Наружный диаметр"));
            Assert.Null(ParamProfile.MapNameToActive("LSR_Тип системы"));

            ParamProfile.Active = ParamSetKind.Adsk;
            Assert.Same(KnownParameters.LsrSystemName, ParamProfile.MapToActive(KnownParameters.LsrSystemName));
            Assert.Equal("ADSK_Марка", ParamProfile.MapNameToActive("ADSK_Марка"));
        }

        [Fact]
        public void Умолчания_модулей_по_набору()
        {
            Assert.Equal(KnownParameters.AdskLevel.Guid, SystemFloorSettings.Default().FloorParam!.Guid);
            Assert.Equal(KnownParameters.LsrSystemName.Guid, SystemFloorSettings.Default().MirrorParam!.Guid);
            Assert.False(PipeNameSettings.Default().WriteName);
            Assert.False(InsulationSettings.Default().WriteName);
            Assert.Equal(KnownParameters.AdskQuantity.Guid, PipeReserveSettings.Default().QtyParam!.Guid);

            ParamProfile.Active = ParamSetKind.Own;
            SystemFloorSettings sf = SystemFloorSettings.Default();
            Assert.Equal(ParamProfile.Own(ParamRole.Floor).Guid, sf.FloorParam!.Guid);
            Assert.Equal(ParamProfile.Own(ParamRole.System).Guid, sf.SystemParam!.Guid);
            Assert.Null(sf.MirrorParam);
            Assert.True(sf.UsesVkSystem);
            Assert.Equal(ParamProfile.Own(ParamRole.Name).Guid, PipeNameSettings.Default().NameParam!.Guid);
            Assert.True(PipeNameSettings.Default().WriteName);
            Assert.True(InsulationSettings.Default().WriteName);
            Assert.Equal(ParamProfile.Own(ParamRole.Quantity).Guid, PipeReserveSettings.Default().QtyParam!.Guid);
            Assert.Equal(new[] { "Р_Сокращение системы" }, OwnSystemRules.Defaults()[0].Targets);
            Assert.Equal(0, OwnSystemRules.Defaults()[0].TargetIndex("Р_Сокращение системы"));
        }

        [Fact]
        public void Правило_своей_системы_с_VK_действует_на_Р_()
        {
            var rule = new OwnSystemRule("*Смеситель*", "СокращениеСистемы", new[] { "VK_Сокращение системы", "LSR_Имя системы" });
            ParamProfile.Active = ParamSetKind.Own;
            Assert.Equal(0, rule.TargetIndex("Р_Сокращение системы"));
            Assert.Equal(-1, rule.TargetIndex("LSR_Имя системы"));
        }

        [Fact]
        public void Изделия_изоляции_своего_набора_K_Flex_и_Р_колонки()
        {
            ParamProfile.Active = ParamSetKind.Own;
            var issues = new List<ConfigIssue>();
            List<InsulationProduct> products = InsulationSettings.ParseProducts(InsulationSettings.DefaultProducts, issues);
            Assert.DoesNotContain(issues, i => i.Severity == Severity.Error);
            Assert.Equal(3, products.Count);
            InsulationProduct st = InsulationSettings.Match(products, "K-Flex ST", "")!;
            Assert.Equal("K-Flex ST", st.Mask);
            Assert.Contains(st.Values, v => v.Key == "Р_Завод-изготовитель" && v.Value == "K-FLEX");
            Assert.Equal("Цилиндры", InsulationSettings.Match(products, "ADSK_Цилиндры некашированные", "")!.Mask);
            Assert.Equal("Трубки теплоизоляционные из вспененного каучука K-FLEX ST", st.BaseName);
            Assert.Equal("K-Flex PE", InsulationSettings.Match(products, "Изоляция K-Flex PE", "")!.Mask);
            Assert.Contains(InsulationSettings.Match(products, "ADSK_Цилиндры некашированные", "")!.Values,
                v => v.Key == "Р_Марка" && v.Value == "ТУ 5762-050-45757503-15");
        }

        [Fact]
        public void Форма_КрМа3_в_своём_наборе_читает_Р_()
        {
            string text = "#schema=1\nЗаголовок;Параметр;GUID;Ширина, мм;Итог;Скрыть;Сортировка;Выравнивание;Точность\n"
                + "Наименование и техническая характеристика;ADSK_Наименование;e6e0f5cd-3e26-485b-9342-23882b20eb43;130;;;2;;\n"
                + "Кол-во;ADSK_Количество;8d057bb3-6ccd-4655-9165-55526691fe3a;20;да;;;центр;0,1\n"
                + "Группирование;ADSK_Группирование;3de5f1a4-d560-4fa8-a74f-25d250fb3401;20;;да;1;;\n";
            ParamProfile.Active = ParamSetKind.Own;
            List<FormColumn> form = ScheduleRules.ParseForm(CsvTable.Parse(text), new List<ConfigIssue>());
            Assert.Equal(new[] { "Р_Наименование", "Р_Количество", "Р_Группирование" }, form.Select(c => c.Parameter.Name));
            Assert.Equal("Наименование и техническая характеристика", form[0].Heading);
            Assert.Equal(130, form[0].WidthMm);
            Assert.True(form[1].Total);

            ParamProfile.Active = ParamSetKind.Adsk;
            form = ScheduleRules.ParseForm(CsvTable.Parse(text), new List<ConfigIssue>());
            Assert.Equal("ADSK_Наименование", form[0].Parameter.Name);
        }
    }
}
