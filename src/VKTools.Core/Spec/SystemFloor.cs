using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VKTools.Core.Config;
using VKTools.Core.Text;

namespace VKTools.Core.Spec
{
    /// <summary>Этаж для уровня: имя уровня в модели → значение параметра этажа. Пустое значение — имя уровня как есть.</summary>
    public sealed class FloorRename
    {
        public FloorRename(string level, string value)
        {
            Level = (level ?? string.Empty).Trim();
            Value = (value ?? string.Empty).Trim();
        }

        public string Level { get; }

        public string Value { get; }
    }

    /// <summary>Откуда «Система и этаж» берёт «Р_Зона» (0.17.6).</summary>
    public enum ZoneSource
    {
        /// <summary>Не писать (по умолчанию).</summary>
        None,

        /// <summary>Помещение АР — то же значение, что «Р_Номер помещения».</summary>
        Room,

        /// <summary>Этаж — то же значение, что параметр этажа.</summary>
        Floor,

        /// <summary>Своя разметка: любой текстовый параметр элемента (экземпляра, иначе типа), например ADSK_Группирование.</summary>
        Param,
    }

    /// <summary>
    /// Настройка «Система и этаж» (Р4, порт NestedParamSync v2.1, шаги 1, 1б, 2): какие параметры писать, каким категориям,
    /// изоляция от основы, переименование уровней и перенос во вложенные той же операцией. Хранится в модели (ключи sf.*).
    /// </summary>
    public sealed class SystemFloorSettings
    {
        /// <summary>Категории по умолчанию — SELF_CATS NestedParamSync v2.1.</summary>
        public static readonly IReadOnlyList<string> DefaultCategories = new[]
        {
            "OST_PipeCurves", "OST_FlexPipeCurves", "OST_PipeFitting", "OST_PipeAccessory",
            "OST_MechanicalEquipment", "OST_PlumbingFixtures", "OST_Sprinklers",
        };

        /// <summary>Пустое сокращение ищется у соседей только у этих категорий (NEIGHBOR_CATS).</summary>
        public static readonly IReadOnlyList<string> NeighborCategories = new[]
        {
            "OST_PipeCurves", "OST_FlexPipeCurves", "OST_PipeFitting", "OST_PipeAccessory",
        };

        private const string SystemKey = "sf.systemParam";
        private const string FloorKey = "sf.floorParam";
        private const string FlagsKey = "sf.flags";
        private const string CategoriesKey = "sf.categories";
        private const string FloorsKey = "sf.floors";
        private const string MirrorKey = "sf.mirrorParam";
        private const string ZoneKey = "sf.zone";

        public ParamKey? SystemParam { get; set; }

        public ParamKey? FloorParam { get; set; }

        /// <summary>
        /// Тем же значением системы — ещё один параметр, если он есть у элемента (по умолчанию LSR_Имя системы: старые спецификации
        /// КрМа3 фильтруются по нему). Нет параметра в проекте — молча не пишется. null — не писать.
        /// </summary>
        public ParamKey? MirrorParam { get; set; }

        public bool WriteSystem { get; set; } = true;

        public bool WriteFloor { get; set; } = true;

        /// <summary>
        /// «Р_Номер помещения» по помещениям АР (модель и загруженные связи), 0.16.0: для спецификации по помещению (насосная).
        /// Помещений нет нигде — ничего не пишется.
        /// </summary>
        public bool WriteRoom { get; set; } = true;

        /// <summary>
        /// «Р_Зона» (0.17.6): из помещения, этажа или своего параметра — для фильтра «Создать спецификацию» по зонам
        /// (ПТ Пушкарева: «Насосная», «Ниже 0,000», «Выше 0,000», «Паркинг» из ADSK_Группирование). Вложенные — зона родителя, изоляция — основы.
        /// </summary>
        public ZoneSource Zone { get; set; } = ZoneSource.None;

        /// <summary>Параметр-источник при <see cref="ZoneSource.Param"/>.</summary>
        public ParamKey? ZoneParam { get; set; }

        /// <summary>Изоляция труб получает систему и этаж своей основы.</summary>
        public bool Insulation { get; set; } = true;

        /// <summary>Затем «Перенос во вложенные» (параметры — из настройки того модуля) в той же операции.</summary>
        public bool ChainNested { get; set; } = true;

        public List<string> Categories { get; set; } = DefaultCategories.ToList();

        public List<FloorRename> Floors { get; set; } = new List<FloorRename>();

