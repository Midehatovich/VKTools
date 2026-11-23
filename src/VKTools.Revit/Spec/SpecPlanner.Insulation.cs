using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Progress;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;
using VKTools.Revit.Mep;

namespace VKTools.Revit.Spec
{
    /// <summary>
    /// «Изоляция» (Р4): порт InsulationPipeOD v1.1.2. Dнар трубы-основы → параметр Dнар изоляции (у фитинга и арматуры — Dнар
    /// присоединённых труб через ≤ 5 фитингов, на переходе больший); параметры изделия и ADSK_Наименование — по таблице «Изделия».
    /// </summary>
    internal sealed partial class SpecPlanner
    {
        private const int MaxOdHops = 5;
        private const double FeetToMm = 304.8;

        private readonly Dictionary<long, (double? OdFeet, string Note, bool Check)> _hostOd = new Dictionary<long, (double?, string, bool)>();
        private HashSet<long> _pipeCategories = new HashSet<long>();
        private HashSet<long> _walkCategories = new HashSet<long>();

        private void PlanInsulation(SpecOptions options, SpecDefaults defaults)
        {
            const SpecModule M = SpecModule.Insulation;
            InsulationSettings ins = options.Insulation;
            if (options.IsDefault(defaults))
            {
                _plan.RuleSources.Add("Изоляция: по умолчанию (U_Наружный диаметр, ROCKWOOL: марка ТУ, завод ROCKWOOL; наименование не пишется)");
            }
            else
            {
                _plan.RuleSources.Add("Изоляция: настройка проекта (" + (!options.SavedInProject ? "изменена в окне, в модели не сохранена" : options.SavedStamp ?? "сохранена в модели") + ")");
            }
            if (defaults.ProjectBlocked != null) Block(defaults.ProjectBlocked);

            var tableIssues = new List<ConfigIssue>();
            List<InsulationProduct> products = InsulationSettings.ParseProducts(ins.Products, tableIssues);
            foreach (ConfigIssue issue in tableIssues) AddConfigIssue(M, issue);
            if (tableIssues.Any(i => i.Severity == Severity.Error)) Block("в таблице «Изделия» ошибки.");

            _pipeCategories = new HashSet<long> { CategoryId(BuiltInCategory.OST_PipeCurves), CategoryId(BuiltInCategory.OST_FlexPipeCurves) };
            _walkCategories = new HashSet<long>(_pipeCategories) { CategoryId(BuiltInCategory.OST_PipeFitting), CategoryId(BuiltInCategory.OST_PipeAccessory) };

            _progress.Report("Изоляция: собираю изоляцию труб…");
            List<InsulationLiningBase> insulations = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_PipeInsulations)
                .WhereElementIsNotElementType().OfType<InsulationLiningBase>().ToList();

            // Варианты параметров для окна: параметры экземпляра изоляции (текст и число)
            var candidates = new Dictionary<ParamKey, ParamCandidate>();
            foreach (InsulationLiningBase sample in insulations.Take(CandidateSamples))
            {
                foreach (Parameter p in sample.Parameters)
                {
                    if ((p.StorageType != StorageType.String && p.StorageType != StorageType.Double) || p.IsReadOnly) continue;
                    ParamKey? key = _access.KeyOf(p);
                    if (key == null || key.Kind == ParamKeyKind.BuiltIn) continue;
                    if (!candidates.TryGetValue(key, out ParamCandidate? c)) candidates[key] = c = new ParamCandidate(key, ParamAccess.DataTypeLabel(p));
                    c.Nested++;
                }
            }
            ParamKey? odKey = ins.OdParam ?? candidates.Keys.FirstOrDefault(k => k.Name == InsulationSettings.DefaultOdName);
            foreach (ParamKey? key in new[] { odKey, ins.NameParam })
            {
                if (key != null && !candidates.ContainsKey(key)) candidates[key] = new ParamCandidate(key, "—");
            }
            _plan.Candidates.AddRange(candidates.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));

