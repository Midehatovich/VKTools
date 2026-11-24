using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Core.Config;
using VKTools.Core.Logging;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;
using VKTools.Core.Schedules;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Schedules;
using VKTools.Revit.Spec;
using VKTools.Revit.UI;

namespace VKTools.Revit.Commands
{
    /// <summary>
    /// «Создать спецификацию» (Р6): «Система и этаж» (с согласия) → окно выбора систем и этажей → новая спецификация
    /// (форма ГОСТ или копия эталона) с фильтрами, которые ставит плагин, и отмеченные функции по порядку выпуска одной операцией;
    /// или обновление фильтров созданных ранее.
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
                var timings = new OperationTimings { Trace = line => Host.Log.Info("  этап " + line) };
                string? before = doc.IsReadOnly ? null : PrepareSystemFloor(app, doc, timings);
                if (before == Cancel) return Result.Cancelled;

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

                if (window.Action == ScheduleAction.Create) return CreateWithChain(app, doc, data, window, before, timings);

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

        private const string Cancel = "\u0000cancel";

        /// <summary>
        /// До окна: параметр «VK_Сокращение системы» и «Система и этаж» по настройке модели — иначе в окне не будет новых систем,
        /// а фильтры не увидят новые элементы. Пишет отдельной операцией и только с согласия. Возвращает строку для итога
        /// (null — ничего не делалось) или <see cref="Cancel"/>.
        /// </summary>
        private static string? PrepareSystemFloor(UIApplication app, Document doc, OperationTimings timings)
        {
            string title = SpecModule.SystemFloor.Title();
            (SpecOptions options, SpecDefaults defaults) = SpecSettingsLoader.Load(doc, SpecModule.SystemFloor);
            bool needsParameter = options.SystemFloor.WriteSystem && options.SystemFloor.UsesVkSystem && VkParameters.NeedsSetup(doc, out _);
            ParamPlan? plan = null;
            if (!needsParameter)
            {
                using (var progress = new ProgressScope(app, "проверка: " + title))
                {
                    plan = SpecPlanner.Build(doc, options, defaults, null, progress, timings);
                }
                if (plan.Changes.Count == 0 || plan.WriteBlockedReason != null)
                {
                    if (plan.WriteBlockedReason != null) Host.Log.Warn($"Создать спецификацию: «{title}» не обновлена — {plan.WriteBlockedReason}");
                    return plan.WriteBlockedReason != null ? $"«{title}» не обновлялась: {plan.WriteBlockedReason}" : null;
                }
            }

            var ask = new TaskDialog("VK Tools: создать спецификацию")
            {
                MainInstruction = "Сначала обновить «Система и этаж»?",
                MainContent = (needsParameter
                        ? $"В проекте нет параметра «{KnownParameters.VkSystem.Name}» (или он не у всех категорий ВК). Плагин добавит его и заполнит из «Сокращения для системы». "
                        : $"У {plan!.Changes.Count} значений система или этаж не совпадают с моделью. ")
                    + "Без обновления новые элементы не попадут в фильтры спецификации, а в окне не будет новых систем.\n\n"
                    + "Обновление — отдельная операция (отменяется своим Ctrl+Z).",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Обновить и продолжить");
            ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Продолжить без обновления", "Спецификация соберётся по текущим значениям");
            TaskDialogResult answer = ask.Show();
            if (answer == TaskDialogResult.CommandLink2) return $"«{title}» не обновлялась (выбрано «без обновления»)";
            if (answer != TaskDialogResult.CommandLink1) return Cancel;

            string? parameterNote = null;
            if (needsParameter)
            {
                string? refused = VkParameters.Ensure(doc, app.Application, out parameterNote);
                if (refused != null)
                {
                    TaskDialog.Show("VK Tools", $"«{KnownParameters.VkSystem.Name}» не добавлен: {refused}.\n\nСпецификация не создаётся: фильтровать не по чему. Модель не изменялась.");
                    return Cancel;
                }
            }
            ChainStepResult step;
            using (var progress = new ProgressScope(app, title))
            {
                if (plan == null)
                {
                    plan = SpecPlanner.Build(doc, options, defaults, null, progress, timings);
                }
                step = ScheduleChain.Apply(doc, plan, options.SavedInProject, title, progress, timings);
            }
            Host.Log.Info("Создать спецификацию, до окна: " + step + (parameterNote != null ? " " + parameterNote : string.Empty));
            if (step.Error != null)
            {
                TaskDialog.Show("VK Tools", $"«{title}» не записана: {step.Error}.\n\nСпецификация не создаётся. Подробности в логе.");
                return Cancel;
            }
            return (parameterNote != null ? parameterNote + ". " : string.Empty) + step;
        }

        /// <summary>Создать и применить отмеченные функции одной группой: отчёт → «Записать» (одна отмена) или «Откатить».</summary>
        private static Result CreateWithChain(UIApplication app, Document doc, ScheduleModelData data, CreateScheduleWindow window, string? before, OperationTimings timings)
        {
            ScheduleRequest request = window.Request;
            var report = new List<string>();
            ScheduleResult created;
            using (var group = new TransactionGroup(doc, "VK Tools: создать спецификацию «" + ScheduleRequest.CleanName(request.Name) + "»"))
            {
                group.Start();
                created = ScheduleBuilder.Create(doc, data, request);
                if (created.Error != null)
                {
                    group.RollBack();
                    Host.Log.Warn("Создать спецификацию: " + created.Error);
                    TaskDialog.Show("VK Tools", "Спецификация не создана: " + created.Error + ".\n\nМодель не изменялась" + (before != null ? " (кроме «Системы и этажа» до окна)" : string.Empty) + ".");
                    return Result.Failed;
                }
                report.Add("Спецификация: " + string.Join(" ", created.Lines) + (created.Notes.Count > 0 ? " Заметки: " + string.Join("; ", created.Notes) + "." : string.Empty));

                string? failed = null;
                try
                {
                    using (var progress = new ProgressScope(app, "создать спецификацию"))
                    {
                        foreach (ChainStep step in window.Steps)
                        {
                            ChainStepResult result = ScheduleChain.Run(doc, step, ScheduleRequest.CleanName(request.Name), progress, timings);
                            report.Add(result.ToString());
                            Host.Log.Info("  " + result);
                            if (result.Error != null)
                            {
                                failed = result.Title;
                                break;
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    group.RollBack();
                    Host.Log.Info("Создать спецификацию: отменено пользователем, всё откатено.");
                    TaskDialog.Show("VK Tools", "Отменено. Спецификация и изменения функций откатены.");
                    return Result.Cancelled;
                }
                catch (Exception)
                {
                    if (group.HasStarted()) group.RollBack();
                    throw;
                }
                foreach (ChainStep skipped in ScheduleChain.Order.Where(s => !window.Steps.Contains(s))) report.Add(ScheduleChain.Title(skipped) + ": не отмечено.");
                if (before != null) report.Insert(0, "До окна — " + before);

                if (failed != null)
                {
                    group.RollBack();
                    Host.Log.Warn($"Создать спецификацию: ошибка на шаге «{failed}», всё откатено. " + string.Join(" | ", report));
                    TaskDialog.Show("VK Tools", $"Ошибка на шаге «{failed}» — спецификация и изменения функций откатены.\n\n" + string.Join("\n\n", report));
                    return Result.Failed;
                }

                var confirm = new TaskDialog("VK Tools: создать спецификацию")
                {
                    MainInstruction = $"«{ScheduleRequest.CleanName(request.Name)}» — записать?",
                    MainContent = string.Join("\n\n", report),
                    CommonButtons = TaskDialogCommonButtons.None,
                };
                confirm.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Записать", "Спецификация и изменения функций — одна операция, отменяется одним Ctrl+Z");
                confirm.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Откатить", "Модель вернётся к состоянию до «Создать»");
                if (confirm.Show() != TaskDialogResult.CommandLink1)
                {
                    group.RollBack();
                    Host.Log.Info("Создать спецификацию: откатено пользователем. " + string.Join(" | ", report));
                    return Result.Cancelled;
                }
                group.Assimilate();
            }
            Host.Log.Info($"Создана спецификация «{request.Name}»: " + string.Join(" | ", report));
            if (created.ScheduleId.HasValue && doc.GetElement(created.ScheduleId.Value.ToElementId()) is View view)
            {
                app.ActiveUIDocument.ActiveView = view;
            }
            return Result.Succeeded;
        }
    }
}
