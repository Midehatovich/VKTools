using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Core.Checks;
using VKTools.Core.Logging;
using VKTools.Core.Mep;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Mep;
using VKTools.Revit.UI;

namespace VKTools.Revit.Commands
{
    /// <summary>«Разрывы стояков». ReadOnly: только поиск и показ, модель не меняется.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class FindRiserGapsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            Document doc = app.ActiveUIDocument.Document;
            var timings = new OperationTimings { Trace = line => Host.Log.Info("  этап " + line) };
            try
            {
                Host.Log.Info($"Разрывы стояков: поиск в «{doc.Title}».");
                CheckReport report;
                using (var progress = new ProgressScope(app, "поиск разрывов стояков"))
                {
                    report = RiserGapScanner.Run(doc, progress, timings, new RiserGapOptions());
                }
                Host.Log.Info($"Разрывы стояков: {report.Count(Severity.Warning)} в «{doc.Title}». Время: {timings.Summary()}");
                ReportWindowHost.Show(report, doc, app);
                return Result.Succeeded;
            }
            catch (OperationCanceledException)
            {
                Host.Log.Info("Разрывы стояков: поиск отменён пользователем.");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Host.Log.Error($"Разрывы стояков «{doc.Title}»: поиск прерван. Время: {timings.Summary()}", ex);
                TaskDialog.Show("VK Tools", "Поиск прерван: " + ex.Message + "\n\nМодель не изменялась. Подробности в логе.");
                return Result.Failed;
            }
        }
    }
}
