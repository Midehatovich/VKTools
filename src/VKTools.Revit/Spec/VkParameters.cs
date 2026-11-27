using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using VKTools.Core.Config;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Spec
{
    /// <summary>
    /// Свои параметры VK Tools в проекте из своего ФОП (GUID зашиты навсегда, <see cref="ParamProfile"/>): набор ADSK — только
    /// «VK_Сокращение системы»; свой набор — все «Р_*» (позиция, наименование… система). Нет в проекте — добавляется параметром проекта
    /// (экземпляра) к категориям ВК; есть, но не у всех категорий — категории добавляются, остальные не трогаются.
    /// ФОП отдела не открывается и не меняется: Revit на время чтения смотрит в свой файл.
    /// </summary>
    internal static class VkParameters
    {
        public const string TransactionName = "VK Tools: свои параметры";

        /// <summary>Категории, где нужна система: элементы ВК, изоляция, расчётные позиции (обобщённые модели).</summary>
        public static readonly IReadOnlyList<BuiltInCategory> Categories = new[]
        {
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory,
            BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Sprinklers,
            BuiltInCategory.OST_PipeInsulations, BuiltInCategory.OST_GenericModel,
        };

        /// <summary>Какие свои параметры нужны модели при активном наборе.</summary>
        public static IReadOnlyList<ParameterRef> Required() =>
            ParamProfile.IsOwn ? ParamProfile.OwnDefinitions.Select(d => d.Ref).ToList() : new[] { KnownParameters.VkSystem, ParamProfile.Own(ParamRole.Room).Ref, ParamProfile.Own(ParamRole.PositionKey).Ref, ParamProfile.Own(ParamRole.Zone).Ref };

        /// <summary>Нужно ли что-то менять: какого-то параметра нет или он привязан не ко всем категориям.</summary>
        public static bool NeedsSetup(Document doc, out List<Category> missing)
        {
            missing = new List<Category>();
            if (ParamProfile.IsOwn && LegacyBound(doc).Count > 0)
            {
                foreach (BuiltInCategory bic in Categories)
                {
                    Category? c = Category.GetCategory(doc, bic);
                    if (c != null && c.AllowsBoundParameters) missing.Add(c);
                }
                return true;
            }
            foreach (ParameterRef param in Required())
            {
                List<Category> lack = Missing(doc, param);
                foreach (Category c in lack)
                {
                    if (!missing.Any(m => m.Id == c.Id)) missing.Add(c);
                }
            }
            return missing.Count > 0;
        }

        private static List<Category> Missing(Document doc, ParameterRef param)
        {
            var missing = new List<Category>();
            (Definition? definition, ElementBinding? binding) = Find(doc, param.Guid);
            foreach (BuiltInCategory bic in Categories)
            {
                Category? category = Category.GetCategory(doc, bic);
                if (category == null || !category.AllowsBoundParameters) continue;
                if (definition == null || binding == null || !binding.Categories.Contains(category)) missing.Add(category);
            }
            return missing;
        }

        /// <summary>
        /// Добавить параметры или недостающие категории отдельной операцией. null — всё на месте или добавлено (текст — в <paramref name="note"/>);
        /// иначе причина отказа (модель не изменена).
        /// </summary>
        public static string? Ensure(Document doc, Application app, out string? note)
        {
            note = null;
            if (!NeedsSetup(doc, out _)) return null;
            if (doc.IsReadOnly) return "документ только для чтения";
            var notes = new List<string>();
            try
            {
                using (var t = new Transaction(doc, TransactionName))
                {
                    t.Start();
                    Dictionary<Guid, ExternalDefinition>? file = null;
                    foreach (ParameterRef param in Required())
                    {
                        List<Category> missing = Missing(doc, param);
                        if (missing.Count == 0) continue;
                        (Definition? existing, ElementBinding? binding) = Find(doc, param.Guid);
                        bool ok;
                        if (existing != null && binding != null)
                        {
                            CategorySet set = app.Create.NewCategorySet();
                            foreach (Category c in binding.Categories) set.Insert(c);
                            foreach (Category c in missing) set.Insert(c);
                            ElementBinding updated = binding is TypeBinding ? (ElementBinding)app.Create.NewTypeBinding(set) : app.Create.NewInstanceBinding(set);
                            ok = doc.ParameterBindings.ReInsertKeepingGroup(existing, updated);
                            notes.Add($"параметру «{param.Name}» добавлены категории: " + string.Join(", ", missing.Select(c => c.Name)));
                        }
                        else
                        {
                            file ??= ReadDefinitions(app);
                            if (!file.TryGetValue(param.Guid, out ExternalDefinition? definition)) throw new InvalidOperationException($"в своём ФОП VK Tools нет «{param.Name}»");
                            CategorySet set = app.Create.NewCategorySet();
                            foreach (Category c in missing) set.Insert(c);
                            ok = ParamProfile.OwnDefinitions.Any(d => d.Guid == param.Guid)
                                ? doc.ParameterBindings.InsertInOwnGroup(definition, app.Create.NewInstanceBinding(set))
                                : doc.ParameterBindings.InsertAsData(definition, app.Create.NewInstanceBinding(set));
                            notes.Add($"в проект добавлен параметр «{param.Name}» (экземпляра)");
                        }
                        if (!ok)
                        {
                            t.RollBack();
                            return $"Revit не принял привязку параметра «{param.Name}»";
                        }
                    }
                    if (ParamProfile.IsOwn)
                    {
                        string? migrated = MigrateLegacy(doc, notes);
                        if (migrated != null)
                        {
                            t.RollBack();
                            return migrated;
                        }
                    }
                    if (t.Commit() != TransactionStatus.Committed) return "Revit не сохранил параметры";
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException)
            {
                Host.Log.Error("Свои параметры VK Tools: не добавлены.", ex);
                return ex.Message;
            }
            note = string.Join("; ", notes) + " — категории: " + string.Join(", ", Categories.Select(c => Category.GetCategory(doc, c)?.Name).Where(n => n != null));
            Host.Log.Info("VK Tools: " + note + ".");
            return null;
        }

        /// <summary>Старые «Р_» 0.14.x (редактируемые вручную), которые ещё привязаны в проекте.</summary>
        private static List<SharedParameterElement> LegacyBound(Document doc)
        {
            var result = new List<SharedParameterElement>();
            foreach (Guid guid in ParamProfile.LegacyOwnGuids.Keys)
            {
                SharedParameterElement? shared = SharedParameterElement.Lookup(doc, guid);
                if (shared != null) result.Add(shared);
            }
            return result;
        }

        /// <summary>
        /// 0.15.0: перенос со старых «Р_» на нередактируемые вручную — значения элементов (экземпляр), графы спецификаций (место, ширины,
        /// сортировка, фильтры, комбинированные), затем старые параметры удаляются. Настройки модели со старыми GUID читаются как новые
        /// (<see cref="ParamProfile.Current"/>). Ошибка — причина, вызывающий откатывает транзакцию.
        /// </summary>
        private static string? MigrateLegacy(Document doc, List<string> notes)
        {
            List<SharedParameterElement> legacy = LegacyBound(doc);
            if (legacy.Count == 0) return null;
            int values = 0;
            var failed = new List<string>();
            var elements = new List<Element>();
            foreach (BuiltInCategory bic in Categories)
            {
                elements.AddRange(new FilteredElementCollector(doc).OfCategory(bic).WhereElementIsNotElementType());
            }
            foreach (SharedParameterElement old in legacy)
            {
                ParamRole role = ParamProfile.LegacyOwnGuids[old.GuidValue];
                Guid target = ParamProfile.Own(role).Guid;
                foreach (Element e in elements)
                {
                    Parameter? from = e.get_Parameter(old.GuidValue);
                    if (from == null || !from.HasValue) continue;
                    Parameter? to = e.get_Parameter(target);
                    if (to == null || to.StorageType != from.StorageType) continue;
                    bool ok;
                    switch (from.StorageType)
                    {
                        case StorageType.String:
                            string text = from.AsString() ?? string.Empty;
                            if (text.Length == 0) continue;
                            ok = to.Set(text);
                            break;
                        case StorageType.Double:
                            ok = to.Set(from.AsDouble());
                            break;
                        case StorageType.Integer:
                            ok = to.Set(from.AsInteger());
                            break;
                        default:
                            continue;
                    }
                    if (ok) values++;
                    else if (failed.Count < 20) failed.Add($"{old.Name} · Id {e.Id.ToLong()}");
                }
            }
            var scheduleNotes = new List<string>();
            foreach (ViewSchedule schedule in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
            {
                if (schedule.IsTitleblockRevisionSchedule || schedule.Definition == null) continue;
                var result = new Schedules.ScheduleResult();
                Schedules.ScheduleBuilder.RemapToOwn(doc, schedule, result, legacyOnly: true);
                foreach (string n in result.Notes) scheduleNotes.Add($"«{schedule.Name}»: {n}");
            }
            if (failed.Count > 0) Host.Log.Warn("Перенос на нередактируемые «Р_»: не записано — " + string.Join("; ", failed));
            foreach (string n in scheduleNotes) Host.Log.Info("Перенос на нередактируемые «Р_»: " + n);
            doc.Delete(legacy.Select(x => x.Id).ToList());
            notes.Add($"«Р_» переведены на нередактируемые вручную: значений перенесено {values}, спецификаций с правкой граф {scheduleNotes.Count}, старых параметров удалено {legacy.Count}"
                + (failed.Count > 0 ? $", не записалось {failed.Count} (в логе)" : string.Empty));
            return null;
        }

        private static (Definition? Definition, ElementBinding? Binding) Find(Document doc, Guid guid)
        {
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (it.Key is InternalDefinition definition && doc.GetElement(definition.Id) is SharedParameterElement shared && shared.GuidValue == guid)
                {
                    return (definition, it.Current as ElementBinding);
                }
            }
            return (null, null);
        }

        /// <summary>Определения из своего ФОП: путь к ФОП в Revit подменяется на время чтения и сразу возвращается.</summary>
        private static Dictionary<Guid, ExternalDefinition> ReadDefinitions(Application app)
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VKTools", "fop");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "VK_ФОП.txt");
            File.WriteAllText(path, ParamProfile.SharedParameterFile(), Encoding.Unicode);
            string previous = app.SharedParametersFilename;
            try
            {
                app.SharedParametersFilename = path;
                DefinitionFile? file = app.OpenSharedParameterFile();
                Dictionary<Guid, ExternalDefinition>? result = file?.Groups.Cast<DefinitionGroup>().SelectMany(g => g.Definitions.Cast<Definition>())
                    .OfType<ExternalDefinition>().ToDictionary(d => d.GUID);
                return result ?? throw new InvalidOperationException("Revit не прочитал свой ФОП VK Tools");
            }
            finally
            {
                app.SharedParametersFilename = previous ?? string.Empty;
            }
        }
    }
}
