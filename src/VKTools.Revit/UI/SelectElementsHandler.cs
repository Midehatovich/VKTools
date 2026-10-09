using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using VKTools.Revit.Compat;
using VKTools.Revit.Infrastructure;

namespace VKTools.Revit.UI
{
    /// <summary>
    /// «Показать в модели» из немодального окна. Окно живёт вне контекста Revit API,
    /// поэтому выделение идёт через ExternalEvent: Revit вызовет Execute, когда будет готов.
    /// </summary>
    internal sealed class SelectElementsHandler : IExternalEventHandler
    {
        private List<long> _ids = new List<long>();
        private Document? _document;

        public void Request(Document document, IEnumerable<long> ids)
        {
            _document = document;
            _ids = ids.Distinct().ToList();
        }

        public void Execute(UIApplication app)
        {
            try
            {
                Document? target = _document;
                UIDocument? uidoc = app.ActiveUIDocument;
                if (target == null || !target.IsValidObject)
                {
                    TaskDialog.Show("VK Tools", "Модель, по которой построен отчёт, уже закрыта. Запустите проверку заново.");
                    return;
                }
                if (uidoc == null || !uidoc.Document.Equals(target))
                {
                    TaskDialog.Show("VK Tools", $"Отчёт построен по модели «{target.Title}». Перейдите в её окно и нажмите ещё раз.");
                    return;
                }

                var ids = new List<ElementId>();
                int missing = 0;
                foreach (long id in _ids)
                {
                    ElementId elementId = id.ToElementId();
                    if (target.GetElement(elementId) != null) ids.Add(elementId);
                    else missing++;
                }
                if (ids.Count == 0)
                {
                    TaskDialog.Show("VK Tools", "Элементов из этой строки в модели уже нет (удалены после проверки).");
                    return;
                }

                uidoc.Selection.SetElementIds(ids);
                try
                {
                    uidoc.ShowElements(ids);
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                {
                    // Нет подходящего вида: элементы выделены, но показать их негде
                }
                if (missing > 0)
                {
                    TaskDialog.Show("VK Tools", $"Выделено {ids.Count}. Ещё {missing} уже удалены из модели.");
                }
            }
            catch (Exception ex)
            {
                Host.Log.Error("Не удалось показать элементы.", ex);
                TaskDialog.Show("VK Tools", "Не удалось показать элементы: " + ex.Message);
            }
        }

        public string GetName() => "VK Tools: показать в модели";
    }
}
