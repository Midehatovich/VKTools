using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VKTools.Core.Checks;

namespace VKTools.Core.Schedules
{
    /// <summary>Что значит графа для проверки: по имени параметра без префикса набора (ADSK_, Р_).</summary>
    public enum AuditRole
    {
        Other,
        Position,
        Name,
        Mark,
        Code,
        Maker,
        Unit,
        Quantity,
        Note,
    }

    /// <summary>Графа спецификации так, как её видит проверка.</summary>
    public sealed class AuditField
    {
        public AuditField(string name, AuditRole role)
        {
            Name = name;
            Role = role;
        }

        /// <summary>Заголовок графы, иначе имя параметра — для текста отчёта.</summary>
        public string Name { get; }

        public AuditRole Role { get; }

        public bool Hidden { get; set; }

        /// <summary>Поле сортировки/группировки: строки спецификации различаются по его значению.</summary>
        public bool Sort { get; set; }

        /// <summary>«Подсчитать итоги»: значения строки складываются, а не дают «‹варианты›».</summary>
        public bool Totals { get; set; }

        /// <summary>Шаг округления в спецификации (0,1; 0,01), 0 — не известен.</summary>
        public double Accuracy { get; set; }
    }

    /// <summary>Элемент спецификации: значения граф в порядке <see cref="AuditSchedule.Fields"/>.</summary>
    public sealed class AuditElement
    {
        public AuditElement(long id, IReadOnlyList<string> values)
        {
            Id = id;
            Values = values;
        }

        public long Id { get; }

        /// <summary>Текст графы, как его показывает спецификация (пусто — нет значения или параметра).</summary>
        public IReadOnlyList<string> Values { get; }

        /// <summary>Числа граф с итогами (количество); NaN — нет числа. Может быть null.</summary>
        public IReadOnlyList<double>? Numbers { get; set; }

        public string Category { get; set; } = string.Empty;

        public string Family { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        /// <summary>Размер («20 мм», «50-32»): вместе с семейством и типом — «один типоразмер».</summary>
        public string Size { get; set; } = string.Empty;

        public string System { get; set; } = string.Empty;
    }

    public sealed class AuditSchedule
    {
        public AuditSchedule(long id, string name, IReadOnlyList<AuditField> fields)
        {
            Id = id;
            Name = name;
            Fields = fields;
        }

        public long Id { get; }

        public string Name { get; }

        public IReadOnlyList<AuditField> Fields { get; }

        /// <summary>«Для каждого экземпляра»: каждая строка — один элемент, «‹варианты›» не бывает.</summary>
        public bool Itemized { get; set; }

        public List<AuditElement> Elements { get; } = new List<AuditElement>();
    }

    /// <summary>
    /// «Проверить спецификацию» (0.17.0): строки спецификации собираются так же, как в Revit (по полям сортировки),
    /// и по ним ищется то, что на «Пушкареве» искали глазами и правили через туннель: «‹варианты›» с причиной,
    /// пустые графы, количество, которое округляется в 0, дыры и повторы позиций, разные единицы, текст «не найдено»
    /// из таблиц семейств, элементы вне спецификаций и в двух спецификациях сразу. Только чтение.
    /// </summary>
    public static class ScheduleAudit
    {
        public const string Variants = "«‹варианты›» в строке";
        public const string NameSplit = "Один типоразмер — разные наименования";
        public const string Empty = "Пустые графы";
        public const string Zero = "Количество округляется в 0";
        public const string Numbering = "Нумерация позиций";

        public const string Excluded = "Служебные элементы «!»";

        /// <summary>Категория труб (как её отдаёт Revit на русском).</summary>
        public const string PipeCategory = "Трубы";
        public const string Units = "Единицы измерения";
        public const string FamilyText = "Текст из таблицы семейства";
        public const string Outside = "Вне спецификаций";
        public const string Twice = "В нескольких спецификациях";
        public const string Summary = "Итог";

        private const int MaxListed = 6;

        private static readonly (string Suffix, AuditRole Role)[] Roles =
        {
            ("Позиция", AuditRole.Position),
            ("Наименование", AuditRole.Name),
            ("Марка", AuditRole.Mark),
            ("Код изделия", AuditRole.Code),
            ("Завод-изготовитель", AuditRole.Maker),
            ("Единица измерения", AuditRole.Unit),
            ("Количество", AuditRole.Quantity),
            ("Примечание", AuditRole.Note),
        };

