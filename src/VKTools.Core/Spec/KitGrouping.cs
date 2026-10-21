using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VKTools.Core.Checks;

namespace VKTools.Core.Spec
{
    /// <summary>
    /// Экземпляр семейства глазами правила комплектов: шкаф (верхний уровень с K ≥ 1),
    /// комплектующее (есть признак) или ни то ни другое. Данные из модели, без Revit API.
    /// </summary>
    public sealed class KitElement
    {
        public long Id { get; set; }

        /// <summary>Верхний родитель (для элемента верхнего уровня — он сам).</summary>
        public long TopId { get; set; }

        /// <summary>0 — верхний уровень, 1 — вложен в него и т. д.</summary>
        public int Depth { get; set; }

        /// <summary>Цепочка SuperComponent длиннее предела: верхний родитель не найден.</summary>
        public bool TooDeep { get; set; }

        /// <summary>У экземпляра есть параметр-признак комплектующего (Сортировка_Целое).</summary>
        public bool IsComponent { get; set; }

        /// <summary>Верхний уровень: K из типа (Комплект_Номер). Вложенный: ключ (ADSK_Позиция_Комплект).</summary>
        public double? Number { get; set; }

        /// <summary>K типа верхнего родителя.</summary>
        public double? TopNumber { get; set; }

        /// <summary>«Семейство : Тип» — для отчёта и проверки «один K у разных типоразмеров».</summary>
        public string TypeLabel { get; set; } = string.Empty;
    }

    public sealed class KitIssue
    {
        public KitIssue(Severity severity, string message, IEnumerable<long> ids, string action)
        {
            Severity = severity;
            Message = message;
            Ids = ids.ToList();
            Action = action;
        }

        public Severity Severity { get; }

        public string Message { get; }

        public List<long> Ids { get; }

        public string Action { get; }
    }

    public sealed class KitPlan
    {
        /// <summary>Id → ADSK_Группирование «группа.ключ»; перекрывает группу по категории.</summary>
        public Dictionary<long, string> Values { get; } = new Dictionary<long, string>();

        public List<KitIssue> Issues { get; } = new List<KitIssue>();

        public int Cabinets { get; set; }

        public int Components { get; set; }
    }

