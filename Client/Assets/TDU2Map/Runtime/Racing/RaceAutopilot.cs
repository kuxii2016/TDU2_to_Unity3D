using System.Collections.Generic;
using TDU2.Traffic;
using TDU2.Vehicles;
using UnityEngine;

namespace TDU2.Racing
{
    /// <summary>
    /// Nach dem Zieleinlauf (wie im Spiel): das Spielerauto wird übernommen, bremst auf Schritttempo herunter und folgt dabei
    /// der Straße (Spuren des Verkehrsnetzes, an Kreuzungen geradeaus), während das Ergebnis angezeigt wird.
    /// Ohne Straße in der Nähe (z.B. abgesperrte Übungsplätze): geradeaus ausrollen und anhalten.
    /// Steuert über <see cref="TDU2VehicleManager.ExternalControl"/> die Pedale/Lenkung des aktuellen Autos.
    /// </summary>
    public sealed class RaceAutopilot : MonoBehaviour
    {
        public TDU2VehicleManager Manager;
        [Tooltip("Tempo, auf das nach dem Ziel heruntergebremst wird (km/h); 0 = anhalten.")]
        public float CruiseKmh = 35f;
        [Tooltip("Maximaler Abstand zur nächsten Spur (m), sonst nur anhalten.")]
        public float SnapDistance = 14f;

        TDU2Vehicle _veh;
        TrafficLanes _lanes;
        readonly List<TrafficTrack> _chain = new List<TrafficTrack>();
        float _s;              // Position auf _chain[0]
        bool _onRoad;
        float _steer;
        float _t;
        readonly List<(int road, bool forward)> _succ = new List<(int, bool)>();

        public bool Active { get; private set; }

        public void Begin(TDU2VehicleManager manager)
        {
            Manager = manager;
            _veh = manager != null ? manager.Current : null;
            if (_veh == null) return;
            Active = true;
            _t = 0f;
            manager.ExternalControl = true;
            _lanes = RaceRoadPath.FindLanes();
            _chain.Clear(); _onRoad = false;
            var root = Root;
            if (_lanes != null && root != null)
            {
                var p = root.InverseTransformPoint(_veh.transform.position);
                var d = root.InverseTransformVector(_veh.transform.forward); d.y = 0f;
                if (_lanes.Nearest(p, d.normalized, SnapDistance, out var lane, out var s))
                {
                    _chain.Add(lane); _s = s; _onRoad = true;
                    Extend(400f);
                }
            }
            TDU2DebugLog.Event("Autopilot nach dem Ziel: " + (_onRoad ? "folgt der Straße (Straße " + ((LaneTrack)_chain[0]).RoadId + ")" : "keine Straße in der Nähe – bremst bis zum Stillstand"));
        }

        public void End()
        {
            if (!Active) return;
            Active = false;
            if (Manager != null) Manager.ExternalControl = false;
            if (_veh != null) { _veh.AccelPedal = 0f; _veh.BrakePedal = 0f; _veh.Steer = 0f; _veh.HandBrake = 0f; }
            _veh = null;
            _chain.Clear();
        }

        void OnDisable() => End();

        Transform Root => Manager != null && Manager.Streamer != null ? Manager.Streamer.MapRoot : null;

        void Extend(float meters)
        {
            float have = -_s;
            foreach (var t in _chain) have += t.Length;
            int guard = 0;
            while (have < meters && guard++ < 40)
            {
                var last = _chain[_chain.Count - 1] as LaneTrack;
                if (last == null)
                {
                    // Kreuzung -> Zielspur
                    var j = _chain[_chain.Count - 1] as JunctionTrack;
                    if (j == null || j.To == null) return;
                    _chain.Add(j.To); have += j.To.Length; continue;
                }
                _lanes.Successors(last, _succ);
                LaneTrack best = null; float bestAng = float.MaxValue;
                foreach (var (road, fwd) in _succ)
                {
                    var nl = _lanes.NextLane(last, road, fwd);
                    if (nl == null) continue;
                    float ang = Vector2.Angle(new Vector2(last.EndDir.x, last.EndDir.z), new Vector2(nl.StartDir.x, nl.StartDir.z));
                    if (ang < bestAng) { bestAng = ang; best = nl; }
                }
                if (best == null) return;
                var jt = _lanes.Junction(last, best);
                if (jt != null && jt.P != null && jt.P.Length > 1) { _chain.Add(jt); have += jt.Length; }
                _chain.Add(best); have += best.Length;
            }
        }

