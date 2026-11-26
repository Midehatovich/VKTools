using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Progress;
using VKTools.Core.Schedules;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Schedules;

namespace VKTools.Revit.Spec
{
    /// <summary>
    /// «Позиции» (0.17.1): в каждой оформленной спецификации свои номера с 1 по порядку строк (решение 2026-11-24).
    /// Строки собираются как в «Проверить спецификацию» (по полям сортировки), порядок строк берётся из самой таблицы:
    /// в откатываемой транзакции каждой строке пишется метка «§k», Revit перестраивает таблицу, метки читаются сверху вниз.
    /// Не вышло (документ только для чтения, позиция в сортировке) — порядок по полям сортировки (замечание в отчёте).
    /// </summary>
    internal sealed partial class SpecPlanner
    {
        private const string OrderMark = "§";

        private void PlanPositions(SpecOptions options, SpecDefaults defaults)
        {
            const SpecModule M = SpecModule.Positions;
            PositionSettings ps = options.Positions;
            if (options.IsDefault(defaults))
            {
                _plan.RuleSources.Add("Позиции: по умолчанию (все оформленные спецификации с 1)");
            }
            else
            {
                _plan.RuleSources.Add("Позиции: настройка проекта (" + (!options.SavedInProject ? "изменена в окне, в модели не сохранена" : options.SavedStamp ?? "сохранена в модели") + ")");
            }
            if (defaults.ProjectBlocked != null) Block(defaults.ProjectBlocked);

            var tableIssues = new List<ConfigIssue>();
            List<PositionRule> rules = PositionSettings.ParseRules(ps.Rules, tableIssues);
            foreach (ConfigIssue issue in tableIssues) AddConfigIssue(M, issue);
            if (tableIssues.Any(i => i.Severity == Severity.Error)) Block("в таблице «Спецификации» ошибки.");

            ParamKey ownKey = ParamProfile.TargetKey(ParamRole.Position);
            ParamKey adskKey = ParamKey.Shared(ParamProfile.Adsk(ParamRole.Position).Name, ParamProfile.Adsk(ParamRole.Position).Guid);
            foreach (ParamKey k in new[] { ownKey, adskKey }.Concat(ps.PosParam != null ? new[] { ps.PosParam } : Array.Empty<ParamKey>()))
            {
                if (!_plan.Candidates.Any(c => c.Key.Equals(k))) _plan.Candidates.Add(new ParamCandidate(k, "Текст") { Nested = 1 });
            }
            if (ps.PosParam == null)
            {
                _plan.AddIssue(M, Severity.Error, string.Empty, "не выбран параметр позиции", string.Empty)
                    .Action = "Выберите параметр на вкладке «Настройка» (обычно Р_Позиция или ADSK_Позиция).";
                Block("не выбран параметр позиции.");
                return;
            }
            ParamKey posKey = ps.PosParam;

            _progress.Report("Позиции: ищу оформленные спецификации…");
            List<ViewSchedule> issued = ScheduleAuditRunner.Issued(_doc);
            if (issued.Count == 0)
            {
                _plan.AddIssue(M, Severity.Warning, string.Empty, "в модели нет оформленных спецификаций", string.Empty)
                    .Action = "Нумеруются спецификации с «ADSK_Назначение вида» = «Оформленные спецификации» или созданные кнопкой «Создать спецификацию».";
                return;
            }
            var report = new CheckReport("Позиции", _doc.Title, DateTime.Now);
            int numbered = 0;
            for (int s = 0; s < issued.Count; s++)
            {
                ViewSchedule vs = issued[s];
                _progress.Report($"Позиции: «{vs.Name}» ({s + 1} из {issued.Count})", s, issued.Count);
                _progress.ThrowIfCancelled();
                PositionRule? rule = PositionSettings.Match(rules, vs.Name);
                if (rule == null || !rule.Number)
                {
                    _plan.Preview.Add($"«{vs.Name}»: не трогается" + (rule != null ? $" (строка {rules.IndexOf(rule) + 1} таблицы, «{rule.Mask}»)" : " (нет строки в таблице)"));
                    continue;
                }
                if (NumberSchedule(vs, posKey, ps.SubNumbers, report)) numbered++;
            }
            _plan.Notes.Add($"Позиции: оформленных спецификаций {issued.Count}, нумеруется {numbered}; параметр {posKey.Name}; "
                + (ps.SubNumbers ? "вложенные детали одной строки-родителя — «номер.N»" : "все строки подряд") + ".");
        }

