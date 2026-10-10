using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Core.Checks;
using VKTools.Core.Logging;
using VKTools.Core.Spec;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Spec;

namespace VKTools.Revit.UI
{
    internal enum SpecRequest
    {
        Refresh,
        Trial,
        Apply,
    }

    /// <summary>Действия окна «Параметры спецификации» в контексте Revit API. Анализ — заново перед каждым действием.</summary>
    internal sealed class SpecParamsHandler : IExternalEventHandler
    {
        private SpecParamsWindow? _window;
        private SpecRequest _request;
        private List<SpecModule> _modules = new List<SpecModule>();

        public void Set(SpecParamsWindow window, SpecRequest request, IEnumerable<SpecModule> modules)
        {
            _window = window;
            _request = request;
            _modules = modules.ToList();
        }

        public void Execute(UIApplication app)
        {
            SpecParamsWindow? window = _window;
            if (window == null) return;
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
                SpecParamsHost.LastModules = _modules;

                if (_request == SpecRequest.Refresh)
                {
                    ParamPlan fresh;
                    using (var progress = new ProgressScope(app, "анализ параметров", window))
                    {
                        fresh = SpecPlanner.Build(doc, _modules, progress, timings);
                    }
                    LogPlan(doc, fresh, timings);
                    window.ShowPlan(fresh, null, null, SpecReportBuilder.Build(doc.Title, fresh, null));
                    return;
                }
                if (doc.IsReadOnly)
                {
                    window.ShowMessage("Документ сейчас только для чтения: запись невозможна.");
                    return;
                }

                SpecRunMode mode = _request == SpecRequest.Apply ? SpecRunMode.Apply : SpecRunMode.Trial;
                Host.Log.Info($"Параметры спецификации «{doc.Title}»: {(mode == SpecRunMode.Apply ? "запись" : "пробный прогон")}, модули: {string.Join(", ", _modules.Select(m => m.Title()))}.");
                ParamPlan plan;
                ParamPlan? after = null;
                SpecRunResult run;
                using (var progress = new ProgressScope(app, mode == SpecRunMode.Apply ? "запись параметров" : "пробный прогон", window))
                {
                    plan = SpecPlanner.Build(doc, _modules, progress, timings);
                    LogPlan(doc, plan, timings);
                    if (mode == SpecRunMode.Apply && plan.WriteBlockedReason != null)
                    {
                        window.ShowPlan(plan, null, null, SpecReportBuilder.Build(doc.Title, plan, null));
                        window.ShowMessage("Запись запрещена: " + plan.WriteBlockedReason);
                        return;
                    }
                    run = SpecExecutor.Run(doc, plan, mode, progress, timings, Host.Log);
                    if (run.Committed)
                    {
                        try
                        {
                            after = SpecPlanner.Build(doc, _modules, progress, timings);
                        }
                        catch (OperationCanceledException)
                        {
                            run.Warnings.Add("Записано. Повторный анализ отменён — нажмите «Обновить».");
                        }
                    }
                }
                run.Timings = timings.Summary();
                LogRun(run, after);
                window.ShowPlan(plan, run, after, SpecReportBuilder.Build(doc.Title, plan, run));
            }
            catch (OperationCanceledException)
            {
                Host.Log.Info($"Параметры спецификации: отменено пользователем на этапе анализа. Время: {timings.Summary()}");
                window.ShowMessage("Отменено на этапе анализа: модель не изменялась.", error: false);
            }
            catch (Exception ex)
            {
                Host.Log.Error($"Параметры спецификации: ошибка. Время: {timings.Summary()}", ex);
                window.ShowMessage("Ошибка: " + ex.Message + " Модель не изменена, если запись не была подтверждена сообщением. Подробности в логе.");
            }
        }

        public string GetName() => "VK Tools: параметры спецификации";