        /// <summary>Умолчание по активному набору: ADSK — VK_Сокращение системы, ADSK_Этаж, второй LSR_Имя системы; свои — Р_*, без LSR.</summary>
        public static SystemFloorSettings Default() => new SystemFloorSettings
        {
            SystemParam = ParamProfile.TargetKey(ParamRole.System),
            FloorParam = ParamProfile.TargetKey(ParamRole.Floor),
            MirrorParam = ParamProfile.IsOwn ? null : ParamKey.Shared(KnownParameters.LsrSystemName.Name, KnownParameters.LsrSystemName.Guid),
        };

        /// <summary>Параметр системы — свой параметр VK Tools: плагин может сам добавить его в проект.</summary>
        public bool UsesVkSystem => SystemParam != null && SystemParam.Kind == ParamKeyKind.Shared
            && (SystemParam.Guid == KnownParameters.VkSystem.Guid || ParamProfile.OwnDefinitions.Any(d => d.Guid == SystemParam.Guid));

        /// <summary>Нужны свои параметры VK Tools в проекте (их плагин добавляет сам).</summary>
        public bool NeedsVkParameters => (WriteSystem && UsesVkSystem) || WriteRoom || WritesZone;

        /// <summary>Зона пишется: источник выбран (для «параметра» — и сам параметр).</summary>
        public bool WritesZone => Zone == ZoneSource.Room || Zone == ZoneSource.Floor || (Zone == ZoneSource.Param && ZoneParam != null);

        /// <summary>«помещение АР», «этаж», «параметр ADSK_Группирование» — для отчёта и окна.</summary>
        public string ZoneText() => Zone switch
        {
            ZoneSource.Room => "помещение АР",
            ZoneSource.Floor => "этаж",
            ZoneSource.Param => ZoneParam == null ? "параметр не выбран" : "параметр «" + ZoneParam.Name + "»",
            _ => "не писать",
        };

        public SystemFloorSettings Clone() => new SystemFloorSettings
        {
            WriteRoom = WriteRoom,
            Zone = Zone,
            ZoneParam = ZoneParam,
            SystemParam = SystemParam,
            FloorParam = FloorParam,
            MirrorParam = MirrorParam,
            WriteSystem = WriteSystem,
            WriteFloor = WriteFloor,
            Insulation = Insulation,
            ChainNested = ChainNested,
            Categories = Categories.ToList(),
            Floors = Floors.Select(f => new FloorRename(f.Level, f.Value)).ToList(),
        };

        /// <summary>Только переименования, которые что-то меняют (пустое значение = как есть).</summary>
        public IEnumerable<FloorRename> EffectiveFloors() =>
            Floors.Where(f => f.Level.Length > 0 && f.Value.Length > 0 && f.Value != f.Level);

        public string Signature() =>
            $"{SystemParam?.Id}|{FloorParam?.Id}|{MirrorParam?.Id}|{Flags()}|{ZoneValue()}|"
            + string.Join(",", Categories.OrderBy(c => c, StringComparer.Ordinal)) + "|"
            + string.Join("\n", EffectiveFloors().OrderBy(f => f.Level, StringComparer.Ordinal).Select(f => f.Level + "\t" + f.Value));

        /// <summary>Значение параметра этажа для уровня с этим именем.</summary>
        public string FloorValue(string levelName)
        {
            string name = (levelName ?? string.Empty).Trim();
            FloorRename? rename = EffectiveFloors().FirstOrDefault(f => string.Equals(f.Level, name, StringComparison.Ordinal));
            return rename?.Value ?? name;
        }

        private string Flags() =>
            $"system={B(WriteSystem)};floor={B(WriteFloor)};insulation={B(Insulation)};nested={B(ChainNested)};room={B(WriteRoom)}";

        private static string B(bool value) => value ? "1" : "0";

        private string ZoneValue() => Zone switch
        {
            ZoneSource.Room => "room",
            ZoneSource.Floor => "floor",
            ZoneSource.Param => "param" + (ZoneParam == null ? string.Empty : ";" + ZoneParam.Id + ";" + ZoneParam.Name),
            _ => string.Empty,
        };

        /// <summary>Положить в настройки проекта; совпадает с умолчанием — ключи удаляются.</summary>
        public void StoreTo(SpecProjectSettings settings, bool isDefault)
        {
            if (isDefault)
            {
                foreach (string key in new[] { SystemKey, FloorKey, FlagsKey, CategoriesKey, FloorsKey, MirrorKey, ZoneKey }) settings.Set(key, null);
                return;
            }
            settings.Set(SystemKey, SystemParam == null ? string.Empty : SystemParam.Id + ";" + SystemParam.Name);
            settings.Set(FloorKey, FloorParam == null ? string.Empty : FloorParam.Id + ";" + FloorParam.Name);
            settings.Set(MirrorKey, MirrorParam == null ? string.Empty : MirrorParam.Id + ";" + MirrorParam.Name);
            settings.Set(FlagsKey, Flags());
            string zone = ZoneValue();
            settings.Set(ZoneKey, zone.Length == 0 ? null : zone);
            settings.Set(CategoriesKey, string.Join(",", Categories));
            List<FloorRename> floors = EffectiveFloors().ToList();
            settings.Set(FloorsKey, floors.Count == 0 ? null
                : string.Join("\n", floors.Select(f => CsvWriter.Field(f.Level) + ";" + CsvWriter.Field(f.Value))));
        }

