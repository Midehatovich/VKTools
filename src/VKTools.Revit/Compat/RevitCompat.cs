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
    }
}
