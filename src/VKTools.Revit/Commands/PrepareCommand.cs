using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Logging;
using VKTools.Core.Spec;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Schedules;
using VKTools.Revit.Spec;
using VKTools.Revit.UI;

namespace VKTools.Revit.Commands
{
    /// <summary>
    /// «Подготовить и собрать» (0.18.0, план 0.17 п. «Подготовить и собрать»): все шаги выпуска по порядку одной операцией —
    /// система и этаж → изоляция → группирование → имена труб → марки → запас → расчётные позиции → позиции → фильтры созданных
    /// спецификаций; отчёт по шагам, «Записать» (одна отмена) или «Откатить»; затем «Проверить спецификацию».
    /// Профиль шагов (какие «не трогать») хранится в модели.
    /// </summary>
    [Transaction(TransactionMode.Manual)]
    public sealed class PrepareCommand : IExternalCommand
    {
        public const string SkipKey = "prep.skip";
        public const string Section = "prep";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication app = commandData.Application;
            Document doc = app.ActiveUIDocument.Document;
            try
            {
                if (doc.IsReadOnly)
                {
                    TaskDialog.Show("VK Tools", "Документ только для чтения: подготовить нельзя.");
                    return Result.Cancelled;
                }
                SpecProjectSettings stored = SpecSettingsStore.Read(doc, out _);
                ISet<ChainStep> skipped = ParseSkipped(stored.Get(SkipKey));
                ScheduleModelData data = ScheduleModel.Collect(doc);
                var window = new PrepareWindow(doc.Title, skipped, data.Own.Count, stored.Stamp(Section));
                window.AttachTo(app.MainWindowHandle);
                if (window.ShowDialog() != true) return Result.Cancelled;
                List<ChainStep> steps = window.Steps.ToList();
                Host.Log.Info($"Подготовить и собрать «{doc.Title}»: шаги {string.Join(", ", steps.Select(ScheduleChain.Title))}.");

                var timings = new OperationTimings { Trace = line => Host.Log.Info("  этап " + line) };
                var report = new List<string>();
                bool checkAfter = window.CheckAfter;
                using (var group = new TransactionGroup(doc, "VK Tools: подготовить и собрать"))
                {
                    group.Start();
                    string? profileNote = SaveProfile(doc, stored, window.Skipped);
                    if (profileNote != null) report.Add(profileNote);

                    if (steps.Contains(ChainStep.SystemFloor) && VkParameters.NeedsSetup(doc, out _)
                        && (ParamProfile.IsOwn || SpecSettingsLoader.Load(doc, SpecModule.SystemFloor).Options.SystemFloor.NeedsVkParameters))
                    {
                        string? refused = VkParameters.Ensure(doc, app.Application, out string? note);
                        if (refused != null)
                        {
                            group.RollBack();
                            TaskDialog.Show("VK Tools", $"Свои параметры VK Tools не добавлены: {refused}.\n\nМодель не изменялась.");
                            return Result.Failed;
                        }
                        if (note != null) report.Add(note);
                    }

                    string? failed = null;
                    var done = new List<string>();
                    try
                    {
                        using (var progress = new ProgressScope(app, "подготовить и собрать"))
                        {
                            foreach (ChainStep step in steps)
                            {
                                ChainStepResult result = ScheduleChain.Run(doc, step, null, progress, timings);
                                report.Add(result.ToString());
                                Host.Log.Info("  " + result);
                                if (result.Error != null)
                                {
                                    failed = result.Title;
                                    break;
                                }
                                done.Add(result.Title);
                            }
                            if (failed == null && window.UpdateSchedules)
                            {
                                progress.Report("Фильтры созданных спецификаций…");
                                ScheduleModelData fresh = ScheduleModel.Collect(doc);
                                var lines = new List<string>();
                                foreach (OwnSchedule own in fresh.Own)
                                {
                                    ScheduleResult updated = ScheduleBuilder.Update(doc, fresh, own.Id);
                                    lines.Add(updated.Error == null ? $"«{own.Name}»" : $"«{own.Name}» — не обновлена: {updated.Error}");
                                }
                                if (lines.Count > 0) report.Add("Фильтры созданных кнопкой: " + string.Join("; ", lines) + ".");
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        group.RollBack();
                        Host.Log.Info("Подготовить и собрать: отменено пользователем, всё откатено.");
                        TaskDialog.Show("VK Tools", "Отменено. Все шаги откатены, модель не изменилась.");
                        return Result.Cancelled;
                    }
                    catch (Exception)
                    {
                        if (group.HasStarted()) group.RollBack();
                        throw;
                    }
                    foreach (ChainStep off in ScheduleChain.PrepareOrder.Where(s => !steps.Contains(s))) report.Add(ScheduleChain.Title(off) + ": не трогать (снято).");

                    var confirm = new TaskDialog("VK Tools: подготовить и собрать")
                    {
                        MainInstruction = failed == null ? "Все шаги прошли — записать?" : $"Ошибка на шаге «{failed}»",
                        MainContent = string.Join("\n\n", report),
                        CommonButtons = TaskDialogCommonButtons.None,
                    };
                    if (failed == null)
                    {
                        confirm.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Записать", "Все шаги — одна операция, отменяется одним Ctrl+Z");
                    }
                    else if (done.Count > 0)
                    {
                        confirm.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, $"Записать прошедшие шаги ({done.Count})",
                            $"{string.Join(", ", done)}. Потом снимите их галки и запустите снова с «{failed}»");
                    }
                    confirm.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Откатить всё", "Модель вернётся к состоянию до запуска");
                    if (confirm.Show() != TaskDialogResult.CommandLink1)
                    {
                        group.RollBack();
                        Host.Log.Info("Подготовить и собрать: откатено. " + string.Join(" | ", report));
                        return failed == null ? Result.Cancelled : Result.Failed;
                    }
                    group.Assimilate();
                    Host.Log.Info("Подготовить и собрать: записано. " + string.Join(" | ", report));
                    if (failed != null) checkAfter = false;
                }

                if (checkAfter)
                {
                    List<ViewSchedule> issued = ScheduleAuditRunner.Issued(doc);
                    if (issued.Count > 0)
                    {
                        CheckReport check;
                        using (var read = new Transaction(doc, "VK Tools: чтение спецификаций"))
                        {
                            bool opened = !doc.IsModifiable && read.Start() == TransactionStatus.Started;
                            try
                            {
                                check = ScheduleAuditRunner.Run(doc, issued, checkOutside: true);
                            }
                            finally
                            {
                                if (opened) read.RollBack();
                            }
                        }
                        Host.Log.Info("Подготовить и собрать → проверка: " + check.Summary);
                        ReportWindowHost.Show(check, doc, app);
                    }
                }
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Host.Log.Error($"Подготовить и собрать «{doc.Title}»: ошибка.", ex);
                TaskDialog.Show("VK Tools", "Ошибка: " + ex.Message + "\n\nИзменения откатены. Подробности в логе.");
                return Result.Failed;
            }
        }

