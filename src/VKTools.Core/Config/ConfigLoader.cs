using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VKTools.Core.Checks;
using VKTools.Core.Text;

namespace VKTools.Core.Config
{
    public enum ConfigSourceKind
    {
        /// <summary>Встроенные правила из установки плагина.</summary>
        BuiltIn,

        /// <summary>Файл из папки правил отдела, перекрывает встроенный целиком.</summary>
        Department,
    }

    /// <summary>Загруженный файл правил и всё, что о нём нужно знать отчёту.</summary>
    public sealed class LoadedTable
    {
        internal LoadedTable(RuleFileSpec spec)
        {
            Spec = spec;
        }

        public RuleFileSpec Spec { get; }

        /// <summary>Таблица; null, если не удалось загрузить даже встроенные правила.</summary>
        public CsvTable? Table { get; internal set; }

        public ConfigSourceKind Source { get; internal set; }

        public string SourcePath { get; internal set; } = string.Empty;

        public string EncodingName { get; internal set; } = string.Empty;

        public List<ConfigIssue> Issues { get; } = new List<ConfigIssue>();

        /// <summary>
        /// Запись в модель запрещена: правила отдела недоступны или с ошибками.
        /// Проверки работают на встроенных правилах, а изменения — нет, чтобы не записать в модель
        /// результат по другим правилам, чем у коллег (REQ-CFG-003).
        /// </summary>
        public bool WriteBlocked { get; internal set; }

        public string? WriteBlockedReason { get; internal set; }
    }

    /// <summary>Где искать правила.</summary>
    public sealed class ConfigLocations
    {
        public ConfigLocations(string builtInDirectory, string? departmentDirectory)
        {
            BuiltInDirectory = builtInDirectory ?? throw new ArgumentNullException(nameof(builtInDirectory));
            DepartmentDirectory = string.IsNullOrWhiteSpace(departmentDirectory) ? null : departmentDirectory!.Trim();
        }

        public string BuiltInDirectory { get; }

        public string? DepartmentDirectory { get; }
    }

    /// <summary>
    /// Загрузка правил: файл из папки отдела перекрывает встроенный целиком (не построчно),
    /// чтобы было однозначно видно, по какому файлу посчитан результат.
    /// </summary>
    public sealed class ConfigLoader
    {
        private readonly ConfigLocations _locations;

        public ConfigLoader(ConfigLocations locations)
        {
            _locations = locations ?? throw new ArgumentNullException(nameof(locations));
        }

        public LoadedTable Load(RuleFileSpec spec)
        {
            if (spec == null) throw new ArgumentNullException(nameof(spec));
            var result = new LoadedTable(spec);

            string? department = _locations.DepartmentDirectory;
            if (department != null)
            {
                if (!SafeDirectoryExists(department))
                {
                    Block(result, $"папка правил отдела недоступна: {department}. Использую встроенные правила.");
                }
                else
                {
                    string path = Path.Combine(department, spec.FileName);
                    if (SafeFileExists(path))
                    {
                        var issues = new List<ConfigIssue>();
                        CsvTable? table = TryLoad(path, spec, issues, out string encoding);
                        result.Issues.AddRange(issues);
                        if (table != null)
                        {
                            result.Table = table;
                            result.Source = ConfigSourceKind.Department;
                            result.SourcePath = path;
                            result.EncodingName = encoding;
                            return result;
                        }
                        Block(result, "файл правил отдела с ошибками. Использую встроенные правила.");
                    }
                }
            }

            string builtInPath = Path.Combine(_locations.BuiltInDirectory, spec.FileName);
            var builtInIssues = new List<ConfigIssue>();
            CsvTable? builtIn = TryLoad(builtInPath, spec, builtInIssues, out string builtInEncoding);
            result.Issues.AddRange(builtInIssues);
            if (builtIn == null)
            {
                Block(result, "встроенные правила повреждены. Переустановите VK Tools.");
                return result;
            }

            result.Table = builtIn;
            result.Source = ConfigSourceKind.BuiltIn;
            result.SourcePath = builtInPath;
            result.EncodingName = builtInEncoding;
            return result;
        }

        private static void Block(LoadedTable result, string reason)
        {
            result.WriteBlocked = true;
            result.WriteBlockedReason = result.WriteBlockedReason == null ? reason : result.WriteBlockedReason + " " + reason;
            result.Issues.Add(new ConfigIssue(Severity.Warning, result.Spec.FileName, null, reason));
        }

        internal static CsvTable? TryLoad(string path, RuleFileSpec spec, List<ConfigIssue> issues, out string encodingName)
        {
            encodingName = string.Empty;
            string text;
            try
            {
                text = TextFileReader.ReadAllText(path, out encodingName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                issues.Add(new ConfigIssue(Severity.Error, spec.FileName, null, $"не удалось прочитать {path}: {ex.Message}"));
                return null;
            }

            CsvTable table = CsvTable.Parse(text);
            return Validate(table, spec, issues) ? table : null;
        }

        internal static bool Validate(CsvTable table, RuleFileSpec spec, List<ConfigIssue> issues)
        {
            bool ok = true;
            if (table.SchemaVersion == null)
            {
                issues.Add(new ConfigIssue(Severity.Error, spec.FileName, null,
                    $"нет строки «#schema={spec.SchemaVersion}» с версией схемы"));
                ok = false;
            }
            else if (table.SchemaVersion.Value > spec.SchemaVersion)
            {
                issues.Add(new ConfigIssue(Severity.Error, spec.FileName, null,
                    $"файл для более новой версии VK Tools (схема {table.SchemaVersion}, плагин знает {spec.SchemaVersion}). Обновите плагин."));
                ok = false;
            }
            else if (table.SchemaVersion.Value < spec.SchemaVersion)
            {
                issues.Add(new ConfigIssue(Severity.Error, spec.FileName, null,
                    $"устаревшая схема {table.SchemaVersion}, нужна {spec.SchemaVersion}"));
                ok = false;
            }

            foreach (string column in spec.RequiredColumns)
            {
                if (!table.HasColumn(column))
                {
                    issues.Add(new ConfigIssue(Severity.Error, spec.FileName, null, $"нет колонки «{column}»"));
                    ok = false;
                }
            }
            return ok;
        }

        /// <summary>
        /// Недоступный сетевой путь (нет VPN) Windows проверяет до 30 секунд, и Revit бы завис.
        /// Ждём не дольше <see cref="NetworkTimeout"/> и считаем папку недоступной.
        /// </summary>
        private static bool SafeDirectoryExists(string path)
        {
            try
            {
                var task = Task.Run(() => Directory.Exists(path));
                return task.Wait(NetworkTimeout) && task.Result;
            }
            catch (AggregateException)
            {
                return false;
            }
        }

        internal static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(3);

        private static bool SafeFileExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                return false;
            }
        }
    }
}
