using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Text;

namespace VKTools.Core.Spec
{
    /// <summary>Вложенное семейство в составе комплекта. Тип пустой — любой типоразмер этого семейства.</summary>
    public sealed class KitMember
    {
        public KitMember(string family, string type = "")
        {
            Family = (family ?? string.Empty).Trim();
            Type = (type ?? string.Empty).Trim();
        }

        public string Family { get; }

        public string Type { get; }

        public string Label => Type.Length == 0 ? Family : Family + " : " + Type;

        public bool Matches(string family, string type) => KitNames.Same(Family, family) && (Type.Length == 0 || KitNames.Same(Type, type));

        public bool SameAs(KitMember other) => KitNames.Same(Family, other.Family) && KitNames.Same(Type, other.Type);
    }

    /// <summary>
    /// Состав комплекта проекта: родитель (семейство или его типоразмер) и вложенные по порядку.
    /// Родитель получает ранг .00, вложенные — .01, .02… по списку; одинаковые вложенные — один ранг.
    /// </summary>
    public sealed class KitRule
    {
        /// <summary>Ранг занимает два знака после точки.</summary>
        public const int MaxMembers = 99;

        public KitRule(string family, string type, IEnumerable<KitMember> members)
        {
            Family = (family ?? string.Empty).Trim();
            Type = (type ?? string.Empty).Trim();
            Members = members.ToList();
        }

        public string Family { get; }

        /// <summary>Пусто — состав для всех типоразмеров; иначе свой состав этого типоразмера.</summary>
        public string Type { get; }

        public IReadOnlyList<KitMember> Members { get; }

        public bool IsTypeOverride => Type.Length > 0;

        public string Label => IsTypeOverride ? $"{Family} : {Type}" : Family;

        /// <summary>Ранг вложенного: 1, 2… по порядку состава; 0 — в комплект не входит.</summary>
        public int RankOf(string family, string type)
        {
            for (int i = 0; i < Members.Count; i++)
            {
                if (Members[i].Matches(family, type)) return i + 1;
            }
            return 0;
        }
    }

    /// <summary>Номер комплекта K типоразмера родителя: присваивается один раз и хранится в модели.</summary>
    public sealed class KitNumber
    {
        public KitNumber(string family, string type, int k)
        {
            Family = (family ?? string.Empty).Trim();
            Type = (type ?? string.Empty).Trim();
            K = k;
        }

        public string Family { get; }

        public string Type { get; }

        public int K { get; }
    }

