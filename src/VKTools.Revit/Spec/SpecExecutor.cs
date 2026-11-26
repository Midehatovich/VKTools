using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Logging;
using VKTools.Core.Progress;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Mep;

namespace VKTools.Revit.Spec
{
    internal enum SpecRunMode
    {
        Trial,
        Apply,
    }

    /// <summary>Изменение, которое не записалось или которое после фиксации переписал кто-то другой.</summary>
    internal sealed class ChangeProblem
    {
        public ChangeProblem(ParamChange change, string reason, string actual = "")
        {
            Change = change;
            Reason = reason;
            Actual = actual;
        }

        public ParamChange Change { get; }

        public string Reason { get; }

        /// <summary>Значение после фиксации (для «перезаписано»).</summary>
        public string Actual { get; }
    }

    internal sealed class SpecRunResult
    {
        public SpecRunResult(SpecRunMode mode, IReadOnlyList<SpecModule> modules)
        {
            Mode = mode;
            Modules = modules;
        }

        public SpecRunMode Mode { get; }

        public IReadOnlyList<SpecModule> Modules { get; }

        public int Planned { get; set; }

        public int Written { get; set; }

        /// <summary>Revit не дал записать: параметр пропал, отказ Set.</summary>
        public List<ChangeProblem> Failed { get; } = new List<ChangeProblem>();

        /// <summary>После фиксации значение другое: его переписал апдейтер другой надстройки или формула (REQ-SAFE-006).</summary>
        public List<ChangeProblem> Overwritten { get; } = new List<ChangeProblem>();

        public List<string> Warnings { get; } = new List<string>();

        public List<string> Errors { get; } = new List<string>();

        public List<string> DisabledUpdaters { get; } = new List<string>();

        /// <summary>Почему Revit «молча» не сохранил значение — по устройству параметра в семействе (после прогона).</summary>
        public List<FamilyDiagnosis> Diagnoses { get; } = new List<FamilyDiagnosis>();

        /// <summary>Настройки окна сохранены в модели той же операцией (только «Применить»).</summary>
        public bool SettingsSaved { get; set; }

        public bool Committed { get; set; }

        public bool Cancelled { get; set; }

        public string Status { get; set; } = string.Empty;

        public string Timings { get; set; } = string.Empty;

        /// <summary>Пробный прогон без ошибок фиксации: «Применить» разрешена (D-013).</summary>
        public bool Passed => Errors.Count == 0 && !Cancelled;
    }

    /// <summary>
    /// Запись плана: TransactionGroup → одна Transaction на все значения → проверка после фиксации → откат (пробный прогон)
    /// или Assimilate (запись, одна строка в журнале отмены, REQ-SAFE-002). Ошибка фиксации — откат всего.
    /// Отказ записи отдельного значения не останавливает остальные (как в Dynamo-эталонах), он в отчёте.
    /// </summary>
    internal static class SpecExecutor
    {
        public static string OperationName(IReadOnlyList<SpecModule> modules) =>
            modules.Contains(SpecModule.SystemFloor) ? "VK Tools: система и этаж"
            : modules.Contains(SpecModule.Insulation) ? "VK Tools: изоляция"
            : modules.Contains(SpecModule.PipeReserve) ? "VK Tools: запас труб"
            : modules.Contains(SpecModule.PipeNames) ? "VK Tools: имена труб"
            : modules.Contains(SpecModule.Vendor) ? "VK Tools: марки и поставщики"
            : modules.Contains(SpecModule.Positions) ? "VK Tools: позиции"
            : modules.Count == 1 && modules[0] == SpecModule.Nested ? "VK Tools: перенос во вложенные" : "VK Tools: группирование";

