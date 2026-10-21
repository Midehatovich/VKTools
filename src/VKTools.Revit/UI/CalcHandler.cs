using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Core.Calc;
using VKTools.Core.Checks;
using VKTools.Core.Logging;
using VKTools.Revit.Calc;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Spec;

namespace VKTools.Revit.UI
{
    internal enum CalcRequest
    {
        Refresh,
        Trial,
        Apply,
        Save,
    }

    /// <summary>
    /// Действия окна «Расчётные позиции» в контексте Revit API. Расчёт — заново перед каждым действием, с настройками окна;
    /// таблицы по умолчанию и настройка проекта перечитываются каждый раз.
    /// </summary>
    internal sealed class CalcHandler : IExternalEventHandler
    {
        private const string Title = "Расчётные позиции";
        private CalcWindow? _window;
        private CalcRequest _request;
        private CalcOptions? _options;

        public void Set(CalcWindow window, CalcRequest request, CalcOptions options)
        {
            _window = window;
            _request = request;
            _options = options;
        }

        public void Execute(UIApplication app)
        {
            CalcWindow? window = _window;
            CalcOptions? options = _options;
            if (window == null || options == null) return;
            Document doc = window.Document;
            var timings = new OperationTimings { Trace = line => Host.Log.Info("  этап " + line) };
            try
            {
                if (!doc.IsValidObject)
                {
                    window.ShowMessage("Модель уже закрыта. Закройте окно.");
                    return;
                }
                if (app.ActiveUIDocument == null || !app.ActiveUIDocument.Document.Equals(doc))
                {
                    window.ShowMessage($"Окно построено по модели «{doc.Title}». Перейдите в её окно и повторите.");
                    return;
                }
                (CalcOptions stored, CalcDefaults defaults) = CalcSettingsLoader.Load(doc);
                // Отметка «сохранено» — из модели: от неё зависит, действует ли запрет записи по файлам отдела
                options.SavedInProject = stored.SavedInProject;
                options.SavedStamp = stored.SavedStamp;
                window.ShowSettingsState(stored, defaults);

                if (_request == CalcRequest.Save)
                {
                    Save(doc, window, options, defaults);
                    return;
                }
                if (_request == CalcRequest.Refresh)
                {
                    CalcPlan fresh;
                    using (var progress = new ProgressScope(app, "расчёт: " + Title, window))
                    {
                        fresh = CalcPlanner.Build(doc, options, defaults, progress, timings);
                    }
                    LogPlan(doc, fresh, timings);
                    window.ShowPlan(fresh, null, null, CalcReportBuilder.Build(doc.Title, fresh, null), options);
                    return;
                }
                if (doc.IsReadOnly)
                {
                    window.ShowMessage("Документ сейчас только для чтения: запись невозможна.");
                    return;
                }

                SpecRunMode mode = _request == CalcRequest.Apply ? SpecRunMode.Apply : SpecRunMode.Trial;
                CalcPlan plan;
                CalcPlan? after = null;
                CalcRunResult run;
                bool saveSettings = mode == SpecRunMode.Apply && options.Signature() != stored.Signature();
                using (var progress = new ProgressScope(app, (mode == SpecRunMode.Apply ? "запись: " : "пробный прогон: ") + Title, window))
                {
                    plan = CalcPlanner.Build(doc, options, defaults, progress, timings);
                    Host.Log.Info($"{Title} «{doc.Title}»: {(mode == SpecRunMode.Apply ? "запись" : "пробный прогон")}" + (saveSettings ? ", с сохранением настройки в модели." : "."));
                    LogPlan(doc, plan, timings);
                    if (mode == SpecRunMode.Apply && plan.WriteBlockedReason != null)
                    {
                        window.ShowPlan(plan, null, null, CalcReportBuilder.Build(doc.Title, plan, null), options);
                        window.ShowMessage("Запись запрещена: " + plan.WriteBlockedReason);
                        return;
                    }
                    Func<Document, string?>? save = saveSettings ? d => CalcSettingsLoader.Store(d, options, defaults) : (Func<Document, string?>?)null;
                    run = CalcExecutor.Run(doc, plan, mode, progress, timings, Host.Log, save);
                    if (run.Committed)
                    {
                        try
                        {
                            (CalcOptions saved, CalcDefaults savedDefaults) = CalcSettingsLoader.Load(doc);
                            CalcOptions again = options.Clone();
                            again.SavedInProject = saved.SavedInProject;
                            again.SavedStamp = saved.SavedStamp;
                            after = CalcPlanner.Build(doc, again, savedDefaults, progress, timings);
                        }
                        catch (OperationCanceledException)
                        {
                            run.Warnings.Add("Записано. Повторный анализ отменён — нажмите «Обновить».");
                        }
                    }
                }
                run.Timings = timings.Summary();
                LogRun(run, after);
                if (run.SettingsSaved)
                {
                    (CalcOptions saved, CalcDefaults savedDefaults) = CalcSettingsLoader.Load(doc);
                    window.ShowSettingsState(saved, savedDefaults);
                }
                window.ShowPlan(plan, run, after, CalcReportBuilder.Build(doc.Title, plan, run), options);
            }
            catch (OperationCanceledException)
            {
                Host.Log.Info($"{Title}: отменено пользователем на этапе анализа. Время: {timings.Summary()}");
                window.ShowMessage("Отменено на этапе анализа: модель не изменялась.", error: false);
            }
            catch (Exception ex)
            {
                Host.Log.Error($"{Title}: ошибка. Время: {timings.Summary()}", ex);
                window.ShowMessage("Ошибка: " + ex.Message + " Модель не изменена, если запись не была подтверждена сообщением. Подробности в логе.");
            }
        }

