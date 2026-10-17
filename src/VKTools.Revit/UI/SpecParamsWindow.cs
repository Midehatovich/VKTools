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
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using VKTools.Core.Checks;
using VKTools.Core.Spec;
using VKTools.Core.Text;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Spec;
using Binding = System.Windows.Data.Binding;
using Color = System.Windows.Media.Color;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Visibility = System.Windows.Visibility;

namespace VKTools.Revit.UI
{
    /// <summary>
    /// Окно модуля Р2: «Перенос во вложенные» или «Группирование». Вкладка настроек (что переносить / таблица групп)
    /// и вкладка «Было → станет»; анализ, пробный прогон, запись. Немодальное, все действия с моделью — через ExternalEvent,
    /// перед каждым действием анализ строится заново с настройками, которые сейчас в окне.
    /// </summary>
    internal sealed class SpecParamsWindow : Window
    {
        private readonly Autodesk.Revit.DB.Document _document;
        private readonly SpecModule _module;
        private readonly SpecParamsHandler _handler;
        private readonly ExternalEvent _event;
        private readonly SelectElementsHandler _selectHandler;
        private readonly ExternalEvent _selectEvent;
        private readonly NestedSettingsPanel? _nestedPanel;
        private readonly GroupSchemePanel? _groupPanel;
        private readonly TabControl _tabs;
        private readonly TabItem _settingsTab;
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
        private SpecOptions _base;
        private SpecDefaults _defaultsData;
        private string _savedSignature;
        private List<CheckFinding> _rows = new List<CheckFinding>();
        private ICollectionView? _view;
        private CheckReport? _report;
        private string? _trialPassedFor;
        private string? _shownFor;
        private List<ElementId>? _scopeIds;
        private bool _writeBlocked;
        private int _changes;
        private bool _busy;

