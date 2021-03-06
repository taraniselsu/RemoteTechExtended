using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using UnityEngine;

namespace RemoteTech {
    [Flags]
    public enum GraphMode {
        None = 0x00,
        MapView = 0x01,
        TrackingStation = 0x02,
    }

    [Flags]
    public enum EdgeType {
        None = 0x00,
        Omni = 0x01,
        Dish = 0x02,
        Connection = 0x04,
    }

    public class NetworkManager : IEnumerable<ISatellite>, IConfigNode {
        public delegate void ConnectionHandler(Path<ISatellite> connection);

        public delegate void TypedEdgeHandler(TypedEdge<ISatellite> edge);

        public event ConnectionHandler ConnectionUpdated;
        public event TypedEdgeHandler EdgeUpdated;

        public float SignalSpeed;

        public int Count { get { return mCore.Satellites.Count + 1; } }

        public Dictionary<Guid, CelestialBody> Planets { get; private set; }
        public MissionControlSatellite MissionControl { get; private set; }
        public Dictionary<Guid, List<ISatellite>> Graph { get; private set; }

        public ISatellite this[Guid guid] {
            get {
                if (guid == MissionControl.Guid)
                    return MissionControl;
                return mCore.Satellites[guid];
            }
        }

        private const int REFRESH_TICKS = 50;
        private readonly RTCore mCore;
        
        private int mTick;
        private int mTickIndex;

        public NetworkManager(RTCore core) {
            mCore = core;
            MissionControl = new MissionControlSatellite();
            Graph = new Dictionary<Guid, List<ISatellite>>();
            Planets = GeneratePlanetGuidMap();

            OnSatelliteRegister(MissionControl);

            mCore.Satellites.Registered += OnSatelliteRegister;
            mCore.Satellites.Unregistered += OnSatelliteUnregister;
            mCore.PhysicsUpdated += OnPhysicsUpdate;
        }

        public void Dispose() {
            mCore.PhysicsUpdated -= OnPhysicsUpdate;
            mCore.Satellites.Registered -= OnSatelliteRegister;
            mCore.Satellites.Unregistered -= OnSatelliteUnregister;
        }

        public void Load(ConfigNode node) {
            try {
                if (node.HasValue("SignalSpeed"))
                    throw new ArgumentException("SignalSpeed non-exist");
                SignalSpeed = Single.Parse(node.GetValue("SignalSpeed"));
            } catch (ArgumentException) {
                SignalSpeed = 299792458.0f;
            }
        }

        public void Save(ConfigNode node) {
            node.AddValue("SignalSpeed", SignalSpeed.ToString());
        }

        private Dictionary<Guid, CelestialBody> GeneratePlanetGuidMap() {
            var planetMap = new Dictionary<Guid, CelestialBody>();

            foreach (CelestialBody cb in FlightGlobals.Bodies) {
                planetMap[cb.Guid()] = cb;
            }

            return planetMap;
        }

        public static float Distance(ISatellite a, ISatellite b) {
            return Vector3.Distance(a.Position, b.Position);
        }

        public void FindPath(ISatellite start, IEnumerable<ISatellite> commandStations) {
            List<Path<ISatellite>> paths = new List<Path<ISatellite>>();
            foreach (ISatellite root in commandStations.Concat(new[] { MissionControl })) {
                if(start != root) {
                    paths.Add(Pathfinder.Solve(start, root, s => {
                        return Graph[s.Guid].Where(x => x.Powered);   
                    }, Distance, Distance)); 
                }
            }
            OnConnectionUpdate(paths.Min());
        }

        private void UpdateGraph(ISatellite a) {
            var result = new List<TypedEdge<ISatellite>>();

            foreach (ISatellite b in this) {
                EdgeType edge = IsConnectedTo(a, b);
                if (edge == EdgeType.None) continue;
                result.Add(new TypedEdge<ISatellite>(a, b, edge));
            }

            // Process
            RTUtil.Log("Debug: a={0}, Graph[a]={1}", a.Guid, Graph.ContainsKey(a.Guid));
            foreach (ISatellite b in Graph[a.Guid]) {
                var edge = new TypedEdge<ISatellite>(a, b, EdgeType.None);
                if (!result.Contains(edge)) {
                    OnEdgeUpdate(edge);
                }
            }
            Graph[a.Guid].Clear();
            foreach (var edge in result) {
                Graph[a.Guid].Add(edge.B);
                OnEdgeUpdate(edge);
            }
        }

