using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using VKTools.Core.Text;

namespace VKTools.Core.Calc
{
    /// <summary>От чего считается ресурс (колонка «База» в calc_resources.csv).</summary>
    public enum CalcBase
    {
        /// <summary>m2 — площадь наружной поверхности прямых труб, м².</summary>
        Area,

        /// <summary>m — длина прямых труб, м.</summary>
        Length,

        /// <summary>pcs — число опор по шагу из правила трубы.</summary>
        Supports,

        /// <summary>sup_kg — масса креплений по таблице креплений, кг (калькулятор ведущего).</summary>
        SupportMass,

        /// <summary>Запас на штучные позиции строки спецификации (не из таблицы ресурсов: позиции строит плагин).</summary>
        Reserve,

        /// <summary>cord — шнур в гильзах, м в один слой: π·(Dнар + добавка)·Lгильз / шаг витка («Расчет гильз и шнура.xlsx»).</summary>
        Cord,
    }

    /// <summary>Строка calc_cord.csv: какой шнур идёт в гильзы трубы этого диапазона DN.</summary>
    public sealed class CordRow
    {
        public CordRow(int line, double dnFrom, double dnTo, string code, string note)
        {
            Line = line;
            DnFrom = dnFrom;
            DnTo = dnTo;
            Code = code;
            Note = note;
        }

        public int Line { get; }

        public double DnFrom { get; }

        public double DnTo { get; }

        public string Code { get; }

        public string Note { get; }

        public bool Matches(double dnMm) => dnMm >= DnFrom - 0.5 && dnMm <= DnTo + 0.5;
    }

    /// <summary>Ресурс: строка calc_resources.csv. Код = имя типоразмера служебного семейства.</summary>
    public sealed class CalcResource
    {
        public CalcResource(int line, string code, string name, string unit, CalcBase basis, double rate, double layers, double reserve,
            int order, double round, string basisText, IReadOnlyList<KeyValuePair<string, string>> extras)
        {
            Line = line;
            Code = code;
            Name = name;
            Unit = unit;
            Base = basis;
            Rate = rate;
            Layers = layers;
            Reserve = reserve;
            Order = order;
            Round = round;
            BasisText = basisText;
            Extras = extras;
        }

        public int Line { get; }

        public string Code { get; }

        public string Name { get; }

        public string Unit { get; }

        public CalcBase Base { get; }

        /// <summary>Расход на единицу базы.</summary>
        public double Rate { get; }

        public double Layers { get; }

        /// <summary>Коэффициент запаса (1,1 = +10 %).</summary>
        public double Reserve { get; }

        public int Order { get; }

        /// <summary>Шаг округления вверх; 0 — не округлять.</summary>
        public double Round { get; }

        /// <summary>Колонка «Основание»: откуда норма — в SYS_Основание.</summary>
        public string BasisText { get; }

        /// <summary>Колонки после «Основание»: имя параметра позиции → значение (ADSK_Марка, ADSK_Завод-изготовитель…).</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Extras { get; }

        /// <summary>ADSK_Группирование позиции: «9.1», «9.2»… по «Порядку» (назначается при разборе таблицы).</summary>
        public string Group { get; internal set; } = string.Empty;

        /// <summary>Количество до округления: база × расход × слоёв × запас.</summary>
        public double Quantity(double baseValue) => baseValue * Rate * Layers * Reserve;

        /// <summary>
        /// Типоразмер позиции запаса строки спецификации: наименование, единица и группа — со штучных позиций строки,
        /// <paramref name="extras"/> — их поля сортировки, которые в проекте параметры типа.
        /// </summary>
        public static CalcResource ForReserve(string code, string name, string unit, string group, IReadOnlyList<KeyValuePair<string, string>> extras) =>
            new CalcResource(0, code, name, unit, CalcBase.Reserve, 1, 1, 1, int.MaxValue, 0, string.Empty, extras) { Group = group };
    }

    /// <summary>Строка calc_reserve.csv: запас на штучные позиции семейства (и типоразмера) с округлением вверх по строке спецификации.</summary>
    public sealed class ReserveRow
    {
        public ReserveRow(int line, string familyMask, string typeMask, double percent, double round, string note)
        {
            Line = line;
            FamilyMask = familyMask;
            TypeMask = typeMask;
            Percent = percent;
            Round = round;
            Note = note;
        }

        public int Line { get; }

        public string FamilyMask { get; }

