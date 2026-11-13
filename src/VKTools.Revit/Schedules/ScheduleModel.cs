using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using VKTools.Core.Config;
using VKTools.Core.Schedules;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Spec;

namespace VKTools.Revit.Schedules
{
    /// <summary>Спецификация модели, из которой можно взять оформление.</summary>
    internal sealed class TemplateInfo
    {
        public TemplateInfo(ViewSchedule schedule, bool multi, int filters)
        {
            Id = schedule.Id.ToLong();
            Name = schedule.Name;
            Multi = multi;
            Filters = filters;
        }

        public long Id { get; }

        public string Name { get; }

        public bool Multi { get; }

        public int Filters { get; }

        public override string ToString() => Name + (Multi ? string.Empty : " (одна категория)");
    }

    /// <summary>Спецификация, созданная кнопкой, и актуальны ли её фильтры.</summary>
    internal sealed class OwnSchedule
    {
        public OwnSchedule(long id, string name, ScheduleRequest request, string state, bool stale)
        {
            Id = id;
            Name = name;
            Request = request;
            State = state;
            Stale = stale;
        }

        public long Id { get; }

        public string Name { get; }

        public ScheduleRequest Request { get; }

        public string State { get; }

        public bool Stale { get; }
    }

    /// <summary>
    /// Что нужно окну: системы и этажи модели (значения параметров «Системы и этажа» у элементов ВК), форма ГОСТ,
    /// группы систем, спецификации-эталоны и уже созданные кнопкой спецификации.
    /// </summary>
    internal sealed class ScheduleModelData
    {
        public ParamKey SystemParam { get; set; } = ParamKey.Shared(KnownParameters.LsrSystemName.Name, KnownParameters.LsrSystemName.Guid);

        public ParamKey FloorParam { get; set; } = ParamKey.Shared(KnownParameters.AdskLevel.Name, KnownParameters.AdskLevel.Guid);

        public List<string> Systems { get; } = new List<string>();

        public Dictionary<string, int> SystemCounts { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        public List<FloorInfo> Floors { get; } = new List<FloorInfo>();

        /// <summary>Элементов на пару «система → этаж» (пустое — без значения): для подсчёта в окне.</summary>
        public Dictionary<(string System, string Floor), int> Pairs { get; } = new Dictionary<(string, string), int>();

        public int Elements { get; set; }

        public int WithoutSystem { get; set; }

        public int WithoutFloor { get; set; }

        public List<FormColumn> Form { get; } = new List<FormColumn>();

        public List<SystemGroup> Groups { get; } = new List<SystemGroup>();

        public List<ConfigIssue> Issues { get; } = new List<ConfigIssue>();

        public List<string> Sources { get; } = new List<string>();

        public List<TemplateInfo> Templates { get; } = new List<TemplateInfo>();

        public List<OwnSchedule> Own { get; } = new List<OwnSchedule>();

        public HashSet<string> ViewNames { get; } = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        public List<string> KnownFloorValues => Floors.Select(f => f.Value).ToList();

        /// <summary>Сколько элементов покажет спецификация (без расчётных позиций Р3).</summary>
        public int Count(ICollection<string> systems, bool allSystems, ICollection<string> floors, bool allFloors) =>
            Pairs.Where(p => (allSystems || systems.Contains(p.Key.System)) && (allFloors || floors.Contains(p.Key.Floor))).Sum(p => p.Value);

        /// <summary>Пересчитать фильтры выбора на текущей модели.</summary>
        public ScheduleFilterSet Plan(ScheduleRequest request, int otherFilters, out List<string> systems, out List<string> floors)
        {
            systems = request.ResolveSystems(Systems, Groups);
            floors = request.ResolveFloors(Floors);
            return ScheduleFilterSet.Build(Systems, systems, KnownFloorValues, floors, otherFilters);
        }
    }

    internal static class ScheduleModel
    {
        private static readonly string[] ExtraCategories = { "OST_PipeInsulations", "OST_GenericModel" };

        public static ScheduleModelData Collect(Document doc)
        {
            var data = new ScheduleModelData();
            ConfigLoader loader = Host.CreateConfigLoader(Host.LoadSettings());
            LoadedTable form = loader.Load(ScheduleRules.Form);
            data.Issues.AddRange(form.Issues);
            data.Sources.Add(SpecSettingsLoader.Source(form));
            if (form.Table != null) data.Form.AddRange(ScheduleRules.ParseForm(form.Table, data.Issues));
            LoadedTable groups = loader.Load(ScheduleRules.Groups);
            data.Issues.AddRange(groups.Issues);
            data.Sources.Add(SpecSettingsLoader.Source(groups));
            if (groups.Table != null) data.Groups.AddRange(ScheduleRules.ParseGroups(groups.Table, data.Issues));

            // Параметры системы и этажа — те же, что пишет «Система и этаж» (настройка проекта или по умолчанию)
            (SpecOptions options, _) = SpecSettingsLoader.Load(doc, SpecModule.SystemFloor);
            SystemFloorSettings sf = options.SystemFloor;
            if (sf.SystemParam != null) data.SystemParam = sf.SystemParam;
            if (sf.FloorParam != null) data.FloorParam = sf.FloorParam;

            ReadElements(doc, data, sf);
            ReadViews(doc, data);
            return data;
        }

