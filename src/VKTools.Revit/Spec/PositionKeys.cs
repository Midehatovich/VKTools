using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Config;

namespace VKTools.Revit.Spec
{
    /// <summary>
    /// Скрытая графа «Р_Ключ позиции» в оформленной спецификации (0.17.1, решение 2026-11-26): ключ сортировки сразу после шапки
    /// (системы). Значения «0012», «0012.01» ставят детали сразу под родителем, какой бы ни была их категория.
    /// Revit 2022 держит не больше 4 ключей сортировки: если места нет, последний ключ снимается, а его имя пишется в заголовок графы,
    /// чтобы «Позиции» при чтении порядка строк вернули его (в откатываемой транзакции).
    /// </summary>
    internal static class PositionKeys
    {
        public const string Heading = "Ключ позиции (VK Tools)";
        private const string DroppedMark = "; снят ключ: ";
        private const string DescMark = " ↓";

        public static ElementId? ParamId(Document doc) => SharedParameterElement.Lookup(doc, ParamProfile.PositionKeyGuid)?.Id;

        /// <summary>Индекс графы ключа; -1 — графы нет.</summary>
        public static int FieldIndex(ScheduleDefinition definition, ElementId? paramId)
        {
            if (paramId == null || paramId == ElementId.InvalidElementId) return -1;
            for (int i = 0; i < definition.GetFieldCount(); i++)
            {
                if (definition.GetField(i).ParameterId == paramId) return i;
            }
            return -1;
        }

        public static bool IsSorted(ScheduleDefinition definition, int fieldIndex) =>
            fieldIndex >= 0 && definition.GetSortGroupFields().Any(s => s.FieldId == definition.GetField(fieldIndex).FieldId);

        /// <summary>Снятый ключ из заголовка графы ключа: индекс графы и «по убыванию»; null — не снимался.</summary>
        public static (int Index, bool Desc)? Dropped(ScheduleDefinition definition, int keyField)
        {
            if (keyField < 0) return null;
            string heading = definition.GetField(keyField).ColumnHeading ?? string.Empty;
            int at = heading.IndexOf(DroppedMark, StringComparison.Ordinal);
            if (at < 0) return null;
            string name = heading.Substring(at + DroppedMark.Length).Trim();
            bool desc = name.EndsWith(DescMark.Trim(), StringComparison.Ordinal);
            if (desc) name = name.Substring(0, name.Length - DescMark.Trim().Length).Trim();
            for (int i = 0; i < definition.GetFieldCount(); i++)
            {
                if (i != keyField && string.Equals(definition.GetField(i).GetName(), name, StringComparison.Ordinal)) return (i, desc);
            }
            return null;
        }

        /// <summary>Ключи сортировки «как без VK Tools»: без графы ключа, со снятым ключом в конце.</summary>
        public static List<(int Index, bool Desc)> NaturalSort(ScheduleDefinition definition, int keyField)
        {
            var fieldIndex = new Dictionary<ScheduleFieldId, int>();
            for (int i = 0; i < definition.GetFieldCount(); i++) fieldIndex[definition.GetField(i).FieldId] = i;
            var keys = definition.GetSortGroupFields()
                .Where(f => fieldIndex.ContainsKey(f.FieldId) && fieldIndex[f.FieldId] != keyField)
                .Select(f => (Index: fieldIndex[f.FieldId], Desc: f.SortOrder == ScheduleSortOrder.Descending))
                .ToList();
            (int Index, bool Desc)? dropped = Dropped(definition, keyField);
            if (dropped.HasValue && keys.All(k => k.Index != dropped.Value.Index)) keys.Add(dropped.Value);
            return keys;
        }

        /// <summary>В откатываемой транзакции: убрать ключ из сортировки и вернуть снятый — таблица собирается как без VK Tools.</summary>
        public static void Detach(ScheduleDefinition definition, int keyField)
        {
            if (keyField < 0) return;
            (int Index, bool Desc)? dropped = Dropped(definition, keyField);
            ScheduleFieldId keyId = definition.GetField(keyField).FieldId;
            IList<ScheduleSortGroupField> sorts = definition.GetSortGroupFields();
            for (int i = sorts.Count - 1; i >= 0; i--)
            {
                if (sorts[i].FieldId == keyId) definition.RemoveSortGroupField(i);
            }
            if (dropped.HasValue)
            {
                bool desc = dropped.Value.Desc;
                ScheduleFieldId id = definition.GetField(dropped.Value.Index).FieldId;
                if (definition.GetSortGroupFields().All(s => s.FieldId != id))
                {
                    definition.AddSortGroupField(new ScheduleSortGroupField(id, desc ? ScheduleSortOrder.Descending : ScheduleSortOrder.Ascending));
                }
            }
        }

        /// <summary>
        /// Добавить графу ключа (скрытую) и поставить её ключом сортировки сразу после последнего ключа с шапкой. Внутри открытой транзакции.
        /// </summary>
        /// <returns>null — готово; иначе причина (спецификация не изменена).</returns>
        public static string? Install(Document doc, ViewSchedule vs)
        {
            ElementId? paramId = ParamId(doc);
            if (paramId == null) return $"в проекте нет параметра «{ParamProfile.PositionKeyName}»";
            ScheduleDefinition definition = vs.Definition;
            int index = FieldIndex(definition, paramId);
            ScheduleField field;
            if (index < 0)
            {
                SchedulableField? schedulable = definition.GetSchedulableFields().FirstOrDefault(f => f.ParameterId == paramId);
                if (schedulable == null) return $"«{ParamProfile.PositionKeyName}» не привязан к категории спецификации";
                field = definition.AddField(schedulable);
                index = definition.GetFieldCount() - 1;
            }
            else
            {
                field = definition.GetField(index);
            }
            field.IsHidden = true;
            if (IsSorted(definition, index)) return null;
            field.ColumnHeading = Heading;

            IList<ScheduleSortGroupField> sorts = definition.GetSortGroupFields();
            int insertAt = 0;
            for (int i = 0; i < sorts.Count; i++)
            {
                if (sorts[i].ShowHeader) insertAt = i + 1;
            }
            var key = new ScheduleSortGroupField(field.FieldId, ScheduleSortOrder.Ascending);
            try
            {
                definition.InsertSortGroupField(key, insertAt);
                return null;
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                // Нет места (Revit 2022: не больше 4 ключей) — снимаем последний, он не нужен: ключ позиции задаёт порядок строк целиком
            }
            sorts = definition.GetSortGroupFields();
            int last = sorts.Count - 1;
            if (last < insertAt) return "нет места для ключа сортировки: все ключи с шапками";
            ScheduleSortGroupField removed = sorts[last];
            ScheduleField removedField = definition.GetField(removed.FieldId);
            definition.RemoveSortGroupField(last);
            definition.InsertSortGroupField(key, insertAt);
            field.ColumnHeading = Heading + DroppedMark + removedField.GetName() + (removed.SortOrder == ScheduleSortOrder.Descending ? DescMark : string.Empty);
            return null;
        }
    }
}
