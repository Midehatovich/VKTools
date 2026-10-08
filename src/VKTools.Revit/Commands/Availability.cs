using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace VKTools.Revit.Commands
{
    /// <summary>Кнопка активна, когда открыт проект (не семейство).</summary>
    public sealed class ProjectDocumentAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories)
        {
            Document? doc = applicationData.ActiveUIDocument?.Document;
            return doc != null && !doc.IsFamilyDocument;
        }
    }

    /// <summary>Кнопка активна всегда, в том числе без открытых документов.</summary>
    public sealed class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
    }
}
