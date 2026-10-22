using System.IO;
using System.Linq;
using VKTools.Core.Config;
using Xunit;

namespace VKTools.Core.Tests
{
    /// <summary>Встроенные правила из репозитория должны грузиться без замечаний: они уходят в установку.</summary>
    public class BuiltInConfigTests
    {
        public static TheoryData<string> Files()
        {
            var data = new TheoryData<string>();
            foreach (RuleFileSpec spec in RuleTables.All) data.Add(spec.FileName);
            return data;
        }

        [Theory]
        [MemberData(nameof(Files))]
        public void Встроенный_файл_валиден(string fileName)
        {
            RuleFileSpec spec = RuleTables.All.Single(s => s.FileName == fileName);
            LoadedTable loaded = new ConfigLoader(new ConfigLocations(RepoPaths.ConfigDirectory(), null)).Load(spec);
            Assert.Empty(loaded.Issues);
            Assert.NotNull(loaded.Table);
            Assert.Equal("UTF-8 BOM", loaded.EncodingName);
            Assert.NotEmpty(loaded.Table!.Rows);
        }

        [Fact]
        public void Параметры_разбираются_без_ошибок()
        {
            LoadedTable loaded = new ConfigLoader(new ConfigLocations(RepoPaths.ConfigDirectory(), null)).Load(RuleTables.Parameters);
            ParsedRules<RequiredParameter> parsed = RuleTables.ParseParameters(loaded.Table!, "parameters.csv");
            Assert.Empty(parsed.Issues);
            Assert.Contains(parsed.Items, p => p.Name == "ADSK_Этаж" && p.Categories.Contains("OST_PipeInsulations"));
        }

        [Theory]
        [MemberData(nameof(Files))]
        public void Встроенный_файл_есть_в_установщике(string fileName)
        {
            // Установщик перечисляет файлы поимённо: без строки в RevitYear.wxi файл не попадёт в MSI
            string wxi = Path.Combine(Path.GetDirectoryName(RepoPaths.ConfigDirectory())!, "installer", "RevitYear.wxi");
            Assert.Contains("/config/" + fileName + "\"", File.ReadAllText(wxi));
        }
    }
}
