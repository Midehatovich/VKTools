using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Checks;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Mep
{
    /// <summary>Отчёт об объединении для общего окна отчёта: ElementId, что было, что стало, что сделать.</summary>
    internal static class MergeReportBuilder
    {
        public const string CheckMerge = "Объединение систем";
        public const string CheckPlan = "Анализ систем";
        public const string CheckPost = "Итоговая проверка";
        private const string Category = "Трубопроводные системы";

        public static CheckReport Build(string documentTitle, MergePlan plan, MergeRunResult? run, IEnumerable<string> skipped)
        {
            string title = run == null ? "Объединение систем: анализ"
                : run.Mode == MergeMode.Trial ? "Объединение систем: пробный прогон" : "Объединение систем: запись";
            var report = new CheckReport(title, documentTitle, DateTime.Now)
            {
                PluginVersion = BuildInfo.PluginVersion,
                RevitVersion = BuildInfo.RevitVersion,
            };

            var executed = new HashSet<string>(StringComparer.Ordinal);
            if (run != null)
            {
                foreach (CandidateOutcome o in run.Outcomes)
                {
                    executed.Add(o.Candidate.Key);
                    Severity severity = o.State == OutcomeState.Failed ? Severity.Error
                        : o.State == OutcomeState.RolledBack || o.State == OutcomeState.NotRun ? Severity.Warning
                        : Severity.Info;
                    report.Findings.Add(Row(o.Candidate, CheckMerge, severity, $"{o.StateText}. {o.Detail}".Trim(),
                        o.State == OutcomeState.Failed ? "Исправьте причину или снимите отметку и повторите пробный прогон." : string.Empty));
                }
                foreach (string line in run.PostCheck)
                {
                    report.Findings.Add(new CheckFinding(CheckPost, line.Contains("!!!") ? Severity.Error : Severity.Info, line.Replace("!!! ", string.Empty)));
                }
                if (run.DisabledUpdaters.Count > 0)
                {
                    report.Findings.Add(new CheckFinding(CheckMerge, Severity.Warning, MergeRunResult.DisabledUpdatersText(run.DisabledUpdaters)));
                }
                report.Findings.AddRange(run.ElementFindings);
                foreach (string w in run.Warnings) report.Findings.Add(new CheckFinding(CheckMerge, Severity.Warning, w));
                foreach (string e in run.Errors.Where(e => !e.Contains("!!!"))) report.Findings.Add(new CheckFinding(CheckMerge, Severity.Error, e));
                report.Findings.Add(new CheckFinding(CheckMerge, run.Errors.Count > 0 ? Severity.Error : Severity.Info, run.Status));
                if (!string.IsNullOrEmpty(run.Timings)) report.Findings.Add(new CheckFinding(CheckMerge, Severity.Info, "Время: " + run.Timings));
            }
            foreach (string s in skipped)
            {
                report.Findings.Add(new CheckFinding(CheckMerge, Severity.Warning, s) { Action = "Нажмите «Обновить», проверьте строку и повторите." });
            }

            // Частичный анализ (перед записью) — только отмеченные: неотмеченных в нём нет, пропущенные уже выше
            foreach (PlannedCandidate c in plan.Candidates.Where(c => !plan.IsPartial && !executed.Contains(c.Key)))
            {
                switch (c.State)
                {
                    case CandidateState.Rejected:
                        report.Findings.Add(Row(c, CheckPlan, Severity.Warning, "Не объединяется: " + c.Explanation,
                            "Исправьте причину в модели (имя, тип системы, соединения) или оставьте как есть."));
                        break;
                    case CandidateState.NeedsConfirmation:
                        report.Findings.Add(Row(c, CheckPlan, Severity.Info, "Нужна ваша отметка: " + c.Explanation,
                            "Покажите систему в модели; если это копия этой основной — отметьте строку."));
                        break;
                    default:
                        report.Findings.Add(Row(c, CheckPlan, Severity.Info, "Готова к объединению. " + c.Explanation, string.Empty));
                        break;
                }
            }
            return report;
        }

        private static CheckFinding Row(PlannedCandidate c, string check, Severity severity, string message, string action)
        {
            message += $" [эл. {c.ElementIds.Count}, терм. {c.TerminalIds.Count}, уровни: {c.Levels}]";
            var finding = new CheckFinding(check, severity, message)
            {
                ElementId = c.TerminalIds.Count > 0 ? c.TerminalIds[0] : c.ElementIds.Count > 0 ? c.ElementIds.Min() : (long?)null,
                Category = Category,
                Type = c.Record.TypeName,
                Current = c.Record.Name,
                Expected = c.Group.Target != null ? c.Group.BaseName : string.Empty,
                Action = action,
            };
            finding.RelatedElementIds.AddRange(c.ElementIds.OrderBy(x => x));
            return finding;
        }
    }
}
