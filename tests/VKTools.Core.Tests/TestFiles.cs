using System;
using System.IO;

namespace VKTools.Core.Tests
{
    internal sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vktools-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Sub(string name)
        {
            string p = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(p);
            return p;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (IOException)
            {
            }
        }
    }

    internal static class RepoPaths
    {
        /// <summary>Папка config/ репозитория: встроенные правила, которые уходят в установку.</summary>
        public static string ConfigDirectory()
        {
            string? dir = AppContext.BaseDirectory;
            while (dir != null)
            {
                string candidate = Path.Combine(dir, "config", "parameters.csv");
                if (File.Exists(candidate)) return Path.Combine(dir, "config");
                dir = Path.GetDirectoryName(dir);
            }
            throw new DirectoryNotFoundException("Не найдена папка config/ репозитория");
        }
    }
}