        /// <summary>Есть ли в модели своя настройка этого модуля.</summary>
        public static bool IsStored(SpecProjectSettings settings) => settings.Get(FlagsKey) != null;

        /// <summary>Разбор настройки из модели; ошибки — в <paramref name="issues"/>, тогда null.</summary>
        public static SystemFloorSettings? Parse(SpecProjectSettings settings, List<ConfigIssue> issues)
        {
            const string where = "настройка проекта: система и этаж";
            var result = new SystemFloorSettings();
            bool ok = true;
            result.SystemParam = ParseKey(settings.Get(SystemKey), "параметр системы", issues, ref ok);
            result.FloorParam = ParseKey(settings.Get(FloorKey), "параметр этажа", issues, ref ok);
            // Настройка до 0.13.0 (ключа нет): второго параметра не было
            result.MirrorParam = ParseKey(settings.Get(MirrorKey), "второй параметр системы", issues, ref ok);
            foreach (string part in (settings.Get(FlagsKey) ?? string.Empty).Split(';'))
            {
                string[] pair = part.Split('=');
                if (pair.Length != 2) continue;
                bool on = pair[1].Trim() == "1";
                switch (pair[0].Trim())
                {
                    case "system": result.WriteSystem = on; break;
                    case "floor": result.WriteFloor = on; break;
                    case "insulation": result.Insulation = on; break;
                    case "nested": result.ChainNested = on; break;
                    case "room": result.WriteRoom = on; break;
                }
            }
            string zone = settings.Get(ZoneKey) ?? string.Empty;
            if (zone == "room") result.Zone = ZoneSource.Room;
            else if (zone == "floor") result.Zone = ZoneSource.Floor;
            else if (zone.StartsWith("param", StringComparison.Ordinal))
            {
                result.Zone = ZoneSource.Param;
                string rest = zone.Length > 6 ? zone.Substring(6) : string.Empty;
                result.ZoneParam = ParseKey(rest, "параметр зоны", issues, ref ok);
            }
            string? categories = settings.Get(CategoriesKey);
            if (categories != null)
            {
                result.Categories = categories.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            }
            string? floors = settings.Get(FloorsKey);
            if (!string.IsNullOrEmpty(floors))
            {
                CsvTable table;
                try
                {
                    table = CsvTable.Parse("Уровень;Этаж\n" + floors);
                }
                catch (FormatException ex)
                {
                    issues.Add(new ConfigIssue(Checks.Severity.Error, where, 0, "таблица этажей повреждена: " + ex.Message));
                    return null;
                }
                foreach (CsvRow row in table.Rows) result.Floors.Add(new FloorRename(row.Get("Уровень"), row.Get("Этаж")));
            }
            if (!ok)
            {
                return null;
            }
            return result;
        }

        private static ParamKey? ParseKey(string? stored, string what, List<ConfigIssue> issues, ref bool ok)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            int semicolon = stored!.IndexOf(';');
            string id = semicolon < 0 ? stored : stored.Substring(0, semicolon);
            string name = semicolon < 0 ? string.Empty : stored.Substring(semicolon + 1);
            ParamKey? key = ParamKey.Parse(id, name);
            if (key == null)
            {
                issues.Add(new ConfigIssue(Checks.Severity.Error, "настройка проекта: система и этаж", 0, $"{what}: непонятный ключ «{id}»"));
                ok = false;
            }
            return key;
        }
    }

    /// <summary>Разбор «Сокращения для системы»: Revit пишет несколько систем через запятую («В1, Т3»).</summary>
    public static class SystemCodes
    {
        public static List<string> Split(string? raw)
        {
            var result = new List<string>();
            foreach (string part in (raw ?? string.Empty).Replace(';', ',').Split(','))
            {
                string code = part.Replace(' ', ' ').Trim();
                if (code.Length > 0 && !result.Contains(code, StringComparer.Ordinal)) result.Add(code);
            }
            return result;
        }

        /// <summary>«В1 / Т3» — для отчёта.</summary>
        public static string Join(IEnumerable<string> codes) => string.Join(" / ", codes);

        public static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);
    }
}