        private static readonly string[] FamilyMarkers = { "не найден", "проверить" };

        /// <summary>«ADSK_Наименование», «Р_Наименование» → Name; «ADSK_Позиция_Сортировка» → Other.</summary>
        public static AuditRole RoleOf(string parameterName)
        {
            string name = (parameterName ?? string.Empty).Trim();
            int us = name.IndexOf('_');
            string bare = us > 0 && us < name.Length - 1 ? name.Substring(us + 1) : name;
            foreach ((string suffix, AuditRole role) in Roles)
            {
                if (string.Equals(bare, suffix, StringComparison.OrdinalIgnoreCase)) return role;
            }
            return AuditRole.Other;
        }

        /// <param name="outside">Элементы категорий ВК, не попавшие ни в одну проверенную спецификацию (null — не проверять).</param>
        public static List<CheckFinding> Run(IReadOnlyList<AuditSchedule> schedules, IReadOnlyList<AuditElement>? outside = null)
        {
            var findings = new List<CheckFinding>();
            int rows = 0;
            var checkedSchedules = new List<AuditSchedule>();
            foreach (AuditSchedule original in schedules)
            {
                AuditSchedule schedule = WithoutExcluded(original, findings);
                checkedSchedules.Add(schedule);
                List<Row> built = BuildRows(schedule);
                rows += built.Count;
                CheckVariants(schedule, built, findings);
                CheckNameSplit(schedule, built, findings);
                CheckEmpty(schedule, built, findings);
                CheckZero(schedule, built, findings);
                CheckNumbering(schedule, built, findings);
                CheckUnits(schedule, findings);
                CheckFamilyText(schedule, findings);
            }
            CheckTwice(checkedSchedules, findings);
            if (outside != null) CheckOutside(outside, findings);
            int elements = checkedSchedules.SelectMany(s => s.Elements).Select(e => e.Id).Distinct().Count();
            findings.Add(new CheckFinding(Summary, Severity.Info,
                $"Проверено спецификаций: {schedules.Count}, строк: {rows}, элементов: {elements}. "
                + (findings.Any(f => f.Severity != Severity.Info) ? "Замечания выше: «Выделить» показывает элементы в модели." : "Замечаний нет.")));
            return findings;
        }

        /// <summary>
        /// Элементы с наименованием на «!» («!Не учитывать») — служебные: в проверки не идут.
        /// Если они всё же есть в спецификации, это отдельное замечание (строка видна на листе).
        /// </summary>
        private static AuditSchedule WithoutExcluded(AuditSchedule schedule, List<CheckFinding> findings)
        {
            int nameIdx = Index(schedule, AuditRole.Name);
            if (nameIdx < 0) return schedule;
            var excluded = schedule.Elements.Where(e => IsExcludedName(Value(e, nameIdx))).ToList();
            if (excluded.Count == 0) return schedule;
            var finding = new CheckFinding(Excluded, Severity.Warning,
                $"«{schedule.Name}»: {excluded.Count} эл. с наименованием на «!» ({string.Join("; ", excluded.Select(e => Shorten(Value(e, nameIdx), 40)).Distinct(StringComparer.Ordinal).Take(MaxListed))}) — в остальных проверках не участвуют")
            {
                Current = "строка «!…» в спецификации",
                Expected = "служебные элементы скрыты фильтром",
                Action = $"Если строка видна на листе — добавьте в спецификацию фильтр «{schedule.Fields[nameIdx].Name} не начинается с !». Если скрыта иначе — ничего делать не нужно.",
                Category = excluded[0].Category,
                Family = excluded[0].Family,
                Type = excluded[0].Type,
            };
            Attach(finding, excluded);
            findings.Add(finding);
            var kept = new AuditSchedule(schedule.Id, schedule.Name, schedule.Fields) { Itemized = schedule.Itemized };
            kept.Elements.AddRange(schedule.Elements.Where(e => !IsExcludedName(Value(e, nameIdx))));
            return kept;
        }

        public static bool IsExcludedName(string name) => name.StartsWith("!", StringComparison.Ordinal);

