using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using VKTools.Core.Logging;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Calc
{
    /// <summary>
    /// Служебное семейство расчётных позиций (REQ-CALC-006). Есть в проекте — берём его. Нет — плагин создаёт его через API
    /// из шаблона «Обобщённая модель» той версии Revit, в которой работает: файл .rfa в 2022 нельзя загрузить в 2021.
    /// В семействе только параметры SYS_ из ФОП SYS (GUID те же, что в _SYS_SpecCalcItem.rfa); ADSK_ и LSR_ — параметры
    /// проекта, их привязку к «Обобщённым моделям» добавляет «Применить» (D-041).
    /// </summary>
    internal static class CalcFamilyBuilder
    {
        public const string DefaultFamily = "_SYS_SpecCalcItem";
        public const string DefaultType = "Расчётная позиция";

        /// <summary>ФОП SYS (plugin/inputs/fop/SYS_SpecCalc_ФОП.txt): GUID совпадают с семейством пользователя.</summary>
        private const string SysSharedParameters =
            "# This is a Revit shared parameter file.\r\n# Do not edit manually.\r\n*META\tVERSION\tMINVERSION\r\nMETA\t2\t1\r\n*GROUP\tID\tNAME\r\nGROUP\t1\tSYS\r\n"
            + "*PARAM\tGUID\tNAME\tDATATYPE\tDATACATEGORY\tGROUP\tVISIBLE\tDESCRIPTION\tUSERMODIFIABLE\tHIDEWHENNOVALUE\r\n"
            + "PARAM\tba2a88aa-82d8-4f53-b4a8-493357b3b4e9\tSYS_Ключ\tTEXT\t\t1\t1\t\t1\t0\r\n"
            + "PARAM\t1800e7b3-7a14-408d-9d29-4f1b6a3c35f9\tSYS_Кол-во вручную\tTEXT\t\t1\t1\t\t1\t0\r\n"
            + "PARAM\t55ee5cc1-6fe1-4eaa-a568-ce129e59f682\tSYS_Основание\tTEXT\t\t1\t1\t\t1\t0\r\n"
            + "PARAM\t68b76ce7-4f25-431d-9eae-8fd68f1ff933\tSYS_Способ\tTEXT\t\t1\t1\t\t1\t0\r\n"
            + "PARAM\tbc891ceb-a0be-4641-871b-ea0ce1034128\tSYS_Кол-во расчёт\tNUMBER\t\t1\t1\t\t1\t0\r\n";

        /// <summary>Параметры, которые есть в семействе, созданном плагином.</summary>
        public static readonly IReadOnlyList<string> OwnParameters = new[] { CalcParams.Key, CalcParams.Manual, CalcParams.Basis, CalcParams.Mode, CalcParams.Calc };

        private static readonly Regex GenericModel = new Regex(@"(generic model|обобщ[её]нн\w* модел\w*|типов\w* модел\w*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex NotPlain = new Regex(@"(face|wall|ceiling|floor|roof|line|adaptive|pattern|two.level|based|грань|гран[ие]|стен|потол|перекр|крыш|лини|адаптив|узор|двух|основ)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Шаблон обобщённой модели без основы; null — не найден (и где искали).</summary>
        public static string? FindTemplate(Application app, out string searched)
        {
            var roots = new List<string>();
            try
            {
                if (!string.IsNullOrWhiteSpace(app.FamilyTemplatePath)) roots.Add(app.FamilyTemplatePath);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Путь к шаблонам не задан — ниже стандартные папки
            }
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            roots.Add(Path.Combine(programData, "Autodesk", "RVT " + app.VersionNumber, "Family Templates"));
            searched = string.Join("; ", roots.Distinct(StringComparer.OrdinalIgnoreCase));
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                List<string> files;
                try
                {
                    if (!Directory.Exists(root)) continue;
                    files = Directory.EnumerateFiles(root, "*.rft", SearchOption.AllDirectories).ToList();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    continue;
                }
                string? best = files
                    .Where(f => GenericModel.IsMatch(Path.GetFileNameWithoutExtension(f)) && !NotPlain.IsMatch(Path.GetFileNameWithoutExtension(f)))
                    .OrderByDescending(f => Path.GetFileName(f).IndexOf("метрич", StringComparison.OrdinalIgnoreCase) >= 0
                        || Path.GetFileName(f).IndexOf("metric", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ThenBy(f => f.Length)
                    .FirstOrDefault();
                if (best != null) return best;
            }
            return null;
        }

        /// <summary>
        /// Создать .rfa в %LocalAppData%\VKTools\families\&lt;версия Revit&gt;\ и вернуть путь. Модель проекта не трогает.
        /// Ошибка — InvalidOperationException с понятным текстом.
        /// </summary>
        public static string Build(Application app, string familyName, ILog log)
        {
            string? template = FindTemplate(app, out string searched);
            if (template == null)
            {
                throw new InvalidOperationException($"нет шаблона семейства «Обобщённая модель» (искал в: {searched}). "
                    + $"Загрузите {familyName}.rfa вручную (Вставка → Загрузить семейство) или установите шаблоны семейств Revit.");
            }
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VKTools", "families", app.VersionNumber);
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, familyName + ".rfa");
            string parametersFile = Path.Combine(folder, "SYS_SpecCalc_ФОП.txt");
            File.WriteAllText(parametersFile, SysSharedParameters, Encoding.Unicode);

            Document family = app.NewFamilyDocument(template);
            try
            {
                if (family.OwnerFamily.FamilyCategory?.Id.ToLong() != (long)BuiltInCategory.OST_GenericModel)
                {
                    throw new InvalidOperationException($"шаблон «{Path.GetFileName(template)}» не «Обобщённая модель». Загрузите {familyName}.rfa вручную.");
                }
                // ФОП SYS держим текущим до конца добавления: определения из него. Путь ФОП приложения возвращается в finally.
                string previous = app.SharedParametersFilename;
                var fallback = new List<string>();
                try
                {
                    List<ExternalDefinition> definitions = ReadDefinitions(app, parametersFile);
                    using (var t = new Transaction(family, "VK Tools: служебное семейство"))
                    {
                        t.Start();
                        FamilyManager manager = family.FamilyManager;
                        if (manager.CurrentType == null) manager.NewType(DefaultType);
                        foreach (ExternalDefinition definition in definitions)
                        {
                            if (manager.get_Parameter(definition.GUID) != null || manager.get_Parameter(definition.Name) != null) continue;
                            try
                            {
                                manager.AddDataParameter(definition, true);
                            }
                            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                            {
                                // «Shared parameter creation failed» (Пушкарев, 2026-11-23): плагин читает SYS_ по имени, поэтому
                                // параметр семейства с тем же именем и типом работает так же.
                                log.Warn($"SYS_ «{definition.Name}» как общий не добавлен ({ex.Message}) — добавлен параметр семейства с тем же именем.");
                                manager.AddPlainParameter(definition, true);
                                fallback.Add(definition.Name);
                            }
                        }
                        t.Commit();
                    }
                }
                finally
                {
                    app.SharedParametersFilename = previous ?? string.Empty;
                }
                if (fallback.Count > 0) log.Info("Служебное семейство: параметры семейства вместо общих — " + string.Join(", ", fallback));
                family.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true, MaximumBackups = 1 });
            }
            finally
            {
                family.Close(false);
            }
            log.Info($"Служебное семейство создано: {path} (шаблон {template}).");
            return path;
        }

        /// <summary>
        /// Определения из ФОП SYS. Revit читает ФОП только через путь в настройках приложения: на время чтения путь
        /// подменяется и сразу возвращается. Сам ФОП отдела не открывается и не меняется.
        /// </summary>
        private static List<ExternalDefinition> ReadDefinitions(Application app, string parametersFile)
        {
            app.SharedParametersFilename = parametersFile;
            DefinitionFile? file = app.OpenSharedParameterFile();
            List<ExternalDefinition> result = file?.Groups.Cast<DefinitionGroup>().SelectMany(g => g.Definitions.Cast<Definition>()).OfType<ExternalDefinition>().ToList()
                ?? new List<ExternalDefinition>();
            if (result.Count != OwnParameters.Count) throw new InvalidOperationException("Revit не прочитал параметры SYS_ для служебного семейства.");
            return result;
        }

        /// <summary>Загрузка в проект (внутри открытой транзакции). Семейство с тем же именем не перезаписывается.</summary>
        public static Family Load(Document doc, string path)
        {
            if (!doc.LoadFamily(path, new KeepExisting(), out Family family) || family == null)
            {
                throw new InvalidOperationException($"Revit не загрузил служебное семейство из {path}.");
            }
            return family;
        }

        private sealed class KeepExisting : IFamilyLoadOptions
        {
            public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
            {
                overwriteParameterValues = false;
                return false;
            }

            public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
            {
                source = FamilySource.Project;
                overwriteParameterValues = false;
                return false;
            }
        }
    }
}
