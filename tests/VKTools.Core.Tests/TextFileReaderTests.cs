using System.Linq;
using System.Text;
using VKTools.Core.Text;
using Xunit;

namespace VKTools.Core.Tests
{
    public class TextFileReaderTests
    {
        private const string Sample = "Параметр;GUID\nADSK_Этаж;9eabf56c-a6cd-4b5c-a9d0-e9223e19ea3f";

        [Fact]
        public void Utf8_с_BOM()
        {
            byte[] bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Sample)).ToArray();
            Assert.Equal(Sample, TextFileReader.Decode(bytes, out string enc));
            Assert.Equal("UTF-8 BOM", enc);
        }

        [Fact]
        public void Utf16_как_ФОП()
        {
            byte[] bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Sample)).ToArray();
            Assert.Equal(Sample, TextFileReader.Decode(bytes, out string enc));
            Assert.Equal("UTF-16 LE", enc);
        }

        [Fact]
        public void Utf8_без_BOM()
        {
            Assert.Equal(Sample, TextFileReader.Decode(Encoding.UTF8.GetBytes(Sample), out string enc));
            Assert.Equal("UTF-8", enc);
        }

        [Fact]
        public void Ansi_из_Excel_читается_как_1251()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            byte[] bytes = Encoding.GetEncoding(1251).GetBytes(Sample);
            Assert.Equal(Sample, TextFileReader.Decode(bytes, out string enc));
            Assert.Equal("Windows-1251", enc);
        }
    }
}
