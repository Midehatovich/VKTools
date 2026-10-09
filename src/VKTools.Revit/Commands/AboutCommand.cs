using System;
using System.Diagnostics;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Commands
{
    [Transaction(TransactionMode.ReadOnly)]
    public sealed class AboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var dialog = new TaskDialog("VK Tools")
            {
                MainInstruction = $"VK Tools {BuildInfo.PluginVersion}",
                MainContent =
                    $"Сборка для Revit {BuildInfo.RevitVersion}, запущено в Revit {commandData.Application.Application.VersionNumber}.\n\n" +
                    $"Надстройка: {Host.AssemblyDirectory}\n" +
                    $"Логи: {Host.LogDirectory}\n" +
                    $"Настройки: {Host.SettingsPath}",
                CommonButtons = TaskDialogCommonButtons.Close,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Открыть папку логов", "Пришлите свежий лог, если что-то пошло не так");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Открыть встроенные правила", "CSV-файлы, которые использует плагин");

            switch (dialog.Show())
            {
                case TaskDialogResult.CommandLink1:
                    OpenFolder(Host.LogDirectory);
                    break;
                case TaskDialogResult.CommandLink2:
                    OpenFolder(Host.BuiltInConfigDirectory);
                    break;
            }
            return Result.Succeeded;
        }

        private static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }))
                {
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)
            {
                Host.Log.Error("Не удалось открыть папку " + path, ex);
                TaskDialog.Show("VK Tools", "Не удалось открыть папку:\n" + path);
            }
        }
    }
}
