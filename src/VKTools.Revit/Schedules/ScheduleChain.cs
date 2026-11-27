using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Calc;
using VKTools.Core.Logging;
using VKTools.Core.Progress;
using VKTools.Core.Spec;
using VKTools.Revit.Calc;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Spec;

namespace VKTools.Revit.Schedules
{
    /// <summary>Функции VK Tools, которые «Создать спецификацию» применяет после создания (в порядке выпуска).</summary>
    internal enum ChainStep
    {
        Insulation,
        Grouping,
        Calc,
        PipeReserve,
        PipeNames,
        Vendor,

        /// <summary>Позиции новой спецификации — последним: номера зависят от наименований и марок.</summary>
        Positions,
    }

    internal sealed class ChainStepResult
    {
        public ChainStepResult(string title)
        {
            Title = title;
        }

        public string Title { get; }

        public string Summary { get; set; } = string.Empty;

        /// <summary>Шаг не выполнен и это не ошибка (нет настройки, запись запрещена правилами отдела).</summary>
        public bool Skipped { get; set; }

        /// <summary>Ошибка: вся операция откатывается.</summary>
        public string? Error { get; set; }

        public override string ToString() => $"{Title}: {Error ?? Summary}";
    }

    /// <summary>
    /// Запуск функций с настройками, сохранёнными в модели (или по умолчанию), без окон: план → запись. Каждая функция пишет своей
    /// группой транзакций; вызывающий держит общую группу и решает — Assimilate (одна отмена) или RollBack.
    /// </summary>
    internal static class ScheduleChain
    {
        public static readonly IReadOnlyList<ChainStep> Order = new[] { ChainStep.Insulation, ChainStep.Grouping, ChainStep.Calc, ChainStep.PipeReserve, ChainStep.PipeNames, ChainStep.Vendor, ChainStep.Positions };

        public static string Title(ChainStep step) => step switch
        {
            ChainStep.Insulation => "Изоляция",
            ChainStep.Grouping => "Группирование",
            ChainStep.Calc => "Расчётные позиции",
            ChainStep.PipeReserve => "Запас труб",
            ChainStep.Vendor => "Марки и поставщики",
            ChainStep.Positions => "Позиции",
            _ => "Имена труб",
        };

        public static ChainStepResult Run(Document doc, ChainStep step, string scheduleName, IOperationProgress progress, OperationTimings timings)
        {
            progress.ThrowIfCancelled();
            if (step == ChainStep.Calc) return RunCalc(doc, scheduleName, progress, timings);
            if (step == ChainStep.Positions) return RunPositions(doc, scheduleName, progress, timings);
            SpecModule module = step switch
            {
                ChainStep.Insulation => SpecModule.Insulation,
                ChainStep.Grouping => SpecModule.Grouping,
                ChainStep.PipeReserve => SpecModule.PipeReserve,
                ChainStep.Vendor => SpecModule.Vendor,
                _ => SpecModule.PipeNames,
            };
            return RunSpec(doc, module, Title(step), progress, timings);
        }

        /// <summary>Модули Р2/Р4/Р5: настройка модели → план → запись.</summary>
        public static ChainStepResult RunSpec(Document doc, SpecModule module, string title, IOperationProgress progress, OperationTimings timings)
        {
            progress.Report(title + ": анализ…");
            (SpecOptions options, SpecDefaults defaults) = SpecSettingsLoader.Load(doc, module);
            ParamPlan plan = SpecPlanner.Build(doc, options, defaults, null, progress, timings);
            return Apply(doc, plan, options.SavedInProject, title, progress, timings);
        }

        /// <summary>Позиции только новой спецификации, с настройкой окна «Позиции» (режим вложенных, таблица «не трогать»).</summary>
        private static ChainStepResult RunPositions(Document doc, string scheduleName, IOperationProgress progress, OperationTimings timings)
        {
            string title = Title(ChainStep.Positions);
            progress.Report(title + ": анализ…");
            (SpecOptions options, SpecDefaults defaults) = SpecSettingsLoader.Load(doc, SpecModule.Positions);
            PositionSettings ps = options.Positions.Clone();
            ps.OnlySchedules = new[] { scheduleName };
            options.Positions = ps;
            ParamPlan plan = SpecPlanner.Build(doc, options, defaults, null, progress, timings);
            ChainStepResult result = Apply(doc, plan, options.SavedInProject, title, progress, timings);
            string? line = plan.Preview.FirstOrDefault();
            if (line != null && result.Error == null) result.Summary += " " + line;
            return result;
        }

