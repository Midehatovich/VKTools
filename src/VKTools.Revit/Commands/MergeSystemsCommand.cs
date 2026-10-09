using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Core.Logging;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Mep;
using VKTools.Revit.UI;

namespace VKTools.Revit.Commands
{
    /// <summary>
    /// «Объединить системы»: окно «было → станет» по всем копиям систем трубопроводов.
    /// Сама команда только анализирует; пробный прогон и запись идут из окна через ExternalEvent.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class MergeSystemsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            Document doc = app.ActiveUIDocument.Document;
            try
            {
                Host.Log.Info($"Объединение систем: анализ «{doc.Title}».");
                var timings = new OperationTimings { Trace = line => Host.Log.Info("  этап " + line) };
                MergePlan plan;
                using (var progress = new ProgressScope(app, "анализ систем"))
                {
                    plan = SystemMergePlanner.Build(doc, progress, timings);
                }
                MergeSystemsHandler.LogPlan(doc, plan, timings);
                MergeWindowHost.Show(app, doc, plan);
                return Result.Succeeded;
            }
            catch (OperationCanceledException)
            {
                Host.Log.Info("Объединение систем: анализ отменён пользователем.");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Host.Log.Error($"Объединение систем «{doc.Title}»: анализ прерван.", ex);
                TaskDialog.Show("VK Tools", "Анализ прерван: " + ex.Message + "\n\nМодель не изменялась. Подробности в логе.");
                return Result.Failed;
            }
        }
    }
}
