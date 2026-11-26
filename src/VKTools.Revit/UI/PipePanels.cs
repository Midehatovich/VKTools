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
    /// <summary>Вкладка «Настройка» модулей с таблицей в модели (изоляция, запас, имена труб): то, что окну нужно от неё.</summary>
    internal interface ISpecTablePanel
    {
        event EventHandler? Changed;

        bool HasErrors { get; }

        void Load(SpecOptions options, IEnumerable<ParamCandidate> candidates, IEnumerable<string> preview);

        void ApplyTo(SpecOptions options);

        void CommitEdits();
    }

    /// <summary>Общее у вкладок «Запас труб» и «Имена труб»: строка параметра, таблица CSV, превью снизу.</summary>
    internal abstract class PipeTablePanel : DockPanel, ISpecTablePanel
    {
        private readonly TextBox _preview;
        private readonly List<ParamOption> _options = new List<ParamOption>();

        protected PipeTablePanel(string fileName, string documentTitle, string help, IReadOnlyList<string> columns, string previewHeader)
        {
            Top = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            DockPanel.SetDock(Top, Dock.Top);
            Children.Add(Top);

            _preview = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 130,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 6, 0, 0),
            };
            var previewBox = new GroupBox { Header = previewHeader, Content = _preview };
            DockPanel.SetDock(previewBox, Dock.Bottom);
            Children.Add(previewBox);

            Table = new CsvGridPanel(fileName, documentTitle, help, columns, allowColumns: false);
            Table.Changed += (s, e) => RaiseChanged();
            Children.Add(Table);
        }

        public event EventHandler? Changed;

        protected StackPanel Top { get; }

        protected CsvGridPanel Table { get; }

        protected bool Loading { get; private set; }

        public bool HasErrors
        {
            get
            {
                var issues = new List<ConfigIssue>();
                Parse(Table.Text(), issues);
                return issues.Any(i => i.Severity == Core.Checks.Severity.Error);
            }
        }

        public void CommitEdits() => Table.CommitEdits();

        public void Load(SpecOptions options, IEnumerable<ParamCandidate> candidates, IEnumerable<string> preview)
        {
            Loading = true;
            try
            {
                _options.Clear();
                foreach (ParamCandidate c in candidates)
                {
                    if (_options.Any(o => o.Key.Equals(c.Key))) continue;
                    _options.Add(new ParamOption(c.Key, c.Nested > 0 ? $"{c.Name} ({c.KindText}, {c.DataType})" : $"{c.Name} ({c.KindText}, в модели не найден)"));
                }
                ParamKey? own = Param(options);
                if (own != null && !_options.Any(o => o.Key.Equals(own))) _options.Add(new ParamOption(own, $"{own.Name} ({own.KindText})"));
                _options.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Text, b.Text));
                ParamBox.ItemsSource = _options.ToList();
                ParamBox.SelectedItem = own == null ? null : _options.FirstOrDefault(o => o.Key.Equals(own));
                LoadOwn(options);
                string table = TableText(options);
                Table.Load(table);
                var issues = new List<ConfigIssue>();
                Parse(table, issues);
                Table.ShowIssues(issues);
                List<string> lines = preview.ToList();
                _preview.Text = lines.Count == 0 ? "Нажмите «Обновить»." : string.Join(Environment.NewLine, lines);
            }
            finally
            {
                Loading = false;
            }
        }

        public abstract void ApplyTo(SpecOptions options);

        protected abstract ComboBox ParamBox { get; }

        protected ParamKey? SelectedParam => (ParamBox.SelectedItem as ParamOption)?.Key;

        protected abstract ParamKey? Param(SpecOptions options);

        protected abstract string TableText(SpecOptions options);

        protected virtual void LoadOwn(SpecOptions options)
        {
        }

        protected abstract void Parse(string text, List<ConfigIssue> issues);

        protected void RaiseChanged()
        {
            if (Loading) return;
            var issues = new List<ConfigIssue>();
            Parse(Table.Text(), issues);
            Table.ShowIssues(issues);
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Вкладка «Настройка» окна «Запас труб»: параметр количества и таблица «Коэффициенты».</summary>
    internal sealed class PipeReservePanel : PipeTablePanel
    {
        private readonly ComboBox _qty;

        public PipeReservePanel(string documentTitle)
            : base("pipe_reserve.csv", documentTitle,
                "Коэффициенты: первая подходящая строка сверху. «Категория» — «Трубы» (жёсткие) или «Изоляция труб» (изоляция на жёстких трубах; "
                + "на фитингах и арматуре не трогается). «Маска типа» — часть имени типа или «Комментариев к типоразмеру» (с * — маска целиком), пусто — любой тип. "
                + "«Системы» — сокращения через запятую (можно с *), пусто — все. «Запас k»: 1,1 = +10 %; пусто — эти элементы не трогать.",
                PipeReserveSettings.Columns, "Системы → количество было → будет (по последнему «Обновить»)")
        {
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock { Text = "Количество = длина × k пишется в", Width = 220, VerticalAlignment = VerticalAlignment.Center });
            _qty = new ComboBox { Width = 360, ToolTip = "Обычно ADSK_Количество. Число — метры как есть, длина — длина; единицы проверяются по значениям в модели." };
            _qty.SelectionChanged += (s, e) => RaiseChanged();
            row.Children.Add(_qty);
            Top.Children.Add(row);
            Top.Children.Add(new TextBlock
            {
                Text = "Гибкие трубы (штуки), изоляция фитингов и элементы с «!» в начале наименования не трогаются. Если в модели работает BIM2B — "
                    + "у всех, кто правит трубы, в нём должен стоять тот же запас, иначе при правке он вернёт своё.",
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        protected override ComboBox ParamBox => _qty;

        protected override ParamKey? Param(SpecOptions options) => options.Reserve.QtyParam;

        protected override string TableText(SpecOptions options) => options.Reserve.Table;

        protected override void Parse(string text, List<ConfigIssue> issues) => PipeReserveSettings.ParseRules(text, issues);

        public override void ApplyTo(SpecOptions options)
        {
            PipeReserveSettings rsv = options.Reserve.Clone();
            rsv.QtyParam = SelectedParam;
            rsv.Table = Table.Text();
            options.Reserve = rsv;
        }
    }

    /// <summary>Вкладка «Настройка» окна «Имена труб»: писать ли, параметр и таблица «Правила».</summary>
    internal sealed class PipeNamesPanel : PipeTablePanel
    {
        private readonly CheckBox _write;
        private readonly ComboBox _name;

        public PipeNamesPanel(string documentTitle)
            : base("pipe_names.csv", documentTitle,
                "Правила: первая подходящая строка сверху. «Маска типа» — часть имени типа или «Комментариев к типоразмеру» (с * — маска целиком). "
                + "«Основа имени» пусто — «Комментарии к типоразмеру» типа трубы. В «Формате имени»: {base} — основа, {dn} — Ду (номинальный диаметр), "
                + "{od} — Dнар, {id} — Dвн, {s} — стенка (Dнар − Dвн)/2. Числа — с запятой, без лишних нулей. Пустой формат — тип не называется.",
                PipeNameSettings.Columns, "Типы труб в модели → правило → имя (по последнему «Обновить»)")
        {
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            _write = new CheckBox
            {
                Content = "Записывать имена труб в этом проекте, параметр",
                Width = 330,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Выключено — только проверка: расхождения в отчёте, модель не меняется. Включайте, когда BIM2B перестанет писать имена труб "
                    + "(или запускайте последним перед выпуском: BIM2B перепишет имя при следующей правке трубы).",
            };
            _write.Click += (s, e) => RaiseChanged();
            _name = new ComboBox { Width = 360 };
            _name.SelectionChanged += (s, e) => RaiseChanged();
            row.Children.Add(_write);
            row.Children.Add(_name);
            Top.Children.Add(row);
        }

        protected override ComboBox ParamBox => _name;

        protected override ParamKey? Param(SpecOptions options) => options.PipeNames.NameParam;

        protected override string TableText(SpecOptions options) => options.PipeNames.Rules;

        protected override void LoadOwn(SpecOptions options) => _write.IsChecked = options.PipeNames.WriteName;

        protected override void Parse(string text, List<ConfigIssue> issues) => PipeNameSettings.ParseRules(text, issues);

        public override void ApplyTo(SpecOptions options)
        {
            PipeNameSettings pn = options.PipeNames.Clone();
            pn.WriteName = _write.IsChecked == true;
            pn.NameParam = SelectedParam;
            pn.Rules = Table.Text();
            options.PipeNames = pn;
        }
    }

    /// <summary>Вкладка «Настройка» окна «Марки и поставщики»: перезапись, параметр наименования и таблица правил.</summary>
    internal sealed class VendorPanel : PipeTablePanel
    {
        private readonly CheckBox _overwrite;
        private readonly ComboBox _name;

        public VendorPanel(string documentTitle)
            : base("vendor.csv", documentTitle,
                "Правила: первая подходящая строка сверху. «Маска наименования» — часть наименования элемента или «Семейство : Тип» (с * — маска целиком, "
                + "например «*SML*», «Хомут соединительный*»). Марка, код, завод — что записать; пустая графа — не трогать. «<пусто>» — очистить графу (даже без галки «Перезаписывать»). Строка без всех трёх — "
                + "исключение (элемент не трогается). Наименования не меняются. Колонка «Примечание» — для себя.",
                VendorSettings.Columns, "Правила → сколько элементов; ниже — наименования без правила с пустыми графами (по последнему «Обновить»)")
        {
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock { Text = "Маску искать в", Width = 110, VerticalAlignment = VerticalAlignment.Center });
            _name = new ComboBox { Width = 360, ToolTip = "Обычно наименование (Р_Наименование или ADSK_Наименование); пустое у экземпляра — берётся у типа." };
            _name.SelectionChanged += (s, e) => RaiseChanged();
            row.Children.Add(_name);
            _overwrite = new CheckBox
            {
                Content = "Перезаписывать заполненные",
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Выключено — пишутся только пустые: марка из семейства (ADSK_) и ручная правка остаются. Включено — таблица главнее.",
            };
            _overwrite.Click += (s, e) => RaiseChanged();
            row.Children.Add(_overwrite);
            Top.Children.Add(row);
            Top.Children.Add(new TextBlock
            {
                Text = "Запускайте после «Имён труб»: маска ищется по готовому наименованию. Служебные позиции (_SYS_) не трогаются — их завод задаётся "
                    + "в «Расчётных позициях» (колонка «Завод-изготовитель» таблицы ресурсов).",
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        protected override ComboBox ParamBox => _name;

        protected override ParamKey? Param(SpecOptions options) => options.Vendor.NameParam;

        protected override string TableText(SpecOptions options) => options.Vendor.Rules;

        protected override void LoadOwn(SpecOptions options) => _overwrite.IsChecked = options.Vendor.Overwrite;

        protected override void Parse(string text, List<ConfigIssue> issues) => VendorSettings.ParseRules(text, issues);

        public override void ApplyTo(SpecOptions options)
        {
            VendorSettings vd = options.Vendor.Clone();
            vd.Overwrite = _overwrite.IsChecked == true;
            vd.NameParam = SelectedParam;
            vd.Rules = Table.Text();
            options.Vendor = vd;
        }
    }

    /// <summary>Вкладка «Настройка» окна «Позиции»: параметр позиции, подномера вложенных, таблица «Спецификации».</summary>
    internal sealed class PositionsPanel : PipeTablePanel
    {
        private readonly CheckBox _sub;
        private readonly ComboBox _param;

        public PositionsPanel(string documentTitle)
            : base("positions.csv", documentTitle,
                "Спецификации: первая подходящая строка сверху. «Спецификация» — часть имени или маска с * («*ниже 0,000*»), «*» — все. "
                + "«Нумерация»: «с 1» — свои номера 1, 2, 3… по порядку строк; «не трогать» — номера не меняются. Смотрятся только оформленные "
                + "(«ADSK_Назначение вида» = «Оформленные спецификации» или созданные кнопкой). «Примечание» — для себя.",
                PositionSettings.Columns, "Спецификации → строк, номера, сколько изменится (по последнему «Обновить»)")
        {
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            row.Children.Add(new TextBlock { Text = "Номер пишется в", Width = 110, VerticalAlignment = VerticalAlignment.Center });
            _param = new ComboBox { Width = 360, ToolTip = "Тот же параметр, что в графе «Позиция» спецификаций. Спецификация с другим параметром в графе не нумеруется." };
            _param.SelectionChanged += (s, e) => RaiseChanged();
            row.Children.Add(_param);
            _sub = new CheckBox
            {
                Content = "Вложенные детали — «номер родителя.N»",
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Фланцы и шпильки затвора, начинка шкафа: 39.1, 39.2… если все детали строки от одной строки-родителя. Выключено — все строки подряд.",
            };
            _sub.Click += (s, e) => RaiseChanged();
            row.Children.Add(_sub);
            Top.Children.Add(row);
            Top.Children.Add(new TextBlock
            {
                Text = "Запускайте последней, перед выпуском. Номера идут сверху вниз так, как строки стоят в таблице Revit; строки «!…» номер не занимают. "
                    + "Элемент в двух нумеруемых спецификациях получит два разных номера — это ошибка в отчёте, такую спецификацию поставьте «не трогать».",
                Foreground = Brushes.DimGray,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        protected override ComboBox ParamBox => _param;

        protected override ParamKey? Param(SpecOptions options) => options.Positions.PosParam;

        protected override string TableText(SpecOptions options) => options.Positions.Rules;

        protected override void LoadOwn(SpecOptions options) => _sub.IsChecked = options.Positions.SubNumbers;

        protected override void Parse(string text, List<ConfigIssue> issues) => PositionSettings.ParseRules(text, issues);

        public override void ApplyTo(SpecOptions options)
        {
            PositionSettings ps = options.Positions.Clone();
            ps.SubNumbers = _sub.IsChecked == true;
            ps.PosParam = SelectedParam;
            ps.Rules = Table.Text();
            options.Positions = ps;
        }
    }
}
