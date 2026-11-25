using System;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace VKTools.Core.Settings
{
    /// <summary>
    /// Настройки пользователя (%AppData%\VKTools\settings.xml).
    /// Повреждённый файл не мешает работе: берутся значения по умолчанию, причина — в <see cref="LoadError"/>.
    /// </summary>
    public sealed class UserSettings
    {
        /// <summary>Папка правил отдела; пусто — только встроенные правила.</summary>
        public string DepartmentRulesFolder { get; set; } = string.Empty;

        public int LogRetentionDays { get; set; } = 30;

        /// <summary>
        /// Есть занятые коллегами или устаревшие элементы: false (по умолчанию) — не записывать ничего, чтобы спецификация
        /// не разъехалась (позиции и группы частично старые); true — записать остальное, занятые — в отчёте.
        /// </summary>
        public bool PartialWriteWhenBusy { get; set; }

        public string? LoadError { get; private set; }

        public static UserSettings Load(string path)
        {
            var settings = new UserSettings();
            try
            {
                if (!File.Exists(path)) return settings;
                XDocument doc = XDocument.Load(path);
                XElement? root = doc.Root;
                if (root == null) return settings;
                settings.DepartmentRulesFolder = (string?)root.Element("DepartmentRulesFolder") ?? string.Empty;
                if (int.TryParse((string?)root.Element("LogRetentionDays"), out int days) && days > 0)
                    settings.LogRetentionDays = days;
                settings.PartialWriteWhenBusy = string.Equals((string?)root.Element("PartialWriteWhenBusy"), "true", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is XmlException)
            {
                settings.LoadError = $"Настройки не прочитаны ({ex.Message}), взяты значения по умолчанию.";
            }
            return settings;
        }

        public void Save(string path)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var doc = new XDocument(
                new XElement("VKToolsSettings",
                    new XAttribute("version", 1),
                    new XElement("DepartmentRulesFolder", DepartmentRulesFolder ?? string.Empty),
                    new XElement("LogRetentionDays", LogRetentionDays),
                    new XElement("PartialWriteWhenBusy", PartialWriteWhenBusy ? "true" : "false")));
            string tmp = path + ".tmp";
            doc.Save(tmp);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
