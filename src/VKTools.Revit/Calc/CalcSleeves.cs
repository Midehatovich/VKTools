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
                if (p.Definition != null && CalcParams.IsLength(p)) plan.SleeveLengthCandidates.Add(p.Definition.Name);
            }
            string? lengthName = options.SleeveLength.Trim().Length > 0 ? options.SleeveLength.Trim() : AutoLength(plan.SleeveLengthCandidates);
            if (lengthName == null)
            {
                plan.Issues.Add(Severity.Error, "не задан параметр длины гильзы, а угадать его по имени «Длина» не удалось — шнур не посчитан",
                    $"Выберите параметр на вкладке «{CalcOptions.SleevesTab}».");
                plan.WriteBlockedReason ??= "не задан параметр длины гильзы";
                return result;
            }
            plan.SleeveLengthUsed = lengthName;

            int noPipe = 0;
            foreach (FamilyInstance sleeve in sleeves)
            {
                long id = sleeve.Id.ToLong();
                Parameter? length = CalcParams.Find(sleeve, lengthName, out _) ?? CalcParams.Find(sleeve.Symbol, lengthName, out _);
                if (length == null || !CalcParams.IsLength(length) || !length.HasValue || length.AsDouble() <= 0)
                {
                    plan.Issues.Add(Severity.Warning, $"у гильзы нет длины в «{lengthName}» (или это не длина) — гильза не учтена",
                        "Заполните длину или выберите другой параметр.", id);
                    continue;
                }
                long? pipe = PipeInside(doc, sleeve);
                if (!pipe.HasValue)
                {
                    noPipe++;
                    plan.Issues.Add(Severity.Warning, "в гильзе не найдена труба (ось трубы не проходит через центр гильзы) — гильза не учтена",
                        "Проверьте гильзу «Показать в модели»: смещена или труба удалена.", id);
                    continue;
                }
                result.TryGetValue(pipe.Value, out (double Mm, int Count) sum);
                result[pipe.Value] = (sum.Mm + length.AsDouble() * FeetToMm, sum.Count + 1);
            }
            return result;
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

        /// <summary>Труба, ось которой ближе всех к центру гильзы и не дальше её поперечного полуразмера.</summary>
        private static long? PipeInside(Document doc, FamilyInstance sleeve)
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
            long? best = null;
            double bestDistance = double.MaxValue;
            foreach (Pipe pipe in new FilteredElementCollector(doc).OfClass(typeof(Pipe)).WherePasses(filter).Cast<Pipe>())
            {
                Curve? curve = (pipe.Location as LocationCurve)?.Curve;
                IntersectionResult? projected = curve?.Project(center);
                if (projected == null || projected.Distance > tolerance || projected.Distance >= bestDistance) continue;
                best = pipe.Id.ToLong();
                bestDistance = projected.Distance;
            }
            return best;
        }
    }
}
