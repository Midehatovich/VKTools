using System;
using System.Collections.Generic;
using System.Linq;
using VKTools.Core.Text;

namespace VKTools.Core.Mep
{
    /// <summary>Система трубопроводов для логики имён: только то, что нужно без модели.</summary>
    public sealed class SystemRecord
    {
        public SystemRecord(long id, string name, long typeId, string typeName, string? abbreviation, string classification)
        {
            Id = id;
            Name = name ?? string.Empty;
            TypeId = typeId;
            TypeName = typeName ?? string.Empty;
            Abbreviation = string.IsNullOrWhiteSpace(abbreviation) ? null : abbreviation!.Trim();
            Classification = classification ?? string.Empty;
        }

        public long Id { get; }

        /// <summary>Имя как есть, без обрезки пробелов: «В1 » и «В1» — разные имена.</summary>
        public string Name { get; }

        public long TypeId { get; }

        public string TypeName { get; }

        /// <summary>Сокращение типа системы («В1»).</summary>
        public string? Abbreviation { get; }

        /// <summary>Классификация системы (PipeSystemType), строкой.</summary>
        public string Classification { get; }

        public override string ToString() => Name;
    }

    public enum CandidateStatus
    {
        /// <summary>Объединяется, если пройдёт проверки по модели.</summary>
        Merge,

        /// <summary>Имя совпадает с сокращением типа, а основных систем этого типа несколько: не объединяется без подтверждения.</summary>
        NeedsConfirmation,

        /// <summary>Не объединяется; причина в Reason.</summary>
        Rejected,
    }

    public sealed class Candidate
    {
        public Candidate(SystemRecord system, CandidateStatus status, string? reason)
        {
            System = system;
            Status = status;
            Reason = reason;
        }

        public SystemRecord System { get; }

        public CandidateStatus Status { get; }

        public string? Reason { get; }
    }

    /// <summary>Результат разбора имён для одной основной системы.</summary>
    public sealed class TargetClassification
    {
        internal TargetClassification(SystemRecord target)
        {
            Target = target;
        }

        public SystemRecord Target { get; }

        public List<Candidate> Candidates { get; } = new List<Candidate>();

        /// <summary>Другие проектные имена: база → имена систем. Не трогаются.</summary>
        public Dictionary<string, List<string>> Ignored { get; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public IEnumerable<SystemRecord> Merge => Candidates.Where(c => c.Status == CandidateStatus.Merge).Select(c => c.System);

        public IEnumerable<SystemRecord> Pending => Candidates.Where(c => c.Status == CandidateStatus.NeedsConfirmation).Select(c => c.System);

        public IEnumerable<SystemRecord> Rejected => Candidates.Where(c => c.Status == CandidateStatus.Rejected).Select(c => c.System);
    }

    /// <summary>Группа «основная ← копии» для пакетного объединения.</summary>
    public sealed class MergeGroup
    {
        internal MergeGroup(string baseName, SystemRecord? target, string? problem, bool baseMissing = false)
        {
            BaseName = baseName;
            Target = target;
            Problem = problem;
            BaseMissing = baseMissing;
        }

        /// <summary>Проектное имя группы («В1.2»).</summary>
        public string BaseName { get; }

        /// <summary>Основная система; null — её нет или их несколько (см. Problem).</summary>
        public SystemRecord? Target { get; }

        /// <summary>Почему группу нельзя объединять целиком (нет основной, основная неоднозначна).</summary>
        public string? Problem { get; }

        /// <summary>Системы с проектным именем нет вовсе: Revit назвал все системы сам («В1 6»).</summary>
        public bool BaseMissing { get; }

        public List<Candidate> Candidates { get; } = new List<Candidate>();
    }

