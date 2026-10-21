using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Core.Calc;
using VKTools.Core.Logging;
using VKTools.Revit.Calc;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.UI;

namespace VKTools.Revit.Commands
{
    /// <summary>
    /// «Расчётные позиции» (Р3): настройка из модели (или по умолчанию) → расчёт → окно. Сама команда только считает;
    /// пробный прогон, запись и сохранение настройки идут из окна через ExternalEvent.
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class CalcItemsCommand : IExternalCommand
    {
        private const string Title = "Расчётные позиции";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            Document doc = app.ActiveUIDocument.Document;
            try
            {
                Host.Log.Info($"{Title}: расчёт «{doc.Title}».");
                var timings = new OperationTimings { Trace = line => Host.Log.Info("  этап " + line) };
                (CalcOptions options, CalcDefaults defaults) = CalcSettingsLoader.Load(doc);
                CalcPlan plan;
                using (var progress = new ProgressScope(app, "расчёт: " + Title))
                {
                    plan = CalcPlanner.Build(doc, options, defaults, progress, timings);
                }
                CalcHandler.LogPlan(doc, plan, timings);
                CalcHost.Show(app, doc, options, defaults, plan);
                return Result.Succeeded;
            }
            catch (OperationCanceledException)
            {
                Host.Log.Info($"{Title}: расчёт отменён пользователем.");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Host.Log.Error($"{Title} «{doc.Title}»: расчёт прерван.", ex);
                TaskDialog.Show("VK Tools", "Расчёт прерван: " + ex.Message + "\n\nМодель не изменялась. Подробности в логе.");
                return Result.Failed;
            }
        }
    }
}
