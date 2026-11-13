using System;
using System.Collections.Generic;
using System.Linq;

namespace VKTools.Core.Schedules
{
    /// <summary>Условие фильтра спецификации Revit по текстовому полю.</summary>
    public enum ConditionKind
    {
        Equal,
        NotEqual,
        BeginsWith,
        NotBeginsWith,

        /// <summary>Значение заполнено (без значения строки нет).</summary>
        HasValue,

        /// <summary>У элемента есть параметр (ограничивает спецификацию нескольких категорий категориями ВК).</summary>
        HasParameter,
    }

    public sealed class FilterCondition
    {
        public FilterCondition(ConditionKind kind, string value = "")
        {
            Kind = kind;
            Value = value ?? string.Empty;
        }

        public ConditionKind Kind { get; }

        public string Value { get; }

        /// <summary>Как в окне «Свойства спецификации → Фильтр», для окна и отчёта.</summary>
        public string Describe(string field) => Kind switch
        {
            ConditionKind.Equal => $"«{field}» равно «{Value}»",
            ConditionKind.NotEqual => $"«{field}» не равно «{Value}»",
            ConditionKind.BeginsWith => $"«{field}» начинается с «{Value}»",
            ConditionKind.NotBeginsWith => $"«{field}» не начинается с «{Value}»",
            ConditionKind.HasValue => $"«{field}» имеет значение",
            _ => $"«{field}» есть у элемента",
        };

        public override string ToString() => Describe("поле");
    }

    /// <summary>Фильтры одного поля (система или этаж) для выбранного набора значений.</summary>
    public sealed class FilterPlan
    {
        public FilterPlan(bool all, IReadOnlyList<FilterCondition> conditions, string? error)
        {
            All = all;
            Conditions = conditions;
            Error = error;
        }

        /// <summary>Выбраны все значения: фильтра по полю нет.</summary>
        public bool All { get; }

        public IReadOnlyList<FilterCondition> Conditions { get; }

        /// <summary>Не удалось выразить выбор фильтрами — причина для пользователя.</summary>
        public string? Error { get; }
    }

    /// <summary>
    /// Фильтры спецификации Revit соединяются только через «И», «ИЛИ» нет. Набор «В1, Т3, Т4» поэтому
    /// выражается исключением остального: «не начинается с К», «не равно В2». Плагин подбирает самый короткий
    /// набор условий и проверяет, что он пропускает ровно выбранные значения из модели.
    /// Сравнение в Revit без учёта регистра — проверки здесь консервативны для обоих случаев.
    /// </summary>
    public static class ScheduleFilterPlanner
    {
        private static readonly StringComparison Ci = StringComparison.CurrentCultureIgnoreCase;

        /// <param name="known">Все непустые значения поля в модели.</param>
        /// <param name="selected">Выбранные значения (подмножество <paramref name="known"/>).</param>
        public static FilterPlan Plan(IEnumerable<string> known, IEnumerable<string> selected)
        {
            List<string> all = Distinct(known);
            List<string> chosen = Distinct(selected).Where(s => all.Contains(s, StringComparer.Ordinal)).ToList();
            if (chosen.Count == 0) return new FilterPlan(false, Array.Empty<FilterCondition>(), "ничего не выбрано");
            List<string> rest = all.Where(v => !chosen.Contains(v, StringComparer.Ordinal)).ToList();
            if (rest.Count == 0) return new FilterPlan(true, Array.Empty<FilterCondition>(), null);

            if (chosen.Any(c => rest.Any(r => string.Equals(c, r, Ci))))
            {
                string pair = chosen.First(c => rest.Any(r => string.Equals(c, r, Ci)));
                return new FilterPlan(false, Array.Empty<FilterCondition>(),
                    $"значения «{pair}» и «{rest.First(r => string.Equals(pair, r, Ci))}» отличаются только регистром букв: Revit их в фильтре не различит. Исправьте написание в модели");
            }

            var candidates = new List<List<FilterCondition>>();
            if (chosen.Count == 1)
            {
                candidates.Add(new List<FilterCondition> { new FilterCondition(ConditionKind.Equal, chosen[0]) });
            }
            string prefix = CommonPrefix(chosen);
            if (prefix.Length > 0 && chosen.Count > 1)
            {
                var plan = new List<FilterCondition> { new FilterCondition(ConditionKind.BeginsWith, prefix) };
                plan.AddRange(Cover(rest.Where(r => r.StartsWith(prefix, Ci)).ToList(), chosen));
                candidates.Add(plan);
            }
            var exclusion = new List<FilterCondition> { new FilterCondition(ConditionKind.HasValue) };
            exclusion.AddRange(Cover(rest, chosen));
            candidates.Add(exclusion);

            foreach (List<FilterCondition> plan in candidates.OrderBy(c => c.Count))
            {
                if (all.All(v => Matches(plan, v) == chosen.Contains(v, StringComparer.Ordinal)) && !Matches(plan, string.Empty))
                {
                    return new FilterPlan(false, plan, null);
                }
            }
            return new FilterPlan(false, Array.Empty<FilterCondition>(), "выбор не выразить фильтрами спецификации");
        }