            if (ins.WriteOd && odKey == null)
            {
                _plan.AddIssue(M, Severity.Warning, InsulationSettings.DefaultOdName,
                    insulations.Count == 0 ? "изоляции в модели нет" : $"у изоляции нет параметра «{InsulationSettings.DefaultOdName}» — Dнар не пишется", string.Empty)
                    .Action = "Выберите параметр Dнар на вкладке «Настройка» или добавьте параметр проекта категории «Материалы изоляции трубопроводов».";
            }
            if (ins.WriteName)
            {
                if (ins.NameParam == null)
                {
                    _plan.AddIssue(M, Severity.Warning, string.Empty, "не выбран параметр наименования — наименование не пишется", string.Empty);
                }
                foreach (UpdaterInfo updater in SystemMergeExecutor.RegisteredUpdaters(_doc).Where(u => (u.ApplicationName + " " + u.UpdaterName).IndexOf("BIM2B", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    _plan.AddIssue(M, Severity.Warning, ins.NameParam?.Name ?? string.Empty,
                        $"в модели зарегистрирован апдейтер «{updater.UpdaterName}» ({updater.ApplicationName}): если у него есть формула наименования изоляции, он перепишет значение", string.Empty)
                        .Action = "Уберите в BIM2B формулу ADSK_Наименование для типов изоляции из таблицы «Изделия».";
                }
            }

            var typeDone = new HashSet<(long, string)>();
            var sizes = new SortedDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            var noProduct = new SortedDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            var preview = new SortedDictionary<string, (int Count, string Text)>(StringComparer.CurrentCultureIgnoreCase);
            int done = 0;
            for (int i = 0; i < insulations.Count; i++)
            {
                if (i % 500 == 0)
                {
                    _progress.Report($"Изоляция: {i + 1} из {insulations.Count}", i, insulations.Count);
                    _progress.ThrowIfCancelled();
                }
                InsulationLiningBase insulation = insulations[i];
                long id = insulation.Id.ToLong();
                Element? type = TypeOf(insulation);
                string typeName = type?.Name ?? string.Empty;
                string comment = type?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_COMMENTS)?.AsString()?.Trim() ?? string.Empty;
                string subject = "Изоляция : " + typeName;
                double? thickness = insulation.Thickness > 1e-9 ? insulation.Thickness * FeetToMm : (double?)null;

                Element? host = _doc.GetElement(insulation.HostElementId);
                if (host == null)
                {
                    _plan.AddIssue(M, Severity.Warning, string.Empty, "у изоляции не найдена основа — Dнар брать неоткуда", subject, id)
                        .Action = "Удалите изоляцию или наложите её заново на трубу.";
                    continue;
                }
                (double? odFeet, string note, bool check) = HostOd(host);
                if (odFeet == null)
                {
                    _plan.AddIssue(M, Severity.Warning, string.Empty, note, subject, id)
                        .Action = "Проверьте, что фитинг или арматура соединены с трубами.";
                    continue;
                }
                done++;
                double od = odFeet.Value * FeetToMm;
                string source = $"основа {host.Id.ToLong()}" + (note.Length > 0 ? ", " + note : string.Empty);
                if (check)
                {
                    _plan.AddIssue(M, Severity.Warning, odKey?.Name ?? string.Empty, "ПРОВЕРИТЬ: " + note, subject, id)
                        .Action = "Изоляция на переходе или тройнике: проверьте, тот ли диаметр (взят больший).";
                }

                InsulationProduct? product = InsulationSettings.Match(products, typeName, comment);
                if (preview.TryGetValue(typeName, out (int Count, string Text) shown))
                {
                    preview[typeName] = (shown.Count + 1, shown.Text);
                }
                else
                {
                    string example;
                    if (product == null) example = "нет строки в таблице — не называется";
                    else if (product.NameFormat.Length == 0) example = $"строка «{product.Mask}», формат имени пуст — имя не пишется";
                    else
                    {
                        string sample = InsulationText.Render(product.NameFormat, product.BaseName.Length > 0 ? product.BaseName : comment, thickness, od, product.CylinderFor(od), out string? sampleError);
                        example = $"строка «{product.Mask}» → " + (sampleError ?? "«" + sample + "»");
                    }
                    preview[typeName] = (1, example);
                }
                string sizeKey = $"{typeName} · δ {(thickness.HasValue ? InsulationText.Mm(thickness.Value) : "?")} · D {InsulationText.Mm(od)}";
                sizes[sizeKey] = sizes.TryGetValue(sizeKey, out int n) ? n + 1 : 1;
                if (product == null)
                {
                    noProduct[typeName] = noProduct.TryGetValue(typeName, out int m) ? m + 1 : 1;
                }
                else
                {
                    if (thickness.HasValue && !product.ThicknessInSeries(thickness.Value))
                    {
                        _plan.AddIssue(M, Severity.Warning, "Толщина изоляции", $"толщины {InsulationText.Mm(thickness.Value)} мм нет в ряду изделия «{product.Mask}»", subject, id)
                            .Action = "Проверьте толщину изоляции или ряд толщин в таблице «Изделия».";
                    }
                    if (product.Cylinders.Count > 0 && product.CylinderFor(od) == null)
                    {
                        _plan.AddIssue(M, Severity.Warning, string.Empty, $"Dнар {InsulationText.Mm(od)} мм больше наибольшего цилиндра ряда ({InsulationText.Mm(product.Cylinders.Max())})", subject, id);
                    }
                }

                // 1) Dнар
                if (ins.WriteOd && odKey != null) PlanOd(insulation, odKey, odFeet.Value, od, subject, source);

                if (product == null) continue;
                double? cylinder = product.CylinderFor(od);

                // 2) Параметры изделия: экземпляр, а если у изоляции это параметр типа — в тип один раз
                if (ins.WriteProduct)
                {
                    foreach (KeyValuePair<string, string> column in product.Values)
                    {
                        // Свой набор «Р_»: колонка ADSK_Марка старой таблицы пишется в Р_Марка, LSR_* — не пишется
                        string? columnParam = ParamProfile.MapNameToActive(column.Key);
                        if (columnParam == null) continue;
                        string value = InsulationText.Render(column.Value, product.BaseName.Length > 0 ? product.BaseName : comment, thickness, od, cylinder, out string? error);
                        if (error != null)
                        {
                            _plan.AddIssue(M, Severity.Warning, columnParam, error, subject, id);
                            continue;
                        }
                        PlanProductValue(insulation, type, columnParam, column.Value, value, subject, source, typeDone);
                    }
                }

                // 3) ADSK_Наименование
                if (ins.WriteName && ins.NameParam != null && product.NameFormat.Length > 0)
                {
                    string baseName = product.BaseName.Length > 0 ? product.BaseName : comment;
                    if (baseName.Length == 0 && product.NameFormat.IndexOf("{base}", StringComparison.Ordinal) >= 0)
                    {
                        _plan.AddIssue(M, Severity.Warning, ins.NameParam.Name, "у типа пусто в «Комментариях к типоразмеру», а «Основа имени» в таблице не задана", subject, id)
                            .Action = "Заполните «Комментарии к типоразмеру» типа изоляции или «Основу имени» в таблице «Изделия».";
                        continue;
                    }
                    string name = InsulationText.Render(product.NameFormat, baseName, thickness, od, cylinder, out string? error);
                    if (error != null)
                    {
                        _plan.AddIssue(M, Severity.Warning, ins.NameParam.Name, error, subject, id);
                        continue;
                    }
                    PlanText(M, insulation, ins.NameParam, name, subject, source);
                }
            }

            _plan.Notes.Add($"Изоляция: обработано {done} из {insulations.Count}; Dнар → {(ins.WriteOd ? odKey?.Name ?? "не найден" : "не пишется")}, "
                + $"наименование → {(ins.WriteName ? ins.NameParam?.Name ?? "не выбрано" : "не пишется")}, параметры изделия {(ins.WriteProduct ? "пишутся" : "не пишутся")}; "
                + $"изделий в таблице {products.Count}.");
            if (noProduct.Count > 0)
            {
                _plan.Notes.Add("Типы изоляции без строки в «Изделиях» (наименование и параметры изделия не пишутся): "
                    + string.Join("; ", noProduct.Select(p => $"{p.Key} — {p.Value} шт.")));
                foreach (KeyValuePair<string, int> p in noProduct)
                {
                    _plan.AddIssue(M, Severity.Info, string.Empty, $"тип «{p.Key}» не подходит ни под одну строку таблицы «Изделия»", "Изоляция : " + p.Key, null, p.Value)
                        .Action = "Если тип нужно называть — добавьте строку с его маской в таблицу «Изделия».";
                }
            }
            foreach (KeyValuePair<string, (int Count, string Text)> p in preview) _plan.Preview.Add($"{p.Key} ({p.Value.Count} шт.): {p.Value.Text}");
            if (sizes.Count > 0)
            {
                _plan.Notes.Add("Типоразмеры (тип · толщина · Dнар трубы · шт.): " + string.Join("; ", sizes.Select(p => $"{p.Key} · {p.Value}")));
            }
        }

