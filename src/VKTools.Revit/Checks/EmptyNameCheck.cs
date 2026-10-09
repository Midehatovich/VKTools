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
    /// Пустое ADSK_Наименование у элементов, которые попадают в спецификации ВК: пустая строка в спецификации.
    /// Одна строка отчёта на типоразмер (не на каждый элемент): 500 одинаковых кранов — одна строка,
    /// «Показать в модели» выделяет все.
    /// </summary>
    internal sealed class EmptyNameCheck : IModelCheck
    {
        private const string TargetName = "ADSK_Наименование";

        public string Name => "Пустое ADSK_Наименование";

        public void Run(CheckContext context)
        {
            Guid guid = KnownParameters.AdskName.Guid;
            var groups = new Dictionary<ElementId, Group>();
            var withoutParameter = new Dictionary<string, int>();

            foreach ((BuiltInCategory bic, string categoryName) in context.ResolvedVkCategories)
            {
                var elements = new FilteredElementCollector(context.Document)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType();
                foreach (Element element in elements)
                {
                    // Пусто, только если пусто и у экземпляра, и у типа
                    string? instanceValue = element.GetText(guid, includeType: false);
                    Element? type = context.Document.GetElement(element.GetTypeId());
                    string? typeValue = type?.GetText(guid, includeType: false);
                    if (instanceValue == null && typeValue == null)
                    {
                        withoutParameter.TryGetValue(categoryName, out int n);
                        withoutParameter[categoryName] = n + 1;
                        continue;
                    }
                    if (!IsBlank(instanceValue) || !IsBlank(typeValue)) continue;

                    ElementId key = element.GetTypeId();
                    if (!groups.TryGetValue(key, out Group? group))
                    {
                        group = new Group(element);
                        groups.Add(key, group);
                    }
                    group.Ids.Add(element.Id.ToLong());
                }
            }

            var limiter = new FindingLimiter(context, Name);
            foreach (Group group in groups.Values.OrderByDescending(g => g.Ids.Count))
            {
                string? hint = WhereIsTheName(group.First, context.Document, guid, out bool otherGuid);
                var finding = new CheckFinding(Name, otherGuid ? Severity.Error : Severity.Warning,
                    otherGuid
                        ? $"{group.Ids.Count} шт.: наименование записано в «{TargetName}» с другим GUID. Спецификация ВК его не видит."
                        : $"{group.Ids.Count} шт.: ADSK_Наименование пустое — в спецификации будет пустая строка.")
                {
                    Current = hint ?? "(пусто)",
                    Action = otherGuid
                        ? "Замените в семействе параметр на ADSK_Наименование из ФОП отдела."
                        : "Заполните наименование в типе или экземпляре (для труб и изоляции — проверьте правило BIM2B или скрипт).",
                }.Describe(group.First);
                finding.RelatedElementIds.AddRange(group.Ids);
                limiter.Add(finding);
            }
            limiter.Complete();

            foreach (KeyValuePair<string, int> kv in withoutParameter)
            {
                context.Add(new CheckFinding(Name, Severity.Info,
                    $"{kv.Key}: у {kv.Value} элементов нет параметра ADSK_Наименование ни в экземпляре, ни в типе.")
                {
                    Action = "Если эти элементы должны быть в спецификации — проверьте привязку параметра (строки «Параметры проекта»).",
                });
            }
        }

        private static bool IsBlank(string? value) => value == null || value.Trim().Length == 0;

        /// <summary>
        /// Подсказка, где у элемента лежит наименование, если не в ADSK_Наименование из ФОП:
        /// параметры с «наименование» в имени, заполненные у экземпляра или типа.
        /// </summary>
        private static string? WhereIsTheName(Element element, Document doc, Guid guid, out bool otherGuid)
        {
            otherGuid = false;
            var found = new List<string>();
            Element? type = doc.GetElement(element.GetTypeId());
            foreach (Element owner in type == null ? new[] { element } : new[] { element, type })
            {
                foreach (Parameter p in owner.Parameters)
                {
                    string name = p.Definition?.Name ?? string.Empty;
                    if (name.IndexOf("аименовани", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (p.IsShared && p.GUID == guid) continue;
                    if (p.StorageType != StorageType.String) continue;
                    string value = p.AsString() ?? string.Empty;
                    if (value.Trim().Length == 0) continue;
                    if (name == TargetName) otherGuid = true;
                    found.Add($"пусто; «{name}» = {Shorten(value)}");
                    if (found.Count == 2) return string.Join("; ", found);
                }
            }
            return found.Count == 0 ? null : string.Join("; ", found);
        }

        private static string Shorten(string value) => value.Length <= 80 ? value : value.Substring(0, 77) + "...";

        private sealed class Group
        {
            public Group(Element first)
            {
                First = first;
            }

            public Element First { get; }

            public List<long> Ids { get; } = new List<long>();
        }
    }
}
