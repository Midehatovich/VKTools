namespace VKTools.Core.Checks
{
    public enum Severity
    {
        Error = 0,
        Warning = 1,
        Info = 2,
    }

    public static class SeverityText
    {
        public static string ToRussian(this Severity severity)
        {
            switch (severity)
            {
                case Severity.Error: return "Ошибка";
                case Severity.Warning: return "Предупреждение";
                default: return "Инфо";
            }
        }

        public static bool TryParse(string? text, out Severity severity)
        {
            switch ((text ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "ошибка":
                case "error":
                    severity = Severity.Error;
                    return true;
                case "предупреждение":
                case "warning":
                    severity = Severity.Warning;
                    return true;
                case "инфо":
                case "info":
                    severity = Severity.Info;
                    return true;
                default:
                    severity = Severity.Info;
                    return false;
            }
        }
    }
}
