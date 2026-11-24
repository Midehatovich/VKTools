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
    /// «Р_Номер помещения» (0.16.0, насосная Пушкарева): помещение АР, где стоит элемент. Помещения — из модели и загруженных связей.
    /// Трубы — по большей части длины (точки через 0,3 м), остальные — по точке вставки (или центру габарита). Вложенные — помещение
    /// родителя, изоляция — основы. Элемент вне помещений получает пустое значение (переехал — значение не остаётся старым).
    /// </summary>
    internal sealed partial class SpecPlanner
    {
        /// <summary>Шаг точек по длине трубы, футы (~0,3 м).</summary>
        private const double RoomSampleStep = 1.0;

        private const int MaxRoomSamples = 200;

        private void PlanRooms(SystemFloorSettings sf, List<Element> elements, List<Element> insulations)
        {
            const SpecModule M = SpecModule.SystemFloor;
            if (!sf.WriteRoom) return;
            ParamKey key = ParamProfile.TargetKey(ParamRole.Room);
            _progress.Report("Помещения АР: читаю контуры из модели и связей…");
            RoomCatalog catalog = RoomCatalog.Read(_doc, geometry: true);
            if (catalog.Rooms.Count == 0)
            {
                _plan.Notes.Add($"Помещения: в модели и загруженных связях помещений нет — «{key.Name}» не пишется.");
                return;
            }
            foreach (string duplicate in catalog.Duplicates)
            {
                _plan.AddIssue(M, Severity.Warning, key.Name, $"помещения «{duplicate}» не различить: одинаковые номер, имя и уровень", string.Empty)
                    .Action = "Попросите АР перенумеровать: спецификация такого помещения соберёт все одноимённые.";
            }

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var resolved = new Dictionary<long, string>();
            var written = new HashSet<long>();
            var missing = new SortedDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            int outside = 0;

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
                AddChange(M, e, key, old, ParamValue.OfText(value), value.Length > 0 ? "помещение АР" : "вне помещений АР", null);
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
                if (i % 200 == 0)
                {
                    _progress.Report($"Помещения АР: элемент {i + 1} из {elements.Count}", i, elements.Count);
                    _progress.ThrowIfCancelled();
                }
                Element e = elements[i];
                if (IsServiceItem(e)) continue;
                string value = RoomOf(e, catalog);
                resolved[e.Id.ToLong()] = value;
                if (value.Length == 0) outside++;
                else counts[value] = counts.TryGetValue(value, out int n) ? n + 1 : 1;
                WriteTree(e, value, 0);
            }
            foreach (Element ins in insulations)
            {
                Element? host = ins is InsulationLiningBase lining ? _doc.GetElement(lining.HostElementId) : null;
                if (host == null) continue;
                if (!resolved.TryGetValue(host.Id.ToLong(), out string? value)) value = RoomOf(host, catalog);
                Write(ins, value, false);
            }

            foreach (KeyValuePair<string, int> m in missing)
            {
                _plan.AddIssue(M, Severity.Info, key.Name, $"нет параметра у категории «{m.Key}»", m.Key, null, m.Value)
                    .Action = "Нажмите «Применить»: плагин добавит свой параметр в проект.";
            }
            string top = string.Join(", ", counts.OrderByDescending(c => c.Value).Take(5).Select(c => $"«{c.Key}» {c.Value.ToString(CultureInfo.InvariantCulture)}"));
            _plan.Notes.Add($"Помещения: {catalog.Rooms.Count} помещений АР ({string.Join("; ", catalog.Sources)}); элементов в помещениях "
                + $"{counts.Values.Sum()} в {counts.Count} помещениях, вне помещений {outside} (пустое «{key.Name}»)"
                + (top.Length > 0 ? "; больше всего: " + top : string.Empty) + ".");
        }

        /// <summary>Значение «Р_Номер помещения» для элемента: помещение большей части точек; вне помещений больше — пусто.</summary>
        private static string RoomOf(Element e, RoomCatalog catalog)
        {
            var votes = new Dictionary<RoomShape, int>();
            int none = 0;
            foreach (XYZ p in SamplePoints(e))
            {
                RoomShape? room = catalog.Find(p);
                if (room == null) none++;
                else votes[room] = votes.TryGetValue(room, out int n) ? n + 1 : 1;
            }
            if (votes.Count == 0) return string.Empty;
            KeyValuePair<RoomShape, int> best = votes.OrderByDescending(v => v.Value).First();
            return best.Value >= none ? best.Key.Label.Key : string.Empty;
        }

        private static IEnumerable<XYZ> SamplePoints(Element e)
        {
            if (e.Location is LocationCurve lc && lc.Curve != null && lc.Curve.IsBound)
            {
                Curve curve = lc.Curve;
                int n = Math.Max(1, Math.Min(MaxRoomSamples, (int)Math.Ceiling(curve.Length / RoomSampleStep)));
                for (int i = 0; i < n; i++) yield return curve.Evaluate((i + 0.5) / n, true);
                yield break;
            }
            if (e.Location is LocationPoint lp)
            {
                yield return lp.Point;
                yield break;
            }
            BoundingBoxXYZ? box = e.get_BoundingBox(null);
            if (box != null) yield return (box.Min + box.Max) / 2;
        }
    }
}
