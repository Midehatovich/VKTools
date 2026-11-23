using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Spec;

namespace VKTools.Core.Config
{
    /// <summary>Какой набор параметров читает спецификация и пишет плагин.</summary>
    public enum ParamSetKind
    {
        /// <summary>ADSK_* из ФОП отдела (КрМа3 и модели, где настройки VK Tools уже сохранены).</summary>
        Adsk = 0,

        /// <summary>Свои параметры «Р_*» из ФОП VK Tools: ADSK_* плагин не пишет (решение 2026-11-23, объект «Пушкарев»).</summary>
        Own = 1,
    }

    /// <summary>Роль параметра в спецификации ВК.</summary>
    public enum ParamRole
    {
        Position,
        Name,
        Mark,
        Code,
        Maker,
        Unit,
        Quantity,
        Mass,
        Note,
        Group,
        Floor,
        System,
    }

    /// <summary>Своё определение параметра VK Tools: имя, GUID (навсегда), тип данных ФОП.</summary>
    public sealed class OwnParamDef
    {
        public OwnParamDef(ParamRole role, string name, Guid guid, bool number, string description)
        {
            Role = role;
            Name = name;
            Guid = guid;
            Number = number;
            Description = description;
        }

        public ParamRole Role { get; }

        public string Name { get; }

        public Guid Guid { get; }

        /// <summary>Число (NUMBER в ФОП); иначе текст.</summary>
        public bool Number { get; }

        public string Description { get; }

        public ParameterRef Ref => new ParameterRef(Name, Guid);

        public ParamKey Key => ParamKey.Shared(Name, Guid);
    }

    /// <summary>
    /// Наборы параметров спецификации: ADSK_* (как в КрМа3) и свои «Р_*». Набор выбирается на модель (ключ <see cref="SettingsKey"/>
    /// в настройках VK Tools в модели); модель без настроек VK Tools — новая, берёт «Р_». Команда, которая читает настройки модели,
    /// ставит <see cref="Active"/> перед построением умолчаний (Revit однопоточный, команды работают с одним документом).
    /// «Р» — кириллица: латинская «P» выглядит так же, но это другая строка.
    /// </summary>
    public static class ParamProfile
    {
        public const string SettingsKey = "params.set";

        public const string OwnPrefix = "Р_";

        public static readonly IReadOnlyList<OwnParamDef> OwnDefinitions = new[]
        {
            new OwnParamDef(ParamRole.Position, "Р_Позиция", new Guid("876103ca-8bd4-4792-8f2f-02e29d454967"), false, "VK Tools: позиция (копия ADSK_Позиция)"),
            new OwnParamDef(ParamRole.Name, "Р_Наименование", new Guid("991962bf-daaa-4cdf-b394-4fa03aa38c62"), false, "VK Tools: наименование и техническая характеристика"),
            new OwnParamDef(ParamRole.Mark, "Р_Марка", new Guid("439aeb1b-28cd-44e9-a8d6-74e0befd6eab"), false, "VK Tools: тип, марка, обозначение документа"),
            new OwnParamDef(ParamRole.Code, "Р_Код изделия", new Guid("ef0897e4-20bd-4b7d-97ca-9ccfc9ce2267"), false, "VK Tools: код изделия"),
            new OwnParamDef(ParamRole.Maker, "Р_Завод-изготовитель", new Guid("6644d9ac-c898-46e7-95fb-80d091ae7391"), false, "VK Tools: поставщик"),
            new OwnParamDef(ParamRole.Unit, "Р_Единица измерения", new Guid("f6a15e44-03a0-42c1-93ac-214fd31c0b2a"), false, "VK Tools: единица измерения"),
            new OwnParamDef(ParamRole.Quantity, "Р_Количество", new Guid("7c8d8687-7995-4bea-a543-f85f2c372376"), true, "VK Tools: количество (трубы и изоляция — с запасом)"),
            new OwnParamDef(ParamRole.Mass, "Р_Масса", new Guid("3784ca1d-1a42-46de-88d0-7a271deb6377"), true, "VK Tools: масса 1 ед., кг"),
            new OwnParamDef(ParamRole.Note, "Р_Примечание", new Guid("8c160a35-6634-4189-a472-526ea2139f43"), false, "VK Tools: примечание"),
            new OwnParamDef(ParamRole.Group, "Р_Группирование", new Guid("0dcd461e-2cd7-4fb5-8254-43d0ee67fe3a"), false, "VK Tools: сортировка разделов спецификации"),
            new OwnParamDef(ParamRole.Floor, "Р_Этаж", new Guid("2f253eb9-0bac-474d-bf7c-d4738b276f3e"), false, "VK Tools: этаж (имя уровня)"),
            new OwnParamDef(ParamRole.System, "Р_Сокращение системы", new Guid("e7686104-a64d-4e99-8466-9fef98d7e175"), false, "VK Tools: сокращение системы для фильтров спецификаций"),
        };

