using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Schedules;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Spec;

namespace VKTools.Revit.Schedules
{
    /// <summary>
    /// «Проверить спецификацию» (0.17.0): читает графы и элементы спецификаций и отдаёт их <see cref="ScheduleAudit"/>.
    /// Только чтение: ни одной транзакции.
    /// </summary>
    internal static class ScheduleAuditRunner
    {
        private const string CountInSpecName = "Учитывать в спецификации";

        /// <summary>Спецификации для проверки «все»: оформленные (ADSK_Назначение вида) или созданные кнопкой, многокатегорийные.</summary>
        public static List<ViewSchedule> Issued(Document doc)
        {
            var list = new List<ViewSchedule>();
            foreach (ViewSchedule vs in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
            {
                if (!Checkable(vs)) continue;
                bool own = ScheduleStore.Read(vs) != null;
                Parameter? purpose = vs.LookupParameter(ScheduleBuilder.PurposeParam);
                bool issued = purpose != null && purpose.StorageType == StorageType.String
                    && string.Equals((purpose.AsString() ?? string.Empty).Trim(), ScheduleBuilder.PurposeValue, StringComparison.OrdinalIgnoreCase);
                if (own || issued) list.Add(vs);
            }
            list.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
            return list;
        }

        public static bool Checkable(ViewSchedule? vs)
        {
            if (vs == null || vs.IsTemplate || vs.IsTitleblockRevisionSchedule || vs.IsInternalKeynoteSchedule) return false;
            try
            {
                ScheduleDefinition d = vs.Definition;
                return !d.IsKeySchedule && !d.IsMaterialTakeoff;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }

        /// <param name="checkOutside">Искать элементы ВК вне всех проверенных спецификаций (имеет смысл, когда проверяются все).</param>
        public static CheckReport Run(Document doc, IReadOnlyList<ViewSchedule> schedules, bool checkOutside)
        {
            var report = new CheckReport("Проверка спецификаций", doc.Title, DateTime.Now)
            {
                PluginVersion = BuildInfo.PluginVersion,
                RevitVersion = BuildInfo.RevitVersion,
            };
            (SpecOptions options, _) = SpecSettingsLoader.Load(doc, SpecModule.SystemFloor);
            SystemFloorSettings sf = options.SystemFloor;
            var access = new ParamAccess(doc);
            ParamKey? systemKey = sf.SystemParam;

            var audits = new List<AuditSchedule>();
            var seen = new HashSet<long>();
            foreach (ViewSchedule vs in schedules)
            {
                AuditSchedule audit = Read(doc, vs, access, systemKey, report);
                audits.Add(audit);
                foreach (AuditElement e in audit.Elements) seen.Add(e.Id);
                report.RuleSources.Add($"«{vs.Name}»: {audit.Elements.Count} эл., граф {audit.Fields.Count}" + (audit.Itemized ? ", для каждого экземпляра" : string.Empty));
            }

            List<AuditElement>? outside = null;
            if (checkOutside)
            {
                outside = new List<AuditElement>();
                var categories = new List<BuiltInCategory>();
                foreach (string name in sf.Categories)
                {
                    if (Enum.TryParse(name, out BuiltInCategory bic) && !categories.Contains(bic)) categories.Add(bic);
                }
                if (categories.Count > 0)
                {
                    foreach (Element e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categories)).WhereElementIsNotElementType())
                    {
                        if (seen.Contains(e.Id.ToLong()) || e.Category == null || Excluded(e)) continue;
                        AuditElement a = Describe(e, Array.Empty<string>(), access, systemKey);
                        outside.Add(a);
                    }
                }
                report.RuleSources.Add($"Вне спецификаций искались категории «Системы и этажа»: {categories.Count} шт.");
            }
            else
            {
                report.RuleSources.Add("Вне спецификаций не искалось: проверялась одна спецификация.");
            }

            report.Findings.AddRange(ScheduleAudit.Run(audits, outside));
            return report;
        }

        /// <summary>Графы и элементы одной спецификации (для «Позиций»): графа i = поле i определения.</summary>
        public static AuditSchedule ReadSchedule(Document doc, ViewSchedule vs, CheckReport report)
        {
            (SpecOptions options, _) = SpecSettingsLoader.Load(doc, SpecModule.SystemFloor);
            return Read(doc, vs, new ParamAccess(doc), options.SystemFloor.SystemParam, report);
        }

        /// <summary>«ADSK_Учитывать в спецификации» = 0 (Нет): элемент нарочно не в спецификации.</summary>
        private static bool Excluded(Element e)
        {
            foreach (Element? holder in new[] { e, e.Document.GetElement(e.GetTypeId()) })
            {
                if (holder == null) continue;
                foreach (Parameter p in holder.Parameters)
                {
                    if (p.StorageType != StorageType.Integer || !p.HasValue) continue;
                    if (p.Definition?.Name?.EndsWith(CountInSpecName, StringComparison.CurrentCultureIgnoreCase) == true) return p.AsInteger() == 0;
                }
            }
            return false;
        }

        private sealed class FieldReader
        {
            public FieldReader(AuditField field) => Field = field;

