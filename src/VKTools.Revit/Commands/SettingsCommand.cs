using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.UI;

namespace VKTools.Revit.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class SettingsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var window = new SettingsWindow(Host.LoadSettings());
            window.AttachTo(commandData.Application.MainWindowHandle);
            window.ShowDialog();
            return Result.Succeeded;
        }
    }
}