        /// <summary>
        /// GUID «Р_» 0.14.x (редактируемые вручную). С 0.15.0 «Р_» не редактируются вручную (USERMODIFIABLE = 0, решение пользователя 2026-11-23),
        /// а этот признак Revit задаёт только при создании параметра — поэтому новые GUID; старые плагин переносит (значения, графы, настройки).
        /// </summary>
        public static readonly IReadOnlyDictionary<Guid, ParamRole> LegacyOwnGuids = new Dictionary<Guid, ParamRole>
        {
            [new Guid("173d86d8-465b-4bef-8541-4d503231d600")] = ParamRole.Position,
            [new Guid("bbe8adf8-7aa7-4b69-aa04-82a50f30f4df")] = ParamRole.Name,
            [new Guid("fcaa3c34-387a-4646-a9a3-69f9afb41628")] = ParamRole.Mark,
            [new Guid("b9d5507a-2d69-429f-800a-d0bb7d7bd0eb")] = ParamRole.Code,
            [new Guid("41e91545-14bb-4712-9c07-7d80554844bb")] = ParamRole.Maker,
            [new Guid("074d72ac-0a0c-47b3-ad0a-3b50b28afc1e")] = ParamRole.Unit,
            [new Guid("e8f7cdb5-fe43-4a07-a577-dd057acc3488")] = ParamRole.Quantity,
            [new Guid("106e552d-8e37-4e1c-a266-d7f117671aab")] = ParamRole.Mass,
            [new Guid("643fda80-80f5-4631-a3a3-d91c680a76c5")] = ParamRole.Note,
            [new Guid("e5b08cda-2011-4a01-a660-f79e0077d802")] = ParamRole.Group,
            [new Guid("db8e55d3-4b24-4f5a-af53-6bc9d686c41a")] = ParamRole.Floor,
            [new Guid("9e0b554d-b41e-4be5-b44f-cd2473c8af61")] = ParamRole.System,
        };

        /// <summary>Старый GUID «Р_» → текущий; прочие — как есть (сохранённые настройки и выбор параметров читаются со старыми GUID).</summary>
        public static Guid Current(Guid guid) => LegacyOwnGuids.TryGetValue(guid, out ParamRole role) ? Own(role).Guid : guid;

        private static readonly Dictionary<ParamRole, ParameterRef> AdskRefs = new Dictionary<ParamRole, ParameterRef>
        {
            [ParamRole.Position] = new ParameterRef("ADSK_Позиция", new Guid("ae8ff999-1f22-4ed7-ad33-61503d85f0f4")),
            [ParamRole.Name] = KnownParameters.AdskName,
            [ParamRole.Mark] = new ParameterRef("ADSK_Марка", new Guid("2204049c-d557-4dfc-8d70-13f19715e46d")),
            [ParamRole.Code] = new ParameterRef("ADSK_Код изделия", new Guid("2fd9e8cb-84f3-4297-b8b8-75f444e124ed")),
            [ParamRole.Maker] = new ParameterRef("ADSK_Завод-изготовитель", new Guid("a8cdbf7b-d60a-485e-a520-447d2055f351")),
            [ParamRole.Unit] = new ParameterRef("ADSK_Единица измерения", new Guid("4289cb19-9517-45de-9c02-5a74ebf5c86d")),
            [ParamRole.Quantity] = KnownParameters.AdskQuantity,
            [ParamRole.Mass] = new ParameterRef("ADSK_Масса", new Guid("32989501-0d17-4916-8777-da950841c6d7")),
            [ParamRole.Note] = new ParameterRef("ADSK_Примечание", new Guid("a85b7661-26b0-412f-979c-66af80b4b2c3")),
            [ParamRole.Group] = KnownParameters.AdskGrouping,
            [ParamRole.Floor] = KnownParameters.AdskLevel,
            [ParamRole.System] = KnownParameters.VkSystem,
        };

        /// <summary>Роли, значения которых «Система и этаж» копирует из ADSK_* (остальные считает плагин).</summary>
        public static readonly IReadOnlyList<ParamRole> CopiedRoles = new[]
        {
            ParamRole.Position, ParamRole.Name, ParamRole.Mark, ParamRole.Code, ParamRole.Maker, ParamRole.Unit,
            ParamRole.Quantity, ParamRole.Mass, ParamRole.Note,
        };

        [ThreadStatic]
        private static ParamSetKind _active;

        /// <summary>
        /// Набор, по которому строятся умолчания модулей. Ставит команда по настройке модели. На поток (Revit работает в одном,
        /// тесты — параллельно); по умолчанию ADSK.
        /// </summary>
        public static ParamSetKind Active
        {
            get => _active;
            set => _active = value;
        }

        public static bool IsOwn => Active == ParamSetKind.Own;

