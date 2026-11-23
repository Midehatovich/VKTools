using System;
using System.Collections.Generic;
using System.Linq;
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
            defaults.ReserveText = LoadText(loader, CalcTables.Reserve, defaults);
            SpecProjectSettings stored = SpecSettingsStore.Read(doc, out int storages);
            ParamProfile.Active = ParamProfile.Resolve(stored);
            var issues = new List<ConfigIssue>();
            CalcOptions options = CalcOptions.Load(stored, defaults, issues, out string? blocked);
            defaults.Issues.AddRange(issues);
            defaults.ProjectBlocked = blocked;
            if (storages > 1) Host.Log.Warn($"В модели «{doc.Title}» хранилищ настроек VK Tools: {storages}; читается последнее созданное.");
            SystemParametersFromSystemFloor(doc, stored, options);
            return (options, defaults);
        }

        /// <summary>
        /// Система позиций согласована с «Системой и этажом»: её параметры (VK_Сокращение системы и второй) тоже получают систему,
        /// если привязаны в проекте. В проекте без LSR_Имя системы и без своей настройки Р3 основной параметр — параметр «Системы и этажа».
        /// </summary>
        private static void SystemParametersFromSystemFloor(Document doc, SpecProjectSettings stored, CalcOptions options)
        {
            (SpecOptions sfOptions, _) = SpecSettingsLoader.Load(doc, SpecModule.SystemFloor);
            SystemFloorSettings sf = sfOptions.SystemFloor;
            Dictionary<string, List<(Definition Definition, ElementBinding Binding)>> bindings = CalcParams.ProjectBindings(doc);
            // Свой набор «Р_» (0.14.2, Пушкарев): LSR_ не пишется и может быть привязан пустым — система позиций только из Р_Сокращение системы.
            bool ownSet = ParamProfile.IsOwn && options.SystemParameter.Trim().StartsWith("LSR_", StringComparison.Ordinal);
            if (sf.SystemParam != null && (ownSet || (stored.Get("calc.systemParam") == null && !bindings.ContainsKey(options.SystemParameter.Trim()))))
            {
                options.SystemParameter = sf.SystemParam.Name;
            }
            options.ExtraSystemParameters = new[] { sf.SystemParam?.Name, sf.MirrorParam?.Name }
                .Where(n => !string.IsNullOrWhiteSpace(n) && n != options.SystemParameter.Trim() && bindings.ContainsKey(n!))
                .Select(n => n!).Distinct(StringComparer.Ordinal).ToList();
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
