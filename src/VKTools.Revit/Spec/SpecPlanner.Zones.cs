using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Progress;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Spec
{
    /// <summary>
    /// «Р_Зона» (0.17.6): одно поле для фильтра «Создать спецификацию» по зонам. Значение — из помещения АР («Р_Номер помещения»),
    /// этажа или своей разметки (любой текстовый параметр, например ADSK_Группирование на ПТ Пушкарева: «Насосная», «Ниже 0,000»…).
    /// Значения помещения и этажа берутся те, что этот же прогон пишет (или уже записаны). Вложенные — зона родителя, изоляция — основы.
    /// Пустой источник — пустая зона (элемент переехал — старое значение не остаётся).
    /// </summary>
    internal sealed partial class SpecPlanner
    {
        private void PlanZones(SystemFloorSettings sf, List<Element> elements, List<Element> insulations)
        {
            const SpecModule M = SpecModule.SystemFloor;
            if (!sf.WritesZone) return;
            ParamKey key = ParamProfile.TargetKey(ParamRole.Zone);
            ParamKey? source = sf.Zone switch
            {
                ZoneSource.Room => ParamProfile.TargetKey(ParamRole.Room),
                ZoneSource.Floor => sf.FloorParam,
                _ => sf.ZoneParam,
            };
            if (source == null)
            {
                _plan.Notes.Add($"Зоны: источник «{sf.ZoneText()}» не задан (нет параметра этажа?) — «{key.Name}» не пишется.");
                return;
            }
            if (source.Equals(key))
            {
                _plan.Notes.Add($"Зоны: источник — сам «{key.Name}» (заполняется вручную) — плагин его не пишет.");
                return;
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var resolved = new Dictionary<long, string>();
            var written = new HashSet<long>();
            var missing = new SortedDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            int empty = 0;
            string label = sf.ZoneText();

            void Write(Element e, string value, bool nested)
            {
                long id = e.Id.ToLong();
                if (!written.Add(id) || IsServiceItem(e)) return;
                Parameter? p = _access.Find(e, key);
                if (p == null)
                {
                    if (!nested)
                    {
                        string category = e.Category?.Name ?? "?";
                        missing[category] = missing.TryGetValue(category, out int n) ? n + 1 : 1;
                    }
                    return;
                }
                if (p.StorageType != StorageType.String)
                {
                    _plan.AddIssue(M, Severity.Error, key.Name, $"параметр не текстовый ({ParamAccess.DataTypeLabel(p)})", FamilyLabel(e), id);
                    return;
                }
                string old = p.AsString() ?? string.Empty;
                _pending[(id, key.Id)] = ParamValue.OfText(value);
                if (string.Equals(old.Trim(), value, StringComparison.Ordinal))
                {
                    if (value.Length > 0) _plan.CountUnchanged(M);
                    return;
                }
                if (p.IsReadOnly)
                {
                    _plan.AddIssue(M, Severity.Error, key.Name, "только для чтения: связан в семействе или задан формулой", FamilyLabel(e), id);
                    return;
                }
                if (Guard(M, e, p, key.Name, FamilyLabel(e))) return;
                AddChange(M, e, key, old, ParamValue.OfText(value), value.Length > 0 ? label : label + ": пусто", null);
            }

            void WriteTree(Element e, string value, int depth)
            {
                Write(e, value, depth > 0);
                if (depth >= MaxNestedLevel || !(e is FamilyInstance fi)) return;
                foreach (ElementId sub in fi.GetSubComponentIds())
                {
                    Element? child = _doc.GetElement(sub);
                    if (child != null) WriteTree(child, value, depth + 1);
                }
            }

            for (int i = 0; i < elements.Count; i++)
            {
                if (i % 500 == 0)
                {
                    _progress.Report($"Зоны: элемент {i + 1} из {elements.Count}", i, elements.Count);
                    _progress.ThrowIfCancelled();
                }
                Element e = elements[i];
                if (IsServiceItem(e)) continue;
                // Вложенные пишутся от родителя (ниже по дереву), сами по себе — нет
                if (e is FamilyInstance fi && fi.SuperComponent != null) continue;
                string value = ZoneOf(e, source);
                resolved[e.Id.ToLong()] = value;
                if (value.Length == 0) empty++;
                else counts[value] = counts.TryGetValue(value, out int n) ? n + 1 : 1;
                WriteTree(e, value, 0);
            }
            foreach (Element ins in insulations)
            {
                Element? host = ins is InsulationLiningBase lining ? _doc.GetElement(lining.HostElementId) : null;
                if (host == null) continue;
                if (!resolved.TryGetValue(host.Id.ToLong(), out string? value)) value = ZoneOf(host, source);
                Write(ins, value, false);
            }

            foreach (KeyValuePair<string, int> m in missing)
            {
                _plan.AddIssue(M, Severity.Info, key.Name, $"нет параметра у категории «{m.Key}»", m.Key, null, m.Value)
                    .Action = "Нажмите «Применить»: плагин добавит свой параметр в проект.";
            }
            if (empty > 0)
            {
                _plan.AddIssue(M, Severity.Warning, key.Name, $"источник зоны ({label}) пустой — зона не задана", "элементы без зоны", null, empty)
                    .Action = "Такие элементы не попадут в спецификацию с выбором зон. Заполните источник (разметку, помещение) или оставьте все зоны отмеченными.";
            }
            string list = string.Join(", ", counts.OrderBy(c => c.Key, Schedules.NaturalComparer.Instance).Take(12)
                .Select(c => $"«{c.Key}» {c.Value.ToString(CultureInfo.InvariantCulture)}"));
            _plan.Notes.Add($"Зоны: «{key.Name}» ← {label}; зон {counts.Count}" + (list.Length > 0 ? " (" + list + ")" : string.Empty)
                + $", без зоны {empty.ToString(CultureInfo.InvariantCulture)}.");
        }

        /// <summary>Значение источника зоны: запланированное этим прогоном (помещение, этаж), иначе текущее — экземпляра, затем типа.</summary>
        private string ZoneOf(Element e, ParamKey source)
        {
            if (_pending.TryGetValue((e.Id.ToLong(), source.Id), out ParamValue? planned)) return (planned.Text ?? string.Empty).Trim();
            Parameter? p = _access.Find(e, source);
            if (p == null)
            {
                Element? type = TypeOf(e);
                if (type != null) p = _access.Find(type, source);
            }
            if (p == null) return string.Empty;
            string? text = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            return (text ?? string.Empty).Trim();
        }
    }
}