        /// <summary>
        /// Наименование без чисел: «Отвод 90° … Ду50» и «Отвод 45° … Ду50» → одно и то же.
        /// Отличие только в числах у одного типоразмера — это разные изделия (угол, Ду у семейств без параметра размера,
        /// диаметр трубы у изоляции), а не «разъехавшееся» имя.
        /// </summary>
        public static string WithoutNumbers(string name)
        {
            var sb = new StringBuilder(name.Length);
            bool inNumber = false;
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                bool digit = char.IsDigit(c);
                bool joiner = inNumber && (c == ',' || c == '.') && i + 1 < name.Length && char.IsDigit(name[i + 1]);
                if (digit || joiner)
                {
                    if (!inNumber) sb.Append('#');
                    inNumber = true;
                    continue;
                }
                inNumber = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- строки

        private sealed class Row
        {
            public Row(string key) => Key = key;

            public string Key { get; }

            public List<AuditElement> Elements { get; } = new List<AuditElement>();

            public string Label { get; set; } = string.Empty;
        }

        private static List<Row> BuildRows(AuditSchedule schedule)
        {
            var sortIdx = Enumerable.Range(0, schedule.Fields.Count).Where(i => schedule.Fields[i].Sort).ToList();
            var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
            var order = new List<Row>();
            foreach (AuditElement e in schedule.Elements)
            {
                // Без «Для каждого экземпляра» Revit сливает элементы с одинаковыми полями сортировки (без них — всё в одну строку)
                string key = schedule.Itemized
                    ? "#" + e.Id.ToString(CultureInfo.InvariantCulture)
                    : string.Join("\u001f", sortIdx.Select(i => Value(e, i)));
                if (!rows.TryGetValue(key, out Row? row))
                {
                    rows[key] = row = new Row(key);
                    order.Add(row);
                }
                row.Elements.Add(e);
            }
            int nameIdx = Index(schedule, AuditRole.Name);
            int posIdx = Index(schedule, AuditRole.Position);
            foreach (Row row in order) row.Label = Label(schedule, row, nameIdx, posIdx);
            return order;
        }

        /// <summary>
        /// Заголовок группы, под которым элемент стоит в спецификации: первое поле сортировки (обычно сокращение системы),
        /// если это не наименование/позиция/марка. Иначе — система элемента. Так у начинки шкафов в отчёте та же система,
        /// что в шапке спецификации, а не система родителя или соседа.
        /// </summary>
        private static string GroupOf(AuditSchedule schedule, AuditElement e)
        {
            for (int i = 0; i < schedule.Fields.Count; i++)
            {
                AuditField f = schedule.Fields[i];
                if (!f.Sort) continue;
                if (f.Role != AuditRole.Other) break;
                string v = Value(e, i);
                if (v.Length > 0) return v;
                break;
            }
            return e.System;
        }

        private static string Label(AuditSchedule schedule, Row row, int nameIdx, int posIdx)
        {
            AuditElement first = row.Elements[0];
            string name = nameIdx >= 0 ? Common(row, nameIdx) : string.Empty;
            if (name.Length == 0) name = $"{first.Family}: {first.Type}".Trim(' ', ':');
            string pos = posIdx >= 0 ? Common(row, posIdx) : string.Empty;
            string sys = GroupOf(schedule, first);
            var label = new StringBuilder();
            if (sys.Length > 0) label.Append(sys).Append(", ");
            if (pos.Length > 0) label.Append("поз. ").Append(pos).Append(", ");
            label.Append(Shorten(name, 90));
            return label.ToString();
        }

        /// <summary>Значение графы, одно на всю строку; иначе пусто.</summary>
        private static string Common(Row row, int idx)
        {
            string? value = null;
            foreach (AuditElement e in row.Elements)
            {
                string v = Value(e, idx);
                if (value == null) value = v;
                else if (!string.Equals(value, v, StringComparison.Ordinal)) return string.Empty;
            }
            return value ?? string.Empty;
        }

        private static string Value(AuditElement e, int idx) => idx < e.Values.Count ? (e.Values[idx] ?? string.Empty).Trim() : string.Empty;

        private static double Number(AuditElement e, int idx) => e.Numbers != null && idx < e.Numbers.Count ? e.Numbers[idx] : double.NaN;

        private static int Index(AuditSchedule schedule, AuditRole role)
        {
            for (int i = 0; i < schedule.Fields.Count; i++)
            {
                if (schedule.Fields[i].Role == role && !schedule.Fields[i].Hidden) return i;
            }
            for (int i = 0; i < schedule.Fields.Count; i++)
            {
                if (schedule.Fields[i].Role == role) return i;
            }
            return -1;
        }

        // ---------------------------------------------------------------- проверки

        private static void CheckVariants(AuditSchedule schedule, List<Row> rows, List<CheckFinding> findings)
        {
            if (schedule.Itemized) return;
            for (int f = 0; f < schedule.Fields.Count; f++)
            {
                AuditField field = schedule.Fields[f];
                if (field.Hidden || field.Totals) continue;
                foreach (Row row in rows)
                {
                    if (row.Elements.Count < 2) continue;
                    var groups = row.Elements.GroupBy(e => Value(e, f), StringComparer.Ordinal)
                        .Select(g => (Value: g.Key, Items: g.ToList())).OrderByDescending(g => g.Items.Count).ThenBy(g => g.Value, StringComparer.CurrentCulture).ToList();
                    if (groups.Count < 2) continue;
                    bool tie = groups[0].Items.Count == groups[1].Items.Count;
                    List<AuditElement> suspects = (tie ? groups : groups.Skip(1)).SelectMany(g => g.Items).ToList();
                    var finding = new CheckFinding(Variants, Severity.Warning,
                        $"«{schedule.Name}»: строка «{row.Label}» — в графе «{field.Name}» «‹варианты›» ({groups.Count} значения)")
                    {
                        Current = Breakdown(groups),
                        Expected = "одно значение на строку",
                        Action = VariantAction(field.Role, groups),
                    };
                    Attach(finding, suspects);
                    findings.Add(finding);
                }
            }
        }

        private static string VariantAction(AuditRole role, List<(string Value, List<AuditElement> Items)> groups)
        {
            bool hasEmpty = groups.Any(g => g.Value.Length == 0);
            switch (role)
            {
                case AuditRole.Position:
                    return hasEmpty
                        ? "У части одинаковых изделий позиция пустая: выделите их и дайте ту же позицию (или «Позиции» в 0.17.1 дозаполнит от соседей)."
                        : "У одинаковых изделий разные позиции (например, начинка шкафов пронумерована по шкафам). Выровняйте позицию или решите, что это разные строки, и добавьте позицию в сортировку.";
                case AuditRole.Name:
                    return "В одну строку слились разные наименования: графа наименования не входит в сортировку или показывает другой параметр, чем сортировка "
                        + "(комбинированная графа на ADSK_ при сортировке по Р_). Проверьте «Сортировка/группировка» и «Поля».";
                case AuditRole.Note:
                    return "Разное примечание у одинаковых изделий («или аналог» не у всех): заполните одинаково у выделенных элементов или в таблице плагина.";
                default:
                    return hasEmpty
                        ? "У части элементов строки графа пустая: заполните её у выделенных элементов (марку/код/завод — «Марки и поставщики»)."
                        : "Разные значения графы у элементов одной строки: заполните одинаково или добавьте графу в сортировку, если это разные изделия.";
            }
        }

        private static void CheckNameSplit(AuditSchedule schedule, List<Row> rows, List<CheckFinding> findings)
        {
            int nameIdx = Index(schedule, AuditRole.Name);
            if (nameIdx < 0 || schedule.Fields[nameIdx].Hidden) return;
            var byType = schedule.Elements.Where(e => e.Family.Length > 0 || e.Type.Length > 0)
                .GroupBy(e => (e.Family, e.Type, e.Size));
            foreach (var group in byType)
            {
                var names = group.Select(e => Value(e, nameIdx)).Where(v => v.Length > 0)
                    .GroupBy(v => v, StringComparer.Ordinal).Select(g => (Value: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).ToList();
                if (names.Count < 2) continue;
                // У труб размер в ключе надёжный — там «ø15x2,8» и «ø21.3x2,8» одного Ду и есть ошибка.
                // У фитингов, муфт и изоляции «размер» часто не тот (толщина, пусто) — отличие только в числах не считаем.
                bool numbersMatter = string.Equals(group.First().Category, PipeCategory, StringComparison.OrdinalIgnoreCase);
                if (!numbersMatter && names.Select(n => WithoutNumbers(n.Value)).Distinct(StringComparer.Ordinal).Count() < 2) continue;
                string size = group.Key.Size.Length > 0 ? ", " + group.Key.Size : string.Empty;
                var finding = new CheckFinding(NameSplit, Severity.Warning,
                    $"«{schedule.Name}»: «{group.Key.Family}: {group.Key.Type}{size}» — {names.Count} наименования, это {names.Count} строки вместо одной")
                {
                    Current = string.Join("; ", names.Take(MaxListed).Select(n => $"«{Shorten(n.Value, 80)}» — {n.Count} шт.")),
                    Expected = "одно наименование на типоразмер",
                    Action = "Обычно отличается одно слово («…неоцинкованная…» и без него): поправьте правило «Имён труб» или «Изоляции», "
                        + "либо приведите ADSK_Наименование в семействе. Выделены элементы с более редким вариантом.",
                    Category = group.First().Category,
                    Family = group.Key.Family,
                    Type = group.Key.Type,
                };
                string major = names[0].Value;
                Attach(finding, group.Where(e => !string.Equals(Value(e, nameIdx), major, StringComparison.Ordinal) && Value(e, nameIdx).Length > 0));
                findings.Add(finding);
            }
        }

        private static void CheckEmpty(AuditSchedule schedule, List<Row> rows, List<CheckFinding> findings)
        {
            foreach (AuditRole role in new[] { AuditRole.Name, AuditRole.Position, AuditRole.Unit, AuditRole.Quantity })
            {
                int idx = Index(schedule, role);
                if (idx < 0 || schedule.Fields[idx].Hidden) continue;
                AuditField field = schedule.Fields[idx];
                var empty = new List<AuditElement>();
                foreach (Row row in rows)
                {
                    bool isEmpty;
                    if (role == AuditRole.Quantity && field.Totals)
                    {
                        double sum = RowSum(row, idx);
                        isEmpty = double.IsNaN(sum) ? row.Elements.All(e => Value(e, idx).Length == 0) : sum == 0;
                    }
                    else
                    {
                        isEmpty = row.Elements.All(e => IsBlank(Value(e, idx)));
                    }
                    if (isEmpty) empty.AddRange(row.Elements);
                }
                foreach (var group in empty.GroupBy(e => (e.Family, e.Type)).OrderByDescending(g => g.Count()))
                {
                    AuditElement first = group.First();
                    string systems = string.Join(", ", group.Select(e => GroupOf(schedule, e)).Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.CurrentCulture).Take(MaxListed));
                    var finding = new CheckFinding(Empty, role == AuditRole.Name || role == AuditRole.Quantity ? Severity.Error : Severity.Warning,
                        $"«{schedule.Name}»: пустая графа «{field.Name}» — {group.Count()} эл. «{group.Key.Family}: {group.Key.Type}»"
                        + (systems.Length > 0 ? $" ({systems})" : string.Empty))
                    {
                        Current = role == AuditRole.Quantity ? "0 или пусто" : "пусто",
                        Expected = "значение",
                        Action = EmptyAction(role),
                        Category = first.Category,
                        Family = group.Key.Family,
                        Type = group.Key.Type,
                    };
                    Attach(finding, group);
                    findings.Add(finding);
                }
            }
        }

        private static string EmptyAction(AuditRole role) => role switch
        {
            AuditRole.Name => "Наименование не собралось: у труб — «Имена труб» (пустые «Комментарии к типоразмеру»), у изоляции — «Изоляция», "
                + "у фасонины — таблица выбора семейства не нашла типоразмер. В строке спецификации без имени эти метры и штуки теряются.",
            AuditRole.Quantity => "Количество 0: у труб и изоляции — «Запас труб» (или ADSK_Количество не заполнено в модели), у штучных — параметр количества в семействе.",
            AuditRole.Position => "Позиция не задана: заполните ADSK_Позиция (набор «Р_» перенесёт её «Системой и этажом»).",
            _ => "Единица не задана: заполните «Единицу измерения» в типе семейства.",
        };

        private static void CheckZero(AuditSchedule schedule, List<Row> rows, List<CheckFinding> findings)
        {
            int idx = Index(schedule, AuditRole.Quantity);
            if (idx < 0) return;
            AuditField field = schedule.Fields[idx];
            if (field.Hidden || field.Accuracy <= 0) return;
            foreach (Row row in rows)
            {
                double sum = RowSum(row, idx);
                if (double.IsNaN(sum) || sum <= 0 || sum >= field.Accuracy / 2) continue;
                int unitIdx = Index(schedule, AuditRole.Unit);
                string unit = unitIdx >= 0 ? Common(row, unitIdx) : string.Empty;
                var finding = new CheckFinding(Zero, Severity.Error,
                    $"«{schedule.Name}»: строка «{row.Label}» — количество {Format(sum)} {unit} при округлении {Format(field.Accuracy)} покажется как 0")
                {
                    Current = Format(sum) + (unit.Length > 0 ? " " + unit : string.Empty),
                    Expected = $"не меньше {Format(field.Accuracy / 2)}",
                    Action = unit.Contains("3") || unit.Contains("³")
                        ? "Изоляцию малой толщины удобнее считать в метрах (длина), а не в м³: поменяйте единицу или добавьте знаков в графе «Кол.» (Формат поля)."
                        : "Добавьте знаков после запятой в графе количества (Формат поля) или проверьте единицу.",
                };
                Attach(finding, row.Elements);
                findings.Add(finding);
            }
        }

        private static double RowSum(Row row, int idx)
        {
            double sum = 0;
            bool any = false;
            foreach (AuditElement e in row.Elements)
            {
                double n = Number(e, idx);
                if (double.IsNaN(n)) continue;
                sum += n;
                any = true;
            }
            return any ? sum : double.NaN;
        }

        private static void CheckNumbering(AuditSchedule schedule, List<Row> rows, List<CheckFinding> findings)
        {
            int idx = Index(schedule, AuditRole.Position);
            if (idx < 0 || schedule.Fields[idx].Hidden) return;
            var byPosition = new Dictionary<string, List<Row>>(StringComparer.Ordinal);
            var numbers = new SortedSet<int>();
            foreach (Row row in rows)
            {
                string pos = Common(row, idx);
                if (IsBlank(pos)) continue;
                if (!byPosition.TryGetValue(pos, out List<Row>? list)) byPosition[pos] = list = new List<Row>();
                list.Add(row);
                if (LeadingInt(pos) is int n) numbers.Add(n);
            }
            foreach (KeyValuePair<string, List<Row>> kv in byPosition.Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key, NaturalOrder.Instance))
            {
                var finding = new CheckFinding(Numbering, Severity.Warning,
                    $"«{schedule.Name}»: позиция {kv.Key} у {kv.Value.Count} разных строк")
                {
                    Current = string.Join("; ", kv.Value.Take(MaxListed).Select(r => "«" + r.Label + "»")),
                    Expected = "своя позиция у каждой строки",
                    Action = "Обычно это одно изделие под двумя наименованиями (см. «Один типоразмер — разные наименования») или ошибка нумерации. "
                        + "Перенумерация по спецификации — «Позиции» (0.17.1).",
                };
                Attach(finding, kv.Value.SelectMany(r => r.Elements));
                findings.Add(finding);
            }
            if (numbers.Count == 0) return;
            int max = numbers.Max;
            var missing = Enumerable.Range(1, max).Where(n => !numbers.Contains(n)).ToList();
            if (missing.Count == 0) return;
            findings.Add(new CheckFinding(Numbering, Severity.Warning,
                $"«{schedule.Name}»: в нумерации 1…{max} нет {missing.Count} номеров")
            {
                Current = "нет: " + Ranges(missing),
                Expected = "позиции подряд с 1 в каждой спецификации (решение 2026-11-24)",
                Action = missing.Count > max / 2
                    ? "Похоже, нумерация сквозная по модели, а не по спецификации. Перенумерацию с 1 сделает «Позиции» (0.17.1)."
                    : "Номера заняты элементами, которых в этой спецификации нет, или освободились после правок. Перенумерацию без дыр сделает «Позиции» (0.17.1).",
            });
        }

