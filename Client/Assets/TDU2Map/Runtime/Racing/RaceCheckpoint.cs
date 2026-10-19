using TDU2.Map.Formats;
using UnityEngine;

namespace TDU2.Map.Racing
{
    /// <summary>Ein Checkpoint / Tor eines Rennens (aus <see cref="Ige2Checkpoint"/>). Alle Koordinaten in Inselkoordinaten.</summary>
    public sealed class RaceCheckpoint
    {
        public int Index;
        public Vector3 Position;    // Inselkoordinaten
        public Vector3 Forward;     // Durchfahrtsrichtung (+Z der Tor-Matrix)
        public Vector3 Right;       // quer zur Fahrtrichtung (+X der Tor-Matrix)
        public Vector3 Up;
        public float Width;         // Durchfahrtsbreite in m
        public int Kind;            // CHK2-Art: 0 Linie, 1 Kegel-Tor, 2 Tor (Bogen), 3 Bremszone
        public bool OnRoad;         // Placement 0: an eine Straße gebunden (Spiel gibt dann seitlich Toleranz dazu)
        public bool IsFinish;       // letztes Tor der Route (wird beim Aufbau gesetzt)

        public Ige2GateKind GateKind => (Ige2GateKind)Kind;
        public bool IsStart => Index == 0;
        public bool IsBrakingZone => Kind == (int)Ige2GateKind.BrakingZone;

        /// <summary>
        /// Halbe Breite, innerhalb der die Durchfahrt zählt. Das Spiel erweitert Straßen-Checkpoints um
        /// DBEnvironnement.CheckpointAdditionalWidthAfterRoad (7 m) bzw. ...Offroad (11 m).
        /// </summary>
        public float HalfAcceptWidth(bool offroad)
        {
            float extra = OnRoad ? (offroad ? 11f : 7f) * 0.5f : 1.5f;
            return Mathf.Max(2.5f, Width * 0.5f) + extra;
        }

        /// <summary>Länge der Bremszone in Fahrtrichtung (im Spiel nicht gespeichert; vermutet: so tief wie breit).</summary>
        public float ZoneDepth => Mathf.Max(8f, Width);

        public static RaceCheckpoint From(Ige2Checkpoint cp, int index)
        {
            var f = cp.Forward; f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            f.Normalize();
            var r = cp.Right; r.y = 0f;
            if (r.sqrMagnitude < 1e-6f) r = new Vector3(f.z, 0f, -f.x);
            r.Normalize();
            return new RaceCheckpoint
            {
                Index = index,
                Position = cp.Position,
                Forward = f,
                Right = r,
                Up = cp.Up.sqrMagnitude > 1e-6f ? cp.Up.normalized : Vector3.up,
                Width = cp.Width > 0.5f ? cp.Width : 10f,
                Kind = (int)cp.GateKind,
                OnRoad = cp.Placement == 0,
            };
        }
    }
}
