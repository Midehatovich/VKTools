using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using VKTools.Core.Text;
using VKTools.Revit.Infrastructure;
using Binding = System.Windows.Data.Binding;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace VKTools.Revit.UI
{
    /// <summary>Строка выбора параметра для переноса во вложенные.</summary>
    internal sealed class CandidateRow : INotifyPropertyChanged
    {
        private bool _selected;

        public CandidateRow(ParamCandidate candidate, bool selected)
        {
            Key = candidate.Key;
            DataType = candidate.DataType;
            Nested = candidate.Nested;
            WouldChange = candidate.WouldChange;
            ParentEmpty = candidate.ParentEmpty;
            Problems = candidate.Problems;
            _selected = selected;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ParamKey Key { get; }

        public string Name => Key.Name;

        public string Kind => Key.KindText;

        public string DataType { get; }

        public int Nested { get; }

        public int WouldChange { get; }

        public int ParentEmpty { get; }

        public int Problems { get; }

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }
    }

    /// <summary>Вкладка «Параметры» окна «Перенос во вложенные»: область, пустые значения, какие параметры переносить.</summary>
    internal sealed class NestedSettingsPanel : DockPanel
    {
        private readonly RadioButton _scopeModel;
        private readonly RadioButton _scopeView;
        private readonly RadioButton _scopeSelection;
        private readonly RadioButton _keepEmpty;
        private readonly RadioButton _clearEmpty;
        private readonly TextBox _search;
        private readonly CheckBox _onlySelected;
        private readonly TextBlock _counter;
        private readonly DataGrid _grid;
        private readonly ObservableCollection<CandidateRow> _rows = new ObservableCollection<CandidateRow>();
        private readonly ICollectionView _view;
        private bool _loading;

        public NestedSettingsPanel()
        {
            var top = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            DockPanel.SetDock(top, Dock.Top);
            var scope = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            scope.Children.Add(Label("Область:"));
            _scopeModel = Radio("вся модель", "scope", "Все родители с общими вложенными");
            _scopeView = Radio("активный вид", "scope", "Родители, видимые на активном виде (план этажа, 3D-вид с подрезкой)");
            _scopeSelection = Radio("выделенные", "scope", "Выделите в модели родителей или их вложенные и нажмите «Обновить»");
            scope.Children.Add(_scopeModel);
            scope.Children.Add(_scopeView);
            scope.Children.Add(_scopeSelection);
            scope.Children.Add(Label("    У родителя пусто:"));
            _keepEmpty = Radio("вложенное не трогать", "empty", "Как NestedParamSync: пустое значение не переносится");
            _clearEmpty = Radio("очищать вложенное", "empty", "Только текст: у вложенного станет пусто. Числа не очищаются.");
            scope.Children.Add(_keepEmpty);
            scope.Children.Add(_clearEmpty);
            top.Children.Add(scope);

            var filter = new WrapPanel();
            filter.Children.Add(Label("Найти:"));
            _search = new TextBox { Width = 220, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Поиск по имени параметра" };
            _onlySelected = new CheckBox { Content = "только отмеченные", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            _counter = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };
            filter.Children.Add(_search);
            filter.Children.Add(_onlySelected);
            filter.Children.Add(_counter);
            top.Children.Add(filter);
            Children.Add(top);

            var note = new TextBlock
            {
                Text = "В списке — параметры, которые есть и у родителя, и у его общих вложенных: общие (ФОП) и параметры проекта; из встроенных — только «Комментарии». "
                    + "Значение берётся у экземпляра верхнего родителя, а если у экземпляра такого параметра нет — у его типа. Пишется только параметр экземпляра вложенного. "
                    + "ADSK_Группирование здесь нет: его ставит «Группирование». Счётчики — по последнему анализу.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 6, 0, 0),
            };
            DockPanel.SetDock(note, Dock.Bottom);
            Children.Add(note);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = Brushes.Gainsboro,
                RowHeaderWidth = 0,
            };
            _grid.Columns.Add(CheckColumn("Переносить", nameof(CandidateRow.Selected), 80));
            _grid.Columns.Add(ReportWindow.TextColumn("Параметр", nameof(CandidateRow.Name), 260));
            _grid.Columns.Add(ReportWindow.TextColumn("Вид", nameof(CandidateRow.Kind), 80));
            _grid.Columns.Add(ReportWindow.TextColumn("Тип данных", nameof(CandidateRow.DataType), 120));
            _grid.Columns.Add(ReportWindow.TextColumn("Вложенных с параметром", nameof(CandidateRow.Nested), 150));
            _grid.Columns.Add(ReportWindow.TextColumn("Изменится", nameof(CandidateRow.WouldChange), 90));
            _grid.Columns.Add(ReportWindow.TextColumn("У родителя пусто", nameof(CandidateRow.ParentEmpty), 120));
            _grid.Columns.Add(ReportWindow.TextColumn("Не перенести", nameof(CandidateRow.Problems), 100));
            foreach (DataGridColumn column in _grid.Columns.Skip(1)) column.IsReadOnly = true;
            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = Matches;
            _grid.ItemsSource = _view;
            Children.Add(_grid);

            foreach (RadioButton radio in new[] { _scopeModel, _scopeView, _scopeSelection, _keepEmpty, _clearEmpty }) radio.Checked += (s, e) => RaiseChanged();
            _search.TextChanged += (s, e) => RefreshView();
            _onlySelected.Click += (s, e) => RefreshView();
        }

        /// <summary>Пользователь поменял настройку (не загрузка).</summary>
        public event EventHandler? Changed;

        /// <summary>Показать настройки и счётчики последнего анализа.</summary>
        public void Load(SpecOptions options, IEnumerable<ParamCandidate> candidates)
        {
            _loading = true;
            try
            {
                _scopeModel.IsChecked = options.Scope == NestedScope.Model;
                _scopeView.IsChecked = options.Scope == NestedScope.ActiveView;
                _scopeSelection.IsChecked = options.Scope == NestedScope.Selection;
                _keepEmpty.IsChecked = !options.NestedClearEmpty;
                _clearEmpty.IsChecked = options.NestedClearEmpty;
                foreach (CandidateRow row in _rows) row.PropertyChanged -= OnRowChanged;
                _rows.Clear();
                var selected = new HashSet<ParamKey>(options.NestedParams);
                var shown = new HashSet<ParamKey>();
                foreach (ParamCandidate candidate in candidates)
                {
                    if (!shown.Add(candidate.Key)) continue;
                    _rows.Add(new CandidateRow(candidate, selected.Contains(candidate.Key)));
                }
                foreach (ParamKey key in options.NestedParams.Where(k => !shown.Contains(k)))
                {
                    _rows.Add(new CandidateRow(new ParamCandidate(key, "—"), true));
                }
                foreach (CandidateRow row in _rows) row.PropertyChanged += OnRowChanged;
            }
            finally
            {
                _loading = false;
            }
            RefreshView();
        }

        /// <summary>Отметить только параметры по умолчанию.</summary>
        public void SelectDefaults(SpecDefaults defaults)
        {
            _loading = true;
            try
            {
                var keys = new HashSet<ParamKey>(defaults.NestedParams);
                foreach (CandidateRow row in _rows) row.Selected = keys.Contains(row.Key);
                foreach (ParamKey key in defaults.NestedParams.Where(k => _rows.All(r => !r.Key.Equals(k))))
                {
                    var row = new CandidateRow(new ParamCandidate(key, "—"), true);
                    row.PropertyChanged += OnRowChanged;
                    _rows.Add(row);
                }
                _keepEmpty.IsChecked = true;
            }
            finally
            {
                _loading = false;
            }
            RaiseChanged();
        }

        /// <summary>Записать выбор окна в настройки (порядок — как в списке).</summary>
        public void ApplyTo(SpecOptions options)
        {
            options.Scope = _scopeSelection.IsChecked == true ? NestedScope.Selection : _scopeView.IsChecked == true ? NestedScope.ActiveView : NestedScope.Model;
            options.NestedClearEmpty = _clearEmpty.IsChecked == true;
            options.NestedParams = _rows.Where(r => r.Selected).Select(r => r.Key).ToList();
        }

        private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_onlySelected.IsChecked == true) RefreshView();
            RaiseChanged();
        }

        private void RaiseChanged()
        {
            if (_loading) return;
            UpdateCounter();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private bool Matches(object item)
        {
            var row = (CandidateRow)item;
            if (_onlySelected.IsChecked == true && !row.Selected) return false;
            string query = _search.Text.Trim();
            return query.Length == 0 || CultureInfo.CurrentCulture.CompareInfo.IndexOf(row.Name, query, CompareOptions.IgnoreCase) >= 0;
        }

        private void RefreshView()
        {
            _view.Refresh();
            UpdateCounter();
        }

        private void UpdateCounter()
        {
            int shown = _view.Cast<object>().Count();
            int selected = _rows.Count(r => r.Selected);
            _counter.Text = $"Отмечено {selected}; показано {shown} из {_rows.Count}";
        }

        internal static DataGridTemplateColumn CheckColumn(string header, string path, double width)
        {
            var check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            check.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            return new DataGridTemplateColumn { Header = header, CellTemplate = new DataTemplate { VisualTree = check }, Width = new DataGridLength(width) };
        }

        internal static TextBlock Label(string text) =>
            new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };

        private static RadioButton Radio(string text, string group, string tip) =>
            new RadioButton { Content = text, GroupName = group, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
    }

    /// <summary>Категория в выпадающем списке таблицы групп.</summary>
    internal sealed class CategoryItem : INotifyPropertyChanged
    {
        private string _name;

        public CategoryItem(string code, string name)
        {
            Code = code;
            _name = name;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Code { get; }

        /// <summary>Имя категории в Revit; пока анализа не было — код (OST_…).</summary>
        public string Name
        {
            get => _name;
            set
            {
                if (_name == value) return;
                _name = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
            }
        }

        public override string ToString() => Name;
    }

    /// <summary>Строка таблицы групп в редакторе.</summary>
    internal sealed class GroupRowVm : INotifyPropertyChanged
    {
        private string _group;
        private string _title;
        private string _category;
        private string _family;
        private bool _skip;

        public GroupRowVm(GroupRule rule)
        {
            _group = rule.Group;
            _title = rule.Title;
            _category = rule.Category;
            _family = rule.Family;
            _skip = rule.Skip;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Group
        {
            get => _group;
            set => SetField(ref _group, value ?? string.Empty, nameof(Group));
        }

        public string Title
        {
            get => _title;
            set => SetField(ref _title, value ?? string.Empty, nameof(Title));
        }

        /// <summary>Имя BuiltInCategory, «*» или пусто.</summary>
        public string Category
        {
            get => _category;
            set => SetField(ref _category, value ?? string.Empty, nameof(Category));
        }

        public string Family
        {
            get => _family;
            set => SetField(ref _family, value ?? string.Empty, nameof(Family));
        }

        public bool Skip
        {
            get => _skip;
            set
            {
                if (_skip == value) return;
                _skip = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Skip)));
            }
        }

        public GroupRule ToRule() => new GroupRule(Group, Title, Category, Family, Skip);

        private void SetField(ref string field, string value, string name)
        {
            if (field == value) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// Вкладка «Нумерация групп»: таблица ADSK_Группирование проекта. Проверка — тем же разбором, что grouping.csv;
    /// экспорт даёт файл, который можно положить в папку правил отдела.
    /// </summary>
    internal sealed class GroupSchemePanel : DockPanel
    {
        private readonly ObservableCollection<GroupRowVm> _rows = new ObservableCollection<GroupRowVm>();
        private readonly ObservableCollection<CategoryItem> _categories = new ObservableCollection<CategoryItem>();
        private readonly ObservableCollection<string> _families = new ObservableCollection<string>();
        private readonly Dictionary<string, string> _categoryNames = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly DataGrid _grid;
        private readonly TextBlock _errors;
        private readonly TextBlock _order;
        private readonly TextBlock _sortWarning;
        private readonly string _documentTitle;
        private bool _loading;

        public GroupSchemePanel(string documentTitle)
        {
            _documentTitle = documentTitle;
            var top = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            DockPanel.SetDock(top, Dock.Top);
            top.Children.Add(new TextBlock
            {
                Text = "Строка — номер группы и что в неё входит: категория и/или семейство (маска имени, * — любые символы). "
                    + "Строка семейства действует на само семейство и на всё, что в него вложено. Выбор правила: семейство родителя → своё семейство → категория → «Все остальные». "
                    + "«Не трогать» — элементы строки плагин не пишет (например, комплект, где группы проставлены вручную). Комплекты пожарных шкафов получают «группа.ключ» поверх таблицы.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 6),
            });
            var buttons = new WrapPanel();
            buttons.Children.Add(ReportWindow.MakeButton("+ Строка", (s, e) => AddRow()));
            buttons.Children.Add(ReportWindow.MakeButton("Удалить", (s, e) => DeleteRow()));
            Button up = ReportWindow.MakeButton("▲", (s, e) => Move(-1));
            Button down = ReportWindow.MakeButton("▼", (s, e) => Move(1));
            up.MinWidth = 36;
            down.MinWidth = 36;
            up.ToolTip = "Выше: среди строк семейств действует первая подходящая";
            down.ToolTip = up.ToolTip;
            buttons.Children.Add(up);
            buttons.Children.Add(down);
            Button export = ReportWindow.MakeButton("Экспорт…", (s, e) => Export());
            export.ToolTip = "Сохранить таблицу в файл формата grouping.csv: для другого проекта или папки правил отдела";
            Button import = ReportWindow.MakeButton("Импорт…", (s, e) => Import());
            import.ToolTip = "Загрузить таблицу из файла (grouping.csv или экспорт другого проекта)";
            buttons.Children.Add(export);
            buttons.Children.Add(import);
            top.Children.Add(buttons);
            Children.Add(top);

            var bottom = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            _errors = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap };
            _order = new TextBlock { Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
            _sortWarning = new TextBlock { Foreground = Brushes.DarkOrange, TextWrapping = TextWrapping.Wrap };
            bottom.Children.Add(_errors);
            bottom.Children.Add(_order);
            bottom.Children.Add(_sortWarning);
            Children.Add(bottom);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserSortColumns = false,
                SelectionMode = DataGridSelectionMode.Single,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                HorizontalGridLinesBrush = Brushes.Gainsboro,
                VerticalGridLinesBrush = Brushes.Gainsboro,
                RowHeaderWidth = 0,
                ItemsSource = _rows,
            };
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Номер",
                Binding = new Binding(nameof(GroupRowVm.Group)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(80),
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Название",
                Binding = new Binding(nameof(GroupRowVm.Title)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(220),
            });
            _grid.Columns.Add(new DataGridComboBoxColumn
            {
                Header = "Категория",
                ItemsSource = _categories,
                DisplayMemberPath = nameof(CategoryItem.Name),
                SelectedValuePath = nameof(CategoryItem.Code),
                SelectedValueBinding = new Binding(nameof(GroupRowVm.Category)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = new DataGridLength(260),
            });
            _grid.Columns.Add(FamilyColumn());
            _grid.Columns.Add(NestedSettingsPanel.CheckColumn("Не трогать", nameof(GroupRowVm.Skip), 90));
            Children.Add(_grid);

            _rows.CollectionChanged += OnRowsChanged;
        }

        public event EventHandler? Changed;

        /// <summary>В таблице есть ошибки: анализ и сохранение недоступны, пока их не исправят.</summary>
        public bool HasErrors { get; private set; }

        public void Load(IEnumerable<GroupRule> rules)
        {
            _loading = true;
            try
            {
                foreach (GroupRowVm row in _rows) row.PropertyChanged -= OnRowChanged;
                _rows.Clear();
                foreach (GroupRule rule in rules) _rows.Add(new GroupRowVm(rule));
                EnsureCategories(_rows.Select(r => r.Category));
            }
            finally
            {
                _loading = false;
            }
            Validate();
        }

        /// <summary>Подсказки из последнего анализа: семейства и категории модели.</summary>
        public void UpdateLists(IEnumerable<string> families, IReadOnlyDictionary<string, string> categories)
        {
            foreach (KeyValuePair<string, string> pair in categories) _categoryNames[pair.Key] = pair.Value;
            var existing = new HashSet<string>(_families, StringComparer.CurrentCultureIgnoreCase);
            foreach (string family in families.Where(f => !existing.Contains(f))) _families.Add(family);
            List<string> sorted = _families.OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList();
            _families.Clear();
            foreach (string family in sorted) _families.Add(family);
            EnsureCategories(_rows.Select(r => r.Category).Concat(categories.Keys));
        }

        /// <summary>Строки таблицы. Без фиксации правки: окно спрашивает их на каждое нажатие клавиши в ячейке.</summary>
        public List<GroupRule> Rules() => _rows.Select(r => r.ToRule()).ToList();

        /// <summary>Зафиксировать ячейку, которую сейчас правят (перед анализом, сохранением, экспортом).</summary>
        public void CommitEdits() => _grid.CommitEdit(DataGridEditingUnit.Row, true);

        /// <summary>
        /// Дополнить список категорий. Только добавляем и переименовываем: если убрать пункт из списка, ячейка с ним
        /// сбросила бы значение строки.
        /// </summary>
        private void EnsureCategories(IEnumerable<string> codes)
        {
            if (_categories.Count == 0)
            {
                _categories.Add(new CategoryItem(string.Empty, "—"));
                _categories.Add(new CategoryItem(GroupingTable.AnyCategory, "Все остальные категории (*)"));
            }
            foreach (string code in codes.Where(c => c.Length > 0 && c != GroupingTable.AnyCategory).Distinct().ToList())
            {
                string name = _categoryNames.TryGetValue(code, out string? n) ? n : code;
                CategoryItem? existing = _categories.FirstOrDefault(c => c.Code == code);
                if (existing != null)
                {
                    existing.Name = name;
                    continue;
                }
                int index = 2;
                while (index < _categories.Count && StringComparer.CurrentCultureIgnoreCase.Compare(_categories[index].Name, name) < 0) index++;
                _categories.Insert(index, new CategoryItem(code, name));
            }
        }

        private DataGridTemplateColumn FamilyColumn()
        {
            var cell = new FrameworkElementFactory(typeof(TextBlock));
            cell.SetBinding(TextBlock.TextProperty, new Binding(nameof(GroupRowVm.Family)));
            cell.SetValue(TextBlock.MarginProperty, new Thickness(3, 0, 3, 0));
            var edit = new FrameworkElementFactory(typeof(ComboBox));
            edit.SetValue(ComboBox.IsEditableProperty, true);
            edit.SetValue(ItemsControl.ItemsSourceProperty, _families);
            edit.SetBinding(ComboBox.TextProperty, new Binding(nameof(GroupRowVm.Family)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            return new DataGridTemplateColumn
            {
                Header = "Семейство (маска)",
                CellTemplate = new DataTemplate { VisualTree = cell },
                CellEditingTemplate = new DataTemplate { VisualTree = edit },
                Width = new DataGridLength(300),
            };
        }

        private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
            {
                foreach (GroupRowVm row in e.NewItems) row.PropertyChanged += OnRowChanged;
            }
            if (e.OldItems != null)
            {
                foreach (GroupRowVm row in e.OldItems) row.PropertyChanged -= OnRowChanged;
            }
            if (!_loading) RaiseChanged();
        }

        private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!_loading) RaiseChanged();
        }

        private void RaiseChanged()
        {
            Validate();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Проверка тем же разбором, что у grouping.csv: строка без категории и семейства, пустой номер, повтор категории.</summary>
        private void Validate()
        {
            List<GroupRule> rules = _rows.Select(r => r.ToRule()).ToList();
            var issues = new List<ConfigIssue>();
            GroupingTable? table = SpecRuleTables.ParseGroupingText(SpecRuleTables.FormatGrouping(rules, Enumerable.Empty<string>()), "таблица", issues);
            List<string> errors = issues.Where(i => i.Severity == Severity.Error)
                .Select(i => i.Line.HasValue ? $"Строка {i.Line.Value - 2}: {i.Message}" : i.Message).ToList();
            HasErrors = errors.Count > 0 || table == null;
            _errors.Text = errors.Count == 0 ? string.Empty : "Исправьте таблицу: " + string.Join("; ", errors) + ".";
            _errors.Visibility = errors.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (table == null)
            {
                _order.Text = string.Empty;
                _sortWarning.Visibility = Visibility.Collapsed;
                return;
            }
            _order.Text = "Порядок групп в спецификации (сортировка как текст): " + string.Join("  ·  ", table.GroupOrder());
            IReadOnlyList<(string First, string Second)> surprises = table.SortSurprises();
            _sortWarning.Text = surprises.Count == 0 ? string.Empty
                : $"«{surprises[0].First}» встанет перед «{surprises[0].Second}»: Revit сортирует текст посимвольно. Поставьте ведущий ноль (02, 10).";
            _sortWarning.Visibility = surprises.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void AddRow()
        {
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            int index = _grid.SelectedIndex >= 0 ? _grid.SelectedIndex + 1 : _rows.Count;
            var row = new GroupRowVm(new GroupRule(string.Empty, string.Empty, string.Empty, string.Empty, false));
            _rows.Insert(index, row);
            _grid.SelectedItem = row;
            _grid.ScrollIntoView(row);
        }

        private void DeleteRow()
        {
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            if (_grid.SelectedItem is GroupRowVm row) _rows.Remove(row);
        }

        private void Move(int delta)
        {
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
            int index = _grid.SelectedIndex;
            int target = index + delta;
            if (index < 0 || target < 0 || target >= _rows.Count) return;
            _rows.Move(index, target);
            _grid.SelectedIndex = target;
        }

        private void Export()
        {
            CommitEdits();
            List<GroupRule> rules = Rules();
            if (HasErrors)
            {
                MessageBox.Show(Window.GetWindow(this), "Сначала исправьте ошибки в таблице.", "VK Tools", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dialog = new SaveFileDialog
            {
                Filter = "Таблица групп (*.csv)|*.csv",
                FileName = ReportWindow.SafeFileName($"grouping-{_documentTitle}.csv"),
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                string text = SpecRuleTables.FormatGrouping(rules, new[]
                {
                    "VK Tools: таблица групп ADSK_Группирование (формат grouping.csv).",
                    $"Экспорт из проекта «{_documentTitle}», {DateTime.Now:dd.MM.yyyy HH:mm}.",
                    "Колонки: Группа; Название; Категория (имя BuiltInCategory или *); Семейство (маска, * — любые символы); Действие (пусто или «не трогать»).",
                });
                TextFileReader.WriteUtf8WithBom(dialog.FileName, text);
                Host.Log.Info("Таблица групп сохранена: " + dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(Window.GetWindow(this), "Не удалось сохранить: " + ex.Message, "VK Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Import()
        {
            var dialog = new OpenFileDialog { Filter = "Таблица групп (*.csv)|*.csv|Все файлы|*.*" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                string text = TextFileReader.ReadAllText(dialog.FileName, out _);
                var issues = new List<ConfigIssue>();
                GroupingTable? table = SpecRuleTables.ParseGroupingText(text, Path.GetFileName(dialog.FileName), issues);
                List<ConfigIssue> errors = issues.Where(i => i.Severity == Severity.Error).ToList();
                if (table == null || errors.Count > 0)
                {
                    MessageBox.Show(Window.GetWindow(this), "Файл не загружен:\n" + string.Join("\n", errors.Take(10)), "VK Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Load(table.Rules);
                RaiseChanged();
                Host.Log.Info("Таблица групп загружена из файла: " + dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(Window.GetWindow(this), "Не удалось прочитать: " + ex.Message, "VK Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
