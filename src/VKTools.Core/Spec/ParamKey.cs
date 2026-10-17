using System;
using System.Globalization;

namespace VKTools.Core.Spec
{
    public enum ParamKeyKind
    {
        /// <summary>Общий параметр (ФОП): ищется по GUID (D-003).</summary>
        Shared,

        /// <summary>Параметр проекта, не общий: ищется по имени среди параметров проекта.</summary>
        Project,

        /// <summary>Встроенный параметр Revit (только «Комментарии»): ищется по BuiltInParameter.</summary>
        BuiltIn,
    }

    /// <summary>
    /// Какой параметр писать: общий (GUID), параметр проекта (имя) или встроенный. Строковый вид <see cref="Id"/>
    /// хранится в настройках проекта и в файлах; имя — для отчёта.
    /// </summary>
    public sealed class ParamKey : IEquatable<ParamKey>
    {
        private const string ProjectPrefix = "проект:";
        private const string BuiltInPrefix = "встроенный:";

        private ParamKey(ParamKeyKind kind, string name, Guid guid, string builtIn)
        {
            Kind = kind;
            Name = name;
            Guid = guid;
            BuiltIn = builtIn;
        }

        public ParamKeyKind Kind { get; }

        public string Name { get; }

        public Guid Guid { get; }

        /// <summary>Имя BuiltInParameter (для <see cref="ParamKeyKind.BuiltIn"/>).</summary>
        public string BuiltIn { get; }

        /// <summary>GUID («9eabf56c-…»), «проект:Имя» или «встроенный:ALL_MODEL_INSTANCE_COMMENTS».</summary>
        public string Id => Kind switch
        {
            ParamKeyKind.Shared => Guid.ToString("D", CultureInfo.InvariantCulture),
            ParamKeyKind.Project => ProjectPrefix + Name,
            _ => BuiltInPrefix + BuiltIn,
        };

        public string KindText => Kind switch
        {
            ParamKeyKind.Shared => "общий",
            ParamKeyKind.Project => "проекта",
            _ => "встроенный",
        };

        public static ParamKey Shared(string name, Guid guid) => new ParamKey(ParamKeyKind.Shared, name, guid, string.Empty);

        public static ParamKey Project(string name) => new ParamKey(ParamKeyKind.Project, name, Guid.Empty, string.Empty);

        public static ParamKey OfBuiltIn(string name, string builtIn) => new ParamKey(ParamKeyKind.BuiltIn, name, Guid.Empty, builtIn);

        /// <summary>Разбор <see cref="Id"/>; null — непонятная строка.</summary>
        public static ParamKey? Parse(string id, string name)
        {
            id = (id ?? string.Empty).Trim();
            name = (name ?? string.Empty).Trim();
            if (id.StartsWith(ProjectPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string projectName = id.Substring(ProjectPrefix.Length).Trim();
                return projectName.Length == 0 ? null : Project(projectName);
            }
            if (id.StartsWith(BuiltInPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string builtIn = id.Substring(BuiltInPrefix.Length).Trim();
                return builtIn.Length == 0 ? null : OfBuiltIn(name.Length > 0 ? name : builtIn, builtIn);
            }
            return Guid.TryParse(id, out Guid guid) && guid != Guid.Empty ? Shared(name.Length > 0 ? name : id, guid) : null;
        }

        public bool Equals(ParamKey? other) => other != null && Kind == other.Kind && Id == other.Id;

        public override bool Equals(object? obj) => Equals(obj as ParamKey);

        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Id);

        public override string ToString() => Name;
    }

    public enum ParamValueKind
    {
        Text,
        Integer,
        Double,
    }

    /// <summary>Значение для записи: текст, целое (в том числе «да/нет») или число во внутренних единицах Revit.</summary>
    public sealed class ParamValue
    {
        private ParamValue(ParamValueKind kind, string text, int integer, double number, string display)
        {
            Kind = kind;
            Text = text;
            Integer = integer;
            Number = number;
            Display = display;
        }

        public ParamValueKind Kind { get; }

        public string Text { get; }

        public int Integer { get; }

        /// <summary>Число во внутренних единицах Revit (футы, радианы…).</summary>
        public double Number { get; }

        /// <summary>Как показать в отчёте: текст как есть, число — как его показывает Revit (с единицами).</summary>
        public string Display { get; }

        public static ParamValue OfText(string? text) => new ParamValue(ParamValueKind.Text, text ?? string.Empty, 0, 0, text ?? string.Empty);

        public static ParamValue OfInteger(int value, string? display) =>
            new ParamValue(ParamValueKind.Integer, string.Empty, value, 0, Show(display, value.ToString(CultureInfo.InvariantCulture)));

        public static ParamValue OfDouble(double value, string? display) =>
            new ParamValue(ParamValueKind.Double, string.Empty, 0, value, Show(display, value.ToString("0.######", CultureInfo.InvariantCulture)));

        /// <summary>Одно и то же значение: текст — посимвольно, число — с допуском на округление.</summary>
        public bool SameAs(ParamValue other)
        {
            if (other.Kind != Kind) return false;
            return Kind switch
            {
                ParamValueKind.Text => string.Equals(Text, other.Text, StringComparison.Ordinal),
                ParamValueKind.Integer => Integer == other.Integer,
                _ => Math.Abs(Number - other.Number) <= 1e-9 * Math.Max(1.0, Math.Abs(Number)),
            };
        }

        private static string Show(string? display, string fallback) => string.IsNullOrEmpty(display) ? fallback : display!;
    }
}
