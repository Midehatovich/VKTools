using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using VKTools.Core.Spec;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Spec
{
    /// <summary>Помещение АР в координатах модели ВК.</summary>
    internal sealed class RoomShape
    {
        public RoomShape(RoomLabel label, string source)
        {
            Label = label;
            Source = source;
        }

        public RoomLabel Label { get; }

        /// <summary>Модель или связь, откуда помещение.</summary>
        public string Source { get; }

        public List<IReadOnlyList<(double X, double Y)>> Loops { get; } = new List<IReadOnlyList<(double X, double Y)>>();

        public double MinX { get; set; } = double.MaxValue;

        public double MinY { get; set; } = double.MaxValue;

        public double MaxX { get; set; } = double.MinValue;

        public double MaxY { get; set; } = double.MinValue;

        public double ZMin { get; set; }

        public double ZMax { get; set; }
    }

    /// <summary>
    /// Помещения АР (0.16.0): из самой модели и из загруженных связей (как их сделали АР), с контурами по чистовой отделке
    /// и высотой по объёму помещения. Не размещённые и с нулевой площадью пропускаются. Только чтение.
    /// </summary>
    internal sealed class RoomCatalog
    {
        /// <summary>Выше потолка помещения до 1,5 м — ещё это помещение (трубы под перекрытием), футы.</summary>
        private const double CeilingZone = 1.5 / 0.3048;

        /// <summary>Ниже пола до 1 м — помещение сверху (приямки, трубы в подготовке), футы.</summary>
        private const double PitZone = 1.0 / 0.3048;

        public List<RoomShape> Rooms { get; } = new List<RoomShape>();

        /// <summary>«модель: 0», «UNI_..._AR_R22: 198» — для отчёта.</summary>
        public List<string> Sources { get; } = new List<string>();

        /// <summary>Значения, которые не различить (одинаковые номер, имя и уровень).</summary>
        public List<string> Duplicates { get; } = new List<string>();

        public int Unplaced { get; private set; }

        public static RoomCatalog Read(Document doc, bool geometry)
        {
            var catalog = new RoomCatalog();
            catalog.ReadDocument(doc, Transform.Identity, "модель", geometry);
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document? linked = link.GetLinkDocument();
                if (linked == null) continue;
                catalog.ReadDocument(linked, link.GetTotalTransform(), linked.Title, geometry);
            }
            catalog.Duplicates.AddRange(RoomKeys.Assign(catalog.Rooms.Select(r => r.Label).ToList()));
            return catalog;
        }

        private void ReadDocument(Document doc, Transform transform, string source, bool geometry)
        {
            int count = 0;
            var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
            foreach (Room room in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>())
            {
                if (room.Location == null || room.Area <= 0)
                {
                    Unplaced++;
                    continue;
                }
                string name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty;
                var shape = new RoomShape(new RoomLabel(room.Number ?? string.Empty, name, room.Level?.Name ?? string.Empty), source);
                // Без контура помещение остаётся в списке (номера не «съезжают» между окном и записью), но точек не находит
                if (geometry) ReadShape(room, transform, options, shape);
                Rooms.Add(shape);
                count++;
            }
            if (count > 0 || doc.IsLinked == false) Sources.Add($"{source}: {count}");
        }

        private static bool ReadShape(Room room, Transform transform, SpatialElementBoundaryOptions options, RoomShape shape)
        {
            try
            {
                IList<IList<BoundarySegment>>? loops = room.GetBoundarySegments(options);
                if (loops == null || loops.Count == 0) return false;
                foreach (IList<BoundarySegment> loop in loops)
                {
                    var points = new List<(double X, double Y)>();
                    foreach (BoundarySegment segment in loop)
                    {
                        IList<XYZ> tessellated = segment.GetCurve().Tessellate();
                        for (int i = 0; i < tessellated.Count - 1; i++)   // конец отрезка — начало следующего
                        {
                            XYZ p = transform.OfPoint(tessellated[i]);
                            points.Add((p.X, p.Y));
                            shape.MinX = Math.Min(shape.MinX, p.X);
                            shape.MinY = Math.Min(shape.MinY, p.Y);
                            shape.MaxX = Math.Max(shape.MaxX, p.X);
                            shape.MaxY = Math.Max(shape.MaxY, p.Y);
                        }
                    }
                    if (points.Count >= 3) shape.Loops.Add(points);
                }
                BoundingBoxXYZ? box = room.get_BoundingBox(null);
                if (box == null || shape.Loops.Count == 0) return false;
                shape.ZMin = transform.OfPoint(box.Min).Z;
                shape.ZMax = transform.OfPoint(box.Max).Z;
                if (shape.ZMax < shape.ZMin) (shape.ZMin, shape.ZMax) = (shape.ZMax, shape.ZMin);
                return true;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                Host.Log.Warn($"Помещения: контур «{room.Number} {room.Name}» не прочитан — {ex.Message}");
                return false;
            }
        }

        /// <summary>Помещение точки модели ВК или null.</summary>
        public RoomShape? Find(XYZ point)
        {
            var hits = new List<RoomShape>();
            foreach (RoomShape r in Rooms)
            {
                if (point.X < r.MinX || point.X > r.MaxX || point.Y < r.MinY || point.Y > r.MaxY) continue;
                if (point.Z < r.ZMin - PitZone || point.Z > r.ZMax + CeilingZone) continue;
                if (RoomGeometry.Contains(r.Loops, point.X, point.Y)) hits.Add(r);
            }
            if (hits.Count == 0) return null;
            if (hits.Count == 1 && point.Z >= hits[0].ZMin && point.Z <= hits[0].ZMax) return hits[0];
            int index = RoomGeometry.PickByHeight(hits.Select(h => (h.ZMin, h.ZMax)).ToList(), point.Z, CeilingZone, PitZone);
            return index < 0 ? null : hits[index];
        }
    }
}
