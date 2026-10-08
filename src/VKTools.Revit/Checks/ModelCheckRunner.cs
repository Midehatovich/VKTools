using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Logging;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Checks
{
    /// <summary>
    /// «Проверить модель»: загружает правила, запускает проверки по очереди и собирает отчёт.
    /// Только чтение: ни одна проверка не открывает транзакцию.
    /// </summary>
    internal sealed class ModelCheckRunner
    {
        public const string RulesCheckName = "Правила VK Tools";

        private readonly ConfigLoader _loader;
        private readonly ILog _log;

        public ModelCheckRunner(ConfigLoader loader, ILog log)
        {
            _loader = loader;
            _log = log;
        }

        public CheckReport Run(Document document)
        {
            var report = new CheckReport("Проверка модели", document.Title, DateTime.Now)
            {
                PluginVersion = BuildInfo.PluginVersion,
                RevitVersion = BuildInfo.RevitVersion,
            };
            var context = new CheckContext(document, report, _log);

            LoadRules(context);
            ResolveCategories(context);

            var checks = new IModelCheck[]
            {
                new ParameterBindingCheck(),
                new UpdaterCheck(),
                new EmptyNameCheck(),
                new InsulationHostCheck(),
                new LookalikeValuesCheck(),
            };

            foreach (IModelCheck check in checks)
            {
                var watch = Stopwatch.StartNew();
                int before = report.Findings.Count;
                try
                {
                    check.Run(context);
                }
                catch (Exception ex) when (!(ex is OutOfMemoryException))
                {
                    // Одна сломанная проверка не должна прятать результаты остальных
                    _log.Error($"Проверка «{check.Name}» упала.", ex);
                    report.Findings.Add(new CheckFinding(check.Name, Severity.Error,
                        $"Проверка не выполнена: {ex.GetType().Name}: {ex.Message}")
                    {
                        Action = "Пришлите лог разработчику (Сервис → О программе → Открыть лог).",
                    });
                }
                _log.Info($"Проверка «{check.Name}»: {report.Findings.Count - before} строк, {watch.ElapsedMilliseconds} мс.");
            }

            if (report.Findings.Count == 0)
            {
                report.Findings.Add(new CheckFinding("Итог", Severity.Info, "Замечаний нет."));
            }
            return report;
        }

        private void LoadRules(CheckContext context)
        {
            context.Parameters = Load(context, RuleTables.Parameters, t => RuleTables.ParseParameters(t, RuleTables.Parameters.FileName));
            context.VkCategories = Load(context, RuleTables.VkCategories, t => RuleTables.ParseCategories(t, RuleTables.VkCategories.FileName));
            context.Updaters = Load(context, RuleTables.Updaters, t => RuleTables.ParseUpdaters(t, RuleTables.Updaters.FileName));
            context.InsulationFromHost = Load(context, RuleTables.InsulationFromHost, t => RuleTables.ParseParameterRefs(t, RuleTables.InsulationFromHost.FileName));
            context.LookalikeParameters = Load(context, RuleTables.LookalikeValues, t => RuleTables.ParseParameterRefs(t, RuleTables.LookalikeValues.FileName));
        }

        private List<T>? Load<T>(CheckContext context, RuleFileSpec spec, Func<Core.Text.CsvTable, ParsedRules<T>> parse)
        {
            LoadedTable loaded = _loader.Load(spec);
            string source = loaded.Source == ConfigSourceKind.Department ? "отдел" : "встроенные";
            context.Report.RuleSources.Add(loaded.Table != null
                ? $"{spec.FileName}: {source}, {loaded.SourcePath} ({loaded.EncodingName})"
                : $"{spec.FileName}: НЕ ЗАГРУЖЕН");

            foreach (ConfigIssue issue in loaded.Issues)
            {
                context.Add(new CheckFinding(RulesCheckName, issue.Severity, issue.ToString()));
                _log.Warn("Правила: " + issue);
            }
            if (loaded.Table == null)
            {
                context.Add(new CheckFinding(RulesCheckName, Severity.Error,
                    $"{spec.FileName} не загружен: проверки, которые от него зависят, пропущены."));
                return null;
            }

            ParsedRules<T> parsed = parse(loaded.Table);
            foreach (ConfigIssue issue in parsed.Issues)
            {
                context.Add(new CheckFinding(RulesCheckName, issue.Severity, issue.ToString()));
                _log.Warn("Правила: " + issue);
            }
            return parsed.Items;
        }

        private static void ResolveCategories(CheckContext context)
        {
            if (context.VkCategories == null) return;
            foreach (CategoryRef category in context.VkCategories)
            {
                if (!CheckContext.TryResolveCategory(category.BuiltInName, out BuiltInCategory bic))
                {
                    context.Add(new CheckFinding(RulesCheckName, Severity.Warning,
                        $"vk_categories.csv: категория «{category.BuiltInName}» неизвестна этой версии Revit, пропущена."));
                    continue;
                }
                Category? revitCategory = Category.GetCategory(context.Document, bic);
                context.ResolvedVkCategories.Add((bic, revitCategory?.Name ?? category.DisplayName));
            }
        }
    }
}
