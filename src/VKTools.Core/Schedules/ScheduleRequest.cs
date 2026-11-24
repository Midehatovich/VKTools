using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VKTools.Core.Schedules
{
    /// <summary>
    /// Что выбрал пользователь в окне «Создать спецификацию». Хранится в самой спецификации, чтобы «Обновить» пересобрало
    /// фильтры по тем же правилам: группа «Канализация» подхватит новую систему «К5», «1 этаж и выше» — новый этаж.
    /// </summary>
    public sealed class ScheduleRequest
    {
        public const string PresetAll = "all";
        public const string PresetManual = "manual";
        public const string PresetBelow = "below";
        public const string PresetAbove = "above";
        public const string GroupPrefix = "group:";
        public const string FormTemplate = "";

        private const int Version = 1;

        public string SystemPreset { get; set; } = PresetAll;

        public List<string> Systems { get; set; } = new List<string>();

        public string FloorPreset { get; set; } = PresetAll;

        public string FirstFloor { get; set; } = string.Empty;

        public List<string> Floors { get; set; } = new List<string>();

        public string Name { get; set; } = string.Empty;

        /// <summary>Пустое — встроенная форма ГОСТ 21.110; иначе имя спецификации-эталона.</summary>
        public string Template { get; set; } = FormTemplate;

        public bool GroupBySystem { get; set; } = true;

        public bool GroupByFloor { get; set; }

        /// <summary>Шапка граф — в основной надписи листа («Девятиграфка_Шапка» ADSK): у формы ГОСТ заголовки граф скрыты.</summary>
        public bool HeaderInTitleBlock { get; set; } = true;

        /// <summary>У копии эталона оставить его сортировку и группировку.</summary>
        public bool KeepTemplateSort { get; set; } = true;

        /// <summary>Значения в модели на момент создания: по ним «Обновить» видит новые системы и этажи.</summary>
        public List<string> KnownSystems { get; set; } = new List<string>();

        public List<string> KnownFloors { get; set; } = new List<string>();

        public string SystemParam { get; set; } = string.Empty;

        public string FloorParam { get; set; } = string.Empty;

        /// <summary>Фильтры, которые плагин поставил (текстом): отличаются от пересчитанных — спецификация устарела.</summary>
        public string FilterSignature { get; set; } = string.Empty;

        /// <summary>
        /// Спецификация одного помещения АР (0.16.0): значение «Р_Номер помещения» (номер, при повторе номера — с именем). Пусто — по
        /// системам и этажам, как раньше.
        /// </summary>
        public string Room { get; set; } = string.Empty;

        /// <summary>Как помещение звучит в названии: «2 «Помещение насосных станций»».</summary>
        public string RoomTitle { get; set; } = string.Empty;

        /// <summary>Помещение только в своей спецификации: остальные созданные кнопкой его исключают (решение пользователя 2026-11-24).</summary>
        public bool RoomExclusive { get; set; } = true;

        public bool IsRoom => Room.Length > 0;

        public bool UsesForm => string.IsNullOrEmpty(Template);

        /// <summary>Системы для текущей модели.</summary>
        public List<string> ResolveSystems(IReadOnlyList<string> known, IReadOnlyList<SystemGroup> groups)
        {
            if (SystemPreset == PresetAll) return known.ToList();
            SystemGroup? group = FindGroup(SystemPreset, groups);
            if (group != null) return known.Where(group.Contains).ToList();
            return known.Where(k => Systems.Contains(k, StringComparer.Ordinal)).ToList();
        }

        /// <summary>Этажи для текущей модели; «ниже/выше» — относительно первого этажа (его нет — пусто).</summary>
        public List<string> ResolveFloors(IReadOnlyList<FloorInfo> known)
        {
            if (FloorPreset == PresetAll) return known.Select(f => f.Value).ToList();
            if (FloorPreset == PresetBelow || FloorPreset == PresetAbove)
            {
                FloorInfo? first = known.FirstOrDefault(f => f.Value == FirstFloor);
                if (first == null) return new List<string>();
                bool below = FloorPreset == PresetBelow;
                return known.Where(f => FloorNames.IsBelow(f, first) == below).Select(f => f.Value).ToList();
            }
            return known.Where(k => Floors.Contains(k.Value, StringComparer.Ordinal)).Select(f => f.Value).ToList();
        }

        public static SystemGroup? FindGroup(string preset, IReadOnlyList<SystemGroup> groups)
        {
            if (preset == null || !preset.StartsWith(GroupPrefix, StringComparison.Ordinal)) return null;
            string name = preset.Substring(GroupPrefix.Length);
            return groups.FirstOrDefault(g => g.Name == name);
        }

        /// <summary>Какой быстрый выбор совпадает с отмеченными системами (галки вручную могут совпасть с группой).</summary>
        public static string DetectSystemPreset(IReadOnlyCollection<string> selected, IReadOnlyList<string> known, IReadOnlyList<SystemGroup> groups)
        {
            var set = new HashSet<string>(selected, StringComparer.Ordinal);
            if (known.Count > 0 && known.All(set.Contains)) return PresetAll;
            foreach (SystemGroup g in groups)
            {
                List<string> inGroup = known.Where(g.Contains).ToList();
                if (inGroup.Count > 0 && inGroup.Count == set.Count && inGroup.All(set.Contains)) return GroupPrefix + g.Name;
            }
            return PresetManual;
        }

        public static string DetectFloorPreset(IReadOnlyCollection<string> selected, IReadOnlyList<FloorInfo> known, string firstFloor)
        {
            var set = new HashSet<string>(selected, StringComparer.Ordinal);
            if (known.Count > 0 && known.All(f => set.Contains(f.Value))) return PresetAll;
            foreach (string preset in new[] { PresetBelow, PresetAbove })
            {
                var probe = new ScheduleRequest { FloorPreset = preset, FirstFloor = firstFloor };
                List<string> floors = probe.ResolveFloors(known);
                if (floors.Count > 0 && floors.Count == set.Count && floors.All(set.Contains)) return preset;
            }
            return PresetManual;
        }

        /// <summary>«Спецификация систем В1, Т3 и Т4 в уровнях 1, 2 и 3 этажа».</summary>
        public string AutoName(IReadOnlyList<string> systems, IReadOnlyList<string> floors, IReadOnlyList<SystemGroup> groups)
        {
            if (IsRoom)
            {
                // Формулировка пользователя (2026-11-24): «Спецификации систем В1, К1 и К4Н в помещении 2 «Помещение насосных станций»»
                string list = systems.Count == 0 ? string.Empty : (systems.Count == 1 ? "системы " + systems[0] : "систем " + JoinAnd(systems)) + " ";
                return CleanName("Спецификации " + list + "в помещении " + (RoomTitle.Length > 0 ? RoomTitle : Room));
            }
            var name = new StringBuilder("Спецификация ");
            SystemGroup? group = FindGroup(SystemPreset, groups);
            if (SystemPreset == PresetAll) name.Append("по всем системам");
            else if (group != null) name.Append(group.Title);
            else if (systems.Count == 1) name.Append("системы ").Append(systems[0]);
            else name.Append("систем ").Append(JoinAnd(systems));

            string floor = FloorPreset switch
            {
                PresetAll => string.Empty,
                PresetBelow => "ниже " + FloorNames.Genitive(FirstFloor),
                PresetAbove => "выше " + FloorNames.Genitive(FirstFloor) + " включительно",
                _ => FloorsText(floors),
            };
            if (floor.Length > 0) name.Append(' ').Append(floor);
            return CleanName(name.ToString());
        }

        private static string FloorsText(IReadOnlyList<string> floors)
        {
            if (floors.Count == 0) return string.Empty;
            List<int?> numbers = floors.Select(FloorNames.Number).ToList();
            if (numbers.All(n => n.HasValue))
            {
                List<string> list = numbers.Select(n => n!.Value.ToString(CultureInfo.InvariantCulture)).ToList();
                return (list.Count == 1 ? "в уровне " : "в уровнях ") + JoinAnd(list) + " этажа";
            }
            return (floors.Count == 1 ? "в уровне " : "в уровнях ") + JoinAnd(floors.Select(f => "«" + f + "»").ToList());
        }

        public static string JoinAnd(IReadOnlyList<string> items)
        {
            if (items.Count == 0) return string.Empty;
            if (items.Count == 1) return items[0];
            return string.Join(", ", items.Take(items.Count - 1)) + " и " + items[items.Count - 1];
        }

        /// <summary>Revit не принимает в имени вида \ : { } [ ] | ; &lt; &gt; ? ` ~.</summary>
        public static string CleanName(string name)
        {
            const string forbidden = "\\:{}[]|;<>?`~";
            var sb = new StringBuilder();
            foreach (char c in name ?? string.Empty) sb.Append(forbidden.IndexOf(c) >= 0 ? '-' : c);
            string result = sb.ToString();
            while (result.Contains("  ")) result = result.Replace("  ", " ");
            return result.Trim();
        }

        public Dictionary<string, string> ToMap()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["version"] = Version.ToString(CultureInfo.InvariantCulture),
                ["systemPreset"] = SystemPreset,
                ["systems"] = Join(Systems),
                ["floorPreset"] = FloorPreset,
                ["firstFloor"] = FirstFloor,
                ["floors"] = Join(Floors),
                ["name"] = Name,
                ["template"] = Template,
                ["flags"] = $"bySystem={B(GroupBySystem)};byFloor={B(GroupByFloor)};keepSort={B(KeepTemplateSort)};titleHeader={B(HeaderInTitleBlock)}",
                ["knownSystems"] = Join(KnownSystems),
                ["knownFloors"] = Join(KnownFloors),
                ["systemParam"] = SystemParam,
                ["floorParam"] = FloorParam,
                ["filters"] = FilterSignature,
                ["room"] = Room,
                ["roomTitle"] = RoomTitle,
                ["roomExclusive"] = B(RoomExclusive),
            };
        }

        public static ScheduleRequest? FromMap(IDictionary<string, string> map)
        {
            if (map == null || !map.TryGetValue("version", out string? version)) return null;
            if (!int.TryParse(version, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) || v > Version) return null;
            string Get(string key) => map.TryGetValue(key, out string? value) ? value ?? string.Empty : string.Empty;
            var request = new ScheduleRequest
            {
                SystemPreset = Get("systemPreset"),
                Systems = Split(Get("systems")),
                FloorPreset = Get("floorPreset"),
                FirstFloor = Get("firstFloor"),
                Floors = Split(Get("floors")),
                Name = Get("name"),
                Template = Get("template"),
                KnownSystems = Split(Get("knownSystems")),
                KnownFloors = Split(Get("knownFloors")),
                SystemParam = Get("systemParam"),
                FloorParam = Get("floorParam"),
                FilterSignature = Get("filters"),
                Room = Get("room"),
                RoomTitle = Get("roomTitle"),
                RoomExclusive = Get("roomExclusive") != "0",
            };
            if (request.SystemPreset.Length == 0) request.SystemPreset = PresetManual;
            if (request.FloorPreset.Length == 0) request.FloorPreset = PresetManual;
            foreach (string part in Get("flags").Split(';'))
            {
                string[] pair = part.Split('=');
                if (pair.Length != 2) continue;
                bool on = pair[1] == "1";
                switch (pair[0])
                {
                    case "bySystem": request.GroupBySystem = on; break;
                    case "byFloor": request.GroupByFloor = on; break;
                    case "keepSort": request.KeepTemplateSort = on; break;
                    case "titleHeader": request.HeaderInTitleBlock = on; break;
                }
            }
            return request;
        }

        private static string B(bool value) => value ? "1" : "0";

        private static string Join(IEnumerable<string> values) => string.Join("\n", values);

        private static List<string> Split(string text) =>
            text.Split('\n').Where(s => s.Length > 0).ToList();
    }
}