        public string GetName() => "VK Tools: расчётные позиции";

        private static void Save(Document doc, CalcWindow window, CalcOptions options, CalcDefaults defaults)
        {
            if (doc.IsReadOnly)
            {
                window.ShowMessage("Документ сейчас только для чтения: сохранить настройку нельзя.");
                return;
            }
            bool toDefault = options.IsDefault(defaults);
            string? problem;
            try
            {
                problem = CalcSettingsLoader.Save(doc, options, defaults);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                problem = ex.Message;
            }
            (CalcOptions stored, CalcDefaults fresh) = CalcSettingsLoader.Load(doc);
            window.ShowSettingsState(stored, fresh);
            if (problem != null)
            {
                Host.Log.Warn($"{Title} «{doc.Title}»: настройка не сохранена — {problem}");
                window.ShowMessage("Настройка не сохранена: " + problem);
                return;
            }
            Host.Log.Info($"{Title} «{doc.Title}»: настройка {(toDefault ? "сброшена на умолчание (удалена из модели)" : "сохранена в модели")}; "
                + $"спецификаций {options.Schedules.Count}, параметр системы «{options.SystemParameter}», группа «{options.Group}».");
            string sync = doc.IsWorkshared ? " Синхронизируйте модель, чтобы коллеги увидели её." : string.Empty;
            window.ShowMessage((toDefault
                ? "Настройка проекта удалена: работают таблицы по умолчанию."
                : $"Настройка сохранена в модели (в журнале отмены — «{SpecSettingsStore.TransactionName}»).") + sync, error: false);
        }

        internal static void LogPlan(Document doc, CalcPlan plan, OperationTimings timings)
        {
            Host.Log.Info($"{Title} «{doc.Title}»: позиций по расчёту {plan.Result.Targets.Count}, изменений {plan.ChangeCount}, без изменений {plan.Unchanged}, "
                + $"ошибок {plan.ErrorCount}, предупреждений {plan.WarningCount}. Время: {timings.Summary()}");
            foreach (string note in plan.Notes) Host.Log.Info("  " + note);
            foreach (string source in plan.RuleSources) Host.Log.Info("  правила: " + source);
            if (plan.WriteBlockedReason != null) Host.Log.Warn("  запись запрещена: " + plan.WriteBlockedReason);
            foreach (CalcIssue issue in plan.Issues.Items.Where(i => i.Severity != Severity.Info))
            {
                Host.Log.Info($"  {issue.Severity.ToRussian()} {issue.Message} — {issue.Count} шт."
                    + (issue.Ids.Count > 0 ? " (Id " + string.Join(", ", issue.Ids.Take(5)) + (issue.Ids.Count > 5 ? " …)" : ")") : string.Empty));
            }
        }

        private static void LogRun(CalcRunResult run, CalcPlan? after)
        {
            foreach ((string key, long? id, string reason) in run.Failed.Take(100)) Host.Log.Warn($"  не записано: «{key}» Id {id}: {reason}");
            foreach ((string key, long id, string reason) in run.Overwritten.Take(100)) Host.Log.Warn($"  перезаписано после фиксации: «{key}» Id {id}: {reason}");
            foreach (string w in run.Warnings) Host.Log.Warn("  " + w);
            foreach (string e in run.Errors) Host.Log.Warn("  ошибка: " + e);
            if (run.SettingsSaved) Host.Log.Info("  настройка сохранена в модели той же операцией");
            if (after != null) Host.Log.Info($"  повторный анализ после записи: изменений {after.ChangeCount}");
            Host.Log.Info("  итог: " + run.Status);
            Host.Log.Info("  время: " + run.Timings);
        }
    }

    /// <summary>Одно окно «Расчётных позиций»; событие создаётся командой (контекст Revit API).</summary>
    internal static class CalcHost
    {
        private static CalcHandler? _handler;
        private static ExternalEvent? _event;
        private static SelectElementsHandler? _selectHandler;
        private static ExternalEvent? _selectEvent;
        private static CalcWindow? _window;

        public static void Show(UIApplication app, Document doc, CalcOptions options, CalcDefaults defaults, CalcPlan plan)
        {
            if (_handler == null || _event == null)
            {
                _handler = new CalcHandler();
                _event = ExternalEvent.Create(_handler);
            }
            if (_selectHandler == null || _selectEvent == null)
            {
                _selectHandler = new SelectElementsHandler();
                _selectEvent = ExternalEvent.Create(_selectHandler);
            }
            _window?.Close();
            var window = new CalcWindow(doc, options, defaults, _handler, _event, _selectHandler, _selectEvent);
            window.AttachTo(app.MainWindowHandle);
            window.Closed += (s, e) =>
            {
                if (ReferenceEquals(_window, s)) _window = null;
            };
            _window = window;
            window.ShowPlan(plan, null, null, CalcReportBuilder.Build(doc.Title, plan, null), options);
            window.Show();
        }

        public static void CloseAll()
        {
            _window?.Close();
            _window = null;
            _event?.Dispose();
            _event = null;
            _handler = null;
            _selectEvent?.Dispose();
            _selectEvent = null;
            _selectHandler = null;
        }
    }
}
