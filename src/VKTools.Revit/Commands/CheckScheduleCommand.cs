using System;
using System.Collections.Generic;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Core.Checks;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Schedules;
using VKTools.Revit.UI;

namespace VKTools.Revit.Commands
{
    /// <summary>
    /// «Проверить спецификацию» (0.17.0): «‹варианты›» с причиной, пустые графы, ноль после округления, нумерация,
    /// единицы, элементы вне спецификаций. Модель не меняется: таблицы читаются в транзакции, которая всегда откатывается
    /// (0.17.3: в режиме ReadOnly Revit не отдаёт текст ячеек — «Changes are disabled», и проверка порядка номеров молчала).
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class CheckScheduleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc.Document;
            try
            {
                var active = uidoc.ActiveView as ViewSchedule;
                if (!ScheduleAuditRunner.Checkable(active)) active = null;
                List<ViewSchedule> issued = ScheduleAuditRunner.Issued(doc);
                if (active == null && issued.Count == 0)
                {
                    TaskDialog.Show("VK Tools", "Проверять нечего: нет открытой спецификации и нет оформленных спецификаций "
                        + "(«ADSK_Назначение вида» = «Оформленные спецификации» или созданных кнопкой «Создать спецификацию»).\n\n"
                        + "Откройте спецификацию и запустите проверку снова.");
                    return Result.Cancelled;
                }

                var dialog = new TaskDialog("VK Tools: проверить спецификацию")
                {
                    MainInstruction = "Что проверить?",
                    MainContent = "Только чтение: модель не меняется. В отчёте «Выделить» показывает элементы каждой строки.",
                    CommonButtons = TaskDialogCommonButtons.Cancel,
                };
                if (active != null) dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Открытую спецификацию", $"«{active.Name}»");
                if (issued.Count > 0)
                {
                    dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, $"Все оформленные спецификации ({issued.Count})",
                        "Плюс элементы ВК, которые не попали ни в одну из них, и элементы сразу в двух.");
                }
                TaskDialogResult choice = dialog.Show();
                List<ViewSchedule> chosen;
                if (choice == TaskDialogResult.CommandLink1 && active != null) chosen = new List<ViewSchedule> { active };
                else if (choice == TaskDialogResult.CommandLink2) chosen = issued;
                else return Result.Cancelled;

                Host.Log.Info($"Проверка спецификаций «{doc.Title}»: {chosen.Count} шт.");
                CheckReport report;
                using (var read = new Transaction(doc, "VK Tools: чтение спецификаций"))
                {
                    bool opened = !doc.IsReadOnly && !doc.IsModifiable && read.Start() == TransactionStatus.Started;
                    try
                    {
                        report = ScheduleAuditRunner.Run(doc, chosen, checkOutside: choice == TaskDialogResult.CommandLink2);
                    }
                    finally
                    {
                        if (opened) read.RollBack();
                    }
                }
                Host.Log.Info($"Проверка спецификаций «{doc.Title}» завершена: {report.Summary}");
                ReportWindowHost.Show(report, doc, app);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Host.Log.Error($"Проверка спецификаций «{doc.Title}» прервана.", ex);
                TaskDialog.Show("VK Tools", "Проверка прервана: " + ex.Message + "\n\nМодель не изменялась. Подробности в логе.");
                return Result.Failed;
            }
        }
    }
}