        /// <param name="saveSettings">Для «Применить»: сохранить настройки окна в модели той же операцией (null — не нужно). Возвращает причину отказа.</param>
        public static SpecRunResult Run(Document doc, ParamPlan plan, SpecRunMode mode, IOperationProgress progress, OperationTimings timings, ILog log,
            Func<Document, string?>? saveSettings = null)
        {
            var result = new SpecRunResult(mode, plan.Modules) { Planned = plan.Changes.Count };
            var access = new ParamAccess(doc);
            if (plan.Changes.Count == 0)
            {
                result.Status = "Записывать нечего: все значения уже такие, как нужно.";
                return result;
            }
            // Порядок записи — порядок модулей: вложенные, группирование, система и этаж (значения посчитаны заранее, порядок не влияет)
            List<ParamChange> changes = plan.Changes.OrderBy(c => c.Module).ToList();
            var written = new List<ParamChange>();
            bool fatal = false;

            string operationName = OperationName(plan.Modules);
            using (var operation = new TransactionGroup(doc, operationName))
            {
                operation.Start();
                try
                {
                    var failures = new FailureCollector(SystemMergeExecutor.RegisteredUpdaters(doc), SystemMergeExecutor.DisableOnFailureApplications);
                    using (Transaction transaction = FailureCollector.Start(doc, "VK Tools: запись параметров", failures))
                    {
                        // «Позиции»: скрытая графа ключа сортировки в спецификациях (детали сразу под родителем)
                        foreach (long scheduleId in plan.PositionKeySchedules)
                        {
                            if (!(doc.GetElement(scheduleId.ToElementId()) is ViewSchedule vs)) continue;
                            string? refused = PositionKeys.Install(doc, vs);
                            if (refused != null) result.Warnings.Add($"«{vs.Name}»: ключ позиции не добавлен — {refused}; детали встанут по категории.");
                            else log.Info($"  «{vs.Name}»: графа «{Core.Config.ParamProfile.PositionKeyName}» — ключ сортировки после системы.");
                        }
                        using (timings.Measure("запись значений"))
                        {
                            for (int i = 0; i < changes.Count; i++)
                            {
                                if (i % 200 == 0)
                                {
                                    progress.Report($"Запись: {i + 1} из {changes.Count}", i, changes.Count);
                                    progress.ThrowIfCancelled();
                                }
                                ParamChange change = changes[i];
                                string? problem = Write(doc, access, change);
                                if (problem == null) written.Add(change);
                                else result.Failed.Add(new ChangeProblem(change, problem));
                            }
                        }
                        progress.Report("Фиксация: Revit пересчитывает модель и запускает обновления других надстроек…");
                        TransactionStatus status;
                        using (timings.Measure("фиксация")) status = transaction.Commit();
                        result.Warnings.AddRange(failures.Warnings.Distinct().Take(50).Select(w => "Revit: " + w));
                        foreach (string note in failures.Notes) log.Info("  " + note);
                        if (failures.DisabledUpdaters.Count > 0) SystemMergeExecutor.RememberDisabled(failures.DisabledUpdaters);
                        if (status != TransactionStatus.Committed)
                        {
                            fatal = true;
                            result.Errors.Add("Revit отклонил фиксацию" + (failures.Errors.Count > 0 ? ": " + string.Join("; ", failures.Errors.Distinct().Take(10)) : $" ({status})."));
                        }
                    }

                    if (!fatal)
                    {
                        using (timings.Measure("проверка после фиксации"))
                        {
                            progress.Report("Проверка: значения после фиксации…");
                            foreach (ParamChange change in written)
                            {
                                ParamValue? actual = Read(doc, access, change);
                                if (actual != null && actual.SameAs(change.Value)) result.Written++;
                                else result.Overwritten.Add(new ChangeProblem(change, "после фиксации значение другое", actual?.Display ?? "(параметр пропал)"));
                            }
                        }
                    }

                    if (mode == SpecRunMode.Trial || fatal)
                    {
                        progress.Report("Откат всей операции…");
                        using (timings.Measure("откат")) operation.RollBack();
                        result.Status = mode == SpecRunMode.Trial && !fatal
                            ? $"Пробный прогон прошёл: записалось бы {result.Written} из {result.Planned}. Всё откачено, модель не изменена."
                            : "Ошибка фиксации — откат всей операции. Модель не изменена.";
                    }
                    else
                    {
                        if (saveSettings != null)
                        {
                            using (var settings = new Transaction(doc, SpecSettingsStore.TransactionName))
                            {
                                settings.Start();
                                string? problem;
                                try
                                {
                                    problem = saveSettings(doc);
                                }
                                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                                {
                                    problem = ex.Message;
                                }
                                if (problem == null && settings.Commit() == TransactionStatus.Committed)
                                {
                                    result.SettingsSaved = true;
                                }
                                else
                                {
                                    if (settings.HasStarted() && !settings.HasEnded()) settings.RollBack();
                                    result.Warnings.Add("Настройки окна в модели не сохранены" + (problem != null ? ": " + problem : ".") + " Значения записаны.");
                                }
                            }
                        }
                        progress.Report("Запись в журнал отмены…");
                        using (timings.Measure("запись (Assimilate)")) operation.Assimilate();
                        result.Committed = true;
                        result.Status = $"Записано {result.Written} из {result.Planned} одной операцией «{operationName}»: Ctrl+Z отменяет её целиком.";
                        if (doc.IsWorkshared && written.Count > 0) result.Warnings.Add(BusySummary.SyncReminder(written.Select(c => c.ElementId).Distinct().Count()));
                    }
                }
                catch (Exception ex)
                {
                    if (operation.HasStarted() && !operation.HasEnded()) operation.RollBack();
                    bool wasCancel = ex is OperationCanceledException || progress.IsCancellationRequested;
                    if (!wasCancel)
                    {
                        log.Error(operationName + ": запись прервана, всё откачено.", ex);
                        result.Errors.Add("прервано: " + ex.Message);
                    }
                    result.Cancelled = wasCancel;
                    result.Written = 0;
                    result.Status = wasCancel ? "Отменено: всё откачено, модель не изменена." : "Ошибка — откат всей операции. Модель не изменена.";
                }
            }
            if (result.Failed.Count > 0) result.Warnings.Add($"Не записалось значений: {result.Failed.Count} — строки «Не записано» в отчёте.");
            if (result.Overwritten.Count > 0)
            {
                result.Warnings.Add($"После фиксации значение переписано у {result.Overwritten.Count}: его меняет другая надстройка (апдейтер) или формула. "
                    + "Строки «Перезаписано после фиксации» в отчёте.");
            }
            result.DisabledUpdaters.AddRange(SystemMergeExecutor.SessionDisabled);
            return result;
        }

        /// <summary>null — записано; иначе причина.</summary>
        private static string? Write(Document doc, ParamAccess access, ParamChange change)
        {
            Element? element = doc.GetElement(change.ElementId.ToElementId());
            if (element == null) return "элемента больше нет в модели";
            Parameter? p = access.Find(element, change.Key);
            if (p == null) return "параметра больше нет у элемента";
            return ParamAccess.Write(p, change.Value);
        }

        private static ParamValue? Read(Document doc, ParamAccess access, ParamChange change)
        {
            Element? element = doc.GetElement(change.ElementId.ToElementId());
            Parameter? p = element == null ? null : access.Find(element, change.Key);
            return p == null ? null : ParamAccess.Read(p);
        }
    }
}