        /// <summary>Набор модели: явный выбор, иначе модель с настройками VK Tools — ADSK (КрМа3), без них — свои «Р_».</summary>
        public static ParamSetKind Resolve(SpecProjectSettings settings)
        {
            string? stored = settings.Get(SettingsKey);
            if (string.Equals(stored, "own", StringComparison.OrdinalIgnoreCase)) return ParamSetKind.Own;
            if (string.Equals(stored, "adsk", StringComparison.OrdinalIgnoreCase)) return ParamSetKind.Adsk;
            return settings.Values.Keys.Any(k => !k.StartsWith("schedule.", StringComparison.Ordinal)) ? ParamSetKind.Adsk : ParamSetKind.Own;
        }

        public static string Store(ParamSetKind kind) => kind == ParamSetKind.Own ? "own" : "adsk";

        public static string Title(ParamSetKind kind) => kind == ParamSetKind.Own ? "свои параметры «Р_»" : "ADSK_ (ФОП отдела)";

        public static ParameterRef Adsk(ParamRole role) => AdskRefs[role];

        public static OwnParamDef Own(ParamRole role) => OwnDefinitions.First(d => d.Role == role);

        /// <summary>Параметр роли в наборе.</summary>
        public static ParameterRef Of(ParamRole role, ParamSetKind kind) => kind == ParamSetKind.Own ? Own(role).Ref : Adsk(role);

        /// <summary>Параметр роли в активном наборе.</summary>
        public static ParameterRef Target(ParamRole role) => Of(role, Active);

        public static ParamKey TargetKey(ParamRole role)
        {
            ParameterRef r = Target(role);
            return ParamKey.Shared(r.Name, r.Guid);
        }

        /// <summary>Роль параметра ADSK_* или своего по GUID; null — параметр не из наборов.</summary>
        public static ParamRole? RoleOf(Guid guid)
        {
            foreach (KeyValuePair<ParamRole, ParameterRef> pair in AdskRefs)
            {
                if (pair.Value.Guid == guid) return pair.Key;
            }
            OwnParamDef? own = OwnDefinitions.FirstOrDefault(d => d.Guid == guid);
            if (own != null) return own.Role;
            return LegacyOwnGuids.TryGetValue(guid, out ParamRole legacy) ? legacy : (ParamRole?)null;
        }

        /// <summary>Роль по имени (ADSK_Марка, Р_Марка, VK_Сокращение системы); null — не из наборов.</summary>
        public static ParamRole? RoleOf(string name)
        {
            foreach (KeyValuePair<ParamRole, ParameterRef> pair in AdskRefs)
            {
                if (string.Equals(pair.Value.Name, name, StringComparison.Ordinal)) return pair.Key;
            }
            OwnParamDef? own = OwnDefinitions.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));
            return own?.Role;
        }

        /// <summary>
        /// Параметр из правил отдела (nested_params.csv, графы эталона, колонки таблиц) в активном наборе: ADSK_* роли → «Р_*»;
        /// параметры заказчика LSR_* в своём наборе не пишутся (null). Прочие — как есть.
        /// </summary>
        public static ParameterRef? MapToActive(ParameterRef source)
        {
            if (!IsOwn) return source;
            if (source.Name.StartsWith("LSR_", StringComparison.Ordinal)) return null;
            ParamRole? role = RoleOf(source.Guid) ?? RoleOf(source.Name);
            return role.HasValue ? Own(role.Value).Ref : source;
        }

        /// <summary>Имя параметра (колонка таблицы, цель правила) в активном наборе.</summary>
        public static string? MapNameToActive(string name)
        {
            if (!IsOwn) return name;
            if (name.StartsWith("LSR_", StringComparison.Ordinal)) return null;
            ParamRole? role = RoleOf(name);
            return role.HasValue ? Own(role.Value).Name : name;
        }

        /// <summary>Текст ФОП Revit со своими параметрами (группа «VK»): «VK_Сокращение системы» и все «Р_*».</summary>
        public static string SharedParameterFile()
        {
            var lines = new List<string>
            {
                "# This is a Revit shared parameter file.",
                "# Do not edit manually.",
                "*META\tVERSION\tMINVERSION",
                "META\t2\t1",
                "*GROUP\tID\tNAME",
                "GROUP\t1\tVK",
                "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE",
                $"PARAM\t{KnownParameters.VkSystem.Guid:D}\t{KnownParameters.VkSystem.Name}\tTEXT\t\t1\t1\tVK Tools: копия «Сокращения для системы» для спецификаций\t1\t0",
            };
            foreach (OwnParamDef d in OwnDefinitions)
            {
                // USERMODIFIABLE = 0: в свойствах и спецификации серые, пишет только плагин (API)
                lines.Add($"PARAM\t{d.Guid:D}\t{d.Name}\t{(d.Number ? "NUMBER" : "TEXT")}\t\t1\t1\t{d.Description}\t0\t0");
            }
            return string.Join("\r\n", lines) + "\r\n";
        }
    }
}
