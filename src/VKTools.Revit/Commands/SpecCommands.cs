using System;
using System.Collections.Generic;
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
    /// <summary>«Перенос во вложенные» (Р2): значения родителя → общие вложенные, параметры выбираются в окне.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class NestedTransferCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            SpecLauncher.Run(commandData.Application, SpecModule.Nested);
    }

    /// <summary>«Группирование» (Р2): ADSK_Группирование по таблице групп проекта и комплекты шкафов.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class GroupingCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            SpecLauncher.Run(commandData.Application, SpecModule.Grouping);
    }

    /// <summary>«Система и этаж» (Р4): параметр системы из сокращения, этаж из уровня, изоляция от основы, затем перенос во вложенные.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class SystemFloorCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            SpecLauncher.Run(commandData.Application, SpecModule.SystemFloor);
    }

    /// <summary>«Изоляция» (Р4): Dнар трубы, наименование и параметры изделия по таблице «Изделия» проекта.</summary>
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class InsulationCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements) =>
            SpecLauncher.Run(commandData.Application, SpecModule.Insulation);
    }

    /// <summary>
    /// Общий запуск модулей Р2: настройки из модели (или по умолчанию) → анализ → окно. Сама команда только анализирует;
    /// пробный прогон, запись и сохранение настройки идут из окна через ExternalEvent.
    /// </summary>
    internal static class SpecLauncher
    {
        public static Result Run(UIApplication app, SpecModule module)
        {
            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc.Document;
            string title = module.Title();
            try
            {
                Host.Log.Info($"{title}: анализ «{doc.Title}».");
                var timings = new OperationTimings { Trace = line => Host.Log.Info("  этап " + line) };
                (SpecOptions options, SpecDefaults defaults) = SpecSettingsLoader.Load(doc, module);
                options.Scope = SpecParamsHost.LastScope(module);
                List<ElementId>? scope = SpecScope.Resolve(uidoc, options, out string scopeLabel);
                ParamPlan plan;
                using (var progress = new ProgressScope(app, "анализ: " + title))
                {
                    plan = SpecPlanner.Build(doc, options, defaults, scope, progress, timings);
                }
                SpecParamsHandler.LogPlan(doc, plan, timings);
                SpecParamsHost.Show(app, doc, options, defaults, plan, scope, scopeLabel);
                return Result.Succeeded;
            }
            catch (OperationCanceledException)
            {
                Host.Log.Info($"{title}: анализ отменён пользователем.");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                Host.Log.Error($"{title} «{doc.Title}»: анализ прерван.", ex);
                TaskDialog.Show("VK Tools", "Анализ прерван: " + ex.Message + "\n\nМодель не изменялась. Подробности в логе.");
                return Result.Failed;
            }
        }
    }
}
