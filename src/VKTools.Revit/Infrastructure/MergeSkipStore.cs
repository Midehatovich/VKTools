using System;
using System.IO;
using System.Text;
using VKTools.Core.Mep;

namespace VKTools.Revit.Infrastructure
{
    /// <summary>
    /// Отметки «Не объединять» (D-025) — у пользователя на компьютере, рядом со списком зависаний.
    /// Модель и ФОП не меняются; коллеги этих отметок не видят.
    /// </summary>
    internal static class MergeSkipStore
    {
        private static readonly string Folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VKTools");

        private static string FilePath => Path.Combine(Folder, "merge-skip.txt");

        public static MergeSkipList Load()
        {
            try
            {
                return File.Exists(FilePath) ? MergeSkipList.Parse(File.ReadAllText(FilePath, Encoding.UTF8)) : new MergeSkipList();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new MergeSkipList();
            }
        }

        /// <summary>Записать; false — не удалось (отметка действует до закрытия окна).</summary>
        public static bool Save(MergeSkipList list)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, list.Format(), Encoding.UTF8);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