        private static void CheckUnits(AuditSchedule schedule, List<CheckFinding> findings)
        {
            int idx = Index(schedule, AuditRole.Unit);
            if (idx < 0 || schedule.Fields[idx].Hidden) return;
            var meters = schedule.Elements.Select(e => (Element: e, Raw: Value(e, idx))).Where(x => IsMeter(x.Raw))
                .GroupBy(x => x.Raw, StringComparer.Ordinal).Select(g => (Value: g.Key, Items: g.Select(x => x.Element).ToList()))
                .OrderByDescending(g => g.Items.Count).ToList();
            if (meters.Count < 2) return;
            var finding = new CheckFinding(Units, Severity.Warning,
                $"«{schedule.Name}»: метры записаны по-разному — {string.Join(", ", meters.Select(m => "«" + m.Value + "»"))}")
            {
                Current = string.Join("; ", meters.Select(m => $"«{m.Value}» — {m.Items.Count} эл. ({string.Join(", ", m.Items.Select(e => e.Type).Distinct().Take(3))})")),
                Expected = $"одна единица, например «{meters[0].Value}»",
                Action = "Поправьте «Единицу измерения» в типах выделенных семейств и прогоните «Систему и этаж».",
            };
            Attach(finding, meters.Skip(1).SelectMany(m => m.Items));
            findings.Add(finding);
        }

