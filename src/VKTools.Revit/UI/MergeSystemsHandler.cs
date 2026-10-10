using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Core.Checks;
using VKTools.Core.Logging;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Mep;

namespace VKTools.Revit.UI
{
    internal enum MergeRequest
    {
        Refresh,
        Trial,
        Apply,

        /// <summary>Пробная пересборка основных отмеченных копий (D-023): всегда откат.</summary>
        RebuildProbe,
    }

    /// <summary>
    /// Действия окна объединения в контексте Revit API. Анализ строится заново перед каждым действием:
    /// пока окно было открыто, модель могла измениться. Отмеченная копия, которая по новому анализу
    /// не объединяется, пропускается и попадает в отчёт.
    /// </summary>
    internal sealed class MergeSystemsHandler : IExternalEventHandler
    {
        private MergeSystemsWindow? _window;
        private MergeRequest _request;
        private List<string> _keys = new List<string>();

        public void Set(MergeSystemsWindow window, MergeRequest request, IEnumerable<string> keys)
        {
            _window = window;
            _request = request;
            _keys = keys.ToList();
        }

        public void Execute(UIApplication app)
        {
            MergeSystemsWindow? window = _window;
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

                if (_request == MergeRequest.Refresh)
                {
                    MergePlan fresh;
                    using (var progress = new ProgressScope(app, "анализ систем", window))
                    {
                        fresh = SystemMergePlanner.Build(doc, progress, timings);
                    }
                    LogPlan(doc, fresh, timings);
                    window.ShowPlan(fresh, null, MergeReportBuilder.Build(doc.Title, fresh, null, Array.Empty<string>()));
                    return;
                }
                if (doc.IsReadOnly)
                {
                    window.ShowMessage("Документ сейчас только для чтения: запись невозможна.");
                    return;
                }

                if (_request == MergeRequest.RebuildProbe)
                {
                    RebuildProbe(app, window, doc, timings);
                    return;
                }

                MergeMode mode = _request == MergeRequest.Apply ? MergeMode.Apply : MergeMode.Trial;
                Host.Log.Info($"Объединение систем «{doc.Title}»: {(mode == MergeMode.Apply ? "запись" : "пробный прогон")}, отмечено {_keys.Count}.");
                MergePlan plan;
                MergePlan? after = null;
                MergeRunResult run;
                var skipped = new List<string>();
                using (var progress = new ProgressScope(app, mode == MergeMode.Apply ? "объединение систем" : "пробный прогон", window))
                {
                    // Анализ заново, но только отмеченных копий: остальные для записи не нужны
                    plan = SystemMergePlanner.Build(doc, progress, timings, new HashSet<string>(_keys, StringComparer.Ordinal));
                    List<PlannedCandidate> selected = Select(plan, _keys, skipped);
                    foreach (string line in MergeDiagnostics.Describe(doc, plan, selected)) Host.Log.Info("  " + line);
                    run = SystemMergeExecutor.Run(doc, plan, selected, mode, progress, timings, Host.Log);
                    if (run.Committed)
                    {
                        try
                        {
                            after = SystemMergePlanner.Build(doc, progress, timings);
                        }
                        catch (OperationCanceledException)
                        {
                            run.Warnings.Add("Записано. Список не обновлён (анализ отменён) — нажмите «Обновить».");
                        }
                    }
                }
                run.Timings = timings.Summary();
                LogRun(run, skipped);

                CheckReport report = MergeReportBuilder.Build(doc.Title, after ?? plan, run, skipped);
                if (after != null) window.ShowPlan(after, run, report);
                else window.ApplyRun(run, report);
            }
            catch (OperationCanceledException)
            {
                Host.Log.Info($"Объединение систем: отменено пользователем на этапе анализа. Время: {timings.Summary()}");
                window.ShowMessage("Отменено на этапе анализа: модель не изменялась.", error: false);
            }
            catch (Exception ex)
            {
                Host.Log.Error($"Объединение систем: ошибка. Время: {timings.Summary()}", ex);
                window.ShowMessage("Ошибка: " + ex.Message + " Модель не изменена, если запись не была подтверждена сообщением. Подробности в логе.");
            }
        }

        public string GetName() => "VK Tools: объединение систем";

