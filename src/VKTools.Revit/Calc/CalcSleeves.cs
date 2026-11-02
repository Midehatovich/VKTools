using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using VKTools.Core.Calc;
using VKTools.Core.Checks;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Calc
{
    /// <summary>
    /// Гильзы для шнура (REQ-CALC-008): экземпляры семейства по маске, длина — параметр экземпляра (или типа), труба —
    /// та, чья ось проходит через центр гильзы. Результат: труба → сумма длин её гильз, мм.
    /// </summary>
    internal static class CalcSleeves
    {
        private const double FeetToMm = 304.8;

        /// <summary>Допуск «ось трубы в гильзе» сверх половины поперечного размера гильзы, мм.</summary>
        private const double ToleranceMm = 5;

        public static Dictionary<long, (double Mm, int Count)> Read(Document doc, CalcOptions options, CalcPlan plan)
        {
            var result = new Dictionary<long, (double, int)>();
            string mask = options.SleeveFamily.Trim();
            List<FamilyInstance> sleeves = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).WhereElementIsNotElementType()
                .Cast<FamilyInstance>().Where(fi => fi.Symbol != null && FamilyMask.Matches(mask, fi.Symbol.FamilyName)).ToList();
            foreach (Family family in new FilteredElementCollector(doc).OfClass(typeof(Family)).Cast<Family>())
            {
                string name = family.Name;
                if (FamilyMask.Matches(mask, name) || name.IndexOf("ильз", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("sleeve", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    plan.SleeveFamilies.Add(name);
                }
            }
            plan.SleeveCount = sleeves.Count;
            if (sleeves.Count == 0)
            {
                plan.Issues.Add(Severity.Warning, $"гильз нет: ни одного экземпляра семейства по маске «{mask}» — шнур не посчитан",
                    $"Выберите семейство гильз на вкладке «{CalcOptions.SleevesTab}».");
                return result;
            }
            foreach (Parameter p in sleeves[0].Parameters.Cast<Parameter>().Concat(sleeves[0].Symbol.Parameters.Cast<Parameter>()))
            {
                // Отметка и смещение — тоже «длина» по типу данных, но не длина гильзы
                if (p.Definition != null && CalcParams.IsLength(p) && !NotSleeveLength(p.Definition.Name)) plan.SleeveLengthCandidates.Add(p.Definition.Name);
            }
            // Параметра длины может не быть (длина гильзы задана геометрией): тогда габарит вдоль трубы
            string lengthName = options.SleeveLength.Trim().Length > 0 ? options.SleeveLength.Trim() : AutoLength(plan.SleeveLengthCandidates) ?? CalcOptions.SleeveLengthGeometry;
            bool byGeometry = lengthName == CalcOptions.SleeveLengthGeometry;
            plan.SleeveLengthCandidates.Add(CalcOptions.SleeveLengthGeometry);
            if (!byGeometry && !sleeves.Any(sl => LengthOf(sl, lengthName) != null))
            {
                // Выбранный параметр пуст у всех гильз — это не длина гильзы; без шнура не оставляем
                plan.Issues.Add(Severity.Warning, $"параметр «{lengthName}» не задаёт длину ни у одной гильзы — длина взята по геометрии вдоль трубы",
                    $"На вкладке «{CalcOptions.SleevesTab}» выберите «{CalcOptions.SleeveLengthGeometry}» или параметр длины и сохраните настройку.");
                lengthName = CalcOptions.SleeveLengthGeometry;
                byGeometry = true;
            }
            plan.SleeveLengthUsed = lengthName;

            var geometry = new Options { DetailLevel = ViewDetailLevel.Fine };
            List<OuterHosts>? hosts = options.SleeveOuterOnly ? OuterHosts.Collect(doc, plan) : null;
            int outer = 0, inner = 0;
            foreach (FamilyInstance sleeve in sleeves)
            {
                long id = sleeve.Id.ToLong();
                Parameter? length = null;
                if (!byGeometry)
                {
                    length = LengthOf(sleeve, lengthName);
                    if (length == null)
                    {
                        plan.Issues.Add(Severity.Warning, $"у гильзы нет длины в «{lengthName}» (или это не длина) — гильза не учтена",
                            $"Заполните длину или выберите другой параметр (или «{CalcOptions.SleeveLengthGeometry}»).", id);
                        continue;
                    }
                }
                (long Id, XYZ Direction)? pipe = PipeInside(doc, sleeve);
                if (!pipe.HasValue)
                {
                    plan.Issues.Add(Severity.Warning, "в гильзе не найдена труба (ось трубы не проходит через центр гильзы) — гильза не учтена",
                        "Проверьте гильзу «Показать в модели»: смещена или труба удалена.", id);
                    continue;
                }
                double feet = length?.AsDouble() ?? AlongAxis(sleeve, pipe.Value.Direction, geometry);
                if (feet <= 0)
                {
                    plan.Issues.Add(Severity.Warning, "у гильзы нет геометрии, по которой можно измерить длину — гильза не учтена",
                        "Выберите параметр длины на вкладке «" + CalcOptions.SleevesTab + "».", id);
                    continue;
                }
                if (hosts != null)
                {
                    if (!OuterHosts.Crosses(hosts, sleeve, pipe.Value.Direction, feet))
                    {
                        inner++;
                        continue;
                    }
                    outer++;
                }
                result.TryGetValue(pipe.Value.Id, out (double Mm, int Count) sum);
                result[pipe.Value.Id] = (sum.Mm + feet * FeetToMm, sum.Count + 1);
            }
            if (hosts != null) plan.SleeveWalls = (outer, inner);
            return result;
        }

        /// <summary>Параметр длины гильзы (экземпляр, затем тип) с положительным значением; иначе null.</summary>
        private static Parameter? LengthOf(FamilyInstance sleeve, string name)
        {
            Parameter? p = CalcParams.Find(sleeve, name, out _) ?? CalcParams.Find(sleeve.Symbol, name, out _);
            return p != null && CalcParams.IsLength(p) && p.HasValue && p.AsDouble() > 0 ? p : null;
        }

        private static bool NotSleeveLength(string name) =>
            name.StartsWith("Отметка", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Смещение", StringComparison.OrdinalIgnoreCase)
            || name.IndexOf("Elevation", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Offset", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Габарит геометрии гильзы вдоль оси трубы, футы: разброс проекций вершин на направление трубы.</summary>
        private static double AlongAxis(FamilyInstance sleeve, XYZ direction, Options options)
        {
            double min = double.MaxValue, max = double.MinValue;
            void Add(XYZ p)
            {
                double t = p.DotProduct(direction);
                if (t < min) min = t;
                if (t > max) max = t;
            }
            void Walk(GeometryElement? element)
            {
                if (element == null) return;
                foreach (GeometryObject obj in element)
                {
                    switch (obj)
                    {
                        case Solid solid when solid.Volume > 0:
                            foreach (Edge edge in solid.Edges)
                            {
                                foreach (XYZ p in edge.Tessellate()) Add(p);
                            }
                            break;
                        case Mesh mesh:
                            foreach (XYZ p in mesh.Vertices) Add(p);
                            break;
                        case GeometryInstance instance:
                            Walk(instance.GetInstanceGeometry());
                            break;
                    }
                }
            }
            Walk(sleeve.get_Geometry(options));
            if (max > min) return max - min;
            // Без тел (семейство из линий) — габаритный контейнер в проекции на ось
            BoundingBoxXYZ? box = sleeve.get_BoundingBox(null);
            if (box == null) return 0;
            XYZ size = box.Max - box.Min;
            return Math.Abs(direction.X) * size.X + Math.Abs(direction.Y) * size.Y + Math.Abs(direction.Z) * size.Z;
        }

        /// <summary>«Длина», затем любое имя, где есть «длина» (ADSK_Размер_Длина…); несколько разных — не угадываем.</summary>
        private static string? AutoLength(IEnumerable<string> candidates)
        {
            List<string> names = candidates.Distinct(StringComparer.Ordinal).ToList();
            string? exact = names.FirstOrDefault(n => string.Equals(n, "Длина", StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            List<string> like = names.Where(n => n.IndexOf("длина", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            return like.Count == 1 ? like[0] : null;
        }

        /// <summary>Труба, ось которой ближе всех к центру гильзы и не дальше её поперечного полуразмера; направление оси в этой точке.</summary>
        private static (long Id, XYZ Direction)? PipeInside(Document doc, FamilyInstance sleeve)
        {
            BoundingBoxXYZ? box = sleeve.get_BoundingBox(null);
            if (box == null) return null;
            XYZ center = (box.Min + box.Max) / 2;
            XYZ size = box.Max - box.Min;
            double[] halves = { size.X / 2, size.Y / 2, size.Z / 2 };
            Array.Sort(halves);
            // Гильза вдоль оси: два малых полуразмера (радиус) и один большой (половина длины). Наклонная — средний больше, допуск шире
            double tolerance = halves[1] + ToleranceMm / FeetToMm;
            var filter = new BoundingBoxIntersectsFilter(new Outline(box.Min, box.Max));
            (long, XYZ)? best = null;
            double bestDistance = double.MaxValue;
            foreach (Pipe pipe in new FilteredElementCollector(doc).OfClass(typeof(Pipe)).WherePasses(filter).Cast<Pipe>())
            {
                Curve? curve = (pipe.Location as LocationCurve)?.Curve;
                IntersectionResult? projected = curve?.Project(center);
                if (projected == null || projected.Distance > tolerance || projected.Distance >= bestDistance) continue;
                XYZ direction = curve is Line line ? line.Direction : curve!.ComputeDerivatives(projected.Parameter, false).BasisX.Normalize();
                best = (pipe.Id.ToLong(), direction);
                bestDistance = projected.Distance;
            }
            return best;
        }
    }

    /// <summary>
    /// Наружные (подпорные) стены и фундаменты одного документа — модели или загруженной связи (решение ведущего 2026-10-29:
    /// шнур только в гильзах наружных стен и фундамента). Гильза засчитывается, если тонкий брус по её оси пересекает такой элемент.
    /// </summary>
    internal sealed class OuterHosts
    {
        private const double FeetToMm = 304.8;

        /// <summary>Полусторона бруса по оси гильзы, мм; брус короче гильзы, чтобы не задеть стену, к которой гильза примыкает торцом.</summary>
        private const double HalfSideMm = 5;
        private const double LengthShare = 0.8;

        private OuterHosts(Document document, Transform toHost, List<ElementId> ids)
        {
            Document = document;
            ToHost = toHost;
            Ids = ids;
        }

        public Document Document { get; }

        /// <summary>Координаты документа → координаты модели ВК.</summary>
        public Transform ToHost { get; }

        public List<ElementId> Ids { get; }

        public static List<OuterHosts>? Collect(Document doc, CalcPlan plan)
        {
            var list = new List<OuterHosts>();
            Add(list, doc, Transform.Identity, "модель", plan);
            int unloaded = 0;
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document? linked = link.GetLinkDocument();
                if (linked == null)
                {
                    unloaded++;
                    continue;
                }
                Add(list, linked, link.GetTotalTransform(), linked.Title, plan);
            }
            if (unloaded > 0)
            {
                plan.Issues.Add(Severity.Warning, $"связей не загружено: {unloaded} — их стены и фундаменты не учтены при отборе гильз для шнура",
                    "Загрузите связи АР и КР и нажмите «Обновить».");
            }
            if (list.Count == 0)
            {
                // Не считать шнур во всех гильзах молча: связи часто просто выгружены (КрМа3, 02.11 — шнур вырос в разы)
                plan.Issues.Add(Severity.Error, "в модели и загруженных связях нет наружных стен и фундаментов — шнур не посчитать, запись запрещена"
                    + (unloaded > 0 ? $" (не загружено связей: {unloaded})" : string.Empty),
                    $"Загрузите связи АР/КР и нажмите «Обновить» или снимите «Только гильзы в наружных стенах» на вкладке «{CalcOptions.SleevesTab}».");
                plan.WriteBlockedReason ??= "не найдены наружные стены и фундаменты для шнура (связи не загружены?)";
                return null;
            }
            return list;
        }

        private static void Add(List<OuterHosts> list, Document document, Transform toHost, string title, CalcPlan plan)
        {
            var ids = new List<ElementId>();
            foreach (Wall wall in new FilteredElementCollector(document).OfClass(typeof(Wall)).WhereElementIsNotElementType().Cast<Wall>())
            {
                if (IsOuter(wall)) ids.Add(wall.Id);
            }
            int walls = ids.Count;
            ids.AddRange(new FilteredElementCollector(document).OfCategory(BuiltInCategory.OST_StructuralFoundation).WhereElementIsNotElementType().ToElementIds());
            if (ids.Count == 0) return;
            list.Add(new OuterHosts(document, toHost, ids));
            plan.SleeveWallSources.Add($"{title}: стен {walls}, фундаментов {ids.Count - walls}");
        }

        /// <summary>Функция типа стены: наружная, фундамент или подпорная.</summary>
        private static bool IsOuter(Wall wall)
        {
            Parameter? function = wall.WallType?.get_Parameter(BuiltInParameter.FUNCTION_PARAM);
            if (function == null || !function.HasValue) return false;
            var value = (WallFunction)function.AsInteger();
            return value == WallFunction.Exterior || value == WallFunction.Foundation || value == WallFunction.Retaining;
        }

        /// <summary>Брус по оси гильзы (центр — центр габарита, длина — <see cref="LengthShare"/> длины гильзы) пересекает элемент одного из документов.</summary>
        public static bool Crosses(List<OuterHosts> hosts, FamilyInstance sleeve, XYZ direction, double lengthFeet)
        {
            BoundingBoxXYZ? box = sleeve.get_BoundingBox(null);
            if (box == null) return false;
            XYZ center = (box.Min + box.Max) / 2;
            double length = Math.Max(lengthFeet * LengthShare, 2 * HalfSideMm / FeetToMm);
            Solid probe;
            try
            {
                probe = Probe(center, direction.Normalize(), length, HalfSideMm / FeetToMm);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
            foreach (OuterHosts host in hosts)
            {
                Solid local = host.ToHost.IsIdentity ? probe : SolidUtils.CreateTransformed(probe, host.ToHost.Inverse);
                BoundingBoxXYZ b = local.GetBoundingBox();
                XYZ min = b.Transform.OfPoint(b.Min), max = b.Transform.OfPoint(b.Max);
                var outline = new Outline(new XYZ(Math.Min(min.X, max.X), Math.Min(min.Y, max.Y), Math.Min(min.Z, max.Z)),
                    new XYZ(Math.Max(min.X, max.X), Math.Max(min.Y, max.Y), Math.Max(min.Z, max.Z)));
                bool any = new FilteredElementCollector(host.Document, host.Ids)
                    .WherePasses(new BoundingBoxIntersectsFilter(outline))
                    .WherePasses(new ElementIntersectsSolidFilter(local))
                    .Any();
                if (any) return true;
            }
            return false;
        }

        /// <summary>Квадратный брус со стороной 2·half вдоль direction, центр — center.</summary>
        private static Solid Probe(XYZ center, XYZ direction, double length, double half)
        {
            XYZ u = Math.Abs(direction.Z) < 0.9 ? direction.CrossProduct(XYZ.BasisZ).Normalize() : direction.CrossProduct(XYZ.BasisX).Normalize();
            XYZ v = direction.CrossProduct(u).Normalize();
            XYZ start = center - direction * (length / 2);
            XYZ[] corners =
            {
                start + u * half + v * half, start - u * half + v * half, start - u * half - v * half, start + u * half - v * half,
            };
            var loop = new CurveLoop();
            for (int i = 0; i < corners.Length; i++) loop.Append(Line.CreateBound(corners[i], corners[(i + 1) % corners.Length]));
            return GeometryCreationUtilities.CreateExtrusionGeometry(new[] { loop }, direction, length);
        }
    }
}
