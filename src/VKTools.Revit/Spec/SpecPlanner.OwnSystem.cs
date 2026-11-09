using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using VKTools.Core.Checks;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Spec
{
    /// <summary>Сокращение системы вложенного по правилу «Своя система»; <see cref="Problem"/> — почему взять нельзя.</summary>
    internal sealed class OwnSystemCode
    {
        public OwnSystemCode(string? code, string? problem)
        {
            Code = code;
            Problem = problem;
        }

        public string? Code { get; }

        public string? Problem { get; }
    }

    /// <summary>
    /// «Своя система у вложенных» (0.12.2): смеситель внутри умывальника берёт LSR_Имя системы из своего «СокращениеСистемы»
    /// (В1, В1.1), а LSR_Тип системы — как у труб этой системы. Остальные параметры по-прежнему от родителя.
    /// </summary>
    internal sealed partial class SpecPlanner
    {
        private readonly Dictionary<(string Code, string Key), ParamValue?> _ownDonors = new Dictionary<(string, string), ParamValue?>();
        private List<Element>? _pipes;
        private int _ownMatched;

        private OwnSystemCode ResolveOwnCode(FamilyInstance child, OwnSystemRule rule)
        {
            _ownMatched++;
            Parameter? p = child.LookupParameter(rule.Source);
            if (p == null && TypeOf(child) is Element type) p = type.LookupParameter(rule.Source);
            if (p == null) return new OwnSystemCode(null, $"нет параметра «{rule.Source}» — систему взять неоткуда, вложенное не трогаем");
            string? raw = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            List<string> codes = SystemCodes.Split(raw);
            if (codes.Count == 0) return new OwnSystemCode(null, $"«{rule.Source}» пусто — вложенное не трогаем");
            if (codes.Count > 1) return new OwnSystemCode(null, $"в «{rule.Source}» несколько систем ({SystemCodes.Join(codes)}) — вложенное не трогаем");
            return new OwnSystemCode(codes[0], null);
        }

        private void EvaluateOwnSystem(OwnSystemRule rule, OwnSystemCode own, int index, ParamKey key, Parameter target, FamilyInstance child,
            bool isSelected, ParamCandidate candidate, string subject, long childId)
        {
            const SpecModule M = SpecModule.Nested;
            if (own.Code == null)
            {
                candidate.Problems++;
                if (isSelected)
                {
                    _plan.AddIssue(M, Severity.Warning, key.Name, "своя система: " + own.Problem, subject, childId)
                        .Action = $"Заполните «{rule.Source}» во вложенном (одна система, например В1) или уберите семейство из правил «Своя система».";
                }
                return;
            }

            ParamValue? value;
            string source;
            if (index == 0)
            {
                if (target.StorageType != StorageType.String)
                {
                    candidate.Problems++;
                    if (isSelected)
                    {
                        _plan.AddIssue(M, Severity.Error, key.Name, "своя система: первый параметр системы должен быть текстовым", subject, childId)
                            .Action = "Поставьте первым в правиле текстовый параметр имени системы (LSR_Имя системы).";
                    }
                    return;
                }
                value = ParamValue.OfText(own.Code);
                source = $"своя система «{own.Code}» ({rule.Source})";
            }
            else
            {
                value = DonorValue(own.Code, key);
                if (value == null || target.StorageType != DonorStorage(value))
                {
                    candidate.Problems++;
                    if (isSelected)
                    {
                        _plan.AddIssue(M, Severity.Warning, key.Name,
                            value == null ? $"своя система «{own.Code}»: у труб этой системы параметр пуст или таких труб нет — вложенное не трогаем"
                                : $"своя система «{own.Code}»: тип данных у трубы и у вложенного разный", subject, childId)
                            .Action = $"Заполните «{key.Name}» у труб системы {own.Code} (например, «Система и этаж») и обновите.";
                    }
                    return;
                }
                source = $"трубы системы «{own.Code}»";
            }

            ParamValue? current = ParamAccess.Read(target);
            if (current != null && current.SameAs(value))
            {
                if (isSelected) _plan.CountUnchanged(M);
                return;
            }
            if (target.IsReadOnly)
            {
                candidate.Problems++;
                if (isSelected)
                {
                    _plan.AddIssue(M, Severity.Error, key.Name,
                        "своя система: у вложенного параметр только для чтения (связан в семействе с параметром родителя или задан формулой)", subject, childId)
                        .Action = "Правьте семейство: отвяжите параметр вложенного от родителя.";
                }
                return;
            }
            if (_guard.Check(child, target) != null)
            {
                candidate.Problems++;
                if (isSelected) Guard(M, child, target, key.Name, subject);
                return;
            }
            candidate.WouldChange++;
            if (!isSelected) return;
            AddChange(M, child, key, current?.Display ?? string.Empty, value, source, null);
        }

        private static StorageType DonorStorage(ParamValue value) => value.Kind switch
        {
            ParamValueKind.Integer => StorageType.Integer,
            ParamValueKind.Double => StorageType.Double,
            _ => StorageType.String,
        };

        /// <summary>Самое частое непустое значение параметра у труб системы (с учётом того, что запишет «Система и этаж» в этой же операции).</summary>
        private ParamValue? DonorValue(string code, ParamKey key)
        {
            if (_ownDonors.TryGetValue((code, key.Id), out ParamValue? cached)) return cached;
            _pipes ??= new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_PipeCurves).WhereElementIsNotElementType().ToElements().ToList();
            var counts = new Dictionary<string, (ParamValue Value, int Count)>(StringComparer.Ordinal);
            foreach (Element pipe in _pipes)
            {
                List<string> codes = OwnCodes(pipe);
                if (codes.Count != 1 || !string.Equals(codes[0], code, StringComparison.Ordinal)) continue;
                ParamValue? value = _pending.TryGetValue((pipe.Id.ToLong(), key.Id), out ParamValue? planned) ? planned : null;
                if (value == null)
                {
                    Parameter? p = _access.Find(pipe, key);
                    value = p == null ? null : ParamAccess.Read(p);
                }
                if (ParamAccess.IsEmpty(value)) continue;
                ParamValue found = value!;
                string display = found.Display;
                counts[display] = counts.TryGetValue(display, out (ParamValue Value, int Count) c) ? (c.Value, c.Count + 1) : (found, 1);
            }
            ParamValue? best = counts.Count == 0 ? null : counts.Values.OrderByDescending(c => c.Count).First().Value;
            _ownDonors[(code, key.Id)] = best;
            return best;
        }
    }
}
