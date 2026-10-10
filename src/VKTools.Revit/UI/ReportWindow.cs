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
using VKTools.Core.Text;
using VKTools.Revit.Infrastructure;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Binding = System.Windows.Data.Binding;
using Color = System.Windows.Media.Color;

namespace VKTools.Revit.UI
{
    /// <summary>
    /// Отчёт: таблица замечаний с фильтрами, экспорт в CSV и «Показать в модели».
    /// Немодальное: можно держать открытым и исправлять модель по строкам.
    /// Окно собрано в коде, без XAML (см. docs/ARCHITECTURE.md).
    /// </summary>
    internal sealed class ReportWindow : Window
    {
        private const string AllChecks = "Все проверки";

        private readonly CheckReport _report;
        private readonly Document _document;
        private readonly ExternalEvent _selectEvent;
        private readonly SelectElementsHandler _selectHandler;
        private readonly ExternalEvent _breakEvent;
        private readonly BreakJunctionsHandler _breakHandler;
        private readonly ICollectionView _view;
        private readonly DataGrid _grid;
        private readonly ComboBox _severityFilter;
        private readonly ComboBox _checkFilter;
        private readonly TextBox _search;
        private readonly TextBlock _counter;

        public ReportWindow(CheckReport report, Document document, ExternalEvent selectEvent, SelectElementsHandler selectHandler,
            ExternalEvent breakEvent, BreakJunctionsHandler breakHandler)
        {
            _report = report;
            _document = document;
            _selectEvent = selectEvent;
            _selectHandler = selectHandler;
            _breakEvent = breakEvent;
            _breakHandler = breakHandler;

            this.ApplyDefaultLook();
            Title = $"VK Tools — {report.Title}: {report.DocumentTitle}";
            Width = 1200;
            Height = 650;
            MinWidth = 700;
            MinHeight = 300;

            var root = new DockPanel { Margin = new Thickness(10) };
            Content = root;

            // Шапка: итог и откуда взяты правила
            var header = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(header, Dock.Top);
            header.Children.Add(new TextBlock
            {
                Text = report.Summary,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            header.Children.Add(new TextBlock
            {
                Text = $"{report.CreatedAt:dd.MM.yyyy HH:mm} · VK Tools {report.PluginVersion} · Revit {report.RevitVersion}",
                Foreground = Brushes.DimGray,
            });
            header.Children.Add(new Expander
            {
                Header = "Правила",
                Margin = new Thickness(0, 4, 0, 0),
                Content = new TextBox
                {
                    Text = string.Join(Environment.NewLine, report.RuleSources),
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.DimGray,
                },
            });
            root.Children.Add(header);

            // Фильтры
            var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(filters, Dock.Top);
            _severityFilter = new ComboBox { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
            _severityFilter.Items.Add("Все уровни");
            foreach (Severity s in new[] { Severity.Error, Severity.Warning, Severity.Info })
            {
                _severityFilter.Items.Add($"{s.ToRussian()} ({report.Count(s)})");
            }
            _severityFilter.SelectedIndex = 0;
            _checkFilter = new ComboBox { Width = 260, Margin = new Thickness(0, 0, 8, 0) };
            _checkFilter.Items.Add(AllChecks);
            foreach (string check in report.Findings.Select(f => f.Check).Distinct())
            {
                _checkFilter.Items.Add(check);
            }
            _checkFilter.SelectedIndex = 0;
            _search = new TextBox { Width = 260, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Поиск по тексту строки" };
            _counter = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray };
            filters.Children.Add(_severityFilter);
            filters.Children.Add(_checkFilter);
            filters.Children.Add(new TextBlock { Text = "Найти:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
            filters.Children.Add(_search);
            filters.Children.Add(_counter);
            root.Children.Add(filters);

            // Кнопки
            var buttons = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = false };
            DockPanel.SetDock(buttons, Dock.Bottom);
            Button show = MakeButton("Показать в модели", (s, e) => ShowSelected());
            show.ToolTip = "Выделить элементы выбранных строк в Revit (или двойной щелчок по строке)";
            Button export = MakeButton("Сохранить CSV…", (s, e) => ExportCsv());
            export.ToolTip = "Таблица для Excel, UTF-8";
            Button close = MakeButton("Закрыть", (s, e) => Close());
            DockPanel.SetDock(show, Dock.Left);
            DockPanel.SetDock(export, Dock.Left);
            DockPanel.SetDock(close, Dock.Right);
            buttons.Children.Add(show);
            if (report.Findings.Any(f => f.JunctionWith.HasValue))
            {
                Button breakJunction = MakeButton("Разорвать стык…", (s, e) => BreakSelected());
                breakJunction.ToolTip = "Для строк «Сеть … соединена с сетью …» и «Кольцо в сети …»: разъединить два элемента строки, не сдвигая их";
                DockPanel.SetDock(breakJunction, Dock.Left);
                buttons.Children.Add(breakJunction);
            }
            buttons.Children.Add(export);
            buttons.Children.Add(close);
            root.Children.Add(buttons);

            // Таблица
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
            };
            _grid.Columns.Add(TextColumn("Важность", nameof(CheckFinding.SeverityText), 105));
            _grid.Columns.Add(TextColumn("Проверка", nameof(CheckFinding.Check), 150));
            _grid.Columns.Add(TextColumn("ElementId", nameof(CheckFinding.ElementIdText), 80));
            _grid.Columns.Add(TextColumn("Категория", nameof(CheckFinding.Category), 110));
            _grid.Columns.Add(TextColumn("Семейство", nameof(CheckFinding.Family), 130));
            _grid.Columns.Add(TextColumn("Тип", nameof(CheckFinding.Type), 120));
            _grid.Columns.Add(TextColumn("Сообщение", nameof(CheckFinding.Message), 330, wrap: true));
            _grid.Columns.Add(TextColumn("Сейчас", nameof(CheckFinding.Current), 120, wrap: true));
            _grid.Columns.Add(TextColumn("Ожидается", nameof(CheckFinding.Expected), 120, wrap: true));
            _grid.Columns.Add(TextColumn("Что сделать", nameof(CheckFinding.Action), 260, wrap: true));
            _grid.RowStyle = SeverityRowStyle();
            _grid.MouseDoubleClick += (s, e) => ShowSelected();
            root.Children.Add(_grid);

            _view = CollectionViewSource.GetDefaultView(report.Findings);
            _view.Filter = Matches;
            _grid.ItemsSource = _view;

            _severityFilter.SelectionChanged += (s, e) => Refresh();
            _checkFilter.SelectionChanged += (s, e) => Refresh();
            _search.TextChanged += (s, e) => Refresh();
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) Close();
            };
            UpdateCounter();
        }

        internal static Button MakeButton(string text, RoutedEventHandler onClick)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(12, 3, 12, 3),
                Margin = new Thickness(0, 0, 8, 0),
                MinWidth = 90,
            };
            button.Click += onClick;
            return button;
        }

