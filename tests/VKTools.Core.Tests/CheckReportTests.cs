using System;
using VKTools.Core.Checks;
using Xunit;

namespace VKTools.Core.Tests
{
    public class CheckReportTests
    {
        [Fact]
        public void Csv_экранирует_разделитель_кавычки_и_формулы()
        {
            var report = new CheckReport("Проверка модели", "Проект.rvt", new DateTime(2026, 10, 8, 10, 0, 0));
            report.Findings.Add(new CheckFinding("Пустое наименование", Severity.Warning, "a;b")
            {
                ElementId = 123456789012,
                Current = "=1+1",
                Expected = "он \"сказал\"",
            });
            string csv = report.ToCsv();
            Assert.Contains("Предупреждение;Пустое наименование;123456789012;", csv);
            Assert.Contains("\"a;b\"", csv);
            Assert.Contains("'=1+1", csv);
            Assert.Contains("\"он \"\"сказал\"\"\"", csv);
            Assert.Equal("Ошибок: 0, предупреждений: 1, инфо: 0", report.Summary);
        }
    }
}
