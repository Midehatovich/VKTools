using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VKTools.Core.Logging
{
    /// <summary>
    /// Лог в файл: один файл на день (vktools-ГГГГММДД.log), старые удаляются через <see cref="RetentionDays"/> дней.
    /// Ошибки записи лога никогда не прерывают работу плагина.
    /// </summary>
    public sealed class FileLog : ILog
    {
        private readonly object _lock = new object();
        private readonly Func<DateTime> _clock;

        public FileLog(string directory, int retentionDays = 30, Func<DateTime>? clock = null)
        {
            Directory = directory ?? throw new ArgumentNullException(nameof(directory));
            RetentionDays = retentionDays < 1 ? 1 : retentionDays;
            _clock = clock ?? (() => DateTime.Now);
            TryCleanup();
        }

        public string Directory { get; }

        public int RetentionDays { get; }

        public string CurrentFile => Path.Combine(Directory, $"vktools-{_clock():yyyyMMdd}.log");

        public void Info(string message) => Write("INFO ", message, null);

        public void Warn(string message) => Write("WARN ", message, null);

        public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

        private void Write(string level, string message, Exception? exception)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(_clock().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
                  .Append(' ').Append(level).Append(' ').Append(message);
                if (exception != null) sb.AppendLine().Append(exception);
                sb.AppendLine();
                lock (_lock)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    File.AppendAllText(CurrentFile, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Лог недоступен — работаем без него.
            }
        }

        private void TryCleanup()
        {
            try
            {
                if (!System.IO.Directory.Exists(Directory)) return;
                DateTime border = _clock().Date.AddDays(-RetentionDays);
                foreach (string file in System.IO.Directory.GetFiles(Directory, "vktools-*.log"))
                {
                    string stamp = Path.GetFileNameWithoutExtension(file).Substring("vktools-".Length);
                    if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day)
                        && day < border)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Не смогли удалить старый лог — не страшно.
            }
        }

        /// <summary>Последние строки текущего лога (для окна «О плагине»).</summary>
        public string[] Tail(int lines)
        {
            try
            {
                lock (_lock)
                {
                    if (!File.Exists(CurrentFile)) return Array.Empty<string>();
                    return File.ReadAllLines(CurrentFile, Encoding.UTF8).Reverse().Take(lines).Reverse().ToArray();
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Array.Empty<string>();
            }
        }
    }
}
