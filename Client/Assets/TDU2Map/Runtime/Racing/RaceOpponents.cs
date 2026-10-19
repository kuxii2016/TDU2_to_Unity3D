using System.Collections.Generic;
using System.Threading.Tasks;
using TDU2.Map.Vehicles;
using UnityEngine;

namespace TDU2.Map.Racing
{
    /// <summary>
    /// Fahrzeuge einer Prüfung (ig2 VEHI), z.B. die zu überholenden Autos der Überhol-Tests.
    /// Vereinfachte KI: das Modell (DBCarPhysicsData-ID "VSph") fährt kinematisch mit seinem Höchsttempo ("Spmx") die
    /// Checkpoint-Linie der Prüfung ab dem nächsten vor ihm liegenden Tor entlang. Es fährt los, sobald der Spieler den
    /// Checkpoint "Stch" − 1 passiert hat (Stch = 1: beim Start) – vermutet, im Original steuert das AiVehicleOppRace.
    /// </summary>
    public sealed class RaceOpponents : MonoBehaviour
    {
        sealed class Opp
        {
            public RaceOpponentDef Def;
            public Task<CarAssets> Load;
            public GameObject Go;
            public CarModel Model;
            public CarAssets Assets;
            public readonly List<Vector3> Path = new List<Vector3>();   // Inselkoordinaten
            public int Seg;
            public float SegT, Speed;
            public bool Active, Done;
            public Vector3 Island, Fwd = Vector3.forward;   // aktuelle Lage in Inselkoordinaten
            public float Lift;                               // Abstand Ursprung -> Unterkante (Räder) in m
            public float Y; public bool HasY;
            public float Pitch; public bool HasPitch;
            // Räder wie TDU2Multiplayer: STIRRUP_xx (Lenkung), DISC_xx (Drehung)
            public (Transform stirrup, Quaternion sBase, float sign, Transform disc, Quaternion dBase, float radius, bool front)[] Wheels;
            public float Spin, Steer;
            public float[] VLim;          // Kurventempo je Routenpunkt (m/s), null = keine Begrenzung
            public float Traveled;        // gefahrener Weg ab der Startlinie (m)
            public float FinishTime = -1f;   // Rennzeit beim Überqueren der Ziellinie (s), -1 = noch nicht
            public float Shift, ShiftTarget, ClearSince;   // seitliches Ausweichen/Überholen (m, zusätzlich zur Spur)
        }

        [Tooltip("Gegner weichen einander und dem Spieler aus (bremsen hinter langsameren Autos, überholen seitlich).")]
        public bool Avoidance = true;
        [Tooltip("Namen der Gegner über den Autos anzeigen.")]
        public bool NameTags = true;

        /// <summary>Stand eines Gegners (für Platzierung/Ergebnis).</summary>
        public struct Standing
        {
            public RaceOpponentDef Def;
            public string Name, Car;
            public float Traveled, FinishTime, Speed;
            public bool Finished => FinishTime >= 0f;
        }

        /// <summary>Weg der Ziellinie ab Start (m); erreicht ein Gegner ihn, wird seine Zielzeit gemerkt. 0 = aus.</summary>
        public float FinishDistance;
        /// <summary>Rennzeit (s) für die Zielzeiten der Gegner (vom Renn-Manager).</summary>
        public System.Func<float> Clock;

        public int Count => _opps.Count;
        /// <summary>Nach dem Zieleinlauf des Spielers weiterfahren (Rennen), statt stehen zu bleiben.</summary>
        public bool KeepDrivingAfterFinish;

        /// <summary>Weltpositionen der Gegnerautos (Minimap).</summary>
        public void WorldPositions(List<Vector3> into)
        {
            foreach (var o in _opps) if (o.Go != null && !o.Done) into.Add(o.Go.transform.position);
        }

