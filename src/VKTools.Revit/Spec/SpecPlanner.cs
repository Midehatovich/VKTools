using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Logging;
using VKTools.Core.Progress;
using VKTools.Core.Spec;
using VKTools.Core.Text;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Spec
{
    /// <summary>
    /// Анализ «Перенос во вложенные» и «Группирование»: что запишется и что нет. Модель не меняется.
    /// Поведение по умолчанию — как у Dynamo-эталонов: NestedParamSync v2.1 (шаг 3), adsk_grouping v2,
    /// fire_cabinet_postprocess v1.0. Настройки (какие параметры, какая таблица групп) — <see cref="SpecOptions"/>.
    /// </summary>
    internal sealed partial class SpecPlanner
    {
        /// <summary>Защита от зацикленной вложенности (как HARD_MAX_LEVEL в NestedParamSync).</summary>
        private const int MaxNestedLevel = 20;

        /// <summary>Шкаф ищется не глубже 10 уровней (fire_cabinet_postprocess).</summary>
        private const int MaxKitDepth = 10;

        /// <summary>Текстов одного семейства для поиска кандидатов в комплекты — не больше.</summary>
        private const int MaxKitTexts = 300;

        /// <summary>Экземпляров одного типоразмера, чьи параметры смотрит поиск кандидатов.</summary>
        private const int KitTextSamples = 3;

        /// <summary>Служебные семейства расчётных позиций и запаса (_SYS_SpecCalcItem, _SYS_ClampReserve): группу 9.x им ставит их модуль.</summary>
        private const string ServiceFamilyPrefix = "_SYS_";

        private static readonly Guid ServiceKeyGuid = new Guid("ba2a88aa-82d8-4f53-b4a8-493357b3b4e9");
        private const string ServiceKeyName = "SYS_Ключ";

        private readonly Document _doc;
        private readonly IOperationProgress _progress;
        private readonly EditGuard _guard;
        private readonly ParamAccess _access;
        private readonly Dictionary<long, (string Family, string Type)> _typeLabels = new Dictionary<long, (string, string)>();
        private ParamPlan _plan = new ParamPlan(Array.Empty<SpecModule>());
        private readonly HashSet<string> _cabinetFamilies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Group, int K)> _cabinetKeys = new List<(string Group, int K)>();

        private SpecPlanner(Document doc, IOperationProgress progress)
        {
            _doc = doc;
            _progress = progress;
            _guard = new EditGuard(doc);
            _access = new ParamAccess(doc);
        }

        /// <param name="scope">Для областей «Активный вид» и «Выделенные»: элементы области (из них берутся верхние родители).</param>
        public static ParamPlan Build(Document doc, SpecOptions options, SpecDefaults defaults, ICollection<ElementId>? scope,
            IOperationProgress progress, OperationTimings timings)
        {
            var planner = new SpecPlanner(doc, progress);
            if (options.Module == SpecModule.SystemFloor)
            {
                bool chain = options.SystemFloor.ChainNested;
                planner._plan = new ParamPlan(chain ? new[] { SpecModule.SystemFloor, SpecModule.Nested } : new[] { SpecModule.SystemFloor });
                foreach (ConfigIssue issue in defaults.Issues) planner.AddConfigIssue(options.Module, issue);
                using (timings.Measure("анализ системы и этажа")) planner.PlanSystemFloor(options, defaults);
                if (chain)
                {
                    // Родитель отдаёт вложенным уже НОВОЕ значение (как шаг 3 NestedParamSync v2.1)
                    var nested = new SpecOptions(SpecModule.Nested)
                    {
                        NestedParams = options.NestedParams.ToList(),
                        NestedClearEmpty = options.NestedClearEmpty,
                        OwnSystem = options.OwnSystem.ToList(),
                        Scope = NestedScope.Model,
                        SavedInProject = options.NestedSavedInProject,
                    };
                    int ownCandidates = planner._plan.Candidates.Count;
                    using (timings.Measure("анализ переноса")) planner.PlanNested(nested, defaults, null);
                    // Варианты параметров окна — только свои (текстовые параметры элементов), без кандидатов переноса
                    planner._plan.Candidates.RemoveRange(ownCandidates, planner._plan.Candidates.Count - ownCandidates);
                }
                return planner._plan;
            }
            planner._plan = new ParamPlan(new[] { options.Module });
            foreach (ConfigIssue issue in defaults.Issues) planner.AddConfigIssue(options.Module, issue);
            if (options.Module == SpecModule.Insulation)
            {
                using (timings.Measure("анализ изоляции")) planner.PlanInsulation(options, defaults);
                return planner._plan;
            }
            if (options.Module == SpecModule.PipeReserve)
            {
                using (timings.Measure("анализ запаса")) planner.PlanReserve(options, defaults);
                return planner._plan;
            }
            if (options.Module == SpecModule.Vendor)
            {
                using (timings.Measure("анализ марок и поставщиков")) planner.PlanVendor(options, defaults);
                return planner._plan;
            }
            if (options.Module == SpecModule.PipeNames)
            {
                using (timings.Measure("анализ имён труб")) planner.PlanPipeNames(options, defaults);
                return planner._plan;
            }
            if (options.Module == SpecModule.Nested)
            {
                using (timings.Measure("анализ переноса")) planner.PlanNested(options, defaults, scope);
            }
            else
            {
                using (timings.Measure("анализ группирования")) planner.PlanGrouping(options, defaults);
            }
            return planner._plan;
        }

        // ---------------------------------------------------------------- правила

        private void Source(SpecOptions options, SpecDefaults defaults, string title, string defaultSource, string? defaultBlocked)
        {
            if (options.IsDefault(defaults))
            {
                _plan.RuleSources.Add(defaultSource);
                if (defaultBlocked != null) Block(defaultBlocked);
            }
            else
            {
                string state = !options.SavedInProject ? "изменена в окне, в модели не сохранена" : options.SavedStamp ?? "сохранена в модели";
                _plan.RuleSources.Add($"{title}: настройка проекта ({state})");
            }
            // Повреждённая настройка проекта запрещает запись, пока её не сохранят заново
            if (defaults.ProjectBlocked != null) Block(defaults.ProjectBlocked);
        }

        private void Block(string reason) =>
            _plan.WriteBlockedReason = _plan.WriteBlockedReason == null ? reason : _plan.WriteBlockedReason + " " + reason;

        private CsvTable? LoadTable(ConfigLoader loader, SpecModule module, RuleFileSpec spec)
        {
            LoadedTable loaded = loader.Load(spec);
            _plan.RuleSources.Add(SpecSettingsLoader.Source(loaded));
            foreach (ConfigIssue issue in loaded.Issues) AddConfigIssue(module, issue);
            if (loaded.WriteBlocked) Block($"{spec.FileName}: {loaded.WriteBlockedReason}");
            return loaded.Table;
        }

        private void AddConfigIssue(SpecModule module, ConfigIssue issue)
        {
            _plan.AddIssue(module, issue.Severity, string.Empty, "Правила: " + issue, string.Empty).Action =
                "Исправьте файл правил (VK Tools → Настройки → «Проверить») или настройку проекта и сохраните её заново.";
            Host.Log.Warn("Правила: " + issue);
        }

        // ---------------------------------------------------------------- перенос во вложенные

        /// <summary>Параметр элемента: где взят (экземпляр или тип) и как узнан.</summary>
        private sealed class ParamSlot
        {
            public ParamSlot(ParamKey key, Parameter parameter, bool fromType)
            {
                Key = key;
                Parameter = parameter;
                FromType = fromType;
            }

            public ParamKey Key { get; }

            public Parameter Parameter { get; }

            public bool FromType { get; }
        }

        private void PlanNested(SpecOptions options, SpecDefaults defaults, ICollection<ElementId>? scope)
        {
            const SpecModule M = SpecModule.Nested;
            Source(options, defaults, "Перенос во вложенные", defaults.NestedSource, defaults.NestedBlocked);
            var selected = new HashSet<ParamKey>(options.NestedParams);
            var candidates = new Dictionary<ParamKey, ParamCandidate>();
            foreach (ParamKey key in options.NestedParams)
            {
                candidates[key] = new ParamCandidate(key, "—") { Selected = true };
            }

            _progress.Report("Перенос: ищу родителей с общими вложенными семействами…");
            List<FamilyInstance> parents = Parents(options.Scope, scope);
            if (options.Scope != NestedScope.Model && parents.Count == 0)
            {
                _plan.AddIssue(M, Severity.Warning, string.Empty, "в выбранной области нет родителей с общими вложенными семействами", string.Empty)
                    .Action = "Выделите в модели родителей или их вложенные (или выберите другую область) и нажмите «Обновить».";
            }
            int withNested = 0;
            var byLevel = new SortedDictionary<int, int>();
            var childKeysByFamily = new Dictionary<(long, long), List<ParamKey>>();
            for (int i = 0; i < parents.Count; i++)
            {
                if (i % 200 == 0)
                {
                    _progress.Report($"Перенос: родитель {i + 1} из {parents.Count}", i, parents.Count);
                    _progress.ThrowIfCancelled();
                }
                FamilyInstance parent = parents[i];
                List<(FamilyInstance Element, int Level)> nested = CollectNested(parent);
                if (nested.Count == 0) continue;
                withNested++;
                foreach ((FamilyInstance _, int level) in nested) byLevel[level] = byLevel.TryGetValue(level, out int n) ? n + 1 : 1;

                Dictionary<ParamKey, ParamSlot> source = ParentParams(parent);
                string parentLabel = FamilyLabel(parent);
                long parentId = parent.Id.ToLong();
                foreach (ParamKey key in selected.Where(k => !source.ContainsKey(k)))
                {
                    MissingOnParent(parent, key, parentLabel, nested.Count);
                }

                foreach ((FamilyInstance child, int _) in nested)
                {
                    List<ParamKey> childKeys = ChildKeys(child, childKeysByFamily);
                    var childSet = new HashSet<ParamKey>(childKeys);
                    string subject = parentLabel + " → " + FamilyLabel(child);
                    long childId = child.Id.ToLong();
                    OwnSystemRule? own = OwnSystemRules.Find(options.OwnSystem, child.Symbol?.Family?.Name ?? string.Empty);
                    OwnSystemCode? ownCode = own == null ? null : ResolveOwnCode(child, own);
                    foreach (KeyValuePair<ParamKey, ParamSlot> pair in source)
                    {
                        ParamKey key = pair.Key;
                        bool isSelected = selected.Contains(key);
                        if (!childSet.Contains(key))
                        {
                            if (isSelected) MissingOnChild(child, key, subject, childId);
                            continue;
                        }
                        Parameter? target = _access.Find(child, key);
                        if (target == null) continue;
                        if (!candidates.TryGetValue(key, out ParamCandidate? candidate))
                        {
                            candidate = new ParamCandidate(key, ParamAccess.DataTypeLabel(pair.Value.Parameter));
                            candidates[key] = candidate;
                        }
                        else if (candidate.DataType == "—")
                        {
                            candidate.DataType = ParamAccess.DataTypeLabel(pair.Value.Parameter);
                        }
                        candidate.Nested++;
                        int ownIndex = own?.TargetIndex(key.Name) ?? -1;
                        if (own != null && ownCode != null && ownIndex >= 0)
                        {
                            EvaluateOwnSystem(own, ownCode, ownIndex, key, target, child, isSelected, candidate, subject, childId);
                            continue;
                        }
                        EvaluatePair(pair.Value, target, child, options.NestedClearEmpty, isSelected, candidate, subject, childId, parentId, parentLabel);
                    }
                }
            }

            _plan.Candidates.AddRange(candidates.Values
                .OrderByDescending(c => c.Selected).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));
            string scopeText = options.Scope == NestedScope.Model ? "вся модель" : options.Scope == NestedScope.ActiveView ? "активный вид" : "выделенные";
            _plan.Notes.Add($"Перенос: область — {scopeText}; родителей с общими вложенными {withNested}, вложенных {byLevel.Values.Sum()}"
                + (byLevel.Count > 0 ? " (" + string.Join(", ", byLevel.Select(p => $"ур. {p.Key}: {p.Value}")) + ")" : string.Empty)
                + "; параметры: " + (options.NestedParams.Count == 0 ? "не выбраны" : string.Join(", ", options.NestedParams.Select(k => k.Name)))
                + (options.NestedClearEmpty ? "; пустое у родителя очищает вложенное." : "."));
            _plan.Notes.Add(options.OwnSystem.Count == 0 ? "Своя система у вложенных: правил нет."
                : "Своя система у вложенных: " + string.Join("; ", options.OwnSystem.Select(r => $"«{r.Mask}» ← {r.Source} ({r.TargetsText})"))
                    + (_ownMatched > 0 ? $"; вложенных по правилам {_ownMatched}." : "; подходящих вложенных нет."));
            if (options.NestedParams.Count == 0)
            {
                _plan.AddIssue(M, Severity.Warning, string.Empty, "не выбран ни один параметр — переносить нечего", string.Empty)
                    .Action = "Отметьте параметры на вкладке «Параметры» и нажмите «Обновить».";
            }
        }

        private List<FamilyInstance> Parents(NestedScope scopeKind, ICollection<ElementId>? scope)
        {
            // Элементы области окно берёт при «Обновить» и передаёт те же в пробный прогон и запись
            IEnumerable<FamilyInstance> source = scope != null || scopeKind != NestedScope.Model
                ? (scope ?? Array.Empty<ElementId>()).Select(id => _doc.GetElement(id)).OfType<FamilyInstance>()
                : new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>();
            var seen = new HashSet<long>();
            var result = new List<FamilyInstance>();
            foreach (FamilyInstance fi in source)
            {
                (Element? top, int _) = TopParent(fi);
                if (!(top is FamilyInstance parent) || !seen.Add(parent.Id.ToLong())) continue;
                if (IsModel(parent) && parent.GetSubComponentIds().Count > 0) result.Add(parent);
            }
            return result;
        }

        /// <summary>Параметры родителя, которые можно переносить: экземпляра, а если такого нет — типа.</summary>
        private Dictionary<ParamKey, ParamSlot> ParentParams(FamilyInstance parent)
        {
            var result = new Dictionary<ParamKey, ParamSlot>();
            foreach (Parameter p in parent.Parameters)
            {
                ParamKey? key = Transferable(p);
                if (key != null && !result.ContainsKey(key)) result[key] = new ParamSlot(key, p, false);
            }
            Element? type = TypeOf(parent);
            if (type != null)
            {
                foreach (Parameter p in type.Parameters)
                {
                    ParamKey? key = Transferable(p);
                    if (key != null && !result.ContainsKey(key)) result[key] = new ParamSlot(key, p, true);
                }
            }
            return result;
        }

        /// <summary>Ключи параметров экземпляра вложенного. У экземпляров одного семейства набор одинаковый — кэш по семейству и категории.</summary>
        private List<ParamKey> ChildKeys(FamilyInstance child, Dictionary<(long, long), List<ParamKey>> cache)
        {
            var cacheKey = (child.Symbol?.Family?.Id.ToLong() ?? child.Id.ToLong(), child.Category?.Id.ToLong() ?? 0);
            if (cache.TryGetValue(cacheKey, out List<ParamKey>? keys)) return keys;
            keys = new List<ParamKey>();
            foreach (Parameter p in child.Parameters)
            {
                ParamKey? key = Transferable(p);
                if (key != null && !keys.Contains(key)) keys.Add(key);
            }
            cache[cacheKey] = keys;
            return keys;
        }

        /// <summary>Ключ переносимого параметра; ADSK_Группирование не переносится: им управляет группирование.</summary>
        private ParamKey? Transferable(Parameter p)
        {
            if (!ParamAccess.IsTransferable(p)) return null;
            ParamKey? key = _access.KeyOf(p);
            if (key == null) return null;
            return key.Kind == ParamKeyKind.Shared && (key.Guid == KnownParameters.AdskGrouping.Guid || key.Guid == ParamProfile.Own(ParamRole.Group).Guid) ? null : key;
        }

        private void EvaluatePair(ParamSlot source, Parameter target, FamilyInstance child, bool clearEmpty, bool isSelected,
            ParamCandidate candidate, string subject, long childId, long parentId, string parentLabel)
        {
            const SpecModule M = SpecModule.Nested;
            ParamKey key = source.Key;
            if (target.StorageType != source.Parameter.StorageType)
            {
                candidate.Problems++;
                if (isSelected)
                {
                    _plan.AddIssue(M, Severity.Error, key.Name,
                        $"несовместимый тип: у родителя {ParamAccess.DataTypeLabel(source.Parameter)}, у вложенного {ParamAccess.DataTypeLabel(target)}", subject, childId)
                        .Action = "Замените параметр в одном из семейств на тот же параметр из ФОП.";
                }
                return;
            }
            ParamValue? value = (!source.FromType && _pending.TryGetValue((parentId, key.Id), out ParamValue? planned) ? planned : null)
                ?? ParamAccess.Read(source.Parameter);
            if (ParamAccess.IsEmpty(value))
            {
                if (!clearEmpty || source.Parameter.StorageType != StorageType.String)
                {
                    candidate.ParentEmpty++;
                    if (isSelected)
                    {
                        _plan.AddIssue(M, Severity.Info, key.Name, "у родителя значение пустое — вложенные не трогаем", parentLabel, parentId)
                            .Action = "Заполните параметр у родителя (или модулем, который его заполняет) и обновите.";
                    }
                    return;
                }
                value = ParamValue.OfText(string.Empty);
            }

            ParamValue? current = ParamAccess.Read(target);
            if (current != null && current.SameAs(value!))
            {
                // Совпадение — до проверки «только чтение»: параметр, связанный в семействе с родителем, уже получает значение средствами Revit
                if (isSelected) _plan.CountUnchanged(M);
                return;
            }
            if (target.IsReadOnly)
            {
                candidate.Problems++;
                if (isSelected)
                {
                    _plan.AddIssue(M, Severity.Error, key.Name,
                        "у вложенного параметр только для чтения, и значение отличается от родителя: он связан в семействе с другим параметром или задан формулой", subject, childId)
                        .Action = "Правьте семейство: свяжите параметр вложенного с тем же параметром родителя или уберите формулу.";
                }
                return;
            }
            (Severity Severity, string Reason)? blocked = _guard.Check(child, target);
            if (blocked != null)
            {
                candidate.Problems++;
                if (isSelected) Guard(M, child, target, key.Name, subject);
                return;
            }
            candidate.WouldChange++;
            if (!isSelected) return;
            string from = source.FromType ? "тип родителя" : "родитель";
            AddChange(M, child, key, current?.Display ?? string.Empty, value!, $"{from} {parentId} ({parentLabel})", parentId);
        }

        private void MissingOnParent(FamilyInstance parent, ParamKey key, string parentLabel, int nestedCount)
        {
            const SpecModule M = SpecModule.Nested;
            long parentId = parent.Id.ToLong();
            Parameter? any = _access.Find(parent, key) ?? (TypeOf(parent) is Element type ? _access.Find(type, key) : null);
            if (any != null && !ParamAccess.IsTransferable(any))
            {
                _plan.AddIssue(M, Severity.Warning, key.Name, "параметр — ссылка на элемент, такие не переносятся", parentLabel, parentId, nestedCount);
            }
            else if (key.Kind == ParamKeyKind.Shared && parent.GetParameters(key.Name).Count > 0)
            {
                _plan.AddIssue(M, Severity.Error, key.Name, "у родителя параметр с этим именем, но другим GUID — не из ФОП отдела", parentLabel, parentId, nestedCount)
                    .Action = "Замените параметр в семействе на параметр из ФОП отдела.";
            }
            else
            {
                _plan.AddIssue(M, Severity.Info, key.Name, "у родителя нет параметра — вложенные не трогаем", parentLabel, parentId, nestedCount);
            }
        }

        private void MissingOnChild(FamilyInstance child, ParamKey key, string subject, long childId)
        {
            const SpecModule M = SpecModule.Nested;
            if (TypeOf(child) is Element type && _access.Find(type, key) != null)
            {
                _plan.AddIssue(M, Severity.Warning, key.Name, "у вложенного — параметр ТИПА: запись изменила бы все экземпляры типа", subject, childId)
                    .Action = "Сделайте параметр параметром экземпляра во вложенном семействе.";
            }
            else if (key.Kind == ParamKeyKind.Shared && child.GetParameters(key.Name).Count > 0)
            {
                _plan.AddIssue(M, Severity.Error, key.Name, "у вложенного параметр с этим именем, но другим GUID — не из ФОП отдела", subject, childId)
                    .Action = "Замените параметр во вложенном семействе на параметр из ФОП отдела.";
            }
            else
            {
                string category = child.Category?.Name ?? "?";
                _plan.AddIssue(M, Severity.Info, key.Name, $"у вложенного нет параметра (категория «{category}»)", subject, childId)
                    .Action = key.Kind == ParamKeyKind.Shared || key.Kind == ParamKeyKind.Project
                        ? $"Если нужен в спецификации: Управление → Параметры проекта → {key.Name} → категория «{category}»."
                        : string.Empty;
            }
        }

        /// <summary>Общие вложенные модельных категорий, обход в ширину (марки и УГО внутри семейств не трогаем).</summary>
        private List<(FamilyInstance Element, int Level)> CollectNested(FamilyInstance parent)
        {
            var result = new List<(FamilyInstance, int)>();
            var seen = new HashSet<long> { parent.Id.ToLong() };
            var queue = new Queue<(FamilyInstance, int)>();
            queue.Enqueue((parent, 0));
            while (queue.Count > 0)
            {
                (FamilyInstance current, int level) = queue.Dequeue();
                if (level >= MaxNestedLevel) continue;
                foreach (ElementId id in current.GetSubComponentIds())
                {
                    if (!(_doc.GetElement(id) is FamilyInstance child) || !seen.Add(id.ToLong())) continue;
                    if (!IsModel(child)) continue;
                    result.Add((child, level + 1));
                    queue.Enqueue((child, level + 1));
                }
            }
            return result;
        }

        // ---------------------------------------------------------------- группирование

        private void PlanGrouping(SpecOptions options, SpecDefaults defaults)
        {
            const SpecModule M = SpecModule.Grouping;
            ParamKey target = ParamProfile.TargetKey(ParamRole.Group);
            Source(options, defaults, "Таблица групп", defaults.GroupingSource, defaults.GroupingBlocked);
            _plan.RuleSources.Add(options.Kits.Rules.Count == 0 ? "Комплекты проекта: нет (шкафы ПК — автоматически)"
                : $"Комплекты проекта: правил {options.Kits.Rules.Count} ({options.KitsStamp ?? "в модели не сохранены"})");
            var groups = new GroupingTable(options.GroupRules);
            var tableIssues = new List<ConfigIssue>();
            foreach (GroupRule rule in groups.Rules.Where(r => r.Category.Length > 0 && r.Category != GroupingTable.AnyCategory))
            {
                if (!Enum.TryParse(rule.Category, out BuiltInCategory bic))
                {
                    _plan.AddIssue(M, Severity.Warning, target.Name, $"категория «{rule.Category}» не найдена в Revit — строка таблицы не действует", string.Empty)
                        .Action = "Исправьте категорию в таблице групп.";
                    continue;
                }
                // Имя для окна и тем категориям таблицы, которых в модели нет (спринклеры) или у которых нет параметра (гибкие трубы)
                string? label = CategoryLabel(bic);
                if (label != null && !_plan.Categories.ContainsKey(rule.Category)) _plan.Categories[rule.Category] = label;
            }

            ConfigLoader loader = Host.CreateConfigLoader(Host.LoadSettings());
            CsvTable? kitTable = LoadTable(loader, M, SpecRuleTables.KitParameters);
            KitParameterNames? kitNames = kitTable != null ? SpecRuleTables.ParseKitParameters(kitTable, tableIssues) : null;
            foreach (ConfigIssue issue in tableIssues) AddConfigIssue(M, issue);

            _progress.Report("Группирование: собираю элементы…");
            List<Element> elements = new FilteredElementCollector(_doc).WhereElementIsNotElementType().Where(IsModel).ToList();
            var tops = new Dictionary<long, (Element? Top, int Depth)>();

            KitPlan kits = kitNames != null ? PlanKits(elements, kitNames, groups, tops) : new KitPlan();
            foreach (KitIssue issue in kits.Issues) AddKitIssue(issue, "Комплекты шкафов");
            ProjectKitPlan project = PlanProjectKits(elements, options.Kits, groups, tops);
            foreach (KitIssue issue in project.Issues) AddKitIssue(issue, "Комплекты проекта");

            int service = 0;
            var skipped = new Dictionary<GroupRule, int>();
            var used = new HashSet<GroupRule>();
            for (int i = 0; i < elements.Count; i++)
            {
                if (i % 2000 == 0)
                {
                    _progress.Report($"Группирование: элемент {i + 1} из {elements.Count}", i, elements.Count);
                    _progress.ThrowIfCancelled();
                }
                Element element = elements[i];
                long id = element.Id.ToLong();
                Parameter? p = element.get_Parameter(target.Guid);
                string? category = element.Category.ToBuiltInCategory()?.ToString();
                string? family = (TypeOf(element) as ElementType)?.FamilyName;
                if (p != null)
                {
                    if (!string.IsNullOrEmpty(family)) _plan.FamilyNames.Add(family!);
                    if (category != null && !_plan.Categories.ContainsKey(category)) _plan.Categories[category] = element.Category?.Name ?? category;
                }
                GroupRule? rule = Resolve(groups, element, category, family, tops);
                if (rule != null) used.Add(rule);
                if (rule?.Skip == true)
                {
                    if (p != null) skipped[rule] = skipped.TryGetValue(rule, out int n) ? n + 1 : 1;
                    continue;
                }
                bool kit = kits.Values.TryGetValue(id, out string? value);
                string? kitSource = kit ? "комплект шкафа" : null;
                if (!kit && project.Values.TryGetValue(id, out string? projectValue))
                {
                    kit = true;
                    value = projectValue;
                    kitSource = project.Sources[id];
                }
                if (!kit) value = rule?.Group;
                if (value == null) continue;

                if (p == null)
                {
                    if (TypeOf(element)?.get_Parameter(target.Guid) != null)
                    {
                        _plan.AddIssue(M, Severity.Warning, target.Name, "параметр ТИПА — не пишется (изменил бы все экземпляры типа)", FamilyLabel(element), id)
                            .Action = $"Сделайте {target.Name} параметром экземпляра (Управление → Параметры проекта).";
                    }
                    else if (kit || groups.IsListed(category))
                    {
                        _plan.AddIssue(M, Severity.Info, target.Name, $"у категории нет параметра {target.Name}", element.Category?.Name ?? "?", id)
                            .Action = "Назначьте параметр категории (Управление → Параметры проекта), если она нужна в спецификации.";
                    }
                    continue;
                }
                if (IsServiceItem(element))
                {
                    service++;
                    continue;
                }
                if (p.StorageType != StorageType.String)
                {
                    _plan.AddIssue(M, Severity.Error, target.Name, "параметр не текстовый", FamilyLabel(element), id);
                    continue;
                }
                string old = p.AsString() ?? string.Empty;
                if (old == value)
                {
                    _plan.CountUnchanged(M);
                    continue;
                }
                if (p.IsReadOnly)
                {
                    // Как в adsk_grouping v2: значение задано в семействе намеренно или формулой — не ошибка модели, но в спецификации будет своё
                    _plan.AddIssue(M, Severity.Warning, target.Name, "только для чтения (задан формулой или связан в семействе)", FamilyLabel(element), id)
                        .Action = $"Уберите формулу или связь у {target.Name} в семействе — или поставьте семейству «не трогать» в таблице групп.";
                    continue;
                }
                if (Guard(M, element, p, target.Name, FamilyLabel(element))) continue;
                string source = kitSource ?? (rule!.IsFamilyRule ? $"семейство «{rule.Family}» → {value}" : $"категория → {value}");
                AddChange(M, element, target, old, ParamValue.OfText(value), source, null);
            }

            foreach (GroupRule rule in groups.Rules.Where(r => r.IsFamilyRule && !used.Contains(r)))
            {
                _plan.AddIssue(M, Severity.Info, target.Name, $"строка таблицы «{rule.Family}» не совпала ни с одним семейством модели", string.Empty)
                    .Action = "Проверьте маску имени семейства (* — любые символы).";
            }
            string skippedText = skipped.Count == 0 ? string.Empty
                : "; «не трогать»: " + string.Join(", ", skipped.Select(p => $"{(p.Key.Family.Length > 0 ? p.Key.Family : p.Key.Category)} — {p.Value}"));
            _plan.Notes.Add($"Группирование: элементов модели {elements.Count}; служебных позиций (_SYS_…, SYS_Ключ), которые не трогаются: {service}"
                + skippedText + "; "
                + (kitNames == null ? "комплекты шкафов не обработаны (kit_params.csv)." : $"шкафов {kits.Cabinets}, комплектующих {kits.Components}."));
            if (options.Kits.Rules.Count > 0)
            {
                _plan.Notes.Add($"Комплекты проекта: правил {options.Kits.Rules.Count}, родителей {project.Parents}, вложенных из состава {project.Members}"
                    + (project.NewNumbers.Count == 0 ? "." : "; новые номера K (сохранятся в модели при «Применить»): "
                        + string.Join(", ", project.NewNumbers.Select(n => $"{n.Family} : {n.Type} → {n.K}")) + "."));
            }
            if (groups.SortSurprises().Count > 0)
            {
                (string first, string second) = groups.SortSurprises()[0];
                _plan.AddIssue(M, Severity.Warning, target.Name, $"номер «{first}» встанет в спецификации перед «{second}»: Revit сортирует текст посимвольно", string.Empty)
                    .Action = "Поставьте ведущий ноль (02, 10) в таблице групп.";
            }
        }

        /// <summary>Правило таблицы групп для элемента: семейство верхнего родителя, своё семейство, категория, «*».</summary>
        private GroupRule? Resolve(GroupingTable groups, Element element, string? category, string? family, Dictionary<long, (Element? Top, int Depth)> tops)
        {
            string? topFamily = null;
            string? topCategory = null;
            if (element is FamilyInstance fi && fi.SuperComponent != null)
            {
                (Element? top, int _) = CachedTop(fi, tops);
                if (top != null)
                {
                    topFamily = (TypeOf(top) as ElementType)?.FamilyName;
                    topCategory = top.Category.ToBuiltInCategory()?.ToString();
                }
            }
            return groups.Resolve(category, family, topCategory, topFamily);
        }

        private static (Element? Top, int Depth) CachedTop(FamilyInstance fi, Dictionary<long, (Element? Top, int Depth)> tops)
        {
            long id = fi.Id.ToLong();
            if (tops.TryGetValue(id, out (Element? Top, int Depth) cached)) return cached;
            (Element? Top, int Depth) top = TopParent(fi);
            tops[id] = top;
            return top;
        }

        private KitPlan PlanKits(List<Element> elements, KitParameterNames names, GroupingTable groups, Dictionary<long, (Element? Top, int Depth)> tops)
        {
            const SpecModule M = SpecModule.Grouping;
            _progress.Report("Группирование: комплекты пожарных шкафов…");
            var records = new List<KitElement>();
            var topElements = new Dictionary<long, Element>();
            foreach (FamilyInstance fi in elements.OfType<FamilyInstance>())
            {
                (Element? top, int depth) = CachedTop(fi, tops);
                bool isComponent = fi.LookupParameter(names.ComponentFlag) != null;
                long id = fi.Id.ToLong();
                if (top == null)
                {
                    if (isComponent) records.Add(new KitElement { Id = id, TooDeep = true, TypeLabel = TypeLabel(fi) });
                    continue;
                }
                double? topNumber = Number((top as FamilyInstance)?.Symbol?.LookupParameter(names.CabinetNumber));
                double? number;
                if (depth == 0)
                {
                    number = topNumber;
                    if (number != null && number.Value >= 1)
                    {
                        // Номера шкафов заняты для комплектов проекта; семейство шкафа правилом проекта не собирается
                        _cabinetFamilies.Add(FamilyLabel(fi));
                        GroupRule? cabinetRow = TopRule(groups, fi);
                        _cabinetKeys.Add((cabinetRow == null || cabinetRow.Skip ? string.Empty : cabinetRow.Group, (int)Math.Round(number.Value, MidpointRounding.AwayFromZero)));
                    }
                    if ((number == null || number.Value < 1) && !isComponent) continue;
                }
                else
                {
                    if (!isComponent) continue;
                    IList<Parameter> keys = fi.GetParameters(names.ComponentKey);
                    if (keys.Count > 1)
                    {
                        _plan.AddIssue(M, Severity.Error, names.ComponentKey, $"у элемента {keys.Count} параметра «{names.ComponentKey}» — неясно, какой ключ брать", FamilyLabel(fi), id)
                            .Action = "Оставьте один параметр с этим именем (в проекте и в семействе начинки).";
                        continue;
                    }
                    number = keys.Count == 1 ? Number(keys[0]) : null;
                }
                // «Не трогать» у шкафа в таблице групп — весь комплект не трогаем
                if (TopRule(groups, top)?.Skip == true) continue;
                topElements[top.Id.ToLong()] = top;
                records.Add(new KitElement
                {
                    Id = id,
                    TopId = top.Id.ToLong(),
                    Depth = depth,
                    IsComponent = isComponent,
                    Number = number,
                    TopNumber = topNumber,
                    TypeLabel = TypeLabel(fi),
                });
            }
            return KitGrouping.Plan(records, topId => topElements.TryGetValue(topId, out Element? top) ? TopRule(groups, top)?.Group : null);
        }

        private void AddKitIssue(KitIssue issue, string subject)
        {
            PlanIssue row = _plan.AddIssue(SpecModule.Grouping, issue.Severity, ParamProfile.Target(ParamRole.Group).Name, issue.Message, subject, null, Math.Max(1, issue.Ids.Count));
            row.Ids.AddRange(issue.Ids);
            row.Action = issue.Action;
        }

        /// <summary>
        /// Комплекты проекта (D-039): семейства с общими вложенными для вкладки «Комплекты» (состав, сколько штук, тексты для поиска)
        /// и значения «группа.K.ранг» по правилам из настройки.
        /// </summary>
        private ProjectKitPlan PlanProjectKits(List<Element> elements, KitSettings kits, GroupingTable groups, Dictionary<long, (Element? Top, int Depth)> tops)
        {
            _progress.Report("Группирование: комплекты проекта…");
            var counts = new Dictionary<long, Dictionary<(string Family, string Type), int>>();
            var nestedCategories = new Dictionary<(string Family, string Type), string>();
            var topElements = new Dictionary<long, FamilyInstance>();
            var records = new List<ProjectKitElement>();
            KitSettings active = kits.Clone();
            foreach (KitRule rule in kits.Rules.Where(r => _cabinetFamilies.Contains(r.Family)))
            {
                active.Rules.Remove(rule);
                _plan.AddIssue(SpecModule.Grouping, Severity.Warning, ParamProfile.Target(ParamRole.Group).Name,
                    $"«{rule.Label}» — шкаф ПК: его комплект собирается по Комплект_Номер, правило проекта не действует", "Комплекты проекта")
                    .Action = "Удалите правило во вкладке «Комплекты».";
            }

            foreach (FamilyInstance fi in elements.OfType<FamilyInstance>())
            {
                (Element? top, int depth) = CachedTop(fi, tops);
                if (!(top is FamilyInstance parent)) continue;
                (string family, string type) = Labels(fi);
                long id = fi.Id.ToLong();
                if (depth == 0)
                {
                    if (active.HasRule(family))
                    {
                        records.Add(new ProjectKitElement { Id = id, TopId = id, Depth = 0, Family = family, Type = type });
                        topElements[id] = fi;
                    }
                    continue;
                }
                long topId = parent.Id.ToLong();
                if (!counts.TryGetValue(topId, out Dictionary<(string Family, string Type), int>? nested))
                {
                    nested = new Dictionary<(string Family, string Type), int>();
                    counts[topId] = nested;
                    topElements[topId] = parent;
                }
                nested[(family, type)] = nested.TryGetValue((family, type), out int n) ? n + 1 : 1;
                if (!nestedCategories.ContainsKey((family, type))) nestedCategories[(family, type)] = fi.Category?.Name ?? string.Empty;
                if (active.HasRule(FamilyLabel(parent))) records.Add(new ProjectKitElement { Id = id, TopId = topId, Depth = depth, Family = family, Type = type });
            }

            ProjectKitPlan project = ProjectKitGrouping.Plan(records, active,
                topId => topElements.TryGetValue(topId, out FamilyInstance? top) ? TopRule(groups, top) : null, _cabinetKeys);
            _plan.KitNumbers.AddRange(project.Numbers);
            _plan.NewKitNumbers.AddRange(project.NewNumbers);
            _plan.KitFamilies.AddRange(KitFamilies(counts, nestedCategories, topElements, groups));
            return project;
        }

        /// <summary>Сводка для вкладки «Комплекты»: по семейству — типоразмеры, вложенные и сколько их в одном экземпляре, тексты для поиска.</summary>
        private List<KitFamilyInfo> KitFamilies(Dictionary<long, Dictionary<(string Family, string Type), int>> counts,
            Dictionary<(string Family, string Type), string> nestedCategories, Dictionary<long, FamilyInstance> topElements, GroupingTable groups)
        {
            var infos = new Dictionary<string, KitFamilyInfo>(StringComparer.OrdinalIgnoreCase);
            var texts = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var samples = new Dictionary<(string Family, string Type), int>();
            var stats = new Dictionary<(string Family, string ParentType, string NestedFamily, string NestedType), (int Total, int Having, int Min, int Max)>();
            foreach (KeyValuePair<long, Dictionary<(string Family, string Type), int>> pair in counts)
            {
                FamilyInstance parent = topElements[pair.Key];
                (string family, string type) = Labels(parent);
                if (!infos.TryGetValue(family, out KitFamilyInfo? info))
                {
                    GroupRule? row = TopRule(groups, parent);
                    info = new KitFamilyInfo(family)
                    {
                        Category = parent.Category?.Name ?? string.Empty,
                        IsCabinet = _cabinetFamilies.Contains(family),
                        Group = row == null || row.Skip ? null : row.Group,
                        Skipped = row?.Skip == true,
                    };
                    infos[family] = info;
                    texts[family] = new HashSet<string>(StringComparer.Ordinal);
                }
                KitTypeInfo? typeInfo = info.Types.FirstOrDefault(t => string.Equals(t.Name, type, StringComparison.Ordinal));
                if (typeInfo == null)
                {
                    typeInfo = new KitTypeInfo(type);
                    info.Types.Add(typeInfo);
                    if (TypeOf(parent) is ElementType symbol) AddTexts(info, texts[family], symbol, "тип");
                }
                typeInfo.Instances++;
                int sampled = samples.TryGetValue((family, type), out int s) ? s : 0;
                if (sampled < KitTextSamples)
                {
                    samples[(family, type)] = sampled + 1;
                    AddTexts(info, texts[family], parent, "экземпляр");
                }
                foreach (KeyValuePair<(string Family, string Type), int> nested in pair.Value)
                {
                    var key = (family, type, nested.Key.Family, nested.Key.Type);
                    (int total, int having, int min, int max) = stats.TryGetValue(key, out var st) ? st : (0, 0, int.MaxValue, 0);
                    stats[key] = (total + nested.Value, having + 1, Math.Min(min, nested.Value), Math.Max(max, nested.Value));
                }
            }
            foreach (KeyValuePair<(string Family, string ParentType, string NestedFamily, string NestedType), (int Total, int Having, int Min, int Max)> pair in stats)
            {
                KitFamilyInfo info = infos[pair.Key.Family];
                int parents = info.Types.First(t => string.Equals(t.Name, pair.Key.ParentType, StringComparison.Ordinal)).Instances;
                info.Nested.Add(new KitNestedInfo(pair.Key.ParentType, pair.Key.NestedFamily, pair.Key.NestedType,
                    nestedCategories.TryGetValue((pair.Key.NestedFamily, pair.Key.NestedType), out string? category) ? category : string.Empty)
                {
                    Total = pair.Value.Total,
                    MinPerParent = pair.Value.Having < parents ? 0 : pair.Value.Min,
                    MaxPerParent = pair.Value.Max,
                });
            }
            return infos.Values.OrderBy(i => i.Family, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>Непустые текстовые параметры элемента — для поиска кандидатов по словам.</summary>
        private static void AddTexts(KitFamilyInfo info, HashSet<string> seen, Element element, string level)
        {
            foreach (Parameter p in element.Parameters)
            {
                if (seen.Count >= MaxKitTexts) return;
                if (p.StorageType != StorageType.String || !p.HasValue) continue;
                string? text = p.AsString();
                if (ValueText.IsBlank(text) || !seen.Add(text!)) continue;
                info.Texts.Add(new KitText($"{p.Definition?.Name ?? "?"} ({level})", text!));
            }
        }

        private GroupRule? TopRule(GroupingTable groups, Element top)
        {
            GroupRule? rule = groups.Resolve(top.Category.ToBuiltInCategory()?.ToString(), (TypeOf(top) as ElementType)?.FamilyName, null, null);
            return rule;
        }

        private static (Element? Top, int Depth) TopParent(FamilyInstance fi)
        {
            Element current = fi;
            for (int depth = 0; depth <= MaxKitDepth; depth++)
            {
                Element? super = (current as FamilyInstance)?.SuperComponent;
                if (super == null) return (current, depth);
                current = super;
            }
            return (null, MaxKitDepth + 1);
        }

        private static double? Number(Parameter? p)
        {
            if (p == null || !p.HasValue) return null;
            switch (p.StorageType)
            {
                case StorageType.Double:
                    return p.AsDouble();
                case StorageType.Integer:
                    return p.AsInteger();
                case StorageType.String:
                    string text = (p.AsString() ?? string.Empty).Trim().Replace(',', '.');
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;
                default:
                    return null;
            }
        }

        private static bool IsServiceItem(Element element)
        {
            if (element is FamilyInstance fi && (fi.Symbol?.FamilyName ?? string.Empty).StartsWith(ServiceFamilyPrefix, StringComparison.Ordinal)) return true;
            Parameter? key = element.get_Parameter(ServiceKeyGuid) ?? element.LookupParameter(ServiceKeyName);
            return key != null && key.StorageType == StorageType.String && !ValueText.IsBlank(key.AsString());
        }

        // ---------------------------------------------------------------- общее

        private bool Guard(SpecModule module, Element element, Parameter parameter, string name, string subject)
        {
            (Severity Severity, string Reason)? blocked = _guard.Check(element, parameter);
            if (blocked == null) return false;
            _plan.AddIssue(module, blocked.Value.Severity, name, blocked.Value.Reason, subject, element.Id.ToLong())
                .Action = blocked.Value.Reason.Contains("группе")
                    ? "Поправьте в режиме редактирования группы или разгруппируйте."
                    : "Синхронизируйтесь с центральной моделью (или попросите коллегу освободить элементы) и обновите.";
            return true;
        }

        private void AddChange(SpecModule module, Element element, ParamKey key, string old, ParamValue value, string source, long? sourceId)
        {
            (string family, string type) = Labels(element);
            _plan.AddChange(new ParamChange(module, element.Id.ToLong(), key, old, value)
            {
                Category = element.Category?.Name ?? string.Empty,
                Family = family,
                Type = type,
                Source = source,
                SourceId = sourceId,
            });
        }

        private string? CategoryLabel(BuiltInCategory bic)
        {
            try
            {
                return Category.GetCategory(_doc, bic)?.Name ?? LabelUtils.GetLabelFor(bic);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
        }

        private static bool IsModel(Element e)
        {
            try
            {
                // Материалы, трубопроводные системы и элементы узлов на видах — не позиции спецификации (Пушкарев 0.13.0: им ставилось «8»)
                if (e is Material || e is MEPSystem || e.OwnerViewId != ElementId.InvalidElementId) return false;
                return e.Category != null && e.Category.CategoryType == CategoryType.Model;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }

        private Element? TypeOf(Element e)
        {
            ElementId typeId = e.GetTypeId();
            return typeId == ElementId.InvalidElementId ? null : _doc.GetElement(typeId);
        }

        private (string Family, string Type) Labels(Element e)
        {
            long typeId = e.GetTypeId().ToLong();
            if (_typeLabels.TryGetValue(typeId, out (string, string) cached)) return cached;
            var type = TypeOf(e) as ElementType;
            (string, string) labels = (type?.FamilyName ?? e.Category?.Name ?? "?", type?.Name ?? string.Empty);
            if (type != null) _typeLabels[typeId] = labels;   // без типа подпись своя у каждой категории — не кэшируем
            return labels;
        }

        private string FamilyLabel(Element e) => Labels(e).Family;

        private string TypeLabel(Element e)
        {
            (string family, string type) = Labels(e);
            return family + " : " + type;
        }
    }
}
