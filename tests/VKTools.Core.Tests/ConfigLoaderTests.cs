using System.IO;
using VKTools.Core.Config;
using Xunit;

namespace VKTools.Core.Tests
{
    public class ConfigLoaderTests
    {
        private static readonly RuleFileSpec Spec = new RuleFileSpec("t.csv", 1, "Параметр", "GUID");

        private const string Good = "#schema=1\nПараметр;GUID\nADSK_Этаж;9eabf56c-a6cd-4b5c-a9d0-e9223e19ea3f\n";

        [Fact]
        public void Без_папки_отдела_берутся_встроенные()
        {
            using var tmp = new TempDir();
            string builtIn = tmp.Sub("builtin");
            File.WriteAllText(Path.Combine(builtIn, "t.csv"), Good);
            LoadedTable r = new ConfigLoader(new ConfigLocations(builtIn, null)).Load(Spec);
            Assert.NotNull(r.Table);
            Assert.Equal(ConfigSourceKind.BuiltIn, r.Source);
            Assert.False(r.WriteBlocked);
            Assert.Empty(r.Issues);
        }

        [Fact]
        public void Файл_отдела_перекрывает_встроенный()
        {
            using var tmp = new TempDir();
            string builtIn = tmp.Sub("builtin");
            string dept = tmp.Sub("dept");
            File.WriteAllText(Path.Combine(builtIn, "t.csv"), Good);
            File.WriteAllText(Path.Combine(dept, "t.csv"), Good + "LSR_Имя системы;0cc98b64-809e-45f3-9ea7-553b2d6f21ca\n");
            LoadedTable r = new ConfigLoader(new ConfigLocations(builtIn, dept)).Load(Spec);
            Assert.Equal(ConfigSourceKind.Department, r.Source);
            Assert.Equal(2, r.Table!.Rows.Count);
            Assert.False(r.WriteBlocked);
        }

        [Fact]
        public void Файла_нет_в_папке_отдела_берётся_встроенный_без_блокировки()
        {
            using var tmp = new TempDir();
            string builtIn = tmp.Sub("builtin");
            string dept = tmp.Sub("dept");
            File.WriteAllText(Path.Combine(builtIn, "t.csv"), Good);
            LoadedTable r = new ConfigLoader(new ConfigLocations(builtIn, dept)).Load(Spec);
            Assert.Equal(ConfigSourceKind.BuiltIn, r.Source);
            Assert.False(r.WriteBlocked);
        }

        [Fact]
        public void Папка_отдела_недоступна_запись_запрещена()
        {
            using var tmp = new TempDir();
            string builtIn = tmp.Sub("builtin");
            File.WriteAllText(Path.Combine(builtIn, "t.csv"), Good);
            LoadedTable r = new ConfigLoader(new ConfigLocations(builtIn, Path.Combine(tmp.Path, "нет-такой"))).Load(Spec);
            Assert.NotNull(r.Table);
            Assert.Equal(ConfigSourceKind.BuiltIn, r.Source);
            Assert.True(r.WriteBlocked);
            Assert.Contains("недоступна", r.WriteBlockedReason);
        }

        [Fact]
        public void Файл_отдела_без_схемы_запись_запрещена_работают_встроенные()
        {
            using var tmp = new TempDir();
            string builtIn = tmp.Sub("builtin");
            string dept = tmp.Sub("dept");
            File.WriteAllText(Path.Combine(builtIn, "t.csv"), Good);
            File.WriteAllText(Path.Combine(dept, "t.csv"), "Параметр;GUID\nx;y\n");
            LoadedTable r = new ConfigLoader(new ConfigLocations(builtIn, dept)).Load(Spec);
            Assert.Equal(ConfigSourceKind.BuiltIn, r.Source);
            Assert.True(r.WriteBlocked);
            Assert.Contains(r.Issues, i => i.Message.Contains("schema"));
        }

        [Fact]
        public void Схема_новее_плагина_ошибка()
        {
            using var tmp = new TempDir();
            string builtIn = tmp.Sub("builtin");
            File.WriteAllText(Path.Combine(builtIn, "t.csv"), Good.Replace("schema=1", "schema=2"));
            LoadedTable r = new ConfigLoader(new ConfigLocations(builtIn, null)).Load(Spec);
            Assert.Null(r.Table);
            Assert.True(r.WriteBlocked);
            Assert.Contains(r.Issues, i => i.Message.Contains("Обновите плагин"));
        }

        [Fact]
        public void Нет_обязательной_колонки_ошибка()
        {
            using var tmp = new TempDir();
            string builtIn = tmp.Sub("builtin");
            File.WriteAllText(Path.Combine(builtIn, "t.csv"), "#schema=1\nПараметр\nx\n");
            LoadedTable r = new ConfigLoader(new ConfigLocations(builtIn, null)).Load(Spec);
            Assert.Null(r.Table);
            Assert.Contains(r.Issues, i => i.Message.Contains("GUID"));
        }
    }
}