        public List<Standing> Standings()
        {
            var l = new List<Standing>(_opps.Count);
            foreach (var o in _opps)
                l.Add(new Standing { Def = o.Def, Name = o.Def.Name, Car = o.Assets?.Car?.ModelName ?? "", Traveled = o.Traveled, FinishTime = o.FinishTime, Speed = o.Speed });
            return l;
        }

        readonly List<Opp> _opps = new List<Opp>();
        TDU2RaceManager _race;
        TDU2MapStreamer _streamer;
        TDU2VehicleManager _vehicles;
        RaceDefinition _def;
        Transform _holder;

        public void Setup(TDU2RaceManager race, TDU2MapStreamer streamer, TDU2VehicleManager vehicles, RaceDefinition def)
        {
            Clear();
            _race = race; _streamer = streamer; _vehicles = vehicles; _def = def;
            if (streamer == null || streamer.MapRoot == null || vehicles == null || vehicles.Database == null) return;
            // Autos wie das Spielerauto in Weltkoordinaten (nicht unter dem gespiegelten MapRoot, sonst doppelt gespiegelt)
            _holder = new GameObject("RaceOpponents").transform;
            string bnk = vehicles.BnkDir; var db = vehicles.Database;
            foreach (var d in def.Opponents)
            {
                if (!db.ById.TryGetValue(d.CarId, out var car) || CarDatabase.CarBank(bnk, car) == null)
                {
                    Debug.LogWarning("[TDU2] Prüfungsfahrzeug " + d.CarId + " nicht gefunden");
                    continue;
                }
                var o = new Opp { Def = d };
                o.Load = Task.Run(() => CarAssets.Load(bnk, db, car, 0, false));
                BuildPath(o);
                _opps.Add(o);
            }
        }

        void BuildPath(Opp o)
        {
            o.Traveled = o.Def.StartDistance;
            if (o.Def.PathOverride != null && o.Def.PathOverride.Count > 1)
            {
                o.Path.AddRange(o.Def.PathOverride);
                Densify(o.Path);
                if (Mathf.Abs(o.Def.LateralOffset) > 0.01f) Offset(o.Path, o.Def.LateralOffset);
                if (o.Def.LatAccel > 0f) o.VLim = CurveSpeeds(o.Path, o.Def.LatAccel, 7f);
                return;
            }
            o.Path.Add(o.Def.Position);
            var cps = _def.Checkpoints;
            // nächstes Tor vor dem Auto (in Fahrtrichtung, am nächsten)
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < cps.Count; i++)
            {
                var d = cps[i].Position - o.Def.Position; d.y = 0f;
                if (Vector3.Dot(d, o.Def.Forward) <= 0f) continue;
                float m = d.sqrMagnitude;
                if (m < bestD) { bestD = m; best = i; }
            }
            if (best < 0) { o.Path.Add(o.Def.Position + o.Def.Forward * 300f); return; }
            int laps = _def.IsCircuit ? _def.Laps : 1;
            for (int l = 0; l < laps; l++)
            {
                for (int i = (l == 0 ? best : 0); i < cps.Count; i++) o.Path.Add(cps[i].Position);
                if (_def.IsCircuit) o.Path.Add(cps[0].Position);
            }
            // etwas über das Ziel hinaus weiterfahren
            var last = o.Path[o.Path.Count - 1]; var prev = o.Path[Mathf.Max(0, o.Path.Count - 2)];
            var dir = last - prev; dir.y = 0f;
            if (dir.sqrMagnitude > 1e-3f) o.Path.Add(last + dir.normalized * 200f);
            // entlang der Straßen fahren (Verkehrsnetz), sonst geglättet durch die Tore
            var lanes = RaceRoadPath.FindLanes();
            int legs = 0;
            var road = lanes != null ? RaceRoadPath.Route(lanes, o.Path, out _, out legs) : null;
            if (road != null && legs > 0) { o.Path.Clear(); o.Path.AddRange(road); TDU2DebugLog.Event($"Gegner #{o.Def.CarId}: Straßenroute {legs} Abschnitte, {road.Count} Punkte"); }
            else { Smooth(o.Path); TDU2DebugLog.Event($"Gegner #{o.Def.CarId}: keine Straßenroute (Netz {(lanes != null ? "da" : "fehlt")}), Linie durch die Tore"); }
            Densify(o.Path);
            if (o.Def.LatAccel > 0f) o.VLim = CurveSpeeds(o.Path, o.Def.LatAccel, 7f);
        }

