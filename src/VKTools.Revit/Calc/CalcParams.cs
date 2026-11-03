using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using VKTools.Core.Calc;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Calc
{
    /// <summary>
    /// Параметры расчётных позиций — по имени, как в Dynamo-эталоне: имена задаются в настройках проекта (LSR_ может не быть),
    /// а у семейства, загруженного пользователем, GUID заранее неизвестен. Два параметра с одним именем — неоднозначно, не пишем.
    /// </summary>
    internal static class CalcParams
    {
        public const string Key = "SYS_Ключ";
        public const string Calc = "SYS_Кол-во расчёт";
        public const string Manual = "SYS_Кол-во вручную";
        public const string Mode = "SYS_Способ";
        public const string Basis = "SYS_Основание";
        public const string Quantity = "ADSK_Количество";
        public const string Name = "ADSK_Наименование";
        public const string Unit = "ADSK_Единица измерения";
        public const string Group = "ADSK_Группирование";

        /// <summary>Параметр по имени; null и причина, если его нет или их несколько.</summary>
        public static Parameter? Find(Element? element, string name, out string? problem)
        {
            problem = null;
            if (element == null || name.Length == 0)
            {
                problem = $"нет параметра «{name}»";
                return null;
            }
            IList<Parameter> found = element.GetParameters(name);
            if (found.Count == 1) return found[0];
            problem = found.Count == 0 ? $"нет параметра «{name}»" : $"у элемента {found.Count} параметра «{name}» — неоднозначно";
            return null;
        }

        public static bool Has(Element? element, string name) => element != null && name.Length > 0 && element.GetParameters(name).Count > 0;

        /// <summary>Текст параметра у экземпляра, а если там нет — у типа. Пусто, если нет нигде.</summary>
        public static string Text(Element element, string name)
        {
            Parameter? p = Find(element, name, out _) ?? Find(element.Document.GetElement(element.GetTypeId()), name, out _);
            if (p == null) return string.Empty;
            return p.StorageType == StorageType.String ? p.AsString() ?? string.Empty : p.HasValue ? p.AsValueString() ?? string.Empty : string.Empty;
        }

        /// <summary>Значение для копирования (поля фильтров спецификации); ссылки на элементы не копируются.</summary>
        public static ParamValue? Raw(Parameter p) => p.StorageType switch
        {
            StorageType.String => ParamValue.OfText(p.AsString()),
            StorageType.Integer => p.HasValue ? ParamValue.OfInteger(p.AsInteger(), p.AsValueString()) : null,
            StorageType.Double => p.HasValue ? ParamValue.OfDouble(p.AsDouble(), p.AsValueString()) : null,
            _ => null,
        };

        /// <summary>Ключ сравнения значений при подсчёте «самого частого» у труб системы.</summary>
        public static string KeyOf(ParamValue value) => value.Kind switch
        {
            ParamValueKind.Text => "T:" + value.Text,
            ParamValueKind.Integer => "I:" + value.Integer.ToString(CultureInfo.InvariantCulture),
            _ => "D:" + value.Number.ToString("R", CultureInfo.InvariantCulture),
        };

        /// <summary>
        /// Число без единиц? Иначе Revit примет кг за футы. Тип данных сравниваем по TypeId без версии: у ADSK_Количество
        /// в ФОП ADSK это «autodesk.spec.aec:number», а не SpecTypeId.Number («autodesk.spec:number») — оба «Число».
        /// </summary>
        public static bool IsPlainNumber(Parameter p)
        {
            if (p.StorageType != StorageType.Double) return true;
            try
            {
#if REVIT2022_OR_GREATER
                string id = p.Definition.GetDataType().TypeId ?? string.Empty;
#else
                string id = p.Definition.GetSpecTypeId().TypeId ?? string.Empty;
#endif
                return Regex.IsMatch(id, @"[:.]number(-|$)");
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return true;
            }
        }

        /// <summary>Длина (футы внутри Revit)? Тем же TypeId без версии: «autodesk.spec.aec:length».</summary>
        public static bool IsLength(Parameter p)
        {
            if (p.StorageType != StorageType.Double) return false;
            try
            {
#if REVIT2022_OR_GREATER
                string id = p.Definition.GetDataType().TypeId ?? string.Empty;
#else
                string id = p.Definition.GetSpecTypeId().TypeId ?? string.Empty;
#endif
                return Regex.IsMatch(id, @"[:.]length(-|$)");
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }

        /// <summary>Значение для записи в параметр этого типа хранения; null — не подходит (и причина).</summary>
        public static ParamValue? Convert(Parameter p, object value, out string? problem)
        {
            problem = null;
            switch (p.StorageType)
            {
                case StorageType.String:
                    return ParamValue.OfText(value is double d ? CalcTables.Format(d, 4) : value?.ToString() ?? string.Empty);
                case StorageType.Integer:
                    if (TryNumber(value, out double i)) return ParamValue.OfInteger((int)Math.Round(i), null);
                    break;
                case StorageType.Double:
                    if (!IsPlainNumber(p))
                    {
                        problem = $"«{p.Definition.Name}» должен иметь тип данных «Число», а не величину с единицами";
                        return null;
                    }
                    if (TryNumber(value, out double n)) return ParamValue.OfDouble(n, null);
                    break;
            }
            problem = $"«{p.Definition.Name}»: значение «{value}» не подходит к типу параметра";
            return null;
        }

        /// <summary>Совпадает ли текущее значение (числа — с точностью 1e-6).</summary>
        public static bool Same(Parameter p, ParamValue value)
        {
            ParamValue? current = Raw(p);
            if (current == null) return false;
            if (current.Kind == ParamValueKind.Double && value.Kind == ParamValueKind.Double) return Math.Abs(current.Number - value.Number) < 1e-6;
            return current.SameAs(value);
        }

        public static string Display(Parameter p)
        {
            if (p.StorageType == StorageType.String) return p.AsString() ?? string.Empty;
            if (!p.HasValue) return string.Empty;
            if (p.StorageType == StorageType.Double && IsPlainNumber(p)) return CalcTables.Format(p.AsDouble(), 4);
            return p.AsValueString() ?? string.Empty;
        }

        public static string Display(ParamValue value) => value.Kind switch
        {
            ParamValueKind.Text => value.Text,
            ParamValueKind.Integer => value.Integer.ToString(CultureInfo.InvariantCulture),
            _ => CalcTables.Format(value.Number, 4),
        };

        /// <summary>null — записано; иначе причина.</summary>
        public static string? Write(Parameter p, ParamValue value)
        {
            if (p.IsReadOnly) return $"«{p.Definition.Name}» только для чтения (формула или ассоциация)";
            try
            {
                bool ok = value.Kind switch
                {
                    ParamValueKind.Text when p.StorageType == StorageType.String => p.Set(value.Text),
                    ParamValueKind.Integer when p.StorageType == StorageType.Integer => p.Set(value.Integer),
                    ParamValueKind.Double when p.StorageType == StorageType.Double => p.Set(value.Number),
                    _ => false,
                };
                if (!ok) return $"«{p.Definition.Name}»: Revit отклонил запись";
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                return $"«{p.Definition.Name}»: Revit не дал записать: {ex.Message}";
            }
            return null;
        }

        private static bool TryNumber(object value, out double number)
        {
            number = 0;
            switch (value)
            {
                case double d:
                    number = d;
                    return true;
                case int i:
                    number = i;
                    return true;
                case string s:
                    try
                    {
                        double? v = CalcTables.ParseNumber(s, null);
                        if (!v.HasValue) return false;
                        number = v.Value;
                        return true;
                    }
                    catch (FormatException)
                    {
                        return false;
                    }
                default:
                    return false;
            }
        }

        /// <summary>Параметры проекта по имени: определение, привязка (экземпляр/тип), категории.</summary>
        public static Dictionary<string, List<(Definition Definition, ElementBinding Binding)>> ProjectBindings(Document doc)
        {
            var result = new Dictionary<string, List<(Definition, ElementBinding)>>(StringComparer.Ordinal);
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (!(it.Key is Definition definition) || !(it.Current is ElementBinding binding)) continue;
                if (!result.TryGetValue(definition.Name, out List<(Definition, ElementBinding)>? list))
                {
                    list = new List<(Definition, ElementBinding)>();
                    result[definition.Name] = list;
                }
                list.Add((definition, binding));
            }
            return result;
        }

        public static bool HasCategory(ElementBinding binding, BuiltInCategory category) =>
            binding.Categories.Cast<Category>().Any(c => c.ToBuiltInCategory() == category);
    }
}
