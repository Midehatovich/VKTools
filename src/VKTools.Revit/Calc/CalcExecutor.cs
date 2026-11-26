using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using VKTools.Core.Calc;
using VKTools.Core.Logging;
using VKTools.Core.Progress;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Mep;
using VKTools.Revit.Spec;

namespace VKTools.Revit.Calc
{
    /// <summary>Итог пробного прогона или записи расчётных позиций.</summary>
    internal sealed class CalcRunResult
    {
        public CalcRunResult(SpecRunMode mode)
        {
            Mode = mode;
        }

        public SpecRunMode Mode { get; }

        public bool FamilyCreated { get; set; }

        public int Bindings { get; set; }

        public int TypesCreated { get; set; }

        public int TypesUpdated { get; set; }

        public int Created { get; set; }

        public int Updated { get; set; }

        public int Deleted { get; set; }

        /// <summary>Позиция или типоразмер, которые не записались: ключ и причина.</summary>
        public List<(string Key, long? Id, string Reason)> Failed { get; } = new List<(string, long?, string)>();

        /// <summary>После фиксации значение другое: его переписал апдейтер другой надстройки или формула.</summary>
        public List<(string Key, long Id, string Reason)> Overwritten { get; } = new List<(string, long, string)>();

        public List<string> Warnings { get; } = new List<string>();

        /// <summary>Ошибки, из-за которых «Применить» запрещена: фиксация не прошла, позиции не видны в своих спецификациях.</summary>
        public List<string> Errors { get; } = new List<string>();

        public List<string> DisabledUpdaters { get; } = new List<string>();

        public bool SettingsSaved { get; set; }

        public bool Committed { get; set; }

        public bool Cancelled { get; set; }

        public string Status { get; set; } = string.Empty;

        public string Timings { get; set; } = string.Empty;

        public bool Passed => Errors.Count == 0 && !Cancelled;
    }

    /// <summary>
    /// Запись расчётных позиций (apply_all эталона): TransactionGroup «VK Tools: расчётные позиции» → транзакция «семейство, привязки,
    /// типоразмеры» → транзакция «позиции» (каждая позиция в своей SubTransaction: ошибка одной откатывает только её) → проверка после
    /// фиксации (значения и попадание в свою спецификацию) → откат (пробный прогон) или Assimilate (одна запись в журнале отмены).
    /// </summary>
    internal static class CalcExecutor
    {
        public const string OperationName = "VK Tools: расчётные позиции";

