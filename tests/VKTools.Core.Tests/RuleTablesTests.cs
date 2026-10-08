using System.Linq;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Text;
using Xunit;

namespace VKTools.Core.Tests
{
    public class RuleTablesTests
    {
        private const string Header = "#schema=1\nПараметр;GUID;Обязательный;Привязка;Категории;Назначение\n";

        [Fact]
        public void Параметр_разбирается()
        {
            CsvTable t = CsvTable.Parse(Header +
                "ADSK_Этаж;9eabf56c-a6cd-4b5c-a9d0-e9223e19ea3f;да;Экземпляр;OST_PipeCurves, OST_PipeInsulations;Этаж\n");
            ParsedRules<RequiredParameter> r = RuleTables.ParseParameters(t, "parameters.csv");
            Assert.Empty(r.Issues);
            RequiredParameter p = Assert.Single(r.Items);
            Assert.True(p.Mandatory);
            Assert.Equal(BindingKind.Instance, p.Binding);
            Assert.Equal(new[] { "OST_PipeCurves", "OST_PipeInsulations" }, p.Categories);
        }

        [Fact]
        public void Плохие_строки_дают_ошибки_с_номером_строки()
        {
            CsvTable t = CsvTable.Parse(Header +
                "A;не-guid;да;Любая;;\n" +
                "B;9eabf56c-a6cd-4b5c-a9d0-e9223e19ea3f;может быть;Любая;;\n" +
                "C;9eabf56c-a6cd-4b5c-a9d0-e9223e19ea3f;нет;Экземплар;;\n" +
                "D;9eabf56c-a6cd-4b5c-a9d0-e9223e19ea3f;нет;;;\n" +
                "E;9eabf56c-a6cd-4b5c-a9d0-e9223e19ea3f;нет;;;\n");
            // B и C отбракованы до проверки повторов, поэтому GUID «занимает» только D, а E — повтор.
            ParsedRules<RequiredParameter> r = RuleTables.ParseParameters(t, "parameters.csv");
            Assert.Equal(new[] { "D" }, r.Items.Select(i => i.Name));
            Assert.Equal(new int?[] { 3, 4, 5, 7 }, r.Issues.Select(i => i.Line));
        }

        [Fact]
        public void Апдейтер_ищется_по_подстроке_без_регистра()
        {
            CsvTable t = CsvTable.Parse("#schema=1\nШаблон;Важность;Сообщение\nbim2b;Предупреждение;BIM2B\n");
            UpdaterRule rule = Assert.Single(RuleTables.ParseUpdaters(t, "updaters.csv").Items);
            Assert.Equal(Severity.Warning, rule.Severity);
            Assert.True(rule.Matches("Bim2b Dynamic Pipe Insulation Updater", null));
            Assert.False(rule.Matches("Other", "Something"));
        }
    }
}
