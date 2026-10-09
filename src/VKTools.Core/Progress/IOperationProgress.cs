using System;

namespace VKTools.Core.Progress
{
    /// <summary>
    /// Прогресс долгой операции и запрос отмены. Операция сама решает, где можно остановиться:
    /// между шагами вызывает <see cref="ProgressExtensions.ThrowIfCancelled"/>.
    /// </summary>
    public interface IOperationProgress
    {
        /// <summary>Этап и, если известно, сколько сделано из скольких (total = 0 — неизвестно).</summary>
        void Report(string stage, int done = 0, int total = 0);

        bool IsCancellationRequested { get; }
    }

    public sealed class NullProgress : IOperationProgress
    {
        public static readonly NullProgress Instance = new NullProgress();

        public bool IsCancellationRequested => false;

        public void Report(string stage, int done = 0, int total = 0)
        {
        }
    }

    public static class ProgressExtensions
    {
        public static void ThrowIfCancelled(this IOperationProgress progress)
        {
            if (progress.IsCancellationRequested) throw new OperationCanceledException("Отменено пользователем.");
        }
    }
}