        private static void CheckFamilyText(AuditSchedule schedule, List<CheckFinding> findings)
        {
            for (int f = 0; f < schedule.Fields.Count; f++)
            {
                if (schedule.Fields[f].Hidden) continue;
                int idx = f;
                var hits = schedule.Elements.Select(e => (Element: e, Text: Value(e, idx)))
                    .Where(x => FamilyMarkers.Any(m => x.Text.IndexOf(m, StringComparison.CurrentCultureIgnoreCase) >= 0))
                    .GroupBy(x => x.Text, StringComparer.Ordinal);
                foreach (var hit in hits)
                {
                    AuditElement first = hit.First().Element;
                    var finding = new CheckFinding(FamilyText, Severity.Error,
                        $"«{schedule.Name}»: в графе «{schedule.Fields[f].Name}» текст «{Shorten(hit.Key, 80)}» — {hit.Count()} эл.")
                    {
                        Current = hit.Key,
                        Expected = "наименование изделия",
                        Action = "Семейство не нашло типоразмер в своей таблице выбора (или помечено «ПРОВЕРИТЬ»): подберите существующий типоразмер у выделенных элементов или дополните таблицу семейства.",
                        Category = first.Category,
                        Family = first.Family,
                        Type = first.Type,
                    };
                    Attach(finding, hit.Select(x => x.Element));
                    findings.Add(finding);
                }
            }
        }

