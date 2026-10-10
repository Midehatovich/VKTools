using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VKTools.Revit.Infrastructure
{
    /// <summary>
    /// Метка «VK Tools ждёт Revit»: перед вызовом Revit, который может не вернуться (MEPSystem.Add на КрМа3, «К1»),
    /// пишется файл, после — удаляется. Если Revit закрыли принудительно, метка остаётся: при следующем запуске
    /// она переносится в список зависаний, и перед повторным запуском той же копии плагин предупреждает.
    /// Метка своя у каждого процесса Revit (в логе бывают одновременно Revit 2021 и 2022).
    /// </summary>
    internal static class HangMarker
    {
        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VKTools");

        private static string HangsPath => Path.Combine(Folder, "hangs.txt");

        private static int CurrentProcessId
        {
            get
            {
#if NET5_0_OR_GREATER
                return Environment.ProcessId;
#else
                using (Process p = Process.GetCurrentProcess()) return p.Id;
#endif
            }
        }

        private static string MarkerPath(int processId) => Path.Combine(Folder, $"running-{processId.ToString(CultureInfo.InvariantCulture)}.txt");

        /// <summary>Начало опасного вызова: документ, Id элемента, на котором он идёт, и описание для человека.</summary>
        public static void Begin(string documentTitle, long elementId, string what)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string line = string.Join("\t", DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                    Clean(documentTitle), elementId.ToString(CultureInfo.InvariantCulture), Clean(what));
                File.WriteAllText(MarkerPath(CurrentProcessId), line, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Метка — подстраховка; без неё вызов всё равно выполняется
            }
        }

        public static void End()
        {
            try
            {
                File.Delete(MarkerPath(CurrentProcessId));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Останется метка — при следующем запуске она будет принята за зависание; лучше лишнее предупреждение, чем никакого
            }
        }

        /// <summary>При запуске: метки процессов, которых больше нет, — это прерванные вызовы. Переносятся в список зависаний.</summary>
        public static IEnumerable<string> CollectAbandoned()
        {
            var found = new List<string>();
            try
            {
                if (!Directory.Exists(Folder)) return found;
                foreach (string path in Directory.GetFiles(Folder, "running-*.txt"))
                {
                    string idText = Path.GetFileNameWithoutExtension(path).Substring("running-".Length);
                    if (int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) && IsAlive(pid)) continue;
                    string line = File.ReadAllText(path, Encoding.UTF8).Trim();
                    File.Delete(path);
                    if (line.Length == 0) continue;
                    File.AppendAllText(HangsPath, line + Environment.NewLine, Encoding.UTF8);
                    found.Add(line.Replace('\t', ' '));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Список зависаний — подстраховка
            }
            return found;
        }

        /// <summary>Зависания в этом документе на этих элементах: «дата — описание».</summary>
        public static List<string> Previous(string documentTitle, ICollection<long> elementIds)
        {
            var result = new List<string>();
            try
            {
                if (!File.Exists(HangsPath)) return result;
                string title = Clean(documentTitle);
                foreach (string line in File.ReadAllLines(HangsPath, Encoding.UTF8))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length < 4 || parts[1] != title) continue;
                    if (!long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) || !elementIds.Contains(id)) continue;
                    result.Add($"{parts[0]} — {parts[3]}");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Не прочитали — не предупредим
            }
            return result.Distinct().ToList();
        }

        private static bool IsAlive(int processId)
        {
            try
            {
                // Номер мог достаться другой программе — живым считаем только Revit
                using (Process p = Process.GetProcessById(processId)) return !p.HasExited && p.ProcessName.IndexOf("Revit", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static string Clean(string text) => (text ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
