using System;
using System.IO;
using System.Text;

namespace VKTools.Core.Text
{
    /// <summary>
    /// Читает текстовые файлы правил в любой кодировке, в которой их сохраняет Excel или Блокнот:
    /// UTF-8 с BOM, UTF-16, UTF-8 без BOM и Windows-1251 (ANSI в русской Windows).
    /// Кодировку определяем сами, потому что «CSV (разделители — запятые)» из Excel сохраняется в ANSI,
    /// и без этого кириллица ломается (так было с lookup-таблицами хомутов).
    /// </summary>
    public static class TextFileReader
    {
#if NET
        private static readonly object EncodingLock = new object();
        private static bool _codePagesRegistered;
#endif

        public static string ReadAllText(string path, out string encodingName)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return Decode(bytes, out encodingName);
        }

        public static string Decode(byte[] bytes, out string encodingName)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                encodingName = "UTF-8 BOM";
                return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                encodingName = "UTF-16 LE";
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            }
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                encodingName = "UTF-16 BE";
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            }

            try
            {
                string text = new UTF8Encoding(false, true).GetString(bytes);
                encodingName = "UTF-8";
                return text;
            }
            catch (DecoderFallbackException)
            {
                encodingName = "Windows-1251";
                return GetWindows1251().GetString(bytes);
            }
        }

        public static void WriteUtf8WithBom(string path, string text)
        {
            File.WriteAllText(path, text, new UTF8Encoding(true));
        }

        private static Encoding GetWindows1251()
        {
#if NET
            lock (EncodingLock)
            {
                if (!_codePagesRegistered)
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    _codePagesRegistered = true;
                }
            }
#endif
            return Encoding.GetEncoding(1251);
        }
    }
}
