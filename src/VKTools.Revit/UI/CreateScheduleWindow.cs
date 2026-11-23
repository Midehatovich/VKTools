using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VKTools.Core.Config;
using VKTools.Core.Schedules;
using VKTools.Revit.Schedules;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace VKTools.Revit.UI
{
    internal enum ScheduleAction
    {
        None,
        Create,
        Update,
    }

    /// <summary>
    /// Окно «Создать спецификацию»: системы и этажи галками или быстрым выбором, автоимя, оформление (форма ГОСТ или эталон),
    /// заголовки по системам/этажам. Фильтры плагин подбирает сам и показывает их до создания.
    /// </summary>
    internal sealed class CreateScheduleWindow : Window
    {
        private const string FormItem = "Встроенная форма ГОСТ 21.110-2013, форма 1";
        private readonly ScheduleModelData _data;
        private readonly Dictionary<string, CheckBox> _systemBoxes = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        private readonly Dictionary<string, CheckBox> _floorBoxes = new Dictionary<string, CheckBox>(StringComparer.Ordinal);
        private readonly Dictionary<string, RadioButton> _systemPresets = new Dictionary<string, RadioButton>(StringComparer.Ordinal);
        private readonly Dictionary<string, RadioButton> _floorPresets = new Dictionary<string, RadioButton>(StringComparer.Ordinal);
        private readonly ComboBox _firstFloor;
        private readonly TextBox _name;
        private readonly CheckBox _autoName;
        private readonly ComboBox _template;
        private readonly CheckBox _bySystem;
        private readonly CheckBox _byFloor;
        private readonly CheckBox _keepSort;
        private readonly CheckBox _titleHeader;
        private readonly TextBlock _preview;
        private readonly TextBlock _problem;
        private readonly Button _create;
        private readonly Dictionary<long, CheckBox> _ownBoxes = new Dictionary<long, CheckBox>();
        private readonly Dictionary<ChainStep, CheckBox> _stepBoxes = new Dictionary<ChainStep, CheckBox>();
        private readonly Button _update;
        private bool _sync;
        private string _systemPreset = ScheduleRequest.PresetAll;
        private string _floorPreset = ScheduleRequest.PresetAll;

        public CreateScheduleWindow(string documentTitle, ScheduleModelData data)
        {
            _data = data;
            this.ApplyDefaultLook();
            Title = "VK Tools — создать спецификацию: " + documentTitle;
            Width = 980;
            Height = 820;
            MinWidth = 760;
            MinHeight = 560;

            var root = new DockPanel { Margin = new Thickness(10) };
            Content = root;

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(header, Dock.Top);
            header.Children.Add(new TextBlock
            {
                Text = "Отметьте системы и этажи — плагин создаст спецификацию и сам поставит фильтры. "
                    + $"Значения — из «{data.SystemParam.Name}» и «{data.FloorParam.Name}» элементов ВК (их заполняет «Система и этаж»: запустите её до создания).",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
            });
            if (data.WithoutSystem > 0 || data.WithoutFloor > 0)
            {
                header.Children.Add(new TextBlock
                {
                    Text = $"Без системы: {data.WithoutSystem} эл., без этажа: {data.WithoutFloor} эл. — в спецификации по выбору они не попадут. Сначала «Система и этаж».",
                    Foreground = Brushes.DarkRed,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }
            if (data.Issues.Count > 0)
            {
                header.Children.Add(new TextBlock
                {
                    Text = "Правила: " + string.Join("; ", data.Issues.Select(i => i.ToString())),
                    Foreground = Brushes.DarkOrange,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                });
            }
            root.Children.Add(header);

            // Низ: превью, ошибки, кнопки
            var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            _preview = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            bottom.Children.Add(_preview);
            _problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkRed, Margin = new Thickness(0, 0, 0, 4) };
            bottom.Children.Add(_problem);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            _create = ReportWindow.MakeButton("Создать", (s, e) => Finish(ScheduleAction.Create));
            _create.FontWeight = FontWeights.SemiBold;
            _create.ToolTip = "Создать спецификацию одной операцией (Ctrl+Z отменит) и открыть её.";
            Button close = ReportWindow.MakeButton("Закрыть", (s, e) => Close());
            buttons.Children.Add(_create);
            buttons.Children.Add(close);
            bottom.Children.Add(buttons);
            root.Children.Add(bottom);

            var body = new StackPanel();
            var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            root.Children.Add(scroll);

            // Системы и этажи — рядом
            var pick = new Grid();
            pick.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pick.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            pick.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.Children.Add(pick);

            var systems = new StackPanel();
            var systemsBox = new GroupBox { Header = "Системы", Content = systems, Padding = new Thickness(6) };
            pick.Children.Add(systemsBox);
            var systemPresets = new WrapPanel();
            AddPreset(systemPresets, _systemPresets, "systems", ScheduleRequest.PresetAll, "Все системы", OnSystemPreset);
            foreach (SystemGroup g in data.Groups)
            {
                AddPreset(systemPresets, _systemPresets, "systems", ScheduleRequest.GroupPrefix + g.Name, "Системы: " + g.Name.ToLower(CultureInfo.CurrentCulture), OnSystemPreset);
            }
            AddPreset(systemPresets, _systemPresets, "systems", ScheduleRequest.PresetManual, "Выбрать вручную", OnSystemPreset);
            systems.Children.Add(systemPresets);
            systems.Children.Add(BoxList(data.Systems.Select(s => (s, s, data.SystemCounts[s])), _systemBoxes, OnSystemBox,
                data.Systems.Count == 0 ? $"В модели нет элементов ВК с заполненным «{data.SystemParam.Name}»." : null));

            var floors = new StackPanel();
            var floorsBox = new GroupBox { Header = "Этажи", Content = floors, Padding = new Thickness(6) };
            Grid.SetColumn(floorsBox, 2);
            pick.Children.Add(floorsBox);
            var floorPresets = new WrapPanel();
            AddPreset(floorPresets, _floorPresets, "floors", ScheduleRequest.PresetAll, "Все этажи", OnFloorPreset);
            AddPreset(floorPresets, _floorPresets, "floors", ScheduleRequest.PresetBelow, "Ниже 1 этажа", OnFloorPreset);
            AddPreset(floorPresets, _floorPresets, "floors", ScheduleRequest.PresetAbove, "1 этаж и выше", OnFloorPreset);
            AddPreset(floorPresets, _floorPresets, "floors", ScheduleRequest.PresetManual, "Выбрать вручную", OnFloorPreset);
            floors.Children.Add(floorPresets);
            var firstRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            firstRow.Children.Add(new TextBlock { Text = "Первый этаж: ", VerticalAlignment = VerticalAlignment.Center });
            _firstFloor = new ComboBox { MinWidth = 180, ItemsSource = data.KnownFloorValues };
            _firstFloor.ToolTip = "От него считаются «Ниже 1 этажа» и «1 этаж и выше»: по отметке уровня, а без уровня — по номеру в имени.";
            _firstFloor.SelectedItem = FloorNames.GuessFirst(data.Floors);
            _firstFloor.SelectionChanged += (s, e) => OnFloorPreset(_floorPreset);
            firstRow.Children.Add(_firstFloor);
            floors.Children.Add(firstRow);
            floors.Children.Add(BoxList(data.Floors.Select(f => (f.Value, FloorLabel(f), f.Count)), _floorBoxes, OnFloorBox,
                data.Floors.Count == 0 ? $"«{data.FloorParam.Name}» не заполнен — спецификация будет по всем этажам." : null));

            // Название
            var nameBox = new StackPanel();
            body.Children.Add(new GroupBox { Header = "Название", Content = nameBox, Padding = new Thickness(6), Margin = new Thickness(0, 8, 0, 0) });
            _name = new TextBox { Margin = new Thickness(0, 0, 0, 4) };
            _name.TextChanged += (s, e) =>
            {
                if (_sync) return;
                _autoName!.IsChecked = false;
                Recompute();
            };
            nameBox.Children.Add(_name);
            _autoName = new CheckBox { Content = "Автоматически по выбору («Спецификация систем В1, Т3 и Т4 в уровнях 1, 2 и 3 этажа»)", IsChecked = true };
            _autoName.Checked += (s, e) => Recompute();
            nameBox.Children.Add(_autoName);

            // Оформление и сортировка
            var look = new StackPanel();
            body.Children.Add(new GroupBox { Header = "Оформление и сортировка", Content = look, Padding = new Thickness(6), Margin = new Thickness(0, 8, 0, 0) });
            var templateRow = new StackPanel { Orientation = Orientation.Horizontal };
            templateRow.Children.Add(new TextBlock { Text = "Взять оформление: ", VerticalAlignment = VerticalAlignment.Center });
            var items = new List<object> { FormItem };
            items.AddRange(data.Templates.Where(t => t.Multi));
            _template = new ComboBox { MinWidth = 420, ItemsSource = items, SelectedIndex = 0 };
            _template.ToolTip = "Форма ГОСТ — графы, ширины и сортировка из schedule_form.csv. Спецификация модели — её копия со всем оформлением; "
                + "плагин меняет только фильтры по системе и этажу и название.";
            _template.SelectionChanged += (s, e) =>
            {
                _keepSort!.IsEnabled = !(_template.SelectedItem is string);
                _titleHeader!.IsEnabled = _template.SelectedItem is string;
                Recompute();
            };
            templateRow.Children.Add(_template);
            look.Children.Add(templateRow);
            _bySystem = new CheckBox { Content = "Заголовки по системам (строка «В1», под ней позиции системы)", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
            _byFloor = new CheckBox { Content = "Заголовки по этажам", Margin = new Thickness(0, 2, 0, 0) };
            _keepSort = new CheckBox { Content = "У копии эталона оставить его сортировку и группировку (галки выше не действуют)", IsChecked = true, IsEnabled = false, Margin = new Thickness(0, 2, 0, 0) };
            _titleHeader = new CheckBox
            {
                Content = "Шапка граф — в основной надписи листа («Девятиграфка_Шапка»): у формы ГОСТ свои заголовки граф скрыть",
                IsChecked = true,
                Margin = new Thickness(0, 2, 0, 0),
                ToolTip = "Галка «Девятиграфка_Шапка» в свойствах основной надписи ADSK рисует шапку на каждом листе. Если её не включаете — снимите, и шапка будет у спецификации.",
            };
            foreach (CheckBox c in new[] { _bySystem, _byFloor, _keepSort, _titleHeader })
            {
                c.Checked += (s, e) => Recompute();
                c.Unchecked += (s, e) => Recompute();
                look.Children.Add(c);
            }

            // Созданные кнопкой
            var own = new StackPanel();
            int stale = data.Own.Count(o => o.Stale);
            body.Children.Add(new Expander
            {
                Header = $"Созданные кнопкой спецификации: {data.Own.Count}" + (stale > 0 ? $", устарели фильтры: {stale}" : string.Empty),
                Content = own,
                IsExpanded = stale > 0,
                Margin = new Thickness(0, 8, 0, 0),
            });
            foreach (OwnSchedule o in data.Own)
            {
                var box = new CheckBox
                {
                    Content = new TextBlock { Text = $"{o.Name} — {o.State}", TextWrapping = TextWrapping.Wrap },
                    IsChecked = o.Stale,
                    Foreground = o.Stale ? Brushes.DarkRed : Brushes.Black,
                    Margin = new Thickness(0, 1, 0, 1),
                };
                box.Checked += (s, e) => UpdateOwnButton();
                box.Unchecked += (s, e) => UpdateOwnButton();
                _ownBoxes[o.Id] = box;
                own.Children.Add(box);
            }
            own.Children.Add(new TextBlock
            {
                Text = "«Обновить» пересобирает фильтры по тому же выбору: группа «Канализация» подхватит новую систему, «1 этаж и выше» — новый этаж. "
                    + "Ручной выбор систем новые значения не добавляет. Название и оформление не меняются.",
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4),
            });
            _update = ReportWindow.MakeButton("Обновить отмеченные", (s, e) => Finish(ScheduleAction.Update));
            _update.HorizontalAlignment = HorizontalAlignment.Left;
            own.Children.Add(_update);
            if (data.Own.Count == 0) own.Children.Clear();

            // Функции после создания — по порядку выпуска, с настройками, сохранёнными в модели
            var chain = new StackPanel();
            body.Children.Add(new GroupBox { Header = "После «Создать» применить по порядку (настройки — сохранённые в модели)", Content = chain, Padding = new Thickness(6), Margin = new Thickness(0, 8, 0, 0) });
            foreach (ChainStep step in ScheduleChain.Order)
            {
                var box = new CheckBox { Content = ScheduleChain.Title(step) + StepHint(step), IsChecked = true, Margin = new Thickness(0, 1, 0, 1) };
                _stepBoxes[step] = box;
                chain.Children.Add(box);
            }
            chain.Children.Add(new TextBlock
            {
                Text = "«Система и этаж» обновляется до этого окна. Всё вместе — одна операция: в конце отчёт и выбор «Записать» или «Откатить»; "
                    + "после записи всё отменяется одним Ctrl+Z. Правила: " + string.Join("; ", data.Sources),
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });

            _sync = true;
            _systemPresets[ScheduleRequest.PresetAll].IsChecked = true;
            _floorPresets[ScheduleRequest.PresetAll].IsChecked = true;
            _sync = false;
            OnSystemPreset(ScheduleRequest.PresetAll);
            OnFloorPreset(ScheduleRequest.PresetAll);
            UpdateOwnButton();
        }

        public ScheduleAction Action { get; private set; }

        public ScheduleRequest Request { get; private set; } = new ScheduleRequest();

        public List<long> UpdateIds { get; } = new List<long>();

        /// <summary>Функции, отмеченные к применению после создания, в порядке <see cref="ScheduleChain.Order"/>.</summary>
        public List<ChainStep> Steps { get; } = new List<ChainStep>();

        private static string StepHint(ChainStep step) => step switch
        {
            ChainStep.Calc => " — новая спецификация добавится к отмеченным в «Расчётных позициях»",
            ChainStep.PipeNames => " — пишет, только если запись включена в окне «Имена труб» (пока имена пишет BIM2B)",
            ChainStep.Vendor => " — по таблице окна «Марки и поставщики»; пустая таблица — ничего не пишет",
            _ => string.Empty,
        };

        private static string FloorLabel(FloorInfo f) =>
            f.Elevation.HasValue ? $"{f.Value}  ({f.Elevation.Value.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture)})" : f.Value + "  (уровня нет)";

        private static void AddPreset(Panel panel, Dictionary<string, RadioButton> map, string group, string key, string text, Action<string> onChecked)
        {
            var radio = new RadioButton { Content = text, GroupName = group, Margin = new Thickness(0, 0, 12, 4) };
            radio.Checked += (s, e) => onChecked(key);
            map[key] = radio;
            panel.Children.Add(radio);
        }

        private static ScrollViewer BoxList(IEnumerable<(string Key, string Label, int Count)> items, Dictionary<string, CheckBox> map, Action onChanged, string? empty)
        {
            var list = new StackPanel();
            foreach ((string key, string label, int count) in items)
            {
                var box = new CheckBox { Content = $"{label}  — {count.ToString(CultureInfo.CurrentCulture)} эл.", Margin = new Thickness(0, 1, 0, 1) };
                box.Checked += (s, e) => onChanged();
                box.Unchecked += (s, e) => onChanged();
                map[key] = box;
                list.Children.Add(box);
            }
            if (empty != null) list.Children.Add(new TextBlock { Text = empty, Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap });
            return new ScrollViewer { Content = list, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 0) };
        }

        private static List<string> Checked(Dictionary<string, CheckBox> map) => map.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();

        private void OnSystemPreset(string preset)
        {
            if (_sync) return;
            _systemPreset = preset;
            if (preset != ScheduleRequest.PresetManual)
            {
                var probe = new ScheduleRequest { SystemPreset = preset };
                var chosen = new HashSet<string>(probe.ResolveSystems(_data.Systems, _data.Groups), StringComparer.Ordinal);
                _sync = true;
                foreach (KeyValuePair<string, CheckBox> kv in _systemBoxes) kv.Value.IsChecked = chosen.Contains(kv.Key);
                _sync = false;
            }
            Recompute();
        }

        private void OnFloorPreset(string preset)
        {
            if (_sync) return;
            _floorPreset = preset;
            if (preset != ScheduleRequest.PresetManual)
            {
                var probe = new ScheduleRequest { FloorPreset = preset, FirstFloor = _firstFloor?.SelectedItem as string ?? string.Empty };
                var chosen = new HashSet<string>(probe.ResolveFloors(_data.Floors), StringComparer.Ordinal);
                _sync = true;
                foreach (KeyValuePair<string, CheckBox> kv in _floorBoxes) kv.Value.IsChecked = chosen.Contains(kv.Key);
                _sync = false;
            }
            Recompute();
        }

        private void OnSystemBox()
        {
            if (_sync) return;
            _systemPreset = ScheduleRequest.DetectSystemPreset(Checked(_systemBoxes), _data.Systems, _data.Groups);
            _sync = true;
            if (_systemPresets.TryGetValue(_systemPreset, out RadioButton? radio)) radio.IsChecked = true;
            _sync = false;
            Recompute();
        }

        private void OnFloorBox()
        {
            if (_sync) return;
            _floorPreset = ScheduleRequest.DetectFloorPreset(Checked(_floorBoxes), _data.Floors, _firstFloor.SelectedItem as string ?? string.Empty);
            _sync = true;
            if (_floorPresets.TryGetValue(_floorPreset, out RadioButton? radio)) radio.IsChecked = true;
            _sync = false;
            Recompute();
        }

        private ScheduleRequest BuildRequest()
        {
            TemplateInfo? template = _template?.SelectedItem as TemplateInfo;
            return new ScheduleRequest
            {
                SystemPreset = _systemPreset,
                Systems = Checked(_systemBoxes),
                FloorPreset = _floorPreset,
                FirstFloor = _firstFloor?.SelectedItem as string ?? string.Empty,
                Floors = Checked(_floorBoxes),
                Name = _name?.Text ?? string.Empty,
                Template = template?.Name ?? ScheduleRequest.FormTemplate,
                GroupBySystem = _bySystem?.IsChecked == true,
                GroupByFloor = _byFloor?.IsChecked == true,
                KeepTemplateSort = _keepSort?.IsChecked == true,
                HeaderInTitleBlock = _titleHeader?.IsChecked == true,
            };
        }

        private void Recompute()
        {
            if (_name == null || _preview == null || _create == null || _template == null) return;
            ScheduleRequest request = BuildRequest();
            TemplateInfo? template = _template.SelectedItem as TemplateInfo;
            ScheduleFilterSet set = _data.Plan(request, template?.Filters ?? 0, out List<string> systems, out List<string> floors);
            if (_autoName.IsChecked == true)
            {
                _sync = true;
                _name.Text = request.AutoName(systems, floors, _data.Groups);
                _sync = false;
                request.Name = _name.Text;
            }
            string? problem = set.Error;
            string name = ScheduleRequest.CleanName(request.Name);
            if (problem == null && _data.Systems.Count == 0) problem = $"в модели нет элементов ВК с заполненным «{_data.SystemParam.Name}»";
            if (problem == null && floors.Count == 0 && _data.Floors.Count > 0)
            {
                problem = _floorPreset == ScheduleRequest.PresetBelow || _floorPreset == ScheduleRequest.PresetAbove
                    ? "ни один этаж не подходит: выберите «Первый этаж»"
                    : "этажи не выбраны";
            }
            if (problem == null && name.Length == 0) problem = "задайте название";
            if (problem == null && _data.ViewNames.Contains(name)) problem = $"вид «{name}» уже есть в модели — измените название (созданные кнопкой обновляются ниже)";

            int count = _data.Count(systems, set.Systems.All, floors, set.Floors.All);
            var lines = new List<string>
            {
                $"Попадёт элементов ВК: {count.ToString(CultureInfo.CurrentCulture)} (без расчётных позиций). Название: «{name}».",
            };
            if (set.Error == null)
            {
                lines.Add($"Фильтры ({set.Count} из {ScheduleFilterSet.MaxFilters}" + (set.OtherFilters > 0 ? $", из них эталона {set.OtherFilters}" : string.Empty) + "): "
                    + string.Join("; ", set.Describe(_data.SystemParam.Name, _data.FloorParam.Name)) + ".");
            }
            _preview.Text = string.Join(Environment.NewLine, lines);
            _problem.Text = problem == null ? string.Empty : "Нельзя создать: " + problem + ".";
            _create.IsEnabled = problem == null;
            Request = request;
        }

        private void UpdateOwnButton()
        {
            if (_update != null) _update.IsEnabled = _ownBoxes.Values.Any(b => b.IsChecked == true);
        }

        private void Finish(ScheduleAction action)
        {
            if (action == ScheduleAction.Create)
            {
                Recompute();
                if (!_create.IsEnabled) return;
            }
            else
            {
                UpdateIds.Clear();
                UpdateIds.AddRange(_ownBoxes.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key));
                if (UpdateIds.Count == 0) return;
            }
            Steps.Clear();
            Steps.AddRange(ScheduleChain.Order.Where(st => _stepBoxes.TryGetValue(st, out CheckBox? b) && b.IsChecked == true));
            Action = action;
            DialogResult = true;
            Close();
        }
    }
}
