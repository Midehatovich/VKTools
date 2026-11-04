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
                foreach (string hang in HangMarker.CollectAbandoned())
                {
                    Host.Log.Warn($"Прошлый сеанс Revit закрыли, пока VK Tools ждал Revit: {hang}. Перед повторным запуском этой копии плагин предупредит.");
                }
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
            SpecParamsHost.CloseAll();
            CalcHost.CloseAll();
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

            RibbonPanel spec = application.CreateRibbonPanel(TabName, "Спецификация");
            var nested = new PushButtonData("VKTools.NestedTransfer", "Перенос во\nвложенные", assembly, typeof(NestedTransferCommand).FullName)
            {
                ToolTip = "Значения верхнего родителя → его общие вложенные семейства. Какие параметры переносить — выбор в окне, сохраняется в модели.",
                LongDescription =
                    "По умолчанию ADSK_Этаж, LSR_Имя системы, LSR_Тип системы (nested_params.csv); можно отметить любые общие параметры и параметры проекта, " +
                    "которые есть и у родителя, и у вложенного. Тип данных должен совпадать; пустое у родителя не переносится. Область: вся модель, активный вид или выделенные. " +
                    "Сначала окно «было → станет» и пробный прогон, запись — одна операция в журнале отмены.",
                AvailabilityClassName = typeof(ProjectDocumentAvailability).FullName,
                LargeImage = RibbonIcons.NestedTransfer(32),
                Image = RibbonIcons.NestedTransfer(16),
            };
            var grouping = new PushButtonData("VKTools.Grouping", "Группирование", assembly, typeof(GroupingCommand).FullName)
            {
                ToolTip = "ADSK_Группирование по таблице групп проекта: категории, семейства, «не трогать». Комплекты пожарных шкафов — «группа.ключ».",
                LongDescription =
                    "Пока в модели нет своей таблицы, работает grouping.csv (0 спринклеры … 8 прочее). Строка семейства действует и на его вложенные; " +
                    "«не трогать» оставляет значения, проставленные вручную. Служебные позиции _SYS_ не трогаются. " +
                    "Пишутся только изменившиеся значения; запись — одна операция в журнале отмены.",
                AvailabilityClassName = typeof(ProjectDocumentAvailability).FullName,
                LargeImage = RibbonIcons.SpecParams(32),
                Image = RibbonIcons.SpecParams(16),
            };
            var calc = new PushButtonData("VKTools.CalcItems", "Расчётные\nпозиции", assembly, typeof(CalcItemsCommand).FullName)
            {
                ToolTip = "Грунтовка, краска, металл для креплений и бентонитовый шнур в гильзы по каждой системе (группа 9.x); запас 20 % на хомуты по строке спецификации.",
                LongDescription =
                    "Площадь труб π·Dнар·L, число креплений по шагу и шнур по длине гильз на трубах — по таблицам проекта (в окне, хранятся в модели). " +
                    "Позиции — экземпляры служебного семейства _SYS_SpecCalcItem; его нет — плагин создаст его сам. «SYS_Кол-во вручную» перекрывает расчёт. " +
                    "Пробный прогон проверяет, что позиции попадают в свои спецификации; запись — одна операция в журнале отмены.",
                AvailabilityClassName = typeof(ProjectDocumentAvailability).FullName,
                LargeImage = RibbonIcons.Calc(32),
                Image = RibbonIcons.Calc(16),
            };
            var systemFloor = new PushButtonData("VKTools.SystemFloor", "Система\nи этаж", assembly, typeof(SystemFloorCommand).FullName)
            {
                ToolTip = "Параметр системы := «Сокращение для системы», параметр этажа := имя уровня; изоляция — значения своей трубы; затем перенос во вложенные.",
                LongDescription =
                    "По умолчанию LSR_Имя системы и ADSK_Этаж (параметры выбираются в окне и хранятся в модели). Пустое сокращение у трубы — из типа системы, " +
                    "у фитинга и арматуры — у соседей (до 3 шагов); элемент в нескольких системах или не подключённый не меняется. " +
                    "Имя уровня можно заменить своим значением в таблице этажей. Сначала окно «было → станет» и пробный прогон, запись — одна операция.",
                AvailabilityClassName = typeof(ProjectDocumentAvailability).FullName,
                LargeImage = RibbonIcons.SystemFloor(32),
                Image = RibbonIcons.SystemFloor(16),
            };
            var insulation = new PushButtonData("VKTools.Insulation", "Изоляция", assembly, typeof(InsulationCommand).FullName)
            {
                ToolTip = "Изоляция труб: Dнар трубы-основы, наименование «… толщиной t мм для трубы D = d мм», марка и завод — по таблице «Изделия» проекта.",
                LongDescription =
                    "Таблица «Изделия» в окне хранится в модели: маска типа, основа и формат имени, ряды толщин и цилиндров, любые параметры изделия (+ Колонка). " +
                    "Наименование пишется, только если включено в окне (тогда формулу имени изоляции в BIM2B нужно убрать). Запись — одна операция в журнале отмены.",
                AvailabilityClassName = typeof(ProjectDocumentAvailability).FullName,
                LargeImage = RibbonIcons.Insulation(32),
                Image = RibbonIcons.Insulation(16),
            };
            var reserve = new PushButtonData("VKTools.PipeReserve", "Запас\nтруб", assembly, typeof(PipeReserveCommand).FullName)
            {
                ToolTip = "Количество = длина × k жёстким трубам и изоляции на трубах (по умолчанию 1,1 = +10 %, ТЗ главного 2026-10-20).",
                LongDescription =
                    "Коэффициенты — таблица в окне, хранится в модели: категория, маска типа, системы, k (пусто — не трогать). Гибкие трубы и изоляция фитингов не трогаются. " +
                    "Единицы количества проверяются по типу данных и по значениям в модели; не сходятся — запись запрещена. Запись — одна операция в журнале отмены.",
                AvailabilityClassName = typeof(ProjectDocumentAvailability).FullName,
                LargeImage = RibbonIcons.PipeReserve(32),
                Image = RibbonIcons.PipeReserve(16),
            };
            var pipeNames = new PushButtonData("VKTools.PipeNames", "Имена\nтруб", assembly, typeof(PipeNamesCommand).FullName)
            {
                ToolTip = "ADSK_Наименование труб по правилу типа: ВГП ГОСТ 3262 и чугун — по Ду, остальные — по Dнар. По умолчанию только проверка.",
                LongDescription =
                    "Правила — таблица в окне, хранится в модели: маска типа, основа («Комментарии к типоразмеру»), формат ({base} ø{dn}x{s}…). " +
                    "Пока имена труб пишет BIM2B, плагин только показывает расхождения и «двойные строки»; запись включается галкой на проект.",
                AvailabilityClassName = typeof(ProjectDocumentAvailability).FullName,
                LargeImage = RibbonIcons.PipeNames(32),
                Image = RibbonIcons.PipeNames(16),
            };
            spec.AddItem(systemFloor);
            spec.AddItem(insulation);
            spec.AddItem(nested);
            spec.AddItem(grouping);
            spec.AddItem(calc);
            spec.AddItem(reserve);
            spec.AddItem(pipeNames);

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
