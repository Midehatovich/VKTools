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
            var others = new List<string>();
            foreach (UpdaterInfo info in infos)
            {
                UpdaterRule? rule = rules.FirstOrDefault(r => r.Matches(info.UpdaterName, info.ApplicationName));
                if (rule == null)
                {
                    others.Add($"{info.UpdaterName} ({info.ApplicationName})");
                    continue;
                }
                context.Add(new CheckFinding(Name, rule.Severity, $"Апдейтер «{info.UpdaterName}» ({info.ApplicationName}). {rule.Message}")
                {
                    Current = info.UpdaterName,
                    Action = "Перед записью VK Tools убедитесь, что это правило в надстройке отключено, иначе результат будет перезаписан.",
                });
            }

            // Остальные — одной строкой: в основном служебные апдейтеры Revit. Пишут ли они параметры ВК, плагин не знает
            if (others.Count > 0)
            {
                context.Add(new CheckFinding(Name, Severity.Info,
                    $"Ещё {others.Count} апдейтеров не из списка правил: {string.Join("; ", others)}.")
                {
                    Action = "Если какой-то из них пишет параметры ВК — добавьте его в updaters.csv.",
                });
            }
        }
    }
}