    /// <summary>
    /// Логика PipingSystemMerge v1.2 без Revit API: какие системы — копии какой основной.
    /// Имя — только первичный фильтр; тип системы и классификация обязаны совпасть.
    /// Проверки по модели (терминалы, связи, совместная работа) — в надстройке.
    /// </summary>
    public static class MergeGrouping
    {
        /// <summary>
        /// Выбрана система «В1.2 4» → основная «В1.2», если она одна и того же типа и классификации.
        /// Выбрана основная → она сама. Ошибка — null и текст в error.
        /// </summary>
        public static SystemRecord? ResolveTarget(SystemRecord selected, IReadOnlyCollection<SystemRecord> systems,
            out string? note, out string? error)
        {
            note = null;
            error = null;
            SystemNameParts parts = SystemNames.Split(selected.Name);
            if (parts.Issue != null)
            {
                note = $"имя выбранной системы «{selected.Name}» нестандартное ({parts.Issue}), считаю его проектным целиком";
                return selected;
            }
            if (!parts.IsCopy) return selected;

            List<SystemRecord> bases = systems.Where(r => r.Name == parts.BaseName).ToList();
            if (bases.Count == 0)
            {
                error = $"выбрана система «{selected.Name}», но системы «{parts.BaseName}» в проекте нет. "
                        + $"Переименуйте нужную копию в «{parts.BaseName}» вручную.";
                return null;
            }
            if (bases.Count > 1)
            {
                error = $"систем с именем «{parts.BaseName}» несколько ({bases.Count}): основная неоднозначна";
                return null;
            }
            SystemRecord b = bases[0];
            if (b.TypeId != selected.TypeId || b.Classification != selected.Classification)
            {
                error = $"выбрана «{selected.Name}» (тип «{selected.TypeName}»), а система «{b.Name}» другого типа или классификации "
                        + $"(«{b.TypeName}»): это не её копия";
                return null;
            }
            note = $"выбрана система «{selected.Name}» → основная «{b.Name}»";
            return b;
        }

