using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Progress;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Spec
{
    /// <summary>
    /// «Система и этаж» (Р4): порт NestedParamSync v2.1, шаги 1, 1б и 2.
    /// Параметр системы := «Сокращение для системы» (пусто у трубы — сокращение типа системы, у фитинга и арматуры — у соседей
    /// по коннекторам, до 3 шагов; несколько систем или не подключён — не трогаем). Параметр этажа := имя уровня (по таблице этажей).
    /// Изоляция — значения своей основы. Вложенные пропускаются: их заполняет перенос во вложенные.
    /// </summary>
    internal sealed partial class SpecPlanner
    {
        private const int MaxNeighborHops = 3;

        /// <summary>Сколько элементов категории смотреть, собирая параметры для выбора в окне.</summary>
        private const int CandidateSamples = 40;

        private static readonly BuiltInParameter[] AbbreviationParams =
        {
            BuiltInParameter.RBS_DUCT_PIPE_SYSTEM_ABBREVIATION_PARAM,
            BuiltInParameter.RBS_SYSTEM_ABBREVIATION_PARAM,
        };

        /// <summary>«Уровень» семейства, «Базовый уровень» трубы, «Опорный уровень», «Уровень спецификации» — первый заполненный.</summary>
        private static readonly BuiltInParameter[] LevelParams =
        {
            BuiltInParameter.FAMILY_LEVEL_PARAM,
            BuiltInParameter.RBS_START_LEVEL_PARAM,
            BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
            BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM,
            BuiltInParameter.SCHEDULE_LEVEL_PARAM,
        };

        /// <summary>Значения, которые получат родители: перенос во вложенные в той же операции берёт их, а не старые.</summary>
        private readonly Dictionary<(long, string), ParamValue> _pending = new Dictionary<(long, string), ParamValue>();

        private readonly Dictionary<long, List<string>> _ownCodes = new Dictionary<long, List<string>>();
        private readonly Dictionary<long, (string? Value, string? Problem)> _systems = new Dictionary<long, (string?, string?)>();
        private HashSet<long> _neighborCategories = new HashSet<long>();

        private void PlanSystemFloor(SpecOptions options, SpecDefaults defaults)
        {
            const SpecModule M = SpecModule.SystemFloor;
            SystemFloorSettings sf = options.SystemFloor;
            if (options.IsDefault(defaults))
            {
                _plan.RuleSources.Add(ParamProfile.IsOwn
                    ? "Система и этаж: по умолчанию, свои параметры (Р_Сокращение системы, Р_Этаж, копия ADSK_ → Р_; категории NestedParamSync v2.1)"
                    : "Система и этаж: по умолчанию (VK_Сокращение системы и LSR_Имя системы, где он есть; ADSK_Этаж; категории NestedParamSync v2.1)");
            }
            else
            {
                string state = !options.SavedInProject ? "изменена в окне, в модели не сохранена" : options.SavedStamp ?? "сохранена в модели";
                _plan.RuleSources.Add($"Система и этаж: настройка проекта ({state})");
            }
            if (defaults.ProjectBlocked != null) Block(defaults.ProjectBlocked);

            ParamKey? systemKey = sf.WriteSystem ? sf.SystemParam : null;
            ParamKey? floorKey = sf.WriteFloor ? sf.FloorParam : null;
            ParamKey? mirrorKey = systemKey != null && sf.MirrorParam != null && !sf.MirrorParam.Equals(systemKey) ? sf.MirrorParam : null;
            if (sf.WriteSystem && systemKey == null)
            {
                _plan.AddIssue(M, Severity.Warning, string.Empty, "не выбран параметр системы — система не пишется", string.Empty)
                    .Action = "Выберите параметр на вкладке «Настройка» и нажмите «Обновить».";
            }
            if (sf.WriteFloor && floorKey == null)
            {
                _plan.AddIssue(M, Severity.Warning, string.Empty, "не выбран параметр этажа — этаж не пишется", string.Empty)
                    .Action = "Выберите параметр на вкладке «Настройка» и нажмите «Обновить».";
            }

            var categories = new List<BuiltInCategory>();
            foreach (string name in sf.Categories)
            {
                if (Enum.TryParse(name, out BuiltInCategory bic)) categories.Add(bic);
                else _plan.AddIssue(M, Severity.Warning, string.Empty, $"категория «{name}» не найдена в Revit — пропущена", string.Empty);
            }
            if (categories.Count == 0)
            {
                _plan.AddIssue(M, Severity.Warning, string.Empty, "не отмечена ни одна категория", string.Empty)
                    .Action = "Отметьте категории на вкладке «Настройка».";
            }
            _neighborCategories = new HashSet<long>(SystemFloorSettings.NeighborCategories
                .Select(n => Enum.TryParse(n, out BuiltInCategory bic) ? CategoryId(bic) : -1L).Where(id => id > 0));

            _progress.Report("Система и этаж: собираю элементы…");
            var elements = new List<Element>();
            var seen = new HashSet<long>();
            var candidates = new Dictionary<ParamKey, ParamCandidate>();
            foreach (BuiltInCategory bic in categories)
            {
                int sampled = 0;
                foreach (Element e in new FilteredElementCollector(_doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (e is FamilyInstance fi && fi.SuperComponent != null) continue;   // вложенные получат значение от родителя
                    if (!seen.Add(e.Id.ToLong())) continue;
                    elements.Add(e);
                    if (sampled++ < CandidateSamples) AddTextCandidates(e, candidates);
                }
            }
            // Родители других категорий с общими вложенными тоже стоят на уровне, а подключённые — в системе (как v2.1)
            int parents = 0;
            foreach (FamilyInstance fi in new FilteredElementCollector(_doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>())
            {
                if (fi.SuperComponent != null || seen.Contains(fi.Id.ToLong()) || !IsModel(fi) || IsServiceItem(fi)) continue;
                if (fi.GetSubComponentIds().Count == 0) continue;
                seen.Add(fi.Id.ToLong());
                elements.Add(fi);
                parents++;
            }
            List<Element> insulations = sf.Insulation
                ? new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_PipeInsulations).WhereElementIsNotElementType().ToList()
                : new List<Element>();
            int sampledInsulation = 0;
            foreach (Element ins in insulations)
            {
                if (sampledInsulation++ >= CandidateSamples) break;
                AddTextCandidates(ins, candidates);
            }
            foreach (ParamKey? key in new[] { sf.SystemParam, sf.FloorParam })
            {
                if (key != null && !candidates.ContainsKey(key)) candidates[key] = new ParamCandidate(key, "—");
            }
            _plan.Candidates.AddRange(candidates.Values.OrderByDescending(c => c.Nested).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));

            foreach (Level level in new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>())
            {
                if (!_plan.Levels.ContainsKey(level.Name)) _plan.Levels[level.Name] = 0;
            }

            int[] counts = new int[2];
            for (int i = 0; i < elements.Count; i++)
            {
                if (i % 500 == 0)
                {
                    _progress.Report($"Система и этаж: элемент {i + 1} из {elements.Count}", i, elements.Count);
                    _progress.ThrowIfCancelled();
                }
                Element e = elements[i];
                bool isParent = i >= elements.Count - parents;
                if (systemKey != null)
                {
                    (string? value, string? problem) = ResolveSystem(e);
                    // Родитель другой категории без своего сокращения — не элемент системы: молча пропускаем
                    if (!(isParent && OwnCodes(e).Count == 0))
                    {
                        PlanSelf(M, e, systemKey, value, problem, "«Сокращение для системы»", null);
                        PlanMirror(M, e, mirrorKey, value, problem, "«Сокращение для системы»", null);
                        counts[0]++;
                    }
                }
                if (floorKey != null)
                {
                    (string? value, string? problem) = ResolveFloor(e, sf, count: true);
                    PlanSelf(M, e, floorKey, value, problem, "уровень", null);
                    counts[1]++;
                }
            }

            int insulationDone = 0;
            for (int i = 0; i < insulations.Count; i++)
            {
                if (i % 500 == 0)
                {
                    _progress.Report($"Система и этаж: изоляция {i + 1} из {insulations.Count}", i, insulations.Count);
                    _progress.ThrowIfCancelled();
                }
                Element ins = insulations[i];
                Element? host = ins is InsulationLiningBase lining ? _doc.GetElement(lining.HostElementId) : null;
                if (host == null)
                {
                    _plan.AddIssue(M, Severity.Warning, string.Empty, "у изоляции не найдена основа — значения брать неоткуда", FamilyLabel(ins), ins.Id.ToLong())
                        .Action = "Удалите изоляцию или наложите её заново на трубу.";
                    continue;
                }
                insulationDone++;
                long hostId = host.Id.ToLong();
                if (systemKey != null)
                {
                    (string? value, string? problem) = ResolveSystem(host);
                    PlanSelf(M, ins, systemKey, value, problem == null ? null : "основа: " + problem, $"основа {hostId}", hostId);
                    PlanMirror(M, ins, mirrorKey, value, problem == null ? null : "основа: " + problem, $"основа {hostId}", hostId);
                }
                if (floorKey != null)
                {
                    (string? value, string? problem) = ResolveFloor(host, sf, count: false);
                    PlanSelf(M, ins, floorKey, value, problem == null ? null : "основа: " + problem, $"основа {hostId}", hostId);
                }
            }

            PlanOwnCopy(defaults);

            List<FloorRename> renames = sf.EffectiveFloors().ToList();
            _plan.Notes.Add($"Система и этаж: элементов {elements.Count - parents} в {categories.Count} категориях, родителей других категорий с вложенными {parents}, "
                + $"изоляции {(sf.Insulation ? insulationDone.ToString(System.Globalization.CultureInfo.InvariantCulture) : "не обрабатывается")}; "
                + $"система → {(systemKey?.Name ?? "не пишется")}{(mirrorKey != null ? " и " + mirrorKey.Name + " (где есть)" : string.Empty)}, этаж → {(floorKey?.Name ?? "не пишется")}"
                + (floorKey != null ? (renames.Count == 0 ? " (имя уровня как есть)" : $" (переименований уровней: {renames.Count})") : string.Empty)
                + (sf.ChainNested ? "; затем перенос во вложенные." : "."));
        }

        /// <summary>Текстовые параметры экземпляра — варианты для «Параметр системы» и «Параметр этажа».</summary>
        private void AddTextCandidates(Element e, Dictionary<ParamKey, ParamCandidate> candidates)
        {
            foreach (Parameter p in e.Parameters)
            {
                if (p.StorageType != StorageType.String || p.IsReadOnly) continue;
                ParamKey? key = _access.KeyOf(p);
                if (key == null || key.Kind == ParamKeyKind.BuiltIn) continue;
                if (!candidates.TryGetValue(key, out ParamCandidate? candidate))
                {
                    candidate = new ParamCandidate(key, "Текст");
                    candidates[key] = candidate;
                }
                else if (candidate.DataType == "—")
                {
                    candidate.DataType = "Текст";
                }
                candidate.Nested++;
            }
        }

        /// <summary>Второй параметр системы: только у элементов, где он есть, без замечаний про пустое (их уже дал основной).</summary>
        private void PlanMirror(SpecModule module, Element e, ParamKey? key, string? value, string? problem, string source, long? sourceId)
        {
            if (key == null || problem != null || string.IsNullOrEmpty(value)) return;
            Parameter? p = _access.Find(e, key);
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.String) return;
            PlanSelf(module, e, key, value, null, source, sourceId);
        }

        private void PlanSelf(SpecModule module, Element e, ParamKey key, string? value, string? problem, string source, long? sourceId)
        {
            long id = e.Id.ToLong();
            string subject = FamilyLabel(e);
            if (problem != null || string.IsNullOrEmpty(value))
            {
                _plan.AddIssue(module, Severity.Warning, key.Name, (problem ?? "значение пустое") + " — не меняем", subject, id)
                    .Action = "Подключите элемент к одной системе (или заполните параметр вручную) и обновите.";
                return;
            }
            Parameter? p = _access.Find(e, key);
            if (p == null)
            {
                Element? type = TypeOf(e);
                if (type != null && _access.Find(type, key) != null)
                {
                    _plan.AddIssue(module, Severity.Warning, key.Name, "параметр ТИПА: запись изменила бы все экземпляры типа — не пишем", subject, id)
                        .Action = "Сделайте параметр параметром экземпляра.";
                }
                else
                {
                    string category = e.Category?.Name ?? "?";
                    _plan.AddIssue(module, Severity.Info, key.Name, $"нет параметра у категории «{category}»", category, id)
                        .Action = $"Если нужен в спецификации: Управление → Параметры проекта → {key.Name} → категория «{category}».";
                }
                return;
            }
            if (p.StorageType != StorageType.String)
            {
                _plan.AddIssue(module, Severity.Error, key.Name, $"параметр не текстовый ({ParamAccess.DataTypeLabel(p)})", subject, id)
                    .Action = "Выберите текстовый параметр.";
                return;
            }
            string old = p.AsString() ?? string.Empty;
            ParamValue planned = ParamValue.OfText(value);
            _pending[(id, key.Id)] = planned;
            if (string.Equals(old, value, StringComparison.Ordinal))
            {
                _plan.CountUnchanged(module);
                return;
            }
            if (p.IsReadOnly)
            {
                _plan.AddIssue(module, Severity.Error, key.Name, "только для чтения: связан в семействе с другим параметром или задан формулой", subject, id)
                    .Action = "Уберите связь или формулу в семействе.";
                _pending.Remove((id, key.Id));
                return;
            }
            if (Guard(module, e, p, key.Name, subject))
            {
                _pending.Remove((id, key.Id));
                return;
            }
            AddChange(module, e, key, old, planned, source, sourceId);
        }

        // ---------------------------------------------------------------- система

        private (string? Value, string? Problem) ResolveSystem(Element e)
        {
            long id = e.Id.ToLong();
            if (_systems.TryGetValue(id, out (string?, string?) cached)) return cached;
            (string?, string?) result;
            List<string> codes = OwnCodes(e);
            if (codes.Count == 1)
            {
                result = (codes[0], null);
            }
            else if (codes.Count > 1)
            {
                result = (null, $"элемент в нескольких системах ({SystemCodes.Join(codes)})");
            }
            else if (!(e is FamilyInstance))
            {
                string abbreviation = SystemTypeAbbreviation(e);
                result = abbreviation.Length > 0 ? (abbreviation, null) : (null, "«Сокращение для системы» пусто — элемент не подключён к системе");
            }
            else if (_neighborCategories.Contains(e.Category?.Id.ToLong() ?? -1))
            {
                List<string> found = FromNeighbors(e);
                result = found.Count == 1 ? (found[0], null)
                    : found.Count > 1 ? (null, $"сокращение пусто, у соседей разные ({SystemCodes.Join(found)})")
                    : (null, "«Сокращение для системы» пусто — элемент не подключён к системе");
            }
            else
            {
                result = (null, "«Сокращение для системы» пусто — элемент не подключён к системе");
            }
            _systems[id] = result;
            return result;
        }

        private List<string> OwnCodes(Element e)
        {
            long id = e.Id.ToLong();
            if (_ownCodes.TryGetValue(id, out List<string>? codes)) return codes;
            string? raw = null;
            foreach (BuiltInParameter bip in AbbreviationParams)
            {
                Parameter? p = e.get_Parameter(bip);
                if (p == null) continue;
                raw = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
                break;
            }
            codes = SystemCodes.Split(raw);
            _ownCodes[id] = codes;
            return codes;
        }

        /// <summary>Труба без экземпляра системы: сокращение её «Типа системы».</summary>
        private string SystemTypeAbbreviation(Element e)
        {
            Parameter? p = e.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM);
            if (p == null || p.StorageType != StorageType.ElementId) return string.Empty;
            return (_doc.GetElement(p.AsElementId()) as MEPSystemType)?.Abbreviation?.Trim() ?? string.Empty;
        }

        /// <summary>Сокращения ближайших по коннекторам элементов с непустым сокращением (через фитинги и арматуру с пустым, до 3 шагов).</summary>
        private List<string> FromNeighbors(Element start)
        {
            var visited = new HashSet<long> { start.Id.ToLong() };
            var frontier = new List<Element> { start };
            for (int hop = 0; hop < MaxNeighborHops; hop++)
            {
                var found = new List<string>();
                var next = new List<Element>();
                foreach (Element current in frontier)
                {
                    foreach (Element neighbor in PipingNeighbors(current))
                    {
                        if (!visited.Add(neighbor.Id.ToLong())) continue;
                        List<string> codes = OwnCodes(neighbor);
                        if (codes.Count > 0)
                        {
                            foreach (string code in codes)
                            {
                                if (!found.Contains(code)) found.Add(code);
                            }
                        }
                        else if (neighbor is FamilyInstance)
                        {
                            next.Add(neighbor);
                        }
                    }
                }
                if (found.Count > 0 || next.Count == 0) return found;
                frontier = next;
            }
            return new List<string>();
        }

        private IEnumerable<Element> PipingNeighbors(Element e)
        {
            ConnectorManager? manager = e is FamilyInstance fi ? fi.MEPModel?.ConnectorManager : (e as MEPCurve)?.ConnectorManager;
            if (manager == null) yield break;
            long own = e.Id.ToLong();
            foreach (Connector c in manager.Connectors)
            {
                if (c.Domain != Domain.DomainPiping || !c.IsConnected) continue;
                foreach (Connector other in c.AllRefs)
                {
                    Element? owner = other.Owner;
                    if (owner == null || owner.Id.ToLong() == own) continue;
                    if (!_neighborCategories.Contains(owner.Category?.Id.ToLong() ?? -1)) continue;
                    yield return owner;
                }
            }
        }

        // ---------------------------------------------------------------- этаж

        private (string? Value, string? Problem) ResolveFloor(Element e, SystemFloorSettings sf, bool count)
        {
            Level? level = LevelOf(e);
            if (level == null) return (null, "уровень не определён");
            string name = level.Name;
            if (count) _plan.Levels[name] = _plan.Levels.TryGetValue(name, out int n) ? n + 1 : 1;
            string value = sf.FloorValue(name);
            return value.Length == 0 ? (null, $"имя уровня «{name}» после переименования пустое") : (value, null);
        }

        private Level? LevelOf(Element e)
        {
            foreach (BuiltInParameter bip in LevelParams)
            {
                Parameter? p = e.get_Parameter(bip);
                if (p == null || p.StorageType != StorageType.ElementId) continue;
                if (_doc.GetElement(p.AsElementId()) is Level level) return level;
            }
            if (e.LevelId != ElementId.InvalidElementId && _doc.GetElement(e.LevelId) is Level own) return own;
            return (e as MEPCurve)?.ReferenceLevel;
        }

        private long CategoryId(BuiltInCategory bic)
        {
            try
            {
                return Category.GetCategory(_doc, bic)?.Id.ToLong() ?? -1;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return -1;
            }
        }
    }
}
