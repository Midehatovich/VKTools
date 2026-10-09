using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Mep
{
    /// <summary>
    /// Сведения о модели для лога перед записью: размер основных систем, уровень расчёта типа системы,
    /// апдейтеры других надстроек. Вместе со временем этапов показывает, что тормозит.
    /// </summary>
    internal static class MergeDiagnostics
    {
        public static IEnumerable<string> Describe(Document doc, MergePlan plan, IReadOnlyCollection<PlannedCandidate> selected)
        {
            yield return $"модель «{doc.Title}»: совместная работа {(doc.IsWorkshared ? "да" : "нет")}, систем трубопроводов {plan.Model.Systems.Count}, отмечено копий {selected.Count}";
            foreach (IGrouping<PlannedGroup, PlannedCandidate> g in selected.GroupBy(c => c.Group))
            {
                var type = doc.GetElement(g.Key.TargetTypeId.ToElementId()) as MEPSystemType;
                string calculation;
                try
                {
                    calculation = type?.CalculationLevel.ToString() ?? "?";
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    calculation = "?";
                }
                yield return $"основная «{g.Key.BaseName}» (Id {g.Key.TargetId}): элементов {plan.Model.MembersOf(g.Key.TargetId).All.Count}, "
                    + $"тип «{type?.Name}», расчёт {calculation}; копий {g.Count()}, в них элементов {g.Sum(c => c.ElementIds.Count)}, терминалов {g.Sum(c => c.TerminalIds.Count)}";
            }
            List<string> updaters;
            try
            {
                updaters = UpdaterRegistry.GetRegisteredUpdaterInfos(doc).Select(u => $"{u.UpdaterName} ({u.ApplicationName})").ToList();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                updaters = new List<string> { "не удалось прочитать" };
            }
            yield return $"апдейтеры документа ({updaters.Count}): " + string.Join("; ", updaters);
        }
    }
}
