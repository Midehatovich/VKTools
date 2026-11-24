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
    /// Свои параметры VK Tools в проекте: «VK_Сокращение системы» из своего ФОП (GUID в <see cref="KnownParameters.VkSystem"/>).
    /// Нет в проекте — добавляется параметром проекта (экземпляра) к категориям ВК; есть, но не у всех категорий — категории
    /// добавляются, остальные не трогаются. ФОП отдела не открывается и не меняется: Revit на время чтения смотрит в свой файл.
    /// </summary>
    internal static class VkParameters
    {
        public const string TransactionName = "VK Tools: параметр VK_Сокращение системы";

        /// <summary>Категории, где нужна система: элементы ВК, изоляция, расчётные позиции (обобщённые модели).</summary>
        public static readonly IReadOnlyList<BuiltInCategory> Categories = new[]
        {
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory,
            BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Sprinklers,
            BuiltInCategory.OST_PipeInsulations, BuiltInCategory.OST_GenericModel,
        };

        private static string SharedParameters =>
            "# This is a Revit shared parameter file.\r\n# Do not edit manually.\r\n*META\tVERSION\tMINVERSION\r\nMETA\t2\t1\r\n*GROUP\tID\tNAME\r\nGROUP\t1\tVK\r\n"
            + "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n"
            + $"PARAM\t{KnownParameters.VkSystem.Guid:D}\t{KnownParameters.VkSystem.Name}\tTEXT\t\t1\t1\tVK Tools: копия «Сокращения для системы» для спецификаций\t1\t0\r\n";

        /// <summary>Нужно ли что-то менять: параметра нет или он привязан не ко всем категориям.</summary>
        public static bool NeedsSetup(Document doc, out List<Category> missing)
        {
            missing = new List<Category>();
            (Definition? definition, ElementBinding? binding) = Find(doc);
            foreach (BuiltInCategory bic in Categories)
            {
                Category? category = Category.GetCategory(doc, bic);
                if (category == null || !category.AllowsBoundParameters) continue;
                if (definition == null || binding == null || !binding.Categories.Contains(category)) missing.Add(category);
            }
            return missing.Count > 0;
        }

        /// <summary>
        /// Добавить параметр или недостающие категории отдельной операцией. null — всё на месте или добавлено (текст — в <paramref name="note"/>);
        /// иначе причина отказа (модель не изменена).
        /// </summary>
        public static string? Ensure(Document doc, Application app, out string? note)
        {
            note = null;
            if (!NeedsSetup(doc, out List<Category> missing)) return null;
            if (doc.IsReadOnly) return "документ только для чтения";
            (Definition? existing, ElementBinding? binding) = Find(doc);
            try
            {
                using (var t = new Transaction(doc, TransactionName))
                {
                    t.Start();
                    bool ok;
                    if (existing != null && binding != null)
                    {
                        CategorySet set = app.Create.NewCategorySet();
                        foreach (Category c in binding.Categories) set.Insert(c);
                        foreach (Category c in missing) set.Insert(c);
                        ElementBinding updated = binding is TypeBinding ? (ElementBinding)app.Create.NewTypeBinding(set) : app.Create.NewInstanceBinding(set);
                        ok = doc.ParameterBindings.ReInsertKeepingGroup(existing, updated);
                        note = $"параметру «{KnownParameters.VkSystem.Name}» добавлены категории: " + string.Join(", ", missing.Select(c => c.Name));
                    }
                    else
                    {
                        ExternalDefinition definition = ReadDefinition(app);
                        CategorySet set = app.Create.NewCategorySet();
                        foreach (Category c in missing) set.Insert(c);
                        ok = doc.ParameterBindings.InsertAsData(definition, app.Create.NewInstanceBinding(set));
                        note = $"в проект добавлен параметр «{KnownParameters.VkSystem.Name}» (экземпляра, текст) для категорий: " + string.Join(", ", missing.Select(c => c.Name));
                    }
                    if (!ok)
                    {
                        t.RollBack();
                        note = null;
                        return "Revit не принял привязку параметра";
                    }
                    if (t.Commit() != TransactionStatus.Committed)
                    {
                        note = null;
                        return "Revit не сохранил параметр";
                    }
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException || ex is IOException || ex is UnauthorizedAccessException)
            {
                note = null;
                Host.Log.Error("VK_Сокращение системы: не добавлен.", ex);
                return ex.Message;
            }
            Host.Log.Info("VK Tools: " + note + ".");
            return null;
        }

        private static (Definition? Definition, ElementBinding? Binding) Find(Document doc)
        {
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (it.Key is InternalDefinition definition && doc.GetElement(definition.Id) is SharedParameterElement shared && shared.GuidValue == KnownParameters.VkSystem.Guid)
                {
                    return (definition, it.Current as ElementBinding);
                }
            }
            return (null, null);
        }

        /// <summary>Определение из своего ФОП: путь к ФОП в Revit подменяется на время чтения и сразу возвращается.</summary>
        private static ExternalDefinition ReadDefinition(Application app)
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VKTools", "fop");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "VK_ФОП.txt");
            File.WriteAllText(path, SharedParameters, Encoding.Unicode);
            string previous = app.SharedParametersFilename;
            try
            {
                app.SharedParametersFilename = path;
                DefinitionFile? file = app.OpenSharedParameterFile();
                ExternalDefinition? definition = file?.Groups.Cast<DefinitionGroup>().SelectMany(g => g.Definitions.Cast<Definition>())
                    .OfType<ExternalDefinition>().FirstOrDefault(d => d.GUID == KnownParameters.VkSystem.Guid);
                return definition ?? throw new InvalidOperationException("Revit не прочитал свой ФОП VK Tools");
            }
            finally
            {
                app.SharedParametersFilename = previous ?? string.Empty;
            }
        }
    }
}
