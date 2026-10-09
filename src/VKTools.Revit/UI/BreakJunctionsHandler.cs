using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Mep;

namespace VKTools.Revit.UI
{
    /// <summary>
    /// «Разорвать стык» из отчёта объединения (D-020). Разъединяет только коннекторы между парами элементов,
    /// найденными как стык сети копии с сетью другой системы (на КрМа3: переход К4.2н ↔ фланцевый адаптер К4.1).
    /// Одна транзакция на все пары; элементы не двигаются (проверяется), соединение не разорвалось или
    /// элемент сдвинулся — откат всего. Отмена — Ctrl+Z.
    /// </summary>
    internal sealed class BreakJunctionsHandler : IExternalEventHandler
    {
        public const string OperationName = "VK Tools: разорвать стык";

        private List<(long A, long B)> _pairs = new List<(long A, long B)>();
        private Document? _document;

        public void Request(Document document, IEnumerable<(long A, long B)> pairs)
        {
            _document = document;
            _pairs = pairs.Distinct().ToList();
        }

        public void Execute(UIApplication app)
        {
            Document? doc = _document;
            try
            {
                if (doc == null || !doc.IsValidObject)
                {
                    TaskDialog.Show("VK Tools", "Модель, по которой построен отчёт, уже закрыта.");
                    return;
                }
                if (app.ActiveUIDocument == null || !app.ActiveUIDocument.Document.Equals(doc))
                {
                    TaskDialog.Show("VK Tools", $"Отчёт построен по модели «{doc.Title}». Перейдите в её окно и нажмите ещё раз.");
                    return;
                }
                if (doc.IsReadOnly)
                {
                    TaskDialog.Show("VK Tools", "Документ сейчас только для чтения: разорвать стык нельзя.");
                    return;
                }

                Host.Log.Info($"Разорвать стык «{doc.Title}»: пар {_pairs.Count}.");
                var done = new List<string>();
                var skipped = new List<string>();
                int systemsBefore = new FilteredElementCollector(doc).OfClass(typeof(PipingSystem)).GetElementCount();
                var collector = new FailureCollector();
                TransactionStatus status;
                using (Transaction transaction = FailureCollector.Start(doc, OperationName, collector))
                {
                    var geometry = new Dictionary<long, GeometryKey>();
                    var broken = new List<(Element A, Element B)>();
                    foreach ((long a, long b) in _pairs)
                    {
                        Element? ea = doc.GetElement(a.ToElementId());
                        Element? eb = doc.GetElement(b.ToElementId());
                        if (ea == null || eb == null)
                        {
                            skipped.Add($"{a} ↔ {b}: элемента уже нет в модели");
                            continue;
                        }
                        List<(Connector Own, Connector Other)> links = Links(ea, eb);
                        if (links.Count == 0)
                        {
                            skipped.Add($"{Describe(ea)} ↔ {Describe(eb)}: уже не соединены");
                            continue;
                        }
                        geometry[a] = GeometryKey.Of(ea);
                        geometry[b] = GeometryKey.Of(eb);
                        foreach ((Connector own, Connector other) in links) own.DisconnectFrom(other);
                        broken.Add((ea, eb));
                    }
                    if (broken.Count == 0)
                    {
                        transaction.RollBack();
                        Finish(doc, done, skipped, "Разрывать нечего.");
                        return;
                    }

                    doc.Regenerate();
                    foreach ((Element ea, Element eb) in broken)
                    {
                        if (Links(ea, eb).Count > 0) throw new InvalidOperationException($"{Describe(ea)} ↔ {Describe(eb)}: Revit не разъединил");
                        foreach (Element e in new[] { ea, eb })
                        {
                            if (!geometry[e.Id.ToLong()].SameAs(GeometryKey.Of(e))) throw new InvalidOperationException($"{Describe(e)} сдвинулся");
                        }
                        done.Add($"{Describe(ea)} ↔ {Describe(eb)}");
                    }
                    status = transaction.Commit();
                }
                foreach (string w in collector.Warnings) Host.Log.Warn("  предупреждение Revit: " + w);
                if (status != TransactionStatus.Committed)
                {
                    string why = collector.Errors.Count > 0 ? string.Join("; ", collector.Errors) : status.ToString();
                    Host.Log.Warn("  Revit отклонил разрыв: " + why);
                    TaskDialog.Show("VK Tools", "Revit не дал разорвать стык, модель не изменена:\n" + why);
                    return;
                }

                // Что стало с системами — для лога и подсказки: разрыв мог отделить часть сети без системы
                foreach ((long a, long b) in _pairs)
                {
                    Element? ea = doc.GetElement(a.ToElementId());
                    Element? eb = doc.GetElement(b.ToElementId());
                    if (ea != null && eb != null) Host.Log.Info($"  {Describe(ea)}: {Systems(doc, ea)} | {Describe(eb)}: {Systems(doc, eb)}");
                }
                int systemsAfter = new FilteredElementCollector(doc).OfClass(typeof(PipingSystem)).GetElementCount();
                string note = systemsAfter != systemsBefore
                    ? $"Систем трубопроводов было {systemsBefore}, стало {systemsAfter}: Revit пересобрал системы. Проверьте их в диспетчере систем."
                    : string.Empty;
                Finish(doc, done, skipped, note);
            }
            catch (Exception ex)
            {
                Host.Log.Error("Не удалось разорвать стык.", ex);
                TaskDialog.Show("VK Tools", "Не удалось разорвать стык, модель не изменена: " + ex.Message);
            }
        }