        private static void CheckTwice(IReadOnlyList<AuditSchedule> schedules, List<CheckFinding> findings)
        {
            if (schedules.Count < 2) return;
            var where = new Dictionary<long, List<string>>();
            var elements = new Dictionary<long, AuditElement>();
            foreach (AuditSchedule schedule in schedules)
            {
                foreach (AuditElement e in schedule.Elements)
                {
                    if (!where.TryGetValue(e.Id, out List<string>? list)) where[e.Id] = list = new List<string>();
                    if (!list.Contains(schedule.Name)) list.Add(schedule.Name);
                    elements[e.Id] = e;
                }
            }
            foreach (var group in where.Where(kv => kv.Value.Count > 1).GroupBy(kv => string.Join("\u001f", kv.Value)).OrderByDescending(g => g.Count()))
            {
                List<string> names = group.First().Value;
                var finding = new CheckFinding(Twice, Severity.Warning,
                    $"{group.Count()} эл. сразу в {names.Count} спецификациях: {string.Join(", ", names.Select(n => "«" + n + "»"))}")
                {
                    Current = string.Join(", ", group.Select(kv => elements[kv.Key]).Select(e => e.Family + ": " + e.Type).Distinct().Take(MaxListed)),
                    Expected = "элемент в одной спецификации",
                    Action = "Посчитаются дважды, а номер позиции станет неоднозначным (нумерация своя в каждой спецификации). "
                        + "Разведите фильтры (зона, этаж, помещение «только в своей») или не отмечайте справочную спецификацию при проверке.",
                };
                Attach(finding, group.Select(kv => elements[kv.Key]));
                findings.Add(finding);
            }
        }

