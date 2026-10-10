using System;
using System.Collections.Generic;
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
using VKTools.Core.Checks;
using VKTools.Core.Spec;
using VKTools.Core.Text;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Spec;
using Binding = System.Windows.Data.Binding;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace VKTools.Revit.UI
{
    /// <summary>
    /// «Параметры спецификации» (Р2): было → станет по модулям «Вложенные» и «Группирование», пробный прогон, запись.
    /// Немодальное; все действия с моделью — через ExternalEvent, перед каждым анализ строится заново (как в объединении систем).
    /// </summary>
    internal sealed class SpecParamsWindow : Window
    {
        private const string AllModules = "Все модули";

        private readonly Autodesk.Revit.DB.Document _document;
        private readonly SpecParamsHandler _handler;
        private readonly ExternalEvent _event;
        private readonly SelectElementsHandler _selectHandler;
        private readonly ExternalEvent _selectEvent;
        private readonly CheckBox _nested;
        private readonly CheckBox _grouping;
        private readonly TextBlock _summary;
        private readonly TextBlock _hint;
        private readonly TextBlock _status;
        private readonly TextBox _details;
        private readonly ComboBox _severityFilter;
        private readonly ComboBox _moduleFilter;
        private readonly TextBox _search;
        private readonly TextBlock _counter;
        private readonly DataGrid _grid;
        private readonly Button _trial;
        private readonly Button _apply;
        private readonly Button _export;
        private List<CheckFinding> _rows = new List<CheckFinding>();
        private ICollectionView? _view;
        private CheckReport? _report;
        private string? _trialPassedFor;
        private string _shownFor = string.Empty;
        private bool _writeBlocked;
        private int _changes;
        private bool _busy;

        public SpecParamsWindow(Autodesk.Revit.DB.Document document, SpecParamsHandler handler, ExternalEvent externalEvent,
            SelectElementsHandler selectHandler, ExternalEvent selectEvent, IReadOnlyCollection<SpecModule> modules)
        {
            _document = document;
            _handler = handler;
            _event = externalEvent;
            _selectHandler = selectHandler;
            _selectEvent = selectEvent;

            this.ApplyDefaultLook();
            Title = $"VK Tools — параметры спецификации: {document.Title}";
            Width = 1250;
            Height = 700;
            MinWidth = 800;
            MinHeight = 380;

            var root = new DockPanel { Margin = new Thickness(10) };
            Content = root;

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(header, Dock.Top);
            _nested = new CheckBox
            {
                Content = "Вложенные семейства: значения родителя → его общие вложенные (ADSK_Этаж, LSR_Имя системы, LSR_Тип системы — nested_params.csv)",
                IsChecked = modules.Contains(SpecModule.Nested),
                Margin = new Thickness(0, 0, 0, 2),
            };
            _grouping = new CheckBox
            {
                Content = "Группирование: ADSK_Группирование по категории (спринклеры 0 … прочее 8 — grouping.csv) и комплекты пожарных шкафов",
                IsChecked = modules.Contains(SpecModule.Grouping),
                Margin = new Thickness(0, 0, 0, 6),
            };
            _nested.Click += (s, e) => OnModulesChanged();
            _grouping.Click += (s, e) => OnModulesChanged();
            header.Children.Add(_nested);
            header.Children.Add(_grouping);
            _summary = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
            header.Children.Add(_summary);
            _hint = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed };
            header.Children.Add(_hint);
            var steps = new List<string>
            {
                "1. Посмотрите строки: «Было → Станет» — что запишется; предупреждения и ошибки — что не запишется и почему.",
                "2. «Пробный прогон» записывает и откатывает: модель не меняется, но видно, не переписывает ли значения другая надстройка.",
                "3. «Применить» включается после успешного пробного прогона с теми же модулями. В журнале отмены будет одна запись.",
            };
            if (document.IsWorkshared) steps.Add("Совместная работа: перед записью синхронизируйтесь; после — синхронизируйте с освобождением элементов.");
            header.Children.Add(new TextBlock { Text = string.Join(Environment.NewLine, steps), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            root.Children.Add(header);

            var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(filters, Dock.Top);
            _severityFilter = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
            _moduleFilter = new ComboBox { Width = 160, Margin = new Thickness(0, 0, 8, 0) };
            _search = new TextBox { Width = 240, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Поиск по тексту строки" };
            _counter = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };
            Button refresh = ReportWindow.MakeButton("Обновить", (s, e) => Send(SpecRequest.Refresh));
            refresh.ToolTip = "Заново проанализировать модель (после правок в ней)";
            filters.Children.Add(refresh);
            filters.Children.Add(_severityFilter);
            filters.Children.Add(_moduleFilter);
            filters.Children.Add(new TextBlock { Text = "Найти:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            filters.Children.Add(_search);
            filters.Children.Add(_counter);
            root.Children.Add(filters);

            var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            bottom.Children.Add(_status);
            _details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Foreground = Brushes.DimGray };
            bottom.Children.Add(new Expander { Header = "Сводка, правила и сообщения Revit", Content = _details });
            var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
            Button show = ReportWindow.MakeButton("Показать в модели", (s, e) => ShowSelected());
            show.ToolTip = "Выделить элементы выбранных строк в Revit (или двойной щелчок по строке)";
            _export = ReportWindow.MakeButton("Сохранить CSV…", (s, e) => ExportCsv());
            _export.ToolTip = "Таблица для Excel, UTF-8 — для сверки с Dynamo-скриптом";
            _trial = ReportWindow.MakeButton("Пробный прогон", (s, e) => Send(SpecRequest.Trial));
            _trial.ToolTip = "Записать и откатить. Модель не меняется.";
            _apply = ReportWindow.MakeButton("Применить", (s, e) => ConfirmApply());
            _apply.FontWeight = FontWeights.SemiBold;
            Button close = ReportWindow.MakeButton("Закрыть", (s, e) => Close());
            foreach (Button b in new[] { show, _export }) DockPanel.SetDock(b, Dock.Left);
            foreach (Button b in new[] { close, _apply, _trial }) DockPanel.SetDock(b, Dock.Right);
            buttons.Children.Add(show);
            buttons.Children.Add(_export);
            buttons.Children.Add(close);
            buttons.Children.Add(_apply);
            buttons.Children.Add(_trial);
            bottom.Children.Add(buttons);
            root.Children.Add(bottom);

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
            _grid.Columns.Add(ReportWindow.TextColumn("Модуль", nameof(CheckFinding.Check), 100));
            _grid.Columns.Add(ReportWindow.TextColumn("ElementId", nameof(CheckFinding.ElementIdText), 80));
            _grid.Columns.Add(ReportWindow.TextColumn("Категория", nameof(CheckFinding.Category), 110));
            _grid.Columns.Add(ReportWindow.TextColumn("Семейство", nameof(CheckFinding.Family), 130));
            _grid.Columns.Add(ReportWindow.TextColumn("Тип", nameof(CheckFinding.Type), 110));
            _grid.Columns.Add(ReportWindow.TextColumn("Сообщение", nameof(CheckFinding.Message), 330, wrap: true));
            _grid.Columns.Add(ReportWindow.TextColumn("Было", nameof(CheckFinding.Current), 100, wrap: true));
            _grid.Columns.Add(ReportWindow.TextColumn("Станет", nameof(CheckFinding.Expected), 110, wrap: true));
            _grid.Columns.Add(ReportWindow.TextColumn("Что сделать", nameof(CheckFinding.Action), 240, wrap: true));
            _grid.RowStyle = SeverityRowStyle();
            _grid.MouseDoubleClick += (s, e) => ShowSelected();
            root.Children.Add(_grid);

            _severityFilter.SelectionChanged += (s, e) => RefreshView();
            _moduleFilter.SelectionChanged += (s, e) => RefreshView();
            _search.TextChanged += (s, e) => RefreshView();
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) Close();
            };
            UpdateButtons();
        }

        public Autodesk.Revit.DB.Document Document => _document;

        /// <summary>Модули, отмеченные в окне (порядок записи — порядок перечисления).</summary>
        public List<SpecModule> SelectedModules()
        {
            var modules = new List<SpecModule>();
            if (_nested.IsChecked == true) modules.Add(SpecModule.Nested);
            if (_grouping.IsChecked == true) modules.Add(SpecModule.Grouping);
            return modules;
        }

        /// <summary>Новый анализ или итог прогона. <paramref name="after"/> — анализ после записи (должен быть пустым, REQ-SAFE-006).</summary>
        public void ShowPlan(ParamPlan plan, SpecRunResult? run, ParamPlan? after, CheckReport report)
        {
            _report = report;
            _rows = report.Findings;
            _shownFor = ModulesKey(plan.Modules);
            _writeBlocked = plan.WriteBlockedReason != null;
            _changes = (after ?? plan).Changes.Count;

            _severityFilter.Items.Clear();
            _severityFilter.Items.Add("Все строки");
            foreach (Severity s in new[] { Severity.Error, Severity.Warning, Severity.Info })
            {
                _severityFilter.Items.Add($"{s.ToRussian()} ({_rows.Count(f => f.Severity == s)})");
            }
            _severityFilter.SelectedIndex = 0;
            _moduleFilter.Items.Clear();
            _moduleFilter.Items.Add(AllModules);
            foreach (string module in _rows.Select(f => f.Check).Distinct()) _moduleFilter.Items.Add(module);
            _moduleFilter.SelectedIndex = 0;
            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = Matches;
            _grid.ItemsSource = _view;
            UpdateCounter();

            var summary = new List<string>();
            foreach (SpecModule m in plan.Modules)
            {
                string done = run?.Committed == true ? "записано" : "будет изменено";
                summary.Add($"{m.Title()}: {done} {plan.ChangeCount(m)}, без изменений {plan.Unchanged(m)}, "
                    + $"ошибок {plan.IssueCount(m, Severity.Error)}, предупреждений {plan.IssueCount(m, Severity.Warning)}");
            }
            _summary.Text = plan.Modules.Count == 0 ? "Отметьте хотя бы один модуль." : string.Join(". ", summary) + ".";
            if (after != null)
            {
                _summary.Text += after.Changes.Count == 0
                    ? " Повторный анализ после записи: изменений 0."
                    : $" Повторный анализ после записи: снова изменилось бы {after.Changes.Count} — значения переписывает кто-то другой, см. «Перезаписано».";
            }
            _hint.Text = plan.WriteBlockedReason == null ? string.Empty
                : "Запись запрещена: " + plan.WriteBlockedReason + " Проверьте папку правил отдела (VK Tools → Настройки → «Проверить»).";
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
                _status.Text = run.Status + (run.DisabledUpdaters.Count > 0 ? $" До перезапуска Revit отключено: {string.Join(", ", run.DisabledUpdaters)}." : string.Empty);
                _status.Foreground = run.Errors.Count > 0 ? Brushes.DarkRed : run.Cancelled ? Brushes.DimGray
                    : run.Failed.Count > 0 || run.Overwritten.Count > 0 ? Brushes.DarkOrange : Brushes.DarkGreen;
                _trialPassedFor = run.Mode == SpecRunMode.Trial && run.Passed ? _shownFor : null;
            }
            else
            {
                _trialPassedFor = null;
                _status.Text = $"Анализ модели: {DateTime.Now:HH:mm:ss}. Модель не изменялась.";
                _status.Foreground = Brushes.DimGray;
            }
            _busy = false;
            UpdateButtons();
        }

        public void ShowMessage(string text, bool error = true)
        {
            _busy = false;
            _status.Text = text;
            _status.Foreground = error ? Brushes.DarkRed : Brushes.DimGray;
            UpdateButtons();
        }

        private static string ModulesKey(IEnumerable<SpecModule> modules) => string.Join("+", modules.OrderBy(m => m));

        private void OnModulesChanged()
        {
            // Другие модули — другой план: старый пробный прогон не в счёт, анализ заново
            _trialPassedFor = null;
            if (SelectedModules().Count == 0)
            {
                UpdateButtons();
                return;
            }
            Send(SpecRequest.Refresh);
        }

        private void UpdateButtons()
        {
            string selected = ModulesKey(SelectedModules());
            bool current = selected.Length > 0 && selected == _shownFor;
            _trial.IsEnabled = !_busy && current && _changes > 0;
            _apply.IsEnabled = !_busy && current && _changes > 0 && !_writeBlocked && _trialPassedFor == selected;
            _apply.ToolTip = _writeBlocked ? "Запись запрещена: правила отдела недоступны или с ошибками"
                : _apply.IsEnabled ? "Записать одной операцией" : "Сначала успешный «Пробный прогон» с этими модулями";
            _export.IsEnabled = _report != null;
            _nested.IsEnabled = !_busy;
            _grouping.IsEnabled = !_busy;
        }

        private void ConfirmApply()
        {
            MessageBoxResult answer = MessageBox.Show(this,
                $"Записать изменения ({_changes})?\n\nЭто одна запись в журнале отмены: Ctrl+Z отменит её целиком.",
                "VK Tools", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.OK) Send(SpecRequest.Apply);
        }

        private void Send(SpecRequest request)
        {
            if (_busy) return;
            List<SpecModule> modules = SelectedModules();
            if (modules.Count == 0)
            {
                ShowMessage("Отметьте хотя бы один модуль.");
                return;
            }
            _busy = true;
            _status.Text = request == SpecRequest.Refresh ? "Анализ модели…" : request == SpecRequest.Trial ? "Пробный прогон…" : "Запись…";
            _status.Foreground = Brushes.DimGray;
            UpdateButtons();
            _handler.Set(this, request, modules);
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
            if (_moduleFilter.SelectedItem is string module && module != AllModules && f.Check != module) return false;
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
                FileName = ReportWindow.SafeFileName($"VKTools-параметры-{_report.DocumentTitle}-{_report.CreatedAt:yyyyMMdd-HHmm}.csv"),
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
