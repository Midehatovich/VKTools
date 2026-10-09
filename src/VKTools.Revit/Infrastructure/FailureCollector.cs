using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace VKTools.Revit.Infrastructure
{
    /// <summary>
    /// Отказы Revit внутри транзакции плагина: предупреждения собираются в отчёт и снимаются,
    /// любая ошибка — откат, чтобы Revit не «чинил» модель удалением элементов.
    /// </summary>
    internal sealed class FailureCollector : IFailuresPreprocessor
    {
        public List<string> Warnings { get; } = new List<string>();

        public List<string> Errors { get; } = new List<string>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            bool hasError = false;
            foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
            {
                string text = failure.GetDescriptionText() ?? "?";
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    Warnings.Add(text);
                    failuresAccessor.DeleteWarning(failure);
                }
                else
                {
                    Errors.Add(text);
                    hasError = true;
                }
            }
            return hasError ? FailureProcessingResult.ProceedWithRollBack : FailureProcessingResult.Continue;
        }

        public static Transaction Start(Document doc, string name, FailureCollector collector)
        {
            var transaction = new Transaction(doc, name);
            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(collector);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);
            transaction.Start();
            return transaction;
        }
    }
}