            public AuditField Field { get; }

            /// <summary>Параметры поля (у комбинированного — несколько) с префиксами и разделителями.</summary>
            public List<(ElementId Id, string Prefix, string Suffix, string Separator)> Parts { get; } = new List<(ElementId, string, string, string)>();

            public bool Readable => Parts.Count > 0;
        }

        private static AuditSchedule Read(Document doc, ViewSchedule vs, ParamAccess access, ParamKey? systemKey, CheckReport report)
        {
            ScheduleDefinition definition = vs.Definition;
            var sortIds = new HashSet<int>(definition.GetSortGroupFields().Select(s => s.FieldId.IntegerValue));
            var readers = new List<FieldReader>();
            for (int i = 0; i < definition.GetFieldCount(); i++)
            {
                ScheduleField field = definition.GetField(i);
                string paramName = field.GetName();
                string heading = (field.ColumnHeading ?? string.Empty).Replace('\n', ' ').Trim();
                var reader = new FieldReader(new AuditField(heading.Length > 0 ? heading : paramName, ScheduleAudit.RoleOf(paramName))
                {
                    Hidden = field.IsHidden,
                    Sort = sortIds.Contains(field.FieldId.IntegerValue),
                    Totals = field.DisplayType == ScheduleFieldDisplayType.Totals,
                    Accuracy = Accuracy(doc, field),
                    Parameter = field.FieldType == ScheduleFieldType.Instance || field.FieldType == ScheduleFieldType.ElementType ? NameOf(doc, field.ParameterId) : string.Empty,
                });
#if REVIT2022_OR_GREATER
                if (field.IsCombinedParameterField)
                {
                    AuditRole role = AuditRole.Other;
                    foreach (TableCellCombinedParameterData part in field.GetCombinedParameters())
                    {
                        reader.Parts.Add((part.ParamId, part.Prefix ?? string.Empty, part.Suffix ?? string.Empty, part.Separator ?? string.Empty));
                        if (role == AuditRole.Other) role = ScheduleAudit.RoleOf(NameOf(doc, part.ParamId));
                        if (reader.Field.Parameter.Length == 0) reader.Field.Parameter = NameOf(doc, part.ParamId);
                    }
                    reader = Retarget(reader, role);
                    readers.Add(reader);
                    continue;
                }
#endif
                if (field.FieldType == ScheduleFieldType.Instance || field.FieldType == ScheduleFieldType.ElementType
                    || field.FieldType == ScheduleFieldType.Space || field.FieldType == ScheduleFieldType.Room)
                {
                    reader.Parts.Add((field.ParameterId, string.Empty, string.Empty, string.Empty));
                }
                readers.Add(reader);
            }
            var audit = new AuditSchedule(vs.Id.ToLong(), vs.Name, readers.Select(r => r.Field).ToList()) { Itemized = definition.IsItemized };
            int unreadable = readers.Count(r => !r.Readable && !r.Field.Hidden);
            if (unreadable > 0)
            {
                report.RuleSources.Add($"«{vs.Name}»: графы без параметра (формула, счёт) не проверяются: "
                    + string.Join(", ", readers.Where(r => !r.Readable && !r.Field.Hidden).Select(r => "«" + r.Field.Name + "»")));
            }

            foreach (Element e in new FilteredElementCollector(doc, vs.Id).WhereElementIsNotElementType())
            {
                if (e.Category == null) continue;
                var values = new string[readers.Count];
                var numbers = new double[readers.Count];
                for (int i = 0; i < readers.Count; i++)
                {
                    (values[i], numbers[i]) = ReadField(e, readers[i]);
                }
                AuditElement a = Describe(e, values, access, systemKey);
                a.Numbers = numbers;
                audit.Elements.Add(a);
            }
            audit.TablePositions = TablePositions(vs, definition, audit);
            return audit;
        }

        /// <summary>Графа позиции сверху вниз из самой таблицы (строки «!…» пропускаются); null — графы нет или скрыта.</summary>
        private static List<string>? TablePositions(ViewSchedule vs, ScheduleDefinition definition, AuditSchedule audit)
        {
            int pos = ScheduleAudit.FieldOf(audit, AuditRole.Position);
            int name = ScheduleAudit.FieldOf(audit, AuditRole.Name);
            if (pos < 0 || definition.GetField(pos).IsHidden) return null;
            int Column(int field)
            {
                int c = 0;
                for (int i = 0; i < field; i++)
                {
                    if (!definition.GetField(i).IsHidden) c++;
                }
                return c;
            }
            try
            {
                TableSectionData body = vs.GetTableData().GetSectionData(SectionType.Body);
                int posColumn = body.FirstColumnNumber + Column(pos);
                int nameColumn = name >= 0 && !definition.GetField(name).IsHidden ? body.FirstColumnNumber + Column(name) : -1;
                var result = new List<string>();
                for (int row = body.FirstRowNumber; row <= body.LastRowNumber; row++)
                {
                    if (nameColumn >= 0 && ScheduleAudit.IsExcludedName((vs.GetCellText(SectionType.Body, row, nameColumn) ?? string.Empty).Trim())) continue;
                    result.Add(vs.GetCellText(SectionType.Body, row, posColumn) ?? string.Empty);
                }
                return result;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                Host.Log.Warn($"Проверка спецификации: таблица «{vs.Name}» не прочитана — {ex.Message}");
                return null;
            }
        }