        private static void CheckOutside(IReadOnlyList<AuditElement> outside, List<CheckFinding> findings)
        {
            foreach (var group in outside.GroupBy(e => (e.Category, e.System)).OrderByDescending(g => g.Count()))
            {
                AuditElement first = group.First();
                string sys = group.Key.System.Length > 0 ? $"системы «{group.Key.System}»" : "без системы";
                var finding = new CheckFinding(Outside, Severity.Warning,
                    $"{group.Count()} эл. «{group.Key.Category}» {sys} не попали ни в одну проверенную спецификацию")
                {
                    Current = string.Join(", ", group.Select(e => e.Family + ": " + e.Type).Distinct().Take(MaxListed)),
                    Expected = "каждый элемент ВК — в одной спецификации",
                    Action = group.Key.System.Length == 0
                        ? "Нет системы: подключите к системе или задайте систему в «Системе и этаже» (приборы в двух системах, неподключённые трубы)."
                        : "Отсекли фильтры: проверьте у выделенных зону (ADSK_Группирование / помещение), этаж и «Учитывать в спецификации».",
                    Category = group.Key.Category,
                    Family = first.Family,
                    Type = first.Type,
                };
                Attach(finding, group);
                findings.Add(finding);
            }
        }

        // ---------------------------------------------------------------- помощники

