using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Core.Checks;
using VKTools.Revit.Checks;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.UI;

namespace VKTools.Revit.Commands
{
    /// <summary>
    /// «Проверить модель». ReadOnly: Revit сам не даст этой команде открыть транзакцию,
    /// так что изменить модель она не может даже по ошибке в коде.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class CheckModelCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            Document doc = app.ActiveUIDocument.Document;
            try
            {
                Host.Log.Info($"Проверка модели «{doc.Title}» начата.");
                var runner = new ModelCheckRunner(Host.CreateConfigLoader(Host.LoadSettings()), Host.Log);
                CheckReport report = runner.Run(doc);
                Host.Log.Info($"Проверка модели «{doc.Title}» завершена: {report.Summary}");
                ReportWindowHost.Show(report, doc, app);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Host.Log.Error($"Проверка модели «{doc.Title}» прервана.", ex);
                TaskDialog.Show("VK Tools", "Проверка прервана: " + ex.Message + "\n\nМодель не изменялась. Подробности в логе.");
                return Result.Failed;
            }
        }
    }
}
