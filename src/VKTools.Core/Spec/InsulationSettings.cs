using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Text;

namespace VKTools.Core.Spec
{
    /// <summary>
    /// Изделие изоляции — строка таблицы «Изделия» (Р4, порт PRODUCTS / NAME_FORMAT InsulationPipeOD v1.1.2).
    /// Колонки после стандартных — параметры изоляции (ADSK_Марка, ADSK_Завод-изготовитель…): значение-шаблон пишется в одноимённый параметр.
    /// </summary>
    public sealed class InsulationProduct
    {
        public InsulationProduct(string mask, string baseName, string nameFormat, IReadOnlyList<double> thicknesses, IReadOnlyList<double> cylinders,
            IReadOnlyList<KeyValuePair<string, string>> values, int line)
        {
            Mask = mask;
            BaseName = baseName;
            NameFormat = nameFormat;
            Thicknesses = thicknesses;
            Cylinders = cylinders;
            Values = values;
            Line = line;
        }

        /// <summary>Маска имени типа или «Комментариев к типоразмеру»: * — любые символы; без * — вхождение подстроки.</summary>
        public string Mask { get; }

        /// <summary>Основа наименования; пусто — «Комментарии к типоразмеру» типа изоляции.</summary>
        public string BaseName { get; }

        /// <summary>Шаблон ADSK_Наименование: {base}, {t}, {d}, {cyl}; пусто — наименование этому изделию не пишется.</summary>
        public string NameFormat { get; }

        /// <summary>Ряд толщин, мм; пусто — не проверяется.</summary>
        public IReadOnlyList<double> Thicknesses { get; }

        /// <summary>Ряд внутренних Ø цилиндров, мм (для {cyl} и проверки «Dнар больше ряда»); пусто — не проверяется.</summary>
        public IReadOnlyList<double> Cylinders { get; }

        /// <summary>Параметр → шаблон значения (пустые не пишутся).</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Values { get; }

        public int Line { get; }

        public bool Matches(string typeName, string typeComment)
        {
            string mask = Mask.Trim();
            if (mask.Length == 0 || mask == "*") return true;
            if (mask.IndexOf('*') >= 0 || mask.IndexOf('?') >= 0)
            {
                return FamilyMask.Matches(mask, typeName ?? string.Empty) || FamilyMask.Matches(mask, typeComment ?? string.Empty);
            }
            string hay = ((typeName ?? string.Empty) + " " + (typeComment ?? string.Empty)).ToUpperInvariant();
            return hay.IndexOf(mask.ToUpperInvariant(), StringComparison.Ordinal) >= 0;
        }

        /// <summary>Наименьший Ø цилиндра не меньше Dнар − 0,5 мм (21 подходит для 21,3); null — ряда нет или Dнар больше ряда.</summary>
        public double? CylinderFor(double odMm)
        {
            foreach (double s in Cylinders.OrderBy(x => x))
            {
                if (s >= odMm - InsulationText.CylinderTolerance) return s;
            }
            return null;
        }

        public bool ThicknessInSeries(double mm) => Thicknesses.Count == 0 || Thicknesses.Any(s => Math.Abs(s - mm) < 0.05);
    }

    /// <summary>Числа и шаблоны изоляции.</summary>
    public static class InsulationText
    {
        public const double CylinderTolerance = 0.5;

        public const string DefaultNameFormat = "{base} толщиной {t} мм для трубы D = {d} мм";

        private static readonly string[] Placeholders = { "{base}", "{t}", "{d}", "{cyl}" };
        private static readonly string[] SizePlaceholders = { "{t}", "{d}", "{cyl}" };

        /// <summary>мм → «60», «33,5», «21,3»: 1 знак, половина вверх, запятая, без незначащего нуля.</summary>
        public static string Mm(double mm)
        {
            double x = Math.Floor(Math.Abs(mm) * 10 + 0.5 + 1e-9) / 10 * (mm >= 0 ? 1 : -1);
            if (Math.Abs(x - Math.Round(x)) < 1e-9) return Math.Round(x).ToString("0", CultureInfo.InvariantCulture);
            return x.ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',');
        }

        /// <summary>Шаблон зависит от размера: такое значение нельзя писать в тип.</summary>
        public static bool SizeDependent(string template) => SizePlaceholders.Any(p => template.IndexOf(p, StringComparison.Ordinal) >= 0);

