using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VKTools.Core.Spec;
using Binding = System.Windows.Data.Binding;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace VKTools.Revit.UI
{
    /// <summary>Строка таблицы этажей: уровень модели, сколько на нём элементов, своё значение этажа.</summary>
    internal sealed class FloorRow : INotifyPropertyChanged
    {
        private string _value;

        public FloorRow(string level, int count, bool inModel, string value)
        {
            Level = level;
            Count = inModel ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "нет в модели";
            _value = value;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Level { get; }

        public string Count { get; }

        /// <summary>Своё значение этажа; пусто — имя уровня как есть.</summary>
        public string Value
        {
            get => _value;
            set
            {
                string v = (value ?? string.Empty).Trim();
                if (v == _value) return;
                _value = v;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Result)));
            }
        }

        /// <summary>Что получит параметр этажа.</summary>
        public string Result => _value.Length > 0 ? _value : Level;
    }

    /// <summary>Параметр в выпадающем списке «Параметр системы» / «Параметр этажа».</summary>
    internal sealed class ParamOption
    {
        public ParamOption(ParamKey key, string text)
        {
            Key = key;
            Text = text;
        }

        public ParamKey Key { get; }

        public string Text { get; }

        public override string ToString() => Text;
    }

    /// <summary>Вкладка «Настройка» окна «Система и этаж»: параметры, изоляция, перенос во вложенные, категории, таблица этажей.</summary>
    internal sealed class SystemFloorPanel : DockPanel
    {
        private static readonly (string Name, string Label)[] KnownCategories =
        {
            ("OST_PipeCurves", "Трубы"),
            ("OST_FlexPipeCurves", "Гибкие трубы"),
            ("OST_PipeFitting", "Соединительные детали"),
            ("OST_PipeAccessory", "Арматура"),
            ("OST_MechanicalEquipment", "Оборудование"),
            ("OST_PlumbingFixtures", "Сантехнические приборы"),
            ("OST_Sprinklers", "Спринклеры"),
        };

        private const string NoMirror = "— не писать —";

        private readonly CheckBox _writeSystem;
        private readonly ComboBox _systemParam;
        private readonly ComboBox _mirrorParam;
        private readonly CheckBox _writeFloor;
        private readonly ComboBox _floorParam;
        private readonly CheckBox _insulation;
        private readonly CheckBox _writeRoom;
        private readonly CheckBox _chainNested;
        private readonly TextBlock _nestedNote;
        private readonly WrapPanel _categoryPanel;
        private readonly List<(string Name, CheckBox Box)> _categories = new List<(string, CheckBox)>();
        private readonly DataGrid _floors;
        private readonly ObservableCollection<FloorRow> _rows = new ObservableCollection<FloorRow>();
        private readonly List<ParamOption> _options = new List<ParamOption>();
        private bool _loading;

        public SystemFloorPanel()
        {
            var top = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            DockPanel.SetDock(top, Dock.Top);

            var system = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            _writeSystem = new CheckBox { Content = "Писать систему в", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Width = 150,
                ToolTip = "Значение — «Сокращение для системы» элемента (у трубы без системы — сокращение типа системы, у фитинга и арматуры — у соседей до 3 шагов)." };
            _systemParam = ParamCombo();
            system.Children.Add(_writeSystem);
            system.Children.Add(_systemParam);
            top.Children.Add(system);

            var mirror = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            mirror.Children.Add(new TextBlock { Text = "и тем же значением в", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 6, 0), Width = 130 });
            _mirrorParam = ParamCombo();
            _mirrorParam.ToolTip = "Второй параметр системы — только у элементов, где он есть (например LSR_Имя системы для старых спецификаций). Нет в проекте — не пишется.";
            mirror.Children.Add(_mirrorParam);
            top.Children.Add(mirror);

            var floor = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            _writeFloor = new CheckBox { Content = "Писать этаж в", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Width = 150,
                ToolTip = "Значение — имя уровня элемента («Уровень», «Базовый уровень», «Опорный уровень» или «Уровень спецификации») или своё значение из таблицы ниже." };
            _floorParam = ParamCombo();
            floor.Children.Add(_writeFloor);
            floor.Children.Add(_floorParam);
            top.Children.Add(floor);

            _insulation = new CheckBox { Content = "Изоляция труб получает систему и этаж своей трубы или фитинга (основы)", Margin = new Thickness(0, 0, 0, 4) };
            top.Children.Add(_insulation);
            _writeRoom = new CheckBox
            {
                Content = $"Писать «{Core.Config.ParamProfile.RoomName}» по помещениям АР (модель и загруженные связи)",
                ToolTip = "Для спецификации по помещению (например, насосной) в «Создать спецификацию». Номер помещения; если номер в АР повторяется — с именем,"
                    + " а если повторяется и так — с уровнем. Элемент вне помещений получает пустое значение.",
                Margin = new Thickness(0, 0, 0, 4),
            };
            top.Children.Add(_writeRoom);
            _chainNested = new CheckBox { Content = "Затем перенести во вложенные в той же операции", Margin = new Thickness(0, 0, 0, 0) };
            top.Children.Add(_chainNested);
            _nestedNote = new TextBlock { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 0, 0, 6) };
            top.Children.Add(_nestedNote);

            top.Children.Add(new TextBlock { Text = "Категории:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 2) });
            _categoryPanel = new WrapPanel();
            foreach ((string name, string label) in KnownCategories) AddCategory(name, label);
            top.Children.Add(_categoryPanel);
            top.Children.Add(new TextBlock
            {
                Text = "Этажи: в колонке «Своё значение» впишите, что писать вместо имени уровня (например «01 этаж» для «Этаж 01 (+0.000)»). Пусто — имя уровня как есть. "
                    + "Вложенные семейства здесь не пишутся — они получают значения от родителя переносом во вложенные.",
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
            Children.Add(top);

            _floors = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = Brushes.Gainsboro,
                RowHeaderWidth = 0,
                ItemsSource = _rows,
            };
            DataGridTextColumn level = ReportWindow.TextColumn("Уровень", nameof(FloorRow.Level), 260);
            level.IsReadOnly = true;
            DataGridTextColumn count = ReportWindow.TextColumn("Элементов", nameof(FloorRow.Count), 100);
            count.IsReadOnly = true;
            var value = new DataGridTextColumn
            {
                Header = "Своё значение",
                Binding = new Binding(nameof(FloorRow.Value)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.LostFocus },
                Width = new DataGridLength(220),
            };
            DataGridTextColumn result = ReportWindow.TextColumn("Будет записано", nameof(FloorRow.Result), 220);
            result.IsReadOnly = true;
            _floors.Columns.Add(level);
            _floors.Columns.Add(count);
            _floors.Columns.Add(value);
            _floors.Columns.Add(result);
            Children.Add(_floors);

            foreach (CheckBox box in new[] { _writeSystem, _writeFloor, _insulation, _writeRoom, _chainNested }) box.Click += (s, e) => RaiseChanged();
            _systemParam.SelectionChanged += (s, e) => RaiseChanged();
            _floorParam.SelectionChanged += (s, e) => RaiseChanged();
            _mirrorParam.SelectionChanged += (s, e) => RaiseChanged();
        }

        /// <summary>Пользователь поменял настройку (не загрузка).</summary>
        public event EventHandler? Changed;

        /// <summary>Показать настройку окна; варианты параметров и уровни — из последнего анализа.</summary>
        public void Load(SpecOptions options, IEnumerable<ParamCandidate> candidates, IReadOnlyDictionary<string, int> levels)
        {
            _loading = true;
            try
            {
                SystemFloorSettings sf = options.SystemFloor;
                _options.Clear();
                foreach (ParamCandidate c in candidates)
                {
                    if (_options.Any(o => o.Key.Equals(c.Key))) continue;
                    _options.Add(new ParamOption(c.Key, c.Nested > 0 ? $"{c.Name} ({c.KindText})" : $"{c.Name} ({c.KindText}, в модели не найден)"));
                }
                foreach (ParamKey? key in new[] { sf.SystemParam, sf.FloorParam, sf.MirrorParam })
                {
                    if (key != null && !_options.Any(o => o.Key.Equals(key))) _options.Add(new ParamOption(key, $"{key.Name} ({key.KindText})"));
                }
                _options.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Text, b.Text));
                Fill(_systemParam, sf.SystemParam);
                Fill(_floorParam, sf.FloorParam);
                var mirrorItems = new List<object> { NoMirror };
                mirrorItems.AddRange(_options);
                _mirrorParam.ItemsSource = mirrorItems;
                _mirrorParam.SelectedItem = sf.MirrorParam == null ? NoMirror : (object?)_options.FirstOrDefault(o => o.Key.Equals(sf.MirrorParam)) ?? NoMirror;
                _writeSystem.IsChecked = sf.WriteSystem;
                _writeFloor.IsChecked = sf.WriteFloor;
                _insulation.IsChecked = sf.Insulation;
                _writeRoom.IsChecked = sf.WriteRoom;
                _chainNested.IsChecked = sf.ChainNested;
                _nestedNote.Text = (options.NestedParams.Count == 0 ? "Параметры переноса не выбраны" : "Переносятся: " + string.Join(", ", options.NestedParams.Select(k => k.Name)))
                    + (options.NestedSavedInProject ? " (настройка «Перенос во вложенные» в модели)." : " (по умолчанию). Изменить — в окне «Перенос во вложенные».");

                foreach (string name in sf.Categories.Where(n => _categories.All(c => c.Name != n))) AddCategory(name, name);
                foreach ((string name, CheckBox box) in _categories) box.IsChecked = sf.Categories.Contains(name);

                var values = sf.Floors.Where(f => f.Level.Length > 0).GroupBy(f => f.Level).ToDictionary(g => g.Key, g => g.Last().Value);
                foreach (FloorRow row in _rows) row.PropertyChanged -= OnRowChanged;
                _rows.Clear();
                foreach (KeyValuePair<string, int> level in levels)
                {
                    _rows.Add(new FloorRow(level.Key, level.Value, true, values.TryGetValue(level.Key, out string? v) ? v : string.Empty));
                }
                foreach (KeyValuePair<string, string> stored in values.Where(p => !levels.ContainsKey(p.Key) && p.Value.Length > 0))
                {
                    _rows.Add(new FloorRow(stored.Key, 0, false, stored.Value));
                }
                foreach (FloorRow row in _rows) row.PropertyChanged += OnRowChanged;
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>Перенести настройку окна в <paramref name="options"/>.</summary>
        public void ApplyTo(SpecOptions options)
        {
            _floors.CommitEdit(DataGridEditingUnit.Row, true);
            SystemFloorSettings sf = options.SystemFloor.Clone();
            sf.SystemParam = (_systemParam.SelectedItem as ParamOption)?.Key;
            sf.FloorParam = (_floorParam.SelectedItem as ParamOption)?.Key;
            sf.MirrorParam = (_mirrorParam.SelectedItem as ParamOption)?.Key;
            sf.WriteSystem = _writeSystem.IsChecked == true;
            sf.WriteFloor = _writeFloor.IsChecked == true;
            sf.Insulation = _insulation.IsChecked == true;
            sf.WriteRoom = _writeRoom.IsChecked == true;
            sf.ChainNested = _chainNested.IsChecked == true;
            sf.Categories = _categories.Where(c => c.Box.IsChecked == true).Select(c => c.Name).ToList();
            sf.Floors = _rows.Where(r => r.Value.Length > 0).Select(r => new FloorRename(r.Level, r.Value)).ToList();
            options.SystemFloor = sf;
        }

        private void AddCategory(string name, string label)
        {
            var box = new CheckBox { Content = label, Margin = new Thickness(0, 0, 14, 2), ToolTip = name };
            box.Click += (s, e) => RaiseChanged();
            _categories.Add((name, box));
            _categoryPanel.Children.Add(box);
        }

        private void Fill(ComboBox combo, ParamKey? selected)
        {
            combo.ItemsSource = null;
            combo.ItemsSource = _options.ToList();
            combo.SelectedItem = selected == null ? null : _options.FirstOrDefault(o => o.Key.Equals(selected));
        }

        private static ComboBox ParamCombo() => new ComboBox
        {
            Width = 360,
            ToolTip = "Текстовые параметры экземпляра, найденные у элементов отмеченных категорий и у изоляции (общие и параметры проекта).",
        };

        private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(FloorRow.Value)) RaiseChanged();
        }

        private void RaiseChanged()
        {
            if (!_loading) Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
