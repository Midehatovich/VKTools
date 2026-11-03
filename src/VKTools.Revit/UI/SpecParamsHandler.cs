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
        Save,

        /// <summary>«+ Из выделения» во вкладке «Комплекты»: прочитать выделение Revit.</summary>
        ReadSelection,
    }

    /// <summary>
    /// Действия окна модуля Р2 в контексте Revit API. Анализ — заново перед каждым действием, с настройками,
    /// которые были в окне при нажатии кнопки; правила по умолчанию и настройка проекта перечитываются каждый раз.
    /// </summary>
    internal sealed class SpecParamsHandler : IExternalEventHandler
    {
        private SpecParamsWindow? _window;
        private SpecRequest _request;
        private SpecOptions? _options;
        private List<ElementId>? _scope;

        /// <param name="scope">Элементы области из последнего «Обновить» (для пробного прогона и записи); null — взять заново.</param>
        public void Set(SpecParamsWindow window, SpecRequest request, SpecOptions options, List<ElementId>? scope)
        {
            _window = window;
            _request = request;
            _options = options;
            _scope = scope;
        }

        public void Execute(UIApplication app)
        {
            SpecParamsWindow? window = _window;
            SpecOptions? options = _options;
            if (window == null || options == null) return;
            Document doc = window.Document;
            var timings = new OperationTimings { Trace = line => Host.Log.Info("  этап " + line) };
            string title = options.Module.Title();
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
                if (_request == SpecRequest.ReadSelection)
                {
                    window.ShowSelectedFamilies(SelectedTopFamilies(app.ActiveUIDocument));
                    return;
                }
                SpecParamsHost.RememberScope(options.Module, options.Scope);
                (SpecOptions stored, SpecDefaults defaults) = SpecSettingsLoader.Load(doc, options.Module);

                if (_request == SpecRequest.Save)
                {
                    Save(doc, window, options, defaults);
                    return;
                }

                List<ElementId>? scope = _scope;
                string scopeLabel;
                if (_request == SpecRequest.Refresh || (scope == null && options.Scope != NestedScope.Model))
                {
                    scope = SpecScope.Resolve(app.ActiveUIDocument, options, out scopeLabel);
                }
                else
                {
                    scopeLabel = SpecScope.Label(options, scope);
                }
                window.ShowSettingsState(stored, defaults);

                if (_request == SpecRequest.Refresh)
                {
                    ParamPlan fresh;
                    using (var progress = new ProgressScope(app, "анализ: " + title, window))
                    {
                        fresh = SpecPlanner.Build(doc, options, defaults, scope, progress, timings);
                    }
                    LogPlan(doc, fresh, timings);
                    window.ShowPlan(fresh, null, null, SpecReportBuilder.Build(doc.Title, fresh, null), options, scope, scopeLabel);
                    return;
                }
                if (doc.IsReadOnly)
                {
                    window.ShowMessage("Документ сейчас только для чтения: запись невозможна.");
                    return;
                }

                SpecRunMode mode = _request == SpecRequest.Apply ? SpecRunMode.Apply : SpecRunMode.Trial;
                ParamPlan plan;
                ParamPlan? after = null;
                SpecRunResult run;
                SpecOptions used = options;
                using (var progress = new ProgressScope(app, (mode == SpecRunMode.Apply ? "запись: " : "пробный прогон: ") + title, window))
                {
                    plan = SpecPlanner.Build(doc, options, defaults, scope, progress, timings);
                    // Номера K новых типоразмеров комплектов сохраняются вместе с первой записью значений: дальше они не меняются (D-039)
                    SpecOptions toStore = options;
                    if (plan.NewKitNumbers.Count > 0)
                    {
                        toStore = options.Clone();
                        toStore.Kits = options.Kits.WithNumbers(plan.NewKitNumbers);
                    }
                    bool saveSettings = mode == SpecRunMode.Apply && toStore.SettingsSignature() != stored.SettingsSignature();
                    Host.Log.Info($"{title} «{doc.Title}»: {(mode == SpecRunMode.Apply ? "запись" : "пробный прогон")}, область: {scopeLabel}"
                        + (saveSettings ? ", с сохранением настройки в модели" + (plan.NewKitNumbers.Count > 0 ? $" (новых номеров K: {plan.NewKitNumbers.Count})." : ".") : "."));
                    LogPlan(doc, plan, timings);
                    if (mode == SpecRunMode.Apply && plan.WriteBlockedReason != null)
                    {
                        window.ShowPlan(plan, null, null, SpecReportBuilder.Build(doc.Title, plan, null), options, scope, scopeLabel);
                        window.ShowMessage("Запись запрещена: " + plan.WriteBlockedReason);
                        return;
                    }
                    Func<Document, string?>? save = saveSettings ? d => SpecSettingsStore.Store(d, toStore, defaults) : (Func<Document, string?>?)null;
                    run = SpecExecutor.Run(doc, plan, mode, progress, timings, Host.Log, save);
                    if (run.SettingsSaved) used = toStore;
                    if (run.Failed.Any(f => f.Reason.StartsWith(FamilyDiagnostics.SilentFailure, StringComparison.Ordinal)))
                    {
                        progress.Report("Проверка семейств, где Revit не сохранил значение…");
                        using (timings.Measure("проверка семейств")) run.Diagnoses.AddRange(FamilyDiagnostics.Explain(doc, run.Failed, Host.Log));
                    }
                    if (run.Committed)
                    {
                        try
                        {
                            after = SpecPlanner.Build(doc, used, defaults, scope, progress, timings);
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
                    (SpecOptions saved, SpecDefaults savedDefaults) = SpecSettingsLoader.Load(doc, options.Module);
                    window.ShowSettingsState(saved, savedDefaults);
                }
                window.ShowPlan(plan, run, after, SpecReportBuilder.Build(doc.Title, plan, run), used, scope, scopeLabel);
            }
            catch (OperationCanceledException)
            {
                Host.Log.Info($"{title}: отменено пользователем на этапе анализа. Время: {timings.Summary()}");
                window.ShowMessage("Отменено на этапе анализа: модель не изменялась.", error: false);
            }
            catch (Exception ex)
            {
                Host.Log.Error($"{title}: ошибка. Время: {timings.Summary()}", ex);
                window.ShowMessage("Ошибка: " + ex.Message + " Модель не изменена, если запись не была подтверждена сообщением. Подробности в логе.");
            }
        }

        public string GetName() => "VK Tools: параметры спецификации";

        /// <summary>Семейства верхних родителей выделенных элементов (выделили вложенное — берётся его родитель).</summary>
        private static List<string> SelectedTopFamilies(UIDocument uidoc)
        {
            var result = new List<string>();
            foreach (ElementId id in uidoc.Selection.GetElementIds())
            {
                var fi = uidoc.Document.GetElement(id) as FamilyInstance;
                for (int depth = 0; depth < 20 && fi?.SuperComponent is FamilyInstance super; depth++) fi = super;
                string? family = fi?.Symbol?.FamilyName;
                if (!string.IsNullOrEmpty(family) && !result.Contains(family!, StringComparer.OrdinalIgnoreCase)) result.Add(family!);
            }
            return result;
        }

        private static void Save(Document doc, SpecParamsWindow window, SpecOptions options, SpecDefaults defaults)
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
                problem = SpecSettingsStore.Save(doc, options, defaults);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                problem = ex.Message;
            }
            (SpecOptions stored, SpecDefaults fresh) = SpecSettingsLoader.Load(doc, options.Module);
            window.ShowSettingsState(stored, fresh);
            if (problem != null)
            {
                Host.Log.Warn($"{options.Module.Title()} «{doc.Title}»: настройка не сохранена — {problem}");
                window.ShowMessage("Настройка не сохранена: " + problem);
                return;
            }
            int kits = options.Kits.Rules.Select(r => r.Family).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            bool removed = toDefault && (options.Module != SpecModule.Grouping || kits == 0);
            string what = options.Module == SpecModule.SystemFloor ? SystemFloorText(options.SystemFloor)
                : options.Module == SpecModule.Nested
                ? "параметры: " + string.Join(", ", options.NestedParams.Select(k => k.Name)) + (options.NestedClearEmpty ? "; пустое очищает вложенное" : string.Empty)
                : $"строк таблицы групп: {options.GroupRules.Count}{(toDefault ? " (по умолчанию)" : string.Empty)}, «не трогать»: {options.GroupRules.Count(r => r.Skip)}; "
                    + $"комплектов: {kits}" + (kits > 0 ? " (" + string.Join(", ", options.Kits.Rules.Select(r => r.Label)) + $"), номеров K: {options.Kits.Numbers.Count}" : string.Empty);
            Host.Log.Info($"{options.Module.Title()} «{doc.Title}»: настройка {(removed ? "сброшена на умолчание (удалена из модели)" : "сохранена в модели")}; {what}.");
            string sync = doc.IsWorkshared ? " Синхронизируйте модель, чтобы коллеги увидели её." : string.Empty;
            window.ShowMessage((removed
                ? "Настройка проекта удалена: работают правила по умолчанию."
                : $"Настройка сохранена в модели (в журнале отмены — «{SpecSettingsStore.TransactionName}»)."
                    + (toDefault && options.Module == SpecModule.Grouping ? " Таблица групп — по умолчанию." : string.Empty)) + sync, error: false);
        }

        private static string SystemFloorText(SystemFloorSettings sf) =>
            $"система → {(sf.WriteSystem ? sf.SystemParam?.Name ?? "не выбран" : "не пишется")}, этаж → {(sf.WriteFloor ? sf.FloorParam?.Name ?? "не выбран" : "не пишется")}, "
            + $"изоляция {(sf.Insulation ? "да" : "нет")}, перенос во вложенные {(sf.ChainNested ? "да" : "нет")}, категорий {sf.Categories.Count}, "
            + $"переименований уровней {sf.EffectiveFloors().Count()}";

        internal static void LogPlan(Document doc, ParamPlan plan, OperationTimings timings)
        {
            Host.Log.Info($"Анализ «{doc.Title}»: " + string.Join("; ", plan.Modules.Select(m =>
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
            foreach (FamilyDiagnosis d in run.Diagnoses) Host.Log.Warn($"  причина в семействе «{d.Family}» ({d.Parameter}, {d.Ids.Count} шт.): {d.Message} → {d.Action}");
            foreach (string w in run.Warnings) Host.Log.Warn("  " + w);
            foreach (string e in run.Errors) Host.Log.Warn("  ошибка: " + e);
            if (run.SettingsSaved) Host.Log.Info("  настройка сохранена в модели той же операцией");
            if (after != null) Host.Log.Info($"  повторный анализ после записи: изменений {after.Changes.Count}");
            Host.Log.Info("  итог: " + run.Status);
            Host.Log.Info("  время: " + run.Timings);
        }
    }

    /// <summary>Элементы области переноса: берутся при «Обновить», те же идут в пробный прогон и запись.</summary>
    internal static class SpecScope
    {
        /// <summary>null — вся модель.</summary>
        public static List<ElementId>? Resolve(UIDocument uidoc, SpecOptions options, out string label)
        {
            Document doc = uidoc.Document;
            if (options.Module != SpecModule.Nested || options.Scope == NestedScope.Model)
            {
                label = "вся модель";
                return null;
            }
            if (options.Scope == NestedScope.Selection)
            {
                List<ElementId> selected = uidoc.Selection.GetElementIds().ToList();
                label = Label(options, selected);
                return selected;
            }
            View? view = doc.ActiveView;
            if (view == null)
            {
                label = "активный вид (его нет)";
                return new List<ElementId>();
            }
            try
            {
                List<ElementId> visible = new FilteredElementCollector(doc, view.Id).OfClass(typeof(FamilyInstance)).ToElementIds().ToList();
                label = $"вид «{view.Name}», элементов {visible.Count}";
                return visible;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                label = $"вид «{view.Name}» не показывает элементы модели";
                return new List<ElementId>();
            }
        }

        public static string Label(SpecOptions options, ICollection<ElementId>? scope) =>
            scope == null ? "вся модель"
            : options.Scope == NestedScope.Selection ? $"выделенные, элементов {scope.Count}"
            : $"активный вид, элементов {scope.Count}";
    }

    /// <summary>По окну на модуль; события создаются командой (контекст Revit API).</summary>
    internal static class SpecParamsHost
    {
        private static readonly Dictionary<SpecModule, Slot> Slots = new Dictionary<SpecModule, Slot>();
        private static readonly Dictionary<SpecModule, NestedScope> Scopes = new Dictionary<SpecModule, NestedScope>();
        private static SelectElementsHandler? _selectHandler;
        private static ExternalEvent? _selectEvent;

        /// <summary>Область последнего запуска в этом сеансе Revit (в модели не хранится).</summary>
        public static NestedScope LastScope(SpecModule module) => Scopes.TryGetValue(module, out NestedScope scope) ? scope : NestedScope.Model;

        public static void RememberScope(SpecModule module, NestedScope scope) => Scopes[module] = scope;

        public static void Show(UIApplication app, Document doc, SpecOptions options, SpecDefaults defaults, ParamPlan plan, List<ElementId>? scope, string scopeLabel)
        {
            if (!Slots.TryGetValue(options.Module, out Slot? slot))
            {
                var handler = new SpecParamsHandler();
                slot = new Slot(handler, ExternalEvent.Create(handler));
                Slots[options.Module] = slot;
            }
            if (_selectHandler == null || _selectEvent == null)
            {
                _selectHandler = new SelectElementsHandler();
                _selectEvent = ExternalEvent.Create(_selectHandler);
            }

            slot.Window?.Close();
            var window = new SpecParamsWindow(doc, options, defaults, slot.Handler, slot.Event, _selectHandler, _selectEvent);
            window.AttachTo(app.MainWindowHandle);
            window.Closed += (s, e) =>
            {
                if (ReferenceEquals(slot.Window, s)) slot.Window = null;
            };
            slot.Window = window;
            window.ShowPlan(plan, null, null, SpecReportBuilder.Build(doc.Title, plan, null), options, scope, scopeLabel);
            window.Show();
        }

        public static void CloseAll()
        {
            foreach (Slot slot in Slots.Values)
            {
                slot.Window?.Close();
                slot.Window = null;
                slot.Event.Dispose();
            }
            Slots.Clear();
            _selectEvent?.Dispose();
            _selectEvent = null;
            _selectHandler = null;
        }

        private sealed class Slot
        {
            public Slot(SpecParamsHandler handler, ExternalEvent externalEvent)
            {
                Handler = handler;
                Event = externalEvent;
            }

            public SpecParamsHandler Handler { get; }

            public ExternalEvent Event { get; }

            public SpecParamsWindow? Window { get; set; }
        }
    }
}
