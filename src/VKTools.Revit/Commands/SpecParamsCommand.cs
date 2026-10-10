using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Core.Logging;
using VKTools.Core.Spec;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Spec;
using VKTools.Revit.UI;

namespace VKTools.Revit.Commands
{
    /// <summary>
    /// «Параметры спецификации» (Р2): вложенные семейства и группирование. Сама команда только анализирует;
    /// пробный прогон и запись идут из окна через ExternalEvent.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class SpecParamsCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            Document doc = app.ActiveUIDocument.Document;
            try
            {
                Host.Log.Info($"Параметры спецификации: анализ «{doc.Title}».");
                var timings = new OperationTimings { Trace = line => Host.Log.Info("  этап " + line) };
                ParamPlan plan;
                using (var progress = new ProgressScope(app, "анализ параметров"))
                {
                    plan = SpecPlanner.Build(doc, SpecParamsHost.LastModules, progress, timings);
                }
                SpecParamsHandler.LogPlan(doc, plan, timings);
                SpecParamsHost.Show(app, doc, plan);
                return Result.Succeeded;
            }
            catch (OperationCanceledException)
            {
                Host.Log.Info("Параметры спецификации: анализ отменён пользователем.");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Host.Log.Error($"Параметры спецификации «{doc.Title}»: анализ прерван.", ex);
                TaskDialog.Show("VK Tools", "Анализ прерван: " + ex.Message + "\n\nМодель не изменялась. Подробности в логе.");
                return Result.Failed;
            }
        }
    }
}
