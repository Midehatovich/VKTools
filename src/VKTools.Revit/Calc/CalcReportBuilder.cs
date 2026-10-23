using System;
using System.Linq;
using VKTools.Core.Calc;
using VKTools.Core.Checks;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Spec;

namespace VKTools.Revit.Calc
{
    /// <summary>
    /// Отчёт «Расчётные позиции» для окна и CSV. Колонки отчёта Р0 используются так: «Категория» — спецификация (область),
    /// «Семейство» — система, «Тип» — код ресурса.
    /// </summary>
    internal static class CalcReportBuilder
    {
        public const string Title = "Расчётные позиции";
        private const string CheckRun = "Запись";
        private const string CheckItems = "Позиции";

        public static CheckReport Build(string documentTitle, CalcPlan plan, CalcRunResult? run)
        {
            var report = new CheckReport(Title + (run == null ? ": анализ" : run.Mode == SpecRunMode.Trial ? ": пробный прогон" : ": запись"), documentTitle, DateTime.Now)
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
                foreach ((string key, long? id, string reason) in run.Failed)
                {
                    report.Findings.Add(new CheckFinding(CheckRun, Severity.Error, $"Не записано «{key}»: {reason}") { ElementId = id, Action = "Проверьте позицию («Показать в модели») и параметры семейства." });
                }
                foreach ((string key, long id, string reason) in run.Overwritten)
                {
                    report.Findings.Add(new CheckFinding(CheckRun, Severity.Warning, $"Перезаписано после фиксации «{key}»: {reason}")
                    {
                        ElementId = id,
                        Action = "Значение меняет другая надстройка (BIM2B) или формула: найдите, кто пишет этот параметр.",
                    });
                }
                if (!string.IsNullOrEmpty(run.Timings)) report.Findings.Add(new CheckFinding(CheckRun, Severity.Info, "Время: " + run.Timings));
            }
            if (plan.WriteBlockedReason != null)
            {
                report.Findings.Add(new CheckFinding(CheckRun, Severity.Error, "Запись запрещена: " + plan.WriteBlockedReason)
                {
                    Action = "Исправьте причину (таблицы на вкладках, папка правил отдела) и нажмите «Обновить».",
                });
            }
            foreach (string note in plan.Notes) report.Findings.Add(new CheckFinding("Сводка", Severity.Info, note));

            foreach (CalcIssue issue in plan.Issues.Items.OrderBy(i => i.Severity))
            {
                var finding = new CheckFinding("Расчёт", issue.Severity, issue.Message + (issue.Count > 1 ? $" — {issue.Count} шт." : string.Empty)) { Action = issue.Action };
                if (issue.Ids.Count == 1) finding.ElementId = issue.Ids[0];
                else finding.RelatedElementIds.AddRange(issue.Ids.Distinct());
                report.Findings.Add(finding);
            }

            bool done = run?.Committed == true;
            if (plan.CreateFamily)
            {
                report.Findings.Add(new CheckFinding(CheckItems, Severity.Info, $"{(done ? "Создано" : "Будет создано")} служебное семейство «{plan.FamilyName}» (обобщённая модель, параметры SYS_)"));
            }
            foreach (CalcBindingFix fix in plan.Bindings)
            {
                report.Findings.Add(new CheckFinding(CheckItems, Severity.Info, $"{(done ? "Добавлена" : "Будет добавлена")} категория «Обобщённые модели» к параметру проекта «{fix.Parameter}»"));
            }
            foreach (CalcTypeOp op in plan.TypeOps.Where(t => t.Error == null && t.HasWork))
            {
                string what = op.SymbolId.HasValue
                    ? "обновить значения: " + string.Join("; ", op.Changes.Select(c => $"{c.Name}: «{c.Old}» → «{c.New}»"))
                    : "создать";
                report.Findings.Add(new CheckFinding(CheckItems, Severity.Info, $"Типоразмер «{op.Code}» ({op.Resource.Name}): {what}")
                {
                    ElementId = op.SymbolId,
                    Type = op.Code,
                });
            }
            foreach (CalcOp op in plan.Ops.OrderBy(o => o.Key, StringComparer.Ordinal))
            {
                report.Findings.Add(Row(op, done));
            }
            return report;
        }

        private static CheckFinding Row(CalcOp op, bool done)
        {
            string[] parts = (op.Key + "||").Split('|');
            CalcTarget? t = op.Target;
            string verb = op.Kind switch
            {
                CalcOpKind.Create => done ? "Создано" : "Будет создано",
                CalcOpKind.DeleteOrphan => done ? "Удалено: устарело" : "Будет удалено: системы или ресурса больше нет в расчёте",
                CalcOpKind.DeleteDuplicate => done ? "Удалено: дубль" : "Будет удалено: повтор ключа (копирование)",
                _ => op.Changes.Count == 0 ? "Без изменений" : done ? "Обновлено" : "Будет обновлено",
            };
            string message = t == null
                ? verb
                : $"{verb}: {t.Resource.Name} — {CalcTables.Format(t.Base)} {CalcTables.BaseLabel(t.Resource.Base)} → {CalcTables.Format(t.Quantity)} → {CalcTables.Format(op.Final)} {t.Resource.Unit}"
                    + (op.Mode == CalcMode.Manual ? " (вручную)" : string.Empty);
            return new CheckFinding(CheckItems, op.Error != null ? Severity.Error : Severity.Info, message)
            {
                ElementId = op.ItemId,
                Category = parts[0],
                Family = parts[1],
                Type = parts[2],
                Current = op.Kind == CalcOpKind.Create ? string.Empty : op.OldQuantity + (op.ManualText.Length > 0 ? $" (вручную {op.ManualText})" : string.Empty),
                Expected = t == null || op.Kind == CalcOpKind.DeleteDuplicate ? "—" : CalcTables.Format(op.Final),
                Action = op.Error ?? Join(string.Join("; ", op.Changes.Where(c => c.Name != CalcParams.Basis && c.Name != CalcParams.Quantity).Select(c => $"{c.Name}: «{c.Old}» → «{c.New}»")),
                    op.Kind == CalcOpKind.Update && op.ManualText.Length == 0 && op.Changes.Any(c => c.Name == CalcParams.Quantity) ? QuantityHint : string.Empty),
            };
        }

        /// <summary>Кол. правят прямо в спецификации — «Применить» вернёт расчёт; закрепляют через параметр ручного значения.</summary>
        private const string QuantityHint = "Если «Кол.» меняли руками в спецификации — «Применить» вернёт расчёт; своё значение впишите в «" + CalcParams.Manual + "».";

        private static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + "; " + b;
    }
}
