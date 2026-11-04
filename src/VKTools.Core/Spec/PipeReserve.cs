using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Text;

namespace VKTools.Core.Spec
{
    /// <summary>Что получает запас: жёсткая труба или изоляция на жёсткой трубе.</summary>
    public enum ReserveTarget
    {
        Pipe,
        Insulation,
    }

    /// <summary>Строка таблицы «Коэффициенты» (Р5, порт K_PIPE / K_INS / EXCLUDE_SYSTEMS PipeQtyReserve v1.0.2).</summary>
    public sealed class ReserveRule
    {
        public ReserveRule(ReserveTarget target, string mask, string systemMask, double? k, int line)
        {
            Target = target;
            Mask = mask;
            SystemMask = systemMask;
            K = k;
            Line = line;
        }

        public ReserveTarget Target { get; }

        /// <summary>Маска имени типа или «Комментариев к типоразмеру»; пусто или «*» — любой тип.</summary>
        public string Mask { get; }

        /// <summary>Маска системы (через «,» — несколько); пусто — любая.</summary>
        public string SystemMask { get; }

        /// <summary>Коэффициент; null — «не трогать» (элементы под строкой пропускаются).</summary>
        public double? K { get; }

        public int Line { get; }

        public bool Matches(ReserveTarget target, string typeName, string typeComment, string system)
        {
            if (target != Target) return false;
            if (!MaskText.Matches(Mask, typeName, typeComment)) return false;
            string systems = SystemMask.Trim();
            if (systems.Length == 0 || systems == "*") return true;
            return systems.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)
                .Any(s => s.IndexOf('*') >= 0 || s.IndexOf('?') >= 0 ? FamilyMask.Matches(s, system ?? string.Empty)
                    : string.Equals(s, (system ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Маски таблиц Р4/Р5: «*» и «?» — маска целиком по имени типа или комментарию; без них — вхождение подстроки.</summary>
    public static class MaskText
    {
        public static bool Matches(string mask, string typeName, string typeComment)
        {
            mask = (mask ?? string.Empty).Trim();
            if (mask.Length == 0 || mask == "*") return true;
            if (mask.IndexOf('*') >= 0 || mask.IndexOf('?') >= 0)
            {
                return FamilyMask.Matches(mask, typeName ?? string.Empty) || FamilyMask.Matches(mask, typeComment ?? string.Empty);
            }
            string hay = ((typeName ?? string.Empty) + " " + (typeComment ?? string.Empty)).ToUpperInvariant();
            return hay.IndexOf(mask.ToUpperInvariant(), StringComparison.Ordinal) >= 0;
        }
    }

    /// <summary>Как хранится количество: число (метры как есть) или длина (внутренние футы).</summary>
    public enum QuantityKind
    {
        Unknown,
        Number,
        Length,
    }

    /// <summary>Числа запаса: единицы количества, значение, k для отчёта. Логика PipeQtyReserve v1.0.2 (после бага ×3,28 в v1.0).</summary>
    public static class ReserveMath
    {
        public const double FeetToMeters = 0.3048;

        /// <summary>Знаков после запятой у записываемого количества (м).</summary>
        public const int Decimals = 4;

        private static readonly double[] KnownFactors = { 1.0, 1.1, 1.2 };

        private static readonly string[] LengthUnits =
        {
            "millimeters", "centimeters", "decimeters", "meters", "feet", "inches", "feetFractionalInches", "fractionalInches", "metersCentimeters",
        };

        /// <summary>
        /// Вид Double-параметра по типу данных и единице без версии («autodesk.spec.aec:number-2.0.0» → «autodesk.spec.aec:number»).
        /// ADSK_Количество в ФОП ADSK — «autodesk.spec.aec:number», не SpecTypeId.Number (gotcha 2026-10-20).
        /// </summary>
        public static QuantityKind Classify(string? specTypeId, string? unitTypeId)
        {
            string spec = Base(specTypeId);
            string unit = Base(unitTypeId);
            if (spec == "autodesk.spec:number" || spec == "autodesk.spec.aec:number" || unit == "autodesk.unit.unit:general") return QuantityKind.Number;
            string tail = unit.Length == 0 ? string.Empty : unit.Substring(unit.LastIndexOf(':') + 1);
            if (spec.EndsWith(":length", StringComparison.Ordinal) || spec.EndsWith(":pipeSize", StringComparison.Ordinal) || LengthUnits.Contains(tail))
            {
                return QuantityKind.Length;
            }
            return QuantityKind.Unknown;
        }

        private static string Base(string? typeId)
        {
            string t = typeId ?? string.Empty;
            int dash = t.IndexOf('-');
            return dash > 0 ? t.Substring(0, dash) : t;
        }

        /// <summary>
        /// Вид по самим значениям: сырое число ≈ длине в метрах × k — «число», ≈ длине в футах × k — «длина».
        /// Нужно ≥ 10 совпадений и перевес втрое; иначе Unknown.
        /// </summary>
        public static QuantityKind Calibrate(IEnumerable<(double Raw, double LengthM)> samples, IEnumerable<double> ks, out int hitsMeters, out int hitsFeet, out int total)
        {
            List<double> factors = KnownFactors.Concat(ks).Distinct().ToList();
            hitsMeters = hitsFeet = total = 0;
            foreach ((double raw, double length) in samples)
            {
                if (length < 0.05) continue;
                total++;
                if (factors.Any(k => Math.Abs(raw / length - k) < 0.02)) hitsMeters++;
                else if (factors.Any(k => Math.Abs(raw / (length / FeetToMeters) - k) < 0.02)) hitsFeet++;
            }
            if (hitsMeters >= 10 && hitsMeters > 3 * hitsFeet) return QuantityKind.Number;
            if (hitsFeet >= 10 && hitsFeet > 3 * hitsMeters) return QuantityKind.Length;
            return QuantityKind.Unknown;
        }

        /// <summary>Тип данных и значения: согласовать. Расходятся или оба не понятны — null и причина.</summary>
        public static QuantityKind? Decide(QuantityKind bySpec, QuantityKind byData, out string? blocked)
        {
            blocked = null;
            if (bySpec != QuantityKind.Unknown && byData != QuantityKind.Unknown && bySpec != byData)
            {
                blocked = $"тип данных говорит «{Text(bySpec)}», а значения в модели — «{Text(byData)}»";
                return null;
            }
            QuantityKind kind = byData != QuantityKind.Unknown ? byData : bySpec;
            if (kind == QuantityKind.Unknown)
            {
                blocked = "не удалось понять, в каких единицах хранится количество";
                return null;
            }
            return kind;
        }

        public static string Text(QuantityKind kind) => kind switch
        {
            QuantityKind.Number => "число (метры как есть)",
            QuantityKind.Length => "длина (футы внутри Revit)",
            _ => "не понятно",
        };

        /// <summary>Количество, м: длина × k, 4 знака.</summary>
        public static double Quantity(double lengthM, double k) => Math.Round(lengthM * k, Decimals, MidpointRounding.AwayFromZero);

        /// <summary>Сырое значение для записи в Double-параметр.</summary>
        public static double Raw(double quantityM, QuantityKind kind) => kind == QuantityKind.Length ? quantityM / FeetToMeters : quantityM;

        /// <summary>Сырое значение → метры.</summary>
        public static double Meters(double raw, QuantityKind kind) => kind == QuantityKind.Length ? raw * FeetToMeters : raw;

        /// <summary>То же значение (допуск 1e-6, как в скрипте).</summary>
        public static bool Same(double a, double b) => Math.Abs(a - b) < 1e-6;

        /// <summary>«12,35» — текстом в спецификацию (если количество — текстовый параметр).</summary>
        public static string Text(double quantityM) => Number(quantityM, 2);

        /// <summary>Какой запас сейчас: «×1», «×1,1», «×1,2», «≈×1,1» (не точно), «пусто», «×0,95».</summary>
        public static string KLabel(double? oldM, double lengthM)
        {
            if (oldM == null) return "пусто";
            if (lengthM < 1e-9) return "длина 0";
            double k = oldM.Value / lengthM;
            foreach (double known in KnownFactors)
            {
                if (Math.Abs(oldM.Value - Quantity(lengthM, known)) < 1e-6) return "×" + Number(known, 2);
                if (Math.Abs(k - known) < 0.005) return "≈×" + Number(known, 2);
            }
            return "×" + Number(k, 2);
        }

        /// <summary>Число с запятой, не больше <paramref name="decimals"/> знаков, без незначащих нулей.</summary>
        public static string Number(double value, int decimals)
        {
            double x = Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            string s = x.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture);
            return s.Replace('.', ',');
        }
    }

    /// <summary>Настройка «Запас» (ключи rsv.* в модели): параметр количества и таблица «Коэффициенты».</summary>
    public sealed class PipeReserveSettings
    {
        public const string TargetColumn = "Категория";
        public const string MaskColumn = "Маска типа";
        public const string SystemColumn = "Системы";
        public const string KColumn = "Запас k";

        public const string PipeLabel = "Трубы";
        public const string InsulationLabel = "Изоляция труб";

        public static readonly IReadOnlyList<string> Columns = new[] { TargetColumn, MaskColumn, SystemColumn, KColumn };

        /// <summary>ТЗ главного специалиста 2026-10-20: трубы и изоляция на трубах — одинаково 1,1.</summary>
        public const string DefaultTable =
            "#schema=1\n"
            + "Категория;Маска типа;Системы;Запас k\n"
            + "Трубы;*;;1,1\n"
            + "Изоляция труб;*;;1,1\n";

        private const string SavedKey = "rsv.saved";
        private const string QtyKey = "rsv.qtyParam";
        private const string TableKey = "rsv.table";

        /// <summary>Параметр количества (по умолчанию ADSK_Количество).</summary>
        public ParamKey? QtyParam { get; set; }

        /// <summary>Таблица «Коэффициенты» текстом CSV.</summary>
        public string Table { get; set; } = DefaultTable;

        public static PipeReserveSettings Default() => new PipeReserveSettings
        {
            QtyParam = ParamKey.Shared(KnownParameters.AdskQuantity.Name, KnownParameters.AdskQuantity.Guid),
        };

        public PipeReserveSettings Clone() => (PipeReserveSettings)MemberwiseClone();

        public string Signature() => $"{QtyParam?.Id}|" + InsulationSettings.Normalize(Table);

        public void StoreTo(SpecProjectSettings settings, bool isDefault)
        {
            if (isDefault)
            {
                foreach (string key in new[] { SavedKey, QtyKey, TableKey }) settings.Set(key, null);
                return;
            }
            settings.Set(SavedKey, "1");
            settings.Set(QtyKey, QtyParam == null ? string.Empty : QtyParam.Id + ";" + QtyParam.Name);
            settings.Set(TableKey, InsulationSettings.Normalize(Table) == InsulationSettings.Normalize(DefaultTable) ? null : Table);
        }

        public static bool IsStored(SpecProjectSettings settings) => settings.Get(SavedKey) != null;

        public static PipeReserveSettings? Parse(SpecProjectSettings settings, List<ConfigIssue> issues)
        {
            PipeReserveSettings result = Default();
            bool ok = true;
            string? qty = settings.Get(QtyKey);
            if (qty != null) result.QtyParam = StoredKey.Parse(qty, "настройка проекта: запас", "параметр количества", issues, ref ok);
            string? table = settings.Get(TableKey);
            if (table != null)
            {
                result.Table = table;
                var tableIssues = new List<ConfigIssue>();
                ParseRules(table, tableIssues);
                if (tableIssues.Any(i => i.Severity == Severity.Error))
                {
                    issues.AddRange(tableIssues);
                    ok = false;
                }
            }
            return ok ? result : null;
        }

        /// <summary>Разбор таблицы «Коэффициенты». Ошибки — с номером строки.</summary>
        public static List<ReserveRule> ParseRules(string text, List<ConfigIssue> issues)
        {
            const string file = "таблица «Коэффициенты»";
            var result = new List<ReserveRule>();
            CsvTable table;
            try
            {
                table = CsvTable.Parse((text ?? string.Empty).TrimStart('﻿'));
            }
            catch (FormatException ex)
            {
                issues.Add(new ConfigIssue(Severity.Error, file, null, ex.Message));
                return result;
            }
            foreach (string column in new[] { TargetColumn, KColumn })
            {
                if (!table.HasColumn(column))
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, null, $"нет колонки «{column}»"));
                    return result;
                }
            }
            foreach (CsvRow row in table.Rows)
            {
                string what = row.Get(TargetColumn).Trim();
                ReserveTarget target;
                if (what.StartsWith("изол", StringComparison.OrdinalIgnoreCase)) target = ReserveTarget.Insulation;
                else if (what.StartsWith("труб", StringComparison.OrdinalIgnoreCase)) target = ReserveTarget.Pipe;
                else
                {
                    issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, $"«{TargetColumn}»: «{what}» — нужно «{PipeLabel}» или «{InsulationLabel}»"));
                    continue;
                }
                string kText = row.Get(KColumn).Trim();
                double? k = null;
                if (kText.Length > 0)
                {
                    if (!double.TryParse(kText.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v <= 0 || v > 3)
                    {
                        issues.Add(new ConfigIssue(Severity.Error, file, row.LineNumber, $"«{KColumn}»: «{kText}» — нужно число от 0 до 3 (1,1 = +10 %), пусто — не трогать"));
                        continue;
                    }
                    if (v < 1) issues.Add(new ConfigIssue(Severity.Warning, file, row.LineNumber, $"«{KColumn}» = {kText} меньше 1: количество станет меньше длины"));
                    k = v;
                }
                result.Add(new ReserveRule(target, row.Get(MaskColumn).Trim(), row.Get(SystemColumn).Trim(), k, row.LineNumber));
            }
            return result;
        }

        /// <summary>Первая подходящая строка сверху; null — элемент не трогается (строки нет).</summary>
        public static ReserveRule? Match(IEnumerable<ReserveRule> rules, ReserveTarget target, string typeName, string typeComment, string system) =>
            rules.FirstOrDefault(r => r.Matches(target, typeName, typeComment, system));
    }

    /// <summary>Ключ параметра, сохранённый в модели как «id;имя».</summary>
    internal static class StoredKey
    {
        public static ParamKey? Parse(string? stored, string file, string what, List<ConfigIssue> issues, ref bool ok)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            int semicolon = stored!.IndexOf(';');
            string id = semicolon < 0 ? stored : stored.Substring(0, semicolon);
            ParamKey? key = ParamKey.Parse(id, semicolon < 0 ? string.Empty : stored.Substring(semicolon + 1));
            if (key == null)
            {
                issues.Add(new ConfigIssue(Severity.Error, file, null, $"{what}: непонятный ключ «{id}»"));
                ok = false;
            }
            return key;
        }
    }
}
