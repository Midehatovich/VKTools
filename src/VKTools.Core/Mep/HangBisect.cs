using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace VKTools.Core.Mep
{
    public enum HangBisectPhase
    {
        /// <summary>Контроль: ничего не удаляем — Revit должен зависнуть, иначе искать нечего.</summary>
        Control,

        /// <summary>Делим подозреваемых пополам.</summary>
        Searching,

        /// <summary>Остался один: проверяем, что без него Revit отвечает.</summary>
        Verifying,

        /// <summary>Найден элемент, без которого добавление проходит.</summary>
        Found,

        /// <summary>Одного виновника нет: зависание держится не на одном элементе.</summary>
        NoSingleCulprit,

        /// <summary>Контроль прошёл быстро: в этой пробе зависание не воспроизводится, делить нечего.</summary>
        NotReproduced,
    }

    /// <summary>
    /// Поиск элемента, из-за которого Revit зависает на MEPSystem.Add (D-024). Шаг: удалить половину подозреваемых
    /// (в пробе, с откатом) и добавить терминал. Revit ответил — виновник в удалённой половине; завис (его сняли) —
    /// в оставшейся. Последний подозреваемый проверяется отдельно: без него Revit должен ответить. Состояние переживает
    /// перезапуск Revit (<see cref="Save"/> / <see cref="Load"/>): зависший шаг записан как «ждёт ответа».
    /// Первый шаг — контроль без удаления: если Revit ответил и так, каждый следующий шаг выглядел бы «вылеченным»
    /// и поиск указал бы на первый попавшийся элемент.
    /// </summary>
    public sealed class HangBisect
    {
        private const string Header = "VKTools.HangBisect 1";
        private static readonly char[] Comma = { ',' };

        private HangBisect(string key, List<long> candidates)
        {
            Key = key;
            Candidates = candidates;
        }

        /// <summary>Документ, основная, копия — чужое состояние не продолжаем.</summary>
        public string Key { get; }

        public HangBisectPhase Phase { get; private set; } = HangBisectPhase.Control;

        /// <summary>Подозреваемые: виновник среди них.</summary>
        public List<long> Candidates { get; private set; }

        /// <summary>Что удалено в шаге, который не вернулся (Revit сняли), — null, если такого нет.</summary>
        public List<long>? Pending { get; private set; }

        public int Step { get; private set; }

        public List<string> History { get; } = new List<string>();

        public bool Finished => Phase == HangBisectPhase.Found || Phase == HangBisectPhase.NoSingleCulprit || Phase == HangBisectPhase.NotReproduced;

        /// <summary>Найденный элемент (Phase = Found).</summary>
        public long Culprit => Phase == HangBisectPhase.Found ? Candidates[0] : 0;

        public static HangBisect Start(string key, IEnumerable<long> candidates)
        {
            var list = candidates.Distinct().ToList();
            var bisect = new HangBisect(key, list);
            if (list.Count == 0) bisect.Phase = HangBisectPhase.NoSingleCulprit;
            return bisect;
        }

        /// <summary>Сколько ещё шагов примерно (контроль, деления и проверка).</summary>
        public int StepsLeft
        {
            get
            {
                if (Finished) return 0;
                int steps = Phase == HangBisectPhase.Control ? 2 : 1; // контроль, проверка последнего
                for (int n = Candidates.Count; n > 1; n = (n + 1) / 2) steps++;
                return steps;
            }
        }

        /// <summary>Что удалить в следующем шаге: ничего при контроле, первая половина подозреваемых или последний при проверке.</summary>
        public List<long> NextTest()
        {
            if (Finished) throw new InvalidOperationException("поиск закончен");
            if (Phase == HangBisectPhase.Control) return new List<long>();
            if (Phase == HangBisectPhase.Verifying) return new List<long>(Candidates);
            return Candidates.Take((Candidates.Count + 1) / 2).ToList();
        }

        /// <summary>Перед вызовом, который может не вернуться: запомнить, что удалено.</summary>
        public void Begin(List<long> deleted)
        {
            Pending = new List<long>(deleted);
        }

        /// <summary>Revit ответил за <paramref name="seconds"/>; <paramref name="cured"/> — быстро, зависания не было.</summary>
        public void Returned(double seconds, bool cured)
        {
            List<long> deleted = Pending ?? throw new InvalidOperationException("нет начатого шага");
            Pending = null;
            Step++;
            string time = seconds.ToString("0.0", CultureInfo.InvariantCulture);
            if (Phase == HangBisectPhase.Control)
            {
                if (cured)
                {
                    History.Add($"шаг {Step}: контроль, ничего не удалено — Revit ответил за {time} с, зависание не воспроизвелось");
                    Phase = HangBisectPhase.NotReproduced;
                }
                else
                {
                    History.Add($"шаг {Step}: контроль, ничего не удалено — Revit ответил за {time} с, медленно: зависание воспроизводится");
                    Reproduced();
                }
                return;
            }
            if (!cured)
            {
                // Медленно, но вернулся: виновник не среди удалённых — как зависание
                History.Add($"шаг {Step}: без {deleted.Count} эл. Revit ответил за {time} с — медленно, виновник не среди них");
                Exclude(deleted);
                return;
            }
            History.Add($"шаг {Step}: без {deleted.Count} эл. Revit ответил за {time} с — виновник среди них");
            if (Phase == HangBisectPhase.Verifying)
            {
                Phase = HangBisectPhase.Found;
                return;
            }
            Candidates = deleted;
            // Удаление одного этого элемента уже вылечило — это и есть проверка
            if (Candidates.Count == 1) Phase = HangBisectPhase.Found;
        }

        /// <summary>Шаг не дал ответа (Revit отказал, а не завис): забыть его, подозреваемые те же.</summary>
        public void Drop()
        {
            Pending = null;
        }

        /// <summary>Шаг не вернулся: Revit сняли. Виновник — среди оставшихся.</summary>
        public void Hung()
        {
            List<long> deleted = Pending ?? throw new InvalidOperationException("нет начатого шага");
            Pending = null;
            Step++;
            if (Phase == HangBisectPhase.Control)
            {
                History.Add($"шаг {Step}: контроль, ничего не удалено — Revit завис: зависание воспроизводится");
                Reproduced();
                return;
            }
            History.Add($"шаг {Step}: без {deleted.Count} эл. Revit завис — виновник не среди них");
            Exclude(deleted);
        }

        private void Reproduced()
        {
            Phase = Candidates.Count == 1 ? HangBisectPhase.Verifying : HangBisectPhase.Searching;
        }

        /// <summary>Элементы, которых больше нет в модели, — из подозреваемых.</summary>
        public void Forget(ICollection<long> missing)
        {
            if (missing.Count == 0) return;
            Candidates = Candidates.Where(id => !missing.Contains(id)).ToList();
            if (Candidates.Count == 0 && !Finished) Phase = HangBisectPhase.NoSingleCulprit;
        }

        private void Exclude(List<long> deleted)
        {
            if (Phase == HangBisectPhase.Verifying)
            {
                Phase = HangBisectPhase.NoSingleCulprit;
                return;
            }
            var set = new HashSet<long>(deleted);
            Candidates = Candidates.Where(id => !set.Contains(id)).ToList();
            if (Candidates.Count == 0) Phase = HangBisectPhase.NoSingleCulprit;
            else if (Candidates.Count == 1) Phase = HangBisectPhase.Verifying;
        }

        public string Save()
        {
            var text = new StringBuilder();
            text.AppendLine(Header);
            text.AppendLine("key\t" + Key.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '));
            text.AppendLine("phase\t" + Phase.ToString());
            text.AppendLine("step\t" + Step.ToString(CultureInfo.InvariantCulture));
            text.AppendLine("candidates\t" + Join(Candidates));
            if (Pending != null) text.AppendLine("pending\t" + Join(Pending));
            foreach (string line in History) text.AppendLine("history\t" + line.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' '));
            return text.ToString();
        }

        /// <summary>Разбор сохранённого; null — не наш формат или испорчен.</summary>
        public static HangBisect? Load(string text)
        {
            string[] lines = text.Replace("\r", string.Empty).Split('\n');
            if (lines.Length == 0 || lines[0] != Header) return null;
            string? key = null;
            HangBisectPhase phase = HangBisectPhase.Searching;
            int step = 0;
            List<long>? candidates = null, pending = null;
            var history = new List<string>();
            foreach (string line in lines.Skip(1))
            {
                int tab = line.IndexOf('\t');
                if (tab < 0) continue;
                string name = line.Substring(0, tab), value = line.Substring(tab + 1);
                switch (name)
                {
                    case "key": key = value; break;
                    case "phase": if (!Enum.TryParse(value, out phase)) return null; break;
                    case "step": if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out step)) return null; break;
                    case "candidates": candidates = Split(value); if (candidates == null) return null; break;
                    case "pending": pending = Split(value); if (pending == null) return null; break;
                    case "history": history.Add(value); break;
                }
            }
            if (key == null || candidates == null) return null;
            var bisect = new HangBisect(key, candidates) { Phase = phase, Step = step, Pending = pending };
            bisect.History.AddRange(history);
            return bisect;
        }

        private static string Join(IEnumerable<long> ids) => string.Join(",", ids.Select(x => x.ToString(CultureInfo.InvariantCulture)));

        private static List<long>? Split(string value)
        {
            var result = new List<long>();
            foreach (string part in value.Split(Comma, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)) return null;
                result.Add(id);
            }
            return result;
        }
    }
}