        /// <summary>Seitlich versetzen (rechts = positiv), am Anfang über 40 m von der Startposition auf den Versatz einblenden.</summary>
        static void Offset(List<Vector3> path, float off)
        {
            var src = new List<Vector3>(path);
            float s = 0f;
            for (int i = 0; i < src.Count; i++)
            {
                if (i > 0) s += Vector3.Distance(src[i - 1], src[i]);
                var d = src[Mathf.Min(src.Count - 1, i + 2)] - src[Mathf.Max(0, i - 2)]; d.y = 0f;
                if (d.sqrMagnitude < 1e-6f) continue;
                var right = new Vector3(d.z, 0f, -d.x).normalized;
                path[i] = src[i] + right * off * Mathf.Lerp(1f, 0.7f, Mathf.Clamp01(s / 40f));
            }
        }

        /// <summary>
        /// Höchsttempo je Punkt aus der Krümmung (v = sqrt(aQuer / k), Krümmung über ±6 m gemessen), danach rückwärts
        /// mit Bremsverzögerung <paramref name="brake"/> ausgeglichen, damit vor Kurven rechtzeitig gebremst wird.
        /// </summary>
        static float[] CurveSpeeds(List<Vector3> p, float latAcc, float brake)
        {
            int n = p.Count;
            var v = new float[n];
            for (int i = 0; i < n; i++)
            {
                int a = Mathf.Max(0, i - 6), b = Mathf.Min(n - 1, i + 6);
                var d1 = p[i] - p[a]; var d2 = p[b] - p[i]; d1.y = 0f; d2.y = 0f;
                float len = d1.magnitude + d2.magnitude;
                float ang = d1.sqrMagnitude > 1e-4f && d2.sqrMagnitude > 1e-4f ? Vector3.Angle(d1, d2) * Mathf.Deg2Rad : 0f;
                float k = len > 0.5f ? ang / len : 0f;   // 1/m
                v[i] = k > 1e-4f ? Mathf.Sqrt(latAcc / k) : 999f;
            }
            for (int i = n - 2; i >= 0; i--)
            {
                float ds = Vector3.Distance(p[i], p[i + 1]);
                v[i] = Mathf.Min(v[i], Mathf.Sqrt(v[i + 1] * v[i + 1] + 2f * brake * ds));
            }
            return v;
        }

        /// <summary>Punkte höchstens ~1 m auseinander (gleichmäßige Fahrt, Blick 4 Punkte voraus).</summary>
        static void Densify(List<Vector3> path)
        {
            if (path.Count < 2) return;
            var src = new List<Vector3>(path); path.Clear(); path.Add(src[0]);
            for (int i = 1; i < src.Count; i++)
            {
                float d = Vector3.Distance(src[i - 1], src[i]);
                int n = Mathf.Clamp(Mathf.CeilToInt(d), 1, 2000);
                for (int k = 1; k <= n; k++) path.Add(Vector3.Lerp(src[i - 1], src[i], k / (float)n));
            }
        }

