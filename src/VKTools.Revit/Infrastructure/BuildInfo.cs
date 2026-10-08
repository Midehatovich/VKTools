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

        public static string PluginVersion { get; } =
            typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString()
            ?? "?";
    }
}
