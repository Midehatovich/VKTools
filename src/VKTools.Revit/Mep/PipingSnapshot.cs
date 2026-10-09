using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using VKTools.Core.Mep;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Mep
{
    /// <summary>
    /// Снимок трубопроводных систем документа: системы и принадлежность элементов по коннекторам.
    /// MEPSystem.Elements — терминалы, PipingSystem.PipingNetwork — трубы и фитинги;
    /// обход всех элементов с трубными коннекторами — независимая сверка (как в PipingSystemMerge v1.2).
    /// </summary>
    internal sealed class PipingSnapshot
    {
        private PipingSnapshot()
        {
        }

        public Dictionary<long, PipingSystem> Systems { get; } = new Dictionary<long, PipingSystem>();

        /// <summary>Система → элементы, у которых есть коннектор в этой системе.</summary>
        public Dictionary<long, HashSet<long>> Members { get; } = new Dictionary<long, HashSet<long>>();

        /// <summary>Элемент → системы его коннекторов.</summary>
        public Dictionary<long, HashSet<long>> ElementSystems { get; } = new Dictionary<long, HashSet<long>>();

        public Dictionary<long, Element> Elements { get; } = new Dictionary<long, Element>();

        public static PipingSnapshot Take(Document doc)
        {
            var snapshot = new PipingSnapshot();
            foreach (PipingSystem system in new FilteredElementCollector(doc).OfClass(typeof(PipingSystem)).Cast<PipingSystem>())
            {
                long id = system.Id.ToLong();
                snapshot.Systems[id] = system;
                snapshot.Members[id] = new HashSet<long>();
            }

            IEnumerable<Element> curves = new FilteredElementCollector(doc)
                .WherePasses(new ElementMulticategoryFilter(new[] { BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_FlexPipeCurves }))
                .WhereElementIsNotElementType();
            IEnumerable<Element> instances = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance));

            foreach (Element element in curves.Concat(instances))
            {
                HashSet<long> systems;
                try
                {
                    systems = Connectors.SystemsOf(element);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    continue;
                }
                if (systems.Count == 0 && !(element is MEPCurve) && Connectors.Piping(element).Count == 0) continue;

                long id = element.Id.ToLong();
                snapshot.Elements[id] = element;
                snapshot.ElementSystems[id] = systems;
                foreach (long systemId in systems)
                {
                    if (!snapshot.Members.TryGetValue(systemId, out HashSet<long>? members))
                    {
                        members = new HashSet<long>();
                        snapshot.Members[systemId] = members;
                    }
                    members.Add(id);
                }
            }
            return snapshot;
        }

        public HashSet<long> MembersOf(long systemId) =>
            Members.TryGetValue(systemId, out HashSet<long>? members) ? members : new HashSet<long>();

        public HashSet<long> SystemsOf(long elementId) =>
            ElementSystems.TryGetValue(elementId, out HashSet<long>? systems) ? systems : new HashSet<long>();

        /// <summary>Записи систем для логики имён в ядре.</summary>
        public Dictionary<long, SystemRecord> Records(Document doc)
        {
            var records = new Dictionary<long, SystemRecord>();
            foreach (KeyValuePair<long, PipingSystem> pair in Systems)
            {
                PipingSystem system = pair.Value;
                ElementId typeId = system.GetTypeId();
                var type = doc.GetElement(typeId) as MEPSystemType;
                records[pair.Key] = new SystemRecord(
                    pair.Key,
                    system.Name ?? string.Empty,
                    typeId.ToLong(),
                    type?.Name ?? string.Empty,
                    type?.Abbreviation,
                    SystemClassification(system));
            }
            return records;
        }

        public static string SystemClassification(PipingSystem system)
        {
            try
            {
                return system.SystemType.ToString();
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return "?";
            }
        }
    }

    /// <summary>Трубные коннекторы элементов: у труб — ConnectorManager, у семейств — MEPModel.ConnectorManager.</summary>
    internal static class Connectors
    {
        public static ConnectorManager? Manager(Element? element)
        {
            try
            {
                switch (element)
                {
                    case FamilyInstance instance:
                        return instance.MEPModel?.ConnectorManager;
                    case MEPCurve curve:
                        return curve.ConnectorManager;
                    default:
                        return null;
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
        }

        public static List<Connector> Piping(Element? element)
        {
            var result = new List<Connector>();
            ConnectorManager? manager = Manager(element);
            if (manager?.Connectors == null) return result;
            foreach (Connector connector in manager.Connectors)
            {
                try
                {
                    if (connector.Domain == Domain.DomainPiping) result.Add(connector);
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Коннектор без домена (редко у кривых семейств) не участвует
                }
            }
            return result;
        }

        /// <summary>Id системы коннектора; -1 — без системы.</summary>
        public static long SystemId(Connector? connector)
        {
            if (connector == null) return -1;
            try
            {
                MEPSystem? system = connector.MEPSystem;
                return system != null && system.IsValidObject ? system.Id.ToLong() : -1;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return -1;
            }
        }

        public static Connector? Find(Element? element, int connectorId) =>
            Piping(element).FirstOrDefault(c => c.Id == connectorId);

        public static HashSet<long> SystemsOf(Element element)
        {
            var result = new HashSet<long>();
            if (element is MEPCurve curve)
            {
                try
                {
                    MEPSystem? system = curve.MEPSystem;
                    if (system != null) result.Add(system.Id.ToLong());
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Система трубы недоступна — берём по коннекторам ниже
                }
            }
            foreach (Connector connector in Piping(element))
            {
                long id = SystemId(connector);
                if (id > 0) result.Add(id);
            }
            return result;
        }

        /// <summary>Элементы, физически присоединённые к element (логические ссылки на объект системы отбрасываются).</summary>
        public static HashSet<long> NeighbourIds(Element element)
        {
            var result = new HashSet<long>();
            long own = element.Id.ToLong();
            foreach (Connector connector in Piping(element))
            {
                try
                {
                    if (!connector.IsConnected) continue;
                    foreach (Connector other in connector.AllRefs)
                    {
                        Element owner = other.Owner;
                        if ((owner is MEPCurve || owner is FamilyInstance) && owner.Id.ToLong() != own) result.Add(owner.Id.ToLong());
                    }
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Недоступный коннектор пропускаем
                }
            }
            return result;
        }
    }

    /// <summary>Положение элемента для проверки «геометрия не изменилась».</summary>
    internal readonly struct GeometryKey
    {
        private const double Tolerance = 1e-6; // футы
        private readonly double[]? _values;
        private readonly bool _isCurve;

        private GeometryKey(bool isCurve, double[] values)
        {
            _isCurve = isCurve;
            _values = values;
        }

        public static GeometryKey Of(Element element)
        {
            try
            {
                switch (element.Location)
                {
                    case LocationCurve lc:
                        XYZ a = lc.Curve.GetEndPoint(0), b = lc.Curve.GetEndPoint(1);
                        return new GeometryKey(true, new[] { a.X, a.Y, a.Z, b.X, b.Y, b.Z });
                    case LocationPoint lp:
                        double rotation = 0;
                        try
                        {
                            rotation = lp.Rotation;
                        }
                        catch (Autodesk.Revit.Exceptions.ApplicationException)
                        {
                            // У части семейств поворот не определён
                        }
                        return new GeometryKey(false, new[] { lp.Point.X, lp.Point.Y, lp.Point.Z, rotation });
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // Нет положения — сравнивать нечего
            }
            return default;
        }

        public bool SameAs(GeometryKey other)
        {
            if (_values == null || other._values == null) return _values == null && other._values == null;
            if (_isCurve != other._isCurve || _values.Length != other._values.Length) return false;
            for (int i = 0; i < _values.Length; i++)
            {
                if (Math.Abs(_values[i] - other._values[i]) > Tolerance) return false;
            }
            return true;
        }
    }
}