        /// <summary>Маска типоразмера; «*» — любой.</summary>
        public string TypeMask { get; }

        public double Percent { get; }

        public double Round { get; }

        public string Note { get; }

        public bool Matches(string family, string type) => Spec.FamilyMask.Matches(FamilyMask, family) && (TypeMask == "*" || Spec.FamilyMask.Matches(TypeMask, type));

        /// <summary>Итог строки: ⌈N × (1 + %/100)⌉ до шага; запас = итог − N (REQ-CALC-010).</summary>
        public double Total(double n) => CalcEngine.RoundUp(n * (1 + Percent / 100.0), Round);
    }

    /// <summary>Признак изоляции в правиле трубы.</summary>
    public enum InsulationMatch
    {
        Any,
        Yes,
        No,
    }

    /// <summary>Правило для прямой трубы: строка calc_pipe_rules.csv. Проверяются сверху вниз, берётся первое подходящее.</summary>
    public sealed class PipeRule
    {
        public PipeRule(int line, string segmentMask, string nameMask, double? dnFrom, double? dnTo, InsulationMatch insulation,
            double? stepHorizontal, double? stepVertical, IReadOnlyList<string> codes, string note)
        {
            Line = line;
            SegmentMask = segmentMask;
            NameMask = nameMask;
            DnFrom = dnFrom;
            DnTo = dnTo;
            Insulation = insulation;
            StepHorizontal = stepHorizontal;
            StepVertical = stepVertical;
            Codes = codes;
            Note = note;
        }

        public int Line { get; }

        public string SegmentMask { get; }

        /// <summary>Маска ADSK_Наименование трубы.</summary>
        public string NameMask { get; }

        public double? DnFrom { get; }

        public double? DnTo { get; }

        public InsulationMatch Insulation { get; }

        public double? StepHorizontal { get; }

        public double? StepVertical { get; }

        /// <summary>Коды ресурсов; пусто — правило «без ресурсов» (трубы исключены осознанно).</summary>
        public IReadOnlyList<string> Codes { get; }

        public string Note { get; }

        public bool Matches(string segment, string name, double dnMm, bool insulated)
        {
            if (!MaskMatches(SegmentMask, segment)) return false;
            if (!MaskMatches(NameMask, name)) return false;
            if (DnFrom.HasValue && dnMm < DnFrom.Value - 0.5) return false;
            if (DnTo.HasValue && dnMm > DnTo.Value + 0.5) return false;
            if (Insulation == InsulationMatch.Yes && !insulated) return false;
            if (Insulation == InsulationMatch.No && insulated) return false;
            return true;
        }

        private static bool MaskMatches(string mask, string value) => mask.Length == 0 || mask == "*" || FamilyMask.Matches(mask, value ?? string.Empty);
    }

    /// <summary>Строка таблицы креплений: шаг и масса одного крепления по DN, для изолированной и неизолированной трубы.</summary>
    public sealed class SupportRow
    {
        public SupportRow(int line, double dn, double stepInsulated, double stepBare, double massInsulated, double massBare, string note)
        {
            Line = line;
            Dn = dn;
            StepInsulated = stepInsulated;
            StepBare = stepBare;
            MassInsulated = massInsulated;
            MassBare = massBare;
            Note = note;
        }

        public int Line { get; }

        public double Dn { get; }

        public double StepInsulated { get; }

        public double StepBare { get; }

        public double MassInsulated { get; }

        public double MassBare { get; }

        public string Note { get; }

        public double Step(bool insulated) => insulated ? StepInsulated : StepBare;

        public double Mass(bool insulated) => insulated ? MassInsulated : MassBare;
    }

    /// <summary>
    /// Таблицы расчётных позиций: ресурсы, правила для труб, крепления. Формат — как у Dynamo-эталона spec_calc_items v0.4.1
    /// (resources.csv, pipe_rules.csv, supports.csv), плюс строка «#schema=1». Файлы эталона без неё тоже читаются (импорт).
    /// </summary>
    public static class CalcTables
    {
        public const string ResourcesFile = "calc_resources.csv";
        public const string PipeRulesFile = "calc_pipe_rules.csv";
        public const string SupportsFile = "calc_supports.csv";
        public const string CordFile = "calc_cord.csv";
        public const string ReserveFile = "calc_reserve.csv";

