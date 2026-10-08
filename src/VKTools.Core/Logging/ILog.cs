using System;

namespace VKTools.Core.Logging
{
    public interface ILog
    {
        void Info(string message);

        void Warn(string message);

        void Error(string message, Exception? exception = null);
    }

    /// <summary>Лог, который ничего не пишет (тесты, ранний старт).</summary>
    public sealed class NullLog : ILog
    {
        public static readonly NullLog Instance = new NullLog();

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message, Exception? exception = null)
        {
        }
    }
}
