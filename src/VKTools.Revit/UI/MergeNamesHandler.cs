using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Core.Config;
using VKTools.Core.Spec;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;
using VKTools.Revit.Spec;

namespace VKTools.Revit.UI
{
    /// <summary>
    /// «Одно наименование» (0.17.4) из отчёта «Проверить спецификацию»: выбранное инженером наименование пишется в Р_Наименование
    /// элементов с другими вариантами, правило «вариант → основное» сохраняется в модели (шаги плагина применяют его дальше).
    /// ADSK_ не трогается. Одна операция, Ctrl+Z отменяет. Занятые элементы, группы — без записи, остальное пишется.
    /// </summary>
    internal sealed class MergeNamesHandler : IExternalEventHandler
    {
        public const string OperationName = "VK Tools: одно наименование";

        private Document? _document;
        private string _main = string.Empty;
        private List<(string Name, IReadOnlyList<long> Ids)> _variants = new List<(string, IReadOnlyList<long>)>();

        public void Request(Document document, string main, IEnumerable<(string Name, IReadOnlyList<long> Ids)> variants)
        {
            _document = document;
            _main = main;
            _variants = variants.ToList();
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
                    TaskDialog.Show("VK Tools", "Документ сейчас только для чтения: записать наименование нельзя.");
                    return;
                }

                string param = ParamProfile.Target(ParamRole.Name).Name;
                var guard = new EditGuard(doc);
                var skipped = new List<string>();
                int written = 0;
                int same = 0;
                var collector = new FailureCollector();
                TransactionStatus status;
                string? settingsProblem;
                using (Transaction transaction = FailureCollector.Start(doc, OperationName, collector))
                {
                    foreach ((string name, IReadOnlyList<long> ids) in _variants)
                    {
                        foreach (long id in ids)
                        {
                            Element? e = doc.GetElement(id.ToElementId());
                            if (e == null) { skipped.Add($"{id}: элемента уже нет"); continue; }
                            Parameter? p = e.LookupParameter(param);
                            if (p == null || p.StorageType != StorageType.String)
                            {
                                skipped.Add($"{id}: нет параметра экземпляра «{param}» (он у типа или не добавлен)");
                                continue;
                            }
                            if (p.IsReadOnly) { skipped.Add($"{id}: «{param}» только для чтения"); continue; }
                            if (string.Equals(p.AsString() ?? string.Empty, _main, StringComparison.Ordinal)) { same++; continue; }
                            (Core.Checks.Severity Severity, string Reason)? blocked = guard.Check(e, p);
                            if (blocked != null) { skipped.Add($"{id}: {blocked.Value.Reason}"); continue; }
                            p.Set(_main);
                            written++;
                        }
                    }
                    SpecProjectSettings settings = SpecSettingsStore.Read(doc, out _);
                    Dictionary<string, string> aliases = NameAliases.Parse(settings.Get(NameAliases.Key));
                    NameAliases.Add(aliases, _variants.Select(v => v.Name), _main);
                    settings.Set(NameAliases.Key, NameAliases.Format(aliases));
                    settingsProblem = SpecSettingsStore.Write(doc, settings);
                    status = transaction.Commit();
                }
                if (status != TransactionStatus.Committed)
                {
                    string why = collector.Errors.Count > 0 ? string.Join("; ", collector.Errors.Take(3)) : status.ToString();
                    Host.Log.Warn($"Одно наименование «{doc.Title}»: не записано — {why}");
                    TaskDialog.Show("VK Tools", "Не записано, модель не изменена: " + why);
                    return;
                }

                // Другие надстройки (BIM2B Updater) могут переписать значение при фиксации — проверяем, что осталось
                int reverted = _variants.SelectMany(v => v.Ids).Distinct()
                    .Select(id => doc.GetElement(id.ToElementId())?.LookupParameter(param))
                    .Count(p => p != null && p.StorageType == StorageType.String && !string.Equals(p.AsString() ?? string.Empty, _main, StringComparison.Ordinal) && !p.IsReadOnly);
                Host.Log.Info($"Одно наименование «{doc.Title}»: «{_main}», записано {written}, уже было {same}, пропущено {skipped.Count}, не удержалось {reverted}.");
                foreach (string s in skipped.Take(50)) Host.Log.Info("  пропущено: " + s);

                string text = $"«{_main}»\n\nЗаписано в {param}: {written}, уже было: {same}."
                    + (skipped.Count > 0 ? $"\nПропущено: {skipped.Count} — " + string.Join("; ", skipped.Take(3)) + (skipped.Count > 3 ? " … (весь список в логе)" : string.Empty) : string.Empty)
                    + (reverted > 0 ? $"\nНе удержалось {reverted}: значение переписала другая надстройка (BIM2B?)." : string.Empty)
                    + (settingsProblem == null
                        ? "\n\nПравило сохранено в модели: следующие прогоны «Системы и этажа», «Имён труб», «Изоляции» пишут это наименование вместо прежних вариантов."
                        : "\n\nПравило НЕ сохранено (" + settingsProblem + "): следующий прогон шагов может вернуть прежний вариант.")
                    + "\n\nОтмена — Ctrl+Z. Затем повторите «Проверить спецификацию».";
                TaskDialog.Show("VK Tools: одно наименование", text);
            }
            catch (Exception ex)
            {
                Host.Log.Error("Одно наименование: сбой.", ex);
                TaskDialog.Show("VK Tools", "Не записано: " + ex.Message + "\n\nПодробности в логе.");
            }
        }

        public string GetName() => OperationName;
    }
}
