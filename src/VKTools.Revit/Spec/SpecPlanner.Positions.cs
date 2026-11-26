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
            // Элементы нумеруемых спецификаций: общий элемент у двух получит номер последней — первая «поедет» (живая проверка 0.17.3)
            var numberedSets = issued
                .Where(v => PositionSettings.Match(rules, v.Name) is PositionRule r && r.Number)
                .ToDictionary(v => v.Name, v => new HashSet<long>(new FilteredElementCollector(_doc, v.Id).WhereElementIsNotElementType().ToElementIds().Select(id => id.ToLong())), StringComparer.Ordinal);
            for (int s = 0; s < issued.Count; s++)
            {
                ViewSchedule vs = issued[s];
                _progress.Report($"Позиции: «{vs.Name}» ({s + 1} из {issued.Count})", s, issued.Count);
                _progress.ThrowIfCancelled();
                if (ps.OnlySchedules != null && !ps.OnlySchedules.Contains(vs.Name, StringComparer.Ordinal)) continue;
                PositionRule? rule = PositionSettings.Match(rules, vs.Name);
                if (rule == null || !rule.Number)
                {
                    _plan.Preview.Add($"«{vs.Name}»: не трогается" + (rule != null ? $" (строка {rules.IndexOf(rule) + 1} таблицы, «{rule.Mask}»)" : " (нет строки в таблице)"));
                    continue;
                }
                if (SharedWithNumbered(vs.Name, numberedSets, skip: ps.OnlySchedules != null)) continue;
                if (NumberSchedule(vs, posKey, ps.SubNumbers, report)) numbered++;
            }
            _plan.Notes.Add($"Позиции: оформленных спецификаций {issued.Count}, нумеруется {numbered}; параметр {posKey.Name}; "
                + (ps.SubNumbers ? "вложенные через точку — сразу под родителем, «номер.N»" : "вложенные по порядку — где их ставит сортировка") + ".");
        }

        /// <summary>
        /// Общие элементы с другими нумеруемыми спецификациями. При нумерации одной спецификации («Создать», шаг «Позиции») — пропуск:
        /// иначе её номера перепишут номера той. В окне «Позиции» — предупреждение (номер останется от последней).
        /// </summary>
        /// <returns>true — спецификацию пропустить.</returns>
        private bool SharedWithNumbered(string name, Dictionary<string, HashSet<long>> numberedSets, bool skip)
        {
            if (!numberedSets.TryGetValue(name, out HashSet<long>? own)) return false;
            var shared = numberedSets.Where(kv => !string.Equals(kv.Key, name, StringComparison.Ordinal))
                .Select(kv => (Name: kv.Key, Count: kv.Value.Count(own.Contains)))
                .Where(x => x.Count > 0).ToList();
            if (shared.Count == 0) return false;
            string list = string.Join(", ", shared.Select(x => $"«{x.Name}» — {x.Count} эл."));
            PlanIssue issue = _plan.AddIssue(SpecModule.Positions, Severity.Warning, string.Empty,
                $"«{name}»: элементы есть и в других нумеруемых спецификациях: {list}" + (skip ? " — не нумеруется" : string.Empty), name);
            issue.Action = skip
                ? "Номера новой спецификации переписали бы номера той. Поставьте одной из них «не трогать» в окне «Позиции» или сузьте фильтры, затем «Позиции»."
                : "У общего элемента один параметр позиции — останется номер последней спецификации. Поставьте одной из них «не трогать» (галка «Нумеровать»).";
            if (skip) _plan.Preview.Add($"«{name}»: не нумеруется — элементы уже в нумеруемых: {list}");
            return skip;
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

            // Графа «Р_Ключ позиции»: строки и порядок собираются «как без VK Tools» — без ключа и со снятым ради него ключом
            ElementId? keyParamId = PositionKeys.ParamId(_doc);
            int keyField = PositionKeys.FieldIndex(definition, keyParamId);
            List<(int Index, bool Desc)> naturalSort = PositionKeys.NaturalSort(definition, keyField);
            // Блок строки — значения граф-шапок (обычно система), как на листе: пустая шапка — свой блок
            List<int> headerFields = PositionKeys.HeaderFields(definition, keyField);
            var rows = new List<PositionRow>();
            var rowOf = new Dictionary<long, int>();
            List<List<AuditElement>> groups;
            var savedSort = audit.Fields.Select(f => f.Sort).ToList();
            try
            {
                for (int i = 0; i < audit.Fields.Count; i++) audit.Fields[i].Sort = naturalSort.Any(k => k.Index == i);
                groups = ScheduleAudit.Rows(audit);
                for (int r = 0; r < groups.Count; r++)
                {
                    List<AuditElement> g = groups[r];
                    var row = new PositionRow(g.Select(e => e.Id).ToList(), PositionNumbering.Common(g.Select(e => ScheduleAudit.ValueOf(e, posField))))
                    {
                        Group = string.Join("\u001f", headerFields.Select(i => ScheduleAudit.ValueOf(g[0], i))),
                    };
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
            }
            finally
            {
                for (int i = 0; i < audit.Fields.Count; i++) audit.Fields[i].Sort = savedSort[i];
            }

            // Порядок строк как в таблице Revit (без ключа позиции)
            bool sorted = definition.GetSortGroupFields().Any(f => f.FieldId == field.FieldId);
            List<int>? order = sorted ? null : TableOrder(vs, definition, posField, keyField, groups, posKey);
            // Откат транзакции с изменённой сортировкой делает старый объект ScheduleDefinition недействительным
            definition = vs.Definition;
            string orderNote;
            if (order == null)
            {
                order = FallbackOrder(naturalSort, groups);
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
            var topCache = new Dictionary<long, int?>();
            int? ParentRowOf(long id)
            {
                if (topCache.TryGetValue(id, out int? cached)) return cached;
                Element? top = TopParent(id);
                int? result = top != null && rowOf.TryGetValue(top.Id.ToLong(), out int pr) && orderedIndex.TryGetValue(pr, out int pi) ? pi : (int?)null;
                topCache[id] = result;
                return result;
            }
            List<PositionUnit> units = PositionNumbering.Layout(ordered, ParentRowOf, subNumbers);

            // Ключ позиции: «через точку» — графа нужна (добавится при записи), «по порядку» — значения пишутся, только если графа уже есть
            bool keyInstalled = keyField >= 0 && PositionKeys.IsSorted(definition, keyField);
            bool writeKeys = subNumbers || keyField >= 0;
            ParamKey keyKey = ParamKey.Shared(ParamProfile.PositionKeyName, ParamProfile.PositionKeyGuid);
            string keyNote = string.Empty;
            if (writeKeys && keyParamId == null)
            {
                writeKeys = false;
                keyNote = $"; графа «{ParamProfile.PositionKeyName}» и параметр добавятся при «Применить» (в пробном прогоне без них)";
                _plan.AddIssue(M, Severity.Info, ParamProfile.PositionKeyName, $"{title}: параметра «{ParamProfile.PositionKeyName}» в проекте ещё нет — добавится при «Применить»", vs.Name);
            }
            else if (subNumbers && !keyInstalled)
            {
                _plan.PositionKeySchedules.Add(vs.Id.ToLong());
                keyNote = $"; при записи добавится скрытая графа «{ParamProfile.PositionKeyName}» (ключ сортировки после системы)";
            }

            int changed = 0;
            int rowsChanged = 0;
            int max = 0;
            for (int i = 0; i < units.Count; i++)
            {
                PositionUnit unit = units[i];
                string? number = unit.Number;
                if (number != null && unit.Parent == null) max = Math.Max(max, int.Parse(number, CultureInfo.InvariantCulture));
                long? parentId = unit.Parent is int pu ? units[pu].Ids[0] : (long?)null;
                string source = $"{title}, строка {i + 1}" + (unit.Parent is int pn ? $" (деталь строки {units[pn].Number})" : string.Empty);
                bool rowChanged = false;
                foreach (long id in unit.Ids)
                {
                    Element? e = _doc.GetElement(id.ToElementId());
                    if (e == null) continue;
                    string subject = $"{title}: {(e.Category?.Name ?? "?")} : {TypeLabel(e)}";
                    bool elementChanged = number != null && WritePositionText(e, posKey, number, subject, source, parentId, true);
                    if (writeKeys && WritePositionText(e, keyKey, unit.Key, subject, source, parentId, false)) elementChanged = true;
                    if (!elementChanged) continue;
                    changed++;
                    rowChanged = true;
                }
                if (rowChanged) rowsChanged++;
            }
            int subRows = units.Count(u => u.Parent != null);
            int skipped = units.Count(u => u.Skip);
            _plan.Preview.Add($"{title}: строк {units.Count}, номера 1…{max}" + (subRows > 0 ? $", деталей под родителем {subRows}" : string.Empty)
                + (skipped > 0 ? $", без номера («!», без наименования) {skipped}" : string.Empty) + $"; меняется строк {rowsChanged}, элементов {changed}; {orderNote}{keyNote}.");
            return true;
        }

        /// <summary>Записать текст в параметр элемента (позиция или ключ). true — значение меняется (изменение в плане).</summary>
        private bool WritePositionText(Element e, ParamKey key, string value, string subject, string source, long? parentId, bool countUnchanged)
        {
            const SpecModule M = SpecModule.Positions;
            long id = e.Id.ToLong();
            Parameter? p = _access.Find(e, key);
            if (p == null)
            {
                Element? type = TypeOf(e);
                bool typeParam = type != null && _access.Find(type, key) != null;
                _plan.AddIssue(M, Severity.Warning, key.Name, typeParam ? "параметр типа — значение у строки не задать (нужен экземпляра)" : "у элемента нет параметра", subject, id)
                    .Action = typeParam ? "Сделайте параметр параметром экземпляра (Управление → Параметры проекта)." : "Добавьте параметр в категорию (Управление → Параметры проекта).";
                return false;
            }
            if (p.StorageType != StorageType.String)
            {
                _plan.AddIssue(M, Severity.Error, key.Name, $"параметр не текстовый ({ParamAccess.DataTypeLabel(p)})", subject, id);
                return false;
            }
            string current = (p.AsString() ?? string.Empty).Trim();
            if (string.Equals(current, value, StringComparison.Ordinal))
            {
                if (countUnchanged) _plan.CountUnchanged(M);
                return false;
            }
            if (p.IsReadOnly)
            {
                _plan.AddIssue(M, Severity.Error, key.Name, "только для чтения: задан формулой или связан в семействе", subject, id);
                return false;
            }
            if (Guard(M, e, p, key.Name, subject)) return false;
            AddChange(M, e, key, current, ParamValue.OfText(value), source, parentId);
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
        private List<int>? TableOrder(ViewSchedule vs, ScheduleDefinition definition, int posField, int keyField, List<List<AuditElement>> groups, ParamKey posKey)
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
                        // Ключ позиции вне сортировки, снятый ключ на месте — таблица как без VK Tools
                        PositionKeys.Detach(definition, keyField);
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
        private static List<int> FallbackOrder(List<(int Index, bool Desc)> keys, List<List<AuditElement>> groups)
        {
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
