using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Spec
{
    /// <summary>
    /// Чтение и запись параметра по <see cref="ParamKey"/>: общий — по GUID, параметр проекта — по его определению,
    /// встроенный — только «Комментарии». Параметры семейств (не общие) сюда не попадают: в спецификацию их не вывести.
    /// </summary>
    internal sealed class ParamAccess
    {
        private const BuiltInParameter Comments = BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS;

        private readonly HashSet<long> _projectIds = new HashSet<long>();
        private readonly Dictionary<string, Definition> _projectByName = new Dictionary<string, Definition>(StringComparer.Ordinal);

        public ParamAccess(Document doc)
        {
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (!(it.Key is InternalDefinition definition)) continue;
                if (doc.GetElement(definition.Id) is ParameterElement element && !(element is SharedParameterElement))
                {
                    _projectIds.Add(definition.Id.ToLong());
                    if (!_projectByName.ContainsKey(definition.Name)) _projectByName[definition.Name] = definition;
                }
            }
        }

        /// <summary>Ключ параметра; null — параметр семейства или встроенный (кроме «Комментарии»).</summary>
        public ParamKey? KeyOf(Parameter p)
        {
            Definition? definition = p.Definition;
            if (definition == null) return null;
            if (p.IsShared) return ParamKey.Shared(definition.Name, p.GUID);
            if (definition is InternalDefinition internalDefinition)
            {
                if (internalDefinition.BuiltInParameter == Comments) return ParamKey.OfBuiltIn(definition.Name, Comments.ToString());
                if (internalDefinition.BuiltInParameter == BuiltInParameter.INVALID && _projectIds.Contains(p.Id.ToLong())) return ParamKey.Project(definition.Name);
            }
            return null;
        }

        public Parameter? Find(Element element, ParamKey key)
        {
            switch (key.Kind)
            {
                case ParamKeyKind.Shared:
                    return element.get_Parameter(key.Guid);
                case ParamKeyKind.Project:
                    return _projectByName.TryGetValue(key.Name, out Definition? definition) ? element.get_Parameter(definition) : null;
                default:
                    return Enum.TryParse(key.BuiltIn, out BuiltInParameter bip) ? element.get_Parameter(bip) : null;
            }
        }

        /// <summary>Переносимый тип хранения: текст, целое, число. Ссылки на элементы не переносим.</summary>
        public static bool IsTransferable(Parameter p) =>
            p.StorageType == StorageType.String || p.StorageType == StorageType.Integer || p.StorageType == StorageType.Double;

        /// <summary>Значение; null — у числа нет значения. Текст возвращается всегда (пустой — "").</summary>
        public static ParamValue? Read(Parameter p)
        {
            switch (p.StorageType)
            {
                case StorageType.String:
                    return ParamValue.OfText(p.AsString());
                case StorageType.Integer:
                    return p.HasValue ? ParamValue.OfInteger(p.AsInteger(), p.AsValueString()) : null;
                case StorageType.Double:
                    return p.HasValue ? ParamValue.OfDouble(p.AsDouble(), p.AsValueString()) : null;
                default:
                    return null;
            }
        }

        public static bool IsEmpty(ParamValue? value) => value == null || (value.Kind == ParamValueKind.Text && ValueText.IsBlank(value.Text));

        /// <summary>null — записано и читается то же; иначе причина.</summary>
        public static string? Write(Parameter p, ParamValue value)
        {
            if (p.IsReadOnly) return "параметр стал только для чтения";
            try
            {
                bool ok;
                switch (value.Kind)
                {
                    case ParamValueKind.Text when p.StorageType == StorageType.String:
                        ok = p.Set(value.Text);
                        break;
                    case ParamValueKind.Integer when p.StorageType == StorageType.Integer:
                        ok = p.Set(value.Integer);
                        break;
                    case ParamValueKind.Double when p.StorageType == StorageType.Double:
                        ok = p.Set(value.Number);
                        break;
                    default:
                        return "тип параметра изменился";
                }
                if (!ok) return "Revit отклонил запись";
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                return "Revit не дал записать: " + ex.Message;
            }
            ParamValue? back = Read(p);
            return back != null && back.SameAs(value) ? null : "Revit записал другое значение: «" + (back?.Display ?? string.Empty) + "»";
        }

        /// <summary>Тип данных для окна: «Текст», «Длина», «Да/Нет»…</summary>
        public static string DataTypeLabel(Parameter p)
        {
            try
            {
#if REVIT2022_OR_GREATER
                ForgeTypeId type = p.Definition.GetDataType();
#else
                ForgeTypeId type = p.Definition.GetSpecTypeId();
#endif
                string label = LabelUtils.GetLabelForSpec(type);
                if (!string.IsNullOrEmpty(label)) return label;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Не спецификация (тип семейства и т. п.) — ниже подпись по хранению
            }
            return p.StorageType switch
            {
                StorageType.String => "Текст",
                StorageType.Integer => "Целое",
                StorageType.Double => "Число",
                StorageType.ElementId => "Ссылка на элемент",
                _ => "?",
            };
        }
    }
}
