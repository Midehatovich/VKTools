using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Config;

namespace VKTools.Revit.Checks
{
    /// <summary>
    /// Апдейтеры других надстроек, зарегистрированные для документа. Они срабатывают после любой записи
    /// и могут перезаписать то, что записал плагин (сценарий «A → B → A», REQ-SAFE-007).
    /// </summary>
    internal sealed class UpdaterCheck : IModelCheck
    {
        public string Name => "Апдейтеры других надстроек";

        public void Run(CheckContext context)
        {
            IList<UpdaterInfo> infos = UpdaterRegistry.GetRegisteredUpdaterInfos(context.Document);
            List<UpdaterRule> rules = context.Updaters ?? new List<UpdaterRule>();
            foreach (UpdaterInfo info in infos)
            {
                UpdaterRule? rule = rules.FirstOrDefault(r => r.Matches(info.UpdaterName, info.ApplicationName));
                string who = $"«{info.UpdaterName}» ({info.ApplicationName})";
                context.Add(rule != null
                    ? new CheckFinding(Name, rule.Severity, $"Апдейтер {who}. {rule.Message}")
                    {
                        Current = info.UpdaterName,
                        Action = "Перед записью VK Tools убедитесь, что это правило в надстройке отключено, иначе результат будет перезаписан.",
                    }
                    : new CheckFinding(Name, Severity.Info, $"Апдейтер {who}.")
                    {
                        Current = info.UpdaterName,
                    });
            }
        }
    }
}
