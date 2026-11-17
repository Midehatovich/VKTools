using Autodesk.Revit.DB;

namespace VKTools.Revit.Compat
{
    /// <summary>
    /// Различия Revit API между версиями собраны здесь, чтобы остальной код их не видел.
    /// ElementId.IntegerValue удалён в 2026, ElementId.Value появился в 2024;
    /// Category.BuiltInCategory появился в 2023.
    /// </summary>
    internal static class RevitCompat
    {
        public static long ToLong(this ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        public static ElementId ToElementId(this long value)
        {
#if REVIT2024_OR_GREATER
            return new ElementId(value);
#else
            return new ElementId(checked((int)value));
#endif
        }

        public static BuiltInCategory? ToBuiltInCategory(this Category? category)
        {
            if (category == null) return null;
#if REVIT2023_OR_GREATER
            BuiltInCategory bic = category.BuiltInCategory;
            return bic == BuiltInCategory.INVALID ? (BuiltInCategory?)null : bic;
#else
            int value = category.Id.IntegerValue;
            return value < 0 ? (BuiltInCategory)value : (BuiltInCategory?)null;
#endif
        }

        /// <summary>Общий параметр в семейство, группа «Данные». С 2022 группа — ForgeTypeId, в 2025 BuiltInParameterGroup удалён.</summary>
        public static FamilyParameter AddDataParameter(this FamilyManager manager, ExternalDefinition definition, bool instance)
        {
#if REVIT2022_OR_GREATER
            return manager.AddParameter(definition, GroupTypeId.Data, instance);
#else
            return manager.AddParameter(definition, BuiltInParameterGroup.PG_DATA, instance);
#endif
        }

        /// <summary>Перепривязать параметр проекта к другому набору категорий, не меняя группу параметра.</summary>
        public static bool ReInsertKeepingGroup(this BindingMap map, Definition definition, ElementBinding binding)
        {
#if REVIT2022_OR_GREATER
            return map.ReInsert(definition, binding, definition.GetGroupTypeId());
#else
            return map.ReInsert(definition, binding, definition.ParameterGroup);
#endif
        }

        /// <summary>Высота строк спецификации на листе (ГОСТ 21.110: не менее 8 мм). Свойство появилось в Revit 2024; раньше — false.</summary>
        public static bool TrySetBodyRowHeight(ViewSchedule schedule, double feet)
        {
#if REVIT2024_OR_GREATER
            schedule.RowHeightOverride = RowHeightOverrideOptions.All;
            schedule.RowHeight = feet;
            return true;
#else
            _ = schedule;
            _ = feet;
            return false;
#endif
        }

        /// <summary>
        /// Округление числа в графе спецификации (шаг <paramref name="accuracy"/>, лишние нули не показываются). Единица — та же,
        /// что в проекте для этого типа данных. false — поле без единиц (текст) или Revit не принимает такой шаг.
        /// </summary>
        public static bool TrySetFieldAccuracy(Document doc, ScheduleField field, double accuracy, out string? reason)
        {
            reason = null;
            ForgeTypeId spec;
            try
            {
                spec = field.GetSpecTypeId();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                reason = "тип данных поля не читается: " + ex.Message;
                return false;
            }
            string specId = spec == null || spec.Empty() ? "(пусто)" : spec.TypeId;
            // Единица — как в проекте для этого типа данных; число без единиц (ADSK_Количество) — «Общий» (General)
            ForgeTypeId? unit = null;
            if (spec != null && !spec.Empty())
            {
                try
                {
                    unit = doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId();
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    unit = null;
                }
            }
            if (unit == null || unit.Empty()) unit = UnitTypeId.General;
            foreach (ForgeTypeId candidate in unit == UnitTypeId.General ? new[] { unit } : new[] { unit, UnitTypeId.General })
            {
                try
                {
                    var options = new FormatOptions(candidate) { UseDefault = false };
                    if (!options.IsValidAccuracy(accuracy))
                    {
                        reason = $"шаг {accuracy} не подходит единице {candidate.TypeId}";
                        continue;
                    }
                    options.Accuracy = accuracy;
                    if (options.CanSuppressTrailingZeros()) options.SuppressTrailingZeros = true;
                    field.SetFormatOptions(options);
                    reason = null;
                    return true;
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    reason = $"тип данных {specId}, единица {candidate.TypeId}: {ex.Message}";
                }
            }
            return false;
        }

        /// <summary>Новый параметр проекта в группе «Данные».</summary>
        public static bool InsertAsData(this BindingMap map, Definition definition, ElementBinding binding)
        {
#if REVIT2022_OR_GREATER
            return map.Insert(definition, binding, GroupTypeId.Data);
#else
            return map.Insert(definition, binding, BuiltInParameterGroup.PG_DATA);
#endif
        }
    }
}
