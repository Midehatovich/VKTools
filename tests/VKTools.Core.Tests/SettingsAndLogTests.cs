using System;
using System.IO;
using VKTools.Core.Logging;
using VKTools.Core.Settings;
using Xunit;

namespace VKTools.Core.Tests
{
    public class SettingsAndLogTests
    {
        [Fact]
        public void Настройки_сохраняются_и_читаются()
        {
            using var tmp = new TempDir();
            string path = Path.Combine(tmp.Path, "VKTools", "settings.xml");
            new UserSettings { DepartmentRulesFolder = @"\\server\ВК\Правила", LogRetentionDays = 10 }.Save(path);
            UserSettings s = UserSettings.Load(path);
            Assert.Equal(@"\\server\ВК\Правила", s.DepartmentRulesFolder);
            Assert.Equal(10, s.LogRetentionDays);
            Assert.Null(s.LoadError);
        }

        [Fact]
        public void Битые_настройки_не_мешают_работе()
        {
            using var tmp = new TempDir();
            string path = Path.Combine(tmp.Path, "settings.xml");
            File.WriteAllText(path, "<VKToolsSettings><DepartmentRulesFolder>");
            UserSettings s = UserSettings.Load(path);
            Assert.Equal(string.Empty, s.DepartmentRulesFolder);
            Assert.NotNull(s.LoadError);
        }

        [Fact]
        public void Лог_пишет_и_удаляет_старые_файлы()
        {
            using var tmp = new TempDir();
            var now = new DateTime(2026, 10, 8, 12, 0, 0);
            File.WriteAllText(Path.Combine(tmp.Path, "vktools-20260801.log"), "old");
            File.WriteAllText(Path.Combine(tmp.Path, "vktools-20261001.log"), "recent");
            var log = new FileLog(tmp.Path, 30, () => now);
            log.Info("Запуск");
            log.Error("Сбой", new InvalidOperationException("пример"));
            Assert.False(File.Exists(Path.Combine(tmp.Path, "vktools-20260801.log")));
            Assert.True(File.Exists(Path.Combine(tmp.Path, "vktools-20261001.log")));
            string text = File.ReadAllText(log.CurrentFile);
            Assert.Contains("INFO  Запуск", text);
            Assert.Contains("InvalidOperationException", text);
            Assert.NotEmpty(log.Tail(2));
        }
    }
}