        private void PlanOd(Element insulation, ParamKey key, double odFeet, double odMm, string subject, string source)
        {
            const SpecModule M = SpecModule.Insulation;
            long id = insulation.Id.ToLong();
            Parameter? p = _access.Find(insulation, key);
            if (p == null)
            {
                MissingInstanceParam(insulation, key.Name, subject);
                return;
            }
            ParamValue value;
            if (p.StorageType == StorageType.String)
            {
                value = ParamValue.OfText(InsulationText.Mm(odMm));
            }
            else if (p.StorageType == StorageType.Double)
            {
                value = ParamValue.OfDouble(IsPlainNumber(p) ? odMm : odFeet, InsulationText.Mm(odMm));
            }
            else
            {
                _plan.AddIssue(M, Severity.Error, key.Name, "параметр должен быть текстом, числом или длиной", subject, id);
                return;
            }
            PlanValue(M, insulation, key, p, value, subject, source);
        }

        private void PlanText(SpecModule module, Element element, ParamKey key, string text, string subject, string source)
        {
            Parameter? p = _access.Find(element, key);
            if (p == null)
            {
                MissingInstanceParam(element, key.Name, subject);
                return;
            }
            if (p.StorageType != StorageType.String)
            {
                _plan.AddIssue(module, Severity.Error, key.Name, $"параметр не текстовый ({ParamAccess.DataTypeLabel(p)})", subject, element.Id.ToLong());
                return;
            }
            PlanValue(module, element, key, p, ParamValue.OfText(text), subject, source);
        }

