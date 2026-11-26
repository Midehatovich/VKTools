using System.Collections.Generic;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Spec
{
    /// <summary>
    /// Можно ли писать параметр элемента: группы модели (REQ-SAFE-010) и совместная работа (REQ-SAFE-004).
    /// Нельзя — элемент не пишется и попадает в отчёт; остальные пишутся.
    /// </summary>
    internal sealed class EditGuard
    {
        private readonly Document _doc;
        private readonly bool _workshared;
        private readonly Dictionary<long, string?> _workshareCache = new Dictionary<long, string?>();
        private readonly Dictionary<long, string> _owners = new Dictionary<long, string>();

        public EditGuard(Document doc)
        {
            _doc = doc;
            _workshared = doc.IsWorkshared;
        }

        /// <summary>Занятые и устаревшие элементы, встреченные за прогон: по ним решается, писать ли вообще (0.17.0).</summary>
        public BusySummary Busy { get; } = new BusySummary();

        /// <summary>Причина запрета всей записи по настройке пользователя; null — писать можно.</summary>
        public string? BusyBlockReason() => Busy.BlockReason(Infrastructure.Host.LoadSettings().PartialWriteWhenBusy);

        /// <summary>null — писать можно; иначе важность и причина для отчёта.</summary>
        /// <param name="track">false — только узнать (подсказка в окне), в <see cref="Busy"/> не считать: значение писаться не будет.</param>
        public (Severity Severity, string Reason)? Check(Element element, Parameter parameter, bool track = true)
        {
            string? group = GroupReason(element, parameter);
            if (group != null) return (Severity.Warning, group);
            string? share = WorkshareReason(element, track);
            if (share != null) return (Severity.Warning, share);
            return null;
        }

        /// <summary>
        /// Элемент (или его родитель) в группе модели: Revit даёт менять у экземпляра группы только параметры
        /// с отметкой «Может различаться по экземплярам групп». Остальные правятся в режиме редактирования группы.
        /// </summary>
        private static string? GroupReason(Element element, Parameter parameter)
        {
            Element? current = element;
            for (int depth = 0; current != null && depth < 50; depth++)
            {
                if (current.GroupId != null && current.GroupId != ElementId.InvalidElementId)
                {
                    if (parameter.Definition is InternalDefinition definition && definition.VariesAcrossGroups) return null;
                    return "элемент в группе модели, а параметр не «может различаться по экземплярам групп»";
                }
                current = (current as FamilyInstance)?.SuperComponent;
            }
            return null;
        }

        private string? WorkshareReason(Element element, bool track)
        {
            if (!_workshared) return null;
            long id = element.Id.ToLong();
            if (_workshareCache.TryGetValue(id, out string? cached))
            {
                if (cached != null && track) Track(id, cached);
                return cached;
            }
            string? reason = null;
            try
            {
                if (WorksharingUtils.GetCheckoutStatus(_doc, element.Id, out string owner) == CheckoutStatus.OwnedByOtherUser)
                {
                    reason = $"элемент занят пользователем «{owner}»";
                    _owners[id] = owner;
                }
                else
                {
                    ModelUpdatesStatus status = WorksharingUtils.GetModelUpdatesStatus(_doc, element.Id);
                    if (status == ModelUpdatesStatus.UpdatedInCentral || status == ModelUpdatesStatus.DeletedInCentral)
                    {
                        reason = "элемент изменён в хранилище — сначала «Обновить до последней версии»";
                    }
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Статус не прочитали — Revit сам откажет при фиксации, если элемент чужой
            }
            _workshareCache[id] = reason;
            if (reason != null && track) Track(id, reason);
            return reason;
        }

        private void Track(long id, string reason)
        {
            if (_owners.TryGetValue(id, out string? owner)) Busy.AddOwned(id, owner);
            else Busy.AddOutdated(id);
        }
    }
}
