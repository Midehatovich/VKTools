using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Text;

namespace VKTools.Core.Calc
{
    /// <summary>Прямая труба, прочитанная из модели: всё, что нужно расчёту, без объектов Revit.</summary>
    public sealed class PipeSample
    {
        public long Id { get; set; }

        /// <summary>Значение параметра системы (LSR_Имя системы) — так строки делятся в спецификации.</summary>
        public string System { get; set; } = string.Empty;

        /// <summary>ADSK_Наименование трубы (для правил по маске).</summary>
        public string Name { get; set; } = string.Empty;

        public string Segment { get; set; } = string.Empty;

        public double DnMm { get; set; }

        public double OdMm { get; set; }

        public double LengthM { get; set; }

        public bool Insulated { get; set; }

        public bool Vertical { get; set; }

        /// <summary>Стадия возведения (Id), -1 — нет.</summary>
        public long Phase { get; set; } = -1;

        /// <summary>Уровень (Id), -1 — нет.</summary>
        public long Level { get; set; } = -1;

        /// <summary>ADSK_Количество трубы, м — для сверки с длиной; null — не заполнено.</summary>
        public double? Quantity { get; set; }

        /// <summary>Значения полей фильтров спецификации у трубы: параметр → ключ значения (как его сравнивать).</summary>
        public Dictionary<string, string> Copy { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Что должно быть в модели: одна служебная позиция на «область | система | ресурс».</summary>
    public sealed class CalcTarget
    {
        public CalcTarget(string scope, string system, CalcResource resource, double baseValue)
        {
            Scope = scope;
            System = system;
            Resource = resource;
            Base = baseValue;
            Quantity = resource.Quantity(baseValue);
            Auto = CalcEngine.RoundUp(Quantity, resource.Round);
        }

        public string Scope { get; }

        public string System { get; }

        public CalcResource Resource { get; }

        public string Code => Resource.Code;

        public string Key => CalcEngine.MakeKey(Scope, System, Resource.Code);

        /// <summary>База: м², м, опор или кг креплений.</summary>
        public double Base { get; }

        /// <summary>Расчёт до округления — в SYS_Кол-во расчёт.</summary>
        public double Quantity { get; }

        /// <summary>Расчёт с округлением вверх.</summary>
        public double Auto { get; }

        /// <summary>Как посчитано — в SYS_Основание.</summary>
        public string Basis { get; set; } = string.Empty;

        /// <summary>Преобладающая стадия и уровень труб системы — для новой позиции.</summary>
        public long Phase { get; set; } = -1;

        public long Level { get; set; } = -1;

        /// <summary>Поля фильтров: преобладающее значение у труб системы (ключ значения).</summary>
        public Dictionary<string, string> Copy { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Сводка по системе области: для отчёта и сверки с Dynamo.</summary>
    public sealed class CalcSystemSummary
    {
        public CalcSystemSummary(string scope, string system)
        {
            Scope = scope;
            System = system;
        }

        public string Scope { get; }

        public string System { get; }

        public int Pipes { get; set; }

        public double Length { get; set; }

        public double Area { get; set; }

        public double AreaByRules { get; set; }

        public int Supports { get; set; }

        /// <summary>Расшифровка креплений: «DN50 изол.: ΣL = 12,3 м / шаг 3 м = 5 шт × 2,43 кг = 12,15 кг».</summary>
        public List<string> SupportLines { get; } = new List<string>();
    }

    /// <summary>Замечание расчёта, сгруппированное по тексту: одна строка на причину, элементы копятся.</summary>
    public sealed class CalcIssue
    {
        public CalcIssue(Severity severity, string message, string action)
        {
            Severity = severity;
            Message = message;
            Action = action;
        }

        public Severity Severity { get; }

        public string Message { get; }

        public string Action { get; }

        public List<long> Ids { get; } = new List<long>();

        public int Count { get; set; }
    }

    /// <summary>Замечания расчёта без повторов.</summary>
    public sealed class CalcIssueList
    {
        private readonly Dictionary<(Severity, string), CalcIssue> _byText = new Dictionary<(Severity, string), CalcIssue>();

        public List<CalcIssue> Items { get; } = new List<CalcIssue>();

        public CalcIssue Add(Severity severity, string message, string action = "", long? id = null)
        {
            if (!_byText.TryGetValue((severity, message), out CalcIssue? issue))
            {
                issue = new CalcIssue(severity, message, action);
                _byText[(severity, message)] = issue;
                Items.Add(issue);
            }
            issue.Count++;
            if (id.HasValue && issue.Ids.Count < 5000) issue.Ids.Add(id.Value);
            return issue;
        }
    }

    /// <summary>Настройки расчёта, не зависящие от таблиц.</summary>
    public sealed class CalcEngineOptions
    {
        /// <summary>Труба (или группа креплений) короче — без своей опоры: её держат соседние участки.</summary>
        public double ShortPipeM { get; set; } = 0.3;

        /// <summary>Сверять сумму ADSK_Количество труб с длиной по модели.</summary>
        public bool CheckQuantity { get; set; } = true;

        public string QuantityParameter { get; set; } = "ADSK_Количество";
    }

    /// <summary>Результат расчёта по всем областям.</summary>
    public sealed class CalcResult
    {
        public List<CalcTarget> Targets { get; } = new List<CalcTarget>();

        public List<CalcSystemSummary> Systems { get; } = new List<CalcSystemSummary>();

        public CalcIssueList Issues { get; } = new CalcIssueList();

        public int PipesWithoutRule { get; set; }

        /// <summary>Труба → правило (номер строки), для таблицы сверки.</summary>
        public Dictionary<long, int> RuleOfPipe { get; } = new Dictionary<long, int>();
    }

    /// <summary>
    /// Расчёт по трубам области (порт spec_calc_items v0.4.1, разделы 5–6): площадь S = π·Dнар·L, правило трубы, базы ресурсов,
    /// крепления N = ⌈ΣL / шаг⌉ по группе «система · DN · изоляция», масса = N × m. Модель не читает: трубы приходят готовыми.
    /// </summary>
    public static class CalcEngine
    {
        private const double Eps = 1e-9;

        public static string MakeKey(string scope, string system, string code) => scope + "|" + system + "|" + code;

        /// <summary>Область ключа «область|система|код»; null — ключ не этого формата.</summary>
        public static string? KeyScope(string key)
        {
            string[] parts = key.Split('|');
            return parts.Length == 3 ? parts[0] : null;
        }

        /// <summary>Округление вверх до шага; шаг 0 — без округления. Шум плавающей точки (5,0000000001) вверх не тянет.</summary>
        public static double RoundUp(double value, double step)
        {
            if (step <= 0) return value;
            double n = Math.Ceiling(value / step - Eps);
            return Math.Round(n * step, 6);
        }

        /// <summary>Число опор участка: промежуточные + одна у начала; короткие вставки своей опоры не получают.</summary>
        public static int SupportsOnPipe(double lengthM, double? stepM, double shortM)
        {
            if (!stepM.HasValue || stepM.Value <= 0 || lengthM < shortM) return 0;
            return (int)Math.Ceiling(lengthM / stepM.Value - Eps);
        }

        /// <param name="scopes">Область (имя спецификации или «МОДЕЛЬ») → её трубы. Труба в двух областях считается в обеих.</param>
        public static CalcResult Calculate(IEnumerable<KeyValuePair<string, IReadOnlyList<PipeSample>>> scopes, IReadOnlyList<CalcResource> resources,
            IReadOnlyList<PipeRule> rules, IReadOnlyList<SupportRow> supports, CalcEngineOptions options)
        {
            var result = new CalcResult();
            var byCode = resources.ToDictionary(r => r.Code, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IReadOnlyList<PipeSample>> scope in scopes)
            {
                CalculateScope(scope.Key, scope.Value, byCode, rules, supports, options, result);
            }
            return result;
        }

        private static void CalculateScope(string scope, IReadOnlyList<PipeSample> pipes, Dictionary<string, CalcResource> resources,
            IReadOnlyList<PipeRule> rules, IReadOnlyList<SupportRow> supports, CalcEngineOptions options, CalcResult result)
        {
            var systems = new SortedDictionary<string, SystemAcc>(StringComparer.Ordinal);
            foreach (PipeSample pipe in pipes)
            {
                if (!systems.TryGetValue(pipe.System, out SystemAcc? acc))
                {
                    acc = new SystemAcc(new CalcSystemSummary(scope, pipe.System));
                    systems[pipe.System] = acc;
                }
                double area = Math.PI * (pipe.OdMm / 1000.0) * pipe.LengthM;
                acc.Summary.Pipes++;
                acc.Summary.Length += pipe.LengthM;
                acc.Summary.Area += area;
                if (pipe.Phase > 0) Count(acc.Phases, pipe.Phase);
                if (pipe.Level > 0) Count(acc.Levels, pipe.Level);
                if (pipe.Quantity.HasValue)
                {
                    acc.QuantitySum += pipe.Quantity.Value;
                    acc.QuantityCount++;
                }
                foreach (KeyValuePair<string, string> copy in pipe.Copy)
                {
                    if (!acc.Copy.TryGetValue(copy.Key, out Dictionary<string, int>? values))
                    {
                        values = new Dictionary<string, int>(StringComparer.Ordinal);
                        acc.Copy[copy.Key] = values;
                    }
                    Count(values, copy.Value);
                }

                PipeRule? rule = rules.FirstOrDefault(r => r.Matches(pipe.Segment, pipe.Name, pipe.DnMm, pipe.Insulated));
                if (rule == null)
                {
                    result.PipesWithoutRule++;
                    result.Issues.Add(Severity.Warning,
                        $"нет правила для «{(pipe.Name.Length > 0 ? pipe.Name : "без наименования")}» (сегмент «{pipe.Segment}», DN{CalcTables.Format(pipe.DnMm, 1)}"
                        + (pipe.Insulated ? ", изол." : string.Empty) + ") — ресурсы не начислены",
                        "Добавьте строку на вкладке «Правила для труб» (или «-» в «Ресурсы», если такие трубы не считаем).", pipe.Id);
                    continue;
                }
                result.RuleOfPipe[pipe.Id] = rule.Line;
                acc.Summary.AreaByRules += area;
                if (Math.Abs(pipe.OdMm - pipe.DnMm) < 0.05 && rule.Codes.Any(c => resources[c].Base == CalcBase.Area))
                {
                    result.Issues.Add(Severity.Warning,
                        $"сегмент «{pipe.Segment}» DN{CalcTables.Format(pipe.DnMm, 1)}: Dнар = DN = {CalcTables.Format(pipe.OdMm, 2)} мм у окрашиваемой трубы",
                        "Проверьте таблицу сегмента: Управление → Параметры MEP → Механические → Сегменты и размеры.", pipe.Id);
                }
                int supportsCount = 0;
                if (rule.Codes.Any(c => resources[c].Base == CalcBase.Supports))
                {
                    double? step = pipe.Vertical && rule.StepVertical.HasValue ? rule.StepVertical : rule.StepHorizontal;
                    supportsCount = SupportsOnPipe(pipe.LengthM, step, options.ShortPipeM);
                    acc.Summary.Supports += supportsCount;
                }
                foreach (string code in rule.Codes)
                {
                    CalcResource resource = resources[code];
                    switch (resource.Base)
                    {
                        case CalcBase.SupportMass:
                            SupportRow? row = CalcTables.FindSupport(supports, pipe.DnMm);
                            if (row == null)
                            {
                                result.Issues.Add(Severity.Warning,
                                    $"{code}: в таблице креплений нет DN{CalcTables.Format(pipe.DnMm, 1)} — масса креплений этих труб не посчитана",
                                    "Добавьте строку на вкладке «Крепления» или исключите такие трубы правилом.", pipe.Id);
                                break;
                            }
                            var groupKey = (code, row.Dn, pipe.Insulated);
                            acc.SupportLength.TryGetValue(groupKey, out double sum);
                            acc.SupportLength[groupKey] = sum + pipe.LengthM;
                            acc.SupportRows[groupKey] = row;
                            break;
                        case CalcBase.Area:
                            Add(acc.Base, code, area);
                            break;
                        case CalcBase.Length:
                            Add(acc.Base, code, pipe.LengthM);
                            break;
                        default:
                            Add(acc.Base, code, supportsCount);
                            break;
                    }
                }
            }

            foreach (SystemAcc acc in systems.Values)
            {
                var basisParts = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (KeyValuePair<(string Code, double Dn, bool Insulated), double> group in acc.SupportLength.OrderBy(g => g.Key.Code, StringComparer.Ordinal)
                    .ThenBy(g => g.Key.Dn).ThenBy(g => g.Key.Insulated))
                {
                    SupportRow row = acc.SupportRows[group.Key];
                    double length = group.Value;
                    string tag = $"DN{CalcTables.Format(group.Key.Dn, 0)} {(group.Key.Insulated ? "изол." : "неизол.")}";
                    if (length < options.ShortPipeM)
                    {
                        result.Issues.Add(Severity.Info,
                            $"«{scope}», {acc.Summary.System}: {group.Key.Code} {tag} — всего {CalcTables.Format(length)} м, короче {CalcTables.Format(options.ShortPipeM)} м: крепление не начислено");
                        continue;
                    }
                    double step = row.Step(group.Key.Insulated);
                    double mass1 = row.Mass(group.Key.Insulated);
                    int n = (int)Math.Ceiling(length / step - Eps);
                    double mass = n * mass1;
                    Add(acc.Base, group.Key.Code, mass);
                    acc.Summary.SupportLines.Add($"{group.Key.Code} {tag}: ΣL = {CalcTables.Format(length)} м / шаг {CalcTables.Format(step, 2)} м = {n} шт × {CalcTables.Format(mass1)} кг = {CalcTables.Format(mass)} кг");
                    if (!basisParts.TryGetValue(group.Key.Code, out List<string>? parts))
                    {
                        parts = new List<string>();
                        basisParts[group.Key.Code] = parts;
                    }
                    parts.Add($"{tag}: ⌈{CalcTables.Format(length, 2)}/{CalcTables.Format(step, 2)}⌉={n}×{CalcTables.Format(mass1)}");
                }

                if (options.CheckQuantity && acc.QuantityCount == acc.Summary.Pipes && acc.Summary.Pipes > 0
                    && Math.Abs(acc.QuantitySum - acc.Summary.Length) > Math.Max(0.01, 0.005 * acc.Summary.Length))
                {
                    result.Issues.Add(Severity.Info,
                        $"«{scope}», {acc.Summary.System}: сумма {options.QuantityParameter} труб {CalcTables.Format(acc.QuantitySum)} ≠ длина по модели {CalcTables.Format(acc.Summary.Length)} м (запас или устаревшее значение); расчёт ведётся по модели");
                }

                result.Systems.Add(acc.Summary);
                foreach (CalcResource resource in resources.Values.OrderBy(r => r.Order).ThenBy(r => r.Code, StringComparer.Ordinal))
                {
                    if (!acc.Base.TryGetValue(resource.Code, out double baseValue) || baseValue <= 0) continue;
                    var target = new CalcTarget(scope, acc.Summary.System, resource, baseValue)
                    {
                        Phase = Mode(acc.Phases) ?? -1,
                        Level = Mode(acc.Levels) ?? -1,
                    };
                    foreach (KeyValuePair<string, Dictionary<string, int>> copy in acc.Copy)
                    {
                        string? value = Mode(copy.Value);
                        if (value != null) target.Copy[copy.Key] = value;
                    }
                    target.Basis = Basis(target, basisParts.TryGetValue(resource.Code, out List<string>? p) ? p : null);
                    result.Targets.Add(target);
                }
            }

            List<string> names = systems.Keys.ToList();
            for (int i = 0; i < names.Count; i++)
            {
                for (int j = i + 1; j < names.Count; j++)
                {
                    if (ValueNormalizer.ComparisonKey(names[i]) == ValueNormalizer.ComparisonKey(names[j]))
                    {
                        result.Issues.Add(Severity.Warning, $"похожие имена систем «{names[i]}» и «{names[j]}» (латиница/кириллица, регистр, пробелы) — это разные строки спецификации",
                            "Исправьте значение у труб (VK Tools → «Проверить модель» покажет разные написания).");
                    }
                }
            }
        }

        private static string Basis(CalcTarget t, List<string>? supportParts)
        {
            CalcResource r = t.Resource;
            string tail = r.BasisText.Length > 0 ? "; " + r.BasisText : string.Empty;
            if (r.Base == CalcBase.SupportMass)
            {
                return $"{string.Join("; ", supportParts ?? new List<string>())} = {CalcTables.Format(t.Base)} кг × {CalcTables.Format(r.Rate, 4)} × {CalcTables.Format(r.Reserve)} = "
                    + $"{CalcTables.Format(t.Quantity)} → {CalcTables.Format(t.Auto)} {r.Unit}{tail}";
            }
            string symbol = r.Base == CalcBase.Area ? "S" : r.Base == CalcBase.Length ? "L" : "N";
            string label = CalcTables.BaseLabel(r.Base);
            return $"{symbol}={CalcTables.Format(t.Base)} {label} × {CalcTables.Format(r.Rate, 4)} {r.Unit}/{label} × {CalcTables.Format(r.Layers)} сл. × {CalcTables.Format(r.Reserve)} = "
                + $"{CalcTables.Format(t.Quantity)} → {CalcTables.Format(t.Auto)} {r.Unit}{tail}";
        }

        private static void Add(Dictionary<string, double> d, string key, double value)
        {
            d.TryGetValue(key, out double sum);
            d[key] = sum + value;
        }

        private static void Count<T>(Dictionary<T, int> d, T key)
            where T : notnull
        {
            d.TryGetValue(key, out int n);
            d[key] = n + 1;
        }

        /// <summary>Самое частое значение; при равенстве — первое встреченное (порядок труб в модели).</summary>
        private static T? Mode<T>(Dictionary<T, int> d)
            where T : notnull
        {
            if (d.Count == 0) return default;
            int max = d.Values.Max();
            return d.First(p => p.Value == max).Key;
        }

        private static long? Mode(Dictionary<long, int> d) => d.Count == 0 ? (long?)null : Mode<long>(d);

        private sealed class SystemAcc
        {
            public SystemAcc(CalcSystemSummary summary)
            {
                Summary = summary;
            }

            public CalcSystemSummary Summary { get; }

            public Dictionary<string, double> Base { get; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            public Dictionary<long, int> Phases { get; } = new Dictionary<long, int>();

            public Dictionary<long, int> Levels { get; } = new Dictionary<long, int>();

            public Dictionary<string, Dictionary<string, int>> Copy { get; } = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

            public Dictionary<(string Code, double Dn, bool Insulated), double> SupportLength { get; } = new Dictionary<(string, double, bool), double>();

            public Dictionary<(string Code, double Dn, bool Insulated), SupportRow> SupportRows { get; } = new Dictionary<(string, double, bool), SupportRow>();

            public double QuantitySum { get; set; }

            public int QuantityCount { get; set; }
        }
    }

    /// <summary>Способ получения количества позиции: SYS_Способ.</summary>
    public enum CalcMode
    {
        Auto,
        Manual,
        Error,
    }

    /// <summary>Ручная корректировка (REQ-CALC-005): SYS_Кол-во вручную ≥ 0 перекрывает расчёт, расхождение больше порога — предупреждение.</summary>
    public static class CalcManual
    {
        public const double WarnPercent = 20.0;

        /// <summary>Разобрать ручное значение: пусто — нет; не число или меньше нуля — ошибка (берётся расчёт).</summary>
        public static (double? Value, string? Error) Parse(string? text)
        {
            string s = (text ?? string.Empty).Trim();
            if (s.Length == 0) return (null, null);
            try
            {
                double? v = CalcTables.ParseNumber(s, null);
                if (v.HasValue && v.Value >= 0) return (v, null);
            }
            catch (FormatException)
            {
                // ниже — ошибка
            }
            return (null, $"ручное значение «{s}» не число или меньше нуля — взят расчёт");
        }

        public static (double Final, CalcMode Mode, string Note) Resolve(double auto, string? manualText)
        {
            (double? manual, string? error) = Parse(manualText);
            if (error != null) return (auto, CalcMode.Error, error);
            if (!manual.HasValue) return (auto, CalcMode.Auto, string.Empty);
            string note = string.Empty;
            if (auto > 0 && Math.Abs(manual.Value - auto) / auto * 100.0 > WarnPercent)
            {
                note = $"ручное {CalcTables.Format(manual.Value)} отличается от расчёта {CalcTables.Format(auto)} на {CalcTables.Format(Math.Abs(manual.Value - auto) / auto * 100.0, 0)} %";
            }
            return (manual.Value, CalcMode.Manual, note);
        }

        public static string ModeText(CalcMode mode) => mode switch
        {
            CalcMode.Manual => "MANUAL",
            CalcMode.Error => "ERROR",
            _ => "AUTO",
        };
    }
}
