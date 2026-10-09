using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Logging;

namespace VKTools.Revit.Checks
{
    /// <summary>Всё, что нужно проверкам: документ, разобранные правила, отчёт и лог.</summary>
    internal sealed class CheckContext
    {
        public CheckContext(Document document, CheckReport report, ILog log)
        {
            Document = document;
            Report = report;
            Log = log;
        }

        public Document Document { get; }

        public CheckReport Report { get; }

        public ILog Log { get; }

        public List<RequiredParameter>? Parameters { get; set; }

        public List<CategoryRef>? VkCategories { get; set; }

        public List<UpdaterRule>? Updaters { get; set; }

        public List<ParameterRef>? InsulationFromHost { get; set; }

        public List<ParameterRef>? LookalikeParameters { get; set; }

        /// <summary>Категории ВК, которые эта версия Revit знает.</summary>
        public List<(BuiltInCategory Category, string Name)> ResolvedVkCategories { get; } =
            new List<(BuiltInCategory Category, string Name)>();

        /// <summary>Сколько строк одна проверка выводит в отчёт, чтобы окно не встало на огромной модели.</summary>
        public int MaxFindingsPerCheck { get; set; } = 5000;

        public void Add(CheckFinding finding) => Report.Findings.Add(finding);

        public static bool TryResolveCategory(string name, out BuiltInCategory category) =>
            Enum.TryParse(name, false, out category) && category != BuiltInCategory.INVALID;
    }

    internal interface IModelCheck
    {
        string Name { get; }

        void Run(CheckContext context);
    }

    /// <summary>Ограничитель числа строк одной проверки с итоговой строкой «показаны первые N из M».</summary>
    internal sealed class FindingLimiter
    {
        private readonly CheckContext _context;
        private readonly string _check;
        private int _added;
        private int _total;

        public FindingLimiter(CheckContext context, string check)
        {
            _context = context;
            _check = check;
        }

        public void Add(CheckFinding finding)
        {
            _total++;
            if (_added >= _context.MaxFindingsPerCheck) return;
            _context.Add(finding);
            _added++;
        }

        public void Complete()
        {
            if (_total > _added)
            {
                _context.Add(new CheckFinding(_check, Severity.Info,
                    $"Показаны первые {_added} строк из {_total}. Исправьте найденное и запустите проверку ещё раз."));
            }
        }
    }
}
