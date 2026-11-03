using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>0.11.0 «Изоляция»: числа, шаблоны, таблица «Изделия», настройка в модели (эталон — InsulationPipeOD v1.1.2).</summary>
    public class InsulationTests
    {
        [Theory]
        [InlineData(60.0, "60")]
        [InlineData(33.5, "33,5")]
        [InlineData(21.3, "21,3")]
        [InlineData(60.00001, "60")]
        [InlineData(26.85, "26,9")]
        [InlineData(42.25, "42,3")]
        public void Мм_как_в_скрипте(double mm, string expected) => Assert.Equal(expected, InsulationText.Mm(mm));

        [Fact]
        public void Имя_ROCKWOOL_по_умолчанию()
        {
            List<InsulationProduct> products = InsulationSettings.ParseProducts(InsulationSettings.DefaultProducts, new List<ConfigIssue>());
            InsulationProduct p = InsulationSettings.Match(products, "Цилиндры ROCKWOOL 25", "Изоляция труб цилиндрами навивными ROCKWOOL 100 (НГ)")!;
            string name = InsulationText.Render(p.NameFormat, "Изоляция труб цилиндрами навивными ROCKWOOL 100 (НГ)", 25, 60, p.CylinderFor(60), out string? error);
            Assert.Null(error);
            Assert.Equal("Изоляция труб цилиндрами навивными ROCKWOOL 100 (НГ) толщиной 25 мм для трубы D = 60 мм", name);
            Assert.Equal(new[] { "ADSK_Марка", "ADSK_Завод-изготовитель" }, p.Values.Select(v => v.Key));
            Assert.Equal("ТУ 5762-050-45757503-15", p.Values[0].Value);
            // Цилиндр по ряду: 21 подходит для 21,3, для 33,5 — 35, больше 273 — нет
            Assert.Equal(21, p.CylinderFor(21.3));
            Assert.Equal(35, p.CylinderFor(33.5));
            Assert.Null(p.CylinderFor(325));
            Assert.True(p.ThicknessInSeries(25));
            Assert.False(p.ThicknessInSeries(13));
            Assert.Null(InsulationSettings.Match(products, "K-FLEX ST 13", ""));
        }

        [Fact]
        public void Своё_изделие_основа_и_параметры()
        {
            var issues = new List<ConfigIssue>();
            List<InsulationProduct> products = InsulationSettings.ParseProducts(
                "Маска типа;Основа имени;Формат имени;Толщины;Ряд Dвн;ADSK_Марка;ADSK_Код изделия\n"
                + "K-FLEX*;Трубка K-FLEX ST;{base} {cyl}x{t};9 13 19;18 22 28 35;ST;ST-{cyl}-{t}\n"
                + "*;;;;;;\n", issues);
            Assert.Empty(issues);
            InsulationProduct k = InsulationSettings.Match(products, "K-FLEX ST 13", "")!;
            Assert.Equal("Трубка K-FLEX ST 35x13", InsulationText.Render(k.NameFormat, k.BaseName, 13, 33.5, k.CylinderFor(33.5), out _));
            Assert.True(InsulationText.SizeDependent(k.Values[1].Value));
            Assert.False(InsulationText.SizeDependent(k.Values[0].Value));
            // «*» — все остальные типы, но без имени и параметров
            InsulationProduct any = InsulationSettings.Match(products, "Минвата", "")!;
            Assert.Equal(string.Empty, any.NameFormat);
            Assert.Empty(any.Values);
        }

        [Fact]
        public void Ошибки_таблицы()
        {
            var issues = new List<ConfigIssue>();
            InsulationSettings.ParseProducts(
                "Маска типа;Основа имени;Формат имени;Толщины;Ряд Dвн\n"
                + ";Основа;;;\n"
                + "ROCK;;{base} {x};20 abc;\n", issues);
            Assert.Equal(3, issues.Count(i => i.Severity == Severity.Error));
            Assert.Contains(issues, i => i.Message.Contains("пустая"));
            Assert.Contains(issues, i => i.Message.Contains("abc"));
            Assert.Contains(issues, i => i.Message.Contains("непонятная вставка"));
            // Без толщины {t} не подставить
            InsulationText.Render("{t}", "", null, 20, null, out string? error);
            Assert.NotNull(error);
        }

        [Fact]
        public void Настройка_проекта_туда_и_обратно()
        {
            var defaults = new SpecDefaults();
            var options = new SpecOptions(SpecModule.Insulation);
            Assert.True(options.IsDefault(defaults));
            Assert.False(options.Insulation.WriteName);

            options.Insulation.WriteName = true;
            options.Insulation.OdParam = ParamKey.Project("U_Наружный диаметр");
            options.Insulation.Products = InsulationSettings.DefaultProducts.Replace("ROCKWOOL;\n", "Альтеза;\n");
            var settings = new SpecProjectSettings();
            options.StoreTo(settings, defaults, "Ivanov", new DateTime(2026, 11, 3, 12, 0, 0));
            SpecOptions loaded = SpecOptions.Load(SpecModule.Insulation, settings, defaults, new List<ConfigIssue>(), out string? blocked);
            Assert.Null(blocked);
            Assert.True(loaded.SavedInProject);
            Assert.True(loaded.Insulation.WriteName);
            Assert.Equal("U_Наружный диаметр", loaded.Insulation.OdParam!.Name);
            Assert.Equal(options.SettingsSignature(), loaded.SettingsSignature());

            // Та же таблица в другом оформлении (CRLF, BOM, пустые строки) — тот же отпечаток
            var same = loaded.Clone();
            same.Insulation.Products = "﻿" + loaded.Insulation.Products.Replace("\n", "\r\n") + "\r\n\r\n";
            Assert.Equal(loaded.SettingsSignature(), same.SettingsSignature());

            loaded.Insulation = InsulationSettings.Default();
            loaded.StoreTo(settings, defaults, "Ivanov", DateTime.Now);
            Assert.DoesNotContain(settings.Values.Keys, k => k.StartsWith("ins.", StringComparison.Ordinal));
        }
    }
}
