using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Config;
using VKTools.Core.Schedules;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Schedules
{
    internal sealed class ScheduleResult
    {
        public long? ScheduleId { get; set; }

        public string? Error { get; set; }

        public List<string> Lines { get; } = new List<string>();

        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// Создание и обновление спецификации. Встроенная форма — новая спецификация нескольких категорий по schedule_form.csv;
    /// эталон — копия существующей (оформление переходит как есть). В обоих случаях плагин ставит только свои фильтры
    /// по полям системы и этажа (старые по этим полям снимает), остальные фильтры эталона не трогает.
    /// </summary>
    internal static class ScheduleBuilder
    {
        private const double MmPerFoot = 304.8;

        public static ScheduleResult Create(Document doc, ScheduleModelData data, ScheduleRequest request)
        {
            var result = new ScheduleResult();
            string name = ScheduleRequest.CleanName(request.Name);
            if (name.Length == 0)
            {
                result.Error = "не задано название";
                return result;
            }
            if (data.ViewNames.Contains(name))
            {
                result.Error = $"вид «{name}» уже есть в модели. Задайте другое название или обновите созданную спецификацию в списке ниже";
                return result;
            }
            ViewSchedule? template = null;
            int other = 0;
            if (!request.UsesForm)
            {
                template = new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>()
                    .FirstOrDefault(v => !v.IsTemplate && v.Name == request.Template);
                if (template == null)
                {
                    result.Error = $"эталона «{request.Template}» нет в модели";
                    return result;
                }
                other = ScheduleModel.OtherFilters(doc, template.Definition, data);
            }
            ScheduleFilterSet set = data.Plan(request, other, out List<string> systems, out List<string> floors);
            if (set.Error != null)
            {
                result.Error = set.Error;
                return result;
            }

            var collector = new FailureCollector();
            using (Transaction t = FailureCollector.Start(doc, "VK Tools: создать спецификацию", collector))
            {
                try
                {
                    ViewSchedule schedule;
                    if (template == null)
                    {
                        schedule = ViewSchedule.CreateSchedule(doc, ElementId.InvalidElementId);
                        schedule.Name = name;
                        ApplyForm(doc, schedule, data, request, result);
                    }
                    else
                    {
                        ElementId copyId = template.Duplicate(ViewDuplicateOption.Duplicate);
                        schedule = (ViewSchedule)doc.GetElement(copyId);
                        schedule.Name = name;
                        if (schedule.ViewTemplateId != ElementId.InvalidElementId)
                        {
                            string templateName = doc.GetElement(schedule.ViewTemplateId)?.Name ?? "?";
                            schedule.ViewTemplateId = ElementId.InvalidElementId;
                            result.Notes.Add($"копия отвязана от шаблона вида «{templateName}»: иначе шаблон не дал бы поставить фильтры (эталон не тронут)");
                        }
                        if (ParamProfile.IsOwn) RemapToOwn(doc, schedule, result);
                    }
                    if (!ApplyFilters(doc, schedule, data, request, set, result))
                    {
                        t.RollBack();
                        return result;
                    }
                    if (template == null || !request.KeepTemplateSort) ApplySorting(doc, schedule, data, request, template == null, result);
                    request.Name = name;
                    MarkPurpose(schedule, result);
                    Remember(request, data, set);
                    ScheduleStore.Write(schedule, request);
                    if (t.Commit() != TransactionStatus.Committed)
                    {
                        result.Error = "Revit не сохранил спецификацию: " + string.Join("; ", collector.Errors);
                        return result;
                    }
                    result.ScheduleId = schedule.Id.ToLong();
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    result.Error = "Revit отказал: " + ex.Message;
                    Host.Log.Error("Создать спецификацию: Revit отказал.", ex);
                    return result;
                }
            }
            result.Lines.Add($"Системы: {(set.Systems.All ? "все" : ScheduleRequest.JoinAnd(systems))}.");
            result.Lines.Add($"Этажи: {(set.Floors.All ? "все" : ScheduleRequest.JoinAnd(floors))}.");
            result.Lines.Add("Фильтры: " + string.Join("; ", set.Describe(data.SystemParam.Name, data.FloorParam.Name)) + ".");
            return result;
        }

        /// <summary>Пересобрать фильтры созданной спецификации по сохранённому выбору и текущей модели.</summary>
        public static ScheduleResult Update(Document doc, ScheduleModelData data, long scheduleId)
        {
            var result = new ScheduleResult { ScheduleId = scheduleId };
            if (!(doc.GetElement(scheduleId.ToElementId()) is ViewSchedule schedule))
            {
                result.Error = "спецификации уже нет в модели";
                return result;
            }
            ScheduleRequest? request = ScheduleStore.Read(schedule);
            if (request == null)
            {
                result.Error = "спецификация создана не кнопкой «Создать спецификацию»";
                return result;
            }
            int other = ScheduleModel.OtherFilters(doc, schedule.Definition, data);
            ScheduleFilterSet set = data.Plan(request, other, out _, out _);
            if (set.Error != null)
            {
                result.Error = set.Error;
                return result;
            }
            var collector = new FailureCollector();
            using (Transaction t = FailureCollector.Start(doc, "VK Tools: обновить фильтры спецификации", collector))
            {
                try
                {
                    if (schedule.ViewTemplateId != ElementId.InvalidElementId)
                    {
                        schedule.ViewTemplateId = ElementId.InvalidElementId;
                        result.Notes.Add("спецификация отвязана от шаблона вида");
                    }
                    if (!ApplyFilters(doc, schedule, data, request, set, result))
                    {
                        t.RollBack();
                        return result;
                    }
                    Remember(request, data, set);
                    ScheduleStore.Write(schedule, request);
                    if (t.Commit() != TransactionStatus.Committed)
                    {
                        result.Error = "Revit не сохранил изменения: " + string.Join("; ", collector.Errors);
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    if (t.HasStarted() && !t.HasEnded()) t.RollBack();
                    result.Error = "Revit отказал: " + ex.Message;
                    Host.Log.Error("Обновить фильтры спецификации: Revit отказал.", ex);
                }
            }
            result.Lines.Add("Фильтры: " + string.Join("; ", set.Describe(data.SystemParam.Name, data.FloorParam.Name)) + ".");
            return result;
        }

        private static void Remember(ScheduleRequest request, ScheduleModelData data, ScheduleFilterSet set)
        {
            request.KnownSystems = data.Systems.ToList();
            request.KnownFloors = data.KnownFloorValues;
            request.SystemParam = data.SystemParam.Id;
            request.FloorParam = data.FloorParam.Id;
            request.FilterSignature = string.Join("\n", set.Describe(data.SystemParam.Name, data.FloorParam.Name));
        }

        /// <summary>Графы формы: заголовок, ширина, выравнивание, итог. Неразвёрнутые строки, без общего итога.</summary>
        private static void ApplyForm(Document doc, ViewSchedule schedule, ScheduleModelData data, ScheduleRequest request, ScheduleResult result)
        {
            ScheduleDefinition definition = schedule.Definition;
            definition.IsItemized = false;
            definition.ShowGrandTotal = false;
            // Шапка граф есть в основной надписи («Девятиграфка_Шапка») — своя скрыта, иначе на листе две шапки
            definition.ShowHeaders = !request.HeaderInTitleBlock;
            // По ГОСТ 21.110 название — в основной надписи листа, не строкой над таблицей
            definition.ShowTitle = false;
            var missing = new List<string>();
            foreach (FormColumn column in data.Form)
            {
                ParamKey key = ParamKey.Shared(column.Parameter.Name, column.Parameter.Guid);
                ScheduleField? field = ScheduleFields.Ensure(doc, definition, key, column.Hidden);
                if (field == null)
                {
                    missing.Add(column.Parameter.Name);
                    continue;
                }
                field.ColumnHeading = column.Heading;
                field.GridColumnWidth = column.WidthMm / MmPerFoot;
                field.IsHidden = column.Hidden;
                field.HorizontalAlignment = column.Center ? ScheduleHorizontalAlignment.Center : ScheduleHorizontalAlignment.Left;
                MiddleVertically(field);
                if (column.Accuracy.HasValue && !RevitCompat.TrySetFieldAccuracy(doc, field, column.Accuracy.Value, out string? why))
                {
                    result.Notes.Add($"«{column.Heading}»: округление {column.Accuracy.Value.ToString(System.Globalization.CultureInfo.CurrentCulture)} не применилось ({why}) — формат как в проекте");
                }
                if (column.Total)
                {
                    if (field.CanTotal()) field.DisplayType = ScheduleFieldDisplayType.Totals;
                    else result.Notes.Add($"«{column.Heading}»: Revit не суммирует это поле («Вычислить итоги» недоступно)");
                }
            }
            if (missing.Count > 0)
            {
                result.Notes.Add("графы пропущены — параметров нет в проекте: " + string.Join(", ", missing)
                    + ". Добавьте их как параметры проекта (Управление → Параметры проекта, ФОП ADSK) и пересоздайте спецификацию");
            }
            ApplyTextStyle(doc, schedule, result);
            if (RevitCompat.TrySetBodyRowHeight(schedule, 8 / MmPerFoot))
            {
                result.Notes.Add("высота строк на листе — 8 мм");
            }
        }

        /// <summary>
        /// Свой набор «Р_» (0.14.0): в копии эталона КрМа3 графы ADSK_* заменяются на Р_* той же роли — место, заголовок, ширины,
        /// выравнивание, формат, стиль, итоги, скрытость, сортировка и фильтры переносятся на новую графу. Эталон не меняется.
        /// Вычисляемые графы (формулы с ADSK_*) Revit пересобрать не даёт — о них замечание.
        /// </summary>
        private static void RemapToOwn(Document doc, ViewSchedule schedule, ScheduleResult result)
        {
            ScheduleDefinition definition = schedule.Definition;
            var replaced = new List<string>();
            var failed = new List<string>();
            IList<ScheduleFieldId> order = definition.GetFieldOrder();
            for (int index = 0; index < order.Count; index++)
            {
                ScheduleField old = definition.GetField(order[index]);
                if (old.IsCalculatedField)
                {
                    if (old.GetName().IndexOf("ADSK_", StringComparison.Ordinal) >= 0) failed.Add($"«{old.ColumnHeading}» (вычисляемая)");
                    continue;
                }
                if (!(doc.GetElement(old.ParameterId) is SharedParameterElement shared)) continue;
                ParamRole? role = ParamProfile.RoleOf(shared.GuidValue);
                if (role == null) continue;
                OwnParamDef own = ParamProfile.Own(role.Value);
                if (shared.GuidValue == own.Guid) continue;
                SchedulableField? candidate = definition.GetSchedulableFields()
                    .Where(c => doc.GetElement(c.ParameterId) is SharedParameterElement sp && sp.GuidValue == own.Guid)
                    .OrderBy(c => c.FieldType == ScheduleFieldType.Instance ? 0 : 1).FirstOrDefault();
                if (candidate == null)
                {
                    failed.Add($"«{old.ColumnHeading}» ({own.Name} нет у категорий спецификации)");
                    continue;
                }
                try
                {
                    ScheduleField field = definition.InsertField(candidate, index);
                    field.ColumnHeading = old.ColumnHeading;
                    field.IsHidden = old.IsHidden;
                    field.HorizontalAlignment = old.HorizontalAlignment;
                    field.GridColumnWidth = old.GridColumnWidth;
                    field.SetStyle(old.GetStyle());
                    try
                    {
                        field.SetFormatOptions(old.GetFormatOptions());
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        // формат числа к тексту не подходит — как в проекте
                    }
                    if (old.DisplayType == ScheduleFieldDisplayType.Totals && field.CanTotal()) field.DisplayType = ScheduleFieldDisplayType.Totals;

                    ScheduleFieldId oldId = old.FieldId;
                    ScheduleFieldId newId = field.FieldId;
                    IList<ScheduleSortGroupField> sorts = definition.GetSortGroupFields();
                    var remapped = new List<ScheduleSortGroupField>();
                    foreach (ScheduleSortGroupField sg in sorts)
                    {
                        if (sg.FieldId != oldId)
                        {
                            remapped.Add(sg);
                            continue;
                        }
                        remapped.Add(new ScheduleSortGroupField(newId, sg.SortOrder)
                        {
                            ShowHeader = sg.ShowHeader,
                            ShowFooter = sg.ShowFooter,
                            ShowBlankLine = sg.ShowBlankLine,
                        });
                    }
                    definition.SetSortGroupFields(remapped);
                    IList<ScheduleFilter> filters = definition.GetFilters();
                    foreach (ScheduleFilter f in filters)
                    {
                        if (f.FieldId == oldId) f.FieldId = newId;
                    }
                    definition.SetFilters(filters);
                    definition.RemoveField(oldId);
                    replaced.Add($"{shared.Name} → {own.Name}");
                    order = definition.GetFieldOrder();
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    failed.Add($"«{old.ColumnHeading}» ({ex.Message})");
                }
            }
            if (replaced.Count > 0) result.Notes.Add("графы эталона на свои параметры: " + string.Join(", ", replaced));
            if (failed.Count > 0) result.Notes.Add("графы остались на ADSK_ — проверьте вручную: " + string.Join("; ", failed));
        }

        /// <summary>Текст ячеек графы — по центру строки по вертикали (в многострочном наименовании соседние графы не прилипают к верху).</summary>
        private static void MiddleVertically(ScheduleField field)
        {
            TableCellStyle style = field.GetStyle();
            TableCellStyleOverrideOptions overrides = style.GetCellStyleOverrideOptions();
            overrides.VerticalAlignment = true;
            style.SetCellStyleOverrideOptions(overrides);
            style.FontVerticalAlignment = VerticalAlignmentStyle.Middle;
            field.SetStyle(style);
        }

        /// <summary>Шрифт ГОСТ, если в проекте есть такой тип текста 3,5 мм; иначе шрифт Revit по умолчанию и строка в отчёте.</summary>
        private static void ApplyTextStyle(Document doc, ViewSchedule schedule, ScheduleResult result)
        {
            TextNoteType? best = null;
            foreach (TextNoteType type in new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>())
            {
                string font = type.get_Parameter(BuiltInParameter.TEXT_FONT)?.AsString() ?? string.Empty;
                double size = (type.get_Parameter(BuiltInParameter.TEXT_SIZE)?.AsDouble() ?? 0) * MmPerFoot;
                bool gost = font.IndexOf("GOST", StringComparison.OrdinalIgnoreCase) >= 0 || font.IndexOf("ISOCPEUR", StringComparison.OrdinalIgnoreCase) >= 0;
                if (gost && Math.Abs(size - 3.5) < 0.05)
                {
                    best = type;
                    break;
                }
            }
            if (best == null)
            {
                result.Notes.Add("типа текста со шрифтом GOST/ISOCPEUR 3,5 мм в проекте нет — шрифт Revit по умолчанию. Назначьте свой в «Свойства → Внешний вид»");
                return;
            }
            if (schedule.IsValidTextTypeId(best.Id))
            {
                schedule.BodyTextTypeId = best.Id;
                schedule.HeaderTextTypeId = best.Id;
                schedule.TitleTextTypeId = best.Id;
                result.Notes.Add($"шрифт — тип текста «{best.Name}»");
            }
        }

        /// <summary>
        /// Снять фильтры плагина (по полям системы и этажа) и поставить новые. Поля добавляются скрытыми, если их нет.
        /// false — поле не добавить или Revit не принимает условие: причина в <paramref name="result"/>.
        /// </summary>
        private static bool ApplyFilters(Document doc, ViewSchedule schedule, ScheduleModelData data, ScheduleRequest request, ScheduleFilterSet set, ScheduleResult result)
        {
            ScheduleDefinition definition = schedule.Definition;
            ScheduleField? system = ScheduleFields.Ensure(doc, definition, data.SystemParam, true);
            if (system == null)
            {
                result.Error = $"параметра «{data.SystemParam.Name}» нет среди полей спецификации: назначьте его категориям ВК (Управление → Параметры проекта)";
                return false;
            }
            ScheduleField? floor = null;
            if (set.Floors.Conditions.Count > 0 || request.GroupByFloor)
            {
                floor = ScheduleFields.Ensure(doc, definition, data.FloorParam, true);
                if (floor == null && set.Floors.Conditions.Count > 0)
                {
                    result.Error = $"параметра «{data.FloorParam.Name}» нет среди полей спецификации: назначьте его категориям ВК (Управление → Параметры проекта)";
                    return false;
                }
            }

            var keep = new List<ScheduleFilter>();
            var removed = new List<string>();
            foreach (ScheduleFilter filter in definition.GetFilters())
            {
                ScheduleField field = definition.GetField(filter.FieldId);
                if (data.IsReplaced(doc, field.ParameterId))
                {
                    if (!ScheduleFields.Is(doc, field.ParameterId, data.SystemParam) && !ScheduleFields.Is(doc, field.ParameterId, data.FloorParam))
                    {
                        removed.Add($"«{field.GetName()}» {FilterText(filter)}");
                    }
                    continue;
                }
                keep.Add(filter);
            }
            if (removed.Count > 0) result.Notes.Add("сняты фильтры эталона по системе: " + string.Join(", ", removed) + " — их заменяет выбор систем");
            var filters = new List<ScheduleFilter>(keep);
            foreach (FilterCondition c in set.SystemConditions)
            {
                ScheduleFilter? f = Make(definition, system.FieldId, c, data.SystemParam.Name, result);
                if (f == null) return false;
                filters.Add(f);
            }
            if (floor != null)
            {
                foreach (FilterCondition c in set.Floors.Conditions)
                {
                    ScheduleFilter? f = Make(definition, floor.FieldId, c, data.FloorParam.Name, result);
                    if (f == null) return false;
                    filters.Add(f);
                }
            }
            definition.SetFilters(filters);
            return true;
        }

        /// <summary>Параметр вида для папок диспетчера (ADSK): созданная спецификация без листа — «Оформленные спецификации».</summary>
        public const string PurposeParam = "ADSK_Назначение вида";

        public const string PurposeValue = "Оформленные спецификации";

        private static void MarkPurpose(ViewSchedule schedule, ScheduleResult result)
        {
            Parameter? p = schedule.LookupParameter(PurposeParam);
            if (p == null || p.StorageType != StorageType.String) return;
            if (p.IsReadOnly)
            {
                result.Notes.Add($"«{PurposeParam}» только для чтения (задаёт шаблон вида?) — не изменён");
                return;
            }
            if (p.AsString() == PurposeValue) return;
            p.Set(PurposeValue);
            result.Notes.Add($"«{PurposeParam}» = «{PurposeValue}»");
        }

        private static string FilterText(ScheduleFilter filter)
        {
            string value = filter.IsStringValue ? filter.GetStringValue() : filter.IsDoubleValue ? filter.GetDoubleValue().ToString(System.Globalization.CultureInfo.CurrentCulture)
                : filter.IsIntegerValue ? filter.GetIntegerValue().ToString(System.Globalization.CultureInfo.CurrentCulture) : string.Empty;
            return value.Length > 0 ? $"{filter.FilterType} «{value}»" : filter.FilterType.ToString();
        }

        private static ScheduleFilter? Make(ScheduleDefinition definition, ScheduleFieldId fieldId, FilterCondition c, string fieldName, ScheduleResult result)
        {
            bool can = c.Kind switch
            {
                ConditionKind.HasParameter => definition.CanFilterByParameterExistence(fieldId),
                ConditionKind.HasValue => definition.CanFilterByValuePresence(fieldId),
                ConditionKind.BeginsWith or ConditionKind.NotBeginsWith => definition.CanFilterBySubstring(fieldId),
                _ => definition.CanFilterByValue(fieldId),
            };
            if (!can)
            {
                result.Error = $"Revit не позволяет фильтр {c.Describe(fieldName)} (поле не текстовое?)";
                return null;
            }
            return c.Kind switch
            {
                ConditionKind.HasParameter => new ScheduleFilter(fieldId, ScheduleFilterType.HasParameter),
                ConditionKind.HasValue => new ScheduleFilter(fieldId, ScheduleFilterType.HasValue),
                ConditionKind.Equal => new ScheduleFilter(fieldId, ScheduleFilterType.Equal, c.Value),
                ConditionKind.NotEqual => new ScheduleFilter(fieldId, ScheduleFilterType.NotEqual, c.Value),
                ConditionKind.BeginsWith => new ScheduleFilter(fieldId, ScheduleFilterType.BeginsWith, c.Value),
                _ => new ScheduleFilter(fieldId, ScheduleFilterType.NotBeginsWith, c.Value),
            };
        }

        /// <summary>Заголовки по системам и этажам, затем сортировка формы (или оставшаяся сортировка эталона).</summary>
        private static void ApplySorting(Document doc, ViewSchedule schedule, ScheduleModelData data, ScheduleRequest request, bool form, ScheduleResult result)
        {
            ScheduleDefinition definition = schedule.Definition;
            var fields = new List<ScheduleSortGroupField>();
            foreach ((bool on, ParamKey key) in new[] { (request.GroupBySystem, data.SystemParam), (request.GroupByFloor, data.FloorParam) })
            {
                if (!on) continue;
                ScheduleField? field = ScheduleFields.Ensure(doc, definition, key, true);
                if (field == null)
                {
                    result.Notes.Add($"заголовков по «{key.Name}» не будет: поля нет в спецификации");
                    continue;
                }
                fields.Add(new ScheduleSortGroupField(field.FieldId, ScheduleSortOrder.Ascending) { ShowHeader = true });
            }
            if (form)
            {
                foreach (FormColumn column in data.Form.Where(c => c.Sort > 0).OrderBy(c => c.Sort))
                {
                    ScheduleField? field = ScheduleFields.Find(doc, definition, ParamKey.Shared(column.Parameter.Name, column.Parameter.Guid));
                    if (field != null && definition.CanSortByField(field.FieldId)) fields.Add(new ScheduleSortGroupField(field.FieldId, ScheduleSortOrder.Ascending));
                }
            }
            else
            {
                foreach (ScheduleSortGroupField old in definition.GetSortGroupFields())
                {
                    ScheduleField field = definition.GetField(old.FieldId);
                    if (ScheduleFields.Is(doc, field.ParameterId, data.SystemParam) || ScheduleFields.Is(doc, field.ParameterId, data.FloorParam)) continue;
                    fields.Add(old);
                }
            }
            definition.SetSortGroupFields(fields);
        }
    }
}