        /// <summary>Route glätten: Catmull-Rom durch die Punkte, alle ~1 m ein Punkt (keine Knicke an den Toren).</summary>
        static void Smooth(List<Vector3> path)
        {
            if (path.Count < 3) return;
            var src = new List<Vector3>(path);
            path.Clear();
            for (int i = 0; i + 1 < src.Count; i++)
            {
                Vector3 P(int k) => src[Mathf.Clamp(k, 0, src.Count - 1)];
                Vector3 p0 = P(i - 1), p1 = P(i), p2 = P(i + 1), p3 = P(i + 2);
                int sub = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(p1, p2)), 1, 600);
                for (int k = 0; k < sub; k++)
                {
                    float u = k / (float)sub, u2 = u * u, u3 = u2 * u;
                    path.Add(0.5f * (2f * p1 + (-p0 + p2) * u + (2f * p0 - 5f * p1 + 4f * p2 - p3) * u2 + (-p0 + 3f * p1 - 3f * p2 + p3) * u3));
                }
            }
            path.Add(src[src.Count - 1]);
        }

        public void OnRaceStarted() { foreach (var o in _opps) if (o.Def.StartCheckpoint <= 1) o.Active = true; }

        public void OnCheckpoint(int cpIndex) { foreach (var o in _opps) if (!o.Active && cpIndex + 1 >= o.Def.StartCheckpoint) o.Active = true; }

        void Update()
        {
            if (_holder == null) return;
            foreach (var o in _opps)
            {
                if (o.Go == null && o.Load != null && o.Load.IsCompleted)
                {
                    var t = o.Load; o.Load = null;
                    if (t.IsFaulted) { Debug.LogWarning("[TDU2] Prüfungsfahrzeug: " + t.Exception?.GetBaseException().Message); continue; }
                    Spawn(o, t.Result);
                }
                if (o.Go == null || !o.Active || o.Done || Time.timeScale <= 0f || _race == null) continue;
                if (!_race.Running && !(KeepDrivingAfterFinish && _race.Finished)) continue;
                Drive(o, Time.deltaTime);
            }
        }

        void Spawn(Opp o, CarAssets a)
        {
            o.Assets = a;
            o.Go = new GameObject("Opponent " + a.Car.ModelName);
            o.Go.transform.SetParent(_holder, false);
            o.Model = CarModel.Build(a, o.Go.transform);
            var rb = o.Go.AddComponent<Rigidbody>(); rb.isKinematic = true;
            var b = o.Model.BodyBounds;
            var box = o.Go.AddComponent<BoxCollider>();
            box.center = b.center; box.size = new Vector3(b.size.x * 0.95f, Mathf.Max(0.5f, b.size.y * 0.9f), b.size.z * 0.97f);
            foreach (var t in o.Go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 2;
            // Unterkante (Reifen) bestimmen, damit das Auto auf dem Boden steht und nicht darin
            float minY = float.MaxValue;
            foreach (var r in o.Go.GetComponentsInChildren<Renderer>(true)) minY = Mathf.Min(minY, o.Go.transform.InverseTransformPoint(r.bounds.min).y);
            o.Lift = minY < float.MaxValue && minY < 0.5f ? -minY : 0f;
            var wheels = new List<(Transform, Quaternion, float, Transform, Quaternion, float, bool)>();
            foreach (var n in new[] { "FL", "FR", "RL", "RR" })
            {
                var st = o.Model.Find("STIRRUP_" + n); var disc = o.Model.Find("DISC_" + n);
                float sign = 1f;
                if (st != null)
                {
                    var fwd = o.Go.transform.forward; var upv = o.Go.transform.up;
                    var q0 = st.rotation; var b0 = st.localRotation;
                    st.localRotation = b0 * Quaternion.AngleAxis(10f, Vector3.up);
                    var v = (st.rotation * Quaternion.Inverse(q0)) * fwd;
                    st.localRotation = b0;
                    if (Vector3.SignedAngle(fwd, Vector3.ProjectOnPlane(v, upv), upv) < 0f) sign = -1f;
                }
                float radius = a.Rim != null ? a.Rim.TireRadius(n[0] == 'F') : 0.33f;
                wheels.Add((st, st != null ? st.localRotation : Quaternion.identity, sign, disc, disc != null ? disc.localRotation : Quaternion.identity, radius, n[0] == 'F'));
            }
            o.Wheels = wheels.ToArray();
            Place(o, o.Def.Position, o.Def.Forward, 0f);
        }

        void Drive(Opp o, float dt)
        {
            float target = Mathf.Max(3f, o.Def.MaxSpeed);
            if (o.VLim != null && o.Seg < o.VLim.Length) target = Mathf.Min(target, Mathf.Max(6f, o.VLim[Mathf.Min(o.VLim.Length - 1, o.Seg + 2)]));
            if (Avoidance) target = Avoid(o, target, dt);
            float acc = o.Speed < target ? Mathf.Max(0.5f, o.Def.Accel) * Mathf.Lerp(1f, 0.35f, Mathf.Clamp01(o.Speed / Mathf.Max(10f, o.Def.MaxSpeed))) : 9f;
            o.Speed = Mathf.MoveTowards(o.Speed, target, acc * dt);
            float dist = o.Speed * dt;
            o.Traveled += dist;
            if (o.FinishTime < 0f && FinishDistance > 0f && o.Traveled >= FinishDistance) o.FinishTime = Clock != null ? Clock() : Time.time;
            while (dist > 0f && o.Seg < o.Path.Count - 1)
            {
                var a = o.Path[o.Seg]; var b = o.Path[o.Seg + 1];
                float len = Vector3.Distance(new Vector3(a.x, 0, a.z), new Vector3(b.x, 0, b.z));
                if (len < 0.01f) { o.Seg++; o.SegT = 0f; continue; }
                float left = len * (1f - o.SegT);
                if (dist < left) { o.SegT += dist / len; dist = 0f; }
                else { dist -= left; o.Seg++; o.SegT = 0f; }
            }
            if (o.Seg >= o.Path.Count - 1) { o.Done = true; return; }
            var pos = Vector3.Lerp(o.Path[o.Seg], o.Path[o.Seg + 1], o.SegT);
            // seitlicher Versatz (Ausweichen/Überholen), sanft
            // Spurwechsel: Querbewegung nach Tempo (ca. 2 s für eine Spur, langsam fahrend nicht seitlich "schieben")
            float shiftRate = Mathf.Clamp(o.Speed * 0.12f, 0.4f, 1.8f);
            float shiftVel = Mathf.Clamp((o.ShiftTarget - o.Shift) * 2f, -shiftRate, shiftRate);
            o.Shift = Mathf.MoveTowards(o.Shift, o.ShiftTarget, Mathf.Abs(shiftVel) * dt);
            var sd = o.Path[o.Seg + 1] - o.Path[o.Seg]; sd.y = 0f;
            var sRight = sd.sqrMagnitude > 1e-4f ? new Vector3(sd.normalized.z, 0f, -sd.normalized.x) : Vector3.zero;
            pos += sRight * o.Shift;
            // Blickrichtung: Punkt auf der (gleich versetzten) Route in tempoabhängiger Entfernung (statt fester Punktzahl –
            // die Routenpunkte liegen unterschiedlich dicht, das gab ruckartige Drehungen in Kurven), plus Querbewegung
            var aheadPos = PointAhead(o, Mathf.Clamp(o.Speed * 0.45f, 5f, 14f));
            var fwd = aheadPos - pos; fwd.y = 0f;
            if (fwd.sqrMagnitude > 1e-4f && Mathf.Abs(shiftVel) > 0.01f && o.Speed > 1f)
                fwd = fwd.normalized + sRight * (shiftVel / o.Speed);
            if (fwd.sqrMagnitude < 1e-4f) fwd = o.Fwd;
            var cur = o.Fwd;
            if (cur.sqrMagnitude > 1e-4f) fwd = Vector3.Slerp(cur.normalized, fwd.normalized, Mathf.Clamp01(6f * dt));
            // Lenkwinkel aus der Kurve voraus (Inselkoordinaten gespiegelt -> Vorzeichen in Weltkoordinaten bestimmen)
            var look = PointAhead(o, Mathf.Clamp(o.Speed * 0.9f, 10f, 28f)) - pos; look.y = 0f;
            float steer = 0f;
            if (look.sqrMagnitude > 1f && fwd.sqrMagnitude > 1e-4f)
            {
                var root = _streamer.MapRoot;
                var wa = root.TransformVector(look); var wf0 = root.TransformVector(fwd); wa.y = 0f; wf0.y = 0f;
                steer = Mathf.Clamp(Vector3.SignedAngle(wf0, wa, Vector3.up) * 1.5f, -30f, 30f);
            }
            o.Steer = Mathf.Lerp(o.Steer, steer, Mathf.Clamp01(6f * dt));
            Place(o, pos, fwd, dt);
            if (o.Wheels != null)
            {
                o.Spin += o.Speed * dt;
                foreach (var w in o.Wheels)
                {
                    if (w.stirrup != null && w.front) w.stirrup.localRotation = w.sBase * Quaternion.AngleAxis(o.Steer * w.sign, Vector3.up);
                    if (w.disc != null) w.disc.localRotation = w.dBase * Quaternion.AngleAxis(o.Spin * Mathf.Rad2Deg / Mathf.Max(0.2f, w.radius), Vector3.right);
                }
            }
        }

        /// <summary>Punkt d Meter voraus auf der Route (ab der aktuellen Lage), um den aktuellen Spurversatz verschoben.</summary>
        static Vector3 PointAhead(Opp o, float d)
        {
            int seg = o.Seg; float t = o.SegT;
            while (seg < o.Path.Count - 1)
            {
                var a = o.Path[seg]; var b = o.Path[seg + 1];
                var ab = b - a; ab.y = 0f;
                float len = ab.magnitude;
                float left = len * (1f - t);
                if (len > 0.01f && d <= left)
                {
                    var p = Vector3.Lerp(a, b, t + d / len);
                    var n = ab / len;
                    return p + new Vector3(n.z, 0f, -n.x) * o.Shift;
                }
                d -= left; seg++; t = 0f;
            }
            return o.Path[o.Path.Count - 1];
        }

        /// <summary>
        /// Andere Autos (Gegner und Spieler) im Fahrschlauch voraus: Tempo auf deren Tempo begrenzen und seitlich zur freien
        /// Seite ausweichen (Überholen); ist der Weg wieder frei, langsam zurück auf die eigene Spur. Ohne das schieben sich die
        /// (kinematischen) Gegner ineinander bzw. in den Spieler.
        /// </summary>
        float Avoid(Opp o, float target, float dt)
        {
            if (o.Go == null) return target;
            var fwd = o.Fwd; fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) return target;
            fwd.Normalize();
            var right = new Vector3(fwd.z, 0f, -fwd.x);
            float look = 6f + o.Speed * 0.9f;
            bool blocked = false; float blockSpeed = float.MaxValue, blockLat = 0f, blockAlong = float.MaxValue;
            bool leftBusy = false, rightBusy = false;
            void Check(Vector3 p, float speed)
            {
                var rel = p - o.Island; rel.y = 0f;
                float along = Vector3.Dot(rel, fwd), lat = Vector3.Dot(rel, right);
                if (along > -12f && along < look * 1.3f)
                {
                    if (lat > 1.0f && lat < 5.8f) rightBusy = true;
                    if (lat < -1.0f && lat > -5.8f) leftBusy = true;
                }
                if (along <= 0.5f || along > look || Mathf.Abs(lat) > 2.4f) return;
                if (along < blockAlong) { blockAlong = along; blockSpeed = speed; blockLat = lat; }
                blocked = true;
            }
            foreach (var x in _opps) if (x != o && x.Go != null && !x.Done) Check(x.Island, x.Speed);
            var me = _vehicles != null ? _vehicles.Current : null;
            if (me != null && _streamer != null && _streamer.MapRoot != null)
                Check(_streamer.MapRoot.InverseTransformPoint(me.transform.position), Mathf.Abs(me.SpeedKmh) / 3.6f);
            if (blocked)
            {
                // Abstand halten: je näher, desto langsamer als der Vordermann
                float gap = Mathf.Max(0f, blockAlong - 6f);
                float limit = blockSpeed + gap * 0.5f - (blockAlong < 7f ? 4f : 0f);
                float desired = target;
                target = Mathf.Min(target, Mathf.Max(0f, limit));
                // Überholen nur, wenn der Vordermann deutlich langsamer ist: zur freien Seite, höchstens ~1,5 Spuren daneben
                bool curve = o.VLim != null && o.Seg + 6 < o.VLim.Length && o.VLim[o.Seg + 6] < o.Def.MaxSpeed * 0.8f;
                float side = desired > blockSpeed + 2f && !curve ? (blockLat >= 0f ? -1f : 1f) : 0f;   // in Kurven nicht überholen
                if (side > 0f && rightBusy) side = leftBusy ? 0f : -1f;
                else if (side < 0f && leftBusy) side = rightBusy ? 0f : 1f;
                // Querrichtung: "right" (Inselkoordinaten) entspricht +Shift

                if (side != 0f)
                {
                    float total = o.Def.LateralOffset + o.ShiftTarget;
                    float want = Mathf.Clamp(total + side * 3.2f, -5f, 5f) - o.Def.LateralOffset;
                    if (Mathf.Abs(want - o.ShiftTarget) > 0.5f && Mathf.Abs(o.Shift - o.ShiftTarget) < 0.3f) o.ShiftTarget = want;
                }
                o.ClearSince = Time.time;
            }
            else if (Mathf.Abs(o.ShiftTarget) > 0.01f && Time.time - o.ClearSince > 2.5f)
            {
                // frei: zurück auf die eigene Spur, wenn dort niemand fährt
                bool busy = o.ShiftTarget > 0f ? leftBusy : rightBusy;
                if (!busy) o.ShiftTarget = 0f;
            }
            return target;
        }

        // ------------------------------------------------------------------ Namen über den Gegnern
        GUIStyle _tag, _tagShadow;

        void OnGUI()
        {
            if (!NameTags || Event.current.type != EventType.Repaint || _race == null || !_race.Running || Time.timeScale <= 0f) return;
            var cam = Camera.main;
            if (cam == null || TDU2BigMap.IsOpen) return;
            if (_tag == null)
            {
                _tag = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerCenter, fontStyle = FontStyle.Bold, richText = false };
                _tag.normal.textColor = Color.white;
                _tagShadow = new GUIStyle(_tag);
                _tagShadow.normal.textColor = new Color(0f, 0f, 0f, 0.75f);
            }
            foreach (var o in _opps)
            {
                if (o.Go == null || o.Model == null || string.IsNullOrEmpty(o.Def.Name)) continue;
                var top = o.Go.transform.position + o.Go.transform.up * (o.Model.BodyBounds.max.y + 0.6f);
                var sp = cam.WorldToScreenPoint(top);
                if (sp.z <= 0.5f || sp.z > 250f) continue;
                int size = Mathf.RoundToInt(Mathf.Clamp(17f - sp.z * 0.04f, 10f, 15f) * Screen.height / 1080f);
                _tag.fontSize = size; _tagShadow.fontSize = size;
                float alpha = Mathf.Clamp01((250f - sp.z) / 80f);
                var rect = new Rect(sp.x - 200f, Screen.height - sp.y - size * 1.6f, 400f, size * 1.6f);
                var c = GUI.color; GUI.color = new Color(1f, 1f, 1f, alpha);
                GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), o.Def.Name, _tagShadow);
                GUI.Label(rect, o.Def.Name, _tag);
                GUI.color = c;
            }
        }

        /// <summary>Auf den Boden setzen: Höhe und Neigung aus zwei Strahlen (vorne/hinten), Höhe leicht gefiltert.</summary>
        void Place(Opp o, Vector3 island, Vector3 fwd, float dt)
        {
            var root = _streamer.MapRoot;
            if (fwd.sqrMagnitude < 1e-6f) fwd = Vector3.forward;
            o.Fwd = fwd.normalized;
            var wf = root.TransformVector(o.Fwd); wf.y = 0f;   // TransformVector: berücksichtigt die Spiegelung (Skala -1) des MapRoot
            if (wf.sqrMagnitude < 1e-6f) wf = Vector3.forward;
            wf.Normalize();
            var w = root.TransformPoint(island);
            float half = o.Model != null ? Mathf.Max(1f, o.Model.BodyBounds.extents.z * 0.8f) : 1.8f;
            // Erwartete Höhe: letzte Höhe bzw. Höhe der Route – Boden nur nahe dieser Höhe suchen (sonst springt das Auto unter
            // Brücken auf die Überführung bzw. fällt auf die Straße darunter)
            float refY = o.HasY ? o.Y : w.y;
            if (!o.HasY && Physics.Raycast(new Vector3(w.x, w.y + 8f, w.z), Vector3.down, out var first, 40f, ~(1 << 2), QueryTriggerInteraction.Ignore)) refY = first.point.y;
            bool hc = Ground(w, refY, out var gc), hf = Ground(w + wf * half, refY, out var gf), hb = Ground(w - wf * half, refY, out var gb);
            float y;
            if (hf && hb) y = (gf.y + gb.y) * 0.5f;          // Mitte zwischen Vorder- und Hinterachse: am Hang nicht im Boden
            else if (hc) y = gc.y;
            else y = o.HasY ? o.Y : w.y;
            if (o.HasY && dt > 0f) y = Mathf.Lerp(o.Y, y, Mathf.Clamp01(25f * dt));   // nur leicht glätten (keine Verzögerung am Berg)
            o.Y = y; o.HasY = true;
            float pitch = 0f;
            if (hf && hb && Mathf.Abs(gf.y - gb.y) < half * 1.2f)
                pitch = Mathf.Clamp(-Mathf.Atan2(gf.y - gb.y, 2f * half) * Mathf.Rad2Deg, -25f, 25f);
            o.Pitch = o.HasPitch ? Mathf.Lerp(o.Pitch, pitch, Mathf.Clamp01(10f * Mathf.Max(dt, 0.016f))) : pitch; o.HasPitch = true;
            var rot = Quaternion.LookRotation(wf, Vector3.up) * Quaternion.Euler(o.Pitch, 0f, 0f);
            var wp = new Vector3(w.x, y + o.Lift, w.z);
            o.Island = root.InverseTransformPoint(wp);
            o.Go.transform.SetPositionAndRotation(wp, rot);
        }

        /// <summary>Boden (statische Welt) unter w, der am nächsten an der erwarteten Höhe refY liegt (±4 m, sonst weiter suchen).</summary>
        static bool Ground(Vector3 w, float refY, out Vector3 point)
        {
            // nur statische Welt (keine Autos mit Rigidbody, sonst "klettert" der Gegner auf den Spieler)
            var hits = Physics.RaycastAll(new Vector3(w.x, refY + 30f, w.z), Vector3.down, 80f, ~(1 << 2), QueryTriggerInteraction.Ignore);
            float best = float.MaxValue; point = w; bool any = false;
            foreach (var h in hits)
            {
                if (h.rigidbody != null || h.normal.y < 0.5f) continue;   // Wände/Geländer ignorieren
                float d = Mathf.Abs(h.point.y - refY);
                if (h.point.y > refY + 4f) d += 50f;                       // deutlich darüber (Brücke): nur als Notlösung
                if (d < best) { best = d; point = h.point; any = true; }
            }
            return any;
        }

        public void Clear()
        {
            foreach (var o in _opps)
            {
                o.Model?.Destroy();
                o.Assets?.DestroyTextures();
                if (o.Go != null) UnityUtil.SafeDestroy(o.Go);
            }
            _opps.Clear();
            if (_holder != null) UnityUtil.SafeDestroy(_holder.gameObject);
            _holder = null;
        }

        void OnDestroy() => Clear();
    }
}