        internal static ISet<ChainStep> ParseSkipped(string? text)
        {
            var result = new HashSet<ChainStep>();
            foreach (string part in (text ?? string.Empty).Split(','))
            {
                if (part.Trim().Length > 0 && Enum.TryParse(part.Trim(), out ChainStep step)) result.Add(step);
            }
            return result;
        }

        /// <summary>Профиль шагов в модель, если изменился (внутри группы — откатывается вместе с ней). Текст — в отчёт или null.</summary>
        private static string? SaveProfile(Document doc, SpecProjectSettings stored, ISet<ChainStep> skipped)
        {
            string? value = skipped.Count == 0 ? null : string.Join(",", ScheduleChain.PrepareOrder.Where(skipped.Contains));
            if (string.Equals(value, stored.Get(SkipKey), StringComparison.Ordinal)) return null;
            stored.Set(SkipKey, value);
            stored.SetStamp(Section, Environment.UserName, DateTime.Now);
            using (var t = new Transaction(doc, "VK Tools: профиль шагов"))
            {
                t.Start();
                string? problem = SpecSettingsStore.Write(doc, stored);
                if (problem != null)
                {
                    t.RollBack();
                    return "Профиль шагов не сохранён: " + problem + ".";
                }
                t.Commit();
            }
            return "Профиль шагов сохранён в модели: " + (skipped.Count == 0 ? "все шаги" : "не трогать — " + string.Join(", ", ScheduleChain.PrepareOrder.Where(skipped.Contains).Select(ScheduleChain.Title))) + ".";
        }
    }
}
