using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using VKTools.Core.Mep;
using VKTools.Revit.Compat;

namespace VKTools.Revit.Mep
{
    /// <summary>Состав системы: терминалы (MEPSystem.Elements), сеть (PipingNetwork) и базовое оборудование.</summary>
    internal sealed class SystemMembers
    {
        public SystemMembers(List<long> terminals, HashSet<long> network, long baseEquipmentId)
        {
            Terminals = terminals;
            Network = network;
            BaseEquipmentId = baseEquipmentId;
            All = new HashSet<long>(network);
            All.UnionWith(terminals);
            if (baseEquipmentId > 0) All.Add(baseEquipmentId);
        }

        public List<long> Terminals { get; }

        public HashSet<long> Network { get; }

        public long BaseEquipmentId { get; }

        public HashSet<long> All { get; }
    }

    /// <summary>
    /// Трубопроводные системы документа. Состав систем и системы элементов читаются по требованию и кэшируются:
    /// обход всех семейств модели не нужен (в 0.2 он шёл трижды за пробный прогон).
    /// Кэш действителен, пока модель не менялась: после записи или отката — новый PipingModel.
    /// </summary>
    internal sealed class PipingModel
    {
        private readonly Document _doc;
        private readonly Dictionary<long, HashSet<long>> _systemsOf = new Dictionary<long, HashSet<long>>();
        private readonly Dictionary<long, SystemMembers> _members = new Dictionary<long, SystemMembers>();

        private PipingModel(Document doc)
        {
            _doc = doc;
        }

        public Dictionary<long, PipingSystem> Systems { get; } = new Dictionary<long, PipingSystem>();

        public static PipingModel Take(Document doc)
        {
            var model = new PipingModel(doc);
            foreach (PipingSystem system in new FilteredElementCollector(doc).OfClass(typeof(PipingSystem)).Cast<PipingSystem>())
            {
                model.Systems[system.Id.ToLong()] = system;
            }
            return model;
        }

        public Element? Element(long id) => _doc.GetElement(id.ToElementId());

        /// <summary>Системы коннекторов элемента (кэш).</summary>
        public HashSet<long> SystemsOf(long elementId)
        {
            if (_systemsOf.TryGetValue(elementId, out HashSet<long>? cached)) return cached;
            Element? e = Element(elementId);
            HashSet<long> systems;
            try
            {
                systems = e == null ? new HashSet<long>() : Connectors.SystemsOf(e);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                systems = new HashSet<long>();
            }
            _systemsOf[elementId] = systems;
            return systems;
        }

        /// <summary>Состав системы (кэш). Нет такой системы — пустой состав.</summary>
        public SystemMembers MembersOf(long systemId)
        {
            if (_members.TryGetValue(systemId, out SystemMembers? cached)) return cached;
            var terminals = new List<long>();
            var network = new HashSet<long>();
            long baseEquipment = -1;
            if (Systems.TryGetValue(systemId, out PipingSystem? system))
            {
                foreach (Element e in Iterate(system.Elements)) terminals.Add(e.Id.ToLong());
                foreach (Element e in Iterate(system.PipingNetwork)) network.Add(e.Id.ToLong());
                baseEquipment = system.BaseEquipment?.Id.ToLong() ?? -1;
            }
            var members = new SystemMembers(terminals.Distinct().OrderBy(x => x).ToList(), network, baseEquipment);
            _members[systemId] = members;
            return members;
        }

        /// <summary>Записи систем для логики имён в ядре.</summary>
        public Dictionary<long, SystemRecord> Records()
        {
            var records = new Dictionary<long, SystemRecord>();
            foreach (KeyValuePair<long, PipingSystem> pair in Systems)
            {
                PipingSystem system = pair.Value;
                ElementId typeId = system.GetTypeId();
                var type = _doc.GetElement(typeId) as MEPSystemType;
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

        private static IEnumerable<Element> Iterate(ElementSet? set)
        {
            if (set == null) yield break;
            foreach (Element e in set)
            {
                if (e != null) yield return e;
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