        public static CalcRunResult Run(Document doc, CalcPlan plan, SpecRunMode mode, IOperationProgress progress, OperationTimings timings, ILog log,
            Func<Document, string?>? saveSettings = null)
        {
            var result = new CalcRunResult(mode);
            if (plan.ChangeCount == 0)
            {
                result.Status = "Записывать нечего: позиции уже такие, как по расчёту.";
                return result;
            }

            string? familyPath = null;
            if (plan.CreateFamily)
            {
                progress.Report("Создание служебного семейства…");
                try
                {
                    using (timings.Measure("семейство")) familyPath = CalcFamilyBuilder.Build(doc.Application, plan.FamilyName, log);
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is Autodesk.Revit.Exceptions.ApplicationException)
                {
                    log.Error("Служебное семейство не создано.", ex);
                    result.Errors.Add("служебное семейство не создано: " + ex.Message);
                    result.Status = "Служебное семейство не создано — модель не изменена.";
                    return result;
                }
            }

            var touched = new List<(CalcOp Op, Element Element)>();
            bool fatal = false;
            using (var operation = new TransactionGroup(doc, OperationName))
            {
                operation.Start();
                try
                {
                    var failures = new FailureCollector(SystemMergeExecutor.RegisteredUpdaters(doc), SystemMergeExecutor.DisableOnFailureApplications);
                    // Все позиции стоят в одной точке (в спецификации места нет): «идентичные экземпляры» — ожидаемо, не в отчёт
                    failures.Quiet.Add(BuiltInFailures.OverlapFailures.DuplicateInstances.Guid);
                    var symbols = new Dictionary<string, FamilySymbol>(StringComparer.Ordinal);
                    using (Transaction prepare = FailureCollector.Start(doc, "VK Tools: служебное семейство и типоразмеры", failures))
                    {
                        using (timings.Measure("семейство, привязки, типоразмеры"))
                        {
                            Family family = familyPath != null ? CalcFamilyBuilder.Load(doc, familyPath)
                                : (Family)doc.GetElement(plan.FamilyId!.Value.ToElementId());
                            result.FamilyCreated = familyPath != null;
                            BuiltInCategory category = family.FamilyCategory?.ToBuiltInCategory() ?? BuiltInCategory.OST_GenericModel;
                            foreach (CalcBindingFix fix in plan.Bindings)
                            {
                                string? problem = Rebind(doc, fix, category);
                                if (problem != null) result.Errors.Add($"параметр «{fix.Parameter}»: {problem}");
                                else result.Bindings++;
                            }
                            doc.Regenerate();
                            List<FamilySymbol> existing = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>().OrderBy(s => s.Id.ToLong()).ToList();
                            FamilySymbol baseSymbol = existing.First();
                            foreach (FamilySymbol symbol in existing) symbols[symbol.Name] = symbol;
                            foreach (CalcTypeOp op in plan.TypeOps)
                            {
                                if (op.Error != null) continue;
                                PrepareType(doc, op, baseSymbol, symbols, result);
                            }
                        }
                        progress.Report("Фиксация: типоразмеры и привязки…");
                        if (!Commit(prepare, failures, result))
                        {
                            fatal = true;
                        }
                    }

                    if (!fatal)
                    {
                        using (Transaction items = FailureCollector.Start(doc, "VK Tools: позиции", failures))
                        {
                            using (timings.Measure("позиции"))
                            {
                                var levels = new Dictionary<long, Level?>();
                                string? createBlock = null;
                                List<CalcOp> ops = plan.Ops.Where(o => o.Error == null && o.HasWork).ToList();
                                for (int i = 0; i < ops.Count; i++)
                                {
                                    if (i % 20 == 0)
                                    {
                                        progress.Report($"Позиции: {i + 1} из {ops.Count}", i, ops.Count);
                                        progress.ThrowIfCancelled();
                                    }
                                    CalcOp op = ops[i];
                                    if (op.Kind == CalcOpKind.Create && createBlock != null)
                                    {
                                        result.Failed.Add((op.Key, null, createBlock));
                                        continue;
                                    }
                                    using (var sub = new SubTransaction(doc))
                                    {
                                        sub.Start();
                                        try
                                        {
                                            Element? element = Apply(doc, plan, op, symbols, levels, result);
                                            sub.Commit();
                                            if (element != null) touched.Add((op, element));
                                        }
                                        catch (Exception ex) when (ex is InvalidOperationException || ex is Autodesk.Revit.Exceptions.ApplicationException)
                                        {
                                            if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                                            result.Failed.Add((op.Key, op.ItemId, ex.Message));
                                            if (op.Kind == CalcOpKind.Create && ex is InvalidOperationException) createBlock = "служебное семейство или привязки параметров не готовы: " + ex.Message;
                                        }
                                    }
                                }
                            }
                            progress.Report("Фиксация: Revit пересчитывает модель и запускает обновления других надстроек…");
                            using (timings.Measure("фиксация")) fatal = !Commit(items, failures, result);
                        }
                    }

                    if (!fatal)
                    {
                        using (timings.Measure("проверка после фиксации"))
                        {
                            progress.Report("Проверка: значения и попадание в спецификации…");
                            Verify(doc, plan, touched, result);
                        }
                    }

                    if (failures.QuietCount > 0) log.Info($"  снято предупреждений Revit «идентичные экземпляры в одном месте»: {failures.QuietCount}");
                    if (mode == SpecRunMode.Trial || fatal)
                    {
                        progress.Report("Откат всей операции…");
                        using (timings.Measure("откат")) operation.RollBack();
                        result.Status = mode == SpecRunMode.Trial && !fatal
                            ? $"Пробный прогон: {Summary(result, true)}. Всё откачено, модель не изменена."
                            : "Ошибка фиксации — откат всей операции. Модель не изменена.";
                    }
                    else
                    {
                        if (saveSettings != null) SaveSettings(doc, saveSettings, result);
                        progress.Report("Запись в журнал отмены…");
                        using (timings.Measure("запись (Assimilate)")) operation.Assimilate();
                        result.Committed = true;
                        result.Status = $"Записано: {Summary(result, false)} — одной операцией «{OperationName}», Ctrl+Z отменяет её целиком.";
                        int touchedCount = result.Created + result.Updated + result.Deleted + result.TypesCreated + result.TypesUpdated;
                        if (doc.IsWorkshared && touchedCount > 0) result.Warnings.Add(BusySummary.SyncReminder(touchedCount));
                    }
                }
                catch (Exception ex)
                {
                    if (operation.HasStarted() && !operation.HasEnded()) operation.RollBack();
                    bool wasCancel = ex is OperationCanceledException || progress.IsCancellationRequested;
                    if (!wasCancel)
                    {
                        log.Error(OperationName + ": запись прервана, всё откачено.", ex);
                        result.Errors.Add("прервано: " + ex.Message);
                    }
                    result.Cancelled = wasCancel;
                    result.Status = wasCancel ? "Отменено: всё откачено, модель не изменена." : "Ошибка — откат всей операции. Модель не изменена.";
                }
            }
            if (result.Failed.Count > 0) result.Warnings.Add($"Не записано позиций и типоразмеров: {result.Failed.Count} — строки «Не записано» в отчёте.");
            if (result.Overwritten.Count > 0)
            {
                result.Warnings.Add($"После фиксации значение переписано у {result.Overwritten.Count}: его меняет другая надстройка (апдейтер) или формула.");
            }
            result.DisabledUpdaters.AddRange(SystemMergeExecutor.SessionDisabled);
            return result;
        }