        public string GetName() => "VK Tools: разорвать стык";

        /// <summary>Физические соединения коннекторов element с other (логические ссылки на систему не в счёт).</summary>
        private static List<(Connector Own, Connector Other)> Links(Element element, Element other)
        {
            var result = new List<(Connector Own, Connector Other)>();
            long otherId = other.Id.ToLong();
            foreach (Connector own in Connectors.Piping(element))
            {
                try
                {
                    if (!own.IsConnected) continue;
                    foreach (Connector c in own.AllRefs)
                    {
                        if ((c.Owner is MEPCurve || c.Owner is FamilyInstance) && c.Owner.Id.ToLong() == otherId) result.Add((own, c));
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Недоступный коннектор пропускаем
                }
            }
            return result;
        }

        private static string Describe(Element e)
        {
            string name = e is FamilyInstance fi && fi.Symbol != null ? $"{fi.Symbol.FamilyName}: {fi.Symbol.Name}" : e.Name;
            return $"{e.Id.ToLong().ToString(CultureInfo.InvariantCulture)} «{name}»";
        }

        private static string Systems(Document doc, Element e)
        {
            HashSet<long> ids = Connectors.SystemsOf(e);
            return ids.Count == 0 ? "без системы" : string.Join(", ", ids.Select(id => "«" + (doc.GetElement(id.ToElementId())?.Name ?? id.ToString(CultureInfo.InvariantCulture)) + "»"));
        }

        private static void Finish(Document doc, List<string> done, List<string> skipped, string note)
        {
            foreach (string d in done) Host.Log.Info("  разорван: " + d);
            foreach (string s in skipped) Host.Log.Info("  пропущен: " + s);
            var lines = new List<string>();
            if (done.Count > 0)
            {
                lines.Add($"Разорвано стыков: {done.Count} (одна операция «{OperationName}», Ctrl+Z отменяет).");
                lines.Add("Дальше: в окне объединения «Обновить» и пробный прогон.");
            }
            if (skipped.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Пропущено:");
                lines.AddRange(skipped.Take(10));
            }
            if (note.Length > 0)
            {
                lines.Add(string.Empty);
                lines.Add(note);
            }
            TaskDialog.Show("VK Tools", string.Join("\n", lines));
        }
    }
}