        private static void Attach(CheckFinding finding, IEnumerable<AuditElement> elements)
        {
            foreach (AuditElement e in elements)
            {
                if (finding.ElementId == null)
                {
                    finding.ElementId = e.Id;
                    if (finding.Category.Length == 0) finding.Category = e.Category;
                    if (finding.Family.Length == 0) finding.Family = e.Family;
                    if (finding.Type.Length == 0) finding.Type = e.Type;
                }
                finding.RelatedElementIds.Add(e.Id);
            }
        }

        private static string Breakdown(List<(string Value, List<AuditElement> Items)> groups) =>
            string.Join("; ", groups.Take(MaxListed).Select(g => $"{(g.Value.Length == 0 ? "(пусто)" : "«" + Shorten(g.Value, 70) + "»")} — {g.Items.Count} шт."))
            + (groups.Count > MaxListed ? $"; ещё {groups.Count - MaxListed}" : string.Empty);

        private static bool IsBlank(string value) => value.Length == 0 || value == "-" || value == "—";

        /// <summary>Метры в разном написании: «м», «п.м», «пог.м», «м.п.», «м пог.».</summary>
        public static bool IsMeter(string unit)
        {
            string raw = unit ?? string.Empty;
            if (raw.Any(c => char.IsDigit(c) || c == '²' || c == '³')) return false;
            string u = new string(raw.ToLowerInvariant().Where(char.IsLetter).ToArray());
            return u == "м" || u == "пм" || u == "погм" || u == "мп" || u == "мпог";
        }

        public static int? LeadingInt(string position)
        {
            long n = 0;
            int i = 0;
            while (i < position.Length && position[i] >= '0' && position[i] <= '9')
            {
                n = n * 10 + (position[i] - '0');
                if (n > int.MaxValue) return null;
                i++;
            }
            return i == 0 ? (int?)null : (int)n;
        }

        /// <summary>1, 2, 3, 5, 7, 8 → «1–3, 5, 7, 8».</summary>
        public static string Ranges(IReadOnlyList<int> numbers)
        {
            var parts = new List<string>();
            for (int i = 0; i < numbers.Count; i++)
            {
                int start = numbers[i];
                int end = start;
                while (i + 1 < numbers.Count && numbers[i + 1] == end + 1) end = numbers[++i];
                parts.Add(end - start >= 2 ? $"{start}–{end}" : end == start ? start.ToString(CultureInfo.InvariantCulture)
                    : $"{start}, {end}");
                if (parts.Count >= 40)
                {
                    parts.Add("…");
                    break;
                }
            }
            return string.Join(", ", parts);
        }

        private static string Format(double value) => value.ToString("0.####", CultureInfo.GetCultureInfo("ru-RU"));

        private static string Shorten(string text, int max) => text.Length <= max ? text : text.Substring(0, max - 1) + "…";

        /// <summary>«2» &lt; «10», «38.1» &lt; «38.7».</summary>
        private sealed class NaturalOrder : IComparer<string>
        {
            public static readonly NaturalOrder Instance = new NaturalOrder();

            public int Compare(string? x, string? y)
            {
                int? a = LeadingInt(x ?? string.Empty);
                int? b = LeadingInt(y ?? string.Empty);
                if (a.HasValue && b.HasValue && a.Value != b.Value) return a.Value.CompareTo(b.Value);
                return string.CompareOrdinal(x, y);
            }
        }
    }
}
