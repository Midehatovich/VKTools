using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Checks
{
    /// <summary>
    /// Параметры из ФОП есть в проекте с правильным GUID и назначены нужным категориям.
    /// Так ловится случай, когда изоляция выпадала из спецификации: у её категории не было ADSK_Этаж (REQ-VAL-003, REQ-VAL-006).
    /// </summary>
    internal sealed class ParameterBindingCheck : IModelCheck
    {
        public string Name => "Параметры проекта";

        public void Run(CheckContext context)
        {
            if (context.Parameters == null) return;
            Document doc = context.Document;

            // Привязки параметров проекта: Id элемента-параметра → привязка
            var bindings = new Dictionary<long, ElementBinding>();
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (it.Key is InternalDefinition def && it.Current is ElementBinding binding)
                    bindings[def.Id.ToLong()] = binding;
            }

            // Общие параметры собираем отдельно: не полагаемся на то, отдаёт ли OfClass наследников
            List<ParameterElement> allParameters = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterElement))
                .Cast<ParameterElement>()
                .Concat(new FilteredElementCollector(doc).OfClass(typeof(SharedParameterElement)).Cast<ParameterElement>())
                .GroupBy(p => p.Id.ToLong())
                .Select(g => g.First())
                .ToList();

            foreach (RequiredParameter required in context.Parameters)
            {
                SharedParameterElement? shared = SharedParameterElement.Lookup(doc, required.Guid);
                if (shared == null)
                {
                    ReportMissing(context, required, allParameters);
                    continue;
                }

                if (!bindings.TryGetValue(shared.Id.ToLong(), out ElementBinding? bound))
                {
                    context.Add(new CheckFinding(Name, Severity.Info,
                        $"«{required.Name}» есть только в семействах, как параметр проекта не добавлен.")
                    {
                        Current = "только в семействах",
                        Expected = "параметр проекта",
                        Action = "Если спецификации фильтруют по этому параметру — добавьте его: Управление → Параметры проекта.",
                    });
                    continue;
                }

                CheckBindingKind(context, required, bound);
                CheckCategories(context, required, bound, doc);
            }
        }

        private void ReportMissing(CheckContext context, RequiredParameter required, List<ParameterElement> all)
        {
            ParameterElement? sameName = all.FirstOrDefault(p =>
                string.Equals(p.GetDefinition()?.Name, required.Name, StringComparison.Ordinal));
            if (sameName is SharedParameterElement otherShared)
            {
                context.Add(new CheckFinding(Name, Severity.Error,
                    $"«{required.Name}» в проекте с другим GUID. Для спецификаций и плагина это разные параметры.")
                {
                    Current = otherShared.GuidValue.ToString(),
                    Expected = required.Guid.ToString(),
                    Action = "Замените параметр на версию из ФОП отдела (Управление → Параметры проекта) и перенесите значения.",
                });
                return;
            }
            if (sameName != null)
            {
                context.Add(new CheckFinding(Name, Severity.Error,
                    $"«{required.Name}» — параметр проекта, но не общий. Плагин и спецификации ВК ищут общий параметр из ФОП.")
                {
                    Current = "не общий",
                    Expected = "общий, GUID " + required.Guid,
                    Action = "Замените на общий параметр из ФОП отдела.",
                });
                return;
            }

            if (FoundOnElements(context, required.Guid))
            {
                // Параметр есть у элементов (из семейств), но элемента параметра в проекте не нашли — не пугаем ошибкой
                context.Add(new CheckFinding(Name, Severity.Info,
                    $"«{required.Name}» есть у элементов из семейств, как параметр проекта не добавлен.")
                {
                    Current = "только в семействах",
                    Expected = "параметр проекта",
                    Action = "Если спецификации фильтруют по этому параметру — добавьте его: Управление → Параметры проекта.",
                });
                return;
            }

            context.Add(new CheckFinding(Name, required.Mandatory ? Severity.Error : Severity.Info,
                required.Mandatory
                    ? $"Нет параметра «{required.Name}»."
                    : $"Нет параметра «{required.Name}» ({required.Purpose}). Нужен не во всех проектах.")
            {
                Current = "нет в проекте",
                Expected = "GUID " + required.Guid,
                Action = "Добавьте из ФОП отдела: Управление → Параметры проекта.",
            });
        }

        /// <summary>Есть ли параметр хотя бы у одного элемента категорий ВК (смотрим первые элементы каждой категории).</summary>
        private static bool FoundOnElements(CheckContext context, Guid guid)
        {
            const int probePerCategory = 200;
            foreach ((BuiltInCategory bic, _) in context.ResolvedVkCategories)
            {
                IEnumerable<Element> elements = new FilteredElementCollector(context.Document)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType()
                    .Take(probePerCategory);
                foreach (Element element in elements)
                {
                    if (element.GetText(guid) != null) return true;
                }
            }
            return false;
        }

        private void CheckBindingKind(CheckContext context, RequiredParameter required, ElementBinding bound)
        {
            bool isInstance = bound is InstanceBinding;
            if (required.Binding == BindingKind.Instance && !isInstance)
            {
                context.Add(new CheckFinding(Name, Severity.Warning,
                    $"«{required.Name}» добавлен как параметр типа, а нужен экземпляра: значения разных элементов одного типа не различить.")
                {
                    Current = "Тип",
                    Expected = "Экземпляр",
                    Action = "Управление → Параметры проекта → «" + required.Name + "» → Изменить → «Экземпляр».",
                });
            }
            else if (required.Binding == BindingKind.Type && isInstance)
            {
                context.Add(new CheckFinding(Name, Severity.Warning,
                    $"«{required.Name}» добавлен как параметр экземпляра, а нужен типа.")
                {
                    Current = "Экземпляр",
                    Expected = "Тип",
                    Action = "Управление → Параметры проекта → «" + required.Name + "» → Изменить → «Тип».",
                });
            }
        }

        private void CheckCategories(CheckContext context, RequiredParameter required, ElementBinding bound, Document doc)
        {
            var missing = new List<string>();
            foreach (string categoryName in required.Categories)
            {
                if (!CheckContext.TryResolveCategory(categoryName, out BuiltInCategory bic))
                {
                    context.Add(new CheckFinding("Правила VK Tools", Severity.Warning,
                        $"parameters.csv, строка {required.Line}: категория «{categoryName}» неизвестна этой версии Revit."));
                    continue;
                }
                Category? category = Category.GetCategory(doc, bic);
                if (category == null) continue;
                if (!bound.Categories.Contains(category)) missing.Add(category.Name);
            }

            if (missing.Count > 0)
            {
                context.Add(new CheckFinding(Name, Severity.Warning,
                    $"«{required.Name}» не назначен категориям: {string.Join(", ", missing)}. "
                    + "Элементы этих категорий выпадут из спецификаций, которые фильтруют или сортируют по этому параметру.")
                {
                    Current = "нет у: " + string.Join(", ", missing),
                    Expected = "назначен",
                    Action = "Управление → Параметры проекта → «" + required.Name + "» → Изменить → отметьте категории.",
                });
            }
        }
    }
}
