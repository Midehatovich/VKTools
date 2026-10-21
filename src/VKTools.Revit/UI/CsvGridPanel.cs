using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using VKTools.Core.Config;
using VKTools.Core.Text;
using VKTools.Revit.Infrastructure;
using Binding = System.Windows.Data.Binding;
using TextBox = System.Windows.Controls.TextBox;

namespace VKTools.Revit.UI
{
    /// <summary>Строка таблицы правил: значения по имени колонки (привязка «[Код]»).</summary>
    internal sealed class CsvRowVm : INotifyPropertyChanged
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        public event PropertyChangedEventHandler? PropertyChanged;

        public string this[string column]
        {
            get => _values.TryGetValue(column, out string? value) ? value : string.Empty;
            set
            {
                if (this[column] == (value ?? string.Empty)) return;
                _values[column] = value ?? string.Empty;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
            }
        }

        public void Remove(string column) => _values.Remove(column);
    }

    /// <summary>
    /// Таблица правил в окне: правка строк как в Excel, импорт и экспорт в формате файла папки отдела (тот же CSV, что
    /// читает плагин и Dynamo-эталон). Текст таблицы — то, что хранится в модели.
    /// </summary>
    internal sealed class CsvGridPanel : DockPanel
    {
        private static readonly char[] BadColumnChars = { ';', ',', '[', ']', '"' };
        private readonly ObservableCollection<CsvRowVm> _rows = new ObservableCollection<CsvRowVm>();
        private readonly List<string> _columns = new List<string>();
        private readonly DataGrid _grid;
        private readonly TextBlock _errors;
        private readonly TextBox _newColumn;
        private readonly string _fileName;
        private readonly string _documentTitle;
        private readonly IReadOnlyList<string> _required;
        private bool _loading;

        /// <param name="fileName">Имя файла папки отдела (calc_resources.csv) — для экспорта и подсказок.</param>
        /// <param name="required">Колонки, которые нельзя удалить.</param>
        /// <param name="allowColumns">Можно добавлять свои колонки (ресурсы: параметры позиции после «Основание»).</param>
        public CsvGridPanel(string fileName, string documentTitle, string help, IReadOnlyList<string> required, bool allowColumns)
        {
            _fileName = fileName;
            _documentTitle = documentTitle;
            _required = required;

            var top = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            DockPanel.SetDock(top, Dock.Top);
            top.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 6) });
            var buttons = new WrapPanel();
            buttons.Children.Add(ReportWindow.MakeButton("+ Строка", (s, e) => AddRow()));
            buttons.Children.Add(ReportWindow.MakeButton("Удалить", (s, e) => DeleteRow()));
            Button export = ReportWindow.MakeButton("Экспорт…", (s, e) => Export());
            export.ToolTip = $"Сохранить таблицу в файл формата {fileName}: для другого проекта или папки правил отдела";
            Button import = ReportWindow.MakeButton("Импорт…", (s, e) => Import());
            import.ToolTip = $"Загрузить таблицу из файла ({fileName}, экспорт другого проекта или файл Dynamo-набора)";
            buttons.Children.Add(export);
            buttons.Children.Add(import);
            _newColumn = new TextBox { Width = 200, Margin = new Thickness(12, 0, 6, 0), VerticalContentAlignment = VerticalAlignment.Center };
            if (allowColumns)
            {
                _newColumn.ToolTip = "Имя параметра позиции, например ADSK_Завод-изготовитель";
                buttons.Children.Add(NestedSettingsPanel.Label("Колонка-параметр:"));
                buttons.Children.Add(_newColumn);
                buttons.Children.Add(ReportWindow.MakeButton("+ Колонка", (s, e) => AddColumn()));
                Button remove = ReportWindow.MakeButton("− Колонка", (s, e) => RemoveColumn());
                remove.ToolTip = "Удалить колонку выбранной ячейки (обязательные колонки не удаляются)";
                buttons.Children.Add(remove);
            }
            top.Children.Add(buttons);
            Children.Add(top);

            _errors = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            DockPanel.SetDock(_errors, Dock.Bottom);
            Children.Add(_errors);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserSortColumns = false,
                CanUserReorderColumns = false,
                SelectionMode = DataGridSelectionMode.Single,
                SelectionUnit = DataGridSelectionUnit.CellOrRowHeader,
                HeadersVisibility = DataGridHeadersVisibility.All,
                GridLinesVisibility = DataGridGridLinesVisibility.All,
                HorizontalGridLinesBrush = Brushes.Gainsboro,
                VerticalGridLinesBrush = Brushes.Gainsboro,
                RowHeaderWidth = 14,
            };
            Children.Add(_grid);

            _rows.CollectionChanged += (s, e) =>
            {
                if (e.NewItems != null) foreach (CsvRowVm row in e.NewItems) row.PropertyChanged += OnRowEdited;
                if (e.OldItems != null) foreach (CsvRowVm row in e.OldItems) row.PropertyChanged -= OnRowEdited;
                OnEdited();
            };
        }

        public event EventHandler? Changed;

        /// <summary>Загрузить текст таблицы (из модели, умолчания или файла).</summary>
        public void Load(string text)
        {
            _loading = true;
            try
            {
                CsvTable csv = CsvTable.Parse((text ?? string.Empty).TrimStart('﻿'));
                _rows.Clear();
                _columns.Clear();
                _columns.AddRange(csv.Columns);
                foreach (string required in _required.Where(r => !_columns.Contains(r, StringComparer.Ordinal))) _columns.Add(required);
                foreach (CsvRow row in csv.Rows)
                {
                    var vm = new CsvRowVm();
                    for (int i = 0; i < csv.Columns.Count && i < row.Values.Count; i++) vm[csv.Columns[i]] = row.Values[i];
                    _rows.Add(vm);
                }
                RebuildColumns();
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>Текст таблицы: «#schema=1», заголовок, строки (пустые строки отбрасываются).</summary>
        public string Text()
        {
            IEnumerable<IReadOnlyList<string>> rows = _rows
                .Select(r => (IReadOnlyList<string>)_columns.Select(h => r[h].Trim()).ToList())
                .Where(r => r.Any(v => v.Length > 0));
            return CsvWriter.Table(Enumerable.Empty<string>(), 1, _columns, rows);
        }

        /// <summary>Зафиксировать ячейку, которую сейчас правят.</summary>
        public void CommitEdits()
        {
            _grid.CommitEdit(DataGridEditingUnit.Cell, true);
            _grid.CommitEdit(DataGridEditingUnit.Row, true);
        }

        /// <summary>Ошибки разбора этой таблицы (строки — как в файле: первая строка данных = 1).</summary>
        public void ShowIssues(IEnumerable<ConfigIssue> issues)
        {
            List<string> lines = issues.Select(i => i.Line.HasValue && i.Line.Value > 2 ? $"строка {i.Line.Value - 2}: {i.Message}" : i.Message).Distinct().ToList();
            _errors.Text = lines.Count == 0 ? string.Empty : "Исправьте таблицу: " + string.Join("; ", lines.Take(12)) + (lines.Count > 12 ? " …" : ".");
            _errors.Visibility = lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RebuildColumns()
        {
            _grid.ItemsSource = null;
            _grid.Columns.Clear();
            foreach (string column in _columns)
            {
                _grid.Columns.Add(new DataGridTextColumn
                {
                    Header = column,
                    Binding = new Binding("[" + column + "]") { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                    Width = new DataGridLength(ColumnWidth(column)),
                });
            }
            _grid.ItemsSource = _rows;
        }

        private static double ColumnWidth(string column) => column switch
        {
            "Наименование" => 260,
            "Основание" => 300,
            "Примечание" => 220,
            "Ресурсы" => 160,
            "Сегмент" => 180,
            _ => column.StartsWith("ADSK_", StringComparison.Ordinal) ? 200 : 95,
        };

        private void OnRowEdited(object? sender, PropertyChangedEventArgs e) => OnEdited();

        private void OnEdited()
        {
            if (_loading) return;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private void AddRow()
        {
            CommitEdits();
            var row = new CsvRowVm();
            int index = _grid.CurrentItem is CsvRowVm current ? _rows.IndexOf(current) : -1;
            _rows.Insert(index >= 0 ? index + 1 : _rows.Count, row);
            _grid.ScrollIntoView(row);
        }

        private void DeleteRow()
        {
            CommitEdits();
            if (_grid.CurrentItem is CsvRowVm row) _rows.Remove(row);
        }

        private void AddColumn()
        {
            CommitEdits();
            string name = _newColumn.Text.Trim();
            if (name.Length == 0) return;
            if (name.IndexOfAny(BadColumnChars) >= 0 || _columns.Contains(name, StringComparer.Ordinal))
            {
                MessageBox.Show(Window.GetWindow(this), $"Колонку «{name}» добавить нельзя: она уже есть или в имени недопустимые символы.", "VK Tools", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _columns.Add(name);
            RebuildColumns();
            _newColumn.Text = string.Empty;
            OnEdited();
        }

        private void RemoveColumn()
        {
            CommitEdits();
            string? name = _grid.CurrentColumn?.Header as string;
            if (name == null) return;
            if (_required.Contains(name, StringComparer.Ordinal))
            {
                MessageBox.Show(Window.GetWindow(this), $"«{name}» — обязательная колонка таблицы.", "VK Tools", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _columns.Remove(name);
            foreach (CsvRowVm row in _rows) row.Remove(name);
            RebuildColumns();
            OnEdited();
        }

        private void Export()
        {
            CommitEdits();
            var dialog = new SaveFileDialog
            {
                Filter = "Таблица правил (*.csv)|*.csv",
                FileName = ReportWindow.SafeFileName($"{Path.GetFileNameWithoutExtension(_fileName)}-{_documentTitle}.csv"),
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                string text = CsvWriter.Table(new[]
                {
                    $"VK Tools: расчётные позиции, формат {_fileName}.",
                    $"Экспорт из проекта «{_documentTitle}», {DateTime.Now:dd.MM.yyyy HH:mm}.",
                }, 1, _columns, CsvTable.Parse(Text()).Rows.Select(r => r.Values));
                TextFileReader.WriteUtf8WithBom(dialog.FileName, text);
                Host.Log.Info("Таблица сохранена: " + dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(Window.GetWindow(this), "Не удалось сохранить: " + ex.Message, "VK Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Import()
        {
            var dialog = new OpenFileDialog { Filter = "Таблица правил (*.csv)|*.csv|Все файлы|*.*" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                string text = TextFileReader.ReadAllText(dialog.FileName, out _);
                CsvTable csv = CsvTable.Parse(text.TrimStart('﻿'));
                List<string> missing = _required.Where(r => !csv.HasColumn(r)).ToList();
                if (csv.Columns.Count == 0 || missing.Count > 0)
                {
                    MessageBox.Show(Window.GetWindow(this), "Файл не загружен: " + (csv.Columns.Count == 0 ? "таблица не прочитана." : "нет колонок " + string.Join(", ", missing) + "."),
                        "VK Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Load(text);
                OnEdited();
                Host.Log.Info("Таблица загружена из файла: " + dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(Window.GetWindow(this), "Не удалось прочитать: " + ex.Message, "VK Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