        /// <summary>Записать готовый план (без окна). Ошибка записи — в <see cref="ChainStepResult.Error"/>.</summary>
        public static ChainStepResult Apply(Document doc, ParamPlan plan, bool savedInProject, string title, IOperationProgress progress, OperationTimings timings)
        {
            var result = new ChainStepResult(title);
            string setting = savedInProject ? "настройка модели" : "настройка по умолчанию";
            string note = plan.Notes.FirstOrDefault(n => n.StartsWith(title + ":", StringComparison.Ordinal)) is string n0 ? " " + n0.Substring(title.Length + 1).Trim() : string.Empty;
            if (plan.WriteBlockedReason != null)
            {
                result.Skipped = true;
                result.Summary = "пропущено — запись запрещена: " + plan.WriteBlockedReason;
                return result;
            }
            if (plan.Changes.Count == 0)
            {
                result.Summary = $"изменений нет ({setting})." + note;
                return result;
            }
            progress.Report(title + ": запись…");
            SpecRunResult run = SpecExecutor.Run(doc, plan, SpecRunMode.Apply, progress, timings, Host.Log);
            if (run.Cancelled) throw new OperationCanceledException();
            if (!run.Committed || run.Errors.Count > 0)
            {
                result.Error = run.Errors.Count > 0 ? string.Join("; ", run.Errors.Take(3)) : run.Status;
                return result;
            }
            result.Summary = $"записано значений {run.Written} из {run.Planned} ({setting})"
                + (run.Failed.Count > 0 ? $", не записалось {run.Failed.Count} (подробности в логе)" : string.Empty)
                + (run.Overwritten.Count > 0 ? $", переписано другой надстройкой {run.Overwritten.Count}" : string.Empty) + "." + note;
            foreach (ChangeProblem problem in run.Failed.Take(50)) Host.Log.Warn($"  {title}: не записано — {problem.Reason}");
            return result;
        }

        /// <summary>
        /// Расчётные позиции: новая спецификация добавляется к отмеченным (и сохраняется в настройке Р3), позиции остальных
        /// пересчитываются как обычно. Если отмеченных нет (расчёт по всей модели) — шаг пропускается: иначе позиции «МОДЕЛЬ» удалились бы.
        /// </summary>
        private static ChainStepResult RunCalc(Document doc, string scheduleName, IOperationProgress progress, OperationTimings timings)
        {
            var result = new ChainStepResult(Title(ChainStep.Calc));
            progress.Report("Расчётные позиции: анализ…");
            (CalcOptions options, CalcDefaults defaults) = CalcSettingsLoader.Load(doc);
            if (options.Schedules.Count == 0)
            {
                result.Skipped = true;
                result.Summary = "пропущено — в настройке не отмечены спецификации (расчёт по всей модели). Отметьте спецификации в окне «Расчётные позиции»";
                return result;
            }
            string before = options.Signature();
            bool added = !options.Schedules.Contains(scheduleName, StringComparer.Ordinal);
            if (added) options.Schedules.Add(scheduleName);
            CalcPlan plan = CalcPlanner.Build(doc, options, defaults, progress, timings);
            string? covered = added ? Overlap(plan, scheduleName) : null;
            if (covered != null)
            {
                // Трубы новой спецификации уже считаются в отмеченной: своя область дала бы вторые копии позиций, и обе спецификации
                // показали бы обе копии. Новая не добавляется — позиции отмеченной видны в ней, если проходят её фильтры.
                options.Schedules.Remove(scheduleName);
                plan = CalcPlanner.Build(doc, options, defaults, progress, timings);
                result.Summary = $"новая спецификация не добавлена в расчёт — её трубы уже считаются в {covered}; "
                    + "в новой видны позиции оттуда (по её фильтрам). ";
            }
            if (plan.WriteBlockedReason != null)
            {
                result.Skipped = true;
                result.Summary = "пропущено — запись запрещена: " + plan.WriteBlockedReason;
                return result;
            }
            Func<Document, string?>? save = options.Signature() != before ? d => CalcSettingsLoader.Store(d, options, defaults) : (Func<Document, string?>?)null;
            if (plan.ChangeCount == 0)
            {
                // Позиции уже по расчёту — не ошибка (исполнитель в этом случае ничего не фиксирует)
                result.Summary += "изменений нет: позиции уже такие, как по расчёту.";
                return result;
            }
            progress.Report("Расчётные позиции: запись…");
            CalcRunResult run = CalcExecutor.Run(doc, plan, SpecRunMode.Apply, progress, timings, Host.Log, save);
            if (run.Cancelled) throw new OperationCanceledException();
            if (!run.Committed || run.Errors.Count > 0)
            {
                result.Error = run.Errors.Count > 0 ? string.Join("; ", run.Errors.Take(3)) : run.Status;
                return result;
            }
            result.Summary += $"позиций создано {run.Created}, обновлено {run.Updated}, удалено {run.Deleted}"
                + (run.SettingsSaved ? "; спецификация добавлена в настройку «Расчётных позиций»" : string.Empty)
                + (run.Failed.Count > 0 ? $"; не записалось {run.Failed.Count}" : string.Empty)
                + (plan.Issues.Items.Any(i => i.Message.Contains("посчитаны дважды")) ? "; ВНИМАНИЕ: отмеченные спецификации пересекаются — материалы посчитаны дважды, см. окно «Расчётные позиции»" : string.Empty);
            return result;
        }

        /// <summary>Пересекается ли новая область по трубам с уже отмеченными: имена этих спецификаций или null.</summary>
        private static string? Overlap(CalcPlan plan, string scheduleName)
        {
            CalcScope? mine = plan.Scopes.FirstOrDefault(s => s.Name == scheduleName);
            if (mine == null || mine.Pipes.Count == 0) return null;
            var ids = new HashSet<long>(mine.Pipes.Select(p => p.Id));
            List<string> others = plan.Scopes.Where(s => s != mine && s.Pipes.Any(p => ids.Contains(p.Id))).Select(s => "«" + s.Name + "»").ToList();
            return others.Count == 0 ? null : string.Join(", ", others);
        }
    }
}
