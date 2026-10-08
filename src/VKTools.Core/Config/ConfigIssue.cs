using VKTools.Core.Checks;

namespace VKTools.Core.Config
{
    /// <summary>Замечание к файлу правил: что не так и в какой строке.</summary>
    public sealed class ConfigIssue
    {
        public ConfigIssue(Severity severity, string fileName, int? line, string message)
        {
            Severity = severity;
            FileName = fileName;
            Line = line;
            Message = message;
        }

        public Severity Severity { get; }

        public string FileName { get; }

        public int? Line { get; }

        public string Message { get; }

        public override string ToString() =>
            Line.HasValue ? $"{FileName}, строка {Line}: {Message}" : $"{FileName}: {Message}";
    }
}
