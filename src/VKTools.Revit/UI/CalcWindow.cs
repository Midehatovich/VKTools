using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using VKTools.Core.Calc;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Text;
using VKTools.Revit.Calc;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Spec;
using Binding = System.Windows.Data.Binding;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Visibility = System.Windows.Visibility;

namespace VKTools.Revit.UI
{
    /// <summary>Строка выбора спецификации — области расчёта.</summary>
    internal sealed class CalcScheduleRow : INotifyPropertyChanged
    {
        private bool _selected;

        public CalcScheduleRow(string name, string category, int? pipes, bool selected)
        {
            Name = name;
            Category = category;
            Pipes = pipes;
            _selected = selected;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Name { get; }

        public string Category { get; }

        /// <summary>null — спецификации нет в модели (сохранена в настройке, но удалена или переименована).</summary>
        public int? Pipes { get; }

        public string PipesText => Pipes.HasValue ? Pipes.Value.ToString(CultureInfo.CurrentCulture) : "нет в модели";

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

    /// <summary>
    /// Окно «Расчётные позиции» (Р3): спецификации-области, таблицы расчёта проекта (ресурсы, правила для труб, крепления)
    /// и «Было → станет». Тот же порядок, что у Р1/Р2: анализ → пробный прогон (запись и откат) → «Применить».
    /// </summary>
    internal sealed class CalcWindow : Window
    {
        private const string SchedulesTab = "Спецификации";
        private readonly Autodesk.Revit.DB.Document _document;
        private readonly CalcHandler _handler;
        private readonly ExternalEvent _event;
        private readonly SelectElementsHandler _selectHandler;
        private readonly ExternalEvent _selectEvent;
        private readonly ObservableCollection<CalcScheduleRow> _scheduleRows = new ObservableCollection<CalcScheduleRow>();
        private readonly ComboBox _systemParam;
        private readonly TextBox _group;
        private readonly TextBlock _scheduleErrors;
        private readonly TextBlock _familyState;
        private readonly CsvGridPanel _resources;
        private readonly CsvGridPanel _pipeRules;
        private readonly CsvGridPanel _supports;
        private readonly CsvGridPanel _cord;
        private readonly CsvGridPanel _reserve;
        private readonly ComboBox _sleeveFamily;
        private readonly ComboBox _sleeveLength;
        private readonly TextBox _cordAdd;
        private readonly TextBox _cordPitch;
        private readonly TextBlock _sleeveState;
        private readonly TextBlock _sleeveErrors;
        private readonly DockPanel _sleevesPanel;
        private readonly TabControl _tabs;
        private readonly TabItem _schedulesTab;
        private readonly TabItem _reportTab;
        private readonly TextBlock _state;
        private readonly TextBlock _summary;
        private readonly TextBlock _hint;
        private readonly TextBlock _status;
        private readonly TextBox _details;
        private readonly ComboBox _severityFilter;
        private readonly TextBox _search;
        private readonly TextBlock _counter;
        private readonly DataGrid _grid;
        private readonly Button _defaults;
        private readonly Button _save;
        private readonly Button _refresh;
        private readonly Button _trial;
        private readonly Button _apply;
        private readonly Button _export;
        private CalcOptions _base;
        private CalcDefaults _defaultsData;
        private string _savedSignature;
        private List<CheckFinding> _rows = new List<CheckFinding>();
        private ICollectionView? _view;
        private CheckReport? _report;
        private string? _trialPassedFor;
        private string? _shownFor;
        private bool _writeBlocked;
        private bool _hasTableErrors;
        private int _changes;
        private bool _busy;
        private bool _loading;

        public CalcWindow(Autodesk.Revit.DB.Document document, CalcOptions options, CalcDefaults defaults, CalcHandler handler, ExternalEvent externalEvent,
            SelectElementsHandler selectHandler, ExternalEvent selectEvent)
        {
            _document = document;
            _handler = handler;
            _event = externalEvent;
            _selectHandler = selectHandler;
            _selectEvent = selectEvent;
            _base = options.Clone();
            _defaultsData = defaults;
            _savedSignature = options.Signature();

            this.ApplyDefaultLook();
            Title = "VK Tools — расчётные позиции: " + document.Title;
            Width = 1250;
            Height = 780;
            MinWidth = 820;
            MinHeight = 440;

            var root = new DockPanel { Margin = new Thickness(10) };
            Content = root;

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(header, Dock.Top);
            header.Children.Add(new TextBlock
            {
                Text = "Грунтовка, краска, металл для креплений, бентонитовый шнур в гильзы и запас на штучные (хомуты): служебные позиции «_SYS_SpecCalcItem» по каждой системе в каждой отмеченной спецификации (группа 9.x). "
                    + "Количество считается по таблицам проекта; «SYS_Кол-во вручную» в позиции перекрывает расчёт.",
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            });
            _state = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            header.Children.Add(_state);
            _summary = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            header.Children.Add(_summary);
            _hint = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
            header.Children.Add(_hint);
            var steps = new List<string>
            {
                "1. Отметьте спецификации, проверьте таблицы и нажмите «Обновить»: на вкладке «Было → станет» — какие позиции и с каким количеством появятся.",
                "2. «Пробный прогон» записывает, проверяет попадание позиций в свои спецификации и откатывает: модель не меняется. 3. «Применить» — после успешного пробного прогона; одна запись в журнале отмены.",
                "Настройка хранится в модели: «Сохранить» или «Применить» (если она изменена). Без неё работают таблицы по умолчанию.",
            };
            if (document.IsWorkshared) steps.Add("Совместная работа: перед записью синхронизируйтесь; после — синхронизируйте с освобождением элементов.");
            header.Children.Add(new TextBlock { Text = string.Join(Environment.NewLine, steps), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            root.Children.Add(header);

            var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            bottom.Children.Add(_status);
            _details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Foreground = Brushes.DimGray };
            bottom.Children.Add(new Expander { Header = "Сводка по системам, правила и сообщения Revit", Content = _details });
            var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
            _defaults = ReportWindow.MakeButton("По умолчанию", (s, e) => ResetToDefaults());
            _defaults.ToolTip = "Таблицы из calc_*.csv (папка отдела или встроенные), параметр системы и группа по умолчанию. Отмеченные спецификации не меняются. В модели — после «Сохранить».";
            _save = ReportWindow.MakeButton("Сохранить", (s, e) => Send(CalcRequest.Save));
            _save.ToolTip = "Сохранить настройку в модели отдельной операцией. Позиции не меняются.";
            _refresh = ReportWindow.MakeButton("Обновить", (s, e) => Send(CalcRequest.Refresh));
            _refresh.ToolTip = "Заново посчитать с настройками окна";
            _trial = ReportWindow.MakeButton("Пробный прогон", (s, e) => Send(CalcRequest.Trial));
            _trial.ToolTip = "Записать, проверить и откатить. Модель не меняется.";
            _apply = ReportWindow.MakeButton("Применить", (s, e) => ConfirmApply());
            _apply.FontWeight = FontWeights.SemiBold;
            Button close = ReportWindow.MakeButton("Закрыть", (s, e) => Close());
            foreach (Button b in new[] { _defaults, _save }) DockPanel.SetDock(b, Dock.Left);
            foreach (Button b in new[] { close, _apply, _trial, _refresh }) DockPanel.SetDock(b, Dock.Right);
            buttons.Children.Add(_defaults);
            buttons.Children.Add(_save);
            buttons.Children.Add(close);
            buttons.Children.Add(_apply);
            buttons.Children.Add(_trial);
            buttons.Children.Add(_refresh);
            bottom.Children.Add(buttons);
            root.Children.Add(bottom);

            // Вкладка «Спецификации»
            var schedules = new DockPanel { Margin = new Thickness(6) };
            var schedTop = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            DockPanel.SetDock(schedTop, Dock.Top);
            schedTop.Children.Add(new TextBlock
            {
                Text = "Отмеченная спецификация — отдельная область: её трубы (с её фильтрами) дают свои позиции, а значения полей фильтров (этаж, раздел…) копируются в позиции, "
                    + "чтобы они попали в эту же спецификацию. Нужна спецификация нескольких категорий, где есть «Обобщённые модели». Ничего не отмечено — расчёт по всей модели.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 6),
            });
            var fields = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            _systemParam = new ComboBox { IsEditable = true, Width = 240, Margin = new Thickness(0, 0, 16, 0) };
            _systemParam.ToolTip = "Текстовый параметр трубы, по которому спецификация делит строки на системы (LSR_Имя системы, ADSK_Система, «Сокращение для системы»…). Он же пишется в позиции.";
            _group = new TextBox { Width = 60, Margin = new Thickness(0, 0, 16, 0), VerticalContentAlignment = VerticalAlignment.Center };
            _group.ToolTip = "ADSK_Группирование позиций: «9» → 9.1, 9.2… по колонке «Порядок» ресурсов";
            fields.Children.Add(NestedSettingsPanel.Label("Параметр системы:"));
            fields.Children.Add(_systemParam);
            fields.Children.Add(NestedSettingsPanel.Label("Группа позиций:"));
            fields.Children.Add(_group);
            schedTop.Children.Add(fields);
            _familyState = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0) };
            schedTop.Children.Add(_familyState);
            schedules.Children.Add(schedTop);
            _scheduleErrors = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            DockPanel.SetDock(_scheduleErrors, Dock.Bottom);
            schedules.Children.Add(_scheduleErrors);
            var scheduleGrid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = Brushes.Gainsboro,
                RowHeaderWidth = 0,
                ItemsSource = _scheduleRows,
            };
            scheduleGrid.Columns.Add(NestedSettingsPanel.CheckColumn("Считать", nameof(CalcScheduleRow.Selected), 70));
            scheduleGrid.Columns.Add(ReportWindow.TextColumn("Спецификация", nameof(CalcScheduleRow.Name), 420));
            scheduleGrid.Columns.Add(ReportWindow.TextColumn("Категория", nameof(CalcScheduleRow.Category), 180));
            scheduleGrid.Columns.Add(ReportWindow.TextColumn("Труб в ней", nameof(CalcScheduleRow.PipesText), 100));
            schedules.Children.Add(scheduleGrid);
            _schedulesTab = new TabItem { Header = SchedulesTab, Content = schedules };

            _resources = new CsvGridPanel(CalcTables.ResourcesFile, document.Title,
                "Ресурс = типоразмер служебного семейства. База: m2 — площадь труб (π·Dнар·L), m — длина, pcs — число креплений, sup_kg — масса креплений по таблице «Крепления». "
                + "Количество = база × Расход × Слоёв × Запас, вверх до «Округления». Колонки после «Основание» — параметры позиции (например ADSK_Завод-изготовитель): значение пишется в тип.",
                CalcTables.ResourceColumns, allowColumns: true) { Margin = new Thickness(6) };
            _pipeRules = new CsvGridPanel(CalcTables.PipeRulesFile, document.Title,
                "Какие ресурсы нужны трубе: первая подходящая строка сверху. Сегмент и Наименование — маски (* — любые символы), DN — диапазон, Изоляция: да / нет / пусто (любая). "
                + "Ресурсы — коды через «,» (пусто — трубе ничего не нужно, например оцинковка без окраски). Шаги креплений — если не по таблице «Крепления».",
                CalcTables.PipeRuleColumns, allowColumns: false) { Margin = new Thickness(6) };
            _supports = new CsvGridPanel(CalcTables.SupportsFile, document.Title,
                "Шаг креплений и масса одного крепления по DN (СП 73.13330.2016, табл. 2 — для металлических труб). Число креплений = ⌈длина участка системы / шаг⌉, участок короче 0,3 м — без крепления.",
                CalcTables.SupportColumns, allowColumns: false) { Margin = new Thickness(6) };
            _cord = new CsvGridPanel(CalcTables.CordFile, document.Title,
                "Какой шнур идёт в гильзы трубы по её DN (коды — ресурсы с базой cord на вкладке «Ресурсы»). Как в «Расчет гильз и шнура.xlsx»: Ф6 — DN15, Ф8 — DN20–25, Ф10 — DN32, Ф12 жёсткий — DN40, Ф12 мягкий — от DN50.",
                CalcTables.CordColumns, allowColumns: false);

            _sleevesPanel = new DockPanel { Margin = new Thickness(6) };
            var sleeveTop = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            DockPanel.SetDock(sleeveTop, Dock.Top);
            sleeveTop.Children.Add(new TextBlock
            {
                Text = "Шнур = Расход (4) × π·(Dнар трубы + добавка) × Lгильз / шаг витка, по гильзам на трубах каждой системы. Гильзы — отдельные элементы модели; "
                    + "труба гильзы — та, чья ось проходит через центр гильзы. Гильзы без трубы и без длины — в отчёте.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 6),
            });
            var sleeveFields = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            _sleeveFamily = new ComboBox { IsEditable = true, Width = 260, Margin = new Thickness(0, 0, 16, 0) };
            _sleeveFamily.ToolTip = "Имя семейства гильз или маска (* — любые символы), например «PA_Гильза» или «*ильз*»";
            _sleeveLength = new ComboBox { IsEditable = true, Width = 220, Margin = new Thickness(0, 0, 16, 0) };
            _sleeveLength.ToolTip = "Параметр длины гильзы (экземпляра или типа). Пусто — плагин возьмёт «Длина» или единственный параметр со словом «длина».";
            _cordAdd = new TextBox { Width = 50, Margin = new Thickness(0, 0, 16, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Excel: Dнар трубы + 10 мм" };
            _cordPitch = new TextBox { Width = 50, Margin = new Thickness(0, 0, 16, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Excel: длина гильз / 10 мм = число витков" };
            sleeveFields.Children.Add(NestedSettingsPanel.Label("Семейство гильз:"));
            sleeveFields.Children.Add(_sleeveFamily);
            sleeveFields.Children.Add(NestedSettingsPanel.Label("Длина гильзы:"));
            sleeveFields.Children.Add(_sleeveLength);
            sleeveFields.Children.Add(NestedSettingsPanel.Label("Добавка к Dнар, мм:"));
            sleeveFields.Children.Add(_cordAdd);
            sleeveFields.Children.Add(NestedSettingsPanel.Label("Шаг витка, мм:"));
            sleeveFields.Children.Add(_cordPitch);
            sleeveTop.Children.Add(sleeveFields);
            _sleeveState = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 4, 0, 0) };
            sleeveTop.Children.Add(_sleeveState);
            _sleeveErrors = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
            sleeveTop.Children.Add(_sleeveErrors);
            _sleevesPanel.Children.Add(sleeveTop);
            _sleevesPanel.Children.Add(_cord);
            _reserve = new CsvGridPanel(CalcTables.ReserveFile, document.Title,
                "Запас на штучные позиции с округлением вверх по строке спецификации: итог строки = ⌈N × (1 + Запас_%/100)⌉. На каждую строку отмеченной спецификации — служебная позиция "
                + "на разницу с теми же значениями полей сортировки, Revit сливает её со строкой (в «Кол.» сразу итог). Семейство и Тип — имя или маска, пустой Тип — все. "
                + "Нужно: спецификация без «Для каждого экземпляра», сортировка по параметрам проекта, у ADSK_Количество — «Вычислять итоги».",
                CalcTables.ReserveColumns, allowColumns: false) { Margin = new Thickness(6) };
            foreach (CsvGridPanel panel in new[] { _resources, _pipeRules, _supports, _cord, _reserve }) panel.Changed += (s, e) => OnSettingsChanged();

            var report = new DockPanel { Margin = new Thickness(6) };
            var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(filters, Dock.Top);
            _severityFilter = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
            _search = new TextBox { Width = 240, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Поиск по тексту строки" };
            _counter = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 12, 0) };
            Button show = ReportWindow.MakeButton("Показать в модели", (s, e) => ShowSelected());
            show.ToolTip = "Выделить элементы выбранных строк в Revit (или двойной щелчок по строке)";
            _export = ReportWindow.MakeButton("Сохранить CSV…", (s, e) => ExportCsv());
            _export.ToolTip = "Таблица для Excel, UTF-8 — для сверки с Dynamo-скриптом";
            filters.Children.Add(_severityFilter);
            filters.Children.Add(NestedSettingsPanel.Label("Найти:"));
            filters.Children.Add(_search);
            filters.Children.Add(_counter);
            filters.Children.Add(show);
            filters.Children.Add(_export);
            report.Children.Add(filters);
            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                SelectionMode = DataGridSelectionMode.Extended,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                ClipboardCopyMode = DataGridClipboardCopyMode.IncludeHeader,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = Brushes.Gainsboro,
                RowHeaderWidth = 0,
                EnableRowVirtualization = true,
            };
            _grid.Columns.Add(ReportWindow.TextColumn("Важность", nameof(CheckFinding.SeverityText), 100));
            _grid.Columns.Add(ReportWindow.TextColumn("ElementId", nameof(CheckFinding.ElementIdText), 80));
            _grid.Columns.Add(ReportWindow.TextColumn("Спецификация", nameof(CheckFinding.Category), 160, wrap: true));
            _grid.Columns.Add(ReportWindow.TextColumn("Система", nameof(CheckFinding.Family), 110));
            _grid.Columns.Add(ReportWindow.TextColumn("Ресурс", nameof(CheckFinding.Type), 80));
            _grid.Columns.Add(ReportWindow.TextColumn("Сообщение", nameof(CheckFinding.Message), 360, wrap: true));
            _grid.Columns.Add(ReportWindow.TextColumn("Было", nameof(CheckFinding.Current), 100, wrap: true));
            _grid.Columns.Add(ReportWindow.TextColumn("Станет", nameof(CheckFinding.Expected), 90, wrap: true));
            _grid.Columns.Add(ReportWindow.TextColumn("Что сделать", nameof(CheckFinding.Action), 240, wrap: true));
            _grid.RowStyle = SeverityRowStyle();
            _grid.MouseDoubleClick += (s, e) => ShowSelected();
            report.Children.Add(_grid);
            _reportTab = new TabItem { Header = "Было → станет", Content = report };

            _tabs = new TabControl();
            _tabs.Items.Add(_schedulesTab);
            _tabs.Items.Add(new TabItem { Header = "Ресурсы", Content = _resources });
            _tabs.Items.Add(new TabItem { Header = "Правила для труб", Content = _pipeRules });
            _tabs.Items.Add(new TabItem { Header = "Крепления", Content = _supports });
            _tabs.Items.Add(new TabItem { Header = CalcOptions.SleevesTab, Content = _sleevesPanel });
            _tabs.Items.Add(new TabItem { Header = CalcOptions.ReserveTab, Content = _reserve });
            _tabs.Items.Add(_reportTab);
            _tabs.SelectedItem = _reportTab;
            root.Children.Add(_tabs);

            LoadOptions(options, Array.Empty<CalcScheduleInfo>(), Array.Empty<string>());
            _scheduleRows.CollectionChanged += (s, e) => OnSettingsChanged();
            _systemParam.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => OnSettingsChanged()));
            _systemParam.SelectionChanged += (s, e) => OnSettingsChanged();
            _group.TextChanged += (s, e) => OnSettingsChanged();
            foreach (ComboBox box in new[] { _sleeveFamily, _sleeveLength })
            {
                box.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => OnSettingsChanged()));
                box.SelectionChanged += (s, e) => OnSettingsChanged();
            }
            _cordAdd.TextChanged += (s, e) => OnSettingsChanged();
            _cordPitch.TextChanged += (s, e) => OnSettingsChanged();
            _severityFilter.SelectionChanged += (s, e) => RefreshView();
            _search.TextChanged += (s, e) => RefreshView();
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && !(e.OriginalSource is TextBox) && !(e.OriginalSource is DataGridCell)) Close();
            };
            Validate();
            UpdateState();
            UpdateButtons();
        }

        public Autodesk.Revit.DB.Document Document => _document;

        /// <summary>Настройки, которые сейчас в окне.</summary>
        public CalcOptions CurrentOptions()
        {
            CalcOptions options = _base.Clone();
            options.Schedules = _scheduleRows.Where(r => r.Selected).Select(r => r.Name).ToList();
            options.SystemParameter = (_systemParam.Text ?? string.Empty).Trim();
            options.Group = _group.Text.Trim();
            options.ResourcesText = _resources.Text();
            options.PipeRulesText = _pipeRules.Text();
            options.SupportsText = _supports.Text();
            options.CordText = _cord.Text();
            options.ReserveText = _reserve.Text();
            options.SleeveFamily = (_sleeveFamily.Text ?? string.Empty).Trim();
            options.SleeveLength = (_sleeveLength.Text ?? string.Empty).Trim();
            options.CordAdd = _cordAdd.Text.Trim();
            options.CordPitch = _cordPitch.Text.Trim();
            return options;
        }

        /// <summary>Что сейчас сохранено в модели (после открытия, «Сохранить», «Применить» и каждого анализа).</summary>
        public void ShowSettingsState(CalcOptions stored, CalcDefaults defaults)
        {
            _defaultsData = defaults;
            _savedSignature = stored.Signature();
            _base.SavedInProject = stored.SavedInProject;
            _base.SavedStamp = stored.SavedStamp;
            UpdateState();
            UpdateButtons();
        }

        /// <summary>Новый анализ или итог прогона; <paramref name="after"/> — анализ после записи (должен быть пустым).</summary>
        public void ShowPlan(CalcPlan plan, CalcRunResult? run, CalcPlan? after, CheckReport report, CalcOptions used)
        {
            _report = report;
            _rows = report.Findings;
            _shownFor = used.Signature();
            _writeBlocked = plan.WriteBlockedReason != null;
            _changes = (after ?? plan).ChangeCount;

            UpdateSchedules(plan.Schedules, used.Schedules);
            UpdateSystemCandidates(plan.SystemCandidates);
            Fill(_sleeveFamily, plan.SleeveFamilies);
            Fill(_sleeveLength, plan.SleeveLengthCandidates);
            _sleeveState.Text = plan.SleeveCount < 0
                ? "Шнура в ресурсах нет (база cord) — гильзы не ищутся."
                : $"Гильз по маске: {plan.SleeveCount}; длина берётся из «{plan.SleeveLengthUsed ?? "не задано"}». Сколько гильз на трубах каждой системы — в «Сводке» внизу.";
            _familyState.Text = plan.FamilyId.HasValue
                ? $"Служебное семейство «{plan.FamilyName}» загружено в проект."
                : $"Служебного семейства «{plan.FamilyName}» в проекте нет — «Применить» создаст его (обобщённая модель с параметрами SYS_) и загрузит.";

            _severityFilter.Items.Clear();
            _severityFilter.Items.Add("Все строки");
            foreach (Severity s in new[] { Severity.Error, Severity.Warning, Severity.Info })
            {
                _severityFilter.Items.Add($"{s.ToRussian()} ({_rows.Count(f => f.Severity == s)})");
            }
            _severityFilter.SelectedIndex = 0;
            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = Matches;
            _grid.ItemsSource = _view;
            UpdateCounter();

            string done = run?.Committed == true ? "Записано" : "Будет изменено";
            _summary.Text = $"Позиций по расчёту: {plan.Result.Targets.Count}. {done} {plan.ChangeCount}, без изменений {plan.Unchanged}, "
                + $"ошибок {plan.ErrorCount}, предупреждений {plan.WarningCount}.";
            if (after != null)
            {
                _summary.Text += after.ChangeCount == 0
                    ? " Повторный анализ после записи: изменений 0."
                    : $" Повторный анализ после записи: снова изменилось бы {after.ChangeCount} — значения переписывает кто-то другой, см. «Перезаписано».";
            }
            _hint.Text = plan.WriteBlockedReason == null ? string.Empty : "Запись запрещена: " + plan.WriteBlockedReason;
            _hint.Visibility = plan.WriteBlockedReason == null ? Visibility.Collapsed : Visibility.Visible;

            var details = new List<string>();
            if (run != null)
            {
                if (!string.IsNullOrEmpty(run.Timings)) details.Add("Время: " + run.Timings);
                details.AddRange(run.Errors);
                details.AddRange(run.Warnings);
            }
            details.AddRange(plan.Notes);
            details.AddRange(plan.RuleSources.Select(r => "Правила: " + r));
            _details.Text = string.Join(Environment.NewLine, details);

            if (run != null)
            {
                _status.Text = run.Status
                    + (run.SettingsSaved ? " Настройка сохранена в модели той же операцией." : string.Empty)
                    + (run.DisabledUpdaters.Count > 0 ? $" До перезапуска Revit отключено: {string.Join(", ", run.DisabledUpdaters)}." : string.Empty);
                _status.Foreground = run.Errors.Count > 0 ? Brushes.DarkRed : run.Cancelled ? Brushes.DimGray
                    : run.Failed.Count > 0 || run.Overwritten.Count > 0 ? Brushes.DarkOrange : Brushes.DarkGreen;
                _trialPassedFor = run.Mode == SpecRunMode.Trial && run.Passed ? _shownFor : null;
                _tabs.SelectedItem = _reportTab;
            }
            else
            {
                _trialPassedFor = null;
                _status.Text = $"Анализ модели: {DateTime.Now:HH:mm:ss}. Модель не изменялась."
                    + (_changes > 0 ? string.Empty
                        : " Изменений нет: пробный прогон и «Применить» не нужны" + (SettingsDirty() ? ", настройку из окна сохраните кнопкой «Сохранить»." : "."));
                _status.Foreground = Brushes.DimGray;
            }
            _busy = false;
            UpdateState();
            UpdateButtons();
        }

        public void ShowMessage(string text, bool error = true)
        {
            _busy = false;
            _status.Text = text;
            _status.Foreground = error ? Brushes.DarkRed : Brushes.DimGray;
            UpdateButtons();
        }

        private void LoadOptions(CalcOptions options, IReadOnlyCollection<CalcScheduleInfo> schedules, IEnumerable<string> candidates)
        {
            _loading = true;
            try
            {
                _systemParam.Text = options.SystemParameter;
                _group.Text = options.Group;
                _resources.Load(options.ResourcesText);
                _pipeRules.Load(options.PipeRulesText);
                _supports.Load(options.SupportsText);
                LoadSleeves(options);
                UpdateSchedules(schedules, options.Schedules);
                UpdateSystemCandidates(candidates);
            }
            finally
            {
                _loading = false;
            }
        }

        private void LoadSleeves(CalcOptions options)
        {
            _reserve.Load(options.ReserveText);
            _cord.Load(options.CordText);
            _sleeveFamily.Text = options.SleeveFamily;
            _sleeveLength.Text = options.SleeveLength;
            _cordAdd.Text = options.CordAdd;
            _cordPitch.Text = options.CordPitch;
        }

        /// <summary>Список спецификаций модели; отметки — из окна (если пользователь их уже менял) или из настройки.</summary>
        private void UpdateSchedules(IReadOnlyCollection<CalcScheduleInfo> schedules, IReadOnlyCollection<string> selected)
        {
            bool wasLoading = _loading;
            _loading = true;
            try
            {
                foreach (CalcScheduleRow row in _scheduleRows) row.PropertyChanged -= OnScheduleChanged;
                var marked = new HashSet<string>(selected, StringComparer.Ordinal);
                _scheduleRows.Clear();
                foreach (string missing in selected.Where(n => schedules.All(s => s.Name != n)))
                {
                    _scheduleRows.Add(new CalcScheduleRow(missing, string.Empty, null, true));
                }
                foreach (CalcScheduleInfo info in schedules.OrderByDescending(s => marked.Contains(s.Name)))
                {
                    _scheduleRows.Add(new CalcScheduleRow(info.Name, info.Category, info.Pipes, marked.Contains(info.Name)));
                }
                foreach (CalcScheduleRow row in _scheduleRows) row.PropertyChanged += OnScheduleChanged;
            }
            finally
            {
                _loading = wasLoading;
            }
        }

        private void UpdateSystemCandidates(IEnumerable<string> candidates) => Fill(_systemParam, candidates);

        /// <summary>Подсказки в выпадающем списке; введённый текст не меняется.</summary>
        private void Fill(ComboBox box, IEnumerable<string> candidates)
        {
            string text = box.Text;
            bool wasLoading = _loading;
            _loading = true;
            try
            {
                List<string> items = candidates.ToList();
                if (items.Count == 0) return;
                box.Items.Clear();
                foreach (string item in items) box.Items.Add(item);
                box.Text = text;
            }
            finally
            {
                _loading = wasLoading;
            }
        }

        private void OnScheduleChanged(object? sender, PropertyChangedEventArgs e) => OnSettingsChanged();

        private bool SettingsDirty() => CurrentOptions().Signature() != _savedSignature;

        private bool IsCurrent() => _shownFor != null && CurrentOptions().Signature() == _shownFor;

        private void OnSettingsChanged()
        {
            if (_loading) return;
            Validate();
            UpdateState();
            UpdateButtons();
        }

        /// <summary>Разбор таблиц тем же кодом, что у расчёта: ошибки — под таблицей своей вкладки.</summary>
        private void Validate()
        {
            CalcRuleSet rules = CurrentOptions().Parse();
            List<ConfigIssue> errors = rules.Issues.Where(i => i.Severity == Severity.Error).ToList();
            _resources.ShowIssues(errors.Where(i => i.FileName == "Ресурсы"));
            _pipeRules.ShowIssues(errors.Where(i => i.FileName == "Правила для труб"));
            _supports.ShowIssues(errors.Where(i => i.FileName == "Крепления"));
            _cord.ShowIssues(errors.Where(i => i.FileName == CalcOptions.SleevesTab && i.Line.HasValue));
            _reserve.ShowIssues(errors.Where(i => i.FileName == CalcOptions.ReserveTab));
            List<string> sleeveErrors = errors.Where(i => i.FileName == CalcOptions.SleevesTab && !i.Line.HasValue).Select(i => i.Message).ToList();
            _sleeveErrors.Text = sleeveErrors.Count == 0 ? string.Empty : "Исправьте: " + string.Join("; ", sleeveErrors) + ".";
            _sleeveErrors.Visibility = sleeveErrors.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            List<string> other = errors.Where(i => i.FileName == SchedulesTab).Select(i => i.Message).ToList();
            _scheduleErrors.Text = other.Count == 0 ? string.Empty : "Исправьте: " + string.Join("; ", other) + ".";
            _scheduleErrors.Visibility = other.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            _hasTableErrors = errors.Count > 0;
        }

        private void UpdateState()
        {
            string text;
            if (_defaultsData.ProjectBlocked != null)
            {
                text = "Настройка проекта: " + _defaultsData.ProjectBlocked;
                _state.Foreground = Brushes.DarkRed;
            }
            else
            {
                text = _base.SavedInProject ? "Настройка проекта: " + (_base.SavedStamp ?? "сохранена в модели") : "Таблицы по умолчанию: в модели своей настройки нет.";
                _state.Foreground = Brushes.DimGray;
            }
            if (SettingsDirty())
            {
                text += " В окне изменено, в модели не сохранено.";
                if (_defaultsData.ProjectBlocked == null) _state.Foreground = Brushes.DarkOrange;
            }
            if (_hasTableErrors) text += " В таблицах ошибки (под таблицей вкладки).";
            if (_shownFor != null && !IsCurrent()) text += " Анализ устарел — нажмите «Обновить».";
            _state.Text = text;
        }

        private void UpdateButtons()
        {
            bool current = IsCurrent();
            bool dirty = SettingsDirty();
            _refresh.IsEnabled = !_busy;
            _refresh.FontWeight = _shownFor != null && !current ? FontWeights.SemiBold : FontWeights.Normal;
            _trial.IsEnabled = !_busy && current && _changes > 0 && !_hasTableErrors;
            const string nothing = "Изменений нет: проверять и записывать нечего. Настройку сохраняет кнопка «Сохранить».";
            _trial.ToolTip = !current ? "Настройки изменились: сначала «Обновить»" : _changes == 0 ? nothing : "Записать, проверить и откатить. Модель не меняется.";
            _apply.IsEnabled = !_busy && current && _changes > 0 && !_writeBlocked && _trialPassedFor == _shownFor;
            _apply.ToolTip = _writeBlocked ? "Запись запрещена: см. красную строку вверху"
                : current && _changes == 0 ? nothing
                : _apply.IsEnabled ? "Записать одной операцией" : "Сначала успешный «Пробный прогон» с этими настройками";
            _save.IsEnabled = !_busy && !_hasTableErrors && (dirty || _defaultsData.ProjectBlocked != null);
            _defaults.IsEnabled = !_busy;
            _export.IsEnabled = _report != null;
            foreach (UIElement panel in new UIElement[] { _resources, _pipeRules, _supports, _sleevesPanel, _reserve, (UIElement)_schedulesTab.Content }) panel.IsEnabled = !_busy;
        }

        private void ResetToDefaults()
        {
            CalcOptions defaults = CalcOptions.FromDefaults(_defaultsData);
            defaults.Schedules = CurrentOptions().Schedules;
            _loading = true;
            try
            {
                _systemParam.Text = defaults.SystemParameter;
                _group.Text = defaults.Group;
                _resources.Load(defaults.ResourcesText);
                _pipeRules.Load(defaults.PipeRulesText);
                _supports.Load(defaults.SupportsText);
                LoadSleeves(defaults);
            }
            finally
            {
                _loading = false;
            }
            OnSettingsChanged();
            ShowMessage(SettingsDirty()
                ? "В окне — таблицы по умолчанию (спецификации не менялись). «Обновить» покажет, что изменится; «Сохранить» запишет это в модель."
                : "В окне уже настройки по умолчанию.", error: false);
        }

        private void ConfirmApply()
        {
            string settings = SettingsDirty() ? "\n\nНастройка из окна сохранится в модели той же операцией." : string.Empty;
            MessageBoxResult answer = MessageBox.Show(this,
                $"Записать изменения ({_changes})?{settings}\n\nЭто одна запись в журнале отмены: Ctrl+Z отменит её целиком.",
                "VK Tools", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.OK) Send(CalcRequest.Apply);
        }

        private void Send(CalcRequest request)
        {
            if (_busy) return;
            foreach (CsvGridPanel panel in new[] { _resources, _pipeRules, _supports, _cord, _reserve }) panel.CommitEdits();
            CalcOptions options = CurrentOptions();
            if ((request == CalcRequest.Trial || request == CalcRequest.Apply) && options.Signature() != _shownFor)
            {
                ShowMessage("Настройки изменились после анализа: сначала «Обновить».");
                return;
            }
            if (request == CalcRequest.Save && _hasTableErrors)
            {
                ShowMessage("В таблицах ошибки: исправьте их (текст под таблицей вкладки), потом «Сохранить».");
                return;
            }
            _busy = true;
            _status.Text = request == CalcRequest.Refresh ? "Расчёт…" : request == CalcRequest.Trial ? "Пробный прогон…"
                : request == CalcRequest.Save ? "Сохранение настройки…" : "Запись…";
            _status.Foreground = Brushes.DimGray;
            UpdateButtons();
            _handler.Set(this, request, options);
            ExternalEventRequest raised = _event.Raise();
            if (raised != ExternalEventRequest.Accepted)
            {
                ShowMessage($"Revit сейчас не может выполнить команду ({raised}). Завершите текущее действие в Revit и повторите.");
            }
        }

        private bool Matches(object item)
        {
            var f = (CheckFinding)item;
            int severity = _severityFilter.SelectedIndex;
            if (severity == 1 && f.Severity != Severity.Error) return false;
            if (severity == 2 && f.Severity != Severity.Warning) return false;
            if (severity == 3 && f.Severity != Severity.Info) return false;
            string query = _search.Text.Trim();
            if (query.Length == 0) return true;
            return Contains(f.Message, query) || Contains(f.Family, query) || Contains(f.Type, query) || Contains(f.Category, query)
                || Contains(f.Current, query) || Contains(f.Expected, query) || Contains(f.ElementIdText, query);
        }

        private static bool Contains(string text, string query) =>
            CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, query, CompareOptions.IgnoreCase) >= 0;

        private void RefreshView()
        {
            if (_view == null) return;
            _view.Refresh();
            UpdateCounter();
        }

        private void UpdateCounter()
        {
            int shown = _view?.Cast<object>().Count() ?? 0;
            _counter.Text = shown == _rows.Count ? $"Строк: {shown}" : $"Показано {shown} из {_rows.Count}";
        }

        private static Style SeverityRowStyle()
        {
            var style = new Style(typeof(DataGridRow));
            style.Triggers.Add(RowColor(Severity.Error, Color.FromRgb(0xFD, 0xE7, 0xE7)));
            style.Triggers.Add(RowColor(Severity.Warning, Color.FromRgb(0xFF, 0xF6, 0xDD)));
            return style;
        }

        private static DataTrigger RowColor(Severity severity, Color color)
        {
            var trigger = new DataTrigger { Binding = new Binding(nameof(CheckFinding.Severity)), Value = severity };
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            trigger.Setters.Add(new Setter(BackgroundProperty, brush));
            return trigger;
        }

        private void ShowSelected()
        {
            List<long> ids = _grid.SelectedItems.Cast<CheckFinding>().SelectMany(f => f.ElementsToShow()).Distinct().ToList();
            if (ids.Count == 0)
            {
                MessageBox.Show(this, "В выбранных строках нет элементов модели.", "VK Tools", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _selectHandler.Request(_document, ids);
            _selectEvent.Raise();
        }

        private void ExportCsv()
        {
            if (_report == null) return;
            var dialog = new SaveFileDialog
            {
                Filter = "CSV для Excel (*.csv)|*.csv",
                FileName = ReportWindow.SafeFileName($"VKTools-расчётные-позиции-{_report.DocumentTitle}-{_report.CreatedAt:yyyyMMdd-HHmm}.csv"),
            };
            if (dialog.ShowDialog(this) != true) return;
            try
            {
                TextFileReader.WriteUtf8WithBom(dialog.FileName, _report.ToCsv());
                Host.Log.Info("Отчёт сохранён: " + dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Host.Log.Error("Не удалось сохранить отчёт.", ex);
                MessageBox.Show(this, "Не удалось сохранить: " + ex.Message, "VK Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
