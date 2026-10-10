using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Logging;
using VKTools.Core.Mep;
using VKTools.Core.Progress;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Mep
{
    /// <summary>
    /// «Разрывы стояков»: свободные вертикальные концы труб, фитингов и арматуры одного типа системы
    /// на одной оси — верхний конец нижнего куска и нижний конец верхнего. Только чтение.
    /// Типичная причина — этаж скопирован, а стояк не соединён с нижним, и Revit держит куски как разные системы.
    /// </summary>
    internal static class RiserGapScanner
    {
        public const string CheckName = "Разрывы стояков";
        private const double FeetToMm = 304.8;

        public static CheckReport Run(Document doc, IOperationProgress progress, OperationTimings timings, RiserGapOptions options)
        {
            var report = new CheckReport(CheckName, doc.Title, DateTime.Now)
            {
                PluginVersion = BuildInfo.PluginVersion,
                RevitVersion = BuildInfo.RevitVersion,
            };
            report.RuleSources.Add(string.Format(CultureInfo.InvariantCulture,
                "Допуски поиска: оси ±{0:0} мм, зазор до {1:0} мм, нахлёст до {2:0} мм, отклонение от вертикали до {3:0}°; только концы одного типа системы, без связанных файлов",
                options.AxisToleranceMm, options.MaxGapMm, options.MaxOverlapMm, Math.Acos(options.VerticalCosine) * 180 / Math.PI));

            List<OpenEnd> ends;
            using (timings.Measure("поиск свободных концов")) ends = CollectOpenEnds(doc, progress, options);
            progress.Report("Сопоставление концов…");
            IReadOnlyList<RiserGap> gaps;
            using (timings.Measure("сопоставление")) gaps = RiserGaps.Find(ends, options);

            foreach (RiserGap gap in gaps)
            {
                Element? lower = doc.GetElement(gap.Lower.ElementId.ToElementId());
                var finding = new CheckFinding(CheckName, Severity.Warning, Message(gap))
                {
                    Current = "не соединены",
                    Expected = "соединены",
                    Action = Action(gap),
                };
                if (lower != null) finding.Describe(lower);
                finding.RelatedElementIds.Add(gap.Lower.ElementId);
                finding.RelatedElementIds.Add(gap.Upper.ElementId);
                report.Findings.Add(finding);
            }

            int vertical = ends.Count(e => Math.Abs(e.DirectionZ) >= options.VerticalCosine);
            report.Findings.Add(new CheckFinding(CheckName, Severity.Info, gaps.Count == 0
                ? $"Разрывов стояков не найдено. Свободных вертикальных концов в модели: {vertical}."
                : $"Найдено разрывов: {gaps.Count}. Свободных вертикальных концов в модели: {vertical}. Пары собраны по ближайшему концу; проверьте каждую в модели."));
            return report;
        }

        private static List<OpenEnd> CollectOpenEnds(Document doc, IOperationProgress progress, RiserGapOptions options)
        {
            var filter = new ElementMulticategoryFilter(new[]
            {
                BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory,
            });
            List<Element> elements = new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType().ToList();
            var ends = new List<OpenEnd>();
            var levelNames = new Dictionary<long, string>();
            for (int i = 0; i < elements.Count; i++)
            {
                if (i % 500 == 0)
                {
                    progress.Report($"Свободные концы труб, фитингов и арматуры: {i} из {elements.Count}", i, elements.Count);
                    progress.ThrowIfCancelled();
                }
                Element e = elements[i];
                DesignOption? option = e.DesignOption;
                if (option != null && !option.IsPrimary) continue;
                foreach (Connector c in Connectors.Piping(e))
                {
                    OpenEnd? end = ToOpenEnd(doc, e, c, options, levelNames);
                    if (end != null) ends.Add(end);
                }
            }
            return ends;
        }

        private static OpenEnd? ToOpenEnd(Document doc, Element e, Connector c, RiserGapOptions options, Dictionary<long, string> levelNames)
        {
            try
            {
                if (c.ConnectorType != ConnectorType.End || c.IsConnected) return null;
                XYZ direction = c.CoordinateSystem.BasisZ;
                if (Math.Abs(direction.Z) < options.VerticalCosine) return null;
                XYZ o = c.Origin;
                var end = new OpenEnd(e.Id.ToLong(), c.Id, o.X * FeetToMm, o.Y * FeetToMm, o.Z * FeetToMm, direction.Z);

                ElementId typeId = e.get_Parameter(BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM)?.AsElementId() ?? ElementId.InvalidElementId;
                // Без типа системы (висящий фитинг) не с чем сравнивать — не угадываем пару
                if (typeId.ToLong() < 1) return null;
                end.SystemTypeId = typeId.ToLong();
                end.SystemTypeName = doc.GetElement(typeId)?.Name ?? string.Empty;
                long systemId = Connectors.SystemId(c);
                if (systemId < 0 && e is MEPCurve curve && curve.MEPSystem != null) systemId = curve.MEPSystem.Id.ToLong();
                end.SystemName = systemId > 0 ? doc.GetElement(systemId.ToElementId())?.Name ?? string.Empty : string.Empty;
                try
                {
                    end.DiameterMm = Math.Round(c.Radius * 2 * FeetToMm, 1);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Не круглый коннектор — диаметр не сравниваем
                }
                end.Level = LevelName(doc, e, levelNames);
                return end;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
        }

        private static string LevelName(Document doc, Element e, Dictionary<long, string> cache)
        {
            ElementId levelId = e.LevelId;
            if ((levelId == null || levelId.ToLong() < 1) && e is MEPCurve curve) levelId = curve.ReferenceLevel?.Id ?? ElementId.InvalidElementId;
            if (levelId == null || levelId.ToLong() < 1) return string.Empty;
            long key = levelId.ToLong();
            if (!cache.TryGetValue(key, out string? name))
            {
                name = doc.GetElement(levelId)?.Name ?? string.Empty;
                cache[key] = name;
            }
            return name;
        }

        private static string Message(RiserGap gap)
        {
            string distance = Math.Abs(gap.GapMm) < 0.5 ? "концы совпадают"
                : gap.GapMm > 0 ? $"зазор {gap.GapMm:0} мм"
                : $"куски заходят друг за друга на {-gap.GapMm:0} мм";
            string diameter = gap.SameDiameter
                ? $"Ø{gap.Lower.DiameterMm:0.#}"
                : $"Ø{gap.Lower.DiameterMm:0.#} снизу и Ø{gap.Upper.DiameterMm:0.#} сверху — нужен переход";
            return $"Стояк «{gap.Lower.SystemTypeName}» не соединён: {distance}, смещение осей {gap.OffsetMm:0} мм, {diameter}. "
                + $"Снизу Id {gap.Lower.ElementId} ({Name(gap.Lower)}), сверху Id {gap.Upper.ElementId} ({Name(gap.Upper)}).";
        }

        private static string Name(OpenEnd end)
        {
            string system = string.IsNullOrEmpty(end.SystemName) ? "без системы" : $"«{end.SystemName}»";
            return string.IsNullOrEmpty(end.Level) ? system : $"{system}, {end.Level}";
        }

        private static string Action(RiserGap gap)
        {
            if (Math.Abs(gap.GapMm) < 0.5)
            {
                return "Потяните ручку конца верхней трубы и отпустите на конце нижней (или «Изменить» → «Соединить»): Revit соединит куски и объединит системы.";
            }
            return gap.GapMm > 0
                ? "Дотяните конец одной трубы до другой: Revit поставит муфту и объединит системы."
                : "Укоротите одну из труб до конца другой и соедините их.";
        }
    }
}
