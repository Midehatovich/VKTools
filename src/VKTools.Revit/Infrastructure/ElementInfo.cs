using System;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Infrastructure
{
    /// <summary>Категория, семейство и тип элемента для строки отчёта.</summary>
    internal static class ElementInfo
    {
        public static CheckFinding Describe(this CheckFinding finding, Element element)
        {
            finding.ElementId = element.Id.ToLong();
            finding.Category = element.Category?.Name ?? string.Empty;
            ElementType? type = element.Document.GetElement(element.GetTypeId()) as ElementType;
            finding.Family = type?.FamilyName ?? string.Empty;
            finding.Type = type?.Name ?? string.Empty;
            return finding;
        }

        /// <summary>
        /// Значение параметра по GUID как текст: у экземпляра, а если его нет — у типа.
        /// null — параметра нет ни там, ни там.
        /// </summary>
        public static string? GetText(this Element element, Guid guid, bool includeType = true)
        {
            Parameter? p = element.get_Parameter(guid);
            if (p == null && includeType)
            {
                Element? type = element.Document.GetElement(element.GetTypeId());
                p = type?.get_Parameter(guid);
            }
            if (p == null) return null;
            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.AsString() ?? string.Empty;
                case StorageType.None:
                    return string.Empty;
                default:
                    return p.HasValue ? p.AsValueString() ?? string.Empty : string.Empty;
            }
        }
    }
}
