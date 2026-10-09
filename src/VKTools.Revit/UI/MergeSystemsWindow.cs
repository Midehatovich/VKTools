using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using VKTools.Core.Checks;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Mep;
using Binding = System.Windows.Data.Binding;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using TextBox = System.Windows.Controls.TextBox;

namespace VKTools.Revit.UI
{
    /// <summary>Строка окна: копия системы и её судьба. Только данные — без объектов Revit.</summary>
    internal sealed class MergeRow : INotifyPropertyChanged
    {
        private static readonly Brush ReadyBrush = Brushes.White;
        private static readonly Brush ConfirmBrush = Frozen(Color.FromRgb(0xFF, 0xF6, 0xDD));
        private static readonly Brush RejectedBrush = Frozen(Color.FromRgb(0xF2, 0xF2, 0xF2));
        private static readonly Brush PassedBrush = Frozen(Color.FromRgb(0xE6, 0xF4, 0xE6));
        private static readonly Brush FailedBrush = Frozen(Color.FromRgb(0xFD, 0xE7, 0xE7));

        private bool _isChecked;
        private string _result = string.Empty;
        private Brush _rowBrush;

        public MergeRow(PlannedCandidate c)
        {
            Key = c.Key;
            State = c.State;
            Target = c.Group.Target != null ? c.Group.BaseName : "— (" + c.Group.BaseName + ")";
            SystemName = c.Record.Name;
            TypeName = c.Record.TypeName;
            Elements = c.ElementIds.Count;
            Terminals = c.TerminalIds.Count;
            Levels = c.Levels;
            Explanation = c.Explanation;
            ShowIds = c.ElementIds.OrderBy(x => x).ToList();
            _rowBrush = BaseBrush;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Key { get; }

        public CandidateState State { get; }

        public bool IsCheckable => State != CandidateState.Rejected;

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                bool v = value && IsCheckable;
                if (_isChecked == v) return;
                _isChecked = v;
                OnChanged(nameof(IsChecked));
            }
        }

        public string Target { get; }

        public string SystemName { get; }

        public string TypeName { get; }

        public string StateText =>
            State == CandidateState.Ready ? "Готова" : State == CandidateState.NeedsConfirmation ? "Нужна ваша отметка" : "Не объединяется";

        public int Elements { get; }

        public int Terminals { get; }

        public string Levels { get; }

        public string Explanation { get; }

        public List<long> ShowIds { get; }

        public string Result
        {
            get => _result;
            set
            {
                _result = value;
                OnChanged(nameof(Result));
            }
        }

        public Brush RowBrush
        {
            get => _rowBrush;
            private set
            {
                _rowBrush = value;
                OnChanged(nameof(RowBrush));
            }
        }

        private Brush BaseBrush => State == CandidateState.Ready ? ReadyBrush : State == CandidateState.NeedsConfirmation ? ConfirmBrush : RejectedBrush;

        public void SetOutcome(CandidateOutcome? outcome)
        {
            if (outcome == null)
            {
                Result = string.Empty;
                RowBrush = BaseBrush;
                return;
            }
            Result = (outcome.StateText + ". " + outcome.Detail).Trim();
            RowBrush = outcome.State == OutcomeState.Failed ? FailedBrush
                : outcome.State == OutcomeState.TrialPassed || outcome.State == OutcomeState.Merged ? PassedBrush
                : ConfirmBrush;
        }

        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    /// <summary>
    /// Объединение систем: «было → станет» по всем группам проекта, отметки, пробный прогон, запись.
    /// Немодальное, чтобы смотреть модель; все действия с моделью — через ExternalEvent,
    /// и перед каждым действием анализ строится заново по текущей модели.
    /// </summary>
    internal sealed class MergeSystemsWindow : Window
    {
        private readonly Autodesk.Revit.DB.Document _document;
        private readonly MergeSystemsHandler _handler;
        private readonly ExternalEvent _event;
        private readonly SelectElementsHandler _selectHandler;
        private readonly ExternalEvent _selectEvent;
        private readonly IntPtr _owner;
        private readonly bool _readOnly;
        private readonly ObservableCollection<MergeRow> _rows = new ObservableCollection<MergeRow>();
        private readonly ICollectionView _view;
        private readonly DataGrid _grid;
        private readonly TextBlock _summary;
        private readonly TextBlock _status;
        private readonly TextBox _details;
        private readonly CheckBox _hideRejected;
        private readonly Button _trial;
        private readonly Button _apply;
        private readonly Button _report;
        private string? _trialPassedFor;
        private CheckReport? _lastReport;
        private bool _busy;