        private static void ReadElements(Document doc, ScheduleModelData data, SystemFloorSettings sf)
        {
            var categories = new List<BuiltInCategory>();
            foreach (string name in sf.Categories.Concat(ExtraCategories))
            {
                if (Enum.TryParse(name, out BuiltInCategory bic) && !categories.Contains(bic)) categories.Add(bic);
            }
            var access = new ParamAccess(doc);
            var floorElevation = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (Level level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
            {
                string value = sf.FloorValue(level.Name);
                double elevation = Math.Round(level.Elevation * 0.3048, 3);
                if (!floorElevation.TryGetValue(value, out double known) || elevation < known) floorElevation[value] = elevation;
            }
            var floorCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var collector = new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categories.ToList())).WhereElementIsNotElementType();
            foreach (Element element in collector)
            {
                Parameter? system = access.Find(element, data.SystemParam);
                if (system == null) continue;
                string sys = Text(system);
                Parameter? floorParam = access.Find(element, data.FloorParam);
                string floor = floorParam == null ? string.Empty : Text(floorParam);
                data.Elements++;
                if (sys.Length == 0) data.WithoutSystem++;
                else data.SystemCounts[sys] = data.SystemCounts.TryGetValue(sys, out int n) ? n + 1 : 1;
                if (floor.Length == 0) data.WithoutFloor++;
                else floorCounts[floor] = floorCounts.TryGetValue(floor, out int f) ? f + 1 : 1;
                data.Pairs[(sys, floor)] = data.Pairs.TryGetValue((sys, floor), out int p) ? p + 1 : 1;
            }
            data.Systems.AddRange(data.SystemCounts.Keys.OrderBy(s => s, NaturalComparer.Instance));
            data.Floors.AddRange(FloorNames.Order(floorCounts.Select(kv =>
                new FloorInfo(kv.Key, floorElevation.TryGetValue(kv.Key, out double e) ? e : (double?)null, kv.Value))));
        }

        private static string Text(Parameter p) =>
            ((p.StorageType == StorageType.String ? p.AsString() : p.AsValueString()) ?? string.Empty).Trim();

        private static void ReadViews(Document doc, ScheduleModelData data)
        {
            foreach (View view in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>())
            {
                if (!view.IsTemplate) data.ViewNames.Add(view.Name);
            }
            foreach (ViewSchedule vs in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
            {
                if (vs.IsTemplate || vs.IsTitleblockRevisionSchedule || vs.IsInternalKeynoteSchedule) continue;
                ScheduleDefinition definition;
                try
                {
                    definition = vs.Definition;
                    if (definition.IsKeySchedule || definition.IsMaterialTakeoff) continue;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    continue;
                }
                bool multi = definition.CategoryId == ElementId.InvalidElementId;
                ScheduleRequest? request = ScheduleStore.Read(vs);
                if (request != null)
                {
                    data.Own.Add(OwnState(vs, request, data));
                }
                data.Templates.Add(new TemplateInfo(vs, multi, OtherFilters(doc, definition, data)));
            }
            data.Templates.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
            data.Own.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        }

        private static OwnSchedule OwnState(ViewSchedule vs, ScheduleRequest request, ScheduleModelData data)
        {
            int other = OtherFilters(vs.Document, vs.Definition, data);
            ScheduleFilterSet set = data.Plan(request, other, out _, out _);
            if (set.Error != null) return new OwnSchedule(vs.Id.ToLong(), vs.Name, request, "не обновить: " + set.Error, false);
            string signature = string.Join("\n", set.Describe(data.SystemParam.Name, data.FloorParam.Name));
            var added = data.Systems.Where(s => !request.KnownSystems.Contains(s, StringComparer.Ordinal))
                .Concat(data.KnownFloorValues.Where(f => !request.KnownFloors.Contains(f, StringComparer.Ordinal))).ToList();
            if (signature == request.FilterSignature)
            {
                return new OwnSchedule(vs.Id.ToLong(), vs.Name, request, added.Count == 0 ? "фильтры актуальны" : "фильтры актуальны (новые значения учтены: " + string.Join(", ", added) + ")", false);
            }
            string why = added.Count > 0 ? "в модели появились " + string.Join(", ", added.Take(6)) + (added.Count > 6 ? "…" : string.Empty) : "состав систем или этажей изменился";
            return new OwnSchedule(vs.Id.ToLong(), vs.Name, request, "нужно обновить: " + why, true);
        }

        /// <summary>Фильтры эталона не по системе и этажу: остаются в копии и занимают места из восьми.</summary>
        public static int OtherFilters(Document doc, ScheduleDefinition definition, ScheduleModelData data)
        {
            int count = 0;
            foreach (ScheduleFilter filter in definition.GetFilters())
            {
                ScheduleField field = definition.GetField(filter.FieldId);
                if (!ScheduleFields.Is(doc, field.ParameterId, data.SystemParam) && !ScheduleFields.Is(doc, field.ParameterId, data.FloorParam)) count++;
            }
            return count;
        }
    }

