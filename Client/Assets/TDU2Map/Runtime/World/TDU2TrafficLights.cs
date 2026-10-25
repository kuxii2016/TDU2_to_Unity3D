using System.Collections.Generic;
using UnityEngine;

namespace TDU2.World
{
    /// <summary>
    /// Ampeln (World Actor SF_Xin_*, Entity__Actor__TrafficLights): das Modell hat die Leuchten HALO_R, HALO_O, HALO_G
    /// als eigene Instanzen – ohne Steuerung leuchten alle drei. Hier läuft ein fester Zyklus:
    ///   Grün -> Gelb -> Rot (die querende Richtung jeweils um eine halbe Periode versetzt).
    /// Zeitbasis ist die Serveruhr (TDU2Session.ServerSeconds): alle Clients sehen dieselbe Phase ohne eigene Netzwerkpakete.
    /// Der Verkehr fragt <see cref="MustStop"/> und hält bei Rot/Gelb vor der Ampel.
    /// </summary>
    public static class TDU2TrafficLights
    {
        public sealed class Signal
        {
            public Transform Root;
            public Vector3 Pos;       // Map-Root-Raum (Spielkoordinaten)
            public Vector3 Axis;      // Blickachse der Ampel (Map-Root-Raum, waagerecht, normiert)
            public int Group;         // 0 / 1: die beiden Fahrtrichtungen einer Kreuzung
            public float Offset;      // Versatz je Kreuzung
            public Renderer[] R, O, G;
            public int Shown = -1;
        }

        public static float GreenTime = 14f, AmberTime = 3f, AllRedTime = 2f;
        /// <summary>Raster, in dem Ampeln zur selben Kreuzung (gleicher Versatz) gezählt werden (m).</summary>
        public static float JunctionCell = 70f;
        /// <summary>Falls die Blickachse des Modells quer zur Fahrtrichtung liegt (Verkehr hält bei der falschen Ampel): umschalten.</summary>
        public static bool SwapAxis = false;
        static float Half => GreenTime + AmberTime + AllRedTime;
        static float Cycle => 2f * Half;

        static readonly List<Signal> _all = new List<Signal>();
        public static int Count => _all.Count;

        public static double Clock => TDU2.Online.TDU2Session.ServerSeconds;

        /// <summary>0 = Grün, 1 = Gelb, 2 = Rot.</summary>
        public static int StateOf(Signal s)
        {
            double t = (Clock + s.Offset) % Cycle;
            if (t < 0) t += Cycle;
            if (s.Group == 1) t = (t + Half) % Cycle;
            if (t < GreenTime) return 0;
            if (t < GreenTime + AmberTime) return 1;
            return 2;
        }

        /// <summary>Eine gebaute Ampel anmelden (holder = Instanz-Wurzel des Actors).</summary>
        public static bool Register(Transform holder, Transform mapRoot)
        {
            if (holder == null || mapRoot == null) return false;
            var r = new List<Renderer>(); var o = new List<Renderer>(); var g = new List<Renderer>();
            foreach (var t in holder.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToUpperInvariant();
                List<Renderer> dst = n.Contains("HALO_R") ? r : n.Contains("HALO_O") ? o : n.Contains("HALO_G") ? g : null;
                if (dst == null) continue;
                foreach (var rr in t.GetComponentsInChildren<Renderer>(true)) if (!dst.Contains(rr)) dst.Add(rr);
            }
            if (r.Count == 0 && o.Count == 0 && g.Count == 0) return false;
            var pos = mapRoot.InverseTransformPoint(holder.position);
            var ax = mapRoot.InverseTransformVector(holder.forward); ax.y = 0f;
            ax = ax.sqrMagnitude > 1e-6f ? ax.normalized : Vector3.forward;
            if (SwapAxis) ax = new Vector3(ax.z, 0f, -ax.x);
            int cx = Mathf.FloorToInt(pos.x / JunctionCell), cz = Mathf.FloorToInt(pos.z / JunctionCell);
            uint h = (uint)(cx * 73856093) ^ (uint)(cz * 19349663);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            var s = new Signal
            {
                Root = holder,
                Pos = pos,
                Axis = ax,
                Group = Mathf.Abs(ax.x) > Mathf.Abs(ax.z) ? 1 : 0,
                Offset = (h % 10000) / 10000f * Cycle,
                R = r.ToArray(),
                O = o.ToArray(),
                G = g.ToArray(),
            };
            _all.Add(s);
            Show(s, StateOf(s));
            return true;
        }

        static void Show(Signal s, int state)
        {
            s.Shown = state;
            foreach (var x in s.G) if (x != null) x.enabled = state == 0;
            foreach (var x in s.O) if (x != null) x.enabled = state == 1;
            foreach (var x in s.R) if (x != null) x.enabled = state == 2;
        }

        /// <summary>Alle Leuchten neu setzen (z.B. nachdem ein Interieur die Welt aus- und wieder eingeblendet hat).</summary>
        public static void ForceRefresh()
        {
            foreach (var s in _all) if (s != null && s.Root != null) Show(s, StateOf(s));
        }

        /// <summary>Jedes Bild (TDU2TrafficLightDriver): Leuchten umschalten, abgebaute Sektoren entfernen.</summary>
        public static void Tick()
        {
            for (int i = _all.Count - 1; i >= 0; i--)
            {
                var s = _all[i];
                if (s.Root == null) { _all.RemoveAt(i); continue; }
                int st = StateOf(s);
                if (st != s.Shown) Show(s, st);
            }
        }

        /// <summary>
        /// Muss ein Fahrzeug an pos (Map-Root-Raum) mit Fahrtrichtung fwd vor einer Ampel halten? dist = Abstand zur Ampel.
        /// Gelb: nur halten, wenn noch genug Weg zum Bremsen bleibt.
        /// </summary>
        public static bool MustStop(Vector3 pos, Vector3 fwd, float look, float speed, out float dist)
        {
            dist = float.MaxValue;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-6f || _all.Count == 0) return false;
            fwd.Normalize();
            bool stop = false;
            foreach (var s in _all)
            {
                if (s.Root == null) continue;
                var d = s.Pos - pos; d.y = 0f;
                float f = Vector3.Dot(d, fwd);
                if (f < 1.5f || f > look) continue;
                float lat = Mathf.Abs(d.x * fwd.z - d.z * fwd.x);
                if (lat > 10f) continue;
                if (Mathf.Abs(Vector3.Dot(s.Axis, fwd)) < 0.7f) continue;   // Ampel der querenden Richtung
                int st = StateOf(s);
                if (st == 0) continue;
                if (st == 1 && speed * speed / (2f * 4f) > f - 3f) continue;   // Gelb, zu nah zum Halten -> fahren
                if (f < dist) { dist = f; stop = true; }
            }
            return stop;
        }
    }

    /// <summary>Schaltet die Ampeln jedes Bild (vom Streamer angelegt).</summary>
    public sealed class TDU2TrafficLightDriver : MonoBehaviour
    {
        void Update() => TDU2TrafficLights.Tick();
    }
}
