using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using VKTools.Core.Logging;
using VKTools.Core.Mep;
using VKTools.Core.Progress;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.Mep
{
    internal sealed class HangSearchResult
    {
        public string Status { get; set; } = string.Empty;

        public List<string> Lines { get; } = new List<string>();

        public bool Found { get; set; }
    }

    /// <summary>
    /// «Найти причину зависания» (D-024): делением пополам ищет элемент основной, без которого MEPSystem.Add терминала копии
    /// проходит. Каждый шаг — проба с откатом: удалить систему-копию и половину подозреваемых, Regenerate, добавить терминал.
    /// Revit ответил быстро — виновник среди удалённых, следующий шаг сразу. Завис — пользователь снимает Revit и нажимает
    /// снова: состояние в файле, незавершённый шаг считается зависанием. Модель не меняется.
    /// Первый шаг — контроль без удаления (должен зависнуть). Путь по сети от терминалов копии до ближайшего терминала
    /// основной не удаляется: иначе добавление проходило бы только потому, что терминал отрезан от основной.
    /// На КрМа3 добавление в «К1» висло и в самом Revit; воронки, стык с «К1.1», объект системы и крестовина
    /// с предупреждением «Пользовательский фитинг создан» проверены вручную и не причина (2026-10-10).
    /// </summary>
    internal static class HangSearch
    {
        /// <summary>Дольше — не вылечено (обычное добавление — доли секунды, насос VANDJORD — около минуты).</summary>
        private static readonly TimeSpan CuredWithin = TimeSpan.FromSeconds(30);

        private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VKTools");

        public static HangSearchResult Run(Document doc, PipingModel model, PlannedGroup group, PlannedCandidate copy, IOperationProgress progress, ILog log)
        {
            var result = new HangSearchResult();
            PipingSystem? target = group.Target;
            if (target == null || !target.IsValidObject)
            {
                result.Status = $"«{group.BaseName}»: основной системы нет в модели.";
                return result;
            }
            string key = $"{doc.Title}|{group.TargetId.ToString(CultureInfo.InvariantCulture)}|{copy.Record.Id.ToString(CultureInfo.InvariantCulture)}";
            string path = Path.Combine(Folder, $"hang-search-{group.TargetId.ToString(CultureInfo.InvariantCulture)}-{Math.Abs(StableHash(doc.Title)).ToString(CultureInfo.InvariantCulture)}.txt");

            SystemMembers members = model.MembersOf(group.TargetId);
            HashSet<long> keep = LinkPath(doc, members, copy, out List<long> anchors);
            bool connected = keep.Count > 0;
            if (!connected)
            {
                long anchor = Anchor(doc, members);
                if (anchor > 0) anchors.Add(anchor);
            }
            var untested = new HashSet<long>(copy.ElementIds);
            untested.UnionWith(copy.Moves.Select(m => m.OwnerId));
            untested.UnionWith(keep);
            untested.UnionWith(anchors);

            HangBisect? bisect = Load(path);
            if (bisect != null && (bisect.Key != key || bisect.Finished)) bisect = null;
            if (bisect != null && bisect.Pending != null)
            {
                bisect.Hung();
                log.Info($"  поиск причины «{group.BaseName}» ← «{copy.Record.Name}»: прошлый шаг не вернулся (Revit сняли) — {bisect.History.Last()}");
                Save(path, bisect);
            }
            if (bisect == null)
            {
                List<long> ordered = Ordered(doc, members.All.Where(id => !untested.Contains(id)));
                bisect = HangBisect.Start(key, ordered);
                log.Info($"  поиск причины «{group.BaseName}» (Id {group.TargetId}) ← «{copy.Record.Name}»: подозреваемых {ordered.Count}, шагов примерно {bisect.StepsLeft}");
                log.Info(connected
                    ? $"    не удаляются: путь от копии до основной {keep.Count} эл. до {string.Join(", ", anchors.Select(a => Describe(doc, a)))}"
                    : $"    копия с основной физически не соединена; не удаляется опорный терминал {(anchors.Count > 0 ? Describe(doc, anchors[0]) : "— нет")}");
                Save(path, bisect);
            }
            else
            {
                log.Info($"  поиск причины «{group.BaseName}» ← «{copy.Record.Name}»: продолжаю, шаг {bisect.Step + 1}, подозреваемых {bisect.Candidates.Count}");
            }
            bisect.Forget(new HashSet<long>(bisect.Candidates.Where(id => doc.GetElement(id.ToElementId()) == null)));

            try
            {
                while (!bisect.Finished)
                {
                    progress.ThrowIfCancelled();
                    List<long> test = bisect.NextTest();
                    string action = bisect.Phase == HangBisectPhase.Control
                        ? $"Контроль: ничего не удаляю, добавляю «{copy.Record.Name}» в «{group.BaseName}» — Revit должен зависнуть."
                        : $"Удаляю {test.Count} из {bisect.Candidates.Count} подозреваемых и добавляю «{copy.Record.Name}» в «{group.BaseName}».";
                    progress.Report($"Поиск причины: шаг {bisect.Step + 1}, осталось примерно {bisect.StepsLeft}. {action} "
                        + "Если Revit не ответит 5 минут — снимите его, откройте модель без сохранения и снова нажмите «Найти причину зависания»: поиск продолжится.");
                    bisect.Begin(test);
                    Save(path, bisect);
                    TimeSpan? elapsed = Step(doc, group, copy, test, keep, bisect.Step + 1, log);
                    if (elapsed == null)
                    {
                        bisect.Drop();
                        Save(path, bisect);
                        result.Status = $"«{group.BaseName}»: шаг {bisect.Step + 1} не дал ответа (см. лог). Поиск остановлен, модель не изменена.";
                        result.Lines.AddRange(bisect.History);
                        return result;
                    }
                    bisect.Returned(elapsed.Value.TotalSeconds, elapsed.Value < CuredWithin);
                    Save(path, bisect);
                    log.Info("    " + bisect.History.Last());
                }
            }
            catch (OperationCanceledException)
            {
                result.Status = $"«{group.BaseName}»: поиск остановлен на шаге {bisect.Step + 1}, подозреваемых {bisect.Candidates.Count}. Нажмите «Найти причину зависания», чтобы продолжить. Модель не изменена.";
                result.Lines.AddRange(bisect.History);
                return result;
            }

            result.Lines.AddRange(bisect.History);
            string notTested = $"Не проверялись (не удалялись): элементы самой копии и путь от неё до основной — {untested.Count} эл.";
            if (bisect.Phase == HangBisectPhase.Found)
            {
                result.Found = true;
                int cut = CutOff(doc, members, copy, connected ? keep : new HashSet<long>(anchors), bisect.Culprit);
                result.Status = $"«{group.BaseName}»: без {Describe(doc, bisect.Culprit)} добавление «{copy.Record.Name}» проходит. Модель не изменена.";
                result.Lines.Add(cut > 0
                    ? $"Его удаление отрезает от сети ещё {cut} эл. — причина может быть и в отрезанной части, а не в нём самом."
                    : "Его удаление ничего от сети не отрезает: причина в нём самом.");
            }
            else if (bisect.Phase == HangBisectPhase.NotReproduced)
            {
                result.Status = $"«{group.BaseName}»: в пробе плагина добавление «{copy.Record.Name}» прошло без зависания — искать нечего. Модель не изменена.";
            }
            else
            {
                result.Status = $"«{group.BaseName}»: одного элемента, из-за которого Revit зависает, среди проверенных нет — зависание держится на сочетании, на размере сети или на непроверенных элементах. Модель не изменена.";
            }
            result.Lines.Add(notTested);
            log.Info("    " + result.Status);
            foreach (string line in result.Lines.Skip(bisect.History.Count)) log.Info("    " + line);
            return result;
        }

        /// <summary>Один шаг-проба; null — Revit отказал (не завис), ответа шаг не дал.</summary>
        private static TimeSpan? Step(Document doc, PlannedGroup group, PlannedCandidate copy, List<long> test, HashSet<long> keep, int step, ILog log)
        {
            var failures = new FailureCollector(SystemMergeExecutor.RegisteredUpdaters(doc), SystemMergeExecutor.DisableOnFailureApplications);
            using (var operation = new TransactionGroup(doc, "VK Tools: поиск причины зависания (проба)"))
            {
                operation.Start();
                Transaction? transaction = null;
                try
                {
                    transaction = FailureCollector.Start(doc, $"Поиск причины, шаг {step.ToString(CultureInfo.InvariantCulture)}", failures);
                    doc.Delete(copy.Record.Id.ToElementId());
                    int skipped = DeleteAll(doc, test);
                    doc.Regenerate();
                    if (!(doc.GetElement(group.TargetId.ToElementId()) is PipingSystem target) || !target.IsValidObject)
                    {
                        log.Warn($"    шаг {step}: после удаления {test.Count} эл. основной системы больше нет — шаг не годится");
                        return null;
                    }
                    int lost = keep.Count(id => doc.GetElement(id.ToElementId()) == null);
                    if (lost > 0)
                    {
                        log.Warn($"    шаг {step}: вместе с удалёнными пропали {lost} эл. пути от копии до основной — шаг не годится");
                        return null;
                    }
                    if (skipped > 0) log.Info($"    шаг {step}: не удалились {skipped} эл. (остались в модели)");
                    long targetId = group.TargetId;
                    var watch = Stopwatch.StartNew();
                    foreach ((long ownerId, int connectorId) in copy.Moves)
                    {
                        Element? owner = doc.GetElement(ownerId.ToElementId());
                        Connector? connector = owner == null ? null : Connectors.Find(owner, connectorId);
                        if (connector == null || Connectors.SystemId(connector) == targetId) continue;
                        HangMarker.Begin(doc.Title, ownerId, $"Поиск причины, шаг {step}: MEPSystem.Add {Describe(doc, ownerId)} → «{group.BaseName}»");
                        try
                        {
                            var set = new ConnectorSet();
                            set.Insert(connector);
                            target.Add(set);
                        }
                        finally
                        {
                            HangMarker.End();
                        }
                    }
                    return watch.Elapsed;
                }
                catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException || ex is InvalidOperationException || ex is ArgumentException)
                {
                    log.Warn($"    шаг {step}: Revit отказал: {ex.Message.Replace(Environment.NewLine, " ")}");
                    return null;
                }
                finally
                {
                    if (transaction != null)
                    {
                        if (transaction.HasStarted() && !transaction.HasEnded()) transaction.RollBack();
                        transaction.Dispose();
                    }
                    operation.RollBack();
                }
            }
        }

        /// <summary>Удалить разом; если Revit отказал — по одному, пропуская неудаляемые. Возвращает, сколько не удалось.</summary>
        private static int DeleteAll(Document doc, List<long> ids)
        {
            List<ElementId> existing = ids.Select(id => id.ToElementId()).Where(id => doc.GetElement(id) != null).ToList();
            if (existing.Count == 0) return 0;
            try
            {
                doc.Delete(existing);
                return 0;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                int failed = 0;
                foreach (ElementId id in existing)
                {
                    if (doc.GetElement(id) == null) continue;
                    try
                    {
                        doc.Delete(id);
                    }
                    catch (Autodesk.Revit.Exceptions.ApplicationException)
                    {
                        failed++;
                    }
                }
                return failed;
            }
        }

        /// <summary>
        /// Путь по сети от каждого терминала копии до ближайшего терминала основной (только по элементам основной и копии).
        /// Пусто — копия с основной физически не соединена.
        /// </summary>
        private static HashSet<long> LinkPath(Document doc, SystemMembers members, PlannedCandidate copy, out List<long> anchors)
        {
            var owners = new HashSet<long>(copy.Moves.Select(m => m.OwnerId));
            var allowed = new HashSet<long>(members.All);
            allowed.UnionWith(copy.ElementIds);
            allowed.UnionWith(owners);
            var targets = new HashSet<long>(members.Terminals.Where(t => !owners.Contains(t) && !copy.ElementIds.Contains(t)));
            var keep = new HashSet<long>();
            anchors = new List<long>();
            foreach (long start in owners.OrderBy(x => x))
            {
                var from = new Dictionary<long, long> { [start] = 0 };
                var queue = new Queue<long>();
                queue.Enqueue(start);
                long found = 0;
                while (queue.Count > 0 && found == 0)
                {
                    Element? e = doc.GetElement(queue.Dequeue().ToElementId());
                    if (e == null) continue;
                    long id = e.Id.ToLong();
                    foreach (long n in Connectors.NeighbourIds(e).OrderBy(x => x))
                    {
                        if (!allowed.Contains(n) || from.ContainsKey(n)) continue;
                        from[n] = id;
                        if (targets.Contains(n))
                        {
                            found = n;
                            break;
                        }
                        queue.Enqueue(n);
                    }
                }
                if (found == 0) continue;
                if (!anchors.Contains(found)) anchors.Add(found);
                for (long id = found; id != 0; id = from[id]) keep.Add(id);
            }
            return keep;
        }

        /// <summary>Сколько элементов основной перестают быть связаны с путём от копии, если удалить <paramref name="culprit"/>.</summary>
        private static int CutOff(Document doc, SystemMembers members, PlannedCandidate copy, HashSet<long> starts, long culprit)
        {
            var allowed = new HashSet<long>(members.All);
            allowed.UnionWith(copy.ElementIds);
            HashSet<long> before = Reach(doc, allowed, starts, 0);
            HashSet<long> after = Reach(doc, allowed, starts, culprit);
            return before.Count(id => id != culprit && !after.Contains(id));
        }

        private static HashSet<long> Reach(Document doc, HashSet<long> allowed, HashSet<long> starts, long blocked)
        {
            var seen = new HashSet<long>(starts.Where(id => id != blocked));
            var queue = new Queue<long>(seen);
            while (queue.Count > 0)
            {
                Element? e = doc.GetElement(queue.Dequeue().ToElementId());
                if (e == null) continue;
                foreach (long n in Connectors.NeighbourIds(e))
                {
                    if (n != blocked && allowed.Contains(n) && seen.Add(n)) queue.Enqueue(n);
                }
            }
            return seen;
        }

        /// <summary>
        /// Копия с основной не соединена: терминал, который не удаляем, чтобы основная система не исчезла вместе со всеми терминалами: оборудование
        /// (на КрМа3 — капельная воронка, удаление всех воронок зависание не сняло), иначе первый по Id.
        /// </summary>
        private static long Anchor(Document doc, SystemMembers members)
        {
            foreach (long id in members.Terminals.OrderBy(x => x))
            {
                if (doc.GetElement(id.ToElementId())?.Category?.Id.ToLong() == (long)BuiltInCategory.OST_MechanicalEquipment) return id;
            }
            return members.Terminals.Count > 0 ? members.Terminals.Min() : -1;
        }

        /// <summary>Порядок обхода по соединениям: половина списка — связный кусок сети, а не россыпь.</summary>
        private static List<long> Ordered(Document doc, IEnumerable<long> ids)
        {
            var set = new HashSet<long>(ids);
            var order = new List<long>();
            var seen = new HashSet<long>();
            foreach (long start in set.OrderBy(x => x))
            {
                if (!seen.Add(start)) continue;
                var queue = new Queue<long>();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    long id = queue.Dequeue();
                    order.Add(id);
                    Element? e = doc.GetElement(id.ToElementId());
                    if (e == null) continue;
                    foreach (long n in Connectors.NeighbourIds(e).OrderBy(x => x))
                    {
                        if (set.Contains(n) && seen.Add(n)) queue.Enqueue(n);
                    }
                }
            }
            return order;
        }

        private static HangBisect? Load(string path)
        {
            try
            {
                return File.Exists(path) ? HangBisect.Load(File.ReadAllText(path, Encoding.UTF8)) : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Записать состояние; без него зависший шаг после перезапуска не распознать, поэтому ошибка записи — стоп.</summary>
        private static void Save(string path, HangBisect bisect)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(path, bisect.Save(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InvalidOperationException("не удалось записать состояние поиска: " + ex.Message, ex);
            }
        }

        private static int StableHash(string text)
        {
            unchecked
            {
                int hash = 17;
                foreach (char c in text) hash = hash * 31 + c;
                return hash == int.MinValue ? 0 : hash;
            }
        }

        private static string Describe(Document doc, long id)
        {
            Element? e = doc.GetElement(id.ToElementId());
            if (e == null) return $"элемента {id} (нет в модели)";
            string name = e is FamilyInstance fi && fi.Symbol != null ? $"{fi.Symbol.FamilyName}: {fi.Symbol.Name}" : e.Name;
            return $"{e.Category?.Name ?? "?"} {id} «{name}»";
        }
    }
}