        private static string Summary(CalcRunResult r, bool trial)
        {
            var parts = new List<string>();
            if (r.FamilyCreated) parts.Add(trial ? "семейство создалось бы" : "создано служебное семейство");
            if (r.Bindings > 0) parts.Add($"категория добавлена к параметрам: {r.Bindings}");
            if (r.TypesCreated + r.TypesUpdated > 0) parts.Add($"типоразмеров создано {r.TypesCreated}, обновлено {r.TypesUpdated}");
            parts.Add($"позиций создано {r.Created}, обновлено {r.Updated}, удалено {r.Deleted}");
            if (r.Failed.Count > 0) parts.Add($"не записано {r.Failed.Count}");
            return string.Join("; ", parts);
        }

        private static bool Commit(Transaction transaction, FailureCollector failures, CalcRunResult result)
        {
            TransactionStatus status = transaction.Commit();
            result.Warnings.AddRange(failures.Warnings.Distinct().Take(50).Select(w => "Revit: " + w).Where(w => !result.Warnings.Contains(w)));
            if (failures.DisabledUpdaters.Count > 0) SystemMergeExecutor.RememberDisabled(failures.DisabledUpdaters);
            if (status == TransactionStatus.Committed) return true;
            result.Errors.Add("Revit отклонил фиксацию" + (failures.Errors.Count > 0 ? ": " + string.Join("; ", failures.Errors.Distinct().Take(10)) : $" ({status})."));
            return false;
        }

