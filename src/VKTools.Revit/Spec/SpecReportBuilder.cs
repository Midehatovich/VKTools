using System;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Spec;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Spec
{
    /// <summary>Отчёт «Перенос во вложенные» / «Группирование» для окна и CSV: было → станет, пропуски, итог прогона.</summary>
    internal static class SpecReportBuilder
    {
        public const string CheckRun = "Запись";

        public static CheckReport Build(string documentTitle, ParamPlan plan, SpecRunResult? run)
        {
            string modules = string.Join(", ", plan.Modules.Select(m => m.Title()));
            string title = modules + (run == null ? ": анализ" : run.Mode == SpecRunMode.Trial ? ": пробный прогон" : ": запись");
            var report = new CheckReport(title, documentTitle, DateTime.Now)
            {
                PluginVersion = BuildInfo.PluginVersion,
                RevitVersion = BuildInfo.RevitVersion,
            };
            report.RuleSources.AddRange(plan.RuleSources);

            if (run != null)
            {
                report.Findings.Add(new CheckFinding(CheckRun, run.Errors.Count > 0 ? Severity.Error : Severity.Info, run.Status));
                foreach (string e in run.Errors) report.Findings.Add(new CheckFinding(CheckRun, Severity.Error, e));
                foreach (string w in run.Warnings) report.Findings.Add(new CheckFinding(CheckRun, Severity.Warning, w));
                if (run.DisabledUpdaters.Count > 0)
                {
                    report.Findings.Add(new CheckFinding(CheckRun, Severity.Warning,
                        $"До перезапуска Revit отключено обновление: {string.Join(", ", run.DisabledUpdaters)} — оно упало при фиксации (D-017)."));
                }
                foreach (FamilyDiagnosis d in run.Diagnoses)
                {
                    var finding = new CheckFinding(CheckRun, Severity.Error, d.Parameter + " — причина «Не записано»: " + d.Message)
                    {
                        Family = d.Family,
                        Action = d.Action,
                    };
                    finding.RelatedElementIds.AddRange(d.Ids);
                    report.Findings.Add(finding);
                }
                foreach (ChangeProblem f in run.Failed)
                {
                    report.Findings.Add(Row(f.Change, Severity.Error, "Не записано: " + f.Reason, "Проверьте элемент («Показать в модели») и повторите."));
                }
                foreach (ChangeProblem o in run.Overwritten)
                {
                    CheckFinding row = Row(o.Change, Severity.Warning, "Перезаписано после фиксации: значение меняет другая надстройка или формула",
                        "Найдите, кто пишет этот параметр (BIM2B, формула в семействе), иначе VK Tools и он будут спорить.");
                    row.Current = o.Actual;
                    report.Findings.Add(row);
                }
                if (!string.IsNullOrEmpty(run.Timings)) report.Findings.Add(new CheckFinding(CheckRun, Severity.Info, "Время: " + run.Timings));
            }

            if (plan.WriteBlockedReason != null)
            {
                report.Findings.Add(new CheckFinding(CheckRun, Severity.Error, "Запись запрещена: " + plan.WriteBlockedReason)
                {
                    Action = BusySummary.ActionFor(plan.WriteBlockedReason) ?? "Проверьте папку правил отдела (VK Tools → Настройки → «Проверить»).",
                });
            }
            foreach (string note in plan.Notes) report.Findings.Add(new CheckFinding("Сводка", Severity.Info, note));
            foreach (SpecModule module in plan.Modules)
            {
                report.Findings.Add(new CheckFinding(module.Title(), Severity.Info,
                    $"{(run?.Committed == true ? "Записано" : "Будет изменено")}: {plan.ChangeCount(module)}; без изменений: {plan.Unchanged(module)}."));
            }

            foreach (PlanIssue issue in plan.Issues.OrderBy(i => i.Severity))
            {
                string prefix = (issue.Parameter.Length > 0 ? issue.Parameter + " — " : string.Empty) + (issue.Subject.Length > 0 ? issue.Subject + ": " : string.Empty);
                var finding = new CheckFinding(issue.Module.Title(), issue.Severity,
                    prefix + issue.Reason + (issue.Count > 1 ? $" — {issue.Count} шт." : string.Empty))
                {
                    Action = issue.Action,
                };
                if (issue.Ids.Count == 1) finding.ElementId = issue.Ids[0];
                else finding.RelatedElementIds.AddRange(issue.Ids.Distinct());
                report.Findings.Add(finding);
            }

            string verb = run?.Committed == true ? "Записано" : "Будет изменено";
            var problems = new System.Collections.Generic.HashSet<ParamChange>(
                run == null ? Enumerable.Empty<ParamChange>() : run.Failed.Concat(run.Overwritten).Select(p => p.Change));
            foreach (ParamChange change in plan.Changes.Where(c => !problems.Contains(c)))
            {
                report.Findings.Add(Row(change, Severity.Info, $"{verb}: {change.Source}", string.Empty));
            }
            return report;
        }

        private static CheckFinding Row(ParamChange change, Severity severity, string message, string action) =>
            new CheckFinding(change.Module.Title(), severity, change.Parameter + " — " + message)
            {
                ElementId = change.ElementId,
                Category = change.Category,
                Family = change.Family,
                Type = change.Type,
                Current = change.Old,
                Expected = change.New,
                Action = action,
            };
    }
}
