using System;
using System.Collections.Generic;
using System.Linq;

namespace VKTools.Core.Spec
{
    /// <summary>
    /// Занятые коллегами и устаревшие (изменённые в хранилище) элементы за один прогон.
    /// По умолчанию хоть один такой элемент запрещает запись целиком: частичная запись позиций, групп и имён
    /// разводит строки спецификации (Пушкарев, ПТ, 2026-11-24: перенумерация сохранилась не вся).
    /// </summary>
    public sealed class BusySummary
    {
        private readonly Dictionary<string, HashSet<long>> _byOwner = new Dictionary<string, HashSet<long>>(StringComparer.CurrentCultureIgnoreCase);
        private readonly HashSet<long> _outdated = new HashSet<long>();

        public void AddOwned(long elementId, string owner)
        {
            string key = string.IsNullOrWhiteSpace(owner) ? "(имя не прочитано)" : owner.Trim();
            if (!_byOwner.TryGetValue(key, out HashSet<long>? set)) _byOwner[key] = set = new HashSet<long>();
            set.Add(elementId);
        }

        public void AddOutdated(long elementId) => _outdated.Add(elementId);

        public int OwnedCount => _byOwner.Values.Sum(s => s.Count);

        public int OutdatedCount => _outdated.Count;

        public bool IsEmpty => OwnedCount == 0 && OutdatedCount == 0;

        /// <summary>«Kondratyev_DA — 6, Ivanova — 2».</summary>
        public string Owners() => string.Join(", ", _byOwner.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(kv => $"{kv.Key} — {kv.Value.Count}"));

        public IEnumerable<long> ElementIds() => _byOwner.Values.SelectMany(s => s).Concat(_outdated).Distinct();

        /// <summary>Причина запрета записи; null — запрещать нечего (или пользователь разрешил частичную запись).</summary>
        public string? BlockReason(bool partialAllowed)
        {
            if (IsEmpty || partialAllowed) return null;
            var parts = new List<string>();
            if (OwnedCount > 0) parts.Add($"заняты коллегами {OwnedCount} эл. ({Owners()})");
            if (OutdatedCount > 0) parts.Add($"изменены в хранилище {OutdatedCount} эл.");
            return "часть элементов нельзя записать: " + string.Join("; ", parts) + ". Чтобы спецификация не разъехалась, не записывается ничего. "
                + (OwnedCount > 0 ? "Попросите освободить элементы (они синхронизируются с освобождением)" : string.Empty)
                + (OwnedCount > 0 && OutdatedCount > 0 ? ", затем " : string.Empty)
                + (OutdatedCount > 0 ? "«Совместная работа» → «Обновить до последней версии»" : string.Empty)
                + " и запустите снова. Записать остальное без них можно галкой в «Настройках».";
        }

        /// <summary>Напоминание после записи в модели совместной работы.</summary>
        public static string SyncReminder(int changedElements) =>
            $"Изменено элементов: {changedElements} — теперь они заняты вами. Синхронизируйтесь с хранилищем с галкой «Освободить» "
            + "(элементы, рабочие наборы, виды): иначе коллеги не смогут их менять и делить спецификации на фрагменты.";
    }
}