        /// <summary>Добавить категорию позиций к привязке параметра проекта; остальные категории и группа параметра не меняются.</summary>
        private static string? Rebind(Document doc, CalcBindingFix fix, BuiltInCategory category)
        {
            Category? target = Category.GetCategory(doc, category);
            if (target == null) return "нет категории позиций в проекте";
            var current = doc.ParameterBindings.get_Item(fix.Definition) as ElementBinding;
            if (current == null) return "привязка параметра не найдена";
            CategorySet categories = doc.Application.Create.NewCategorySet();
            foreach (Category c in current.Categories) categories.Insert(c);
            categories.Insert(target);
            ElementBinding binding = current is InstanceBinding
                ? doc.Application.Create.NewInstanceBinding(categories)
                : doc.Application.Create.NewTypeBinding(categories);
            return doc.ParameterBindings.ReInsertKeepingGroup(fix.Definition, binding) ? null : "Revit не изменил привязку";
        }

        private static void PrepareType(Document doc, CalcTypeOp op, FamilySymbol baseSymbol, Dictionary<string, FamilySymbol> symbols, CalcRunResult result)
        {
            using (var sub = new SubTransaction(doc))
            {
                sub.Start();
                try
                {
                    bool created = !symbols.TryGetValue(op.Code, out FamilySymbol? symbol);
                    if (symbol == null)
                    {
                        symbol = baseSymbol.Duplicate(op.Code) as FamilySymbol ?? throw new InvalidOperationException("Revit не создал типоразмер");
                    }
                    List<CalcWrite> writes = CalcPlanner.Diff(symbol, op.Values, false, out string? error);
                    if (error != null) throw new InvalidOperationException(error);
                    foreach (CalcWrite write in writes) Write(write);
                    if (!symbol.IsActive) symbol.Activate();
                    sub.Commit();
                    symbols[op.Code] = symbol;
                    if (created) result.TypesCreated++;
                    else if (writes.Count > 0) result.TypesUpdated++;
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is Autodesk.Revit.Exceptions.ApplicationException)
                {
                    if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                    result.Failed.Add(("типоразмер " + op.Code, op.SymbolId, ex.Message));
                }
            }
        }

        /// <summary>Одна позиция. InvalidOperationException — причина для отчёта (позиция откатывается).</summary>
        private static Element? Apply(Document doc, CalcPlan plan, CalcOp op, Dictionary<string, FamilySymbol> symbols, Dictionary<long, Level?> levels, CalcRunResult result)
        {
            if (op.Kind == CalcOpKind.DeleteOrphan || op.Kind == CalcOpKind.DeleteDuplicate)
            {
                Element? old = doc.GetElement(op.ItemId!.Value.ToElementId());
                if (old == null) return null;
                if (old.Pinned) old.Pinned = false;
                doc.Delete(old.Id);
                result.Deleted++;
                return null;
            }
            CalcTarget target = op.Target!;
            if (!symbols.TryGetValue(target.Code, out FamilySymbol? symbol)) throw new InvalidOperationException($"нет типоразмера «{target.Code}» (см. ошибки типоразмеров)");
            Element element;
            if (op.Kind == CalcOpKind.Create)
            {
                Level level = PickLevel(doc, target.Level, levels) ?? throw new InvalidOperationException("в проекте нет уровней");
                FamilyInstance instance = doc.Create.NewFamilyInstance(XYZ.Zero, symbol, level, StructuralType.NonStructural);
                if (instance.DesignOption != null) throw new InvalidOperationException("элемент создаётся в варианте конструкции — переключитесь на «Главная модель»");
                element = instance;
            }
            else
            {
                element = doc.GetElement(op.ItemId!.Value.ToElementId()) ?? throw new InvalidOperationException("позиции больше нет в модели");
                if (element.GetTypeId() != symbol.Id) element.ChangeTypeId(symbol.Id);
            }
            List<CalcWrite> writes = CalcPlanner.Diff(element, op.Values, true, out string? error);
            if (error != null) throw new InvalidOperationException(error);
            foreach (CalcWrite write in writes) Write(write);
            if (target.Phase > 0)
            {
                Parameter? phase = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
                if (phase != null && !phase.IsReadOnly && phase.AsElementId().ToLong() != target.Phase) phase.Set(target.Phase.ToElementId());
            }
            if (!element.Pinned) element.Pinned = true;
            if (op.Kind == CalcOpKind.Create) result.Created++;
            else result.Updated++;
            return element;
        }

