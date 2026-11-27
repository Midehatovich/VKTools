using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VKTools.Revit.Schedules;

namespace VKTools.Revit.UI
{
    /// <summary>
    /// «Подготовить и собрать» (0.18.0): какие шаги выполнять. Выбор хранится в модели (профиль шагов): на ПТ «Пушкарева» имена,
    /// запас и изоляцию правили вручную — там эти шаги снимаются один раз, и случайный запуск их больше не перетрёт.
    /// </summary>
    internal sealed class PrepareWindow : Window
    {
        private readonly Dictionary<ChainStep, CheckBox> _boxes = new Dictionary<ChainStep, CheckBox>();
        private readonly CheckBox _update;
        private readonly CheckBox _check;

        public PrepareWindow(string documentTitle, ISet<ChainStep> skipped, int ownSchedules, string? savedStamp)
        {
            this.ApplyDefaultLook();
            Title = "VK Tools — подготовить и собрать: " + documentTitle;
            Width = 760;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            var root = new StackPanel { Margin = new Thickness(12) };
            Content = root;
            root.Children.Add(new TextBlock
            {
                Text = "Шаги выпуска по порядку, с настройками, сохранёнными в модели (или по умолчанию). Всё вместе — одна операция: "
                    + "в конце отчёт по шагам и выбор «Записать» (одна отмена Ctrl+Z) или «Откатить».",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
            root.Children.Add(new TextBlock
            {
                Text = "Снятая галка = «не трогать»: шаг не запускается, значения в модели остаются как есть (например, поправленные вручную). "
                    + "Выбор сохраняется в модели" + (savedStamp != null ? $" ({savedStamp})" : string.Empty) + ".",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 8),
            });
            int n = 1;
            foreach (ChainStep step in ScheduleChain.PrepareOrder)
            {
                var box = new CheckBox
                {
                    Content = new TextBlock { Text = $"{n++}. {ScheduleChain.Title(step)}{Hint(step)}", TextWrapping = TextWrapping.Wrap },
                    IsChecked = !skipped.Contains(step),
                    Margin = new Thickness(0, 2, 0, 2),
                };
                _boxes[step] = box;
                root.Children.Add(box);
            }
            _update = new CheckBox
            {
                Content = $"Обновить фильтры спецификаций, созданных кнопкой «Создать спецификацию» ({ownSchedules})",
                IsChecked = ownSchedules > 0,
                IsEnabled = ownSchedules > 0,
                Margin = new Thickness(0, 8, 0, 2),
            };
            root.Children.Add(_update);
            _check = new CheckBox { Content = "После записи — «Проверить спецификацию» по всем оформленным (только чтение)", IsChecked = true, Margin = new Thickness(0, 2, 0, 2) };
            root.Children.Add(_check);
            root.Children.Add(new TextBlock
            {
                Text = "Подводные камни: прогон по всей модели идёт минуты; элементы, занятые коллегами, не пишутся (как в каждой кнопке). "
                    + "Ошибка на шаге — выбор: записать то, что уже прошло, или откатить всё; потом можно запустить снова без прошедших шагов.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 8, 0, 0),
            });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var run = new Button { Content = "Запустить", MinWidth = 100, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            run.Click += (s, e) =>
            {
                if (!Steps.Any())
                {
                    MessageBox.Show(this, "Не отмечено ни одного шага.", "VK Tools", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                DialogResult = true;
            };
            var cancel = new Button { Content = "Отмена", MinWidth = 100, IsCancel = true };
            buttons.Children.Add(run);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
        }

        public IEnumerable<ChainStep> Steps => ScheduleChain.PrepareOrder.Where(s => _boxes[s].IsChecked == true);

        public ISet<ChainStep> Skipped => new HashSet<ChainStep>(ScheduleChain.PrepareOrder.Where(s => _boxes[s].IsChecked != true));

        public bool UpdateSchedules => _update.IsChecked == true;

        public bool CheckAfter => _check.IsChecked == true;

        private static string Hint(ChainStep step) => step switch
        {
            ChainStep.SystemFloor => " — система, этаж, копии ADSK_ → Р_ (свой набор); свои параметры добавятся, если их нет",
            ChainStep.Insulation => " — наименование и параметры изоляции по таблице окна «Изоляция»",
            ChainStep.PipeNames => " — пишет, только если запись включена в окне «Имена труб»",
            ChainStep.Vendor => " — марка, код, завод по таблице окна «Марки и поставщики»",
            ChainStep.PipeReserve => " — количество труб и изоляции с запасом",
            ChainStep.Calc => " — по спецификациям, отмеченным в окне «Расчётные позиции»",
            ChainStep.Positions => " — номера с 1 в каждой оформленной спецификации (таблица окна «Позиции»)",
            _ => string.Empty,
        };
    }
}
