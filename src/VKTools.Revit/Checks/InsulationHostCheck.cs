using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Checks
{
    /// <summary>
    /// Изоляция должна иметь те же этаж и систему, что труба или фитинг, на котором она лежит.
    /// Иначе в спецификации по этажам и системам изоляция уходит в чужую строку.
    /// </summary>
    internal sealed class InsulationHostCheck : IModelCheck
    {
        public string Name => "Изоляция и основа";

        public void Run(CheckContext context)
        {
            List<ParameterRef> parameters = context.InsulationFromHost ?? new List<ParameterRef>();
            var limiter = new FindingLimiter(context, Name);
            Document doc = context.Document;

            var insulations = new FilteredElementCollector(doc).OfClass(typeof(PipeInsulation));
            foreach (Element element in insulations)
            {
                var insulation = (PipeInsulation)element;
                Element? host = doc.GetElement(insulation.HostElementId);
                if (host == null)
                {
                    limiter.Add(new CheckFinding(Name, Severity.Warning, "Изоляция без основы: значения брать неоткуда.")
                    {
                        Action = "Удалите изоляцию или наложите её заново на трубу.",
                    }.Describe(insulation));
                    continue;
                }

                foreach (ParameterRef parameter in parameters)
                {
                    string? hostValue = host.GetText(parameter.Guid, includeType: false);
                    if (string.IsNullOrWhiteSpace(hostValue)) continue;
                    string? value = insulation.GetText(parameter.Guid, includeType: false);
                    if (value == null) continue; // нет параметра у категории — покажет проверка «Параметры проекта»
                    if (string.Equals(value, hostValue, StringComparison.Ordinal)) continue;

                    limiter.Add(new CheckFinding(Name, Severity.Warning,
                        $"{parameter.Name} у изоляции не совпадает с основой (Id {host.Id.ToLong()}).")
                    {
                        Current = value.Length == 0 ? "(пусто)" : value,
                        Expected = hostValue!,
                        Action = "Перенесите значения на изоляцию (скрипт NestedParamSync, позже — модуль «Изоляция» VK Tools).",
                    }.Describe(insulation));
                }
            }
            limiter.Complete();
        }
    }
}
