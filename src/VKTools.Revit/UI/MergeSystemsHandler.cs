using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Core.Checks;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Mep;

namespace VKTools.Revit.UI
{
    internal enum MergeRequest
    {
        Refresh,
        Trial,
        Apply,
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

                MergePlan plan = SystemMergePlanner.Build(doc);
                if (_request == MergeRequest.Refresh)
                {
                    LogPlan(doc, plan);
                    window.ShowPlan(plan, null, MergeReportBuilder.Build(doc.Title, plan, null, Array.Empty<string>()));
                    return;
                }
                if (doc.IsReadOnly)
                {
                    window.ShowMessage("Документ только для чтения: запись невозможна.");
                    return;
                }

                var byKey = plan.Candidates.ToDictionary(c => c.Key, StringComparer.Ordinal);
                var selected = new List<PlannedCandidate>();
                var skipped = new List<string>();
                foreach (string key in _keys)
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

                MergeMode mode = _request == MergeRequest.Apply ? MergeMode.Apply : MergeMode.Trial;
                Host.Log.Info($"Объединение систем «{doc.Title}»: {(mode == MergeMode.Apply ? "запись" : "пробный прогон")}, отмечено {_keys.Count}, к выполнению {selected.Count}.");
                MergeRunResult run = SystemMergeExecutor.Run(doc, plan, selected, mode, Host.Log);
                LogRun(run, skipped);

                // Всегда заново: после отката или записи объекты прошлого анализа могут быть недействительны
                MergePlan after = SystemMergePlanner.Build(doc);
                CheckReport report = MergeReportBuilder.Build(doc.Title, after, run, skipped);
                if (skipped.Count > 0 && mode == MergeMode.Apply && run.Committed)
                {
                    run.Warnings.AddRange(skipped);
                }
                window.ShowPlan(after, run, report);
            }
            catch (Exception ex)
            {
                Host.Log.Error("Объединение систем: ошибка.", ex);
                window.ShowMessage("Ошибка: " + ex.Message + " Модель не изменена, если запись не была подтверждена сообщением. Подробности в логе.");
            }
        }

        public string GetName() => "VK Tools: объединение систем";

        private static void LogPlan(Document doc, MergePlan plan)
        {
            Host.Log.Info($"Анализ систем «{doc.Title}»: групп {plan.Groups.Count}, готовы {plan.Count(CandidateState.Ready)}, "
                + $"нужна отметка {plan.Count(CandidateState.NeedsConfirmation)}, не объединяются {plan.Count(CandidateState.Rejected)}.");
        }

        private static void LogRun(MergeRunResult run, List<string> skipped)
        {
            foreach (CandidateOutcome o in run.Outcomes)
            {
                Host.Log.Info($"  «{o.Candidate.Record.Name}» (Id {o.Candidate.Record.Id}) → «{o.Candidate.Group.BaseName}» (Id {o.Candidate.Group.TargetId}): {o.StateText}. {o.Detail}");
            }
            foreach (string line in run.PostCheck) Host.Log.Info("  проверка: " + line);
            foreach (string w in run.Warnings) Host.Log.Warn("  " + w);
            foreach (string e in run.Errors) Host.Log.Warn("  ошибка: " + e);
            foreach (string s in skipped) Host.Log.Warn("  " + s);
            Host.Log.Info("  итог: " + run.Status);
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
