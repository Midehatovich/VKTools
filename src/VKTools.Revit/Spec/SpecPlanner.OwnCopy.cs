using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using VKTools.Core.Checks;
using VKTools.Core.Config;
using VKTools.Core.Progress;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Spec
{
    /// <summary>
    /// Свой набор «Р_» (0.14.0, объект «Пушкарев»): «Система и этаж» заодно копирует ADSK_* → Р_* (позиция, наименование, марка, код,
    /// завод, единица, количество, масса, примечание) у всех элементов ВК, включая вложенные. Источник — экземпляр, пустой — тип.
    /// ADSK_* не меняются. Не копируется то, что считают другие шаги цепочки, иначе значения скакали бы туда-обратно:
    /// наименование труб («Имена труб» с записью) и изоляции («Изоляция» с наименованием), марка/код/завод изоляции («Изоляция»),
    /// количество жёстких труб и изоляции на трубах («Запас труб»). Служебные позиции _SYS_ заполняет их модуль.
    /// </summary>
    internal sealed partial class SpecPlanner
    {
        private static readonly BuiltInCategory[] CopyCategories =
        {
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory,
            BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Sprinklers,
            BuiltInCategory.OST_PipeInsulations, BuiltInCategory.OST_GenericModel,
        };

        private void PlanOwnCopy(SpecDefaults defaults)
        {
            const SpecModule M = SpecModule.SystemFloor;
            if (!ParamProfile.IsOwn) return;

            SpecProjectSettings stored = SpecSettingsStore.Read(_doc, out _);
            var ignored = new List<ConfigIssue>();
            InsulationSettings ins = SpecOptions.Load(SpecModule.Insulation, stored, defaults, ignored, out _).Insulation;
            PipeNameSettings pn = SpecOptions.Load(SpecModule.PipeNames, stored, defaults, ignored, out _).PipeNames;

            long pipeCat = CategoryId(BuiltInCategory.OST_PipeCurves);
            long insCat = CategoryId(BuiltInCategory.OST_PipeInsulations);
            var skippedByRule = new SortedDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            void Skipped(string why) => skippedByRule[why] = skippedByRule.TryGetValue(why, out int n) ? n + 1 : 1;

            _progress.Report("Система и этаж: копия ADSK_ → Р_…");
            var elements = new List<Element>();
            foreach (BuiltInCategory bic in CopyCategories)
            {
                foreach (Element e in new FilteredElementCollector(_doc).OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (IsServiceItem(e)) continue;
                    elements.Add(e);
                }
            }

            var missing = new SortedDictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
            int copied = 0;
            for (int i = 0; i < elements.Count; i++)
            {
                if (i % 500 == 0)
                {
                    _progress.Report($"Система и этаж: копия ADSK_ → Р_, элемент {i + 1} из {elements.Count}", i, elements.Count);
                    _progress.ThrowIfCancelled();
                }
                Element e = elements[i];
                long cat = e.Category?.Id.ToLong() ?? -1;
                bool isPipe = cat == pipeCat;
                bool isIns = cat == insCat;
                bool insOnPipe = isIns && e is InsulationLiningBase lining && _doc.GetElement(lining.HostElementId) is Pipe;
                Element? type = TypeOf(e);
                string subject = FamilyLabel(e);

                foreach (ParamRole role in ParamProfile.CopiedRoles)
                {
                    if (role == ParamRole.Name && isPipe && pn.WriteName) { Skipped("наименование труб — «Имена труб»"); continue; }
                    if (role == ParamRole.Name && isIns && ins.WriteName) { Skipped("наименование изоляции — «Изоляция»"); continue; }
                    if ((role == ParamRole.Mark || role == ParamRole.Code || role == ParamRole.Maker) && isIns && ins.WriteProduct)
                    {
                        Skipped("марка, код, завод изоляции — «Изоляция»");
                        continue;
                    }
                    if (role == ParamRole.Quantity && (isPipe || insOnPipe)) { Skipped("количество труб и изоляции на трубах — «Запас труб»"); continue; }

                    OwnParamDef own = ParamProfile.Own(role);
                    ParamKey targetKey = own.Key;
                    Parameter? target = _access.Find(e, targetKey);
                    if (target == null)
                    {
                        string category = e.Category?.Name ?? "?";
                        missing[$"{own.Name} · {category}"] = missing.TryGetValue($"{own.Name} · {category}", out int m) ? m + 1 : 1;
                        continue;
                    }
                    ParameterRef adsk = ParamProfile.Adsk(role);
                    ParamKey sourceKey = ParamKey.Shared(adsk.Name, adsk.Guid);
                    Parameter? source = _access.Find(e, sourceKey);
                    string from = "экземпляр";
                    if (source == null || !source.HasValue || IsBlank(source))
                    {
                        Parameter? typeSource = type != null ? _access.Find(type, sourceKey) : null;
                        if (typeSource != null && typeSource.HasValue && !IsBlank(typeSource))
                        {
                            source = typeSource;
                            from = "тип";
                        }
                    }
                    ParamValue? value = Convert(source, target, role, out string? problem);
                    if (problem != null)
                    {
                        _plan.AddIssue(M, Severity.Warning, own.Name, $"{adsk.Name}: {problem} — не копируем", subject, e.Id.ToLong());
                        continue;
                    }
                    if (value == null || (value.Kind == ParamValueKind.Text && value.Text.Trim().Length == 0))
                    {
                        // 0.15.0: пустое ADSK_ не стирает заполненное Р_ — ручная правка и «Марки и поставщики» остаются (Пушкарев 2026-11-23)
                        if (target.StorageType == StorageType.String && !IsBlank(target)) Skipped("пустое ADSK_ не стирает заполненное Р_");
                        continue;
                    }
                    copied++;
                    ParamValue? current = ParamAccess.Read(target);
                    if (current != null && current.SameAs(value))
                    {
                        _plan.CountUnchanged(M);
                        continue;
                    }
                    if (target.IsReadOnly)
                    {
                        _plan.AddIssue(M, Severity.Error, own.Name, "только для чтения", subject, e.Id.ToLong());
                        continue;
                    }
                    if (Guard(M, e, target, own.Name, subject)) continue;
                    AddChange(M, e, targetKey, current?.Display ?? string.Empty, value, $"копия {adsk.Name} ({from})", null);
                }
            }

            _plan.Notes.Add($"Свои параметры «Р_»: копия ADSK_ → Р_ у {elements.Count} элементов ВК (с вложенными), значений {copied}; ADSK_ не меняются.");
            if (skippedByRule.Count > 0)
            {
                _plan.Notes.Add("Свои параметры «Р_»: не копируются (заполняют другие шаги) — " + string.Join("; ", skippedByRule.Select(x => $"{x.Key}: {x.Value}")));
            }
            foreach (KeyValuePair<string, int> m in missing)
            {
                _plan.AddIssue(M, Severity.Warning, m.Key.Split('·')[0].Trim(), $"нет параметра у категории «{m.Key.Split('·')[1].Trim()}»", string.Empty, null, m.Value)
                    .Action = "Нажмите «Применить»: плагин сам добавит свои параметры в проект (или Управление → Параметры проекта).";
            }
        }

        private static bool IsBlank(Parameter p) => p.StorageType == StorageType.String && string.IsNullOrWhiteSpace(p.AsString());

        /// <summary>Значение источника в тип цели. null без проблемы — источника нет (текст станет пустым, число не трогаем).</summary>
        private static ParamValue? Convert(Parameter? source, Parameter target, ParamRole role, out string? problem)
        {
            problem = null;
            if (source == null || !source.HasValue) return null;
            if (target.StorageType == StorageType.String)
            {
                switch (source.StorageType)
                {
                    case StorageType.String: return ParamValue.OfText(source.AsString()?.Trim());
                    case StorageType.Integer: return ParamValue.OfText(source.AsInteger().ToString(CultureInfo.InvariantCulture));
                    case StorageType.Double: return ParamValue.OfText(source.AsValueString() ?? string.Empty);
                    default: problem = "ссылка на элемент"; return null;
                }
            }
            if (target.StorageType == StorageType.Double)
            {
                switch (source.StorageType)
                {
                    case StorageType.Integer:
                        return ParamValue.OfDouble(source.AsInteger(), null);
                    case StorageType.Double:
                        // ×3,28 (2026-10-20): длину в «число» не копируем. Масса во внутренних единицах — кг, как в спецификации.
                        if (!IsPlainNumber(source) && !(role == ParamRole.Mass && IsMass(source)))
                        {
                            problem = "у источника единицы (не «Число») — значение не перенести без пересчёта";
                            return null;
                        }
                        return ParamValue.OfDouble(source.AsDouble(), source.AsValueString());
                    case StorageType.String:
                        string text = (source.AsString() ?? string.Empty).Trim().Replace(',', '.');
                        if (text.Length == 0) return null;
                        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return ParamValue.OfDouble(v, null);
                        problem = $"текст «{source.AsString()}» не число";
                        return null;
                    default:
                        problem = "ссылка на элемент";
                        return null;
                }
            }
            problem = "параметр Р_ не текст и не число";
            return null;
        }

        private static bool IsMass(Parameter p)
        {
            try
            {
#if REVIT2022_OR_GREATER
                string id = p.Definition.GetDataType().TypeId ?? string.Empty;
#else
                string id = p.Definition.GetSpecTypeId().TypeId ?? string.Empty;
#endif
                return id.IndexOf(":mass", StringComparison.Ordinal) >= 0;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }
    }
}