        public static readonly string[] ResourceColumns = { "Код", "Наименование", "Ед_изм", "База", "Расход", "Слоёв", "Запас", "Порядок", "Округление", "Основание" };
        public static readonly string[] PipeRuleColumns = { "Сегмент", "Наименование", "DN_от", "DN_до", "Изоляция", "Шаг_гор_м", "Шаг_верт_м", "Ресурсы", "Примечание" };
        public static readonly string[] SupportColumns = { "DN", "Шаг_изол_м", "Шаг_неизол_м", "Масса_изол_кг", "Масса_неизол_кг", "Примечание" };
        public static readonly string[] CordColumns = { "DN_от", "DN_до", "Ресурс", "Примечание" };
        public static readonly string[] ReserveColumns = { "Семейство", "Тип", "Запас_%", "Округление", "Примечание" };

        public static readonly RuleFileSpec Resources = new RuleFileSpec(ResourcesFile, 1, "Код", "Наименование", "Ед_изм", "База", "Расход", "Порядок");
        public static readonly RuleFileSpec PipeRules = new RuleFileSpec(PipeRulesFile, 1, "Ресурсы");
        public static readonly RuleFileSpec Supports = new RuleFileSpec(SupportsFile, 1, "DN", "Шаг_изол_м", "Шаг_неизол_м", "Масса_изол_кг", "Масса_неизол_кг");
        public static readonly RuleFileSpec Cord = new RuleFileSpec(CordFile, 1, "DN_от", "DN_до", "Ресурс");
        public static readonly RuleFileSpec Reserve = new RuleFileSpec(ReserveFile, 1, "Семейство", "Запас_%");

        private static readonly char[] CodeSeparators = { '+', ';', ',', ' ' };

        private static readonly Dictionary<string, CalcBase> BaseAliases = new Dictionary<string, CalcBase>(StringComparer.OrdinalIgnoreCase)
        {
            ["m2"] = CalcBase.Area, ["м2"] = CalcBase.Area, ["м²"] = CalcBase.Area, ["площадь"] = CalcBase.Area,
            ["m"] = CalcBase.Length, ["м"] = CalcBase.Length, ["длина"] = CalcBase.Length,
            ["pcs"] = CalcBase.Supports, ["шт"] = CalcBase.Supports, ["опоры"] = CalcBase.Supports,
            ["sup_kg"] = CalcBase.SupportMass, ["крепления_кг"] = CalcBase.SupportMass, ["крепления"] = CalcBase.SupportMass,
            ["cord"] = CalcBase.Cord, ["шнур"] = CalcBase.Cord,
        };

        public static string BaseCode(CalcBase basis) => basis switch
        {
            CalcBase.Area => "m2",
            CalcBase.Length => "m",
            CalcBase.Supports => "pcs",
            CalcBase.Cord => "cord",
            CalcBase.Reserve => "reserve",
            _ => "sup_kg",
        };

        public static string BaseLabel(CalcBase basis) => basis switch
        {
            CalcBase.Area => "м²",
            CalcBase.Length => "м",
            CalcBase.Supports => "опор",
            CalcBase.Cord => "м шнура в 1 слой",
            CalcBase.Reserve => "в строке",
            _ => "кг креплений",
        };

        /// <summary>
        /// Текст таблицы (файл, импорт, значение в модели) → таблица. Нет «#schema» — считаем схемой 1: так читаются файлы
        /// Dynamo-эталона. Схема новее — ошибка.
        /// </summary>
        public static CsvTable? ParseText(string text, RuleFileSpec spec, string source, List<ConfigIssue> issues)
        {
            CsvTable table = CsvTable.Parse((text ?? string.Empty).TrimStart('\uFEFF'));
            if (table.SchemaVersion.HasValue && table.SchemaVersion.Value != spec.SchemaVersion)
            {
                issues.Add(new ConfigIssue(Severity.Error, source, null, $"схема {table.SchemaVersion}, VK Tools знает {spec.SchemaVersion}"));
                return null;
            }
            bool ok = true;
            foreach (string column in spec.RequiredColumns)
            {
                if (!HasColumn(table, column))
                {
                    issues.Add(new ConfigIssue(Severity.Error, source, null, $"нет колонки «{column}»"));
                    ok = false;
                }
            }
            return ok ? table : null;
        }