        /// <summary>Punkt d Meter vor der aktuellen Position entlang der Kette (Inselkoordinaten).</summary>
        Vector3 Ahead(float d)
        {
            float s = _s + d;
            for (int i = 0; i < _chain.Count; i++)
            {
                var t = _chain[i];
                if (s <= t.Length || i == _chain.Count - 1) { t.Sample(Mathf.Min(s, t.Length + 30f), out var p, out _); return p; }
                s -= t.Length;
            }
            return Vector3.zero;
        }

        void Project(Vector3 island)
        {
            // auf der aktuellen Spur weiterrücken (nur in der Nähe der bisherigen Position suchen)
            for (int guard = 0; guard < 4 && _chain.Count > 0; guard++)
            {
                var t = _chain[0];
                float best = float.MaxValue, bs = _s;
                for (int i = 0; i < t.P.Length; i++)
                {
                    if (t.S[i] < _s - 20f || t.S[i] > _s + 40f) continue;
                    var d = t.P[i] - island; d.y = 0f;
                    float dd = d.sqrMagnitude;
                    if (dd < best) { best = dd; bs = t.S[i]; }
                }
                _s = Mathf.Max(_s, bs);
                if (_s < t.Length - 0.5f || _chain.Count < 2) break;
                _s -= t.Length;
                _chain.RemoveAt(0);
            }
        }

        void FixedUpdate()
        {
            if (!Active) return;
            if (_veh == null || Manager == null || Manager.Current != _veh) { End(); return; }
            float dt = Time.fixedDeltaTime;
            _t += dt;
            float kmh = Mathf.Abs(_veh.SpeedKmh);
            float targetKmh = CruiseKmh;
            float steerCmd = 0f;
            var root = Root;
            if (_onRoad && root != null && _chain.Count > 0)
            {
                var island = root.InverseTransformPoint(_veh.transform.position);
                Project(island);
                Extend(300f);
                float look = Mathf.Clamp(kmh / 3.6f * 0.9f, 7f, 30f);
                var targetWorld = root.TransformPoint(Ahead(look));
                var local = _veh.transform.InverseTransformPoint(targetWorld);
                float ang = Mathf.Atan2(local.x, Mathf.Max(0.5f, local.z)) * Mathf.Rad2Deg;
                steerCmd = Mathf.Clamp(ang / 22f, -1f, 1f);
                // Tempo der Straße beachten (Kurven)
                var t0 = _chain[0];
                float lim = t0.MinLimit(_s, 40f) * 3.6f;
                if (lim > 1f) targetKmh = Mathf.Min(targetKmh, lim);
                // zu weit weg von der Spur (z.B. Mauer): anhalten
                t0.Sample(_s, out var lp, out _);
                var off = lp - island; off.y = 0f;
                if (off.magnitude > SnapDistance * 1.6f) { _onRoad = false; TDU2DebugLog.Event("Autopilot: Spur verloren – bremst"); }
            }
            else targetKmh = 0f;

            _steer = Mathf.MoveTowards(_steer, steerCmd, 3f * dt);
            float accel = 0f, brake = 0f, hand = 0f;
            if (targetKmh <= 0.5f)
            {
                // im Stand nicht die Bremse halten (TDU-Verhalten: sonst Rückwärtsgang) -> Handbremse
                if (kmh > 3f) brake = Mathf.Clamp(kmh / 60f, 0.25f, 0.7f); else hand = 1f;
            }
            else if (kmh > targetKmh + 3f) brake = Mathf.Clamp((kmh - targetKmh) / 40f, 0.15f, 0.65f);
            else if (kmh < targetKmh - 2f) accel = Mathf.Clamp((targetKmh - kmh) / 25f, 0.15f, 0.45f);
            _veh.AccelPedal = accel;
            _veh.BrakePedal = brake;
            _veh.Steer = _steer;
            _veh.HandBrake = hand;
        }
    }
}
