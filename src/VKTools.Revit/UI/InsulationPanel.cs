using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

namespace VKTools.Revit.UI
{
    /// <summary>Вкладка «Настройка» окна «Изоляция»: что писать, параметры и таблица «Изделия» с превью имён.</summary>
    internal sealed class InsulationPanel : DockPanel
    {
        private const string AutoOd = "(найти по имени «U_Наружный диаметр»)";

        private readonly CheckBox _writeOd;
        private readonly ComboBox _odParam;
        private readonly CheckBox _writeName;
        private readonly ComboBox _nameParam;
        private readonly CheckBox _writeProduct;
        private readonly CsvGridPanel _products;
        private readonly TextBox _preview;
        private readonly List<ParamOption> _options = new List<ParamOption>();
        private bool _loading;

        public InsulationPanel(string documentTitle)
        {
            var top = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            DockPanel.SetDock(top, Dock.Top);

            var od = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            _writeOd = new CheckBox { Content = "Писать Dнар трубы в", Width = 190, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Dнар трубы-основы; у фитинга и арматуры — присоединённых труб (через ≤ 5 фитингов), на переходе — больший. Текст — «33,5», число — мм, длина — длина." };
            _odParam = new ComboBox { Width = 360 };
            od.Children.Add(_writeOd);
            od.Children.Add(_odParam);
            top.Children.Add(od);

            var name = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            _writeName = new CheckBox { Content = "Писать наименование в", Width = 190, VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "По «Формату имени» строки таблицы «Изделия». Если имя изоляции собирает BIM2B — уберите у него эту формулу, иначе они перепишут друг друга." };
            _nameParam = new ComboBox { Width = 360 };
            name.Children.Add(_writeName);
            name.Children.Add(_nameParam);
            top.Children.Add(name);

            _writeProduct = new CheckBox { Content = "Писать параметры изделия (колонки таблицы после «Ряд Dвн»: марка, завод-изготовитель…)", Margin = new Thickness(0, 0, 0, 4) };
            top.Children.Add(_writeProduct);
            Children.Add(top);

            _preview = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 110,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 6, 0, 0),
            };
            var previewBox = new GroupBox { Header = "Типы изоляции в модели → строка таблицы → имя (по последнему «Обновить»)", Content = _preview };
            DockPanel.SetDock(previewBox, Dock.Bottom);
            Children.Add(previewBox);

            _products = new CsvGridPanel("insulation_products.csv", documentTitle,
                "Изделия: первая подходящая строка сверху. «Маска типа» — часть имени типа или «Комментариев к типоразмеру» (с * — маска целиком). "
                + "«Основа имени» пусто — берётся «Комментарии к типоразмеру». В «Формате имени» и колонках-параметрах: {base} — основа, {t} — толщина, {d} — Dнар трубы, "
                + "{cyl} — Ø цилиндра по «Ряду Dвн». «Толщины» и «Ряд Dвн» — числа через пробел, для проверки. Колонки после «Ряд Dвн» — параметры изоляции "
                + "(добавить — «+ Колонка»): значение пишется в одноимённый параметр, а если у изоляции это параметр типа — в тип.",
                InsulationSettings.Columns, allowColumns: true);
            _products.Changed += (s, e) => RaiseChanged();
            Children.Add(_products);

            foreach (CheckBox box in new[] { _writeOd, _writeName, _writeProduct }) box.Click += (s, e) => RaiseChanged();
            _odParam.SelectionChanged += (s, e) => RaiseChanged();
            _nameParam.SelectionChanged += (s, e) => RaiseChanged();
        }

        public event EventHandler? Changed;

        public void Load(SpecOptions options, IEnumerable<ParamCandidate> candidates, IEnumerable<string> preview)
        {
            _loading = true;
            try
            {
                InsulationSettings ins = options.Insulation;
                _options.Clear();
                foreach (ParamCandidate c in candidates)
                {
                    if (_options.Any(o => o.Key.Equals(c.Key))) continue;
                    _options.Add(new ParamOption(c.Key, c.Nested > 0 ? $"{c.Name} ({c.KindText}, {c.DataType})" : $"{c.Name} ({c.KindText}, в модели не найден)"));
                }
                foreach (ParamKey? key in new[] { ins.OdParam, ins.NameParam })
                {
                    if (key != null && !_options.Any(o => o.Key.Equals(key))) _options.Add(new ParamOption(key, $"{key.Name} ({key.KindText})"));
                }
                _options.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Text, b.Text));
                var odItems = new List<object> { AutoOd };
                odItems.AddRange(_options);
                _odParam.ItemsSource = odItems;
                _odParam.SelectedItem = ins.OdParam == null ? AutoOd : (object?)_options.FirstOrDefault(o => o.Key.Equals(ins.OdParam));
                _nameParam.ItemsSource = _options.ToList();
                _nameParam.SelectedItem = ins.NameParam == null ? null : _options.FirstOrDefault(o => o.Key.Equals(ins.NameParam));
                _writeOd.IsChecked = ins.WriteOd;
                _writeName.IsChecked = ins.WriteName;
                _writeProduct.IsChecked = ins.WriteProduct;
                _products.Load(ins.Products);
                var issues = new List<ConfigIssue>();
                InsulationSettings.ParseProducts(ins.Products, issues);
                _products.ShowIssues(issues);
                List<string> lines = preview.ToList();
                _preview.Text = lines.Count == 0 ? "Нажмите «Обновить»." : string.Join(Environment.NewLine, lines);
            }
            finally
            {
                _loading = false;
            }
        }

        public bool HasErrors
        {
            get
            {
                var issues = new List<ConfigIssue>();
                InsulationSettings.ParseProducts(_products.Text(), issues);
                return issues.Any(i => i.Severity == Core.Checks.Severity.Error);
            }
        }

        public void CommitEdits() => _products.CommitEdits();

        public void ApplyTo(SpecOptions options)
        {
            InsulationSettings ins = options.Insulation.Clone();
            ins.WriteOd = _writeOd.IsChecked == true;
            ins.OdParam = (_odParam.SelectedItem as ParamOption)?.Key;
            ins.WriteName = _writeName.IsChecked == true;
            ins.NameParam = (_nameParam.SelectedItem as ParamOption)?.Key;
            ins.WriteProduct = _writeProduct.IsChecked == true;
            ins.Products = _products.Text();
            options.Insulation = ins;
        }

        private void RaiseChanged()
        {
            if (_loading) return;
            var issues = new List<ConfigIssue>();
            InsulationSettings.ParseProducts(_products.Text(), issues);
            _products.ShowIssues(issues);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