        private EdgeType IsConnectedTo(ISatellite a, ISatellite b) {
            bool los = LineOfSight(a, b) || CheatOptions.InfiniteEVAFuel;
            if (a == b || !los) return EdgeType.None;

            float distance = Distance(a, b);
            if (distance < (a.Omni + b.Omni) / 2 && a.Omni > 0 && b.Omni > 0) {
                return EdgeType.Omni;              
            }

            float a_range = 0.0f;
            float b_range = 0.0f;

            foreach (Dish dish in a.Dishes.Where(d => d.Target == b.Guid)) {
                a_range = Math.Max(a_range, dish.Distance);
            }

            foreach (Dish dish in a.Dishes.Where(d => Planets.ContainsKey(d.Target) && 
                                                      Planets[d.Target] == b.Body)) {
                Vector3 dir_cb = (Planets[dish.Target].position - a.Position);
                Vector3 dir_b = (b.Position - a.Position);
                if (Vector3.Dot(dir_cb.normalized, dir_b.normalized) >= dish.Factor) {
                    a_range = Math.Max(a_range, dish.Distance);
                }
            }

            foreach (Dish dish in b.Dishes.Where(d => d.Target == a.Guid)) {
                b_range = Math.Max(b_range, dish.Distance);
            }

            foreach (Dish dish in b.Dishes.Where(d => Planets.ContainsKey(d.Target) &&
                                                      Planets[d.Target] == a.Body)) {
                Vector3 dir_cb = (Planets[dish.Target].position - b.Position);
                Vector3 dir_a = (a.Position - b.Position);
                if (Vector3.Dot(dir_cb.normalized, dir_a.normalized) >= dish.Factor) {
                    b_range = Math.Max(b_range, dish.Distance);
                }
            }

            if ((distance < (a_range + b_range) / 2 && a_range > 0 && b_range > 0) || 
                (distance < (a.Omni + b_range) / 2 && a.Omni > 0 && b_range > 0) ||
                (distance < (a_range + b.Omni) / 2 && a_range > 0 && b.Omni > 0)) {
                return EdgeType.Dish;
            }
                

            return EdgeType.None;
        }

        private static bool LineOfSight(ISatellite a, ISatellite b) {
            foreach (CelestialBody referenceBody in FlightGlobals.Bodies) {
                Vector3d bodyFromA = referenceBody.position - a.Position;
                Vector3d bFromA = b.Position - a.Position;
                if (Vector3d.Dot(bodyFromA, bFromA) > 0) {
                    Vector3d bFromAnorm = bFromA.normalized;
                    if (Vector3d.Dot(bodyFromA, bFromAnorm) < bFromA.magnitude) {
                        Vector3d lateralOffset = bodyFromA -
                                                 Vector3d.Dot(bodyFromA, bFromAnorm)*bFromAnorm;
                        if (lateralOffset.magnitude < (referenceBody.Radius - 5)) {
                            return false;
                        }
                    }
                }
            }
            return true;
        }

        public void OnPhysicsUpdate() {
            int takeCount = (mCore.Satellites.Count/REFRESH_TICKS) +
                            (((mCore.Satellites.Count%REFRESH_TICKS) > mTick) ? 1 : 0);
            IEnumerable<ISatellite> commandStations = mCore.Satellites.FindCommandStations();
            foreach (VesselSatellite s in mCore.Satellites.Skip(mTickIndex).Take(takeCount)) {
                UpdateGraph(s);
                RTUtil.Log("Status for {0}: CS?{1}, E?{2}", 
                    s, commandStations.Contains(s), Graph[s.Guid].ToDebugString());
                if (s.Vessel.loaded || RTCore.Instance.IsTrackingStation) {
                    FindPath(s, commandStations);
                }
            }
            mTickIndex += takeCount;
            mTick = (mTick + 1)%REFRESH_TICKS;
            if (mTick == 0) {
                mTickIndex = 0;
            }
        }

        private void OnSatelliteUnregister(ISatellite s) {
            RTUtil.Log("NetworkManager: SatelliteUnregister {0} {1}", s, s.Guid);
            Graph.Remove(s.Guid);
            foreach (var list in Graph.Values) {
                list.Remove(s);
            }
        }

        private void OnSatelliteRegister(ISatellite s) {
            RTUtil.Log("NetworkManager: SatelliteRegister {0} {1}", s, s.Guid);
            Graph[s.Guid] = new List<ISatellite>();
        }

        private void OnEdgeUpdate(TypedEdge<ISatellite> edge) {
            if (EdgeUpdated != null) {
                EdgeUpdated.Invoke(edge);
                RTUtil.Log(edge.ToString());
            }
        }

        private void OnConnectionUpdate(Path<ISatellite> path) {
            if (ConnectionUpdated != null) {
                ConnectionUpdated.Invoke(path);
            }
        }
    
        public IEnumerator<ISatellite> GetEnumerator() {
            return mCore.Satellites.Cast<ISatellite>().Concat(new[] {
                MissionControl
            }).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() {
            return GetEnumerator();
        }
    }

    public class MissionControlSatellite : ISatellite {
        public bool Powered { get { return true; } }
        public bool Visible { get { return true; } }
        public String Name { get { return "Mission Control"; } set { return; } }
        public Guid Guid { get { return new Guid("5105f5a9d62841c6ad4b21154e8fc488"); } }
        public Vector3 Position {
            get {
                return FlightGlobals.Bodies[1].GetWorldSurfacePosition(-0.1313315, -74.59484, 75.0151197366649);
            }
        }
        public CelestialBody Body { get { return FlightGlobals.Bodies[1]; } }
        public bool LocalControl { get { return false; } }
        public float Omni { get { return 8000000; } }
        public IEnumerable<Dish> Dishes { get { return Enumerable.Empty<Dish>(); } }

        public override String ToString() {
            return Name;
        }

        public override int GetHashCode() {
            return Guid.GetHashCode();
        }

        public bool CommandStation { get { return true; } }
    }
}