        /// <summary>Ресурсы по порядку «Порядок, Код»; группа «9.1», «9.2»… (ширина номера общая: 9.01…9.12 при 10+ ресурсах).</summary>
        public static List<CalcResource> ParseResources(CsvTable table, string source, string groupPrefix, List<ConfigIssue> issues)
        {
            var result = new List<CalcResource>();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<string> extraColumns = ExtraColumns(table);
            foreach (CsvRow row in table.Rows)
            {
                string code = Get(row, "Код").ToUpperInvariant();
                try
                {
                    if (code.Length == 0 || code.IndexOf('|') >= 0) throw new FormatException("пустой код или символ «|» в коде");
                    if (seen.TryGetValue(code, out int other)) throw new FormatException($"код {code} уже задан в строке {other}");
                    if (!BaseAliases.TryGetValue(Get(row, "База"), out CalcBase basis)) throw new FormatException($"База «{Get(row, "База")}»: допустимо m2, m, pcs, sup_kg, cord");
                    double rate = Number(row, "Расход", null) ?? throw new FormatException("Расход должен быть числом ≥ 0");
                    double layers = Number(row, "Слоёв", 1.0) ?? 1.0;
                    double reserve = Number(row, "Запас", 1.0) ?? 1.0;
                    if (rate < 0) throw new FormatException("Расход должен быть числом ≥ 0");
                    if (layers <= 0 || reserve <= 0) throw new FormatException("Слоёв и Запас должны быть больше нуля");
                    double? orderValue = Number(row, "Порядок", null);
                    if (!orderValue.HasValue) throw new FormatException("Порядок должен быть целым числом");
                    double round = Number(row, "Округление", basis == CalcBase.Supports ? 1.0 : 0.0) ?? 0.0;
                    if (round < 0) throw new FormatException("Округление должно быть ≥ 0");
                    string name = Get(row, "Наименование");
                    string unit = Get(row, "Ед_изм");
                    if (name.Length == 0 || unit.Length == 0) throw new FormatException("пустое Наименование или Ед_изм");
                    var extras = extraColumns.Select(c => new KeyValuePair<string, string>(c, Get(row, c))).ToList();
                    seen[code] = row.LineNumber;
                    result.Add(new CalcResource(row.LineNumber, code, name, unit, basis, rate, layers, reserve, (int)Math.Round(orderValue.Value),
                        round, Get(row, "Основание"), extras));
                }
                catch (FormatException ex)
                {
                    issues.Add(new ConfigIssue(Severity.Error, source, row.LineNumber, ex.Message));
                }
            }
            result = result.OrderBy(r => r.Order).ThenBy(r => r.Code, StringComparer.Ordinal).ToList();
            int width = result.Count.ToString(CultureInfo.InvariantCulture).Length;
            for (int i = 0; i < result.Count; i++)
            {
                result[i].Group = groupPrefix.Length == 0 ? string.Empty : groupPrefix + "." + (i + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
            }
            return result;
        }

        /// <summary>Колонки ресурсов после «Основание» с непустым заголовком: имена параметров позиции.</summary>
        public static List<string> ExtraColumns(CsvTable table)
        {
            var known = new HashSet<string>(ResourceColumns, StringComparer.OrdinalIgnoreCase) { "Слоев" };
            return table.Columns.Where(c => c.Length > 0 && !known.Contains(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static List<PipeRule> ParsePipeRules(CsvTable table, string source, IReadOnlyCollection<CalcResource> resources, List<ConfigIssue> issues)
        {
            var result = new List<PipeRule>();
            if (!HasColumn(table, "Сегмент") && !HasColumn(table, "Наименование"))
            {
                issues.Add(new ConfigIssue(Severity.Error, source, null, "нужна колонка «Сегмент» или «Наименование»"));
                return result;
            }
            var byCode = resources.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
            foreach (CsvRow row in table.Rows)
            {
                try
                {
                    string insulationText = Get(row, "Изоляция").ToLowerInvariant();
                    InsulationMatch insulation = insulationText switch
                    {
                        "" or "*" => InsulationMatch.Any,
                        "да" or "yes" => InsulationMatch.Yes,
                        "нет" or "no" => InsulationMatch.No,
                        _ => throw new FormatException($"Изоляция «{insulationText}»: допустимо *, да, нет"),
                    };
                    string rawCodes = Get(row, "Ресурсы");
                    List<string> codes = rawCodes.Length == 0 || rawCodes == "-"
                        ? new List<string>()
                        : rawCodes.Split(CodeSeparators, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim().ToUpperInvariant()).ToList();
                    List<string> unknown = codes.Where(c => !byCode.ContainsKey(c)).ToList();
                    if (unknown.Count > 0) throw new FormatException("нет в таблице ресурсов: " + string.Join(", ", unknown));
                    List<string> cords = codes.Where(c => byCode[c].Base == CalcBase.Cord).ToList();
                    if (cords.Count > 0) throw new FormatException($"{string.Join(", ", cords)} — шнур считается по гильзам (вкладка «Гильзы и шнур»), в правила труб не входит");
                    double? stepH = Number(row, "Шаг_гор_м", null);
                    double? stepV = Number(row, "Шаг_верт_м", null);
                    if (codes.Any(c => byCode[c].Base == CalcBase.Supports) && (!stepH.HasValue || stepH.Value <= 0))
                    {
                        throw new FormatException("для ресурсов «на опору» (pcs) нужен Шаг_гор_м больше нуля");
                    }
                    if (stepV.HasValue && stepV.Value <= 0) throw new FormatException("Шаг_верт_м должен быть больше нуля или пусто");
                    string segment = Get(row, "Сегмент");
                    string name = Get(row, "Наименование");
                    result.Add(new PipeRule(row.LineNumber, segment.Length == 0 ? "*" : segment, name.Length == 0 ? "*" : name,
                        Number(row, "DN_от", null), Number(row, "DN_до", null), insulation, stepH, stepV, codes, Get(row, "Примечание")));
                }
                catch (FormatException ex)
                {
                    issues.Add(new ConfigIssue(Severity.Error, source, row.LineNumber, ex.Message));
                }
            }
            return result;
        }

        /// <summary>Строки по возрастанию DN. Повтор DN — ошибка.</summary>
        public static List<SupportRow> ParseSupports(CsvTable table, string source, List<ConfigIssue> issues)
        {
            var result = new List<SupportRow>();
            var seen = new Dictionary<long, int>();
            foreach (CsvRow row in table.Rows)
            {
                try
                {
                    double?[] values = { Number(row, "DN", null), Number(row, "Шаг_изол_м", null), Number(row, "Шаг_неизол_м", null),
                        Number(row, "Масса_изол_кг", null), Number(row, "Масса_неизол_кг", null) };
                    if (values.Any(v => !v.HasValue || v.Value <= 0)) throw new FormatException("DN, шаги и массы должны быть числами больше нуля");
                    long dn = (long)Math.Round(values[0]!.Value);
                    if (seen.TryGetValue(dn, out int other)) throw new FormatException($"DN{dn} уже задан в строке {other}");
                    seen[dn] = row.LineNumber;
                    result.Add(new SupportRow(row.LineNumber, values[0]!.Value, values[1]!.Value, values[2]!.Value, values[3]!.Value, values[4]!.Value, Get(row, "Примечание")));
                }
                catch (FormatException ex)
                {
                    issues.Add(new ConfigIssue(Severity.Error, source, row.LineNumber, ex.Message));
                }
            }
            return result.OrderBy(r => r.Dn).ToList();
        }

        /// <summary>Шнур по диапазонам DN. Ресурс — из таблицы ресурсов с базой cord; диапазоны не пересекаются.</summary>
        public static List<CordRow> ParseCord(CsvTable table, string source, IReadOnlyCollection<CalcResource> resources, List<ConfigIssue> issues)
        {
            var result = new List<CordRow>();
            var byCode = resources.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
            foreach (CsvRow row in table.Rows)
            {
                try
                {
                    double? from = Number(row, "DN_от", null);
                    double? to = Number(row, "DN_до", null);
                    if (!from.HasValue || !to.HasValue || from.Value <= 0 || to.Value < from.Value) throw new FormatException("DN_от и DN_до — числа, DN_до не меньше DN_от");
                    string code = Get(row, "Ресурс").ToUpperInvariant();
                    if (!byCode.TryGetValue(code, out CalcResource? resource)) throw new FormatException($"ресурса «{code}» нет в таблице ресурсов");
                    if (resource.Base != CalcBase.Cord) throw new FormatException($"у ресурса {code} База «{BaseCode(resource.Base)}», нужна cord");
                    CordRow? overlap = result.FirstOrDefault(c => from.Value <= c.DnTo && to.Value >= c.DnFrom);
                    if (overlap != null) throw new FormatException($"DN{Format(from.Value)}–{Format(to.Value)} пересекается со строкой {overlap.Line - 2}");
                    result.Add(new CordRow(row.LineNumber, from.Value, to.Value, resource.Code, Get(row, "Примечание")));
                }
                catch (FormatException ex)
                {
                    issues.Add(new ConfigIssue(Severity.Error, source, row.LineNumber, ex.Message));
                }
            }
            return result.OrderBy(r => r.DnFrom).ToList();
        }

        /// <summary>Запас на штучные: первая подходящая строка сверху.</summary>
        public static List<ReserveRow> ParseReserve(CsvTable table, string source, List<ConfigIssue> issues)
        {
            var result = new List<ReserveRow>();
            foreach (CsvRow row in table.Rows)
            {
                try
                {
                    string family = Get(row, "Семейство");
                    if (family.Length == 0) throw new FormatException("пустое Семейство (имя или маска)");
                    double? percent = Number(row, "Запас_%", null);
                    if (!percent.HasValue || percent.Value <= 0 || percent.Value > 1000) throw new FormatException("Запас_% — число больше 0 (20 = +20 %)");
                    double round = Number(row, "Округление", 1.0) ?? 1.0;
                    if (round <= 0) throw new FormatException("Округление — число больше 0 (1 = целые штуки)");
                    string type = Get(row, "Тип");
                    result.Add(new ReserveRow(row.LineNumber, family, type.Length == 0 ? "*" : type, percent.Value, round, Get(row, "Примечание")));
                }
                catch (FormatException ex)
                {
                    issues.Add(new ConfigIssue(Severity.Error, source, row.LineNumber, ex.Message));
                }
            }
            return result;
        }

        public static CordRow? FindCord(IEnumerable<CordRow> rows, double dnMm) => rows.FirstOrDefault(r => r.Matches(dnMm));

        /// <summary>Строка таблицы с тем же DN (±0,5 мм). Ближайшую не подбираем: нет строки — нет нормы.</summary>
        public static SupportRow? FindSupport(IEnumerable<SupportRow> rows, double dnMm) => rows.FirstOrDefault(r => Math.Abs(r.Dn - dnMm) <= 0.5);

        /// <summary>Ближайшая по DN строка (масса для трубы не из стального ряда, Dн110 пластика → DN100); дальше 25 % за краем таблицы — нет.</summary>
        public static SupportRow? NearestSupport(IEnumerable<SupportRow> rows, double dnMm)
        {
            List<SupportRow> list = rows.ToList();
            if (list.Count == 0 || dnMm > list.Max(r => r.Dn) * 1.25 || dnMm < list.Min(r => r.Dn) * 0.75) return null;
            return list.OrderBy(r => Math.Abs(r.Dn - dnMm)).ThenBy(r => r.Dn).First();
        }

        /// <summary>Число из ячейки: запятая или точка, пробелы внутри. Пусто — <paramref name="empty"/>; не число — FormatException.</summary>
        public static double? ParseNumber(string text, double? empty)
        {
            string s = (text ?? string.Empty).Replace(" ", string.Empty).Replace(" ", string.Empty).Replace(',', '.').Trim();
            if (s.Length == 0) return empty;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && !double.IsNaN(v) && !double.IsInfinity(v)) return v;
            throw new FormatException($"«{text}» — не число");
        }

        /// <summary>Число для людей: запятая, без хвостовых нулей.</summary>
        public static string Format(double value, int digits = 3)
        {
            string s = Math.Round(value, digits, MidpointRounding.AwayFromZero).ToString("0." + new string('#', Math.Max(1, digits)), CultureInfo.InvariantCulture);
            return s.Replace('.', ',');
        }

        private static double? Number(CsvRow row, string column, double? empty)
        {
            try
            {
                return ParseNumber(Get(row, column), empty);
            }
            catch (FormatException)
            {
                throw new FormatException($"«{column}»: «{Get(row, column)}» — не число");
            }
        }

        /// <summary>Значение колонки; «Слоёв» читается и как «Слоев» (Excel и ручной ввод теряют «ё»).</summary>
        private static string Get(CsvRow row, string column)
        {
            string value = row.Get(column);
            if (value.Length == 0 && column.IndexOf('ё') >= 0) value = row.Get(column.Replace('ё', 'е'));
            return value.Trim();
        }

        private static bool HasColumn(CsvTable table, string column) =>
            table.HasColumn(column) || (column.IndexOf('ё') >= 0 && table.HasColumn(column.Replace('ё', 'е')));
    }
}