        private void PlanProductValue(Element insulation, Element? type, string name, string template, string value, string subject, string source, HashSet<(long, string)> typeDone)
        {
            const SpecModule M = SpecModule.Insulation;
            long id = insulation.Id.ToLong();
            Parameter? p = One(insulation, name, out string? why);
            if (p != null)
            {
                ParamKey? key = _access.KeyOf(p);
                if (key == null) _plan.AddIssue(M, Severity.Error, name, "параметр семейства или встроенный — такие не пишутся", subject, id);
                else if (p.StorageType != StorageType.String) _plan.AddIssue(M, Severity.Error, name, "параметр не текстовый", subject, id);
                else PlanValue(M, insulation, key, p, ParamValue.OfText(value), subject, source);
                return;
            }
            Parameter? tp = type == null ? null : One(type, name, out _);
            if (tp == null || type == null)
            {
                _plan.AddIssue(M, Severity.Info, name, why ?? $"нет параметра «{name}»", "Материалы изоляции трубопроводов", id)
                    .Action = $"Если нужен в спецификации: Управление → Параметры проекта → {name} → категория «Материалы изоляции трубопроводов».";
                return;
            }
            if (InsulationText.SizeDependent(template))
            {
                _plan.AddIssue(M, Severity.Error, name, "параметр ТИПА, а значение зависит от размера — сделайте его параметром экземпляра", subject, id);
                return;
            }
            if (!typeDone.Add((type.Id.ToLong(), name))) return;
            ParamKey? typeKey = _access.KeyOf(tp);
            if (typeKey == null || tp.StorageType != StorageType.String)
            {
                _plan.AddIssue(M, Severity.Error, name, "у типа параметр не текстовый или не общий/проекта", subject, type.Id.ToLong());
                return;
            }
            PlanValue(M, type, typeKey, tp, ParamValue.OfText(value), subject, "в тип «" + type.Name + "»");
        }

        private void PlanValue(SpecModule module, Element element, ParamKey key, Parameter p, ParamValue value, string subject, string source)
        {
            ParamValue? current = ParamAccess.Read(p);
            if (current != null && current.SameAs(value))
            {
                _plan.CountUnchanged(module);
                return;
            }
            if (p.IsReadOnly)
            {
                _plan.AddIssue(module, Severity.Error, key.Name, "только для чтения: связан в семействе или задан формулой", subject, element.Id.ToLong());
                return;
            }
            if (Guard(module, element, p, key.Name, subject)) return;
            AddChange(module, element, key, current?.Display ?? string.Empty, value, source, null);
        }

