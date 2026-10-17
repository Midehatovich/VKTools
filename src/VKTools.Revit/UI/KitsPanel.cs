using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using VKTools.Core.Text;
using VKTools.Revit.Infrastructure;
using Binding = System.Windows.Data.Binding;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace VKTools.Revit.UI
{
    /// <summary>Строка списка семейств во вкладке «Комплекты».</summary>
    internal sealed class KitFamilyRow : INotifyPropertyChanged
    {
        private string _status = string.Empty;
        private string _where = string.Empty;
        private KitFamilyInfo? _info;

        public KitFamilyRow(string family)
        {
            Family = family;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Family { get; }

        /// <summary>Данные последнего анализа; null — семейства с вложенными в модели нет (осталось только правило).</summary>
        public KitFamilyInfo? Info
        {
            get => _info;
            set
            {
                if (ReferenceEquals(_info, value)) return;
                _info = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Category)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Group)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Instances)));
            }
        }

        public string Category => Info?.Category ?? string.Empty;

        public string Group => Info == null ? string.Empty : Info.Skipped ? "не трогать" : Info.Group ?? "—";

        public string Instances => Info == null ? "0" : Info.Instances.ToString(CultureInfo.CurrentCulture);

        public bool HasRule { get; set; }

        public bool Found { get; set; }

        public bool FromSelection { get; set; }

        public string Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }

        public string Where
        {
            get => _where;
            set
            {
                if (_where == value) return;
                _where = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Where)));
            }
        }
    }

    /// <summary>Строка состава: родитель (ранг .00, всегда в комплекте) или вложенное семейство.</summary>
    internal sealed class KitMemberRow : INotifyPropertyChanged
    {
        private bool _selected;
        private string _rank = string.Empty;

        public KitMemberRow(KitMember key, bool selected, bool isParent)
        {
            Key = key;
            _selected = selected;
            IsParent = isParent;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public KitMember Key { get; }

        public bool IsParent { get; }

        public bool CanToggle => !IsParent;

        public string Label => IsParent ? $"(родитель) {Key.Family}" : Key.Label;

        public string Types { get; set; } = string.Empty;

        public string Category { get; set; } = string.Empty;

        public string PerParent { get; set; } = string.Empty;

        public string Note { get; set; } = string.Empty;

        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value || IsParent) return;
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }

        public string Rank
        {
            get => _rank;
            set
            {
                if (_rank == value) return;
                _rank = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rank)));
            }
        }
    }

    /// <summary>
    /// Вкладка «Комплекты» окна «Группирование» (0.6.0, D-039): поиск кандидатов по словам, состав и порядок вложенных,
    /// свой состав типоразмера, номера K. Правит копию настроек; в модель — кнопками окна «Сохранить» и «Применить».
    /// </summary>
    internal sealed class KitsPanel : DockPanel
    {
        private const string AllTypes = "";

        private readonly ObservableCollection<KitFamilyRow> _families = new ObservableCollection<KitFamilyRow>();
        private readonly ObservableCollection<KitMemberRow> _members = new ObservableCollection<KitMemberRow>();
        private readonly ICollectionView _familyView;
        private readonly Dictionary<string, KitFamilyInfo> _infos = new Dictionary<string, KitFamilyInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _fromSelection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<KitNumber> _numbers = new List<KitNumber>();
        private readonly List<KitNumber> _newNumbers = new List<KitNumber>();
        private readonly string _documentTitle;
        private readonly TextBox _words;
        private readonly RadioButton _showFound;
        private readonly RadioButton _showAll;
        private readonly TextBlock _counter;
        private readonly DataGrid _familyGrid;
        private readonly DataGrid _memberGrid;
        private readonly TextBlock _memberTitle;
        private readonly ComboBox _typeCombo;
        private readonly Button _sameAsFamily;
        private readonly Button _deleteRule;
        private readonly Button _up;
        private readonly Button _down;
        private readonly TextBlock _memberHint;
        private readonly TextBlock _numbersText;
        private KitSettings _kits = new KitSettings();
        private string? _family;
        private string _type = AllTypes;
        private bool _loading;

        public KitsPanel(string documentTitle)
        {
            _documentTitle = documentTitle;
            var top = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };
            DockPanel.SetDock(top, Dock.Top);
            top.Children.Add(new TextBlock
            {
                Text = "Комплект встаёт в спецификации подряд: родитель «группа.K.00», отмеченные вложенные «группа.K.01», «.02»… по порядку списка, одинаковые вложенные — одной строкой. "
                    + "Группа — по таблице групп для родителя. K у каждого типоразмера свой: плагин присваивает его сам и хранит в модели, чтобы порядок не менялся между выпусками. "
                    + "Неотмеченные вложенные получают группу по таблице. Шкафы ПК собираются автоматически (Комплект_Номер).",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 0, 0, 6),
            });
            var search = new WrapPanel();
            search.Children.Add(NestedSettingsPanel.Label("Слова поиска:"));
            _words = new TextBox { Width = 360, Margin = new Thickness(0, 0, 12, 0), ToolTip = "Через «;». Ищутся в имени семейства и типоразмера, в текстовых параметрах типа и экземпляра." };
            search.Children.Add(_words);
            search.Children.Add(NestedSettingsPanel.Label("Показать:"));
            _showFound = new RadioButton { Content = "найденные и настроенные", GroupName = "kitsShow", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), IsChecked = true };
            _showAll = new RadioButton { Content = "все семейства с вложенными", GroupName = "kitsShow", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            search.Children.Add(_showFound);
            search.Children.Add(_showAll);
            Button fromSelection = ReportWindow.MakeButton("+ Из выделения", (s, e) => SelectionRequested?.Invoke(this, EventArgs.Empty));
            fromSelection.ToolTip = "Выделите в модели родителя (или его вложенное) и нажмите: семейство появится в списке";
            search.Children.Add(fromSelection);
            _counter = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray, Margin = new Thickness(12, 0, 0, 0) };
            search.Children.Add(_counter);
            top.Children.Add(search);
            Children.Add(top);

            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 90 });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.4, GridUnitType.Star), MinHeight = 140 });
            Children.Add(layout);

            _familyGrid = Table();
            _familyGrid.Columns.Add(ReportWindow.TextColumn("Статус", nameof(KitFamilyRow.Status), 150));
            _familyGrid.Columns.Add(ReportWindow.TextColumn("Семейство", nameof(KitFamilyRow.Family), 280));
            _familyGrid.Columns.Add(ReportWindow.TextColumn("Категория", nameof(KitFamilyRow.Category), 170));
            _familyGrid.Columns.Add(ReportWindow.TextColumn("Группа", nameof(KitFamilyRow.Group), 80));
            _familyGrid.Columns.Add(ReportWindow.TextColumn("Экз.", nameof(KitFamilyRow.Instances), 50));
            _familyGrid.Columns.Add(ReportWindow.TextColumn("Где найдено", nameof(KitFamilyRow.Where), 380, wrap: true));
            _familyView = CollectionViewSource.GetDefaultView(_families);
            _familyView.Filter = IsShown;
            _familyGrid.ItemsSource = _familyView;
            Grid.SetRow(_familyGrid, 0);
            layout.Children.Add(_familyGrid);
            var splitter = new GridSplitter { Height = 5, HorizontalAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent };
            Grid.SetRow(splitter, 1);
            layout.Children.Add(splitter);

            var composition = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            Grid.SetRow(composition, 2);
            layout.Children.Add(composition);
            var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(head, Dock.Top);
            _memberTitle = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            head.Children.Add(_memberTitle);
            head.Children.Add(NestedSettingsPanel.Label("для:"));
            _typeCombo = new ComboBox { Width = 260, Margin = new Thickness(0, 0, 8, 0), ToolTip = "Состав для всех типоразмеров или свой для одного" };
            head.Children.Add(_typeCombo);
            _sameAsFamily = ReportWindow.MakeButton("Как у семейства", (s, e) => ResetType());
            _sameAsFamily.ToolTip = "Убрать свой состав типоразмера: он возьмёт состав семейства";
            head.Children.Add(_sameAsFamily);
            _deleteRule = ReportWindow.MakeButton("Удалить комплект", (s, e) => DeleteRule());
            _deleteRule.ToolTip = "Убрать комплект семейства целиком (составы и номера K): элементы получат группы по таблице";
            head.Children.Add(_deleteRule);
            composition.Children.Add(head);
            _memberHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 4) };
            DockPanel.SetDock(_memberHint, Dock.Top);
            composition.Children.Add(_memberHint);

            var bottom = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(bottom, Dock.Bottom);
            _up = ReportWindow.MakeButton("▲", (s, e) => Move(-1));
            _down = ReportWindow.MakeButton("▼", (s, e) => Move(1));
            _up.MinWidth = 36;
            _down.MinWidth = 36;
            _up.ToolTip = "Выше в комплекте: ранг меньше";
            _down.ToolTip = "Ниже в комплекте: ранг больше";
            bottom.Children.Add(_up);
            bottom.Children.Add(_down);
            _numbersText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, MaxWidth = 700, Margin = new Thickness(8, 0, 12, 0) };
            bottom.Children.Add(_numbersText);
            Button export = ReportWindow.MakeButton("Экспорт…", (s, e) => Export());
            export.ToolTip = "Сохранить комплекты проекта (составы и K) в файл: для другой модели этого объекта";
            Button import = ReportWindow.MakeButton("Импорт…", (s, e) => Import());
            import.ToolTip = "Заменить комплекты в окне содержимым файла экспорта";
            bottom.Children.Add(export);
            bottom.Children.Add(import);
            composition.Children.Add(bottom);

            _memberGrid = Table();
            _memberGrid.Columns.Add(MemberCheckColumn());
            _memberGrid.Columns.Add(ReportWindow.TextColumn("Ранг", nameof(KitMemberRow.Rank), 50));
            _memberGrid.Columns.Add(ReportWindow.TextColumn("Вложенное семейство", nameof(KitMemberRow.Label), 300));
            _memberGrid.Columns.Add(ReportWindow.TextColumn("Типоразмеры", nameof(KitMemberRow.Types), 200, wrap: true));
            _memberGrid.Columns.Add(ReportWindow.TextColumn("Категория", nameof(KitMemberRow.Category), 160));
            _memberGrid.Columns.Add(ReportWindow.TextColumn("Шт. в 1 экз.", nameof(KitMemberRow.PerParent), 80));
            _memberGrid.Columns.Add(ReportWindow.TextColumn("Примечание", nameof(KitMemberRow.Note), 200, wrap: true));
            foreach (DataGridColumn column in _memberGrid.Columns.Skip(1)) column.IsReadOnly = true;
            _memberGrid.ItemsSource = _members;
            composition.Children.Add(_memberGrid);

            _words.TextChanged += (s, e) =>
            {
                if (_loading) return;
                _kits.Words = _words.Text;
                UpdateFamilyRows();
                RaiseChanged();
            };
            _showFound.Checked += (s, e) => RefreshFamilies();
            _showAll.Checked += (s, e) => RefreshFamilies();
            _familyGrid.SelectionChanged += (s, e) =>
            {
                if (_loading) return;
                ShowFamily((_familyGrid.SelectedItem as KitFamilyRow)?.Family, AllTypes);
            };
            _typeCombo.SelectionChanged += (s, e) =>
            {
                if (_loading || _family == null) return;
                ShowFamily(_family, (_typeCombo.SelectedItem as TypeItem)?.Name ?? AllTypes);
            };
            ShowFamily(null, AllTypes);
        }

        /// <summary>Пользователь изменил комплекты (не загрузка).</summary>
        public event EventHandler? Changed;

        /// <summary>«Из выделения»: окно читает выделение Revit и вызывает <see cref="AddFromSelection"/>.</summary>
        public event EventHandler? SelectionRequested;

        /// <summary>Комплекты, которые сейчас в окне (копия).</summary>
        public KitSettings Settings() => _kits.Clone();

        /// <summary>Показать комплекты из настройки (открытие окна, импорт).</summary>
        public void Load(KitSettings kits)
        {
            _kits = kits.Clone();
            _loading = true;
            try
            {
                _words.Text = _kits.Words;
            }
            finally
            {
                _loading = false;
            }
            UpdateFamilyRows();
            ShowFamily(_family, _type);
        }

        /// <summary>Данные последнего анализа: семейства с вложенными, номера K (новые сохранятся при «Применить»).</summary>
        public void UpdateData(IEnumerable<KitFamilyInfo> families, IEnumerable<KitNumber> numbers, IEnumerable<KitNumber> newNumbers)
        {
            _infos.Clear();
            foreach (KitFamilyInfo info in families) _infos[info.Family] = info;
            _numbers.Clear();
            _numbers.AddRange(numbers);
            _newNumbers.Clear();
            _newNumbers.AddRange(newNumbers);
            UpdateFamilyRows();
            ShowFamily(_family, _type);
        }

        /// <summary>
        /// Номера K, которые «Применить» сохранило в модели, — в окно: иначе окно сочло бы, что его настройка отличается от модели.
        /// Номер берётся, только если у типоразмера в окне номера нет и он не занят.
        /// </summary>
        public void AcceptNumbers(IEnumerable<KitNumber> stored)
        {
            KitSettings merged = _kits.WithNumbers(stored.Where(n => _kits.HasRule(n.Family)));
            if (merged.Numbers.Count == _kits.Numbers.Count) return;
            _kits = merged;
            _newNumbers.RemoveAll(n => _kits.NumberFor(n.Family, n.Type) != null);
            ShowNumbers();
        }

        /// <summary>Семейства выделенных элементов (верхний родитель) — в список. Возвращает сообщение для окна.</summary>
        public string AddFromSelection(IReadOnlyCollection<string> families)
        {
            if (families.Count == 0) return "В выделении нет экземпляров семейств. Выделите родителя или его вложенное и нажмите «+ Из выделения».";
            List<string> known = families.Where(f => _infos.ContainsKey(f)).ToList();
            List<string> unknown = families.Where(f => !_infos.ContainsKey(f)).ToList();
            foreach (string family in known) _fromSelection.Add(family);
            UpdateFamilyRows();
            if (known.Count > 0)
            {
                KitFamilyRow? row = _families.FirstOrDefault(r => string.Equals(r.Family, known[0], StringComparison.OrdinalIgnoreCase));
                if (row != null)
                {
                    _familyGrid.SelectedItem = row;
                    _familyGrid.ScrollIntoView(row);
                }
            }
            string text = known.Count > 0 ? "Добавлено в список: " + string.Join(", ", known) + "." : string.Empty;
            if (unknown.Count > 0)
            {
                text += (text.Length > 0 ? " " : string.Empty) + "Без общих вложенных (собирать не из чего, или элемент новый — нажмите «Обновить»): "
                    + string.Join(", ", unknown.Take(5)) + (unknown.Count > 5 ? " …" : string.Empty) + ".";
            }
            return text;
        }

        // ---------------------------------------------------------------- список семейств

        private void UpdateFamilyRows()
        {
            IReadOnlyList<string> words = _kits.WordList();
            var names = new SortedSet<string>(_infos.Keys, StringComparer.CurrentCultureIgnoreCase);
            foreach (KitRule rule in _kits.Rules) names.Add(rule.Family);
            string? selected = (_familyGrid.SelectedItem as KitFamilyRow)?.Family ?? _family;
            bool wasLoading = _loading;
            _loading = true;
            try
            {
                var rows = new List<KitFamilyRow>();
                foreach (string name in names)
                {
                    KitFamilyRow row = _families.FirstOrDefault(r => string.Equals(r.Family, name, StringComparison.OrdinalIgnoreCase)) ?? new KitFamilyRow(name);
                    row.Info = _infos.TryGetValue(name, out KitFamilyInfo? info) ? info : null;
                    row.HasRule = _kits.HasRule(name);
                    row.FromSelection = _fromSelection.Contains(name);
                    string? where = row.Info == null ? null : KitSearch.Find(row.Info, words);
                    row.Found = where != null;
                    row.Where = where ?? string.Empty;
                    row.Status = StatusOf(row);
                    rows.Add(row);
                }
                bool same = rows.Count == _families.Count && rows.Zip(_families, (a, b) => ReferenceEquals(a, b)).All(x => x);
                if (!same)
                {
                    _families.Clear();
                    foreach (KitFamilyRow row in rows) _families.Add(row);
                }
                _familyView.Refresh();
                KitFamilyRow? keep = selected == null ? null : _families.FirstOrDefault(r => string.Equals(r.Family, selected, StringComparison.OrdinalIgnoreCase));
                if (keep != null && IsShown(keep)) _familyGrid.SelectedItem = keep;
            }
            finally
            {
                _loading = wasLoading;
            }
            UpdateCounter();
        }

        private string StatusOf(KitFamilyRow row)
        {
            if (row.Info?.IsCabinet == true) return "шкаф ПК: авто";
            if (row.HasRule)
            {
                if (row.Info == null) return "комплект, нет в модели";
                int own = _kits.Rules.Count(r => r.IsTypeOverride && string.Equals(r.Family, row.Family, StringComparison.OrdinalIgnoreCase));
                return own > 0 ? $"комплект (свой состав: {own})" : "комплект";
            }
            if (row.FromSelection) return "из выделения";
            return row.Found ? "кандидат" : string.Empty;
        }

        private bool IsShown(object item)
        {
            var row = (KitFamilyRow)item;
            return _showAll.IsChecked == true || row.HasRule || row.Found || row.FromSelection || row.Info?.IsCabinet == true;
        }

        private void RefreshFamilies()
        {
            _familyView.Refresh();
            UpdateCounter();
        }

        private void UpdateCounter()
        {
            int shown = _familyView.Cast<object>().Count();
            int rules = _families.Count(r => r.HasRule);
            _counter.Text = $"Комплектов: {rules}; показано {shown} из {_families.Count} семейств с вложенными";
        }

        // ---------------------------------------------------------------- состав

        private void ShowFamily(string? family, string type)
        {
            _family = family;
            _type = type;
            bool wasLoading = _loading;
            _loading = true;
            try
            {
                foreach (KitMemberRow row in _members) row.PropertyChanged -= OnMemberChanged;
                _members.Clear();
                _typeCombo.Items.Clear();
                if (family == null)
                {
                    _memberTitle.Text = "Состав комплекта";
                    _memberHint.Text = "Выберите семейство в списке выше: ниже появятся его вложенные. Отмечайте их по порядку комплекта (а, б, в…): отмеченные встают вверх, ранги .01, .02 идут по списку. Порядок правится стрелками.";
                    UpdateMemberButtons();
                    ShowNumbers();
                    return;
                }
                _infos.TryGetValue(family, out KitFamilyInfo? info);
                _memberTitle.Text = $"Состав «{family}»";
                _typeCombo.Items.Add(new TypeItem(AllTypes, "все типоразмеры"));
                var typeNames = new List<string>();
                if (info != null) typeNames.AddRange(info.Types.Select(t => t.Name));
                typeNames.AddRange(_kits.Rules.Where(r => r.IsTypeOverride && string.Equals(r.Family, family, StringComparison.OrdinalIgnoreCase)).Select(r => r.Type));
                foreach (string name in typeNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, Comparer<string>.Create(GroupingTable.NaturalCompare)))
                {
                    bool own = _kits.TypeRule(family, name) != null;
                    int instances = info?.Types.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))?.Instances ?? 0;
                    _typeCombo.Items.Add(new TypeItem(name, $"{name} ({instances} шт.){(own ? " — свой состав" : string.Empty)}"));
                }
                _typeCombo.SelectedItem = _typeCombo.Items.Cast<TypeItem>().FirstOrDefault(t => string.Equals(t.Name, type, StringComparison.OrdinalIgnoreCase))
                    ?? _typeCombo.Items[0];
                _type = ((TypeItem)_typeCombo.SelectedItem).Name;

                KitRule? rule = _type.Length == 0 ? _kits.FamilyRule(family) : _kits.RuleFor(family, _type);
                Dictionary<string, NestedSummary> found = Summaries(info, _type);
                _members.Add(new KitMemberRow(new KitMember(family), true, true) { Rank = ".00", PerParent = "1", Category = info?.Category ?? string.Empty });
                if (rule != null)
                {
                    foreach (KitMember member in rule.Members)
                    {
                        var row = new KitMemberRow(member, true, false);
                        if (found.TryGetValue(member.Family, out NestedSummary? summary)) Fill(row, summary);
                        else row.Note = "нет в экземплярах" + (_type.Length > 0 ? " этого типоразмера" : string.Empty);
                        _members.Add(row);
                    }
                }
                foreach (NestedSummary summary in found.Values.OrderBy(v => v.Family, StringComparer.CurrentCultureIgnoreCase))
                {
                    var key = new KitMember(summary.Family);
                    if (rule != null && rule.Members.Any(m => m.SameAs(key))) continue;
                    var row = new KitMemberRow(key, false, false);
                    Fill(row, summary);
                    _members.Add(row);
                }
                foreach (KitMemberRow row in _members) row.PropertyChanged += OnMemberChanged;
                UpdateRanks();
                _memberGrid.IsEnabled = info?.IsCabinet != true;
                _memberHint.Text = Hint(family, info);
            }
            finally
            {
                _loading = wasLoading;
            }
            UpdateMemberButtons();
            ShowNumbers();
        }

        private string Hint(string family, KitFamilyInfo? info)
        {
            if (info?.IsCabinet == true) return "Шкаф ПК: комплект собирается автоматически по Комплект_Номер (kit_params.csv). Правило проекта для него не нужно.";
            var parts = new List<string>();
            if (info == null) parts.Add("Семейства нет в модели (или у его экземпляров нет общих вложенных): правило не действует. Удалите его или нажмите «Обновить».");
            else if (info.Skipped) parts.Add("В таблице групп у семейства «не трогать»: комплект не применится, пока строку не уберут.");
            if (_type.Length > 0)
            {
                parts.Add(_kits.TypeRule(family, _type) != null
                    ? $"У «{_type}» свой состав. «Как у семейства» вернёт общий."
                    : $"«{_type}» берёт состав семейства. Отметки здесь создадут ему свой состав.");
            }
            else if (!_kits.HasRule(family))
            {
                parts.Add("Отметьте вложенные, которые входят в комплект: семейство станет комплектом.");
            }
            return string.Join(" ", parts);
        }

        private static void Fill(KitMemberRow row, NestedSummary summary)
        {
            row.Types = string.Join(", ", summary.Types.Take(6)) + (summary.Types.Count > 6 ? " …" : string.Empty);
            row.Category = summary.Category;
            row.PerParent = summary.Min == summary.Max ? summary.Max.ToString(CultureInfo.CurrentCulture) : $"{summary.Min}–{summary.Max}";
        }

        /// <summary>Вложенные семейства родителя (всех типоразмеров или одного): типоразмеры, категория, сколько в одном экземпляре.</summary>
        private static Dictionary<string, NestedSummary> Summaries(KitFamilyInfo? info, string type)
        {
            var result = new Dictionary<string, NestedSummary>(StringComparer.OrdinalIgnoreCase);
            if (info == null) return result;
            List<KitTypeInfo> parentTypes = info.Types.Where(t => type.Length == 0 || string.Equals(t.Name, type, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (KitNestedInfo nested in info.Nested.Where(n => parentTypes.Any(t => string.Equals(t.Name, n.ParentType, StringComparison.Ordinal))))
            {
                if (!result.TryGetValue(nested.Family, out NestedSummary? summary))
                {
                    summary = new NestedSummary(nested.Family, nested.Category);
                    result[nested.Family] = summary;
                }
                if (!summary.Types.Contains(nested.Type)) summary.Types.Add(nested.Type);
                summary.Add(nested.ParentType, nested.MinPerParent, nested.MaxPerParent);
            }
            foreach (NestedSummary summary in result.Values) summary.Finish(parentTypes.Select(t => t.Name));
            return result;
        }

        private void OnMemberChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_loading || e.PropertyName != nameof(KitMemberRow.Selected) || !(sender is KitMemberRow row)) return;
            // Отмеченные — вверху по порядку отметки: отмечаешь а), б), в) — получаешь .01, .02, .03.
            // Перестановка после щелчка (не внутри него), но раньше следующего нажатия кнопки.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _memberGrid.CommitEdit(DataGridEditingUnit.Row, true);
                PlaceRow(row);
                CommitRule();
            }), DispatcherPriority.Normal);
        }

        /// <summary>Строку — сразу под отмеченными: отмеченная становится последней в комплекте, снятая — первой из неотмеченных.</summary>
        private void PlaceRow(KitMemberRow row)
        {
            int from = _members.IndexOf(row);
            if (from < 1) return;
            int target = 1 + _members.Count(r => !r.IsParent && r.Selected && !ReferenceEquals(r, row));
            if (target != from && target < _members.Count) _members.Move(from, target);
        }

        /// <summary>Отметки и порядок строк → состав в настройке (семейства или выбранного типоразмера).</summary>
        private void CommitRule()
        {
            if (_family == null) return;
            List<KitMember> members = _members.Where(r => !r.IsParent && r.Selected).Select(r => r.Key).ToList();
            if (members.Count > KitRule.MaxMembers)
            {
                _memberHint.Text = $"В комплекте больше {KitRule.MaxMembers} вложенных: ранг не помещается в два знака. Лишние не сохранятся.";
                members = members.Take(KitRule.MaxMembers).ToList();
            }
            bool hadOwn = _type.Length > 0 && _kits.TypeRule(_family, _type) != null;
            _kits.SetRule(new KitRule(_family, _type, members));
            UpdateRanks();
            bool hasOwn = _type.Length > 0 && _kits.TypeRule(_family, _type) != null;
            if (hadOwn != hasOwn)
            {
                ShowFamily(_family, _type);
            }
            else
            {
                _memberHint.Text = Hint(_family, _infos.TryGetValue(_family, out KitFamilyInfo? info) ? info : null);
                UpdateMemberButtons();
                ShowNumbers();
            }
            UpdateFamilyRows();
            RaiseChanged();
        }

        private void UpdateRanks()
        {
            int rank = 0;
            foreach (KitMemberRow row in _members.Where(r => !r.IsParent))
            {
                row.Rank = row.Selected ? "." + (++rank).ToString("00", CultureInfo.InvariantCulture) : string.Empty;
            }
        }

        private void Move(int delta)
        {
            _memberGrid.CommitEdit(DataGridEditingUnit.Row, true);
            if (!(_memberGrid.SelectedItem is KitMemberRow row) || row.IsParent) return;
            int index = _members.IndexOf(row);
            int target = index + delta;
            if (target < 1 || target >= _members.Count) return;
            _members.Move(index, target);
            _memberGrid.SelectedItem = row;
            _memberGrid.ScrollIntoView(row);
            if (row.Selected) CommitRule();
        }

        private void ResetType()
        {
            if (_family == null || _type.Length == 0) return;
            KitRule? own = _kits.TypeRule(_family, _type);
            if (own == null) return;
            _kits.Rules.Remove(own);
            ShowFamily(_family, _type);
            UpdateFamilyRows();
            RaiseChanged();
        }

        private void DeleteRule()
        {
            if (_family == null || !_kits.HasRule(_family)) return;
            MessageBoxResult answer = MessageBox.Show(Window.GetWindow(this),
                $"Удалить комплект «{_family}»: составы всех типоразмеров и номера K?\n\nВ модели — после «Сохранить» или «Применить». Элементы получат группы по таблице.",
                "VK Tools", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) return;
            _kits.RemoveFamily(_family);
            ShowFamily(_family, AllTypes);
            UpdateFamilyRows();
            RaiseChanged();
        }

        private void UpdateMemberButtons()
        {
            bool cabinet = _family != null && _infos.TryGetValue(_family, out KitFamilyInfo? info) && info.IsCabinet;
            _typeCombo.IsEnabled = _family != null && !cabinet;
            _sameAsFamily.IsEnabled = _family != null && _type.Length > 0 && _kits.TypeRule(_family, _type) != null;
            _deleteRule.IsEnabled = _family != null && _kits.HasRule(_family);
            _up.IsEnabled = _family != null && !cabinet;
            _down.IsEnabled = _up.IsEnabled;
        }

        private void ShowNumbers()
        {
            if (_family == null)
            {
                _numbersText.Text = string.Empty;
                return;
            }
            if (!_kits.HasRule(_family))
            {
                _numbersText.Text = string.Empty;
                return;
            }
            var shown = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KitNumber number in _numbers.Where(n => string.Equals(n.Family, _family, StringComparison.OrdinalIgnoreCase)))
            {
                int k = _kits.NumberFor(number.Family, number.Type) ?? number.K;
                bool fresh = _kits.NumberFor(number.Family, number.Type) == null;
                if (seen.Add(number.Type)) shown.Add($"{number.Type} → {k}{(fresh ? " (новый)" : string.Empty)}");
            }
            foreach (KitNumber number in _kits.Numbers.Where(n => string.Equals(n.Family, _family, StringComparison.OrdinalIgnoreCase)))
            {
                if (seen.Add(number.Type)) shown.Add($"{number.Type} → {number.K}");
            }
            _numbersText.Text = shown.Count == 0
                ? "K: присвоится при «Обновить»."
                : "K: " + string.Join(", ", shown) + (shown.Any(s => s.EndsWith("(новый)", StringComparison.Ordinal)) ? ". Новые сохранятся в модели при «Применить»." : ".");
        }

        // ---------------------------------------------------------------- файл

        private void Export()
        {
            if (_kits.Rules.Count == 0)
            {
                MessageBox.Show(Window.GetWindow(this), "Комплектов проекта нет: экспортировать нечего.", "VK Tools", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dialog = new SaveFileDialog
            {
                Filter = "Комплекты VK Tools (*.csv)|*.csv",
                FileName = ReportWindow.SafeFileName($"kits-{_documentTitle}.csv"),
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                var comments = KitSettings.FileComments.ToList();
                comments.Add($"Экспорт из проекта «{_documentTitle}», {DateTime.Now:dd.MM.yyyy HH:mm}.");
                TextFileReader.WriteUtf8WithBom(dialog.FileName, _kits.Format(comments));
                Host.Log.Info("Комплекты проекта сохранены: " + dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(Window.GetWindow(this), "Не удалось сохранить: " + ex.Message, "VK Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Import()
        {
            var dialog = new OpenFileDialog { Filter = "Комплекты VK Tools (*.csv)|*.csv|Все файлы|*.*" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                string text = TextFileReader.ReadAllText(dialog.FileName, out _);
                var issues = new List<ConfigIssue>();
                KitSettings? kits = KitSettings.Parse(text, Path.GetFileName(dialog.FileName), issues);
                List<ConfigIssue> errors = issues.Where(i => i.Severity == Severity.Error).ToList();
                if (kits == null || errors.Count > 0)
                {
                    MessageBox.Show(Window.GetWindow(this), "Файл не загружен:\n" + string.Join("\n", (errors.Count > 0 ? errors : issues).Take(10)), "VK Tools",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                kits.Words = _kits.Words;
                Load(kits);
                RaiseChanged();
                Host.Log.Info($"Комплекты проекта загружены из файла: {dialog.FileName} (правил {kits.Rules.Count}, номеров K {kits.Numbers.Count}).");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                MessageBox.Show(Window.GetWindow(this), "Не удалось прочитать: " + ex.Message, "VK Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------------------------------------------------------------- общее

        private void RaiseChanged()
        {
            if (_loading) return;
            UpdateCounter();
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static DataGrid Table() => new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            CanUserSortColumns = false,
            SelectionMode = DataGridSelectionMode.Single,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = Brushes.Gainsboro,
            RowHeaderWidth = 0,
        };

        private static DataGridTemplateColumn MemberCheckColumn()
        {
            var check = new FrameworkElementFactory(typeof(CheckBox));
            check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(KitMemberRow.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            check.SetBinding(IsEnabledProperty, new Binding(nameof(KitMemberRow.CanToggle)) { Mode = BindingMode.OneWay });
            check.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            return new DataGridTemplateColumn { Header = "В комплекте", CellTemplate = new DataTemplate { VisualTree = check }, Width = new DataGridLength(85) };
        }

        private sealed class TypeItem
        {
            public TypeItem(string name, string text)
            {
                Name = name;
                Text = text;
            }

            public string Name { get; }

            public string Text { get; }

            public override string ToString() => Text;
        }

        /// <summary>Вложенное семейство родителя: типоразмеры, категория и разброс «штук в одном экземпляре» по типоразмерам родителя.</summary>
        private sealed class NestedSummary
        {
            private readonly Dictionary<string, (int Min, int Max)> _byParentType = new Dictionary<string, (int Min, int Max)>(StringComparer.Ordinal);

            public NestedSummary(string family, string category)
            {
                Family = family;
                Category = category;
            }

            public string Family { get; }

            public string Category { get; }

            public List<string> Types { get; } = new List<string>();

            public int Min { get; private set; }

            public int Max { get; private set; }

            /// <summary>Разные типоразмеры вложенного в одном родителе складываются.</summary>
            public void Add(string parentType, int min, int max)
            {
                (int Min, int Max) current = _byParentType.TryGetValue(parentType, out (int Min, int Max) c) ? c : (0, 0);
                _byParentType[parentType] = (current.Min + min, current.Max + max);
            }

            public void Finish(IEnumerable<string> parentTypes)
            {
                List<(int Min, int Max)> values = parentTypes.Select(t => _byParentType.TryGetValue(t, out (int Min, int Max) v) ? v : (0, 0)).ToList();
                Min = values.Count == 0 ? 0 : values.Min(v => v.Min);
                Max = values.Count == 0 ? 0 : values.Max(v => v.Max);
            }
        }
    }
}
