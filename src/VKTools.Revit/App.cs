using System;
using Autodesk.Revit.UI;
using VKTools.Revit.Commands;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.UI;

namespace VKTools.Revit
{
    /// <summary>
    /// Точка входа: вкладка «VK Tools». Апдейтеров (IUpdater) нет: плагин ничего не делает сам,
    /// только по кнопке (REQ-SAFE-008).
    /// </summary>
    public sealed class App : IExternalApplication
    {
        public const string TabName = "VK Tools";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                Host.Initialize();
                Host.Log.Info($"VK Tools {BuildInfo.PluginVersion} (сборка для Revit {BuildInfo.RevitVersion}) запущен в Revit {application.ControlledApplication.VersionNumber}.");
                CreateRibbon(application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Host.Log.Error("Не удалось создать вкладку VK Tools.", ex);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            MergeWindowHost.CloseAll();
            ReportWindowHost.CloseAll();
            Host.Log.Info("VK Tools выгружен.");
            return Result.Succeeded;
        }

        private static void CreateRibbon(UIControlledApplication application)
        {
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Вкладка уже есть (вторая копия надстройки) — добавим панели в неё
            }

            string assembly = typeof(App).Assembly.Location;

            RibbonPanel check = application.CreateRibbonPanel(TabName, "Проверка");
            var checkModel = new PushButtonData("VKTools.CheckModel", "Проверить\nмодель", assembly, typeof(CheckModelCommand).FullName)
            {
                ToolTip = "Проверка модели перед выпуском. Ничего не изменяет.",
                LongDescription =
                    "Параметры ФОП и их категории, апдейтеры других надстроек (BIM2B), пустые ADSK_Наименование, " +
                    "изоляция с другими этажом и системой, чем у трубы, разные написания одного значения (B1 латиницей и В1).",
                AvailabilityClassName = typeof(ProjectDocumentAvailability).FullName,
                LargeImage = RibbonIcons.Check(32),
                Image = RibbonIcons.Check(16),
            };
            check.AddItem(checkModel);

            RibbonPanel systems = application.CreateRibbonPanel(TabName, "Системы");
            var merge = new PushButtonData("VKTools.MergeSystems", "Объединить\nсистемы", assembly, typeof(MergeSystemsCommand).FullName)
            {
                ToolTip = "Копии систем трубопроводов («В1 1», «В1.2 3») — в основные системы. Сначала окно «было → станет» и пробный прогон.",
                LongDescription =
                    "Копия — проектное имя + один пробел + номер, тот же тип системы и классификация. Копии без приборов, " +
                    "занятые, в группах и связанные с другими системами не объединяются. Запись — одна операция в журнале отмены.",
                AvailabilityClassName = typeof(ProjectDocumentAvailability).FullName,
                LargeImage = RibbonIcons.Merge(32),
                Image = RibbonIcons.Merge(16),
            };
            var gaps = new PushButtonData("VKTools.RiserGaps", "Разрывы\nстояков", assembly, typeof(FindRiserGapsCommand).FullName)
            {
                ToolTip = "Несоединённые куски стояков: свободные концы одного типа системы на одной вертикали. Ничего не изменяет.",
                LongDescription =
                    "Типичная причина — этаж скопирован, а стояк не соединён с нижним, и Revit держит куски как разные системы («В1 6», «В1 8»). " +
                    "Отчёт с «Показать в модели»: выделяются оба конца разрыва.",
                AvailabilityClassName = typeof(ProjectDocumentAvailability).FullName,
                LargeImage = RibbonIcons.RiserGap(32),
                Image = RibbonIcons.RiserGap(16),
            };
            systems.AddItem(merge);
            systems.AddItem(gaps);

            RibbonPanel service = application.CreateRibbonPanel(TabName, "Сервис");
            var settings = new PushButtonData("VKTools.Settings", "Настройки", assembly, typeof(SettingsCommand).FullName)
            {
                ToolTip = "Папка правил отдела и срок хранения логов.",
                AvailabilityClassName = typeof(AlwaysAvailable).FullName,
                LargeImage = RibbonIcons.Settings(32),
                Image = RibbonIcons.Settings(16),
            };
            var about = new PushButtonData("VKTools.About", "О программе", assembly, typeof(AboutCommand).FullName)
            {
                ToolTip = "Версия, папки логов и правил.",
                AvailabilityClassName = typeof(AlwaysAvailable).FullName,
                LargeImage = RibbonIcons.About(32),
                Image = RibbonIcons.About(16),
            };
            service.AddItem(settings);
            service.AddItem(about);
        }
    }
}