        private void MissingInstanceParam(Element element, string name, string subject)
        {
            Element? type = TypeOf(element);
            if (type != null && type.GetParameters(name).Count > 0)
            {
                _plan.AddIssue(SpecModule.Insulation, Severity.Error, name, "параметр ТИПА, нужен параметр экземпляра", subject, element.Id.ToLong());
            }
            else
            {
                _plan.AddIssue(SpecModule.Insulation, Severity.Error, name, "нет параметра у изоляции", "Материалы изоляции трубопроводов", element.Id.ToLong())
                    .Action = $"Управление → Параметры проекта → {name} → категория «Материалы изоляции трубопроводов».";
            }
        }

        /// <summary>Ровно один параметр с этим именем; иначе null и причина.</summary>
        private static Parameter? One(Element element, string name, out string? why)
        {
            IList<Parameter> found = element.GetParameters(name);
            why = found.Count > 1 ? $"{found.Count} параметров «{name}» — неоднозначно" : null;
            return found.Count == 1 ? found[0] : null;
        }

        /// <summary>«Число» без единиц: пишем миллиметры. Длина — внутренние единицы (футы).</summary>
        private static bool IsPlainNumber(Parameter p)
        {
            try
            {
#if REVIT2022_OR_GREATER
                string id = p.Definition.GetDataType().TypeId ?? string.Empty;
#else
                string id = p.Definition.GetSpecTypeId().TypeId ?? string.Empty;
#endif
                int dash = id.IndexOf('-');
                if (dash > 0) id = id.Substring(0, dash);
                return id.EndsWith(":number", StringComparison.Ordinal);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }

        /// <summary>Dнар трубы-основы (футы); у фитинга и арматуры — присоединённых труб через ≤ 5 фитингов, на переходе больший.</summary>
        private (double? OdFeet, string Note, bool Check) HostOd(Element host)
        {
            long hostId = host.Id.ToLong();
            if (_hostOd.TryGetValue(hostId, out (double?, string, bool) cached)) return cached;
            (double?, string, bool) result;
            if (_pipeCategories.Contains(host.Category?.Id.ToLong() ?? -1))
            {
                double? od = PipeOd(host);
                result = od.HasValue ? (od, string.Empty, false) : ((double?)null, "у трубы-основы нет «Наружного диаметра»", false);
            }
            else
            {
                var visited = new HashSet<long> { hostId };
                var frontier = new List<Element> { host };
                var pipes = new List<Element>();
                for (int hop = 0; hop <= MaxOdHops && pipes.Count == 0 && frontier.Count > 0; hop++)
                {
                    var next = new List<Element>();
                    foreach (Element e in frontier)
                    {
                        foreach (Element n in ConnectedOf(e, _walkCategories))
                        {
                            if (!visited.Add(n.Id.ToLong())) continue;
                            if (_pipeCategories.Contains(n.Category?.Id.ToLong() ?? -1)) pipes.Add(n);
                            else next.Add(n);
                        }
                    }
                    frontier = next;
                }
                List<double> ods = pipes.Select(PipeOd).Where(v => v.HasValue).Select(v => v!.Value).ToList();
                string what = host.Category?.Name ?? "основа";
                if (ods.Count == 0) result = (null, $"{what}: не найдено присоединённых труб с Dнар", false);
                else if (ods.Max() - ods.Min() > 1e-6)
                {
                    string shown = string.Join(" / ", ods.Select(v => v * FeetToMm).Distinct().OrderBy(v => v).Select(InsulationText.Mm));
                    result = (ods.Max(), $"переход: трубы рядом {shown} — взят больший", true);
                }
                else result = (ods[0], "по присоединённой трубе", false);
            }
            _hostOd[hostId] = result;
            return result;
        }

        private static double? PipeOd(Element pipe)
        {
            Parameter? p = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER);
            return p != null && p.HasValue && p.AsDouble() > 1e-6 ? p.AsDouble() : (double?)null;
        }

        private static IEnumerable<Element> ConnectedOf(Element e, HashSet<long> categories)
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
                    if (owner == null || owner.Id.ToLong() == own || !categories.Contains(owner.Category?.Id.ToLong() ?? -1)) continue;
                    yield return owner;
                }
            }
        }
    }
}