        internal static DataGridTextColumn TextColumn(string header, string path, double width, bool wrap = false)
        {
            var column = new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path) { Mode = BindingMode.OneWay },
                Width = new DataGridLength(width),
            };
            if (wrap)
            {
                var style = new Style(typeof(TextBlock));
                style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
                column.ElementStyle = style;
            }
            return column;
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
            trigger.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(color)));
            return trigger;
        }

        private bool Matches(object item)
        {
            var finding = (CheckFinding)item;
            int severityIndex = _severityFilter.SelectedIndex;
            if (severityIndex == 1 && finding.Severity != Severity.Error) return false;
            if (severityIndex == 2 && finding.Severity != Severity.Warning) return false;
            if (severityIndex == 3 && finding.Severity != Severity.Info) return false;

            if (_checkFilter.SelectedItem is string check && check != AllChecks && finding.Check != check) return false;

            string query = _search.Text.Trim();
            if (query.Length == 0) return true;
            return Contains(finding.Message, query) || Contains(finding.Category, query) || Contains(finding.Family, query)
                || Contains(finding.Type, query) || Contains(finding.Current, query) || Contains(finding.Expected, query)
                || Contains(finding.ElementIdText, query);
        }

        private static bool Contains(string text, string query) =>
            CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, query, CompareOptions.IgnoreCase) >= 0;

        private void Refresh()
        {
            _view.Refresh();
            UpdateCounter();
        }

        private void UpdateCounter()
        {
            int shown = _view.Cast<object>().Count();
            _counter.Text = shown == _report.Findings.Count
                ? $"Строк: {shown}"
                : $"Показано {shown} из {_report.Findings.Count}";
        }

        private void ShowSelected()
        {
            List<long> ids = _grid.SelectedItems.Cast<CheckFinding>().SelectMany(f => f.ElementsToShow()).ToList();
            if (ids.Count == 0)
            {
                MessageBox.Show(this, "В выбранных строках нет элементов модели.", "VK Tools", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _selectHandler.Request(_document, ids);
            _selectEvent.Raise();
        }

        private void BreakSelected()
        {
            List<CheckFinding> rows = _grid.SelectedItems.Cast<CheckFinding>().Where(f => f.ElementId.HasValue && f.JunctionWith.HasValue).ToList();
            if (rows.Count == 0)
            {
                MessageBox.Show(this, "Выделите строки «Сеть … соединена с сетью …» или «Кольцо в сети …» — для них есть место разрыва.", "VK Tools",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            List<(long A, long B)> pairs = rows.Select(f => (f.ElementId!.Value, f.JunctionWith!.Value)).Distinct().ToList();
            string list = string.Join(Environment.NewLine, rows.Take(10).Select(f => $"• {f.ElementId} ({f.Type}) ↔ {f.JunctionWith}"))
                + (rows.Count > 10 ? Environment.NewLine + $"… и ещё {rows.Count - 10}" : string.Empty);
            string text = $"Разорвать стыков: {pairs.Count}?" + Environment.NewLine + Environment.NewLine + list + Environment.NewLine + Environment.NewLine
                + "Разъединяются только коннекторы этих пар, элементы не сдвигаются. Это одна операция, Ctrl+Z её отменяет." + Environment.NewLine
                + "Подводный камень: на месте стыка останутся открытые концы, проверки Revit их покажут." + Environment.NewLine
                + "Затем — «Обновить» и пробный прогон.";
            if (MessageBox.Show(this, text, "VK Tools", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _breakHandler.Request(_document, pairs);
            _breakEvent.Raise();
        }

        private void ExportCsv()
        {
            var dialog = new SaveFileDialog
            {
                Filter = "CSV для Excel (*.csv)|*.csv",
                FileName = SafeFileName($"VKTools-{_report.DocumentTitle}-{_report.CreatedAt:yyyyMMdd-HHmm}.csv"),
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

        internal static string SafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }
            return name;
        }
    }

    /// <summary>Одно окно отчёта на сессию Revit: новая проверка заменяет старое окно.</summary>
    internal static class ReportWindowHost
    {
        private static ReportWindow? _current;
        private static ExternalEvent? _selectEvent;
        private static SelectElementsHandler? _selectHandler;
        private static ExternalEvent? _breakEvent;
        private static BreakJunctionsHandler? _breakHandler;

        /// <summary>Вызывать только из команды Revit (ExternalEvent создаётся в контексте API).</summary>
        public static void Show(CheckReport report, Document document, UIApplication app)
        {
            EnsureCreated();
            Show(report, document, app.MainWindowHandle);
        }

        /// <summary>ExternalEvent для «Показать в модели». Только в контексте Revit API: команда или обработчик события.</summary>
        public static void EnsureCreated()
        {
            if (_selectEvent == null || _selectHandler == null)
            {
                _selectHandler = new SelectElementsHandler();
                _selectEvent = ExternalEvent.Create(_selectHandler);
            }
            if (_breakEvent == null || _breakHandler == null)
            {
                _breakHandler = new BreakJunctionsHandler();
                _breakEvent = ExternalEvent.Create(_breakHandler);
            }
        }

        /// <summary>Из любого окна плагина; перед этим команда должна вызвать <see cref="EnsureCreated"/>.</summary>
        public static void Show(CheckReport report, Document document, IntPtr owner)
        {
            if (_selectEvent == null || _selectHandler == null || _breakEvent == null || _breakHandler == null)
            {
                throw new InvalidOperationException("Окно отчёта не подготовлено командой.");
            }

            _current?.Close();
            var window = new ReportWindow(report, document, _selectEvent, _selectHandler, _breakEvent, _breakHandler);
            window.AttachTo(owner);
            window.Closed += (s, e) =>
            {
                if (ReferenceEquals(_current, s)) _current = null;
            };
            _current = window;
            window.Show();
        }

        public static void CloseAll()
        {
            _current?.Close();
            _current = null;
            _selectEvent?.Dispose();
            _selectEvent = null;
            _breakEvent?.Dispose();
            _breakEvent = null;
        }
    }
}