    internal static class KitNames
    {
        public static bool Same(string? a, string? b) =>
            string.Equals((a ?? string.Empty).Trim(), (b ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Комплекты проекта (настройка «Группирования», D-039): составы, номера K типоразмеров, слова поиска.
    /// Хранится в модели текстом в формате CSV; тот же текст — файл экспорта.
    /// </summary>
    public sealed class KitSettings
    {
        public const string DefaultWords = "комплект; компл.; в комплекте; в составе; в т.ч.";
        public const int SchemaVersion = 1;
        public const int MaxNumber = 999999;

        public const string ColumnFamily = "Семейство";
        public const string ColumnType = "Тип";
        public const string ColumnMember = "Вложенное семейство";
        public const string ColumnMemberType = "Вложенный тип";
        public const string ColumnNumber = "K";

        private static readonly string[] Header = { ColumnFamily, ColumnType, ColumnMember, ColumnMemberType, ColumnNumber };

        public List<KitRule> Rules { get; } = new List<KitRule>();

        public List<KitNumber> Numbers { get; } = new List<KitNumber>();

        /// <summary>Слова поиска кандидатов через «;». На запись не влияют.</summary>
        public string Words { get; set; } = DefaultWords;

        public static IReadOnlyList<string> FileComments { get; } = new[]
        {
            "VK Tools: комплекты проекта для ADSK_Группирование.",
            "Строка состава: Семейство; Тип (пусто — все типоразмеры); Вложенное семейство; Вложенный тип (пусто — любой). Порядок строк — ранг .01, .02…",
            "Строка номера: Семейство; Тип; K (вложенное пусто). K присваивает плагин, у разных типоразмеров разные.",
        };

        public KitRule? FamilyRule(string family) => Rules.FirstOrDefault(r => !r.IsTypeOverride && KitNames.Same(r.Family, family));

        public KitRule? TypeRule(string family, string type) => Rules.FirstOrDefault(r => r.IsTypeOverride && KitNames.Same(r.Family, family) && KitNames.Same(r.Type, type));

        /// <summary>Состав для типоразмера: свой, иначе семейства; null — не комплект.</summary>
        public KitRule? RuleFor(string family, string type) => TypeRule(family, type) ?? FamilyRule(family);

        public bool HasRule(string family) => Rules.Any(r => KitNames.Same(r.Family, family));

        public int? NumberFor(string family, string type) =>
            Numbers.FirstOrDefault(n => KitNames.Same(n.Family, family) && KitNames.Same(n.Type, type))?.K;

        public IReadOnlyList<string> WordList() =>
            Words.Split(';').Select(w => w.Trim()).Where(w => w.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();

        public KitSettings Clone()
        {
            var copy = new KitSettings { Words = Words };
            copy.Rules.AddRange(Rules);
            copy.Numbers.AddRange(Numbers);
            return copy;
        }

        /// <summary>Копия с добавленными номерами (типоразмеры, у которых номер уже есть, не меняются).</summary>
        public KitSettings WithNumbers(IEnumerable<KitNumber> added)
        {
            KitSettings copy = Clone();
            var used = new HashSet<int>(copy.Numbers.Select(n => n.K));
            foreach (KitNumber number in added)
            {
                if (copy.NumberFor(number.Family, number.Type) != null || used.Contains(number.K)) continue;
                copy.Numbers.Add(number);
                used.Add(number.K);
            }
            return copy;
        }

        /// <summary>Заменить состав (семейства или типоразмера); пустой состав удаляет правило.</summary>
        public void SetRule(KitRule rule)
        {
            int index = Rules.FindIndex(r => KitNames.Same(r.Family, rule.Family) && KitNames.Same(r.Type, rule.Type));
            if (rule.Members.Count == 0)
            {
                if (index >= 0) Rules.RemoveAt(index);
                if (!HasRule(rule.Family)) Numbers.RemoveAll(n => KitNames.Same(n.Family, rule.Family));
                return;
            }
            if (index >= 0) Rules[index] = rule;
            else Rules.Add(rule);
        }

        /// <summary>Убрать все правила семейства и его номера K.</summary>
        public void RemoveFamily(string family)
        {
            Rules.RemoveAll(r => KitNames.Same(r.Family, family));
            Numbers.RemoveAll(n => KitNames.Same(n.Family, family));
        }

        /// <summary>
        /// Номера для типоразмеров без K: по порядку «семейство, тип» (числа в именах — как числа), каждому наименьший свободный.
        /// Занятые — номера комплектов проекта и <paramref name="reserved"/> (K шкафов ПК в модели). Уже присвоенные не меняются.
        /// </summary>
        public List<KitNumber> AssignNumbers(IEnumerable<(string Family, string Type)> types, IEnumerable<int> reserved)
        {
            var used = new HashSet<int>(Numbers.Select(n => n.K));
            used.UnionWith(reserved);
            var result = new List<KitNumber>();
            var seen = new List<(string Family, string Type)>();
            foreach ((string family, string type) in types)
            {
                if (NumberFor(family, type) != null || seen.Any(s => KitNames.Same(s.Family, family) && KitNames.Same(s.Type, type))) continue;
                seen.Add((family.Trim(), type.Trim()));
            }
            seen.Sort((a, b) =>
            {
                int c = NameCompare(a.Family, b.Family);
                return c != 0 ? c : NameCompare(a.Type, b.Type);
            });
            int k = 1;
            foreach ((string family, string type) in seen)
            {
                while (used.Contains(k)) k++;
                used.Add(k);
                result.Add(new KitNumber(family, type, k));
            }
            return result;
        }

        private static int NameCompare(string a, string b)
        {
            int c = GroupingTable.NaturalCompare(a.ToUpperInvariant(), b.ToUpperInvariant());
            return c != 0 ? c : string.CompareOrdinal(a, b);
        }

        /// <summary>Отпечаток того, что влияет на запись: составы и номера (без слов поиска).</summary>
        public string Signature() => Format(Enumerable.Empty<string>());

        public string Format(IEnumerable<string> comments)
        {
            var rows = new List<IReadOnlyList<string>>();
            foreach (KitRule rule in Rules)
            {
                foreach (KitMember member in rule.Members) rows.Add(new[] { rule.Family, rule.Type, member.Family, member.Type, string.Empty });
            }
            foreach (KitNumber number in Numbers)
            {
                rows.Add(new[] { number.Family, number.Type, string.Empty, string.Empty, number.K.ToString(CultureInfo.InvariantCulture) });
            }
            return CsvWriter.Table(comments, SchemaVersion, Header, rows);
        }

        /// <summary>Разбор настройки проекта или файла экспорта. null — схема или колонки не те; ошибки строк — в <paramref name="issues"/>.</summary>
        public static KitSettings? Parse(string text, string source, List<ConfigIssue> issues)
        {
            CsvTable table = CsvTable.Parse(text);
            if (!ConfigLoader.Validate(table, new RuleFileSpec(source, SchemaVersion, ColumnFamily, ColumnType, ColumnMember, ColumnMemberType, ColumnNumber), issues)) return null;
            var result = new KitSettings();
            var members = new List<(string Family, string Type, List<KitMember> Members)>();
            foreach (CsvRow row in table.Rows)
            {
                string family = row.Get(ColumnFamily);
                string type = row.Get(ColumnType);
                string member = row.Get(ColumnMember);
                string memberType = row.Get(ColumnMemberType);
                string number = row.Get(ColumnNumber);
                if (family.Length == 0)
                {
                    issues.Add(new ConfigIssue(Severity.Error, source, row.LineNumber, "пустое семейство"));
                    continue;
                }
                if (member.Length > 0 && number.Length > 0)
                {
                    issues.Add(new ConfigIssue(Severity.Error, source, row.LineNumber, "в строке и вложенное, и K: это разные строки"));
                    continue;
                }
                if (member.Length > 0)
                {
                    var item = new KitMember(member, memberType);
                    int index = members.FindIndex(m => KitNames.Same(m.Family, family) && KitNames.Same(m.Type, type));
                    if (index < 0)
                    {
                        members.Add((family, type, new List<KitMember>()));
                        index = members.Count - 1;
                    }
                    List<KitMember> list = members[index].Members;
                    if (list.Any(m => m.SameAs(item)))
                    {
                        issues.Add(new ConfigIssue(Severity.Warning, source, row.LineNumber, $"«{item.Label}» уже есть в составе «{family}», повтор пропущен"));
                        continue;
                    }
                    list.Add(item);
                    continue;
                }
                if (number.Length == 0)
                {
                    issues.Add(new ConfigIssue(Severity.Error, source, row.LineNumber, "нет ни вложенного, ни K"));
                    continue;
                }
                if (type.Length == 0)
                {
                    issues.Add(new ConfigIssue(Severity.Error, source, row.LineNumber, "K задаётся типоразмеру: тип пустой"));
                    continue;
                }
                if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out int k) || k < 1 || k > MaxNumber)
                {
                    issues.Add(new ConfigIssue(Severity.Error, source, row.LineNumber, $"K «{number}»: нужно целое от 1 до {MaxNumber}"));
                    continue;
                }
                if (result.NumberFor(family, type) != null)
                {
                    issues.Add(new ConfigIssue(Severity.Error, source, row.LineNumber, $"у «{family} : {type}» K указан дважды"));
                    continue;
                }
                KitNumber? same = result.Numbers.FirstOrDefault(n => n.K == k);
                if (same != null)
                {
                    issues.Add(new ConfigIssue(Severity.Error, source, row.LineNumber, $"K = {k} уже у «{same.Family} : {same.Type}»: у разных типоразмеров K разные"));
                    continue;
                }
                result.Numbers.Add(new KitNumber(family, type, k));
            }
            foreach ((string family, string type, List<KitMember> list) in members)
            {
                if (list.Count > KitRule.MaxMembers)
                {
                    issues.Add(new ConfigIssue(Severity.Error, source, null, $"в составе «{family}» больше {KitRule.MaxMembers} вложенных"));
                    continue;
                }
                result.Rules.Add(new KitRule(family, type, list));
            }
            return result;
        }
    }

    /// <summary>Тип родителя с вложенными: имя и сколько экземпляров в модели.</summary>
    public sealed class KitTypeInfo
    {
        public KitTypeInfo(string name)
        {
            Name = name;
        }

        public string Name { get; }

        public int Instances { get; set; }
    }

    /// <summary>Вложенное у типоразмера родителя: сколько штук на один экземпляр родителя.</summary>
    public sealed class KitNestedInfo
    {
        public KitNestedInfo(string parentType, string family, string type, string category)
        {
            ParentType = parentType;
            Family = family;
            Type = type;
            Category = category;
        }

        public string ParentType { get; }

        public string Family { get; }

        public string Type { get; }

        public string Category { get; }

        /// <summary>Всего штук во всех экземплярах родителя этого типоразмера.</summary>
        public int Total { get; set; }

        public int MinPerParent { get; set; }

        public int MaxPerParent { get; set; }
    }

    /// <summary>Текст семейства для поиска кандидатов: где взят и что в нём.</summary>
    public sealed class KitText
    {
        public KitText(string where, string text)
        {
            Where = where;
            Text = text;
        }

        public string Where { get; }

        public string Text { get; }
    }

    /// <summary>Семейство верхнего уровня с общими вложенными — строка списка во вкладке «Комплекты».</summary>
    public sealed class KitFamilyInfo
    {
        public KitFamilyInfo(string family)
        {
            Family = family;
        }

        public string Family { get; }

        public string Category { get; set; } = string.Empty;

        /// <summary>Шкаф ПК: комплект собирается по Комплект_Номер, правило проекта не нужно.</summary>
        public bool IsCabinet { get; set; }

        /// <summary>Группа по таблице групп; null — строки нет или «не трогать».</summary>
        public string? Group { get; set; }

        public bool Skipped { get; set; }

        public int Instances => Types.Sum(t => t.Instances);

        public List<KitTypeInfo> Types { get; } = new List<KitTypeInfo>();

        public List<KitNestedInfo> Nested { get; } = new List<KitNestedInfo>();

        public List<KitText> Texts { get; } = new List<KitText>();
    }

    /// <summary>Поиск кандидатов в комплекты по словам: имя семейства, типоразмера, текстовые параметры.</summary>
    public static class KitSearch
    {
        /// <summary>Где найдено первое слово; null — не найдено.</summary>
        public static string? Find(KitFamilyInfo family, IReadOnlyList<string> words)
        {
            if (words.Count == 0) return null;
            if (Hit(family.Family, words)) return "имя семейства";
            foreach (KitTypeInfo type in family.Types)
            {
                if (Hit(type.Name, words)) return $"тип «{type.Name}»";
            }
            foreach (KitText text in family.Texts)
            {
                if (Hit(text.Text, words)) return $"{text.Where}: «{Short(text.Text)}»";
            }
            return null;
        }

        private static bool Hit(string text, IReadOnlyList<string> words) =>
            words.Any(w => CultureInfo.InvariantCulture.CompareInfo.IndexOf(text, w, CompareOptions.IgnoreCase) >= 0);

        private static string Short(string text) => text.Length <= 60 ? text : text.Substring(0, 57) + "…";
    }

    /// <summary>Экземпляр в комплекте проекта: родитель (глубина 0) или его вложенное. Данные из модели, без Revit API.</summary>
    public sealed class ProjectKitElement
    {
        public long Id { get; set; }

        public long TopId { get; set; }

        public int Depth { get; set; }

        public string Family { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;
    }

    public sealed class ProjectKitPlan
    {
        /// <summary>Id → ADSK_Группирование «группа.K.ранг»; перекрывает группу по таблице.</summary>
        public Dictionary<long, string> Values { get; } = new Dictionary<long, string>();

        /// <summary>Id → откуда значение (для отчёта).</summary>
        public Dictionary<long, string> Sources { get; } = new Dictionary<long, string>();

        public List<KitIssue> Issues { get; } = new List<KitIssue>();

        /// <summary>Номера K новым типоразмерам: сохраняются в модели при «Применить».</summary>
        public List<KitNumber> NewNumbers { get; } = new List<KitNumber>();

        /// <summary>K типоразмеров в модели (сохранённые и новые) — для вкладки «Комплекты».</summary>
        public List<KitNumber> Numbers { get; } = new List<KitNumber>();

        public int Parents { get; set; }

        public int Members { get; set; }
    }

    /// <summary>
    /// Комплекты проекта в группировании: родитель «группа.K.00», вложенные из состава «группа.K.ранг» — тот же формат,
    /// что у шкафов ПК (<see cref="KitGrouping.Value"/>). Вложенные не из состава получают группу по таблице.
    /// </summary>
    public static class ProjectKitGrouping
    {
        /// <param name="elements">Родители (глубина 0) семейств с правилом и их вложенные.</param>
        /// <param name="topRule">Строка таблицы групп для родителя; null — строки нет.</param>
        /// <param name="cabinetKeys">K шкафов ПК в модели по группам: заняты для новых номеров, совпадение в группе — замечание.</param>
        public static ProjectKitPlan Plan(IEnumerable<ProjectKitElement> elements, KitSettings settings, Func<long, GroupRule?> topRule,
            IEnumerable<(string Group, int K)> cabinetKeys)
        {
            var plan = new ProjectKitPlan();
            List<ProjectKitElement> list = elements.ToList();
            List<(string Group, int K)> cabinets = cabinetKeys.ToList();
            var tops = new Dictionary<long, (KitRule Rule, string Group, string Family, string Type)>();
            var skipped = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            var noGroup = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            var types = new List<(string Family, string Type)>();
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var presentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ProjectKitElement parent in list.Where(e => e.Depth == 0))
            {
                present.Add(parent.Family.Trim());
                presentTypes.Add(parent.Family.Trim() + "\n" + parent.Type.Trim());
                KitRule? rule = settings.RuleFor(parent.Family, parent.Type);
                if (rule == null) continue;
                GroupRule? row = topRule(parent.Id);
                if (row?.Skip == true)
                {
                    Add(skipped, parent.Family, parent.Id);
                    continue;
                }
                if (row == null || row.Group.Length == 0)
                {
                    Add(noGroup, parent.Family, parent.Id);
                    continue;
                }
                tops[parent.Id] = (rule, row.Group, parent.Family, parent.Type);
                types.Add((parent.Family, parent.Type));
            }

            plan.NewNumbers.AddRange(settings.AssignNumbers(types, cabinets.Select(c => c.K)));
            KitSettings numbered = settings.WithNumbers(plan.NewNumbers);
            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string family, string type) in types)
            {
                if (!shown.Add(family.Trim() + "\n" + type.Trim())) continue;
                plan.Numbers.Add(new KitNumber(family, type, numbered.NumberFor(family, type) ?? 0));
            }

            var matched = new Dictionary<KitRule, HashSet<int>>();
            var kitKeys = new Dictionary<(string Group, int K), string>();
            foreach (ProjectKitElement e in list)
            {
                if (!tops.TryGetValue(e.TopId, out (KitRule Rule, string Group, string Family, string Type) top)) continue;
                int k = numbered.NumberFor(top.Family, top.Type) ?? 0;
                if (k < 1) continue;
                int rank = 0;
                if (e.Depth > 0)
                {
                    rank = top.Rule.RankOf(e.Family, e.Type);
                    if (rank == 0) continue;
                    if (!matched.TryGetValue(top.Rule, out HashSet<int>? ranks))
                    {
                        ranks = new HashSet<int>();
                        matched[top.Rule] = ranks;
                    }
                    ranks.Add(rank);
                    plan.Members++;
                }
                else
                {
                    plan.Parents++;
                    kitKeys[(top.Group, k)] = top.Family + " : " + top.Type;
                }
                plan.Values[e.Id] = KitGrouping.Value(top.Group, k + rank / 100.0);
                plan.Sources[e.Id] = $"комплект «{top.Rule.Label}», K = {k}, ранг .{rank:00}";
            }

            foreach (KeyValuePair<string, List<long>> pair in skipped)
            {
                plan.Issues.Add(new KitIssue(Severity.Warning, $"Комплект «{pair.Key}» не применён: в таблице групп у него «не трогать» ({pair.Value.Count} шт.)", pair.Value,
                    "Уберите строку «не трогать» этого семейства в таблице групп, если комплект должен нумероваться."));
            }
            foreach (KeyValuePair<string, List<long>> pair in noGroup)
            {
                plan.Issues.Add(new KitIssue(Severity.Warning, $"Комплект «{pair.Key}»: в таблице групп нет группы для родителя ({pair.Value.Count} шт.)", pair.Value,
                    "Добавьте категорию родителя, строку семейства или «*» в таблицу групп."));
            }
            foreach ((string group, int k) in kitKeys.Keys.Where(key => cabinets.Contains(key)).OrderBy(key => key.K))
            {
                plan.Issues.Add(new KitIssue(Severity.Warning, $"ПРОВЕРЬ K: в группе {group} K = {k} и у шкафа ПК, и у комплекта «{kitKeys[(group, k)]}»",
                    Array.Empty<long>(), "Строки двух комплектов перемешаются в спецификации. Дайте шкафу другой Комплект_Номер."));
            }
            foreach (KitRule rule in settings.Rules)
            {
                if (!present.Contains(rule.Family))
                {
                    plan.Issues.Add(new KitIssue(Severity.Info, $"Правило комплекта: семейства «{rule.Family}» нет в модели (как элемента верхнего уровня)", Array.Empty<long>(),
                        "Если семейство переименовали или загрузили копию под другим именем — настройте комплект заново или удалите правило."));
                    continue;
                }
                if (rule.IsTypeOverride && !presentTypes.Contains(rule.Family + "\n" + rule.Type))
                {
                    plan.Issues.Add(new KitIssue(Severity.Info, $"Свой состав типоразмера «{rule.Label}»: такого типоразмера нет в модели", Array.Empty<long>(), string.Empty));
                    continue;
                }
                if (!tops.Values.Any(t => ReferenceEquals(t.Rule, rule))) continue;
                matched.TryGetValue(rule, out HashSet<int>? found);
                for (int i = 0; i < rule.Members.Count; i++)
                {
                    if (found?.Contains(i + 1) == true) continue;
                    plan.Issues.Add(new KitIssue(Severity.Info, $"Комплект «{rule.Label}»: вложенного «{rule.Members[i].Label}» (ранг .{i + 1:00}) нет ни в одном экземпляре",
                        Array.Empty<long>(), "Проверьте состав во вкладке «Комплекты»: возможно, семейство вложенного заменили."));
                }
            }
            return plan;
        }

        private static void Add(Dictionary<string, List<long>> map, string key, long id)
        {
            if (!map.TryGetValue(key.Trim(), out List<long>? ids))
            {
                ids = new List<long>();
                map[key.Trim()] = ids;
            }
            ids.Add(id);
        }
    }
}