    /// <summary>
    /// Комплекты пожарных шкафов (fire_cabinet_postprocess v1.0): шкаф и его комплектующие встают в спецификации подряд.
    /// ADSK_Группирование = «группа шкафа.ключ», ключ — K шкафа или K + ранг комплектующего, 12 знаков с двумя после точки.
    /// </summary>
    public static class KitGrouping
    {
        public static KitPlan Plan(IEnumerable<KitElement> elements, Func<long, string?> topGroup)
        {
            var plan = new KitPlan();
            var cabinetTypes = new Dictionary<int, Dictionary<string, List<long>>>();
            var unbound = new List<long>();
            var tooDeep = new List<long>();
            var noKey = new Dictionary<string, List<long>>(StringComparer.Ordinal);
            var mismatch = new List<(long Id, string Text)>();
            var emptyGroup = new HashSet<long>();

            foreach (KitElement e in elements)
            {
                if (e.TooDeep)
                {
                    tooDeep.Add(e.Id);
                    continue;
                }
                double key;
                if (e.Depth == 0)
                {
                    if (e.Number == null || e.Number.Value < 1)
                    {
                        if (e.IsComponent) unbound.Add(e.Id);
                        continue;
                    }
                    key = e.Number.Value;
                    int k = (int)Math.Round(key, MidpointRounding.AwayFromZero);
                    if (!cabinetTypes.TryGetValue(k, out Dictionary<string, List<long>>? types))
                    {
                        types = new Dictionary<string, List<long>>(StringComparer.Ordinal);
                        cabinetTypes[k] = types;
                    }
                    if (!types.TryGetValue(e.TypeLabel, out List<long>? ids))
                    {
                        ids = new List<long>();
                        types[e.TypeLabel] = ids;
                    }
                    ids.Add(e.Id);
                }
                else
                {
                    if (!e.IsComponent) continue;
                    if (e.Number == null || e.Number.Value < 1)
                    {
                        if (!noKey.TryGetValue(e.TypeLabel, out List<long>? ids))
                        {
                            ids = new List<long>();
                            noKey[e.TypeLabel] = ids;
                        }
                        ids.Add(e.Id);
                        continue;
                    }
                    key = e.Number.Value;
                    if (!KeyMatches(key, e.TopNumber))
                    {
                        mismatch.Add((e.Id, $"{e.TypeLabel}: ключ {Format(key)} не совпадает с K шкафа {(e.TopNumber.HasValue ? Format(e.TopNumber.Value) : "— (нет K)")}"));
                        continue;
                    }
                }

                string? group = topGroup(e.TopId);
                if (string.IsNullOrEmpty(group))
                {
                    emptyGroup.Add(e.TopId);
                    continue;
                }
                plan.Values[e.Id] = Value(group!, key);
                if (e.Depth == 0) plan.Cabinets++;
                else plan.Components++;
            }

            if (unbound.Count > 0)
            {
                plan.Issues.Add(new KitIssue(Severity.Warning, $"Комплектующее шкафа стоит в модели само по себе, без шкафа: {unbound.Count} шт.", unbound,
                    "Вставьте его в семейство шкафа или удалите. ADSK_Группирование ему ставится по категории."));
            }
            foreach (KeyValuePair<string, List<long>> pair in noKey)
            {
                plan.Issues.Add(new KitIssue(Severity.Warning, $"{pair.Key}: ключ комплекта 0 или пусто — в семействе шкафа не настроена связь «Сортировка_Целое»: {pair.Value.Count} шт.", pair.Value,
                    "Настройте ключ в семействе шкафа. Пока ADSK_Группирование ставится по категории."));
            }
            foreach ((long id, string text) in mismatch)
            {
                plan.Issues.Add(new KitIssue(Severity.Warning, text, new[] { id },
                    "Проверьте Комплект_Номер типа шкафа и ключ в семействе. Пока ADSK_Группирование ставится по категории."));
            }
            foreach (KeyValuePair<int, Dictionary<string, List<long>>> pair in cabinetTypes.Where(p => p.Value.Count > 1).OrderBy(p => p.Key))
            {
                plan.Issues.Add(new KitIssue(Severity.Warning,
                    $"ПРОВЕРЬ K: K = {pair.Key} у разных типоразмеров шкафов: {string.Join("; ", pair.Value.Keys.OrderBy(x => x, StringComparer.Ordinal))}",
                    pair.Value.Values.SelectMany(x => x),
                    "Комплекты разных шкафов сольются в спецификации. Дайте типоразмерам разные Комплект_Номер."));
            }
            if (tooDeep.Count > 0)
            {
                plan.Issues.Add(new KitIssue(Severity.Warning, $"Вложенность глубже 10 уровней, шкаф не найден: {tooDeep.Count} шт.", tooDeep, string.Empty));
            }
            if (emptyGroup.Count > 0)
            {
                plan.Issues.Add(new KitIssue(Severity.Warning, $"У шкафа нет группы по категории (её нет в grouping.csv): {emptyGroup.Count} шт.", emptyGroup,
                    "Добавьте категорию шкафа или строку «*» в grouping.csv."));
            }
            return plan;
        }

        /// <summary>Целая часть ключа комплектующего = K шкафа (ключ 3,01 → шкаф 3).</summary>
        public static bool KeyMatches(double key, double? cabinetNumber) =>
            cabinetNumber.HasValue && (int)Math.Floor(key + 1e-6) == (int)Math.Round(cabinetNumber.Value, MidpointRounding.AwayFromZero);

        /// <summary>«1» + 3,01 → «1.000000003.01»: ширина 12 знаков, чтобы текстовая сортировка шла по числу.</summary>
        public static string Value(string group, double key) => group + "." + key.ToString("000000000.00", CultureInfo.InvariantCulture);

        private static string Format(double value) => value.ToString("0.##", CultureInfo.GetCultureInfo("ru-RU"));
    }
}