        private static void Write(CalcWrite write)
        {
            if (write.Parameter == null) return;
            string? problem = write.IdValue != null
                ? (write.Parameter.Set(write.IdValue) ? null : $"«{write.Name}»: Revit отклонил запись")
                : CalcParams.Write(write.Parameter, write.Value);
            if (problem != null) throw new InvalidOperationException(problem);
        }

        private static Level? PickLevel(Document doc, long id, Dictionary<long, Level?> cache)
        {
            if (cache.TryGetValue(id, out Level? cached)) return cached;
            Level? level = id > 0 ? doc.GetElement(id.ToElementId()) as Level : null;
            level ??= new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).FirstOrDefault();
            cache[id] = level;
            return level;
        }

        /// <summary>
        /// После фиксации: значения те, что записали (иначе их переписал апдейтер), и каждая позиция видна в своей спецификации
        /// и не видна в чужих (проба эталона ClampReserve v1.1 — здесь на настоящих позициях пробного прогона).
        /// </summary>
        private static void Verify(Document doc, CalcPlan plan, List<(CalcOp Op, Element Element)> touched, CalcRunResult result)
        {
            foreach ((CalcOp op, Element element) in touched)
            {
                if (!element.IsValidObject) continue;
                List<CalcWrite> left = CalcPlanner.Diff(element, op.Values, true, out _);
                if (left.Count > 0)
                {
                    result.Overwritten.Add((op.Key, element.Id.ToLong(), string.Join("; ", left.Select(w => $"{w.Name}: «{w.Old}», а записано «{w.New}»"))));
                }
            }
            var visible = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
            foreach (CalcScope scope in plan.Scopes.Where(s => s.Schedule != null))
            {
                visible[scope.Name] = new HashSet<long>(new FilteredElementCollector(doc, scope.Schedule!.Id).WhereElementIsNotElementType().ToElementIds().Select(id => id.ToLong()));
            }
            foreach (IGrouping<string, (CalcOp Op, Element Element)> group in touched.Where(t => t.Element.IsValidObject).GroupBy(t => t.Op.Target!.Scope))
            {
                if (!visible.TryGetValue(group.Key, out HashSet<long>? own)) continue;
                List<(CalcOp Op, Element Element)> missing = group.Where(t => !own.Contains(t.Element.Id.ToLong())).ToList();
                if (missing.Count > 0)
                {
                    CalcScope scope = plan.Scopes.First(s => s.Name == group.Key);
                    result.Errors.Add($"«{group.Key}»: позиции не попадают в спецификацию ({missing.Count} из {group.Count()}, например «{missing[0].Op.Key}»). "
                        + "Проверьте фильтры спецификации" + (scope.CopyNames.Count > 0 ? " (копируются с труб: " + string.Join(", ", scope.CopyNames) + ")" : string.Empty)
                        + " и что в ней есть категория «Обобщённые модели». «Применить» запрещена.");
                }
                foreach (KeyValuePair<string, HashSet<long>> other in visible.Where(v => v.Key != group.Key))
                {
                    int foreign = group.Count(t => other.Value.Contains(t.Element.Id.ToLong()));
                    if (foreign > 0)
                    {
                        result.Errors.Add($"позиции области «{group.Key}» видны и в спецификации «{other.Key}» ({foreign} шт.): материалы посчитаются дважды. "
                            + "Разведите фильтры спецификаций. «Применить» запрещена.");
                    }
                }
            }
        }

        private static void SaveSettings(Document doc, Func<Document, string?> saveSettings, CalcRunResult result)
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
                    result.Warnings.Add("Настройка в модели не сохранена" + (problem != null ? ": " + problem : ".") + " Позиции записаны.");
                }
            }
        }
    }
}
