using System.Collections.Generic;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Checks
{
    /// <summary>Пустое ADSK_Наименование у элементов, которые попадают в спецификации ВК: пустая строка в спецификации.</summary>
    internal sealed class EmptyNameCheck : IModelCheck
    {
        public string Name => "Пустое ADSK_Наименование";

        public void Run(CheckContext context)
        {
            var limiter = new FindingLimiter(context, Name);
            var withoutParameter = new Dictionary<string, int>();
            foreach ((BuiltInCategory bic, string categoryName) in context.ResolvedVkCategories)
            {
                var elements = new FilteredElementCollector(context.Document)
                    .OfCategory(bic)
                    .WhereElementIsNotElementType();
                foreach (Element element in elements)
                {
                    string? value = element.GetText(KnownParameters.AdskName.Guid);
                    if (value == null)
                    {
                        withoutParameter.TryGetValue(categoryName, out int n);
                        withoutParameter[categoryName] = n + 1;
                        continue;
                    }
                    if (value.Trim().Length > 0) continue;
                    limiter.Add(new CheckFinding(Name, Severity.Warning, "ADSK_Наименование пустое: в спецификации будет пустая строка.")
                    {
                        Current = value.Length == 0 ? "(пусто)" : "(только пробелы)",
                        Action = "Заполните наименование в типе или экземпляре (для труб и изоляции — проверьте правило BIM2B или скрипт).",
                    }.Describe(element));
                }
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
    }
}