        public MergeSystemsWindow(Autodesk.Revit.DB.Document document, MergeSystemsHandler handler, ExternalEvent externalEvent,
            SelectElementsHandler selectHandler, ExternalEvent selectEvent, IntPtr owner)
        {
            _document = document;
            _handler = handler;
            _event = externalEvent;
            _selectHandler = selectHandler;
            _selectEvent = selectEvent;
            _owner = owner;
            _readOnly = document.IsReadOnly;

            this.ApplyDefaultLook();
            Title = $"VK Tools — объединение систем: {document.Title}";
            Width = 1250;
            Height = 680;
            MinWidth = 800;
            MinHeight = 350;

            var root = new DockPanel { Margin = new Thickness(10) };
            Content = root;

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(header, Dock.Top);
            _summary = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            header.Children.Add(_summary);
            var steps = new List<string>
            {
                "1. Отметьте копии. Готовые отмечены сразу; «Нужна ваша отметка» — только после проверки в модели («Показать в модели»).",
                "2. «Пробный прогон» выполняет объединение и откатывает его: модель не меняется.",
                "3. «Применить» включается после успешного пробного прогона с теми же отметками. В журнале отмены будет одна запись.",
            };
            if (document.IsWorkshared) steps.Add("Совместная работа: перед записью синхронизируйтесь; после — синхронизируйте с освобождением элементов.");
            if (_readOnly) steps.Add("Документ открыт только для чтения: доступен только анализ.");
            header.Children.Add(new TextBlock { Text = string.Join(Environment.NewLine, steps), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            root.Children.Add(header);

            var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(tools, Dock.Top);
            tools.Children.Add(ReportWindow.MakeButton("Отметить готовые", (s, e) => SetChecks(r => r.State == CandidateState.Ready)));
            tools.Children.Add(ReportWindow.MakeButton("Снять отметки", (s, e) => SetChecks(r => false)));
            Button refresh = ReportWindow.MakeButton("Обновить", (s, e) => Send(MergeRequest.Refresh));
            refresh.ToolTip = "Заново проанализировать модель (после правок в ней)";
            tools.Children.Add(refresh);
            _hideRejected = new CheckBox { Content = "Скрыть не объединяемые", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            _hideRejected.Checked += (s, e) => _view?.Refresh();
            _hideRejected.Unchecked += (s, e) => _view?.Refresh();
            tools.Children.Add(_hideRejected);
            root.Children.Add(tools);

            var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            bottom.Children.Add(_status);
            _details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Foreground = Brushes.DimGray };
            bottom.Children.Add(new Expander { Header = "Итоговая проверка и сообщения Revit", Content = _details });
            var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
            Button show = ReportWindow.MakeButton("Показать в модели", (s, e) => ShowSelected());
            show.ToolTip = "Выделить элементы выбранных строк в Revit (или двойной щелчок по строке)";
            _report = ReportWindow.MakeButton("Отчёт…", (s, e) => OpenReport());
            _report.ToolTip = "Таблица с ElementId и сохранением в CSV";
            _trial = ReportWindow.MakeButton("Пробный прогон", (s, e) => Send(MergeRequest.Trial));
            _trial.ToolTip = "Выполнить отмеченное и откатить. Модель не меняется.";
            _apply = ReportWindow.MakeButton("Применить", (s, e) => ConfirmApply());
            _apply.FontWeight = FontWeights.SemiBold;
            Button close = ReportWindow.MakeButton("Закрыть", (s, e) => Close());
            foreach (Button b in new[] { show, _report }) DockPanel.SetDock(b, Dock.Left);
            foreach (Button b in new[] { close, _apply, _trial }) DockPanel.SetDock(b, Dock.Right);
            buttons.Children.Add(show);
            buttons.Children.Add(_report);
            buttons.Children.Add(close);
            buttons.Children.Add(_apply);
            buttons.Children.Add(_trial);
            bottom.Children.Add(buttons);
            root.Children.Add(bottom);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Extended,
                SelectionUnit = DataGridSelectionUnit.FullRow,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = Brushes.Gainsboro,
                RowHeaderWidth = 0,
            };
            _grid.Columns.Add(CheckColumn());
            _grid.Columns.Add(ReportWindow.TextColumn("Основная", nameof(MergeRow.Target), 110));
            _grid.Columns.Add(ReportWindow.TextColumn("Копия", nameof(MergeRow.SystemName), 110));
            _grid.Columns.Add(ReportWindow.TextColumn("Состояние", nameof(MergeRow.StateText), 130));
            _grid.Columns.Add(ReportWindow.TextColumn("Тип системы", nameof(MergeRow.TypeName), 140, wrap: true));
            _grid.Columns.Add(ReportWindow.TextColumn("Эл.", nameof(MergeRow.Elements), 50));
            _grid.Columns.Add(ReportWindow.TextColumn("Терм.", nameof(MergeRow.Terminals), 50));
            _grid.Columns.Add(ReportWindow.TextColumn("Уровни", nameof(MergeRow.Levels), 130, wrap: true));
            _grid.Columns.Add(ReportWindow.TextColumn("Пояснение", nameof(MergeRow.Explanation), 330, wrap: true));
            _grid.Columns.Add(ReportWindow.TextColumn("Результат", nameof(MergeRow.Result), 220, wrap: true));
            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(BackgroundProperty, new Binding(nameof(MergeRow.RowBrush))));
            _grid.RowStyle = rowStyle;
            _grid.MouseDoubleClick += (s, e) =>
            {
                if (!(e.OriginalSource is CheckBox)) ShowSelected();
            };
            root.Children.Add(_grid);

            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = item => !(_hideRejected.IsChecked == true && ((MergeRow)item).State == CandidateState.Rejected);
            _grid.ItemsSource = _view;

            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) Close();
            };
            UpdateButtons();
        }

        public Autodesk.Revit.DB.Document Document => _document;

        /// <summary>Новый анализ: строки заменяются, отметки пользователя сохраняются по ключу.</summary>
        public void ShowPlan(MergePlan plan, MergeRunResult? run, CheckReport report)
        {
            var previous = _rows.ToDictionary(r => r.Key, r => r.IsChecked, StringComparer.Ordinal);
            bool first = _rows.Count == 0 && _lastReport == null;
            foreach (MergeRow r in _rows) r.PropertyChanged -= OnRowChanged;
            _rows.Clear();

            Dictionary<string, CandidateOutcome> outcomes = run?.Outcomes.ToDictionary(o => o.Candidate.Key, StringComparer.Ordinal)
                ?? new Dictionary<string, CandidateOutcome>(StringComparer.Ordinal);
            var rows = plan.Candidates.Select(c => new MergeRow(c)).ToList();
            // Объединённые (их систем уже нет в модели) остаются в списке с результатом
            if (run != null && run.Committed)
            {
                rows.InsertRange(0, run.Outcomes.Where(o => o.State == OutcomeState.Merged).Select(o => new MergeRow(o.Candidate)));
            }
            foreach (MergeRow row in rows)
            {
                if (outcomes.TryGetValue(row.Key, out CandidateOutcome? outcome)) row.SetOutcome(outcome);
                if (outcome?.State == OutcomeState.Merged) row.IsChecked = false;
                else if (previous.TryGetValue(row.Key, out bool wasChecked) && !first) row.IsChecked = wasChecked;
                else row.IsChecked = row.State == CandidateState.Ready && !(run != null && run.Committed);
                row.PropertyChanged += OnRowChanged;
                _rows.Add(row);
            }

            int groups = plan.Groups.Count(g => g.Target != null && g.Candidates.Any(c => c.State != CandidateState.Rejected));
            _summary.Text = plan.Groups.Count == 0
                ? "Копий систем («В1 1», «К1.2 3») в модели нет — объединять нечего."
                : $"Групп к объединению: {groups}. Копий: готовы {plan.Count(CandidateState.Ready)}, нужна ваша отметка {plan.Count(CandidateState.NeedsConfirmation)}, не объединяются {plan.Count(CandidateState.Rejected)}.";

            _lastReport = report;
            if (run != null)
            {
                _status.Text = run.Status + (run.Errors.Count > 0 ? $" Ошибок: {run.Errors.Count} — см. «Результат» и «Отчёт…»." : string.Empty);
                _status.Foreground = run.Errors.Count > 0 ? Brushes.DarkRed : Brushes.DarkGreen;
                _details.Text = string.Join(Environment.NewLine, run.PostCheck.Concat(run.Warnings).Concat(run.Errors));
                _trialPassedFor = run.Mode == MergeMode.Trial && run.Passed ? SelectionKey(run.Outcomes.Select(o => o.Candidate.Key)) : null;
            }
            else
            {
                _trialPassedFor = null;
            }
            _busy = false;
            UpdateButtons();
        }

        public void ShowMessage(string text)
        {
            _busy = false;
            _status.Text = text;
            _status.Foreground = Brushes.DarkRed;
            UpdateButtons();
        }

        private static DataGridTemplateColumn CheckColumn()
        {
            var factory = new FrameworkElementFactory(typeof(CheckBox));
            factory.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new Binding(nameof(MergeRow.IsChecked)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            factory.SetBinding(IsEnabledProperty, new Binding(nameof(MergeRow.IsCheckable)) { Mode = BindingMode.OneWay });
            factory.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            factory.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            return new DataGridTemplateColumn
            {
                Header = "✓",
                CellTemplate = new DataTemplate { VisualTree = factory },
                Width = new DataGridLength(32),
            };
        }

        private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MergeRow.IsChecked)) UpdateButtons();
        }

        private void SetChecks(Func<MergeRow, bool> rule)
        {
            foreach (MergeRow r in _rows) r.IsChecked = rule(r);
        }

        private List<string> CheckedKeys() => _rows.Where(r => r.IsChecked).Select(r => r.Key).ToList();

        private static string SelectionKey(IEnumerable<string> keys) => string.Join("|", keys.OrderBy(k => k, StringComparer.Ordinal));

        private void UpdateButtons()
        {
            List<string> keys = CheckedKeys();
            _trial.IsEnabled = !_busy && !_readOnly && keys.Count > 0;
            _apply.IsEnabled = !_busy && !_readOnly && keys.Count > 0 && _trialPassedFor == SelectionKey(keys);
            _apply.ToolTip = _apply.IsEnabled ? "Записать отмеченное одной операцией" : "Сначала успешный «Пробный прогон» с этими отметками";
            _report.IsEnabled = _lastReport != null;
        }

        private void ConfirmApply()
        {
            List<string> keys = CheckedKeys();
            int targets = _rows.Where(r => r.IsChecked).Select(r => r.Target).Distinct().Count();
            MessageBoxResult answer = MessageBox.Show(this,
                $"Объединить отмеченные копии ({keys.Count}) в основные системы ({targets})?\n\nЭто одна запись в журнале отмены: Ctrl+Z отменит её целиком.",
                "VK Tools", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.OK) Send(MergeRequest.Apply);
        }

        private void Send(MergeRequest request)
        {
            if (_busy) return;
            _busy = true;
            _status.Text = request == MergeRequest.Refresh ? "Анализ модели…" : request == MergeRequest.Trial ? "Пробный прогон…" : "Запись…";
            _status.Foreground = Brushes.DimGray;
            UpdateButtons();
            _handler.Set(this, request, CheckedKeys());
            ExternalEventRequest raised = _event.Raise();
            if (raised != ExternalEventRequest.Accepted)
            {
                ShowMessage($"Revit сейчас не может выполнить команду ({raised}). Завершите текущее действие в Revit и повторите.");
            }
        }

        private void ShowSelected()
        {
            List<long> ids = _grid.SelectedItems.Cast<MergeRow>().SelectMany(r => r.ShowIds).Distinct().ToList();
            if (ids.Count == 0)
            {
                MessageBox.Show(this, "В выбранных строках нет элементов модели.", "VK Tools", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _selectHandler.Request(_document, ids);
            _selectEvent.Raise();
        }

        private void OpenReport()
        {
            if (_lastReport == null) return;
            try
            {
                ReportWindowHost.Show(_lastReport, _document, _owner);
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, ex.Message, "VK Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