        /// <returns>true — спецификация пронумерована (изменения добавлены в план).</returns>
        private bool NumberSchedule(ViewSchedule vs, ParamKey posKey, bool subNumbers, CheckReport report)
        {
            const SpecModule M = SpecModule.Positions;
            string title = $"«{vs.Name}»";
            AuditSchedule audit = ScheduleAuditRunner.ReadSchedule(_doc, vs, report);
            int posField = ScheduleAudit.FieldOf(audit, AuditRole.Position);
            int nameField = ScheduleAudit.FieldOf(audit, AuditRole.Name);
            if (posField < 0)
            {
                _plan.AddIssue(M, Severity.Warning, string.Empty, $"{title}: нет графы позиции — не нумеруется", vs.Name)
                    .Action = "Добавьте в спецификацию поле позиции (Р_Позиция или ADSK_Позиция) или поставьте «не трогать» в таблице.";
                _plan.Preview.Add($"{title}: нет графы позиции");
                return false;
            }
            ScheduleDefinition definition = vs.Definition;
            ScheduleField field = definition.GetField(posField);
            ParamKey? fieldKey = FieldKey(audit, field.ParameterId);
            if (fieldKey != null && !fieldKey.Equals(posKey))
            {
                _plan.AddIssue(M, Severity.Warning, posKey.Name, $"{title}: графа «Позиция» показывает {fieldKey.Name}, а пишется {posKey.Name} — не нумеруется", vs.Name)
                    .Action = "Выберите на вкладке «Настройка» тот же параметр, что в графе спецификации, или замените поле в спецификации.";
                _plan.Preview.Add($"{title}: графа позиции — {fieldKey.Name}, не {posKey.Name}");
                return false;
            }
            if (audit.Itemized)
            {
                _plan.AddIssue(M, Severity.Info, string.Empty, $"{title}: «Для каждого экземпляра» — каждый элемент получит свой номер", vs.Name);
            }

            List<List<AuditElement>> groups = ScheduleAudit.Rows(audit);
            var rows = new List<PositionRow>(groups.Count);
            var rowOf = new Dictionary<long, int>();
            for (int r = 0; r < groups.Count; r++)
            {
                List<AuditElement> g = groups[r];
                var row = new PositionRow(g.Select(e => e.Id).ToList(), PositionNumbering.Common(g.Select(e => ScheduleAudit.ValueOf(e, posField))));
                string name = nameField >= 0 ? PositionNumbering.Common(g.Select(e => ScheduleAudit.ValueOf(e, nameField))) : string.Empty;
                if (ScheduleAudit.IsExcludedName(name)) row.Skip = true;
                else if (nameField >= 0 && name.Length == 0)
                {
                    // Пустое наименование (трубы без «Имён труб» и т. п.): номер не даём, чтобы не было номеров у пустых строк на листе
                    row.Skip = true;
                    AuditElement first = g[0];
                    PlanIssue unnamed = _plan.AddIssue(M, Severity.Warning, posKey.Name, $"{title}: строка без наименования — номер не дан", $"{first.Family}: {first.Type}", first.Id, g.Count);
                    unnamed.Action = "Заполните наименование («Имена труб», «Изоляция» или семейство) и запустите «Позиции» снова.";
                    unnamed.Ids.AddRange(g.Skip(1).Select(e => e.Id));
                }
                rows.Add(row);
                foreach (AuditElement e in g) rowOf[e.Id] = r;
            }

            // Порядок строк как в таблице Revit
            bool sorted = definition.GetSortGroupFields().Any(f => f.FieldId == field.FieldId);
            List<int>? order = sorted ? null : TableOrder(vs, definition, posField, groups, posKey);
            string orderNote;
            if (order == null)
            {
                order = FallbackOrder(definition, groups);
                orderNote = sorted ? "позиция входит в сортировку — порядок по полям сортировки" : "таблицу прочитать не удалось — порядок по полям сортировки";
                _plan.AddIssue(M, Severity.Warning, string.Empty, $"{title}: {orderNote}", vs.Name)
                    .Action = "Сверьте номера с таблицей после пробного прогона. Если позиция в сортировке — уберите её оттуда (номер должен идти по порядку строк).";
            }
            else
            {
                orderNote = "порядок строк — по таблице Revit";
            }
            var ordered = order.Select(i => rows[i]).ToList();
            var orderedIndex = new Dictionary<int, int>();
            for (int i = 0; i < order.Count; i++) orderedIndex[order[i]] = i;

            // Вложенные детали: все элементы строки — вложенные, и их верхние родители в одной строке этой спецификации
            for (int i = 0; i < order.Count; i++)
            {
                int? parentRow = null;
                bool ok = true;
                foreach (long id in ordered[i].Ids)
                {
                    Element? top = TopParent(id);
                    if (top == null || !rowOf.TryGetValue(top.Id.ToLong(), out int pr))
                    {
                        ok = false;
                        break;
                    }
                    if (parentRow == null) parentRow = pr;
                    else if (parentRow != pr)
                    {
                        ok = false;
                        break;
                    }
                }
                // Родитель — только в том же блоке системы: муфта под датчик в В21.2 не получает «1.1» от оросителя из другого блока
                if (ok && parentRow.HasValue && orderedIndex.TryGetValue(parentRow.Value, out int pIndex) && pIndex != i
                    && ScheduleAudit.GroupOf(audit, groups[order[i]][0]) == ScheduleAudit.GroupOf(audit, groups[parentRow.Value][0]))
                    ordered[i].Parent = pIndex;
            }

            string?[] numbers = PositionNumbering.Number(ordered, subNumbers);
            int changed = 0;
            int rowsChanged = 0;
            int subRows = 0;
            int max = 0;
            for (int i = 0; i < ordered.Count; i++)
            {
                string? number = numbers[i];
                if (number == null) continue;
                if (number.IndexOf('.') >= 0) subRows++;
                else max = Math.Max(max, int.Parse(number, CultureInfo.InvariantCulture));
                bool rowChanged = false;
                foreach (long id in ordered[i].Ids)
                {
                    Element? e = _doc.GetElement(id.ToElementId());
                    if (e == null) continue;
                    string subject = $"{title}: {(e.Category?.Name ?? "?")} : {TypeLabel(e)}";
                    Parameter? p = _access.Find(e, posKey);
                    if (p == null)
                    {
                        Element? type = TypeOf(e);
                        bool typeParam = type != null && _access.Find(type, posKey) != null;
                        _plan.AddIssue(M, Severity.Warning, posKey.Name, typeParam ? "параметр типа — номер у строки не задать (нужен экземпляра)" : "у элемента нет параметра", subject, id)
                            .Action = typeParam ? "Сделайте позицию параметром экземпляра (Управление → Параметры проекта)." : "Добавьте параметр в категорию (Управление → Параметры проекта).";
                        continue;
                    }
                    if (p.StorageType != StorageType.String)
                    {
                        _plan.AddIssue(M, Severity.Error, posKey.Name, $"параметр не текстовый ({ParamAccess.DataTypeLabel(p)})", subject, id);
                        continue;
                    }
                    string current = (p.AsString() ?? string.Empty).Trim();
                    if (string.Equals(current, number, StringComparison.Ordinal))
                    {
                        _plan.CountUnchanged(M);
                        continue;
                    }
                    if (p.IsReadOnly)
                    {
                        _plan.AddIssue(M, Severity.Error, posKey.Name, "только для чтения: задан формулой или связан в семействе", subject, id);
                        continue;
                    }
                    if (Guard(M, e, p, posKey.Name, subject)) continue;
                    long? parentId = ordered[i].Parent is int pi ? ordered[pi].Ids[0] : (long?)null;
                    AddChange(M, e, posKey, current, ParamValue.OfText(number), $"{title}, строка {i + 1}" + (parentId.HasValue ? $" (деталь строки {numbers[ordered[i].Parent!.Value]})" : string.Empty), parentId);
                    changed++;
                    rowChanged = true;
                }
                if (rowChanged) rowsChanged++;
            }
            int skipped = ordered.Count(r => r.Skip);
            _plan.Preview.Add($"{title}: строк {ordered.Count}, номера 1…{max}" + (subRows > 0 ? $", подномеров {subRows}" : string.Empty)
                + (skipped > 0 ? $", «!» без номера {skipped}" : string.Empty) + $"; меняется строк {rowsChanged}, элементов {changed}; {orderNote}.");
            return true;
        }

