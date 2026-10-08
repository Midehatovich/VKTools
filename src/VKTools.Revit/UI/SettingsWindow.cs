using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VKTools.Core.Config;
using VKTools.Core.Settings;
using VKTools.Revit.Infrastructure;
using Grid = System.Windows.Controls.Grid;
using TextBox = System.Windows.Controls.TextBox;

namespace VKTools.Revit.UI
{
    /// <summary>Настройки пользователя: папка правил отдела и срок хранения логов.</summary>
    internal sealed class SettingsWindow : Window
    {
        private readonly UserSettings _settings;
        private readonly TextBox _folder;
        private readonly TextBox _retention;
        private readonly TextBlock _checkResult;

        public SettingsWindow(UserSettings settings)
        {
            _settings = settings;
            this.ApplyDefaultLook();
            Title = "VK Tools — настройки";
            Width = 640;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;

            var panel = new StackPanel { Margin = new Thickness(12) };
            Content = panel;

            panel.Children.Add(new TextBlock { Text = "Папка правил отдела", FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock
            {
                Text = "Сетевая папка с CSV-правилами. Файл оттуда заменяет встроенный целиком. Пусто — только встроенные правила.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 2, 0, 4),
            });
            var folderRow = new DockPanel();
            var checkButton = new Button { Content = "Проверить", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(6, 0, 0, 0) };
            checkButton.Click += (s, e) => CheckFolder();
            DockPanel.SetDock(checkButton, Dock.Right);
            folderRow.Children.Add(checkButton);
            _folder = new TextBox { Text = settings.DepartmentRulesFolder, ToolTip = @"Например \\server\BIM\VKTools\config" };
            folderRow.Children.Add(_folder);
            panel.Children.Add(folderRow);
            _checkResult = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            panel.Children.Add(_checkResult);

            panel.Children.Add(new TextBlock { Text = "Хранить логи, дней", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
            _retention = new TextBox { Text = settings.LogRetentionDays.ToString(CultureInfo.InvariantCulture), Width = 60, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(_retention);
            panel.Children.Add(new TextBlock
            {
                Text = "Логи: " + Host.LogDirectory,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var save = new Button { Content = "Сохранить", IsDefault = true, Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 0, 8, 0) };
            save.Click += (s, e) => Save();
            var cancel = new Button { Content = "Отмена", IsCancel = true, Padding = new Thickness(12, 3, 12, 3) };
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
        }

        private void CheckFolder()
        {
            string folder = _folder.Text.Trim();
            var loader = new ConfigLoader(new ConfigLocations(Host.BuiltInConfigDirectory, folder));
            var text = new StringBuilder();
            bool problems = false;
            foreach (RuleFileSpec spec in RuleTables.All)
            {
                LoadedTable loaded = loader.Load(spec);
                string source = loaded.Source == ConfigSourceKind.Department ? "из папки отдела" : "встроенный";
                text.Append(spec.FileName).Append(": ").Append(loaded.Table != null ? source : "не загружен");
                if (loaded.Issues.Count > 0)
                {
                    problems = true;
                    text.Append(" — ").Append(string.Join("; ", loaded.Issues.Select(i => i.Message)));
                }
                text.AppendLine();
            }
            _checkResult.Text = text.ToString().TrimEnd();
            _checkResult.Foreground = problems ? Brushes.DarkRed : Brushes.DarkGreen;
        }

        private void Save()
        {
            if (!int.TryParse(_retention.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int days) || days < 1 || days > 365)
            {
                MessageBox.Show(this, "Срок хранения логов — целое число от 1 до 365.", "VK Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _settings.DepartmentRulesFolder = _folder.Text.Trim();
            _settings.LogRetentionDays = days;
            try
            {
                _settings.Save(Host.SettingsPath);
                Host.Log.Info($"Настройки сохранены: папка правил «{_settings.DepartmentRulesFolder}», логи {days} дн.");
                DialogResult = true;
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                Host.Log.Error("Не удалось сохранить настройки.", ex);
                MessageBox.Show(this, "Не удалось сохранить настройки: " + ex.Message, "VK Tools", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