        public SpecParamsWindow(Autodesk.Revit.DB.Document document, SpecOptions options, SpecDefaults defaults, SpecParamsHandler handler, ExternalEvent externalEvent,
            SelectElementsHandler selectHandler, ExternalEvent selectEvent)
        {
            _document = document;
            _module = options.Module;
            _handler = handler;
            _event = externalEvent;
            _selectHandler = selectHandler;
            _selectEvent = selectEvent;
            _base = options.Clone();
            _defaultsData = defaults;
            _savedSignature = options.SettingsSignature();
            bool nested = _module == SpecModule.Nested;

            this.ApplyDefaultLook();
            Title = $"VK Tools — {(nested ? "перенос во вложенные" : "группирование")}: {document.Title}";
            Width = 1250;
            Height = 760;
            MinWidth = 820;
            MinHeight = 420;

            var root = new DockPanel { Margin = new Thickness(10) };
            Content = root;

            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(header, Dock.Top);
            header.Children.Add(new TextBlock
            {
                Text = nested
                    ? "Значения верхнего родителя → его общие вложенные семейства (все уровни). Какие параметры — на вкладке «Параметры»."
                    : "ADSK_Группирование по таблице групп: категории, семейства, «не трогать»; комплекты пожарных шкафов — «группа.ключ».",
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
                "1. Настройте и нажмите «Обновить»: на вкладке «Было → станет» — что запишется, а что нет и почему.",
                "2. «Пробный прогон» записывает и откатывает: модель не меняется. 3. «Применить» — после успешного пробного прогона с теми же настройками; одна запись в журнале отмены.",
                "Настройка хранится в модели: «Сохранить» или «Применить» (если она изменена). Без неё работают правила по умолчанию.",
            };
            if (document.IsWorkshared) steps.Add("Совместная работа: перед записью синхронизируйтесь; после — синхронизируйте с освобождением элементов, тогда коллеги увидят и значения, и настройку.");
            header.Children.Add(new TextBlock { Text = string.Join(Environment.NewLine, steps), Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
            root.Children.Add(header);

            var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            _status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            bottom.Children.Add(_status);
            _details = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Foreground = Brushes.DimGray };
            bottom.Children.Add(new Expander { Header = "Сводка, правила и сообщения Revit", Content = _details });
            var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
            _defaults = ReportWindow.MakeButton("По умолчанию", (s, e) => ResetToDefaults());
            _defaults.ToolTip = nested
                ? "Отметить параметры из nested_params.csv (папка отдела или встроенные). В модели — после «Сохранить»."
                : "Таблица групп из grouping.csv (папка отдела или встроенная), как в 0.4.0. В модели — после «Сохранить»: настройка проекта удалится.";
            _save = ReportWindow.MakeButton("Сохранить", (s, e) => Send(SpecRequest.Save));
            _save.ToolTip = "Сохранить настройку в модели отдельной операцией. Значения элементов не меняются.";
            _refresh = ReportWindow.MakeButton("Обновить", (s, e) => Send(SpecRequest.Refresh));
            _refresh.ToolTip = "Заново проанализировать модель с настройками окна";
            _trial = ReportWindow.MakeButton("Пробный прогон", (s, e) => Send(SpecRequest.Trial));
            _trial.ToolTip = "Записать и откатить. Модель не меняется.";
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

            _tabs = new TabControl();
            if (nested)
            {
                _nestedPanel = new NestedSettingsPanel { Margin = new Thickness(6) };
                _nestedPanel.Load(options, Array.Empty<ParamCandidate>());
                _nestedPanel.Changed += (s, e) => OnSettingsChanged();
                _settingsTab = new TabItem { Header = "Параметры", Content = _nestedPanel };
            }
            else
            {
                _groupPanel = new GroupSchemePanel(document.Title) { Margin = new Thickness(6) };
                _groupPanel.Load(options.GroupRules);
                _groupPanel.Changed += (s, e) => OnSettingsChanged();
                _settingsTab = new TabItem { Header = "Таблица групп", Content = _groupPanel };
            }

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
            _grid.Columns.Add(ReportWindow.TextColumn("Категория", nameof(CheckFinding.Category), 120));
            _grid.Columns.Add(ReportWindow.TextColumn("Семейство", nameof(CheckFinding.Family), 140));
            _grid.Columns.Add(ReportWindow.TextColumn("Тип", nameof(CheckFinding.Type), 110));
            _grid.Columns.Add(ReportWindow.TextColumn("Сообщение", nameof(CheckFinding.Message), 340, wrap: true));
            _grid.Columns.Add(ReportWindow.TextColumn("Было", nameof(CheckFinding.Current), 100, wrap: true));
            _grid.Columns.Add(ReportWindow.TextColumn("Станет", nameof(CheckFinding.Expected), 110, wrap: true));
            _grid.Columns.Add(ReportWindow.TextColumn("Что сделать", nameof(CheckFinding.Action), 240, wrap: true));
            _grid.RowStyle = SeverityRowStyle();
            _grid.MouseDoubleClick += (s, e) => ShowSelected();
            report.Children.Add(_grid);
            _reportTab = new TabItem { Header = "Было → станет", Content = report };

            _tabs.Items.Add(_settingsTab);
            _tabs.Items.Add(_reportTab);
            _tabs.SelectedItem = nested ? _settingsTab : _reportTab;
            root.Children.Add(_tabs);

            _severityFilter.SelectionChanged += (s, e) => RefreshView();
            _search.TextChanged += (s, e) => RefreshView();
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && !(e.OriginalSource is TextBox) && !(e.OriginalSource is DataGridCell)) Close();
            };
            UpdateState();
            UpdateButtons();
        }

        public Autodesk.Revit.DB.Document Document => _document;

        public SpecModule Module => _module;

        /// <summary>Настройки, которые сейчас в окне (копия: окно может меняться, пока Revit работает).</summary>
        public SpecOptions CurrentOptions()
        {
            SpecOptions options = _base.Clone();
            _nestedPanel?.ApplyTo(options);
            if (_groupPanel != null) options.GroupRules = _groupPanel.Rules();
            return options;
        }

        /// <summary>Что сейчас сохранено в модели (после открытия, «Сохранить», «Применить» и каждого анализа).</summary>
        public void ShowSettingsState(SpecOptions stored, SpecDefaults defaults)
        {
            _defaultsData = defaults;
            _savedSignature = stored.SettingsSignature();
            _base.SavedInProject = stored.SavedInProject;
            _base.SavedStamp = stored.SavedStamp;
            UpdateState();
            UpdateButtons();
        }

