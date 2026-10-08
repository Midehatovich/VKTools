using VKTools.Core.Text;
using Xunit;

namespace VKTools.Core.Tests
{
    public class CsvTableTests
    {
        [Fact]
        public void Комментарии_схема_и_заголовок()
        {
            const string text = "# Справочник;;;;\n# schema = 2;;;\nКод;Расход;Слоёв\n\nprimer;0,08;1\n;;\nenamel;0,14;2\n";
            CsvTable t = CsvTable.Parse(text);
            Assert.Equal(2, t.SchemaVersion);
            Assert.Equal(new[] { "Код", "Расход", "Слоёв" }, t.Columns);
            Assert.Equal(2, t.Rows.Count);
            Assert.Equal("enamel", t.Rows[1].Get("Код"));
            Assert.Equal("0,14", t.Rows[1].Get("расход"));
            Assert.Equal(7, t.Rows[1].LineNumber);
        }

        [Fact]
        public void Кавычки_с_разделителем_и_кавычкой_внутри()
        {
            CsvTable t = CsvTable.Parse("#schema=1\nА;Б\n\"x;y\";\"он сказал \"\"да\"\"\"\n");
            Assert.Equal("x;y", t.Rows[0].Get("А"));
            Assert.Equal("он сказал \"да\"", t.Rows[0].Get("Б"));
        }

        [Fact]
        public void Перевод_строки_внутри_кавычек_не_сбивает_номера_строк()
        {
            CsvTable t = CsvTable.Parse("А;Б\n\"1\n2\";x\nz;y\n");
            Assert.Equal("1\n2", t.Rows[0].Get("А"));
            Assert.Equal(4, t.Rows[1].LineNumber);
        }

        [Fact]
        public void Нет_колонки_пустая_строка()
        {
            CsvTable t = CsvTable.Parse("А\n1\n");
            Assert.Equal(string.Empty, t.Rows[0].Get("Нет такой"));
            Assert.Null(t.SchemaVersion);
        }

        [Fact]
        public void Пробелы_по_краям_обрезаются()
        {
            CsvTable t = CsvTable.Parse("А ; Б\n  1 ;  2  \n");
            Assert.Equal("1", t.Rows[0].Get("А"));
            Assert.Equal("2", t.Rows[0].Get("Б"));
        }
    }
}
