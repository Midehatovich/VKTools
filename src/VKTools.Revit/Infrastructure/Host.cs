using System;
using System.IO;
using VKTools.Core.Config;
using VKTools.Core.Logging;
using VKTools.Core.Settings;

namespace VKTools.Revit.Infrastructure
{
    /// <summary>Общие службы надстройки: пути, лог, настройки пользователя, загрузчик правил.</summary>
    internal static class Host
    {
        private static ILog _log = NullLog.Instance;

        public static ILog Log => _log;

        /// <summary>Папка DLL; рядом лежит config\ со встроенными правилами.</summary>
        public static string AssemblyDirectory { get; } =
            Path.GetDirectoryName(typeof(Host).Assembly.Location) ?? AppDomain.CurrentDomain.BaseDirectory;

        public static string BuiltInConfigDirectory => Path.Combine(AssemblyDirectory, "config");

        public static string SettingsPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VKTools", "settings.xml");

        public static string LogDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VKTools", "logs");

        public static void Initialize()
        {
            UserSettings settings = UserSettings.Load(SettingsPath);
            _log = new FileLog(LogDirectory, settings.LogRetentionDays);
            if (settings.LoadError != null) _log.Warn(settings.LoadError);
        }

        /// <summary>Настройки читаются при каждой команде: изменения в окне настроек действуют сразу.</summary>
        public static UserSettings LoadSettings() => UserSettings.Load(SettingsPath);

        public static ConfigLoader CreateConfigLoader(UserSettings settings) =>
            new ConfigLoader(new ConfigLocations(BuiltInConfigDirectory, settings.DepartmentRulesFolder));
    }
}
