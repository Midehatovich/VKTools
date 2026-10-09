using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Infrastructure
{
    /// <summary>
    /// Отказы Revit внутри транзакции плагина: предупреждения собираются в отчёт и снимаются,
    /// любая ошибка — откат, чтобы Revit не «чинил» модель удалением элементов.
    /// Исключение одно (D-017): упавшее обновление другой надстройки из разрешённого списка.
    /// Если это единственная ошибка, плагин принимает решение Revit по умолчанию для такого отказа
    /// (как кнопка в окне Revit — апдейтер отключается до перезапуска Revit) и фиксирует транзакцию.
    /// </summary>
    internal sealed class FailureCollector : IFailuresPreprocessor
    {
        private readonly IReadOnlyList<UpdaterInfo> _updaters;
        private readonly ISet<string> _disableApplications;

        /// <summary>Обычный режим: любая ошибка — откат.</summary>
        public FailureCollector()
            : this(Array.Empty<UpdaterInfo>(), new HashSet<string>())
        {
        }

        /// <param name="updaters">Все апдейтеры документа (по ним имя из текста отказа сопоставляется с надстройкой).</param>
        /// <param name="disableApplications">Надстройки, чьё упавшее обновление разрешено отключить.</param>
        public FailureCollector(IReadOnlyList<UpdaterInfo> updaters, ISet<string> disableApplications)
        {
            _updaters = updaters;
            _disableApplications = disableApplications;
        }

        public List<string> Warnings { get; } = new List<string>();

        public List<string> Errors { get; } = new List<string>();

        /// <summary>Отключённые обновления: «Имя» (Надстройка).</summary>
        public List<string> DisabledUpdaters { get; } = new List<string>();

        /// <summary>Как Revit разрешил отказ апдейтера (текст кнопки) — для лога.</summary>
        public List<string> Notes { get; } = new List<string>();

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            bool hasError = false;
            var updaterFailures = new List<(FailureMessageAccessor Failure, string Text, UpdaterInfo Updater)>();
            foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages())
            {
                string text = failure.GetDescriptionText() ?? "?";
                ICollection<ElementId> failing = failure.GetFailingElementIds();
                if (failing != null && failing.Count > 0)
                {
                    text += " [элементы: " + string.Join(", ", failing.Take(10).Select(id => id.ToLong().ToString(CultureInfo.InvariantCulture)))
                        + (failing.Count > 10 ? " …" : string.Empty) + "]";
                }
                if (failure.GetSeverity() == FailureSeverity.Warning)
                {
                    Warnings.Add(text);
                    failuresAccessor.DeleteWarning(failure);
                    continue;
                }
                UpdaterInfo? updater = DisableableUpdater(failuresAccessor, failure, text);
                if (updater != null)
                {
                    updaterFailures.Add((failure, text, updater));
                }
                else
                {
                    Errors.Add(text);
                    hasError = true;
                }
            }

            // Есть другие ошибки — транзакция всё равно откатится, отключать чужой апдейтер незачем
            if (hasError)
            {
                foreach (var u in updaterFailures) Errors.Add(u.Text);
                return FailureProcessingResult.ProceedWithRollBack;
            }
            if (updaterFailures.Count == 0) return FailureProcessingResult.Continue;

            foreach (var u in updaterFailures)
            {
                string caption;
                try
                {
                    caption = u.Failure.GetDefaultResolutionCaption() ?? "?";
                    failuresAccessor.ResolveFailure(u.Failure);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    Errors.Add($"{u.Text} (VK Tools не смог отключить обновление: {ex.Message})");
                    return FailureProcessingResult.ProceedWithRollBack;
                }
                string name = $"«{u.Updater.UpdaterName}» ({u.Updater.ApplicationName})";
                if (!DisabledUpdaters.Contains(name)) DisabledUpdaters.Add(name);
                Notes.Add($"отказ обновления {name} разрешён решением Revit «{caption}»");
            }
            return FailureProcessingResult.ProceedWithCommit;
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

        /// <summary>
        /// Апдейтер, которого можно отключить: отказ «стороннее средство обновления … произошла ошибка»,
        /// имя однозначно указывает на надстройку из разрешённого списка, у отказа есть решение,
        /// Revit его разрешает и оно ещё не пробовалось (иначе — петля, лучше откат).
        /// </summary>
        private UpdaterInfo? DisableableUpdater(FailuresAccessor accessor, FailureMessageAccessor failure, string text)
        {
            if (_disableApplications.Count == 0) return null;
            if (failure.GetFailureDefinitionId().Guid != BuiltInFailures.DocumentFailures.DUMisbehavingUpdater.Guid) return null;

            // Имя в тексте в кавычках: «MEPUpdater» (рус.), 'MEPUpdater' (англ.). Одно имя у двух надстроек — не трогаем.
            List<UpdaterInfo> named = _updaters
                .Where(i => !string.IsNullOrEmpty(i.UpdaterName) && NameQuoted(text, i.UpdaterName))
                .ToList();
            if (named.Count == 0 || named.Select(i => i.ApplicationName).Distinct().Count() != 1) return null;
            UpdaterInfo updater = named[0];
            if (!_disableApplications.Contains(updater.ApplicationName)) return null;

            if (!failure.HasResolutions() || !accessor.IsFailureResolutionPermitted(failure)) return null;
            if (accessor.GetAttemptedResolutionTypes(failure).Count > 0) return null;
            return updater;
        }

        private static bool NameQuoted(string text, string name) =>
            text.Contains("«" + name + "»") || text.Contains("'" + name + "'") || text.Contains("\"" + name + "\"") || text.Contains("“" + name + "”");
    }
}