        /// <summary>Ключ параметра графы по любому элементу спецификации, у которого он есть.</summary>
        private ParamKey? FieldKey(AuditSchedule audit, ElementId parameterId)
        {
            foreach (AuditElement a in audit.Elements.Take(50))
            {
                Element? e = _doc.GetElement(a.Id.ToElementId());
                if (e == null) continue;
                foreach (Element? holder in new[] { e, TypeOf(e) })
                {
                    if (holder == null) continue;
                    foreach (Parameter p in holder.Parameters)
                    {
                        if (p.Id == parameterId) return _access.KeyOf(p);
                    }
                }
            }
            return null;
        }

        private Element? TopParent(long id)
        {
            if (!(_doc.GetElement(id.ToElementId()) is FamilyInstance fi) || fi.SuperComponent == null) return null;
            Element top = fi.SuperComponent;
            for (int depth = 0; depth < MaxNestedLevel && top is FamilyInstance parent && parent.SuperComponent != null; depth++) top = parent.SuperComponent;
            return top;
        }

        /// <summary>
        /// Порядок строк из таблицы: метка «§k» в позицию всех элементов строки k, перестроение, чтение графы сверху вниз, откат.
        /// null — не получилось (тогда порядок по полям сортировки).
        /// </summary>
        private List<int>? TableOrder(ViewSchedule vs, ScheduleDefinition definition, int posField, List<List<AuditElement>> groups, ParamKey posKey)
        {
            if (_doc.IsReadOnly || groups.Count == 0) return null;
            int column = 0;
            for (int i = 0; i < posField; i++)
            {
                if (!definition.GetField(i).IsHidden) column++;
            }
            if (definition.GetField(posField).IsHidden) return null;
            var order = new List<int>();
            try
            {
                using (var t = new Transaction(_doc, "VK Tools: порядок строк (откат)"))
                {
                    if (_doc.IsModifiable) return null;
                    t.Start();
                    try
                    {
                        for (int r = 0; r < groups.Count; r++)
                        {
                            string mark = OrderMark + r.ToString(CultureInfo.InvariantCulture);
                            foreach (AuditElement a in groups[r])
                            {
                                Element? e = _doc.GetElement(a.Id.ToElementId());
                                Parameter? p = e == null ? null : _access.Find(e, posKey);
                                if (p != null && !p.IsReadOnly && p.StorageType == StorageType.String) p.Set(mark);
                            }
                        }
                        _doc.Regenerate();
                        TableSectionData body = vs.GetTableData().GetSectionData(SectionType.Body);
                        var seen = new HashSet<int>();
                        for (int row = body.FirstRowNumber; row <= body.LastRowNumber; row++)
                        {
                            string text = (vs.GetCellText(SectionType.Body, row, body.FirstColumnNumber + column) ?? string.Empty).Trim();
                            if (MarkIndex(text) is int k && k < groups.Count && seen.Add(k)) order.Add(k);
                        }
                    }
                    finally
                    {
                        t.RollBack();
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                Host.Log.Warn($"Позиции: порядок строк «{vs.Name}» не прочитан — {ex.Message}");
                return null;
            }
            if (order.Count != groups.Count)
            {
                Host.Log.Warn($"Позиции: «{vs.Name}» — в таблице найдено {order.Count} из {groups.Count} строк, порядок по полям сортировки.");
                return null;
            }
            return order;
        }

        /// <summary>«§12» → 12; иначе null.</summary>
        private static int? MarkIndex(string text)
        {
            if (!text.StartsWith(OrderMark, StringComparison.Ordinal) || text.Length == OrderMark.Length) return null;
            long n = 0;
            for (int i = OrderMark.Length; i < text.Length; i++)
            {
                char c = text[i];
                if (c < '0' || c > '9') return null;
                n = n * 10 + (c - '0');
                if (n > int.MaxValue) return null;
            }
            return (int)n;
        }

        /// <summary>Порядок по полям сортировки спецификации (по возрастанию/убыванию), «2» раньше «10».</summary>
        private static List<int> FallbackOrder(ScheduleDefinition definition, List<List<AuditElement>> groups)
        {
            var fieldIndex = new Dictionary<ScheduleFieldId, int>();
            for (int i = 0; i < definition.GetFieldCount(); i++) fieldIndex[definition.GetField(i).FieldId] = i;
            var keys = definition.GetSortGroupFields()
                .Where(f => fieldIndex.ContainsKey(f.FieldId))
                .Select(f => (Index: fieldIndex[f.FieldId], Desc: f.SortOrder == ScheduleSortOrder.Descending))
                .ToList();
            var idx = Enumerable.Range(0, groups.Count).ToList();
            idx.Sort((a, b) =>
            {
                foreach ((int index, bool desc) in keys)
                {
                    int c = ScheduleAudit.Natural.Compare(ScheduleAudit.ValueOf(groups[a][0], index), ScheduleAudit.ValueOf(groups[b][0], index));
                    if (c != 0) return desc ? -c : c;
                }
                return a.CompareTo(b);
            });
            return idx;
        }
    }
}
