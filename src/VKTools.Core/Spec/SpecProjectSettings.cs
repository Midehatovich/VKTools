using System;
using System.Collections.Generic;
using System.Globalization;

namespace VKTools.Core.Spec
{
    /// <summary>
    /// Настройки «Параметров спецификации», сохранённые в модели (REQ-CFG-005): словарь «ключ → текст».
    /// Раздел, которого нет, означает «по умолчанию» (правила отдела или встроенные). Новые разделы
    /// (комплекты в 0.6.0) добавляются новыми ключами без смены схемы хранилища.
    /// </summary>
    public sealed class SpecProjectSettings
    {
        public const string NestedSection = "nested";
        public const string GroupingSection = "grouping";

        private const string NestedParamsKey = "nested.params";
        private const string NestedClearKey = "nested.clearEmpty";
        private const string GroupingTableKey = "grouping.table";

        public SpecProjectSettings()
        {
        }

        public SpecProjectSettings(IDictionary<string, string> values)
        {
            foreach (KeyValuePair<string, string> pair in values) Values[pair.Key] = pair.Value;
        }

        public Dictionary<string, string> Values { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Выбор параметров переноса (CSV «Параметр;Ключ»); null — по умолчанию (nested_params.csv).</summary>
        public string? NestedParams
        {
            get => Get(NestedParamsKey);
            set => Set(NestedParamsKey, value);
        }

        /// <summary>Пустое у родителя очищает вложенное (по умолчанию — не трогает).</summary>
        public bool NestedClearEmpty
        {
            get => Get(NestedClearKey) == "1";
            set => Set(NestedClearKey, value ? "1" : null);
        }

        /// <summary>Таблица групп в формате grouping.csv; null — по умолчанию (grouping.csv).</summary>
        public string? GroupingTable
        {
            get => Get(GroupingTableKey);
            set => Set(GroupingTableKey, value);
        }

        /// <summary>Кто и когда сохранил раздел — в окно и отчёт.</summary>
        public string? Stamp(string section)
        {
            string? by = Get(section + ".savedBy");
            string? at = Get(section + ".savedAt");
            if (by == null && at == null) return null;
            return $"сохранил {by ?? "?"} {at ?? string.Empty}".Trim();
        }

        public void SetStamp(string section, string user, DateTime when)
        {
            Set(section + ".savedBy", user);
            Set(section + ".savedAt", when.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
        }

        public void ClearStamp(string section)
        {
            Set(section + ".savedBy", null);
            Set(section + ".savedAt", null);
        }

        private string? Get(string key) => Values.TryGetValue(key, out string? value) ? value : null;

        private void Set(string key, string? value)
        {
            if (value == null) Values.Remove(key);
            else Values[key] = value;
        }
    }
}