        /// <summary>
        /// Новый анализ или итог прогона. <paramref name="used"/> — настройки, с которыми он построен; <paramref name="scopeIds"/> —
        /// элементы области (их же получат пробный прогон и запись). <paramref name="after"/> — анализ после записи (должен быть пустым, REQ-SAFE-006).
        /// </summary>
        public void ShowPlan(ParamPlan plan, SpecRunResult? run, ParamPlan? after, CheckReport report, SpecOptions used, List<ElementId>? scopeIds, string scopeLabel)
        {
            _report = report;
            _rows = report.Findings;
            _shownFor = used.Signature();
            _scopeIds = scopeIds;
            _writeBlocked = plan.WriteBlockedReason != null;
            _changes = (after ?? plan).Changes.Count;

            if (_nestedPanel != null && run == null) _nestedPanel.Load(used, plan.Candidates);
            _groupPanel?.UpdateLists(plan.FamilyNames, plan.Categories);

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

            string done = run?.Committed == true ? "записано" : "будет изменено";
            _summary.Text = $"{(_module == SpecModule.Nested ? "Область: " + scopeLabel + ". " : string.Empty)}"
                + $"{char.ToUpper(done[0], CultureInfo.CurrentCulture)}{done.Substring(1)} {plan.ChangeCount(_module)}, без изменений {plan.Unchanged(_module)}, "
                + $"ошибок {plan.IssueCount(_module, Severity.Error)}, предупреждений {plan.IssueCount(_module, Severity.Warning)}.";
            if (after != null)
            {
                _summary.Text += after.Changes.Count == 0
                    ? " Повторный анализ после записи: изменений 0."
                    : $" Повторный анализ после записи: снова изменилось бы {after.Changes.Count} — значения переписывает кто-то другой, см. «Перезаписано».";
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
                    + (run.Diagnoses.Count > 0 ? $" Причины «Не записано» по семействам: {run.Diagnoses.Count} — в начале вкладки «Было → станет»." : string.Empty)
                    + (run.DisabledUpdaters.Count > 0 ? $" До перезапуска Revit отключено: {string.Join(", ", run.DisabledUpdaters)}." : string.Empty);
                _status.Foreground = run.Errors.Count > 0 ? Brushes.DarkRed : run.Cancelled ? Brushes.DimGray
                    : run.Failed.Count > 0 || run.Overwritten.Count > 0 ? Brushes.DarkOrange : Brushes.DarkGreen;
                _trialPassedFor = run.Mode == SpecRunMode.Trial && run.Passed ? _shownFor : null;
                _tabs.SelectedItem = _reportTab;
            }
            else
            {
                _trialPassedFor = null;
                _status.Text = $"Анализ модели: {DateTime.Now:HH:mm:ss}. Модель не изменялась.";
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

        private bool SettingsDirty() => CurrentOptions().SettingsSignature() != _savedSignature;

        private bool IsCurrent() => _shownFor != null && CurrentOptions().Signature() == _shownFor;

        private void OnSettingsChanged()
        {
            UpdateState();
            UpdateButtons();
        }

        private void UpdateState()
        {
            bool dirty = SettingsDirty();
            string source = _module == SpecModule.Nested ? _defaultsData.NestedSource : _defaultsData.GroupingSource;
            int comma = source.IndexOf(", ", StringComparison.Ordinal);
            if (comma > 0) source = source.Substring(0, comma);
            string text;
            if (_defaultsData.ProjectBlocked != null)
            {
                text = "Настройка проекта: " + _defaultsData.ProjectBlocked;
                _state.Foreground = Brushes.DarkRed;
            }
            else
            {
                text = _base.SavedInProject ? "Настройка проекта: " + (_base.SavedStamp ?? "сохранена в модели") : $"Настройки по умолчанию ({source}): в модели своей настройки нет.";
                _state.Foreground = Brushes.DimGray;
            }
            if (dirty)
            {
                text += " В окне изменено, в модели не сохранено.";
                if (_defaultsData.ProjectBlocked == null) _state.Foreground = Brushes.DarkOrange;
            }
            if (_shownFor != null && !IsCurrent())
            {
                text += " Анализ устарел — нажмите «Обновить».";
            }
            _state.Text = text;
        }

        private void UpdateButtons()
        {
            bool errors = _groupPanel?.HasErrors == true;
            bool current = !errors && IsCurrent();
            bool dirty = SettingsDirty();
            _refresh.IsEnabled = !_busy && !errors;
            _refresh.FontWeight = _shownFor != null && !current && !errors ? FontWeights.SemiBold : FontWeights.Normal;
            _trial.IsEnabled = !_busy && current && _changes > 0;
            _trial.ToolTip = current ? "Записать и откатить. Модель не меняется." : "Настройки изменились: сначала «Обновить»";
            _apply.IsEnabled = !_busy && current && _changes > 0 && !_writeBlocked && _trialPassedFor == _shownFor;
            _apply.ToolTip = _writeBlocked ? "Запись запрещена: см. красную строку вверху"
                : _apply.IsEnabled ? "Записать одной операцией" : "Сначала успешный «Пробный прогон» с этими настройками";
            _save.IsEnabled = !_busy && !errors && (dirty || _defaultsData.ProjectBlocked != null);
            _defaults.IsEnabled = !_busy;
            _export.IsEnabled = _report != null;
            if (_nestedPanel != null) _nestedPanel.IsEnabled = !_busy;
            if (_groupPanel != null) _groupPanel.IsEnabled = !_busy;
        }

        private void ResetToDefaults()
        {
            if (_nestedPanel != null)
            {
                _nestedPanel.SelectDefaults(_defaultsData);
            }
            else if (_groupPanel != null)
            {
                _groupPanel.Load(_defaultsData.GroupRules);
                OnSettingsChanged();
            }
            _tabs.SelectedItem = _settingsTab;
            ShowMessage(SettingsDirty()
                ? "В окне — настройки по умолчанию. «Обновить» покажет, что изменится; «Сохранить» уберёт настройку проекта из модели."
                : "В окне уже настройки по умолчанию.", error: false);
        }

        private void ConfirmApply()
        {
            string settings = SettingsDirty() ? "\n\nНастройка из окна сохранится в модели той же операцией." : string.Empty;
            MessageBoxResult answer = MessageBox.Show(this,
                $"Записать изменения ({_changes})?{settings}\n\nЭто одна запись в журнале отмены: Ctrl+Z отменит её целиком.",
                "VK Tools", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.OK) Send(SpecRequest.Apply);
        }

        private void Send(SpecRequest request)
        {
            if (_busy) return;
            _groupPanel?.CommitEdits();
            if (_groupPanel?.HasErrors == true)
            {
                _tabs.SelectedItem = _settingsTab;
                ShowMessage("В таблице групп ошибки (под таблицей): исправьте их, потом «Обновить».");
                return;
            }
            SpecOptions options = CurrentOptions();
            if ((request == SpecRequest.Trial || request == SpecRequest.Apply) && options.Signature() != _shownFor)
            {
                ShowMessage("Настройки изменились после анализа: сначала «Обновить».");
                return;
            }
            _busy = true;
            _status.Text = request == SpecRequest.Refresh ? "Анализ модели…" : request == SpecRequest.Trial ? "Пробный прогон…"
                : request == SpecRequest.Save ? "Сохранение настройки…" : "Запись…";
            _status.Foreground = Brushes.DimGray;
            UpdateButtons();
            _handler.Set(this, request, options, request == SpecRequest.Refresh ? null : _scopeIds);
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
            string module = _module == SpecModule.Nested ? "перенос" : "группирование";
            var dialog = new SaveFileDialog
            {
                Filter = "CSV для Excel (*.csv)|*.csv",
                FileName = ReportWindow.SafeFileName($"VKTools-{module}-{_report.DocumentTitle}-{_report.CreatedAt:yyyyMMdd-HHmm}.csv"),
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