        internal static void LogPlan(Document doc, ParamPlan plan, OperationTimings timings)
        {
            Host.Log.Info($"Анализ параметров «{doc.Title}»: " + string.Join("; ", plan.Modules.Select(m =>
                $"{m.Title()}: изменений {plan.ChangeCount(m)}, без изменений {plan.Unchanged(m)}, ошибок {plan.IssueCount(m, Severity.Error)}, "
                + $"предупреждений {plan.IssueCount(m, Severity.Warning)}")) + $". Время: {timings.Summary()}");
            foreach (string note in plan.Notes) Host.Log.Info("  " + note);
            foreach (string source in plan.RuleSources) Host.Log.Info("  правила: " + source);
            if (plan.WriteBlockedReason != null) Host.Log.Warn("  запись запрещена: " + plan.WriteBlockedReason);
            foreach (PlanIssue issue in plan.Issues.Where(i => i.Severity != Severity.Info))
            {
                Host.Log.Info($"  {issue.Severity.ToRussian()} [{issue.Module.Title()}] {issue.Parameter} {issue.Subject}: {issue.Reason} — {issue.Count} шт."
                    + (issue.Ids.Count > 0 ? " (Id " + string.Join(", ", issue.Ids.Take(5)) + (issue.Ids.Count > 5 ? " …)" : ")") : string.Empty));
            }
        }

        private static void LogRun(SpecRunResult run, ParamPlan? after)
        {
            foreach (ChangeProblem f in run.Failed.Take(100)) Host.Log.Warn($"  не записано: Id {f.Change.ElementId} {f.Change.Parameter} «{f.Change.New}»: {f.Reason}");
            foreach (ChangeProblem o in run.Overwritten.Take(100)) Host.Log.Warn($"  перезаписано после фиксации: Id {o.Change.ElementId} {o.Change.Parameter} «{o.Change.New}» → «{o.Actual}»");
            foreach (string w in run.Warnings) Host.Log.Warn("  " + w);
            foreach (string e in run.Errors) Host.Log.Warn("  ошибка: " + e);
            if (after != null) Host.Log.Info($"  повторный анализ после записи: изменений {after.Changes.Count}");
            Host.Log.Info("  итог: " + run.Status);
            Host.Log.Info("  время: " + run.Timings);
        }
    }

    /// <summary>Одно окно на сессию; события создаются командой (контекст Revit API).</summary>
    internal static class SpecParamsHost
    {
        private static SpecParamsWindow? _current;
        private static SpecParamsHandler? _handler;
        private static ExternalEvent? _event;
        private static SelectElementsHandler? _selectHandler;
        private static ExternalEvent? _selectEvent;

        /// <summary>Модули последнего запуска в этом сеансе Revit: окно открывается с ними.</summary>
        public static List<SpecModule> LastModules { get; set; } = new List<SpecModule> { SpecModule.Nested, SpecModule.Grouping };

        public static void Show(UIApplication app, Document doc, ParamPlan plan)
        {
            if (_handler == null || _event == null)
            {
                _handler = new SpecParamsHandler();
                _event = ExternalEvent.Create(_handler);
            }
            if (_selectHandler == null || _selectEvent == null)
            {
                _selectHandler = new SelectElementsHandler();
                _selectEvent = ExternalEvent.Create(_selectHandler);
            }

            _current?.Close();
            var window = new SpecParamsWindow(doc, _handler, _event, _selectHandler, _selectEvent, plan.Modules);
            window.AttachTo(app.MainWindowHandle);
            window.Closed += (s, e) =>
            {
                if (ReferenceEquals(_current, s)) _current = null;
            };
            _current = window;
            window.ShowPlan(plan, null, null, SpecReportBuilder.Build(doc.Title, plan, null));
            window.Show();
        }

        public static void CloseAll()
        {
            _current?.Close();
            _current = null;
            _event?.Dispose();
            _event = null;
            _selectEvent?.Dispose();
            _selectEvent = null;
        }
    }
}
