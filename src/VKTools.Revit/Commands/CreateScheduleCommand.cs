using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Schedules;
using VKTools.Revit.UI;

namespace VKTools.Revit.Commands
{
    /// <summary>
    /// «Создать спецификацию» (Р6): окно выбора систем и этажей → новая спецификация (форма ГОСТ или копия эталона)
    /// с фильтрами, которые ставит плагин; или обновление фильтров созданных ранее. Элементы модели не меняются, только виды.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class CreateScheduleCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            UIDocument uidoc = app.ActiveUIDocument;
            Document doc = uidoc.Document;
            try
            {
                ScheduleModelData data = ScheduleModel.Collect(doc);
                Host.Log.Info($"Создать спецификацию «{doc.Title}»: элементов ВК {data.Elements}, систем {data.Systems.Count}, этажей {data.Floors.Count}, "
                    + $"без системы {data.WithoutSystem}, без этажа {data.WithoutFloor}, созданных кнопкой {data.Own.Count}.");
                var window = new CreateScheduleWindow(doc.Title, data);
                window.AttachTo(app.MainWindowHandle);
                if (window.ShowDialog() != true || window.Action == ScheduleAction.None) return Result.Cancelled;
                if (doc.IsReadOnly)
                {
                    TaskDialog.Show("VK Tools", "Документ только для чтения: спецификацию не создать.");
                    return Result.Cancelled;
                }

                if (window.Action == ScheduleAction.Create)
                {
                    ScheduleResult result = ScheduleBuilder.Create(doc, data, window.Request);
                    if (result.Error != null)
                    {
                        Host.Log.Warn("Создать спецификацию: " + result.Error);
                        TaskDialog.Show("VK Tools", "Спецификация не создана: " + result.Error + ".\n\nМодель не изменялась.");
                        return Result.Failed;
                    }
                    Host.Log.Info($"Создана спецификация «{window.Request.Name}»: " + string.Join(" ", result.Lines) + " " + string.Join("; ", result.Notes));
                    if (result.ScheduleId.HasValue && doc.GetElement(result.ScheduleId.Value.ToElementId()) is View view)
                    {
                        uidoc.ActiveView = view;
                    }
                    Show($"Создана «{window.Request.Name}».", result);
                    return Result.Succeeded;
                }

                var lines = new List<string>();
                int ok = 0;
                foreach (long id in window.UpdateIds)
                {
                    string name = doc.GetElement(id.ToElementId())?.Name ?? id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    ScheduleResult result = ScheduleBuilder.Update(doc, data, id);
                    if (result.Error != null)
                    {
                        lines.Add($"«{name}»: не обновлена — {result.Error}.");
                        continue;
                    }
                    ok++;
                    lines.Add($"«{name}»: " + string.Join(" ", result.Lines) + (result.Notes.Count > 0 ? " " + string.Join("; ", result.Notes) + "." : string.Empty));
                }
                Host.Log.Info("Обновить фильтры спецификаций: " + string.Join(" | ", lines));
                TaskDialog.Show("VK Tools", $"Обновлено спецификаций: {ok} из {window.UpdateIds.Count}.\n\n" + string.Join("\n\n", lines));
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Host.Log.Error($"Создать спецификацию «{doc.Title}»: ошибка.", ex);
                TaskDialog.Show("VK Tools", "Ошибка: " + ex.Message + "\n\nПодробности в логе.");
                return Result.Failed;
            }
        }

        private static void Show(string head, ScheduleResult result)
        {
            var text = new List<string> { head };
            text.AddRange(result.Lines);
            if (result.Notes.Count > 0) text.Add("Заметки: " + string.Join("; ", result.Notes) + ".");
            text.Add("Расчётные позиции попадут в неё после «Расчётные позиции» с отмеченной этой спецификацией.");
            TaskDialog.Show("VK Tools", string.Join("\n\n", text.Where(t => t.Length > 0)));
        }
    }
}