        /// <summary>Разбор имён для одной основной системы (classify_names из v1.2).</summary>
        public static TargetClassification ClassifyForTarget(SystemRecord target, IReadOnlyCollection<SystemRecord> systems,
            bool trustTypeNamed = false, ICollection<string>? onlyNames = null)
        {
            string t = target.Name;
            var result = new TargetClassification(target);

            // Другие основные (без номера) системы того же типа: В1.1 и В1.2 при основной В1
            List<string> otherBases = systems
                .Where(r => r.Id != target.Id && r.TypeId == target.TypeId && r.Name != t && SystemNames.Split(r.Name).IsProjectName)
                .Select(r => r.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            bool typeNamed = t == target.TypeName || (target.Abbreviation != null && t == target.Abbreviation);

            foreach (SystemRecord r in systems.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (r.Id == target.Id) continue;
                string name = r.Name;
                SystemNameParts parts = SystemNames.Split(name);

                if (parts.Issue == null && parts.IsCopy && parts.BaseName == t)
                {
                    if (onlyNames != null && !onlyNames.Contains(name))
                    {
                        AddIgnored(result, t, name + " (нет в списке)");
                        continue;
                    }
                    var why = new List<string>();
                    if (r.TypeId != target.TypeId) why.Add($"тип системы «{r.TypeName}» ≠ «{target.TypeName}»");
                    if (r.Classification != target.Classification) why.Add($"классификация {r.Classification} ≠ {target.Classification}");

                    if (why.Count > 0)
                    {
                        result.Candidates.Add(new Candidate(r, CandidateStatus.Rejected, string.Join("; ", why)));
                    }
                    else if (typeNamed && otherBases.Count > 0 && !trustTypeNamed)
                    {
                        result.Candidates.Add(new Candidate(r, CandidateStatus.NeedsConfirmation,
                            $"имя «{t}» совпадает с именем или сокращением типа «{target.TypeName}», а основных систем этого типа несколько "
                            + $"({string.Join(", ", otherBases.Take(8))}): копия могла получить имя от типа и принадлежать любой из них"));
                    }
                    else
                    {
                        result.Candidates.Add(new Candidate(r, CandidateStatus.Merge, null));
                    }
                    continue;
                }

                if (parts.IsProjectName && name == t)
                {
                    result.Candidates.Add(new Candidate(r, CandidateStatus.Rejected, "второй экземпляр с именем основной системы: основная неоднозначна"));
                    continue;
                }

                string rootBase = SystemNames.RootBase(name);
                if (rootBase == t)
                {
                    result.Candidates.Add(new Candidate(r, CandidateStatus.Rejected,
                        $"похоже на копию «{t}», но номер не по правилу Revit: {parts.Issue ?? "двойной номер"}"));
                }
                else if (ValueNormalizer.ReplaceLatinLookalikes(rootBase) == ValueNormalizer.ReplaceLatinLookalikes(t))
                {
                    result.Candidates.Add(new Candidate(r, CandidateStatus.Rejected,
                        $"смешаны латиница и кириллица («{ValueNormalizer.MakeVisible(rootBase)}» и «{t}»)"));
                }
                else if (SystemNames.SameIgnoringCaseAndSpaces(rootBase, t))
                {
                    result.Candidates.Add(new Candidate(r, CandidateStatus.Rejected, $"отличается от «{t}» регистром или пробелами"));
                }
                else
                {
                    AddIgnored(result, parts.Issue == null ? parts.BaseName : rootBase, name);
                }
            }
            return result;
        }

        /// <summary>
        /// Пакетный режим: все группы «основная ← копии» по проекту (REQ-MEP-003).
        /// Каждая система попадает не больше чем в одну группу.
        /// </summary>
        public static IReadOnlyList<MergeGroup> BuildGroups(IReadOnlyCollection<SystemRecord> systems)
        {
            var groups = new List<MergeGroup>();
            var claimed = new HashSet<long>();

            var projectNames = systems
                .Where(r => SystemNames.Split(r.Name).IsProjectName)
                .GroupBy(r => r.Name, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

            foreach (IGrouping<string, SystemRecord> sameName in projectNames)
            {
                List<SystemRecord> bases = sameName.ToList();
                if (bases.Count > 1)
                {
                    List<SystemRecord> copies = systems.Where(r => IsCopyOf(r, sameName.Key)).ToList();
                    if (copies.Count == 0) continue;
                    var ambiguous = new MergeGroup(sameName.Key, null,
                        $"систем с именем «{sameName.Key}» несколько ({bases.Count}): основная неоднозначна, переименуйте лишние");
                    foreach (SystemRecord copy in copies)
                    {
                        if (claimed.Add(copy.Id)) ambiguous.Candidates.Add(new Candidate(copy, CandidateStatus.Rejected, "основная неоднозначна"));
                    }
                    if (ambiguous.Candidates.Count > 0) groups.Add(ambiguous);
                    continue;
                }

                TargetClassification classification = ClassifyForTarget(bases[0], systems);
                var group = new MergeGroup(sameName.Key, bases[0], null);
                foreach (Candidate candidate in classification.Candidates)
                {
                    if (claimed.Add(candidate.System.Id)) group.Candidates.Add(candidate);
                }
                if (group.Candidates.Count > 0) groups.Add(group);
            }

            // Копии без основной: «В1.3 1», а «В1.3» в проекте нет
            var existing = new HashSet<string>(projectNames.Select(g => g.Key), StringComparer.Ordinal);
            var orphans = systems
                .Where(r => !claimed.Contains(r.Id))
                .Select(r => (Record: r, Parts: SystemNames.Split(r.Name)))
                .Where(x => x.Parts.Issue == null && x.Parts.IsCopy && !existing.Contains(x.Parts.BaseName))
                .GroupBy(x => x.Parts.BaseName, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal);
            foreach (var orphanGroup in orphans)
            {
                var group = new MergeGroup(orphanGroup.Key, null,
                    $"системы «{orphanGroup.Key}» в проекте нет. Плагин не назначает основную сам: переименуйте одну из копий в «{orphanGroup.Key}» вручную",
                    baseMissing: true);
                foreach (var x in orphanGroup.OrderBy(x => x.Parts.Number))
                {
                    claimed.Add(x.Record.Id);
                    group.Candidates.Add(new Candidate(x.Record, CandidateStatus.Rejected, "нет основной системы"));
                }
                groups.Add(group);
            }
            return groups;
        }

        private static bool IsCopyOf(SystemRecord r, string baseName)
        {
            SystemNameParts parts = SystemNames.Split(r.Name);
            return parts.Issue == null && parts.IsCopy && parts.BaseName == baseName;
        }

        private static void AddIgnored(TargetClassification result, string key, string name)
        {
            if (!result.Ignored.TryGetValue(key, out List<string>? list))
            {
                list = new List<string>();
                result.Ignored.Add(key, list);
            }
            list.Add(name);
        }
    }
}