        /// <summary>Пересборка основных отмеченных копий — только проба, модель не меняется (D-023).</summary>
        private void RebuildProbe(UIApplication app, MergeSystemsWindow window, Document doc, OperationTimings timings)
        {
            Host.Log.Info($"Пересборка систем «{doc.Title}» (проба), отмечено {_keys.Count}.");
            var skipped = new List<string>();
            var results = new List<RebuildProbeResult>();
            using (var progress = new ProgressScope(app, "пересборка (проба)", window))
            {
                MergePlan plan = SystemMergePlanner.Build(doc, progress, timings, new HashSet<string>(_keys, StringComparer.Ordinal));
                List<PlannedCandidate> selected = Select(plan, _keys, skipped);
                foreach (IGrouping<PlannedGroup, PlannedCandidate> g in selected.GroupBy(c => c.Group))
                {
                    results.Add(SystemRebuildProbe.Run(doc, plan.Model, g.Key, g.ToList(), progress, timings, Host.Log));
                    if (progress.IsCancellationRequested) break;
                }
            }
            foreach (string s in skipped) Host.Log.Warn("  " + s);
            Host.Log.Info("  время: " + timings.Summary());
            var lines = new List<string> { "Время: " + timings.Summary() };
            foreach (RebuildProbeResult r in results)
            {
                lines.Add(r.Summary);
                lines.AddRange(r.Lines.Select(x => "  " + x));
            }
            lines.AddRange(skipped);
            bool ok = results.Count > 0 && results.All(r => r.Passed);
            string status = results.Count == 0
                ? "Пересборка (проба): нечего проверять."
                : string.Join(" ", results.Select(r => r.Summary));
            window.ShowProbe(status, lines, ok);
        }

        /// <summary>Отмеченные копии по свежему анализу; пропавшие и ставшие «не объединяется» — в skipped.</summary>
        private static List<PlannedCandidate> Select(MergePlan plan, List<string> keys, List<string> skipped)
        {
            var byKey = plan.Candidates.ToDictionary(c => c.Key, StringComparer.Ordinal);
            var selected = new List<PlannedCandidate>();
            foreach (string key in keys)
            {
                if (!byKey.TryGetValue(key, out PlannedCandidate? c))
                {
                    skipped.Add("Отмеченной системы больше нет в модели или у неё другая основная — пропущена.");
                }
                else if (c.State == CandidateState.Rejected)
                {
                    skipped.Add($"«{c.Record.Name}»: по новому анализу не объединяется ({c.Explanation}) — пропущена.");
                }
                else
                {
                    selected.Add(c);
                }
            }
            return selected;
        }

        internal static void LogPlan(Document doc, MergePlan plan, OperationTimings timings)
        {
            Host.Log.Info($"Анализ систем «{doc.Title}»: групп {plan.Groups.Count}, готовы {plan.Count(CandidateState.Ready)}, "
                + $"нужна отметка {plan.Count(CandidateState.NeedsConfirmation)}, не объединяются {plan.Count(CandidateState.Rejected)}. Время: {timings.Summary()}");
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                foreach (string line in MergeDiagnostics.Networks(doc, plan)) Host.Log.Info("  " + line);
                Host.Log.Info($"  (разбор сетей: {watch.Elapsed.TotalSeconds:0.0} с)");
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException)
            {
                Host.Log.Warn("  разбор сетей не удался: " + ex.Message);
            }
        }

        private static void LogRun(MergeRunResult run, List<string> skipped)
        {
            foreach (CandidateOutcome o in run.Outcomes)
            {
                Host.Log.Info($"  «{o.Candidate.Record.Name}» (Id {o.Candidate.Record.Id}) → «{o.Candidate.Group.BaseName}» (Id {o.Candidate.Group.TargetId}): {o.StateText}. {o.Detail}");
            }
            foreach (string line in run.PostCheck) Host.Log.Info("  проверка: " + line);
            if (run.DisabledUpdaters.Count > 0) Host.Log.Warn("  " + MergeRunResult.DisabledUpdatersText(run.DisabledUpdaters));
            foreach (string w in run.Warnings) Host.Log.Warn("  " + w);
            foreach (string e in run.Errors) Host.Log.Warn("  ошибка: " + e);
            foreach (string s in skipped) Host.Log.Warn("  " + s);
            Host.Log.Info("  итог: " + run.Status);
            Host.Log.Info("  время: " + run.Timings);
        }
    }

    /// <summary>Одно окно объединения на сессию; события создаются командой (контекст Revit API).</summary>
    internal static class MergeWindowHost
    {
        private static MergeSystemsWindow? _current;
        private static MergeSystemsHandler? _handler;
        private static ExternalEvent? _event;
        private static SelectElementsHandler? _selectHandler;
        private static ExternalEvent? _selectEvent;

        public static void Show(UIApplication app, Document doc, MergePlan plan)
        {
            if (_handler == null || _event == null)
            {
                _handler = new MergeSystemsHandler();
                _event = ExternalEvent.Create(_handler);
            }
            if (_selectHandler == null || _selectEvent == null)
            {
                _selectHandler = new SelectElementsHandler();
                _selectEvent = ExternalEvent.Create(_selectHandler);
            }
            ReportWindowHost.EnsureCreated();

            _current?.Close();
            var window = new MergeSystemsWindow(doc, _handler, _event, _selectHandler, _selectEvent, app.MainWindowHandle);
            window.AttachTo(app.MainWindowHandle);
            window.Closed += (s, e) =>
            {
                if (ReferenceEquals(_current, s)) _current = null;
            };
            _current = window;
            window.ShowPlan(plan, null, MergeReportBuilder.Build(doc.Title, plan, null, Array.Empty<string>()));
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
