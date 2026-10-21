using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Logging;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Spec
{
    /// <summary>Объяснение «молчаливого» отказа записи по устройству параметра в семействе.</summary>
    internal sealed class FamilyDiagnosis
    {
        public FamilyDiagnosis(string family, string parameter, string message, string action, IEnumerable<long> ids)
        {
            Family = family;
            Parameter = parameter;
            Message = message;
            Action = action;
            Ids = ids.ToList();
        }

        public string Family { get; }

        public string Parameter { get; }

        public string Message { get; }

        public string Action { get; }

        public List<long> Ids { get; }
    }

    /// <summary>
    /// Revit принял Set без ошибки, а читается прежнее значение (15 фитингов КрМа3 в 0.4.0). Причина — в семействе:
    /// плагин открывает семейство только для чтения (EditFamily, без сохранения) и смотрит, как там заведён параметр.
    /// Вызывается вне транзакций, после прогона; не больше <see cref="MaxFamilies"/> семейств за раз.
    /// </summary>
    internal static class FamilyDiagnostics
    {
        public const string SilentFailure = "Revit записал другое значение";
        private const int MaxFamilies = 5;

        public static List<FamilyDiagnosis> Explain(Document doc, IEnumerable<ChangeProblem> failed, ILog log)
        {
            var result = new List<FamilyDiagnosis>();
            var groups = failed.Where(f => f.Reason.StartsWith(SilentFailure, StringComparison.Ordinal))
                .Select(f => (Problem: f, Family: (doc.GetElement(f.Change.ElementId.ToElementId()) as FamilyInstance)?.Symbol?.Family))
                .Where(x => x.Family != null)
                .GroupBy(x => (x.Family!.Id.ToLong(), x.Problem.Change.Key.Id))
                .Take(MaxFamilies);
            foreach (var group in groups)
            {
                Family family = group.First().Family!;
                ParamKey key = group.First().Problem.Change.Key;
                List<long> ids = group.Select(x => x.Problem.Change.ElementId).ToList();
                try
                {
                    result.Add(Explain(doc, family, key, ids));
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException)
                {
                    log.Warn($"Диагностика семейства «{family.Name}»: не удалось открыть — {ex.Message}");
                    result.Add(new FamilyDiagnosis(family.Name, key.Name, $"семейство «{family.Name}» не открылось для проверки: {ex.Message}",
                        "Откройте семейство и проверьте параметр вручную (Типоразмеры семейства).", ids));
                }
            }
            return result;
        }

        private static FamilyDiagnosis Explain(Document doc, Family family, ParamKey key, List<long> ids)
        {
            if (!family.IsEditable)
            {
                return new FamilyDiagnosis(family.Name, key.Name, $"семейство «{family.Name}» Revit не даёт открыть (системное или защищённое)",
                    "Проверьте параметр в свойствах типа.", ids);
            }
            Document familyDoc = doc.EditFamily(family);
            try
            {
                FamilyManager manager = familyDoc.FamilyManager;
                FamilyParameter? fp = key.Kind == ParamKeyKind.Shared ? manager.get_Parameter(key.Guid) : manager.get_Parameter(key.Name);
                FamilyParameter? sameName = fp == null ? manager.get_Parameter(key.Name) : null;
                string where = $"В семействе «{family.Name}» ";
                if (fp == null && sameName == null)
                {
                    return new FamilyDiagnosis(family.Name, key.Name,
                        where + $"параметра «{key.Name}» нет — значение даёт параметр проекта, и Revit его всё равно не сохраняет. Причина не в параметре семейства.",
                        "Пришлите семейство разработчику VK Tools.", ids);
                }
                if (fp == null)
                {
                    return new FamilyDiagnosis(family.Name, key.Name,
                        where + $"есть «{key.Name}», но это другой параметр ({(sameName!.IsShared ? "другой GUID" : "не общий")}), и он перекрывает параметр проекта.",
                        "Замените его в семействе на параметр из ФОП отдела или удалите из семейства.", ids);
                }
                int associated = fp.AssociatedParameters?.Size ?? 0;
                string links = associated > 0 ? $" Управляет параметрами вложенных: {associated}." : string.Empty;
                if (!fp.IsInstance)
                {
                    return new FamilyDiagnosis(family.Name, key.Name,
                        where + $"«{key.Name}» — параметр ТИПА, а в проекте — экземпляра: значение экземпляра Revit не сохраняет, берёт из типа.{links}",
                        "В семействе сделайте параметр параметром экземпляра (Типоразмеры семейства → «По экземпляру») и загрузите семейство в проект.", ids);
                }
                if (!string.IsNullOrEmpty(fp.Formula))
                {
                    return new FamilyDiagnosis(family.Name, key.Name, where + $"«{key.Name}» задан формулой «{fp.Formula}».{links}",
                        "Уберите формулу или оставьте значение семейству («не трогать» в таблице групп).", ids);
                }
                if (fp.IsReporting)
                {
                    return new FamilyDiagnosis(family.Name, key.Name, where + $"«{key.Name}» — параметр-отчёт: его значение вычисляет семейство.{links}",
                        "Сделайте параметр обычным (не отчётным) или оставьте значение семейству.", ids);
                }
                return new FamilyDiagnosis(family.Name, key.Name,
                    where + $"«{key.Name}» — общий параметр экземпляра без формулы.{links} По семейству причина не видна.",
                    "Пришлите семейство разработчику VK Tools.", ids);
            }
            finally
            {
                familyDoc.Close(false);
            }
        }
    }
}
