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
    /// Анализ «Параметры спецификации» (Р2): что запишут модули «Вложенные» и «Группирование».
    /// Модель не меняется. Поведение — как у Dynamo-эталонов: NestedParamSync v2.1 (шаг 3),
    /// adsk_grouping v2 и fire_cabinet_postprocess v1.0.
    /// </summary>
    internal sealed class SpecPlanner
    {
        /// <summary>Защита от зацикленной вложенности (как HARD_MAX_LEVEL в NestedParamSync).</summary>
        private const int MaxNestedLevel = 20;

        /// <summary>Шкаф ищется не глубже 10 уровней (fire_cabinet_postprocess).</summary>
        private const int MaxKitDepth = 10;

        /// <summary>Служебные семейства расчётных позиций и запаса (_SYS_SpecCalcItem, _SYS_ClampReserve): группу 9.x им ставит их модуль.</summary>
        private const string ServiceFamilyPrefix = "_SYS_";

        private static readonly Guid ServiceKeyGuid = new Guid("ba2a88aa-82d8-4f53-b4a8-493357b3b4e9");
        private const string ServiceKeyName = "SYS_Ключ";

        private readonly Document _doc;
        private readonly IOperationProgress _progress;
        private readonly EditGuard _guard;
        private readonly Dictionary<long, (string Family, string Type)> _typeLabels = new Dictionary<long, (string, string)>();
        private ParamPlan _plan = new ParamPlan(Array.Empty<SpecModule>());

        private SpecPlanner(Document doc, IOperationProgress progress)
        {
            _doc = doc;
            _progress = progress;
            _guard = new EditGuard(doc);
        }

        public static ParamPlan Build(Document doc, IReadOnlyCollection<SpecModule> modules, IOperationProgress progress, OperationTimings timings)
        {
            var planner = new SpecPlanner(doc, progress);
            planner._plan = new ParamPlan(modules);
            ConfigLoader loader = Host.CreateConfigLoader(Host.LoadSettings());
            foreach (SpecModule module in planner._plan.Modules)
            {
                progress.ThrowIfCancelled();
                if (module == SpecModule.Nested)
                {
                    using (timings.Measure("анализ вложенных")) planner.PlanNested(loader);
                }
                else
                {
                    using (timings.Measure("анализ группирования")) planner.PlanGrouping(loader);
                }
            }
            return planner._plan;
        }

        // ---------------------------------------------------------------- правила

        private CsvTable? LoadTable(ConfigLoader loader, SpecModule module, RuleFileSpec spec)
        {
            LoadedTable loaded = loader.Load(spec);
            string source = loaded.Source == ConfigSourceKind.Department ? "отдел" : "встроенные";
            _plan.RuleSources.Add(loaded.Table != null
                ? $"{spec.FileName}: {source}, {loaded.SourcePath} ({loaded.EncodingName})"
                : $"{spec.FileName}: НЕ ЗАГРУЖЕН");
            foreach (ConfigIssue issue in loaded.Issues) AddConfigIssue(module, issue);
            if (loaded.WriteBlocked)
            {
                string reason = $"{spec.FileName}: {loaded.WriteBlockedReason}";
                _plan.WriteBlockedReason = _plan.WriteBlockedReason == null ? reason : _plan.WriteBlockedReason + " " + reason;
            }
            return loaded.Table;
        }

        private void AddConfigIssue(SpecModule module, ConfigIssue issue)
        {
            _plan.AddIssue(module, issue.Severity, string.Empty, "Правила: " + issue, string.Empty).Action =
                "Исправьте файл правил (VK Tools → Настройки → «Проверить»).";
            Host.Log.Warn("Правила: " + issue);
        }

        // ---------------------------------------------------------------- вложенные

        private void PlanNested(ConfigLoader loader)
        {
            const SpecModule M = SpecModule.Nested;
            CsvTable? table = LoadTable(loader, M, SpecRuleTables.NestedParameters);
            if (table == null)
            {
                _plan.AddIssue(M, Severity.Error, string.Empty, "nested_params.csv не загружен — вложенные не обрабатываются", string.Empty);
                return;
            }
            ParsedRules<ParameterRef> parsed = SpecRuleTables.ParseNestedParameters(table);
            foreach (ConfigIssue issue in parsed.Issues) AddConfigIssue(M, issue);
            List<ParameterRef> rules = parsed.Items;
            if (rules.Count == 0)
            {
                _plan.AddIssue(M, Severity.Error, string.Empty, "в nested_params.csv нет ни одного параметра", string.Empty);
                return;
            }

            _progress.Report("Вложенные: ищу родителей с общими вложенными семействами…");
            List<FamilyInstance> parents = new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(fi => fi.SuperComponent == null && IsModel(fi) && fi.GetSubComponentIds().Count > 0)
                .ToList();

            int withNested = 0;
            var byLevel = new SortedDictionary<int, int>();
            for (int i = 0; i < parents.Count; i++)
            {
                if (i % 200 == 0)
                {
                    _progress.Report($"Вложенные: родитель {i + 1} из {parents.Count}", i, parents.Count);
                    _progress.ThrowIfCancelled();
                }
                FamilyInstance parent = parents[i];
                List<(FamilyInstance Element, int Level)> nested = CollectNested(parent);
                if (nested.Count == 0) continue;
                withNested++;
                foreach ((FamilyInstance _, int level) in nested) byLevel[level] = byLevel.TryGetValue(level, out int n) ? n + 1 : 1;
                foreach (ParameterRef rule in rules) PlanNestedRule(parent, nested, rule);
            }
            _plan.Notes.Add($"Вложенные: родителей с общими вложенными {withNested}, вложенных {byLevel.Values.Sum()}"
                + (byLevel.Count > 0 ? " (" + string.Join(", ", byLevel.Select(p => $"ур. {p.Key}: {p.Value}")) + ")" : string.Empty)
                + "; параметры: " + string.Join(", ", rules.Select(r => r.Name)) + ".");
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

        private void PlanNestedRule(FamilyInstance parent, List<(FamilyInstance Element, int Level)> nested, ParameterRef rule)
        {
            const SpecModule M = SpecModule.Nested;
            string parentLabel = FamilyLabel(parent);
            long parentId = parent.Id.ToLong();

            Parameter? source = parent.get_Parameter(rule.Guid);
            if (source == null)
            {
                if (TypeOf(parent)?.get_Parameter(rule.Guid) != null)
                {
                    _plan.AddIssue(M, Severity.Warning, rule.Name, "у родителя — параметр ТИПА, перенос идёт из экземпляра", parentLabel, parentId, nested.Count)
                        .Action = "Сделайте параметр параметром экземпляра в проекте или заполните вложенные вручную.";
                }
                else if (parent.GetParameters(rule.Name).Count > 0)
                {
                    _plan.AddIssue(M, Severity.Error, rule.Name, "у родителя параметр с этим именем, но другим GUID — не из ФОП отдела", parentLabel, parentId, nested.Count)
                        .Action = "Замените параметр в семействе на параметр из ФОП отдела.";
                }
                else
                {
                    _plan.AddIssue(M, Severity.Info, rule.Name, "у родителя нет параметра — вложенные не трогаем", parentLabel, parentId, nested.Count);
                }
                return;
            }
            if (source.StorageType != StorageType.String)
            {
                _plan.AddIssue(M, Severity.Error, rule.Name, "у родителя параметр не текстовый — переносится только текст", parentLabel, parentId, nested.Count);
                return;
            }
            string? value = source.AsString();
            if (ValueText.IsBlank(value))
            {
                _plan.AddIssue(M, Severity.Info, rule.Name, "у родителя значение пустое — вложенные не трогаем", parentLabel, parentId, nested.Count)
                    .Action = "Заполните параметр у родителя (или модулем, который его заполняет) и обновите.";
                return;
            }

            foreach ((FamilyInstance child, int _) in nested)
            {
                string subject = parentLabel + " → " + FamilyLabel(child);
                long childId = child.Id.ToLong();
                Parameter? target = child.get_Parameter(rule.Guid);
                if (target == null)
                {
                    if (TypeOf(child)?.get_Parameter(rule.Guid) != null)
                    {
                        _plan.AddIssue(M, Severity.Warning, rule.Name, "у вложенного — параметр ТИПА: запись изменила бы все экземпляры типа", subject, childId)
                            .Action = "Сделайте параметр параметром экземпляра во вложенном семействе.";
                    }
                    else if (child.GetParameters(rule.Name).Count > 0)
                    {
                        _plan.AddIssue(M, Severity.Error, rule.Name, "у вложенного параметр с этим именем, но другим GUID — не из ФОП отдела", subject, childId)
                            .Action = "Замените параметр во вложенном семействе на параметр из ФОП отдела.";
                    }
                    else
                    {
                        _plan.AddIssue(M, Severity.Info, rule.Name, "у вложенного нет параметра", subject, childId);
                    }
                    continue;
                }
                if (target.StorageType != StorageType.String)
                {
                    _plan.AddIssue(M, Severity.Error, rule.Name, "у вложенного параметр не текстовый", subject, childId);
                    continue;
                }
                string old = target.AsString() ?? string.Empty;
                if (old == value)
                {
                    // Совпадение — до проверки «только чтение»: параметр, связанный в семействе с родителем, уже получает значение средствами Revit
                    _plan.CountUnchanged(M);
                    continue;
                }
                if (target.IsReadOnly)
                {
                    _plan.AddIssue(M, Severity.Error, rule.Name,
                        "у вложенного параметр только для чтения, и значение отличается от родителя: он связан в семействе с другим параметром или формулой", subject, childId)
                        .Action = "Правьте семейство: свяжите параметр вложенного с тем же параметром родителя или уберите формулу.";
                    continue;
                }
                if (Guard(M, child, target, rule.Name, subject)) continue;
                AddChange(M, child, rule.Name, rule.Guid, old, value!, $"родитель {parentId} ({parentLabel})", parentId);
            }
        }

        // ---------------------------------------------------------------- группирование

        private void PlanGrouping(ConfigLoader loader)
        {
            const SpecModule M = SpecModule.Grouping;
            ParameterRef target = KnownParameters.AdskGrouping;
            CsvTable? groupingTable = LoadTable(loader, M, SpecRuleTables.Grouping);
            if (groupingTable == null)
            {
                _plan.AddIssue(M, Severity.Error, string.Empty, "grouping.csv не загружен — группирование не выполняется", string.Empty);
                return;
            }
            var configIssues = new List<ConfigIssue>();
            GroupingTable groups = SpecRuleTables.ParseGrouping(groupingTable, configIssues);
            CsvTable? kitTable = LoadTable(loader, M, SpecRuleTables.KitParameters);
            KitParameterNames? kitNames = kitTable != null ? SpecRuleTables.ParseKitParameters(kitTable, configIssues) : null;
            foreach (ConfigIssue issue in configIssues) AddConfigIssue(M, issue);

            _progress.Report("Группирование: собираю элементы…");
            List<Element> elements = new FilteredElementCollector(_doc).WhereElementIsNotElementType().Where(IsModel).ToList();

            KitPlan kits = kitNames != null ? PlanKits(elements, kitNames, groups) : new KitPlan();
            foreach (KitIssue issue in kits.Issues)
            {
                PlanIssue row = _plan.AddIssue(M, issue.Severity, target.Name, issue.Message, "Комплекты шкафов", null, issue.Ids.Count);
                row.Ids.AddRange(issue.Ids);
                row.Action = issue.Action;
            }

            int service = 0;
            for (int i = 0; i < elements.Count; i++)
            {
                if (i % 2000 == 0)
                {
                    _progress.Report($"Группирование: элемент {i + 1} из {elements.Count}", i, elements.Count);
                    _progress.ThrowIfCancelled();
                }
                Element element = elements[i];
                long id = element.Id.ToLong();
                string? category = element.Category.ToBuiltInCategory()?.ToString();
                bool kit = kits.Values.TryGetValue(id, out string? value);
                if (!kit) value = groups.GroupFor(category);
                if (value == null) continue;

                Parameter? p = element.get_Parameter(target.Guid);
                if (p == null)
                {
                    if (TypeOf(element)?.get_Parameter(target.Guid) != null)
                    {
                        _plan.AddIssue(M, Severity.Warning, target.Name, "параметр ТИПА — не пишется (изменил бы все экземпляры типа)", FamilyLabel(element), id)
                            .Action = "Сделайте ADSK_Группирование параметром экземпляра (Управление → Параметры проекта).";
                    }
                    else if (kit || groups.IsListed(category))
                    {
                        _plan.AddIssue(M, Severity.Info, target.Name, "у категории нет параметра ADSK_Группирование", element.Category?.Name ?? "?", id)
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
                        .Action = "Уберите формулу или связь у ADSK_Группирование в семействе.";
                    continue;
                }
                if (Guard(M, element, p, target.Name, FamilyLabel(element))) continue;
                AddChange(M, element, target.Name, target.Guid, old, value!, kit ? "комплект шкафа" : $"категория → {value}", null);
            }
            _plan.Notes.Add($"Группирование: элементов модели {elements.Count}; служебных позиций (_SYS_…, SYS_Ключ), которые не трогаются: {service}; "
                + (kitNames == null ? "комплекты шкафов не обработаны (kit_params.csv)." : $"шкафов {kits.Cabinets}, комплектующих {kits.Components}."));
        }

        private KitPlan PlanKits(List<Element> elements, KitParameterNames names, GroupingTable groups)
        {
            const SpecModule M = SpecModule.Grouping;
            _progress.Report("Группирование: комплекты пожарных шкафов…");
            var records = new List<KitElement>();
            var tops = new Dictionary<long, Element>();
            foreach (FamilyInstance fi in elements.OfType<FamilyInstance>())
            {
                (Element? top, int depth) = TopParent(fi);
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
                tops[top.Id.ToLong()] = top;
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
            return KitGrouping.Plan(records, topId => tops.TryGetValue(topId, out Element? top) ? groups.GroupFor(top.Category.ToBuiltInCategory()?.ToString()) : null);
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

        private void AddChange(SpecModule module, Element element, string name, Guid guid, string old, string value, string source, long? sourceId)
        {
            (string family, string type) = Labels(element);
            _plan.AddChange(new ParamChange(module, element.Id.ToLong(), name, guid, old, value)
            {
                Category = element.Category?.Name ?? string.Empty,
                Family = family,
                Type = type,
                Source = source,
                SourceId = sourceId,
            });
        }

        private static bool IsModel(Element e)
        {
            try
            {
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
