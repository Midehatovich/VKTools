using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>0.12.2: «Своя система у вложенных» — смесители берут В1/В1.1 из своего «СокращениеСистемы», а не К1 от прибора.</summary>
    public class OwnSystemTests
    {
        private static readonly Guid SystemName = new Guid("e2c7a8b0-5b8e-4c1c-9a35-0f6a1b2c3d4e");

        [Fact]
        public void По_умолчанию_смесители_берут_систему_из_СокращениеСистемы()
        {
            List<OwnSystemRule> rules = OwnSystemRules.Defaults();
            OwnSystemRule? shower = OwnSystemRules.Find(rules, "Влж_ADSK_Смеситель для душа ГОСТ 25809-96 (Тип12)");
            Assert.NotNull(shower);
            Assert.Equal("СокращениеСистемы", shower!.Source);
            Assert.NotNull(OwnSystemRules.Find(rules, "Влж_Смеситель"));
            Assert.NotNull(OwnSystemRules.Find(rules, "Влж_Смеситель двухрукояточный для мойки стальной"));
            Assert.Null(OwnSystemRules.Find(rules, "Умывальник керамический"));
            // Первый параметр получает само сокращение, второй — как у труб системы
            // VK_Сокращение системы (фильтр «Создать спецификацию») получает само сокращение, LSR_* — как у труб системы
            Assert.Equal(0, shower.TargetIndex("VK_Сокращение системы"));
            Assert.Equal(1, shower.TargetIndex("LSR_Имя системы"));
            Assert.Equal(2, shower.TargetIndex("lsr_тип системы"));
            Assert.Equal(-1, shower.TargetIndex("ADSK_Этаж"));
        }

        [Fact]
        public void Правила_пишутся_и_читаются()
        {
            var rules = new List<OwnSystemRule>
            {
                new OwnSystemRule("*Смеситель*", "СокращениеСистемы", new[] { "LSR_Имя системы", "LSR_Тип системы" }),
                new OwnSystemRule("Влж_Кран*", "Моя система", new[] { "LSR_Имя системы" }),
            };
            string text = OwnSystemRules.Format(rules);
            var issues = new List<ConfigIssue>();
            List<OwnSystemRule> back = OwnSystemRules.Parse(text, issues);
            Assert.Empty(issues);
            Assert.Equal(OwnSystemRules.Signature(rules), OwnSystemRules.Signature(back));
        }

        [Fact]
        public void Строка_без_источника_или_целей_ошибка()
        {
            var issues = new List<ConfigIssue>();
            List<OwnSystemRule> back = OwnSystemRules.Parse(
                "#schema=1\nМаска семейства;Параметр-источник;Параметры системы\n*Смеситель*;;LSR_Имя системы\nКран*;Сист;\n*Ок*;Сист;LSR_Имя системы\n", issues);
            Assert.Single(back);
            Assert.Equal(2, issues.Count(i => i.Severity == Severity.Error));
        }

        [Fact]
        public void Свои_правила_сохраняются_в_модели_а_по_умолчанию_нет()
        {
            var defaults = new SpecDefaults();
            defaults.NestedParams.Add(ParamKey.Shared("LSR_Имя системы", SystemName));
            var settings = new SpecProjectSettings();

            var options = SpecOptions.Load(SpecModule.Nested, settings, defaults, new List<ConfigIssue>(), out string? _);
            Assert.True(options.IsDefault(defaults));
            options.StoreTo(settings, defaults, "Ivanov", new DateTime(2026, 11, 9, 10, 0, 0));
            Assert.Empty(settings.Values);

            options.OwnSystem.Add(new OwnSystemRule("Влж_Кран*", "СокращениеСистемы", new[] { "LSR_Имя системы" }));
            Assert.False(options.IsDefault(defaults));
            options.StoreTo(settings, defaults, "Ivanov", new DateTime(2026, 11, 9, 10, 0, 0));
            Assert.NotNull(settings.NestedOwnSystem);

            var issues = new List<ConfigIssue>();
            SpecOptions back = SpecOptions.Load(SpecModule.Nested, new SpecProjectSettings(settings.Values), defaults, issues, out string? blocked);
            Assert.Null(blocked);
            Assert.Empty(issues);
            Assert.Equal(2, back.OwnSystem.Count);
            Assert.Equal(options.SettingsSignature(), back.SettingsSignature());

            // «Система и этаж» с переносом той же операцией видит те же правила
            SpecOptions sf = SpecOptions.Load(SpecModule.SystemFloor, new SpecProjectSettings(settings.Values), defaults, new List<ConfigIssue>(), out string? _);
            Assert.Equal(2, sf.OwnSystem.Count);

            // Пустой список — тоже настройка (правила выключены)
            back.OwnSystem.Clear();
            back.StoreTo(settings, defaults, "Petrov", DateTime.Now);
            SpecOptions none = SpecOptions.Load(SpecModule.Nested, new SpecProjectSettings(settings.Values), defaults, new List<ConfigIssue>(), out string? _);
            Assert.Empty(none.OwnSystem);
        }
    }
}
