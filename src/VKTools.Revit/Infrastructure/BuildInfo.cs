using System.Linq;
using System.Reflection;

namespace VKTools.Revit.Infrastructure
{
    internal static class BuildInfo
    {
        /// <summary>Версия Revit, под которую собрана эта DLL (из свойства сборки RevitVersion).</summary>
        public static string RevitVersion { get; } =
            typeof(BuildInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "RevitVersion")?.Value ?? "?";

        /// <summary>«0.1.1+7b65862»: версия и короткий хеш коммита, из которого собрано (полный хеш в окне не нужен).</summary>
        public static string PluginVersion { get; } = ShortVersion(
            typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString()
            ?? "?");

        private static string ShortVersion(string version)
        {
            int plus = version.IndexOf('+');
            return plus >= 0 && version.Length > plus + 8 ? version.Substring(0, plus + 8) : version;
        }
    }
}