        private static FieldReader Retarget(FieldReader reader, AuditRole role)
        {
            if (role == reader.Field.Role) return reader;
            AuditField f = reader.Field;
            var copy = new FieldReader(new AuditField(f.Name, role) { Hidden = f.Hidden, Sort = f.Sort, Totals = f.Totals, Accuracy = f.Accuracy, Parameter = f.Parameter });
            copy.Parts.AddRange(reader.Parts);
            return copy;
        }

        private static string NameOf(Document doc, ElementId id)
        {
            if (id.ToLong() < 0)
            {
                try
                {
                    return LabelUtils.GetLabelFor((BuiltInParameter)(int)id.ToLong());
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    return string.Empty;
                }
            }
            return doc.GetElement(id)?.Name ?? string.Empty;
        }

        private static double Accuracy(Document doc, ScheduleField field)
        {
            try
            {
                FormatOptions options = field.GetFormatOptions();
                if (!options.UseDefault) return options.Accuracy;
                return doc.GetUnits().GetFormatOptions(field.GetSpecTypeId()).Accuracy;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return 0;
            }
            catch (ArgumentException)
            {
                return 0;
            }
        }

        private static (string Text, double Number) ReadField(Element e, FieldReader reader)
        {
            if (!reader.Readable) return (string.Empty, double.NaN);
            if (reader.Parts.Count == 1 && reader.Parts[0].Prefix.Length == 0 && reader.Parts[0].Suffix.Length == 0)
            {
                Parameter? p = Find(e, reader.Parts[0].Id);
                return p == null ? (string.Empty, double.NaN) : (Text(p), Number(p));
            }
            // Комбинированная графа: Revit пропускает пустые части вместе с их префиксом и суффиксом
            var parts = new List<string>();
            string separator = string.Empty;
            foreach ((ElementId id, string prefix, string suffix, string sep) in reader.Parts)
            {
                Parameter? p = Find(e, id);
                string text = p == null ? string.Empty : Text(p);
                if (text.Length > 0) parts.Add(prefix + text + suffix);
                separator = sep;
            }
            return (string.Join(separator, parts), double.NaN);
        }

        /// <summary>Параметр поля: у экземпляра, иначе у типа; общий — по GUID, встроенный — по BuiltInParameter.</summary>
        private static Parameter? Find(Element e, ElementId id)
        {
            Parameter? p = FindOn(e, id);
            if (p != null && (p.HasValue || e.GetTypeId() == ElementId.InvalidElementId)) return p;
            Parameter? typeParam = FindOn(e.Document.GetElement(e.GetTypeId()), id);
            return typeParam ?? p;
        }

        private static Parameter? FindOn(Element? element, ElementId id)
        {
            if (element == null || id == ElementId.InvalidElementId) return null;
            long raw = id.ToLong();
            if (raw < 0)
            {
                try
                {
                    return element.get_Parameter((BuiltInParameter)(int)raw);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    return null;
                }
            }
            if (element.Document.GetElement(id) is SharedParameterElement shared) return element.get_Parameter(shared.GuidValue);
            foreach (Parameter p in element.Parameters)
            {
                if (p.Id == id) return p;
            }
            return null;
        }

        private static string Text(Parameter p)
        {
            if (!p.HasValue) return string.Empty;
            string? text = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            return (text ?? string.Empty).Trim();
        }

        private static double Number(Parameter p)
        {
            if (!p.HasValue) return double.NaN;
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.Double:
                        return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), p.GetUnitTypeId());
                    case StorageType.Integer:
                        return p.AsInteger();
                    default:
                        return double.NaN;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return p.StorageType == StorageType.Double ? p.AsDouble() : double.NaN;
            }
        }

        private static AuditElement Describe(Element e, IReadOnlyList<string> values, ParamAccess access, ParamKey? systemKey)
        {
            string family = string.Empty;
            string type = string.Empty;
            if (e is FamilyInstance fi && fi.Symbol != null)
            {
                family = fi.Symbol.FamilyName;
                type = fi.Symbol.Name;
            }
            else if (e.Document.GetElement(e.GetTypeId()) is ElementType et)
            {
                family = et.FamilyName;
                type = et.Name;
            }
            string system = string.Empty;
            if (systemKey != null && access.Find(e, systemKey) is Parameter sp) system = Text(sp);
            if (system.Length == 0 && e.get_Parameter(BuiltInParameter.RBS_SYSTEM_ABBREVIATION_PARAM) is Parameter abbr) system = Text(abbr);
            string size = e.get_Parameter(BuiltInParameter.RBS_CALCULATED_SIZE) is Parameter s ? Text(s) : string.Empty;
            return new AuditElement(e.Id.ToLong(), values)
            {
                Category = e.Category?.Name ?? string.Empty,
                Family = family,
                Type = type,
                Size = size,
                System = system,
            };
        }
    }
}