        /// <summary>Пропустит ли набор условий значение (как Revit: без учёта регистра).</summary>
        public static bool Matches(IEnumerable<FilterCondition> conditions, string? value)
        {
            string v = value ?? string.Empty;
            foreach (FilterCondition c in conditions)
            {
                bool ok = c.Kind switch
                {
                    ConditionKind.Equal => string.Equals(v, c.Value, Ci),
                    ConditionKind.NotEqual => !string.Equals(v, c.Value, Ci),
                    ConditionKind.BeginsWith => v.Length > 0 && v.StartsWith(c.Value, Ci),
                    ConditionKind.NotBeginsWith => !v.StartsWith(c.Value, Ci),
                    ConditionKind.HasValue => v.Trim().Length > 0,
                    _ => true,
                };
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// Исключить значения: префиксом «не начинается с», если он покрывает два и больше значения и не задевает выбранные
        /// (короче — лучше: новая система «К5» тоже не попадёт в спецификацию водоснабжения), иначе «не равно».
        /// </summary>
        private static List<FilterCondition> Cover(List<string> exclude, List<string> chosen)
        {
            var result = new List<FilterCondition>();
            var left = new List<string>(exclude);
            while (left.Count > 0)
            {
                string? best = null;
                int bestCount = 0;
                foreach (string u in left)
                {
                    for (int len = 1; len <= u.Length; len++)
                    {
                        string p = u.Substring(0, len);
                        if (p.Trim().Length == 0 || chosen.Any(s => s.StartsWith(p, Ci))) continue;
                        int count = left.Count(v => v.StartsWith(p, Ci));
                        if (count > bestCount || (count == bestCount && best != null && p.Length < best.Length))
                        {
                            best = p;
                            bestCount = count;
                        }
                    }
                }
                if (best != null && bestCount >= 2)
                {
                    result.Add(new FilterCondition(ConditionKind.NotBeginsWith, best));
                    left.RemoveAll(v => v.StartsWith(best, Ci));
                    continue;
                }
                foreach (string v in left) result.Add(new FilterCondition(ConditionKind.NotEqual, v));
                left.Clear();
            }
            return result;
        }

        private static string CommonPrefix(List<string> values)
        {
            string prefix = values[0];
            foreach (string v in values.Skip(1))
            {
                int i = 0;
                while (i < prefix.Length && i < v.Length && char.ToUpperInvariant(prefix[i]) == char.ToUpperInvariant(v[i])) i++;
                prefix = prefix.Substring(0, i);
            }
            return prefix.Trim().Length == 0 ? string.Empty : prefix;
        }

        private static List<string> Distinct(IEnumerable<string> values) =>
            (values ?? Array.Empty<string>()).Select(v => (v ?? string.Empty).Trim()).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }
}

namespace VKTools.Core.Schedules
{
    /// <summary>Все фильтры новой спецификации: по системе и по этажу, с проверкой предела Revit.</summary>
    public sealed class ScheduleFilterSet
    {
        /// <summary>Больше фильтров окно «Свойства спецификации» не показывает: дальше спецификацию не поправить руками.</summary>
        public const int MaxFilters = 8;

        private ScheduleFilterSet(FilterPlan systems, FilterPlan floors, int otherFilters)
        {
            Systems = systems;
            Floors = floors;
            OtherFilters = otherFilters;
            // Без условий по системе спецификация нескольких категорий покажет и стены, и двери: «у элемента есть параметр системы»
            SystemConditions = systems.All
                ? new List<FilterCondition> { new FilterCondition(ConditionKind.HasParameter) }
                : systems.Conditions.ToList();
            Count = SystemConditions.Count + floors.Conditions.Count + otherFilters;
            if (systems.Error != null) Error = "системы: " + systems.Error;
            else if (floors.Error != null) Error = "этажи: " + floors.Error;
            else if (Count > MaxFilters)
            {
                Error = $"нужно {Count} фильтров, а в спецификации Revit их не больше {MaxFilters}"
                    + (otherFilters > 0 ? $" (из них {otherFilters} — фильтры эталона)" : string.Empty)
                    + ". Выберите группу систем или этажи подряд — тогда хватит одного-двух условий";
            }
        }

        public FilterPlan Systems { get; }

        public FilterPlan Floors { get; }

        public IReadOnlyList<FilterCondition> SystemConditions { get; }

        public int OtherFilters { get; }

        public int Count { get; }

        public string? Error { get; }

        public static ScheduleFilterSet Build(IEnumerable<string> knownSystems, IEnumerable<string> systems,
            IEnumerable<string> knownFloors, IEnumerable<string> floors, int otherFilters)
        {
            List<string> floorKnown = knownFloors.ToList();
            List<string> floorChosen = floors.ToList();
            FilterPlan floorPlan = floorKnown.Count == 0 || floorKnown.All(f => floorChosen.Contains(f, StringComparer.Ordinal))
                ? new FilterPlan(true, Array.Empty<FilterCondition>(), null)
                : ScheduleFilterPlanner.Plan(floorKnown, floorChosen);
            return new ScheduleFilterSet(ScheduleFilterPlanner.Plan(knownSystems, systems), floorPlan, otherFilters);
        }

        public List<string> Describe(string systemField, string floorField)
        {
            var lines = SystemConditions.Select(c => c.Describe(systemField)).ToList();
            lines.AddRange(Floors.Conditions.Select(c => c.Describe(floorField)));
            return lines;
        }
    }
}