        /// <summary>Подставить значения; ошибка — текст причины.</summary>
        public static string Render(string template, string baseName, double? thickness, double odMm, double? cylinder, out string? error)
        {
            error = null;
            if (template.IndexOf("{t}", StringComparison.Ordinal) >= 0 && thickness == null)
            {
                error = "толщина изоляции не определена";
                return string.Empty;
            }
            if (template.IndexOf("{cyl}", StringComparison.Ordinal) >= 0 && cylinder == null)
            {
                error = $"Dнар {Mm(odMm)} мм вне ряда цилиндров — {{cyl}} не определён";
                return string.Empty;
            }
            string text = template
                .Replace("{base}", baseName.Trim())
                .Replace("{t}", thickness.HasValue ? Mm(thickness.Value) : string.Empty)
                .Replace("{d}", Mm(odMm))
                .Replace("{cyl}", cylinder.HasValue ? Mm(cylinder.Value) : string.Empty);
            int brace = text.IndexOf('{');
            if (brace >= 0 && text.IndexOf('}', brace) > brace)
            {
                error = $"в шаблоне «{template}» непонятная вставка — можно {string.Join(", ", Placeholders)}";
                return string.Empty;
            }
            return text;
        }
    }

    /// <summary>Настройка «Изоляция» (ключи ins.* в модели): что писать и таблица «Изделия».</summary>
    public sealed class InsulationSettings
    {
        public const string MaskColumn = "Маска типа";
        public const string BaseColumn = "Основа имени";
        public const string FormatColumn = "Формат имени";
        public const string ThicknessColumn = "Толщины";
        public const string CylinderColumn = "Ряд Dвн";
        public const string DefaultOdName = "U_Наружный диаметр";

        private static readonly char[] NumberSeparators = { ' ', '\t' };

        public static readonly IReadOnlyList<string> Columns = new[] { MaskColumn, BaseColumn, FormatColumn, ThicknessColumn, CylinderColumn };

        /// <summary>Как InsulationPipeOD v1.1.2: ROCKWOOL 100, марка — ТУ (ТЗ главного специалиста 2026-10-22), завод ROCKWOOL, кода нет.</summary>
        public const string DefaultProducts =
            "#schema=1\n"
            + "Маска типа;Основа имени;Формат имени;Толщины;Ряд Dвн;ADSK_Марка;ADSK_Завод-изготовитель;ADSK_Код изделия\n"
            + "ROCKWOOL;;" + InsulationText.DefaultNameFormat + ";20 25 30 40 50 60 70 80 90 100;"
            + "18 21 25 28 32 35 38 42 45 48 54 57 60 64 70 76 83 89 102 108 114 133 140 159 169 194 205 219 245 273;"
            + "ТУ 5762-050-45757503-15;ROCKWOOL;\n";

        private const string FlagsKey = "ins.flags";
        private const string OdKey = "ins.odParam";
        private const string NameKey = "ins.nameParam";
        private const string ProductsKey = "ins.products";

        public bool WriteOd { get; set; } = true;

        /// <summary>Параметр Dнар; null — найти у изоляции по имени «U_Наружный диаметр».</summary>
        public ParamKey? OdParam { get; set; }

        /// <summary>Пишет ли плагин ADSK_Наименование (решение 2026-11-03: да, включается на проект; по умолчанию выключено).</summary>
        public bool WriteName { get; set; }

        public ParamKey? NameParam { get; set; }

        /// <summary>Колонки-параметры таблицы «Изделия» (марка, завод…).</summary>
        public bool WriteProduct { get; set; } = true;

        /// <summary>Таблица «Изделия» текстом CSV.</summary>
        public string Products { get; set; } = DefaultProducts;

        public static InsulationSettings Default() => new InsulationSettings
        {
            NameParam = ParamKey.Shared(KnownParameters.AdskName.Name, KnownParameters.AdskName.Guid),
        };

        public InsulationSettings Clone() => (InsulationSettings)MemberwiseClone();

        public string Signature() =>
            $"{Flags()}|{OdParam?.Id}|{NameParam?.Id}|" + Normalize(Products);

        private string Flags() => $"od={B(WriteOd)};name={B(WriteName)};product={B(WriteProduct)}";

        private static string B(bool v) => v ? "1" : "0";

        /// <summary>Текст таблицы без «шума» (пустые строки, BOM, переводы строк), чтобы отпечаток не зависел от редактора.</summary>
        public static string Normalize(string text)
        {
            try
            {
                CsvTable table = CsvTable.Parse((text ?? string.Empty).TrimStart('\uFEFF'));
                IEnumerable<IReadOnlyList<string>> rows = table.Rows
                    .Select(r => (IReadOnlyList<string>)table.Columns.Select(c => r.Get(c).Trim()).ToList())
                    .Where(r => r.Any(v => v.Length > 0));
                return CsvWriter.Table(Enumerable.Empty<string>(), 1, table.Columns, rows).Replace("\r", string.Empty).Trim();
            }
            catch (FormatException)
            {
                return (text ?? string.Empty).Trim();
            }
        }

        public void StoreTo(SpecProjectSettings settings, bool isDefault)
        {
            if (isDefault)
            {
                foreach (string key in new[] { FlagsKey, OdKey, NameKey, ProductsKey }) settings.Set(key, null);
                return;
            }
            settings.Set(FlagsKey, Flags());
            settings.Set(OdKey, OdParam == null ? null : OdParam.Id + ";" + OdParam.Name);
            settings.Set(NameKey, NameParam == null ? string.Empty : NameParam.Id + ";" + NameParam.Name);
            settings.Set(ProductsKey, Normalize(Products) == Normalize(DefaultProducts) ? null : Products);
        }

        public static bool IsStored(SpecProjectSettings settings) => settings.Get(FlagsKey) != null;

        public static InsulationSettings? Parse(SpecProjectSettings settings, List<ConfigIssue> issues)
        {
            InsulationSettings result = Default();
            foreach (string part in (settings.Get(FlagsKey) ?? string.Empty).Split(';'))
            {
                string[] pair = part.Split('=');
                if (pair.Length != 2) continue;
                bool on = pair[1].Trim() == "1";
                switch (pair[0].Trim())
                {
                    case "od": result.WriteOd = on; break;
                    case "name": result.WriteName = on; break;
                    case "product": result.WriteProduct = on; break;
                }
            }
            bool ok = true;
            result.OdParam = Key(settings.Get(OdKey), "параметр Dнар", issues, ref ok);
            string? name = settings.Get(NameKey);
            result.NameParam = name == null ? result.NameParam : Key(name, "параметр наименования", issues, ref ok);
            string? products = settings.Get(ProductsKey);
            if (products != null)
            {
                result.Products = products;
                var tableIssues = new List<ConfigIssue>();
                ParseProducts(products, tableIssues);
                if (tableIssues.Any(i => i.Severity == Severity.Error))
                {
                    issues.AddRange(tableIssues);
                    ok = false;
                }
            }
            return ok ? result : null;
        }

        private static ParamKey? Key(string? stored, string what, List<ConfigIssue> issues, ref bool ok)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            int semicolon = stored!.IndexOf(';');
            string id = semicolon < 0 ? stored : stored.Substring(0, semicolon);
            ParamKey? key = ParamKey.Parse(id, semicolon < 0 ? string.Empty : stored.Substring(semicolon + 1));
            if (key == null)
            {
                issues.Add(new ConfigIssue(Severity.Error, "настройка проекта: изоляция", null, $"{what}: непонятный ключ «{id}»"));
                ok = false;
            }
            return key;
        }

        /// <summary>Разбор таблицы «Изделия». Ошибки — с номером строки файла.</summary>
        public static List<InsulationProduct> ParseProducts(string text, List<ConfigIssue> issues)
        {
            const string file = "таблица «Изделия»";
            var result = new List<InsulationProduct>();
            CsvTable table;
            try
            {
                table = CsvTable.Parse((text ?? string.Empty).TrimStart('\uFEFF'));
            }
            catch (FormatException ex)
            {
                issues.Add(new ConfigIssue(Severity.Error, file, null, ex.Message));
                return result;
            }
            if (!table.HasColumn(MaskColumn))
            {
                issues.Add(new ConfigIssue(Severity.Error, file, null, $"нет колонки «{MaskColumn}»"));
                return result;
            }
            List<string> extra = table.Columns.Where(c => c.Length > 0 && !Columns.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (CsvRow row in table.Rows)
            {
                string mask = row.Get(MaskColumn).Trim();
                if (mask.Length == 0)
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, "пустая «Маска типа» (для всех типов — «*»)"));
                    continue;
                }
                List<double> thick = Numbers(row.Get(ThicknessColumn), ThicknessColumn, row.LineNumber, issues);
                List<double> cyl = Numbers(row.Get(CylinderColumn), CylinderColumn, row.LineNumber, issues);
                string format = row.Get(FormatColumn).Trim();
                var values = extra.Select(c => new KeyValuePair<string, string>(c, row.Get(c).Trim())).Where(p => p.Value.Length > 0).ToList();
                foreach (string template in new[] { format }.Concat(values.Select(v => v.Value)))
                {
                    InsulationText.Render(template, "основа", 25, 33.5, 35, out string? error);
                    if (error != null && error.StartsWith("в шаблоне", StringComparison.Ordinal)) issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, error));
                }
                result.Add(new InsulationProduct(mask, row.Get(BaseColumn).Trim(), format, thick, cyl, values, row.LineNumber));
            }
            return result;
        }

        private static List<double> Numbers(string text, string column, int line, List<ConfigIssue> issues)
        {
            var result = new List<double>();
            foreach (string part in (text ?? string.Empty).Split(NumberSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                string p = part.Trim().Replace(',', '.');
                if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0) result.Add(v);
                else issues.Add(new ConfigIssue(Severity.Error, "таблица «Изделия»", line, $"«{column}»: «{part}» — не число (числа через пробел)"));
            }
            return result;
        }

        /// <summary>Первое подходящее изделие сверху.</summary>
        public static InsulationProduct? Match(IEnumerable<InsulationProduct> products, string typeName, string typeComment) =>
            products.FirstOrDefault(p => p.Matches(typeName, typeComment));
    }
}
