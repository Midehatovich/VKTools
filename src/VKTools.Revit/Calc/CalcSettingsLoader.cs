using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using VKTools.Core.Calc;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using VKTools.Core.Text;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Spec;

namespace VKTools.Revit.Calc
{
    /// <summary>
    /// Настройки «Расчётных позиций»: таблицы по умолчанию (calc_*.csv — папка отдела или встроенные) и настройка проекта в модели
    /// (то же хранилище, что у Р2, ключи calc.*).
    /// </summary>
    internal static class CalcSettingsLoader
    {
        public static (CalcOptions Options, CalcDefaults Defaults) Load(Document doc)
        {
            ConfigLoader loader = Host.CreateConfigLoader(Host.LoadSettings());
            var defaults = new CalcDefaults();
            defaults.ResourcesText = LoadText(loader, CalcTables.Resources, defaults);
            defaults.PipeRulesText = LoadText(loader, CalcTables.PipeRules, defaults);
            defaults.SupportsText = LoadText(loader, CalcTables.Supports, defaults);
            defaults.CordText = LoadText(loader, CalcTables.Cord, defaults);
            SpecProjectSettings stored = SpecSettingsStore.Read(doc, out int storages);
            var issues = new List<ConfigIssue>();
            CalcOptions options = CalcOptions.Load(stored, defaults, issues, out string? blocked);
            defaults.Issues.AddRange(issues);
            defaults.ProjectBlocked = blocked;
            if (storages > 1) Host.Log.Warn($"В модели «{doc.Title}» хранилищ настроек VK Tools: {storages}; читается последнее созданное.");
            return (options, defaults);
        }

        private static string LoadText(ConfigLoader loader, RuleFileSpec spec, CalcDefaults defaults)
        {
            LoadedTable loaded = loader.Load(spec);
            defaults.Sources.Add(SpecSettingsLoader.Source(loaded));
            defaults.Issues.AddRange(loaded.Issues);
            if (loaded.WriteBlocked)
            {
                string reason = $"{spec.FileName}: {loaded.WriteBlockedReason}";
                defaults.Blocked = defaults.Blocked == null ? reason : defaults.Blocked + "; " + reason;
            }
            if (loaded.Table == null) return string.Empty;
            try
            {
                return TextFileReader.ReadAllText(loaded.SourcePath, out _);
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                defaults.Blocked ??= $"{spec.FileName}: не удалось прочитать {loaded.SourcePath}";
                return string.Empty;
            }
        }

        /// <summary>Сохранить настройку отдельной операцией (кнопка «Сохранить»). null — сохранено.</summary>
        public static string? Save(Document doc, CalcOptions options, CalcDefaults defaults)
        {
            using (var transaction = new Transaction(doc, SpecSettingsStore.TransactionName))
            {
                transaction.Start();
                string? problem = Store(doc, options, defaults);
                if (problem != null)
                {
                    transaction.RollBack();
                    return problem;
                }
                TransactionStatus status = transaction.Commit();
                return status == TransactionStatus.Committed ? null : $"Revit не зафиксировал настройки ({status})";
            }
        }

        /// <summary>Положить настройку в модель (внутри открытой транзакции): разделы Р2 не трогаются.</summary>
        public static string? Store(Document doc, CalcOptions options, CalcDefaults defaults)
        {
            SpecProjectSettings settings = SpecSettingsStore.Read(doc, out _);
            options.StoreTo(settings, defaults, Environment.UserName, DateTime.Now);
            return SpecSettingsStore.Write(doc, settings);
        }
    }
}