    /// <summary>Поля спецификации по ключу параметра: общий — по GUID, параметр проекта — по имени.</summary>
    internal static class ScheduleFields
    {
        public static bool Is(Document doc, ElementId parameterId, ParamKey key)
        {
            switch (key.Kind)
            {
                case ParamKeyKind.Shared:
                    return doc.GetElement(parameterId) is SharedParameterElement shared && shared.GuidValue == key.Guid;
                case ParamKeyKind.Project:
                    return doc.GetElement(parameterId) is ParameterElement element && !(element is SharedParameterElement) && element.Name == key.Name;
                default:
                    return Enum.TryParse(key.BuiltIn, out BuiltInParameter bip) && parameterId.ToLong() == (long)bip;
            }
        }

        public static ScheduleField? Find(Document doc, ScheduleDefinition definition, ParamKey key)
        {
            for (int i = 0; i < definition.GetFieldCount(); i++)
            {
                ScheduleField field = definition.GetField(i);
                if (!field.IsCalculatedField && Is(doc, field.ParameterId, key)) return field;
            }
            return null;
        }

        /// <summary>Найти поле или добавить (скрытым, если просили). null — параметра нет среди доступных полей.</summary>
        public static ScheduleField? Ensure(Document doc, ScheduleDefinition definition, ParamKey key, bool hidden)
        {
            ScheduleField? existing = Find(doc, definition, key);
            if (existing != null) return existing;
            SchedulableField? best = null;
            foreach (SchedulableField candidate in definition.GetSchedulableFields())
            {
                if (!Is(doc, candidate.ParameterId, key)) continue;
                if (best == null || candidate.FieldType == ScheduleFieldType.Instance) best = candidate;
                if (candidate.FieldType == ScheduleFieldType.Instance) break;
            }
            if (best == null) return null;
            ScheduleField field = definition.AddField(best);
            field.IsHidden = hidden;
            return field;
        }
    }

    /// <summary>Выбор пользователя в самой спецификации (Extensible Storage на виде): копия вида уносит его с собой.</summary>
    internal static class ScheduleStore
    {
        private static readonly Guid SchemaGuid = new Guid("c3a9e0b4-5d61-4f2e-8b7a-1e9d4c6f2a85");
        private const string FieldValues = "Values";

        public static ScheduleRequest? Read(ViewSchedule schedule)
        {
            Schema? schema = Schema.Lookup(SchemaGuid);
            if (schema == null) return null;
            Entity entity = schedule.GetEntity(schema);
            if (!entity.IsValid()) return null;
            IDictionary<string, string> values = entity.Get<IDictionary<string, string>>(schema.GetField(FieldValues));
            return ScheduleRequest.FromMap(values);
        }

        /// <summary>Внутри открытой транзакции.</summary>
        public static void Write(ViewSchedule schedule, ScheduleRequest request)
        {
            Schema schema = Schema.Lookup(SchemaGuid) ?? Build();
            var entity = new Entity(schema);
            entity.Set<IDictionary<string, string>>(schema.GetField(FieldValues), request.ToMap());
            schedule.SetEntity(entity);
        }

        private static Schema Build()
        {
            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("VKToolsScheduleRequest");
            builder.SetDocumentation("VK Tools: выбор систем и этажей, по которому создана спецификация («Создать спецификацию»).");
            builder.SetVendorId("VKTOOLS");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.AddMapField(FieldValues, typeof(string), typeof(string));
            return builder.Finish();
        }
    }

    /// <summary>«В2» после «В1», «К10» после «К4.1».</summary>
    internal sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();

        public int Compare(string? x, string? y)
        {
            string a = x ?? string.Empty;
            string b = y ?? string.Empty;
            int i = 0;
            int j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    long na = 0;
                    long nb = 0;
                    while (i < a.Length && char.IsDigit(a[i])) na = (na * 10) + (a[i++] - '0');
                    while (j < b.Length && char.IsDigit(b[j])) nb = (nb * 10) + (b[j++] - '0');
                    if (na != nb) return na.CompareTo(nb);
                    continue;
                }
                int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
