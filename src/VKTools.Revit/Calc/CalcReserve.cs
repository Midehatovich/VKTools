using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using VKTools.Core.Calc;
using VKTools.Core.Checks;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Calc
{
    /// <summary>
    /// Запас на штучные позиции (REQ-CALC-010, порт ClampReserve v1.1): по каждой строке отмеченной спецификации, где есть
    /// позиции из таблицы запаса, — одна служебная позиция на ⌈N × k⌉ − N с теми же значениями полей сортировки и группировки,
    /// чтобы Revit слил её с этой строкой. Позиции — те же _SYS_SpecCalcItem, ключ «спецификация | система | ЗАПАС …»,
    /// свой типоразмер на строку (наименование, единица и группа строки могут быть параметрами типа).
    /// </summary>
    internal static class CalcReserve
    {
        public const string TypePrefix = "ЗАПАС";
        private const string TypeNameBad = "\\:{}[]|;<>?`~";
        private static readonly char[] Space = { ' ' };

        public static void Add(Document doc, CalcPlan plan, CalcRuleSet rules)
        {
            if (rules.Reserve.Count == 0) return;
            List<CalcScope> scopes = plan.Scopes.Where(s => s.Schedule != null).ToList();
            if (scopes.Count == 0)
            {
                plan.Issues.Add(Severity.Info, "запас на штучные считается по строкам отмеченных спецификаций — спецификации не отмечены, запаса нет",
                    "Отметьте спецификации на вкладке «Спецификации».");
                return;
            }
            Dictionary<string, List<(Definition Definition, ElementBinding Binding)>> bindings = CalcParams.ProjectBindings(doc);
            string systemName = plan.Options.SystemParameter.Trim();
            var resources = new Dictionary<string, CalcResource>(StringComparer.Ordinal);
            int rows = 0, unmerged = 0;
            var shown = new HashSet<string>(StringComparer.Ordinal);
            // Параметр типа в самом служебном семействе (пользователь мог отредактировать его) — значение уходит в тип запаса
            FamilySymbol? baseSymbol = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => s.FamilyName == plan.FamilyName);
            // Уже записанные позиции запаса: сверить их поля строки с позицией (то, по чему Revit сливает строки)
            var existing = new Dictionary<string, FamilyInstance>(StringComparer.Ordinal);
            foreach (FamilyInstance item in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType().Cast<FamilyInstance>()
                .Where(fi => fi.Symbol != null && fi.Symbol.FamilyName == plan.FamilyName))
            {
                string itemKey = CalcParams.Find(item, CalcParams.Key, out _)?.AsString() ?? string.Empty;
                if (itemKey.Length > 0 && !existing.ContainsKey(itemKey)) existing[itemKey] = item;
            }
            foreach (CalcScope scope in scopes)
            {
                ViewSchedule schedule = scope.Schedule!;
                List<FamilyInstance> pieces = new FilteredElementCollector(doc, schedule.Id).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType()
                    .Cast<FamilyInstance>().Where(fi => fi.Symbol != null && rules.Reserve.Any(r => r.Matches(fi.Symbol.FamilyName, fi.Symbol.Name))).ToList();
                if (pieces.Count == 0) continue;
                List<(string Name, ElementId Id)>? sortFields = SortFields(scope, plan);
                if (sortFields == null) continue;
                List<string> fields = sortFields.Select(f => f.Name).ToList();
                var ids = sortFields.ToDictionary(f => f.Name, f => f.Id, StringComparer.Ordinal);
                plan.ReserveFields.UnionWith(fields.Where(f => f != systemName && !IsTypeOf(f)));
                // Остальные видимые текстовые колонки (ADSK_Масса_Текст…) — тоже с позиции, иначе в слитой строке «<варианты>»
                List<string> visible = VisibleFields(scope, fields, systemName);
                plan.Notes.Add($"Запас, «{scope.Name}»: строка спецификации — по полям {string.Join(", ", fields.Select(f => "«" + f + "»"))}; позиция запаса получает их значения"
                    + (visible.Count > 0 ? $", а также колонок {string.Join(", ", visible.Select(f => "«" + f + "»"))}." : "."));
                var unread = new HashSet<string>(StringComparer.Ordinal);

                var groups = new Dictionary<string, List<FamilyInstance>>(StringComparer.Ordinal);
                foreach (FamilyInstance piece in pieces)
                {
                    string key = string.Join("\u0001", fields.Select(f => Value(piece, f, ids[f]) is ParamValue v ? CalcParams.KeyOf(v) : string.Empty));
                    if (!groups.TryGetValue(key, out List<FamilyInstance>? list))
                    {
                        list = new List<FamilyInstance>();
                        groups[key] = list;
                    }
                    list.Add(piece);
                }
                foreach (KeyValuePair<string, List<FamilyInstance>> group in groups.OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    FamilyInstance first = group.Value[0];
                    List<ReserveRow> matched = group.Value.Select(p => rules.Reserve.First(r => r.Matches(p.Symbol.FamilyName, p.Symbol.Name))).Distinct().ToList();
                    ReserveRow rule = matched[0];
                    if (matched.Count > 1)
                    {
                        plan.Issues.Add(Severity.Warning, $"«{scope.Name}»: в одной строке спецификации позиции с разным запасом ({string.Join(", ", matched.Select(m => CalcTables.Format(m.Percent) + " %"))}) — взят {CalcTables.Format(rule.Percent)} %",
                            "Строки таблицы запаса для одной строки спецификации должны совпадать.", first.Id.ToLong());
                    }
                    double n = group.Value.Sum(Quantity);
                    double total = rule.Total(n);
                    double extra = Math.Round(total - n, 6);
                    if (extra <= 0) continue;

                    string system = Value(first, systemName) is ParamValue sys ? CalcParams.Display(sys) : string.Empty;
                    // Как есть, без Trim: «Rapid DN50 » с пробелом в конце (таблица поиска) — другая строка спецификации
                    string name = Exact(first, CalcParams.Name, ids);
                    string unit = Exact(first, CalcParams.Unit, ids);
                    string groupValue = Exact(first, CalcParams.Group, ids);
                    var extras = new List<KeyValuePair<string, string>>();
                    var copy = new Dictionary<string, ParamValue>(StringComparer.Ordinal);
                    foreach (string field in fields.Where(f => f != systemName && !IsTypeOf(f)))
                    {
                        if (!(Value(first, field, ids[field]) is ParamValue value))
                        {
                            // Пустое у позиции — пустое и у запаса; но «нет» или «два параметра с таким именем» — строки не сольются
                            if (!Readable(first, field, ids[field]) && unread.Add(field))
                            {
                                plan.Issues.Add(Severity.Warning, $"«{scope.Name}»: поле сортировки «{field}» у «{first.Symbol.FamilyName}» не читается (нет параметра или два с таким именем) — запас может не слиться со строкой",
                                    "Пришлите разработчику скрин свойств позиции и вкладки «Сортировка/группирование» спецификации.", first.Id.ToLong());
                            }
                            continue;
                        }
                        bool typeBound = (bindings.TryGetValue(field, out var list) && list.Count == 1 && list[0].Binding is TypeBinding) || CalcParams.Has(baseSymbol, field);
                        if (typeBound) extras.Add(new KeyValuePair<string, string>(field, CalcParams.Display(value)));
                        else copy[field] = value;
                    }
                    foreach (string field in visible)
                    {
                        if (Value(first, field) is ParamValue shownValue && shownValue.Kind == ParamValueKind.Text && !extras.Any(e => e.Key == field))
                        {
                            extras.Add(new KeyValuePair<string, string>(field, CalcParams.Display(shownValue)));
                        }
                    }
                    string code = TypeName(name.Trim().Length > 0 ? name : first.Symbol.FamilyName + " " + first.Symbol.Name, group.Key);
                    if (!resources.TryGetValue(code, out CalcResource? resource))
                    {
                        resource = CalcResource.ForReserve(code, name, unit.Trim().Length > 0 ? unit : "шт", groupValue, extras);
                        resources[code] = resource;
                    }
                    var target = new CalcTarget(scope.Name, system, resource, n, extra)
                    {
                        Basis = $"запас {CalcTables.Format(rule.Percent)} % на строку: N = {CalcTables.Format(n)} → ⌈{CalcTables.Format(n)} × {CalcTables.Format(1 + rule.Percent / 100, 4)}⌉ = "
                            + $"{CalcTables.Format(total)}, позиция +{CalcTables.Format(extra)}",
                        Phase = first.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId()?.ToLong() ?? -1,
                        Level = first.LevelId?.ToLong() ?? -1,
                    };
                    foreach (KeyValuePair<string, ParamValue> c in copy)
                    {
                        string key = CalcParams.KeyOf(c.Value);
                        target.Copy[c.Key] = key;
                        plan.CopyValues[c.Key + "\u0001" + key] = c.Value;
                    }
                    plan.Result.Targets.Add(target);
                    rows++;
                    if (existing.TryGetValue(target.Key, out FamilyInstance? written))
                    {
                        // Контроль: значения полей первой строки каждой спецификации — видно, что сравнивается
                        if (shown.Add(scope.Name))
                        {
                            plan.Notes.Add($"Запас, «{scope.Name}», {system}, «{name}»: хомут {first.Id.ToLong()} / запас {written.Id.ToLong()}: "
                                + string.Join("; ", fields.Concat(visible).Select(f => $"{f} [{Id(ids, f).ToLong()}] = {Describe(first, f, Id(ids, f))} / {Describe(written, f, Id(ids, f))}")));
                        }
                        List<string> diff = fields.Concat(visible).Select(f => (f, piece: Describe(first, f, Id(ids, f)), item: Describe(written, f, Id(ids, f))))
                            .Where(d => Bare(d.piece) != Bare(d.item)).Select(d => $"«{d.f}»: у позиции {d.piece}, у запаса {d.item}").ToList();
                        if (diff.Count > 0)
                        {
                            unmerged++;
                            if (unmerged <= 20)
                            {
                                plan.Issues.Add(Severity.Warning, $"«{scope.Name}», {system}: запас «{name}» не сольётся со строкой — " + string.Join("; ", diff),
                                    "Пришлите разработчику эту строку отчёта (или CSV).", written.Id.ToLong());
                            }
                        }
                    }
                }
            }
            if (rows > 0) plan.Notes.Add($"Запас на штучные: строк спецификаций с запасом {rows}" + (unmerged > 0 ? $", из них записанный запас не совпадает со строкой по полям: {unmerged}" : string.Empty) + ".");
        }

        /// <summary>Поля сортировки и группировки (и фильтров) спецификации — то, по чему Revit сливает строки. null — запас по ней невозможен.</summary>
        private static List<(string Name, ElementId Id)>? SortFields(CalcScope scope, CalcPlan plan)
        {
            ScheduleDefinition definition = scope.Schedule!.Definition;
            if (definition.IsItemized)
            {
                plan.Issues.Add(Severity.Warning, $"«{scope.Name}»: включено «Для каждого экземпляра» — позиция запаса стала бы отдельной строкой, запас не считается",
                    "Выключите «Для каждого экземпляра» в свойствах спецификации (Сортировка/группирование).");
                return null;
            }
            ScheduleField? quantity = Enumerable.Range(0, definition.GetFieldCount()).Select(definition.GetField).FirstOrDefault(f => f.GetName() == CalcParams.Quantity);
            if (quantity == null || quantity.DisplayType != ScheduleFieldDisplayType.Totals)
            {
                plan.Issues.Add(Severity.Warning, $"«{scope.Name}»: {(quantity == null ? $"нет поля {CalcParams.Quantity}" : $"у поля {CalcParams.Quantity} не включено «Вычислять итоги»")} — запас не попадёт в «Кол.», запас не считается",
                    $"Добавьте поле {CalcParams.Quantity} с «Вычислять итоги» (Формат поля).");
                return null;
            }
            var names = new List<(string Name, ElementId Id)>();
            foreach (ScheduleSortGroupField sort in definition.GetSortGroupFields())
            {
                ScheduleField field = definition.GetField(sort.FieldId);
                if (field.ParameterId.ToLong() < 0)
                {
                    plan.Issues.Add(Severity.Warning, $"«{scope.Name}»: сортировка по системному полю «{field.GetName()}» — позиция запаса не может получить его значение, запас не считается",
                        "Сортируйте и группируйте по параметрам проекта (LSR_Имя системы, ADSK_Группирование, ADSK_Наименование, ADSK_Марка).");
                    return null;
                }
                if (!names.Any(n => n.Name == field.GetName())) names.Add((field.GetName(), field.ParameterId));
            }
            names.AddRange(scope.CopyNames.Where(n => !names.Any(x => x.Name == n)).Select(n => (n, ElementId.InvalidElementId)));
            return names;
        }

        private static ElementId Id(Dictionary<string, ElementId> ids, string name) => ids.TryGetValue(name, out ElementId? id) ? id : ElementId.InvalidElementId;

        /// <summary>Видимые колонки спецификации с параметром (не итоговые, не поля строки) — их текст копируется в запас, необязательно.</summary>
        private static List<string> VisibleFields(CalcScope scope, List<string> rowFields, string systemName)
        {
            ScheduleDefinition definition = scope.Schedule!.Definition;
            var result = new List<string>();
            for (int i = 0; i < definition.GetFieldCount(); i++)
            {
                ScheduleField field = definition.GetField(i);
                string name = field.GetName();
                if (field.IsHidden || field.ParameterId.ToLong() < 0 || field.DisplayType == ScheduleFieldDisplayType.Totals) continue;
                if (name == CalcParams.Quantity || name == systemName || IsTypeOf(name) || rowFields.Contains(name) || result.Contains(name)) continue;
                if (CalcFamilyBuilder.OwnParameters.Contains(name)) continue;
                result.Add(name);
            }
            return result;
        }

        /// <summary>Наименование, единица и группа уходят в тип позиции (как у ресурсов), остальное — копией с позиции строки.</summary>
        private static bool IsTypeOf(string field) => field == CalcParams.Name || field == CalcParams.Unit || field == CalcParams.Group;

        private static ParamValue? Value(Element element, string name) => Value(element, name, ElementId.InvalidElementId);

        /// <summary>Значение поля спецификации: по параметру поля (общий — по GUID), иначе по имени у экземпляра, затем у типа.</summary>
        private static ParamValue? Value(Element element, string name, ElementId id)
        {
            Parameter? p = ById(element, id) ?? ById(element.Document.GetElement(element.GetTypeId()), id)
                ?? CalcParams.Find(element, name, out _) ?? CalcParams.Find(element.Document.GetElement(element.GetTypeId()), name, out _);
            return p == null ? null : CalcParams.Raw(p);
        }

        private static bool Readable(Element element, string name, ElementId id) =>
            ById(element, id) != null || ById(element.Document.GetElement(element.GetTypeId()), id) != null
            || CalcParams.Find(element, name, out _) != null || CalcParams.Find(element.Document.GetElement(element.GetTypeId()), name, out _) != null;

        /// <summary>Параметр поля по его Id: у общих параметров (и во вложенных семействах) — по GUID, у параметров проекта — по Id.</summary>
        private static Parameter? ById(Element? element, ElementId id)
        {
            if (element == null || id == ElementId.InvalidElementId || id.ToLong() < 0) return null;
            if (element.Document.GetElement(id) is SharedParameterElement shared) return element.get_Parameter(shared.GuidValue);
            foreach (Parameter p in element.Parameters)
            {
                if (p.Id == id) return p;
            }
            return null;
        }

        /// <summary>Значение поля для сверки: «нет параметра», «(не задано)» или «текст» с типом хранения и местом (экземпляр/тип).</summary>
        private static string Describe(Element element, string name, ElementId id)
        {
            Element? type = element.Document.GetElement(element.GetTypeId());
            Parameter? p = ById(element, id);
            string where = "экз.";
            if (p == null && (p = ById(type, id)) != null) where = "тип";
            if (p == null && id != ElementId.InvalidElementId && id.ToLong() >= 0)
            {
                Parameter? byName = CalcParams.Find(element, name, out _) ?? CalcParams.Find(type, name, out _);
                return byName == null ? "нет параметра" : "нет параметра поля (есть одноимённый с другим GUID)";
            }
            if (p == null && (p = CalcParams.Find(element, name, out _)) == null && (p = CalcParams.Find(type, name, out _)) != null) where = "тип";
            if (p == null) return "нет параметра";
            if (!p.HasValue) return "(не задано)";
            string text = p.StorageType == StorageType.String ? "«" + (p.AsString() ?? string.Empty) + "»"
                : p.StorageType == StorageType.Double ? p.AsDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                : p.StorageType == StorageType.Integer ? p.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture) : "(ссылка)";
            return $"{text} ({p.StorageType}, {where})";
        }

        /// <summary>Значение без пояснения «(тип хранения, экз./тип)» — им Revit строки не различает.</summary>
        private static string Bare(string described)
        {
            int cut = described.LastIndexOf(" (", StringComparison.Ordinal);
            return cut > 0 && described.EndsWith(")", StringComparison.Ordinal) && !described.StartsWith("(", StringComparison.Ordinal) ? described.Substring(0, cut) : described;
        }

        /// <summary>Текст поля строки без обрезки пробелов — по параметру поля спецификации, если оно в сортировке.</summary>
        private static string Exact(Element element, string name, Dictionary<string, ElementId> ids) =>
            Value(element, name, ids.TryGetValue(name, out ElementId? id) ? id : ElementId.InvalidElementId) is ParamValue v ? CalcParams.Display(v) : string.Empty;

        private static string Text(Element element, string name) => Value(element, name) is ParamValue v ? CalcParams.Display(v).Trim() : string.Empty;

        /// <summary>ADSK_Количество позиции; пусто или 0 — одна штука (как в эталоне).</summary>
        private static double Quantity(FamilyInstance piece)
        {
            if (!(Value(piece, CalcParams.Quantity) is ParamValue v)) return 1;
            double n = v.Kind == ParamValueKind.Double ? v.Number : v.Kind == ParamValueKind.Integer ? v.Integer
                : double.TryParse(CalcParams.Display(v).Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double t) ? t : 0;
            return n > 0 ? n : 1;
        }

        /// <summary>«ЗАПАС Хомут соединительный Rapid DN100 · 3f2a1c»: читаемо в списке типоразмеров, хвост — от значений строки.</summary>
        private static string TypeName(string name, string rowKey)
        {
            var sb = new StringBuilder();
            foreach (char c in name) sb.Append(TypeNameBad.IndexOf(c) >= 0 || char.IsControl(c) ? ' ' : c);
            string clean = string.Join(" ", sb.ToString().Split(Space, StringSplitOptions.RemoveEmptyEntries));
            if (clean.Length > 60) clean = clean.Substring(0, 60).TrimEnd();
            // FNV-1a: стабильно между запусками и версиями .NET (string.GetHashCode — нет)
            uint hash = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(rowKey)) hash = (hash ^ b) * 16777619;
            return $"{TypePrefix} {clean} · {(hash & 0xFFFFFF).ToString("x6", System.Globalization.CultureInfo.InvariantCulture)}";
        }
    }
}
