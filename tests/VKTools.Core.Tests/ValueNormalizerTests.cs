using System.Linq;
using VKTools.Core.Text;
using Xunit;

namespace VKTools.Core.Tests
{
    public class ValueNormalizerTests
    {
        [Theory]
        [InlineData("B1", "В1")]          // латинская B и кириллическая В
        [InlineData("02 Этаж", "02 этаж")]
        [InlineData(" 03  этаж ", "03 этаж")]
        [InlineData("K1н", "К1н")]
        public void Одинаковые_на_вид_значения_дают_один_ключ(string a, string b)
        {
            Assert.Equal(ValueNormalizer.ComparisonKey(a), ValueNormalizer.ComparisonKey(b));
        }

        [Theory]
        [InlineData("В1", "В1.1")]
        [InlineData("К1", "К1н")]
        [InlineData("02 этаж", "03 этаж")]
        public void Разные_значения_не_склеиваются(string a, string b)
        {
            Assert.NotEqual(ValueNormalizer.ComparisonKey(a), ValueNormalizer.ComparisonKey(b));
        }

        [Fact]
        public void Группы_двойников_по_частоте()
        {
            var values = Enumerable.Repeat("В1", 10).Concat(new[] { "B1", "B1", "Т3", "02 этаж", "02 Этаж", "" });
            var groups = ValueNormalizer.FindLookalikes(values);
            Assert.Equal(2, groups.Count);
            var v1 = groups.Single(g => g.Variants.Any(v => v.Value == "В1"));
            Assert.Equal("В1", v1.Variants[0].Value);
            Assert.Equal(10, v1.Variants[0].Count);
            Assert.Equal("B1", v1.Variants[1].Value);
        }

        [Fact]
        public void Смешанные_алфавиты()
        {
            Assert.True(ValueNormalizer.HasMixedScripts("К1H"));   // H латинская
            Assert.False(ValueNormalizer.HasMixedScripts("К1н"));
            Assert.False(ValueNormalizer.HasMixedScripts("ADSK"));
        }

        [Fact]
        public void Лишние_пробелы()
        {
            Assert.True(ValueNormalizer.HasExtraWhitespace("02 этаж "));
            Assert.True(ValueNormalizer.HasExtraWhitespace("02  этаж"));
            Assert.True(ValueNormalizer.HasExtraWhitespace("02\tэтаж"));
            Assert.False(ValueNormalizer.HasExtraWhitespace("02 этаж"));
        }
    
        [Theory]
        [InlineData("В1", "В1")]
        [InlineData("B1", "B1 (латиница: B)")]
        [InlineData("02 этаж ", "02·этаж·")]
        [InlineData("K1H", "K1H (латиница: K, H)")]
        [InlineData(null, "")]
        public void Разница_видна_в_отчёте(string? value, string expected)
        {
            Assert.Equal(expected, ValueNormalizer.MakeVisible(value));
        }
    }
}
