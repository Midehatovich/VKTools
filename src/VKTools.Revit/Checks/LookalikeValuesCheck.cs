using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Text;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Checks
{
    /// <summary>
    /// Разные написания одного значения: «B1» латиницей и «В1», «02 Этаж» и «02 этаж», лишние пробелы.
    /// В спецификации они дают разные строки или отсекаются фильтром, а на глаз неотличимы (REQ-VAL-004).
    /// Какое написание правильное, плагин не решает: показывает самое частое и просит проверить.
    /// </summary>
    internal sealed class LookalikeValuesCheck : IModelCheck
    {
        public string Name => "Разные написания одного значения";

        public void Run(CheckContext context)
        {
            List<ParameterRef> parameters = context.LookalikeParameters ?? new List<ParameterRef>();
            foreach (ParameterRef parameter in parameters)
            {
                var elementsByValue = new Dictionary<string, List<long>>();
                foreach ((BuiltInCategory bic, _) in context.ResolvedVkCategories)
                {
                    var elements = new FilteredElementCollector(context.Document)
                        .OfCategory(bic)
                        .WhereElementIsNotElementType();
                    foreach (Element element in elements)
                    {
                        string? value = element.GetText(parameter.Guid);
                        if (string.IsNullOrWhiteSpace(value)) continue;
                        if (!elementsByValue.TryGetValue(value!, out List<long>? ids))
                        {
                            ids = new List<long>();
                            elementsByValue.Add(value!, ids);
                        }
                        ids.Add(element.Id.ToLong());
                    }
                }

                var reported = new HashSet<string>();
                var counts = elementsByValue.SelectMany(kv => Enumerable.Repeat(kv.Key, kv.Value.Count));
                foreach (LookalikeGroup group in ValueNormalizer.FindLookalikes(counts))
                {
                    LookalikeVariant top = group.Variants[0];
                    foreach (LookalikeVariant variant in group.Variants.Skip(1))
                    {
                        reported.Add(variant.Value);
                        AddFinding(context, parameter, variant.Value, elementsByValue[variant.Value],
                            $"«{Visible(variant.Value)}» ({variant.Count} шт.) выглядит как «{Visible(top.Value)}» ({top.Count} шт.), но для Revit это разные значения.");
                    }
                    reported.Add(top.Value);
                }

                foreach (KeyValuePair<string, List<long>> kv in elementsByValue)
                {
                    if (reported.Contains(kv.Key)) continue;
                    if (ValueNormalizer.HasMixedScripts(kv.Key))
                    {
                        AddFinding(context, parameter, kv.Key, kv.Value,
                            $"В «{Visible(kv.Key)}» смешаны латинские и русские буквы ({kv.Value.Count} шт.).");
                    }
                    else if (ValueNormalizer.HasExtraWhitespace(kv.Key))
                    {
                        AddFinding(context, parameter, kv.Key, kv.Value,
                            $"В «{Visible(kv.Key)}» лишние пробелы ({kv.Value.Count} шт.).");
                    }
                }
            }
        }

        private void AddFinding(CheckContext context, ParameterRef parameter, string value, List<long> ids, string message)
        {
            var finding = new CheckFinding(Name, Severity.Warning, $"{parameter.Name}: {message}")
            {
                ElementId = ids[0],
                Current = Visible(value),
                Action = "Проверьте и приведите к одному написанию. «Показать в модели» выделит все такие элементы.",
            };
            finding.RelatedElementIds.AddRange(ids);
            context.Add(finding);
        }

        private static string Visible(string value) => ValueNormalizer.MakeVisible(value);
    }
}
