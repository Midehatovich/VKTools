using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using VKTools.Core.Calc;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Logging;
using VKTools.Core.Progress;
using VKTools.Core.Spec;
using VKTools.Core.Text;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Calc
{
    /// <summary>
    /// Анализ «Расчётных позиций» (порт spec_calc_items v0.4.1, разделы 4–7): области-спецификации, трубы, расчёт (Core),
    /// служебное семейство и позиции, что создать, обновить и удалить. Модель не меняется.
    /// </summary>
    internal static class CalcPlanner
    {
        private const double FeetToMm = 304.8;
        private const double FeetToM = 0.3048;
        private const double VerticalDegrees = 10.0;
        private const double OuterDiameterToleranceMm = 0.1;

        private static readonly char[] SystemSeparators = { ',', ';', '|' };

        /// <summary>Префикс ключей позиций запаса (0.9.0): расчёт их не трогает.</summary>
        public const string ReserveKeyPrefix = "ЗАПАС|";

        public static CalcPlan Build(Document doc, CalcOptions options, CalcDefaults defaults, IOperationProgress progress, OperationTimings timings)
        {
            CalcRuleSet rules = options.Parse();
            var plan = new CalcPlan(options, rules);
            if (options.SavedInProject) plan.RuleSources.Add("настройка проекта в модели: " + (options.SavedStamp ?? "сохранена"));
            plan.RuleSources.AddRange(defaults.Sources);
            plan.WriteBlockedReason = defaults.ProjectBlocked ?? (!options.SavedInProject ? defaults.Blocked : null);
            foreach (ConfigIssue issue in defaults.Issues.Where(i => i.Severity == Severity.Error)) plan.Issues.Add(Severity.Error, "Правила: " + issue);
            foreach (ConfigIssue issue in rules.Issues)
            {
                string where = issue.Line.HasValue ? $"{issue.FileName}, строка {issue.Line.Value - 2}" : issue.FileName;
                plan.Issues.Add(issue.Severity, $"{where}: {issue.Message}", $"Исправьте на вкладке «{issue.FileName}».");
            }
            if (rules.HasErrors) plan.WriteBlockedReason ??= "ошибки в таблицах расчёта (см. вкладки)";

            using (timings.Measure("спецификации"))
            {
                progress.Report("Спецификации проекта…");
                CollectSchedules(doc, plan);
            }
            if (rules.HasErrors) return plan;

            var access = new PipeReader(doc, options.SystemParameter.Trim(), plan);
            Dictionary<long, (double Mm, int Count)> sleeves = new Dictionary<long, (double, int)>();
            if (rules.NeedsSleeves)
            {
                using (timings.Measure("гильзы"))
                {
                    progress.Report("Гильзы…");
                    sleeves = CalcSleeves.Read(doc, options, plan);
                }
            }
            using (timings.Measure("трубы"))
            {
                BuildScopes(doc, plan, rules);
                int n = 0;
                foreach (CalcScope scope in plan.Scopes)
                {
                    progress.Report($"Трубы: «{scope.Name}»…", ++n, plan.Scopes.Count);
                    progress.ThrowIfCancelled();
                    ReadScope(doc, scope, access, plan, sleeves);
                }
                plan.SystemCandidates.UnionWith(access.TextParameters);
            }
            if (plan.Scopes.Count == 0) return plan;

            using (timings.Measure("расчёт"))
            {
                plan.Result = CalcEngine.Calculate(plan.Scopes.Select(s => new KeyValuePair<string, IReadOnlyList<PipeSample>>(s.Name, s.Pipes)),
                    rules.Resources, rules.Rules, rules.Supports, new CalcEngineOptions { QuantityParameter = CalcParams.Quantity, Cord = rules.Cord, CordAddMm = rules.CordAddMm, CordPitchMm = rules.CordPitchMm });
                foreach (CalcIssue issue in plan.Result.Issues.Items)
                {
                    CalcIssue copy = plan.Issues.Add(issue.Severity, issue.Message, issue.Action);
                    copy.Count += issue.Count - 1;
                    copy.Ids.AddRange(issue.Ids);
                }
                List<long> overlap = access.InScopes.Where(p => p.Value > 1).Select(p => p.Key).ToList();
                if (overlap.Count > 0)
                {
                    CalcIssue issue = plan.Issues.Add(Severity.Warning, $"трубы входят сразу в несколько отмеченных спецификаций — их материалы посчитаны в каждой ({overlap.Count} шт.)",
                        "Так и нужно, если спецификации не пересекаются по позициям; иначе разведите фильтры.");
                    issue.Ids.AddRange(overlap.Take(5000));
                }
            }

            if (rules.Reserve.Count > 0)
            {
                using (timings.Measure("запас на штучные"))
                {
                    progress.Report("Запас на штучные: строки спецификаций…");
                    CalcReserve.Add(doc, plan, rules);
                }
            }

            using (timings.Measure("позиции"))
            {
                progress.Report("Служебные позиции…");
                PlanItems(doc, plan);
            }
            plan.Notes.Add($"Труб в областях: {access.Read}, пропущено: {access.Skipped}, без правила: {plan.Result.PipesWithoutRule}. "
                + $"Позиций по расчёту: {plan.Result.Targets.Count}.");
            if (plan.SleeveCount >= 0)
            {
                int inScopes = plan.Result.Systems.Sum(s => s.Sleeves);
                int onPipes = sleeves.Values.Sum(v => v.Count);
                plan.Notes.Add($"Гильз «{options.SleeveFamily.Trim()}»: {plan.SleeveCount}, длина — «{plan.SleeveLengthUsed ?? "не задана"}»; на трубах областей: {inScopes}"
                    + (onPipes > inScopes ? $", на трубах вне отмеченных спецификаций (или пропущенных): {onPipes - inScopes}" : string.Empty) + ".");
                if (plan.SleeveWalls.HasValue)
                {
                    plan.Notes.Add($"Шнур только в наружных стенах и фундаментах: гильз в них {plan.SleeveWalls.Value.Outer}, во внутренних (без шнура) {plan.SleeveWalls.Value.Inner}. "
                        + "Стены и фундаменты: " + string.Join("; ", plan.SleeveWallSources) + ".");
                }
            }
            foreach (CalcSystemSummary s in plan.Result.Systems)
            {
                plan.Notes.Add($"«{s.Scope}», {s.System}: труб {s.Pipes}, L = {CalcTables.Format(s.Length)} м, S = {CalcTables.Format(s.Area)} м² (по правилам {CalcTables.Format(s.AreaByRules)} м²)"
                    + (s.SupportLines.Count > 0 ? "; крепления: " + string.Join("; ", s.SupportLines) : string.Empty)
                    + (s.CordLines.Count > 0 ? $"; гильз {s.Sleeves}, шнур: " + string.Join("; ", s.CordLines) : string.Empty));
            }
            return plan;
        }

        /// <summary>Спецификации модели (для выбора) и области расчёта из настройки.</summary>
        private static void CollectSchedules(Document doc, CalcPlan plan)
        {
            foreach (ViewSchedule vs in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
            {
                if (vs.IsTemplate || vs.IsTitleblockRevisionSchedule || vs.IsInternalKeynoteSchedule) continue;
                ScheduleDefinition definition;
                try
                {
                    definition = vs.Definition;
                    if (definition.IsKeySchedule) continue;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    continue;
                }
                bool multi = definition.CategoryId == ElementId.InvalidElementId;
                long category = definition.CategoryId.ToLong();
                if (!multi && category != (long)BuiltInCategory.OST_PipeCurves && category != (long)BuiltInCategory.OST_GenericModel) continue;
                int pipes = 0;
                try
                {
                    pipes = new FilteredElementCollector(doc, vs.Id).OfCategory(BuiltInCategory.OST_PipeCurves).WhereElementIsNotElementType().GetElementCount();
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Спецификация не отдаёт элементы — покажем 0
                }
                string name = multi ? "несколько категорий" : Category.GetCategory(doc, definition.CategoryId)?.Name ?? string.Empty;
                plan.Schedules.Add(new CalcScheduleInfo(vs.Name, vs.Id.ToLong(), pipes, name));
            }
            plan.Schedules.Sort((a, b) => b.Pipes > 0 == a.Pipes > 0 ? StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name) : b.Pipes > 0 ? 1 : -1);
        }

        private static void BuildScopes(Document doc, CalcPlan plan, CalcRuleSet rules)
        {
            if (plan.Options.Schedules.Count == 0)
            {
                plan.Scopes.Add(new CalcScope(CalcOptions.ModelScope, null));
                plan.Issues.Add(Severity.Warning, "спецификации не отмечены — расчёт по всей модели (область «МОДЕЛЬ»): фильтры спецификаций (подземная/надземная часть, этаж) не учитываются",
                    "Отметьте спецификации на вкладке «Спецификации», чтобы позиции считались по их фильтрам (этаж, система, часть здания).");
                return;
            }
            var byName = new Dictionary<string, ViewSchedule>(StringComparer.Ordinal);
            foreach (ViewSchedule vs in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Where(v => !v.IsTemplate))
            {
                if (!byName.ContainsKey(vs.Name)) byName[vs.Name] = vs;
            }
            HashSet<string> managed = ManagedNames(plan.Options, rules);
            foreach (string name in plan.Options.Schedules)
            {
                if (!byName.TryGetValue(name, out ViewSchedule? vs))
                {
                    plan.Issues.Add(Severity.Error, $"спецификации «{name}» нет в проекте (переименована или удалена?)", "Отметьте её заново на вкладке «Спецификации».");
                    plan.WriteBlockedReason ??= "отмеченной спецификации нет в проекте";
                    continue;
                }
                var scope = new CalcScope(name, vs);
                ScheduleDefinition definition = vs.Definition;
                if (definition.CategoryId != ElementId.InvalidElementId && definition.CategoryId.ToLong() != (long)BuiltInCategory.OST_GenericModel)
                {
                    plan.Issues.Add(Severity.Error, $"«{name}» — спецификация одной категории («{Category.GetCategory(doc, definition.CategoryId)?.Name}»): расчётные позиции (обобщённые модели) в неё не попадут",
                        "Нужна спецификация нескольких категорий, где есть «Обобщённые модели».");
                }
                for (int i = 0; i < definition.GetFilterCount(); i++)
                {
                    ScheduleFilter filter = definition.GetFilter(i);
                    ScheduleField field = definition.GetField(filter.FieldId);
                    string fieldName = field.GetName();
                    if (managed.Contains(fieldName) || scope.CopyNames.Contains(fieldName)) continue;
                    if (field.ParameterId.ToLong() < 0)
                    {
                        plan.Issues.Add(Severity.Warning, $"«{name}»: фильтр по системному полю «{fieldName}» — позиции не получат его значение",
                            "Пробный прогон покажет, попадают ли позиции в спецификацию. Если нет — фильтруйте по параметру проекта (ADSK_Этаж и т. п.).");
                        continue;
                    }
                    scope.CopyNames.Add(fieldName);
                }
                plan.Scopes.Add(scope);
            }
        }

        public static HashSet<string> ManagedNames(CalcOptions options, CalcRuleSet rules)
        {
            var names = new HashSet<string>(StringComparer.Ordinal)
            {
                options.SystemParameter.Trim(), CalcParams.Key, CalcParams.Quantity, CalcParams.Name, CalcParams.Unit, CalcParams.Group,
                CalcParams.Calc, CalcParams.Mode, CalcParams.Basis, CalcParams.Manual,
            };
            names.UnionWith(rules.ExtraParameters);
            return names;
        }

        private static void ReadScope(Document doc, CalcScope scope, PipeReader reader, CalcPlan plan, Dictionary<long, (double Mm, int Count)> sleeves)
        {
            FilteredElementCollector collector = scope.Schedule != null ? new FilteredElementCollector(doc, scope.Schedule.Id) : new FilteredElementCollector(doc);
            foreach (Pipe pipe in collector.OfCategory(BuiltInCategory.OST_PipeCurves).OfClass(typeof(Pipe)).WhereElementIsNotElementType().Cast<Pipe>())
            {
                PipeSample? read = reader.Get(pipe);
                if (read == null) continue;
                var sample = new PipeSample
                {
                    Id = read.Id, System = read.System, Name = read.Name, Segment = read.Segment, DnMm = read.DnMm, OdMm = read.OdMm,
                    LengthM = read.LengthM, Insulated = read.Insulated, Vertical = read.Vertical, Phase = read.Phase, Level = read.Level, Quantity = read.Quantity,
                };
                if (sleeves.TryGetValue(read.Id, out (double Mm, int Count) onPipe))
                {
                    sample.SleeveMm = onPipe.Mm;
                    sample.Sleeves = onPipe.Count;
                }
                foreach (string name in scope.CopyNames)
                {
                    Parameter? p = CalcParams.Find(pipe, name, out _);
                    ParamValue? value = p == null ? null : CalcParams.Raw(p);
                    if (value == null)
                    {
                        plan.Issues.Add(Severity.Warning, $"«{scope.Name}»: у трубы нет значения поля фильтра «{name}» — нечего скопировать в позицию",
                            "Заполните параметр у труб или уберите фильтр из спецификации.", pipe.Id.ToLong());
                        continue;
                    }
                    string key = CalcParams.KeyOf(value);
                    sample.Copy[name] = key;
                    plan.CopyValues[name + "\u0001" + key] = value;
                }
                scope.Pipes.Add(sample);
            }
        }

        /// <summary>Семейство, типоразмеры, существующие позиции → что создать, обновить, удалить.</summary>
        private static void PlanItems(Document doc, CalcPlan plan)
        {
            var bindings = CalcParams.ProjectBindings(doc);
            Family? family = new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>().FirstOrDefault(f => f.Name == plan.FamilyName);
            var symbols = new List<FamilySymbol>();
            BuiltInCategory category = BuiltInCategory.OST_GenericModel;
            if (family == null)
            {
                plan.CreateFamily = true;
                plan.Issues.Add(Severity.Info, $"служебного семейства «{plan.FamilyName}» в проекте нет — «Применить» создаст его (обобщённая модель, параметры SYS_)",
                    "Если у вас есть своё _SYS_SpecCalcItem.rfa, загрузите его до записи: тогда плагин возьмёт его.");
            }
            else
            {
                plan.FamilyId = family.Id.ToLong();
                category = family.FamilyCategory?.ToBuiltInCategory() ?? BuiltInCategory.OST_GenericModel;
                symbols = family.GetFamilySymbolIds().Select(id => doc.GetElement(id)).OfType<FamilySymbol>().OrderBy(s => s.Id.ToLong()).ToList();
                if (symbols.Count == 0)
                {
                    plan.Issues.Add(Severity.Error, $"у семейства «{plan.FamilyName}» нет типоразмеров", "Перезагрузите семейство или удалите его: плагин создаст своё.");
                    plan.WriteBlockedReason ??= "у служебного семейства нет типоразмеров";
                    return;
                }
                plan.BaseSymbolId = symbols[0].Id.ToLong();
            }

            CalcOptions options = plan.Options;
            string system = options.SystemParameter.Trim();
            var anyNames = new List<string> { CalcParams.Name, CalcParams.Unit, CalcParams.Group };
            anyNames.AddRange(plan.Rules.ExtraParameters.Where(n => !anyNames.Contains(n)));
            anyNames.AddRange(plan.Result.Targets.SelectMany(t => t.Resource.Extras.Select(e => e.Key)).Distinct().Where(n => !anyNames.Contains(n)).ToList());
            FamilySymbol? baseSymbol = symbols.FirstOrDefault();
            foreach (string name in anyNames)
            {
                bool typeBinding = bindings.TryGetValue(name, out var list) && list.Count == 1 && list[0].Binding is TypeBinding;
                if (CalcParams.Has(baseSymbol, name) || typeBinding) plan.TypeLevel.Add(name);
            }

            // Привязки параметров проекта к категории позиций
            var needed = new List<(string Name, bool Required)> { (system, true), (CalcParams.Quantity, true) };
            needed.AddRange(anyNames.Select(n => (n, n == CalcParams.Name || n == CalcParams.Unit || n == CalcParams.Group)));
            needed.AddRange(options.ExtraSystemParameters.Select(n => (n, false)));
            needed.AddRange(plan.Scopes.SelectMany(s => s.CopyNames).Concat(plan.ReserveFields).Distinct().Select(n => (n, true)));
            Element? sampleItem = null;
            foreach ((string name, bool required) in needed.Distinct())
            {
                if (bindings.TryGetValue(name, out var list))
                {
                    if (list.Count > 1)
                    {
                        plan.Issues.Add(Severity.Warning, $"в проекте {list.Count} параметра «{name}» — привязку к категории позиций плагин не трогает", "Удалите лишний параметр проекта.");
                        continue;
                    }
                    (Definition definition, ElementBinding binding) = list[0];
                    if (!CalcParams.HasCategory(binding, category))
                    {
                        plan.Bindings.Add(new CalcBindingFix(name, definition, binding is InstanceBinding, binding.Categories.Size));
                    }
                    continue;
                }
                bool inFamily = plan.CreateFamily ? CalcFamilyBuilder.OwnParameters.Contains(name) : CalcParams.Has(baseSymbol, name) || FamilyHasInstanceParameter(doc, symbols, name, ref sampleItem);
                if (!inFamily && (plan.CreateFamily || sampleItem != null))
                {
                    plan.Issues.Add(required ? Severity.Error : Severity.Warning, $"параметра «{name}» нет ни в служебном семействе, ни в параметрах проекта — "
                        + (required ? "позиции не создать" : "не заполняется"),
                        "Добавьте параметр проекта (Управление → Параметры проекта) с категорией «Обобщённые модели»" + (required ? "." : " или уберите колонку ресурсов."));
                    if (required) plan.WriteBlockedReason ??= $"нет параметра «{name}» для позиций";
                }
            }
            foreach (CalcBindingFix fix in plan.Bindings)
            {
                plan.Issues.Add(Severity.Info, $"параметр проекта «{fix.Parameter}» не привязан к «{LabelUtils.GetLabelFor(category)}» — «Применить» добавит категорию (остальные {fix.Categories} не меняются)",
                    "Ничего делать не нужно. Отменяется вместе с записью (Ctrl+Z).");
            }

            // Типоразмеры ресурсов
            var symbolByName = symbols.GroupBy(s => s.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var guard = new Spec.EditGuard(doc);
            foreach (CalcResource resource in plan.Result.Targets.Select(t => t.Resource).Distinct().OrderBy(r => r.Order))
            {
                var op = new CalcTypeOp(resource);
                foreach (string name in plan.TypeLevel) op.Values.Add(new CalcItemValue(name, TypeValue(resource, name), name == CalcParams.Name || name == CalcParams.Unit || name == CalcParams.Group));
                if (symbolByName.TryGetValue(resource.Code, out FamilySymbol? symbol))
                {
                    op.SymbolId = symbol.Id.ToLong();
                    op.Changes.AddRange(Diff(symbol, op.Values, false, out string? error));
                    op.Error = error ?? (op.Changes.Count > 0 && op.Changes[0].Parameter != null ? Reason(guard, symbol, op.Changes[0].Parameter!) : null);
                }
                plan.TypeOps.Add(op);
                if (op.Error != null) plan.Issues.Add(Severity.Error, $"типоразмер «{resource.Code}»: {op.Error}", "Типоразмер не будет обновлён.");
            }

            // Позиции
            var byKey = new Dictionary<string, List<Item>>(StringComparer.Ordinal);
            foreach (FamilySymbol symbol in symbols)
            {
                foreach (FamilyInstance inst in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WherePasses(new FamilyInstanceFilter(doc, symbol.Id)).Cast<FamilyInstance>())
                {
                    string key = (CalcParams.Find(inst, CalcParams.Key, out _)?.AsString() ?? string.Empty).Trim();
                    if (key.Length == 0)
                    {
                        plan.Issues.Add(Severity.Info, $"экземпляр «{plan.FamilyName}» без {CalcParams.Key} — плагин его не трогает (ручная позиция?)", string.Empty, inst.Id.ToLong());
                        continue;
                    }
                    if (key.StartsWith(ReserveKeyPrefix, StringComparison.Ordinal)) continue;
                    var item = new Item(inst, key, CalcParams.Find(inst, CalcParams.Manual, out _) is Parameter m ? CalcParams.Display(m).Trim() : string.Empty,
                        CalcParams.Find(inst, CalcParams.Quantity, out _) is Parameter q ? CalcParams.Display(q) : string.Empty);
                    if (!byKey.TryGetValue(key, out List<Item>? list))
                    {
                        list = new List<Item>();
                        byKey[key] = list;
                    }
                    list.Add(item);
                }
            }

            var targets = plan.Result.Targets.ToDictionary(t => t.Key, StringComparer.Ordinal);
            foreach (CalcTarget target in plan.Result.Targets.OrderBy(t => t.Key, StringComparer.Ordinal))
            {
                if (!byKey.TryGetValue(target.Key, out List<Item>? found) || found.Count == 0)
                {
                    var create = new CalcOp(CalcOpKind.Create, target.Key) { Target = target };
                    FillValues(create, target, string.Empty, plan);
                    plan.Ops.Add(create);
                    continue;
                }
                List<Item> ordered = found.OrderBy(i => i.Manual.Length == 0).ThenBy(i => i.Element.Id.ToLong()).ToList();
                Item keep = ordered[0];
                var manualValues = ordered.Where(i => i.Manual.Length > 0).Select(i => i.Manual).Distinct().ToList();
                if (manualValues.Count > 1)
                {
                    plan.Issues.Add(Severity.Warning, $"дубли «{target.Key}» с разными ручными значениями ({string.Join(", ", manualValues)}) — оставлен Id {keep.Element.Id.ToLong()}",
                        "Проверьте ручное значение у оставленной позиции.", keep.Element.Id.ToLong());
                }
                var update = new CalcOp(CalcOpKind.Update, target.Key) { Target = target, ItemId = keep.Element.Id.ToLong(), ManualText = keep.Manual, OldQuantity = keep.Quantity };
                FillValues(update, target, keep.Manual, plan);
                update.Changes.AddRange(Diff(keep.Element, update.Values, true, out string? error));
                if (error == null && targets.ContainsKey(target.Key) && plan.TypeOps.FirstOrDefault(t => t.Code == target.Code) is CalcTypeOp typeOp
                    && typeOp.SymbolId.HasValue && keep.Element.GetTypeId().ToLong() != typeOp.SymbolId.Value)
                {
                    update.Changes.Add(new CalcWrite(null, "Типоразмер", doc.GetElement(keep.Element.GetTypeId())?.Name ?? string.Empty, ParamValue.OfText(target.Code))
                    {
                        IdValue = typeOp.SymbolId.Value.ToElementId(),
                    });
                }
                PhaseChange(doc, keep.Element, target, update);
                update.Error = error ?? (update.Changes.Count > 0 ? GuardReason(guard, keep.Element, update.Changes) : null);
                plan.Ops.Add(update);
                foreach (Item duplicate in ordered.Skip(1))
                {
                    plan.Ops.Add(new CalcOp(CalcOpKind.DeleteDuplicate, target.Key)
                    {
                        Target = target, ItemId = duplicate.Element.Id.ToLong(), ManualText = duplicate.Manual, OldQuantity = duplicate.Quantity,
                        Error = Reason(guard, duplicate.Element, null),
                    });
                }
            }

            var scopeNames = new HashSet<string>(plan.Scopes.Select(s => s.Name), StringComparer.Ordinal);
            int modelReplacedNoted = 0;
            var scheduleNames = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>().Select(v => v.Name), StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<Item>> pair in byKey.Where(p => !targets.ContainsKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string? scope = CalcEngine.KeyScope(pair.Key);
                // Расчёт по всей модели и по спецификациям взаимоисключающие: позиции «МОДЕЛЬ» при отмеченных спецификациях задвоили бы итог
                bool replacedModel = scope == CalcOptions.ModelScope && !scopeNames.Contains(scope);
                if (replacedModel && modelReplacedNoted++ == 0)
                {
                    plan.Issues.Add(Severity.Warning, $"позиции области «{CalcOptions.ModelScope}» (прошлый расчёт без отмеченных спецификаций) будут удалены: теперь позиции считаются по отмеченным спецификациям",
                        "Ничего делать не нужно: иначе материалы попали бы в спецификации дважды.", pair.Value[0].Element.Id.ToLong());
                }
                if (scope != null && (scopeNames.Contains(scope) || replacedModel))
                {
                    foreach (Item item in pair.Value)
                    {
                        plan.Ops.Add(new CalcOp(CalcOpKind.DeleteOrphan, pair.Key)
                        {
                            ItemId = item.Element.Id.ToLong(), ManualText = item.Manual, OldQuantity = item.Quantity, Error = Reason(guard, item.Element, null),
                        });
                        if (item.Manual.Length > 0)
                        {
                            plan.Issues.Add(Severity.Warning, $"«{pair.Key}»: будет удалена позиция с ручным значением {item.Manual} — система или ресурс больше не рассчитываются",
                                "Если позиция нужна, заведите её вручную без SYS_Ключ.", item.Element.Id.ToLong());
                        }
                    }
                }
                else if (scope == null)
                {
                    plan.Issues.Add(Severity.Warning, $"ключ «{pair.Key}» не формата «область|система|код» — позиция не тронута", string.Empty, pair.Value[0].Element.Id.ToLong());
                }
                else if (scope != CalcOptions.ModelScope && !scheduleNames.Contains(scope))
                {
                    plan.Issues.Add(Severity.Warning, $"позиции области «{scope}»: такой спецификации в проекте нет (переименована или удалена?) — не тронуты",
                        "Удалите их вручную или отметьте спецификацию с новым именем и удалите старые позиции.", pair.Value[0].Element.Id.ToLong());
                }
                else
                {
                    plan.Issues.Add(Severity.Info, $"позиции области «{scope}» не тронуты: эта область сейчас не отмечена", string.Empty, pair.Value[0].Element.Id.ToLong());
                }
            }

            CheckCrossScope(doc, plan, family);
            foreach (CalcOp op in plan.Ops.Where(o => o.Error != null))
            {
                plan.Issues.Add(Severity.Error, $"«{op.Key}»: {op.Error}", "Позиция не будет изменена.", op.ItemId);
            }
            foreach (CalcOp op in plan.Ops.Where(o => o.Note.Length > 0))
            {
                plan.Issues.Add(Severity.Warning, $"«{op.Key}»: {op.Note}", "Проверьте ручное значение SYS_Кол-во вручную.", op.ItemId);
            }
        }

        /// <summary>Значения экземпляра (inst_values эталона): ключ, система, количество, расчёт, способ, основание, поля фильтров; «любые» — если не в типе.</summary>
        private static void FillValues(CalcOp op, CalcTarget target, string manual, CalcPlan plan)
        {
            (double final, CalcMode mode, string note) = CalcManual.Resolve(target.Auto, manual);
            op.Final = final;
            op.Mode = mode;
            op.Note = note;
            string basis = target.Basis + (mode == CalcMode.Manual ? " | ВРУЧНУЮ: " + CalcTables.Format(final) : string.Empty) + (note.Length > 0 ? " | " + note : string.Empty);
            op.Values.Add(new CalcItemValue(CalcParams.Key, target.Key, true));
            op.Values.Add(new CalcItemValue(plan.Options.SystemParameter.Trim(), target.System, true));
            foreach (string extra in plan.Options.ExtraSystemParameters) op.Values.Add(new CalcItemValue(extra, target.System, false));
            op.Values.Add(new CalcItemValue(CalcParams.Quantity, final, true));
            op.Values.Add(new CalcItemValue(CalcParams.Calc, Math.Round(target.Quantity, 4), false));
            op.Values.Add(new CalcItemValue(CalcParams.Mode, CalcManual.ModeText(mode), false));
            op.Values.Add(new CalcItemValue(CalcParams.Basis, basis, false));
            foreach (string name in new[] { CalcParams.Name, CalcParams.Unit, CalcParams.Group }.Concat(target.Resource.Extras.Select(e => e.Key)).Distinct())
            {
                if (plan.TypeLevel.Contains(name)) continue;
                op.Values.Add(new CalcItemValue(name, TypeValue(target.Resource, name), name == CalcParams.Name || name == CalcParams.Unit || name == CalcParams.Group));
            }
            foreach (KeyValuePair<string, string> copy in target.Copy.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                if (plan.CopyValues.TryGetValue(copy.Key + "\u0001" + copy.Value, out ParamValue? value)) op.Values.Add(new CalcItemValue(copy.Key, value, true));
            }
        }

        /// <summary>Значение «любого» параметра ресурса: наименование, единица, группа; колонка таблицы перекрывает (например ADSK_Группирование).</summary>
        private static string TypeValue(CalcResource resource, string name)
        {
            foreach (KeyValuePair<string, string> extra in resource.Extras)
            {
                if (string.Equals(extra.Key, name, StringComparison.Ordinal)) return extra.Value;
            }
            if (name == CalcParams.Name) return resource.Name;
            if (name == CalcParams.Unit) return resource.Unit;
            if (name == CalcParams.Group) return resource.Group;
            return string.Empty;
        }

        /// <summary>
        /// Что изменится у элемента (diff_params эталона). Ошибка — нет обязательного параметра, тип данных не тот: элемент не трогаем.
        /// </summary>
        public static List<CalcWrite> Diff(Element element, IEnumerable<CalcItemValue> values, bool instance, out string? error)
        {
            error = null;
            var changes = new List<CalcWrite>();
            foreach (CalcItemValue v in values)
            {
                Parameter? p = CalcParams.Find(element, v.Parameter, out string? problem);
                if (p == null)
                {
                    if (instance && CalcParams.Has(element.Document.GetElement(element.GetTypeId()), v.Parameter) && !IsAny(v.Parameter))
                    {
                        problem = $"«{v.Parameter}» — параметр типа, а значение своё у каждой системы: нужен параметр экземпляра";
                    }
                    if (v.Required)
                    {
                        error = problem;
                        return changes;
                    }
                    continue;
                }
                ParamValue? value;
                if (v.Value is ParamValue copy)
                {
                    value = copy;
                    if ((copy.Kind == ParamValueKind.Text) != (p.StorageType == StorageType.String)
                        || (copy.Kind == ParamValueKind.Integer) != (p.StorageType == StorageType.Integer))
                    {
                        problem = $"«{v.Parameter}»: тип данных у позиции и у трубы различается";
                        value = null;
                    }
                }
                else if ((v.Parameter == CalcParams.Quantity || v.Parameter == CalcParams.Calc) && p.StorageType == StorageType.Integer)
                {
                    problem = $"«{v.Parameter}» — целое, а нужен тип «Число» (дробные кг)";
                    value = null;
                }
                else
                {
                    value = CalcParams.Convert(p, v.Value, out problem);
                }
                if (value == null)
                {
                    if (v.Required)
                    {
                        error = problem;
                        return changes;
                    }
                    continue;
                }
                if (CalcParams.Same(p, value)) continue;
                if (p.IsReadOnly)
                {
                    if (v.Required)
                    {
                        error = $"«{v.Parameter}» только для чтения (формула или ассоциация)";
                        return changes;
                    }
                    continue;
                }
                changes.Add(new CalcWrite(p, v.Parameter, CalcParams.Display(p), value));
            }
            return changes;
        }

        private static bool IsAny(string name) => name == CalcParams.Name || name == CalcParams.Unit || name == CalcParams.Group;

        private static void PhaseChange(Document doc, Element element, CalcTarget target, CalcOp op)
        {
            if (target.Phase <= 0) return;
            Parameter? p = element.get_Parameter(BuiltInParameter.PHASE_CREATED);
            if (p == null || p.IsReadOnly || p.AsElementId().ToLong() == target.Phase) return;
            string newName = doc.GetElement(target.Phase.ToElementId())?.Name ?? target.Phase.ToString(System.Globalization.CultureInfo.InvariantCulture);
            op.Changes.Add(new CalcWrite(p, "Стадия возведения", doc.GetElement(p.AsElementId())?.Name ?? string.Empty, ParamValue.OfText(newName))
            {
                IdValue = target.Phase.ToElementId(),
            });
        }

        private static string? GuardReason(Spec.EditGuard guard, Element element, List<CalcWrite> changes)
        {
            foreach (CalcWrite change in changes)
            {
                if (change.Parameter == null) continue;
                string? reason = Reason(guard, element, change.Parameter);
                if (reason != null) return reason;
            }
            return Reason(guard, element, null);
        }

        /// <summary>Группа модели и совместная работа (EditGuard). Без параметра — только совместная работа (удаление, смена типа).</summary>
        private static string? Reason(Spec.EditGuard guard, Element element, Parameter? parameter)
        {
            if (parameter != null) return guard.Check(element, parameter)?.Reason;
            if (element.GroupId != null && element.GroupId != ElementId.InvalidElementId) return "позиция в группе модели — плагин её не трогает";
            return guard.Check(element, element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) ?? element.Parameters.Cast<Parameter>().First())?.Reason;
        }

        /// <summary>Позиции одной области, видимые в спецификации другой, — двойной учёт.</summary>
        private static void CheckCrossScope(Document doc, CalcPlan plan, Family? family)
        {
            if (family == null) return;
            foreach (CalcScope scope in plan.Scopes.Where(s => s.Schedule != null))
            {
                foreach (FamilyInstance inst in new FilteredElementCollector(doc, scope.Schedule!.Id).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
                {
                    if (inst.Symbol?.Family?.Id != family.Id) continue;
                    string key = (CalcParams.Find(inst, CalcParams.Key, out _)?.AsString() ?? string.Empty).Trim();
                    string? other = CalcEngine.KeyScope(key);
                    if (other != null && other != scope.Name && !key.StartsWith(ReserveKeyPrefix, StringComparison.Ordinal))
                    {
                        plan.Issues.Add(Severity.Warning, $"в спецификации «{scope.Name}» видны расчётные позиции области «{other}» — материалы посчитаны дважды",
                            "Спецификации пересекаются по фильтрам: разведите их или снимите отметку с одной.", inst.Id.ToLong());
                    }
                }
            }
        }

        private static bool FamilyHasInstanceParameter(Document doc, List<FamilySymbol> symbols, string name, ref Element? sample)
        {
            if (sample == null)
            {
                foreach (FamilySymbol symbol in symbols)
                {
                    sample = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WherePasses(new FamilyInstanceFilter(doc, symbol.Id)).FirstElement();
                    if (sample != null) break;
                }
            }
            return CalcParams.Has(sample, name);
        }

        private sealed class Item
        {
            public Item(FamilyInstance element, string key, string manual, string quantity)
            {
                Element = element;
                Key = key;
                Manual = manual;
                Quantity = quantity;
            }

            public FamilyInstance Element { get; }

            public string Key { get; }

            public string Manual { get; }

            public string Quantity { get; }
        }

        /// <summary>Чтение трубы один раз, даже если она входит в несколько областей (read_pipe эталона).</summary>
        private sealed class PipeReader
        {
            private readonly Document _doc;
            private readonly string _systemParameter;
            private readonly CalcPlan _plan;
            private readonly Dictionary<long, PipeSample?> _cache = new Dictionary<long, PipeSample?>();
            private readonly Dictionary<long, (string Name, Dictionary<long, double> Sizes)> _segments = new Dictionary<long, (string, Dictionary<long, double>)>();
            private readonly double _verticalCos = Math.Cos(VerticalDegrees * Math.PI / 180.0);

            public PipeReader(Document doc, string systemParameter, CalcPlan plan)
            {
                _doc = doc;
                _systemParameter = systemParameter;
                _plan = plan;
            }

            public Dictionary<long, int> InScopes { get; } = new Dictionary<long, int>();

            public SortedSet<string> TextParameters { get; } = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);

            public int Read { get; private set; }

            public int Skipped { get; private set; }

            public PipeSample? Get(Pipe pipe)
            {
                long id = pipe.Id.ToLong();
                InScopes.TryGetValue(id, out int n);
                InScopes[id] = n + 1;
                if (_cache.TryGetValue(id, out PipeSample? cached)) return cached;
                if (TextParameters.Count == 0) CollectTextParameters(pipe);
                Read++;
                PipeSample? sample;
                string? reason;
                try
                {
                    sample = ReadPipe(pipe, out reason);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    sample = null;
                    reason = "ошибка чтения: " + ex.Message;
                }
                if (sample == null)
                {
                    Skipped++;
                    _plan.Issues.Add(Severity.Warning, "труба пропущена: " + reason, "Исправьте трубу, если её материалы нужны в расчёте.", id);
                }
                _cache[id] = sample;
                return sample;
            }

            private void CollectTextParameters(Pipe pipe)
            {
                foreach (Parameter p in pipe.Parameters)
                {
                    if (p.StorageType != StorageType.String || p.Definition == null) continue;
                    if (p.IsShared || (p.Definition is InternalDefinition d && d.BuiltInParameter == BuiltInParameter.INVALID)) TextParameters.Add(p.Definition.Name);
                }
            }

            private PipeSample? ReadPipe(Pipe pipe, out string? reason)
            {
                reason = null;
                long id = pipe.Id.ToLong();
                if (pipe.DesignOption != null && !pipe.DesignOption.IsPrimary)
                {
                    reason = "во второстепенном варианте конструкции";
                    return null;
                }
                if (pipe.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED)?.AsElementId() is ElementId demolished && demolished.ToLong() > 0)
                {
                    reason = "снесена (задана стадия сноса)";
                    return null;
                }
                string? system = ResolveSystem(pipe, out reason);
                if (system == null) return null;

                double length = pipe.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0;
                Curve? curve = (pipe.Location as LocationCurve)?.Curve;
                if (length <= 0 && curve != null) length = curve.Length;
                if (length <= 0)
                {
                    reason = "нулевая длина";
                    return null;
                }
                bool vertical = false;
                if (curve != null && curve.Length > 0)
                {
                    XYZ a = curve.GetEndPoint(0);
                    XYZ b = curve.GetEndPoint(1);
                    vertical = Math.Abs(b.Z - a.Z) / curve.Length >= _verticalCos;
                }
                Parameter? dnParameter = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (dnParameter == null || !dnParameter.HasValue)
                {
                    reason = "нет номинального диаметра";
                    return null;
                }
                double dn = dnParameter.AsDouble();
                ElementId segmentId = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_SEGMENT_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
                (string segmentName, Dictionary<long, double> sizes) = Segment(segmentId);
                if (segmentName.Length == 0) segmentName = _doc.GetElement(pipe.GetTypeId())?.Name ?? string.Empty;
                double odParameter = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER)?.AsDouble() ?? 0;
                sizes.TryGetValue(SizeKey(dn), out double odTable);
                double od;
                if (odParameter > 0)
                {
                    od = odParameter;
                    if (odTable > 0 && Math.Abs(odParameter - odTable) * FeetToMm > OuterDiameterToleranceMm)
                    {
                        _plan.Issues.Add(Severity.Warning, $"сегмент «{segmentName}» DN{CalcTables.Format(dn * FeetToMm, 1)}: Dнар трубы {CalcTables.Format(odParameter * FeetToMm, 2)} мм ≠ таблица сегмента {CalcTables.Format(odTable * FeetToMm, 2)} мм (взят параметр трубы)",
                            "Проверьте таблицу сегмента.", id);
                    }
                }
                else if (odTable > 0)
                {
                    od = odTable;
                }
                else
                {
                    reason = "нет наружного диаметра (ни в трубе, ни в таблице сегмента)";
                    return null;
                }
                double inner = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_INNER_DIAM_PARAM)?.AsDouble() ?? 0;
                if (inner > 0 && inner >= od)
                {
                    _plan.Issues.Add(Severity.Warning, $"сегмент «{segmentName}» DN{CalcTables.Format(dn * FeetToMm, 1)}: Dнар ≤ Dвн — ошибка таблицы сегмента", "Проверьте таблицу сегмента.", id);
                }
                string name = CalcParams.Text(pipe, CalcParams.Name);
                Match m = Regex.Match(name, @"[øØ⌀]\s*(\d+(?:[.,]\d+)?)");
                if (m.Success)
                {
                    double size = CalcTables.ParseNumber(m.Groups[1].Value, 0) ?? 0;
                    if (Math.Abs(size - dn * FeetToMm) > 1.0 && Math.Abs(size - od * FeetToMm) > 1.0)
                    {
                        _plan.Issues.Add(Severity.Warning, $"«{name}»: в наименовании ø{CalcTables.Format(size, 1)}, а в модели DN{CalcTables.Format(dn * FeetToMm, 1)} / Dнар {CalcTables.Format(od * FeetToMm, 1)} мм — наименование устарело? Правило подбирается по наименованию",
                            "Обновите наименование трубы.", id);
                    }
                }
                var sample = new PipeSample
                {
                    Id = id,
                    System = system,
                    Name = name,
                    Segment = segmentName,
                    DnMm = dn * FeetToMm,
                    OdMm = od * FeetToMm,
                    LengthM = length * FeetToM,
                    Vertical = vertical,
                    Insulated = InsulationLiningBase.GetInsulationIds(_doc, pipe.Id).Count > 0,
                };
                ElementId created = pipe.get_Parameter(BuiltInParameter.PHASE_CREATED)?.AsElementId() ?? ElementId.InvalidElementId;
                if (created.ToLong() > 0) sample.Phase = created.ToLong();
                ElementId level = pipe.ReferenceLevel?.Id ?? pipe.LevelId;
                if (level.ToLong() <= 0) level = pipe.get_Parameter(BuiltInParameter.RBS_START_LEVEL_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
                if (level.ToLong() > 0) sample.Level = level.ToLong();
                if (CalcParams.Find(pipe, CalcParams.Quantity, out _) is Parameter qty && qty.StorageType == StorageType.Double && qty.HasValue) sample.Quantity = qty.AsDouble();
                return sample;
            }

            /// <summary>Значение параметра системы (resolve_system эталона); null — труба пропускается (причина в <paramref name="reason"/>).</summary>
            private string? ResolveSystem(Pipe pipe, out string? reason)
            {
                reason = null;
                long id = pipe.Id.ToLong();
                string abbreviation = pipe.get_Parameter(BuiltInParameter.RBS_DUCT_PIPE_SYSTEM_ABBREVIATION_PARAM)?.AsString() ?? string.Empty;
                string systemName = pipe.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM)?.AsString() ?? string.Empty;
                string revit = abbreviation.Length > 0 ? abbreviation : systemName.Length > 0 ? systemName : "—";
                IList<Parameter> found = pipe.GetParameters(_systemParameter);
                if (found.Count > 1)
                {
                    reason = $"у трубы {found.Count} параметра «{_systemParameter}» — неоднозначно";
                    return null;
                }
                if (found.Count == 0)
                {
                    reason = $"нет параметра «{_systemParameter}» (Revit: «{revit}»)";
                    return null;
                }
                string raw = found[0].AsString() ?? string.Empty;
                string value = raw.Replace(' ', ' ').Trim();
                if (value.Length == 0)
                {
                    reason = $"«{_systemParameter}» пуст (Revit: «{revit}»)";
                    return null;
                }
                if (raw != value)
                {
                    _plan.Issues.Add(Severity.Warning, $"в «{_systemParameter}» лишние пробелы: «{raw}» — в спецификации это отдельная строка", "Исправьте значение у трубы.", id);
                }
                if (value.IndexOfAny(SystemSeparators) >= 0)
                {
                    reason = $"несколько систем в одном значении: «{value}»";
                    return null;
                }
                if (ValueNormalizer.HasMixedScripts(value))
                {
                    _plan.Issues.Add(Severity.Warning, $"в имени системы «{value}» смешаны латиница и кириллица", "Исправьте значение у трубы.", id);
                }
                if ((abbreviation.Length > 0 || systemName.Length > 0) && value != abbreviation && value != systemName && !systemName.StartsWith(value + " ", StringComparison.Ordinal))
                {
                    _plan.Issues.Add(Severity.Info, $"«{_systemParameter}» = «{value}», а система Revit: «{abbreviation}» / «{systemName}» — параметр мог устареть", string.Empty, id);
                }
                return value;
            }

            private (string Name, Dictionary<long, double> Sizes) Segment(ElementId id)
            {
                long key = id.ToLong();
                if (_segments.TryGetValue(key, out var cached)) return cached;
                var sizes = new Dictionary<long, double>();
                string name = string.Empty;
                if (key > 0 && _doc.GetElement(id) is PipeSegment segment)
                {
                    name = segment.Name;
                    foreach (MEPSize size in segment.GetSizes()) sizes[SizeKey(size.NominalDiameter)] = size.OuterDiameter;
                }
                _segments[key] = (name, sizes);
                return (name, sizes);
            }

            private static long SizeKey(double feet) => (long)Math.Round(feet * 1e6);
        }
    }
}
