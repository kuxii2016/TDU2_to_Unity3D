using System;
using System.Collections.Generic;
using TDU2.Flash;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>Anklickbare Teile eines Autos (Händler/Garage: Türen und Fenster, im Auto auch Lenkrad = Hupe).</summary>
    public enum CarPart { None, DoorL, DoorR, WindowL, WindowR, Steering }

    /// <summary>
    /// Zubehör eines Autos wie im Spiel:
    ///   * HUD-Menü "Accessories" (hud.vmf /HUD/ig_menu, 4 Symbole): 1 "hood" Verdeck (HARDTOP-/ROOF-Teile), 2 "clir" Blinker rechts,
    ///     3 "wind" Fenster, 4 "clil" Blinker links. Motorräder: Gruß/Hupen statt Verdeck/Fenster (hier ohne Wirkung).
    ///   * Türen und Fenster nach den Original-Knoten der Karosserie (.3dd): DOOR_L/DOOR_R (unter DOOR_x_COL) haben ihren Ursprung am
    ///     Scharnier vorne und schwenken um die Hochachse nach außen; Kinder der Tür sind Scheibe WND_x1, Außenspiegel RV_x_HR und
    ///     der Platzhalter der Türverkleidung (D_INT_HR_x). WND_x2 ist ein leerer Platzhalter = Lage der geöffneten Scheibe
    ///     (abgesenkt/gekippt) – die Scheibe fährt zwischen WND_x1 und WND_x2. Die Türverkleidung des Interieurs (INT_HR_L/R der
    ///     Cockpit-Bank) schwenkt mit der Tür. Ohne WND_x2 fährt die Scheibe um ihre Höhe nach unten (ältere GLASS_LEFT/RIGHT).
    ///   * Klänge aus CarVSTConfig: DoorOpen/DoorClose, WindowStart + WindowOpen/WindowClose + WindowStop.
    ///   * Zustand als Bits (<see cref="Bits"/>) für die Online-Synchronisation: 1 Tür links, 2 Tür rechts, 4 Fenster links,
    ///     8 Fenster rechts, 16 Verdeck offen.
    /// </summary>
    public sealed class TDU2Accessories
    {
        public const int BitDoorL = 1, BitDoorR = 2, BitWinL = 4, BitWinR = 8, BitRoof = 16;
        /// <summary>Öffnungswinkel der Türen (Grad).</summary>
        public static float DoorAngle = 62f;

        public readonly CarModel Model;
        public TDU2VehicleLights Lights;
        /// <summary>Klang (Tür/Fenster); null = stumm.</summary>
        public TDU2VehicleAudio Audio;

        sealed class Door
        {
            public Transform T; public bool Left;
            public Vector3 Pos0, PivotL, AxisL; public Quaternion Rot0;
            public float Sign = 1f, Cur, Target;
            public Renderer[] Rs;
        }
        sealed class Window
        {
            public Transform T; public bool Left;
            public Vector3 Pos0, Pos1; public Quaternion Rot0, Rot1;
            public float Cur, Target; public bool Moving;
            public Renderer[] Rs;
        }
        readonly List<Door> _doors = new List<Door>();
        readonly List<Window> _windows = new List<Window>();
        readonly List<Renderer> _roof = new List<Renderer>();
        // GLASS_I der Cockpit-Bank (Innenseite aller Scheiben, ein Mesh): die Dreiecke im Bereich der geschlossenen Türen werden
        // als eigene Teile abgetrennt und nur ausgeblendet, solange die Tür bzw. das Fenster der Seite offen ist
        sealed class GlassPart { public bool Left; public Renderer R; }
        readonly List<GlassPart> _sideGlass = new List<GlassPart>();
        readonly bool[] _sideGlassShown = { true, true };
        int _frame = -1;

        public bool HasWindows => _windows.Count > 0;
        public bool HasDoors => _doors.Count > 0;
        public bool HasRoof => _roof.Count > 0;
        public bool HasTurnSignals => Lights != null;
        public bool WindowsOpen { get { foreach (var w in _windows) if (w.Target > 0.5f) return true; return false; } }
        public bool DoorsOpen { get { foreach (var d in _doors) if (d.Target > 0.5f) return true; return false; } }
        public bool RoofOpen { get; private set; }

        TDU2Accessories(CarModel model, TDU2VehicleLights lights)
        {
            Model = model; Lights = lights;
            if (model == null || model.Visual == null) return;
            var root = model.Visual.transform.parent != null ? model.Visual.transform.parent : model.Visual.transform;
            foreach (var side in new[] { "L", "R" })
            {
                var door = model.Find("DOOR_" + side);
                // Türverkleidung des Interieurs (Cockpit-Bank, Lage in Fahrzeugkoordinaten) schwenkt mit der Tür
                var panel = model.Find("INT_HR_" + side);
                if (door != null && panel != null && model.CockpitRoot != null && panel.IsChildOf(model.CockpitRoot) && !panel.IsChildOf(door))
                    panel.SetParent(door, true);
                if (door != null) AddDoor(root, door, side == "L");
                // Scheibe: WND_x1 fährt in die Lage des Platzhalters WND_x2
                var w1 = model.Find("WND_" + side + "1");
                var w2 = model.Find("WND_" + side + "2");
                if (w1 != null) AddWindow(root, w1, w2, side == "L");
            }
            // Innenseite der Scheiben (GLASS_I, ein Stück für alle Fenster): bleibt sonst als durchsichtige Fläche an der Stelle
            // der geschlossenen Tür stehen -> ausblenden, solange eine Tür oder ein Fenster offen ist
            var gi = model.Find("GLASS_I");
            if (gi != null) { try { SplitInnerGlass(root, gi); } catch (Exception ex) { Debug.LogWarning("[TDU2] GLASS_I teilen: " + ex.Message); } }
            if (_windows.Count == 0)
                foreach (var n in new[] { "GLASS_LEFT", "GLASS_RIGHT" })
                {
                    var t = model.Find(n);
                    if (t != null) AddWindow(root, t, null, n == "GLASS_LEFT");
                }
            foreach (var kv in model.Nodes)
            {
                string n = kv.Key.ToUpperInvariant();
                if (kv.Value != null && (n.Contains("HARDTOP") || n.Contains("ROOF")))
                    foreach (var r in kv.Value.GetComponentsInChildren<Renderer>(true)) if (r.enabled && !_roof.Contains(r)) _roof.Add(r);
            }
        }

        static bool GetBounds(Renderer[] rs, out Bounds b)
        {
            b = default; bool any = false;
            foreach (var r in rs) { if (r == null) continue; if (!any) { b = r.bounds; any = true; } else b.Encapsulate(r.bounds); }
            return any;
        }

        void AddDoor(Transform root, Transform t, bool left)
        {
            if (t.parent == null) return;
            var rs = t.GetComponentsInChildren<Renderer>(true);
            if (!GetBounds(rs, out var b)) return;
            var d = new Door { T = t, Left = left, Pos0 = t.localPosition, Rot0 = t.localRotation, Rs = rs };
            // Scharnier: Ursprung der Tür (Original), sonst Vorderkante der Tür
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                float z = root.InverseTransformPoint(c).z; minZ = Mathf.Min(minZ, z); maxZ = Mathf.Max(maxZ, z);
            }
            var o = root.InverseTransformPoint(t.position);
            d.PivotL = t.localPosition;
            if (maxZ - o.z > 0.35f * Mathf.Max(0.1f, maxZ - minZ))
                d.PivotL = t.parent.InverseTransformPoint(root.TransformPoint(new Vector3(o.x, o.y, maxZ - 0.04f)));
            d.AxisL = t.parent.InverseTransformVector(root.up).normalized;
            if (d.AxisL.sqrMagnitude < 0.5f) d.AxisL = Vector3.up;
            // Richtung: die Tür muss nach außen schwenken (Mitte der Tür entfernt sich von der Fahrzeugmitte)
            var cLocal = t.InverseTransformPoint(b.center);
            float x0 = Mathf.Abs(root.InverseTransformPoint(b.center).x);
            Pose(d, 30f);
            float x1 = Mathf.Abs(root.InverseTransformPoint(t.TransformPoint(cLocal)).x);
            if (x1 < x0) d.Sign = -1f;
            Pose(d, 0f);
            _doors.Add(d);
        }

        static void Pose(Door d, float angle)
        {
            var q = Quaternion.AngleAxis(angle * d.Sign, d.AxisL);
            d.T.localPosition = d.PivotL + q * (d.Pos0 - d.PivotL);
            d.T.localRotation = q * d.Rot0;
        }

        void AddWindow(Transform root, Transform t, Transform openDummy, bool left)
        {
            if (t.parent == null) return;
            var rs = t.GetComponentsInChildren<Renderer>(true);
            if (rs.Length == 0 || !GetBounds(rs, out var b)) return;
            var w = new Window { T = t, Left = left, Pos0 = t.localPosition, Rot0 = t.localRotation, Rs = rs };
            if (openDummy != null && openDummy.parent == t.parent && openDummy.GetComponentsInChildren<Renderer>(true).Length == 0)
            {
                w.Pos1 = openDummy.localPosition; w.Rot1 = openDummy.localRotation;
            }
            else
            {
                // Scheibe so weit absenken, wie sie hoch ist (in die Tür)
                float drop = Mathf.Clamp(b.size.y * 0.92f, 0.05f, 0.6f);
                w.Pos1 = w.Pos0 + t.parent.InverseTransformVector(-root.up * drop);
                w.Rot1 = w.Rot0;
            }
            _windows.Add(w);
        }

        static readonly Dictionary<CarModel, TDU2Accessories> _byModel = new Dictionary<CarModel, TDU2Accessories>();

        /// <summary>Zubehör eines Modells (einmal je Modell angelegt).</summary>
        public static TDU2Accessories For(CarModel model, TDU2VehicleLights lights)
        {
            if (model == null) return null;
            if (_byModel.Count > 32) Prune();
            if (!_byModel.TryGetValue(model, out var a)) _byModel[model] = a = new TDU2Accessories(model, lights);
            if (lights != null) a.Lights = lights;
            return a;
        }

        /// <summary>Zubehör nur, wenn es schon angelegt ist (kein Neuaufbau).</summary>
        public static TDU2Accessories Existing(CarModel model) => model != null && _byModel.TryGetValue(model, out var a) ? a : null;

        static void Prune()
        {
            var dead = new List<CarModel>();
            foreach (var k in _byModel.Keys) if (k.Visual == null) dead.Add(k);
            foreach (var k in dead) _byModel.Remove(k);
        }

        public static void Forget(CarModel model) { if (model != null) _byModel.Remove(model); }

        /// <summary>Alle angelegten Zubehörteile animieren (Händler-/Garagenautos, Mitspieler); jedes Teil höchstens einmal je Bild.</summary>
        public static void UpdateAll(float dt)
        {
            bool dead = false;
            foreach (var kv in _byModel) { if (kv.Key.Visual == null) { dead = true; continue; } kv.Value.Update(dt); }
            if (dead) Prune();
        }

        void Play(string name) { if (Audio != null) Audio.Play(name); }

        // ------------------------------------------------------------------ Türen
        public bool DoorOpen(bool left) { foreach (var d in _doors) if (d.Left == left) return d.Target > 0.5f; return false; }
        public bool HasDoor(bool left) { foreach (var d in _doors) if (d.Left == left) return true; return false; }

        public void SetDoor(bool left, bool open)
        {
            foreach (var d in _doors)
            {
                if (d.Left != left) continue;
                float t = open ? 1f : 0f;
                if (Mathf.Approximately(d.Target, t)) continue;
                if (open && d.Cur < 0.05f) Play("DoorOpen");
                d.Target = t;
            }
        }

        public void ToggleDoor(bool left) => SetDoor(left, !DoorOpen(left));

        /// <summary>Beide Türen: ist eine offen, gehen alle zu, sonst gehen alle auf.</summary>
        public void ToggleDoors() { bool open = !DoorsOpen; SetDoor(true, open); SetDoor(false, open); }

        // ------------------------------------------------------------------ Fenster
        public bool WindowOpen(bool left) { foreach (var w in _windows) if (w.Left == left) return w.Target > 0.5f; return false; }

        public void SetWindow(bool left, bool open)
        {
            foreach (var w in _windows)
            {
                if (w.Left != left) continue;
                float t = open ? 1f : 0f;
                if (Mathf.Approximately(w.Target, t)) continue;
                if (!w.Moving) { Play("WindowStart"); Play(open ? "WindowOpen" : "WindowClose"); }
                w.Target = t; w.Moving = true;
            }
        }

        public void ToggleWindow(bool left) => SetWindow(left, !WindowOpen(left));

        /// <summary>Alle Fenster (HUD-Menü "wind"): ist eines offen, gehen alle zu, sonst gehen alle auf.</summary>
        public void ToggleWindows() { if (!HasWindows) return; bool open = !WindowsOpen; SetWindow(true, open); SetWindow(false, open); }

        // ------------------------------------------------------------------ Verdeck, Blinker
        public void ToggleRoof() => SetRoof(!RoofOpen);

        void SetRoof(bool open)
        {
            if (!HasRoof || RoofOpen == open) return;
            RoofOpen = open;
            foreach (var r in _roof) if (r != null) r.enabled = !RoofOpen;
        }

        public void ToggleIndicator(bool left)
        {
            if (Lights == null) return;
            if (left) { Lights.IndicatorLeft = !Lights.IndicatorLeft; Lights.IndicatorRight = false; }
            else { Lights.IndicatorRight = !Lights.IndicatorRight; Lights.IndicatorLeft = false; }
            Lights.Hazard = false;
        }

        // ------------------------------------------------------------------ Zustand (Online)
        /// <summary>Gewünschter Zustand als Bits (Türen/Fenster/Verdeck).</summary>
        public int Bits
        {
            get
            {
                int b = 0;
                if (DoorOpen(true)) b |= BitDoorL;
                if (DoorOpen(false)) b |= BitDoorR;
                if (WindowOpen(true)) b |= BitWinL;
                if (WindowOpen(false)) b |= BitWinR;
                if (RoofOpen) b |= BitRoof;
                return b;
            }
        }

        /// <summary>Zustand eines Mitspielers übernehmen (Türen/Fenster fahren mit Klang, Verdeck sofort).</summary>
        public void ApplyBits(int bits)
        {
            SetDoor(true, (bits & BitDoorL) != 0);
            SetDoor(false, (bits & BitDoorR) != 0);
            SetWindow(true, (bits & BitWinL) != 0);
            SetWindow(false, (bits & BitWinR) != 0);
            SetRoof((bits & BitRoof) != 0);
        }

        // ------------------------------------------------------------------ Anvisieren
        /// <summary>Teil unter dem Strahl (Türen, Fenster, mit <paramref name="steering"/> auch das Lenkrad); None = keins.</summary>
        public CarPart PartAt(Ray ray, float maxDist, bool steering = false)
        {
            CarPart best = CarPart.None; float bd = maxDist;
            foreach (var d in _doors)
                if (GetBounds(d.Rs, out var b) && b.IntersectRay(ray, out float t) && t < bd) { bd = t; best = d.Left ? CarPart.DoorL : CarPart.DoorR; }
            // Scheiben liegen in der Tür: trifft der Strahl die Scheibe kurz hinter der Türkante, gilt die Scheibe
            foreach (var w in _windows)
                if (GetBounds(w.Rs, out var b) && b.IntersectRay(ray, out float t) && t < Mathf.Min(maxDist, bd + 0.3f)) { bd = Mathf.Min(bd, t); best = w.Left ? CarPart.WindowL : CarPart.WindowR; }
            if (steering && Model?.SteeringWheel != null)
            {
                var rs = Model.SteeringWheel.GetComponentsInChildren<Renderer>(true);
                if (GetBounds(rs, out var b) && b.IntersectRay(ray, out float t) && t < bd) { bd = t; best = CarPart.Steering; }
            }
            return best;
        }

        /// <summary>Teil umschalten (Klick); false = kein schaltbares Teil.</summary>
        public bool Toggle(CarPart p)
        {
            switch (p)
            {
                case CarPart.DoorL: ToggleDoor(true); return true;
                case CarPart.DoorR: ToggleDoor(false); return true;
                case CarPart.WindowL: ToggleWindow(true); return true;
                case CarPart.WindowR: ToggleWindow(false); return true;
            }
            return false;
        }

        /// <summary>Hinweistext für ein Teil (Cursor-Leiste des Films).</summary>
        public string Hint(CarPart p)
        {
            switch (p)
            {
                case CarPart.DoorL: return DoorOpen(true) ? "Tür schließen" : "Tür öffnen";
                case CarPart.DoorR: return DoorOpen(false) ? "Tür schließen" : "Tür öffnen";
                case CarPart.WindowL: return WindowOpen(true) ? "Fenster schließen" : "Fenster öffnen";
                case CarPart.WindowR: return WindowOpen(false) ? "Fenster schließen" : "Fenster öffnen";
                case CarPart.Steering: return "Hupe";
            }
            return "";
        }

        // ------------------------------------------------------------------ Jedes Bild
        /// <summary>Türen (ca. 1 s auf, 0,8 s zu) und Fenster (ca. 2 s) fahren; höchstens einmal je Bild.</summary>
        public void Update(float dt)
        {
            if (_frame == Time.frameCount) return;
            _frame = Time.frameCount;
            foreach (var d in _doors)
            {
                if (d.T == null || Mathf.Approximately(d.Cur, d.Target)) continue;
                bool closing = d.Target < d.Cur;
                d.Cur = Mathf.MoveTowards(d.Cur, d.Target, dt / (closing ? 0.8f : 1.0f));
                // auf: weich auslaufen; zu: zum Ende hin schneller (Tür fällt ins Schloss)
                float e = closing ? d.Cur * d.Cur * (2f - d.Cur) : 1f - (1f - d.Cur) * (1f - d.Cur);
                Pose(d, e * DoorAngle);
                if (closing && d.Cur <= 0f) Play("DoorClose");
            }
            foreach (var w in _windows)
            {
                if (w.T == null || !w.Moving) continue;
                w.Cur = Mathf.MoveTowards(w.Cur, w.Target, dt / 2f);
                float e = Mathf.SmoothStep(0f, 1f, w.Cur);
                w.T.localPosition = Vector3.Lerp(w.Pos0, w.Pos1, e);
                w.T.localRotation = Quaternion.Slerp(w.Rot0, w.Rot1, e);
                if (Mathf.Approximately(w.Cur, w.Target)) { w.Moving = false; Play("WindowStop"); }
            }
            UpdateInnerGlass();
        }

        /// <summary>GLASS_I in Rest und Türbereiche (links/rechts) teilen (Dreiecksmitte innerhalb der Tür-Box der geschlossenen Tür).</summary>
        void SplitInnerGlass(Transform root, Transform gi)
        {
            var boxes = new List<(bool left, Bounds b)>();
            foreach (var d in _doors)
            {
                if (!GetBounds(d.Rs, out var wb)) continue;
                var rb = new Bounds(root.InverseTransformPoint(wb.center), Vector3.zero);
                for (int i = 0; i < 8; i++)
                    rb.Encapsulate(root.InverseTransformPoint(wb.center + Vector3.Scale(wb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1))));
                rb.Expand(0.1f);
                boxes.Add((d.Left, rb));
            }
            if (boxes.Count == 0) return;
            foreach (var mf in gi.GetComponentsInChildren<MeshFilter>(true))
            {
                var mr = mf.GetComponent<MeshRenderer>(); var mesh = mf.sharedMesh;
                if (mr == null || mesh == null || !mesh.isReadable) continue;
                var v = mesh.vertices;
                var m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                int subs = mesh.subMeshCount;
                var rest = new List<int>[subs]; var left = new List<int>[subs]; var right = new List<int>[subs];
                int nl = 0, nr = 0;
                for (int s = 0; s < subs; s++)
                {
                    rest[s] = new List<int>(); left[s] = new List<int>(); right[s] = new List<int>();
                    var t = mesh.GetTriangles(s);
                    for (int i = 0; i + 2 < t.Length; i += 3)
                    {
                        var c = m.MultiplyPoint3x4((v[t[i]] + v[t[i + 1]] + v[t[i + 2]]) / 3f);
                        int side = 0;
                        foreach (var (l, b) in boxes) if (b.Contains(c)) { side = l ? 1 : 2; break; }
                        var dst = side == 1 ? left[s] : side == 2 ? right[s] : rest[s];
                        dst.Add(t[i]); dst.Add(t[i + 1]); dst.Add(t[i + 2]);
                        if (side == 1) nl++; else if (side == 2) nr++;
                    }
                }
                if (nl == 0 && nr == 0) continue;
                Mesh Part(List<int>[] tris, string name)
                {
                    var pm = UnityEngine.Object.Instantiate(mesh);
                    pm.name = mesh.name + " " + name;
                    for (int s = 0; s < subs; s++) pm.SetTriangles(tris[s], s);
                    Model.Owned.Add(pm);
                    return pm;
                }
                mf.sharedMesh = Part(rest, "(Rest)");
                foreach (var (isLeft, tris, n) in new[] { (true, left, nl), (false, right, nr) })
                {
                    if (n == 0) continue;
                    var go = new GameObject("GLASS_I " + (isLeft ? "Tür links" : "Tür rechts"));
                    go.layer = mf.gameObject.layer;
                    go.transform.SetParent(mf.transform, false);
                    go.AddComponent<MeshFilter>().sharedMesh = Part(tris, isLeft ? "(Tür links)" : "(Tür rechts)");
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterials = mr.sharedMaterials;
                    r.shadowCastingMode = mr.shadowCastingMode; r.receiveShadows = mr.receiveShadows;
                    r.enabled = mr.enabled;
                    _sideGlass.Add(new GlassPart { Left = isLeft, R = r });
                }
            }
        }

        void UpdateInnerGlass()
        {
            if (_sideGlass.Count == 0) return;
            for (int side = 0; side < 2; side++)
            {
                bool left = side == 0, open = false;
                foreach (var d in _doors) if (d.Left == left && (d.Cur > 0.001f || d.Target > 0.5f)) open = true;
                foreach (var w in _windows) if (w.Left == left && (w.Cur > 0.001f || w.Target > 0.5f)) open = true;
                if (_sideGlassShown[side] == !open) continue;
                _sideGlassShown[side] = !open;
                foreach (var g in _sideGlass) if (g.Left == left && g.R != null) g.R.enabled = !open;
            }
        }

        /// <summary>Türen und Fenster zufahren (mit Klang), Blinker aus.</summary>
        public void CloseAll()
        {
            SetDoor(true, false); SetDoor(false, false);
            SetWindow(true, false); SetWindow(false, false);
            if (Lights != null) { Lights.IndicatorLeft = Lights.IndicatorRight = false; }
        }

        /// <summary>Alles sofort wieder zu (Verdeck, Türen, Fenster, Blinker).</summary>
        public void Reset()
        {
            foreach (var d in _doors) { d.Cur = d.Target = 0f; if (d.T != null) Pose(d, 0f); }
            foreach (var w in _windows) { w.Cur = w.Target = 0f; w.Moving = false; if (w.T != null) { w.T.localPosition = w.Pos0; w.T.localRotation = w.Rot0; } }
            UpdateInnerGlass();
            SetRoof(false);
            if (Lights != null) { Lights.IndicatorLeft = Lights.IndicatorRight = false; }
        }
    }

    /// <summary>
    /// Das Original-Zubehörmenü im HUD-Film (/HUD/ig_menu): öffnen (Label "open" ruft SetIcons, liest _root.HasHood,
    /// HasWindows, HasTurnSignals, IsBike), Auswahl über ValidIGM(&lt;Symbol&gt;) – das Menü bestätigt und schließt sich selbst.
    /// Tasten: Pfeile/WASD oder 1–4 (oben Verdeck, rechts Blinker rechts, unten Fenster, links Blinker links), Zubehör-Taste/ESC schließt.
    /// </summary>
    public static class TDU2AccessoryMenu
    {
        const string Menu = "/HUD/ig_menu";
        static TDU2Accessories _acc;
        static float _openedAt;
        static bool _hudShown;
        public static bool IsOpen { get; private set; }
        static int _closedFrame = -10;
        /// <summary>ESC gehört gerade dem Zubehörmenü (offen bzw. in diesem/letztem Bild per ESC geschlossen) – Pausemenü nicht öffnen.</summary>
        public static bool OwnsEscape => IsOpen || Time.frameCount - _closedFrame <= 1;

        static VmfPlayer Hud => TDU2UIManager.Hud;

        public static bool Open(TDU2Accessories acc, bool bike = false)
        {
            var h = Hud;
            if (h == null || acc == null || h.Find(Menu) == null) return false;
            _acc = acc; IsOpen = true; _openedAt = Time.unscaledTime;
            h.SetValue("/:IsBike", bike);
            h.SetValue("/:HasHood", acc.HasRoof);
            h.SetValue("/:HasWindows", acc.HasWindows);
            h.SetValue("/:HasTurnSignals", acc.HasTurnSignals);
            var title = h.GetVariable("/:m_Accessories");
            if (string.IsNullOrEmpty(title) || title.StartsWith("_") || title.StartsWith("m_")) h.SetVariable("/:m_Accessories", "Zubehör");
            _hudShown = h.Find(Menu).Visible;
            h.SetVisible(Menu, true);
            h.GotoLabel(Menu, "open", true);
            return true;
        }

        public static void Close(bool animate = true)
        {
            if (!IsOpen) return;
            IsOpen = false; _closedFrame = Time.frameCount;
            var h = Hud;
            if (h != null && h.Find(Menu) != null)
            {
                if (animate && h.Label(Menu) != "default") h.GotoLabel(Menu, "close", true);
                else { h.GotoLabel(Menu, "default", false); if (!_hudShown) h.SetVisible(Menu, false); }
            }
            _acc = null;
        }

        /// <summary>Symbol wählen: Menü bestätigt (Animation) und schließt; Wirkung am Auto.</summary>
        static void Choose(int slot)
        {
            var h = Hud; var a = _acc;
            if (h == null || a == null) return;
            string msg = h.GetVariable(Menu + ":ig_msg_" + slot);
            if (string.IsNullOrEmpty(msg)) msg = slot == 1 ? "hood" : slot == 2 ? "clir" : slot == 3 ? "wind" : "clil";
            bool locked = (msg == "hood" && !a.HasRoof) || (msg == "wind" && !a.HasWindows) || ((msg == "clir" || msg == "clil") && !a.HasTurnSignals);
            if (locked) { TDU2FrontendSounds.Command("sndp:wrong"); return; }
            switch (msg)
            {
                case "hood": a.ToggleRoof(); break;
                case "wind": a.ToggleWindows(); break;
                case "clil": a.ToggleIndicator(true); break;
                case "clir": a.ToggleIndicator(false); break;
            }
            IsOpen = false; _acc = null;
            if (!h.CallClip(Menu, "ValidIGM", msg)) h.GotoLabel(Menu, "close", true);
        }

        /// <summary>Jedes Bild, solange das Menü offen ist; true = Tasten verbraucht.</summary>
        /// <param name="arrows">Pfeile/WASD wählen mit aus (Händler); beim Fahren nur 1–4, die Pfeile lenken.</param>
        public static bool Update(bool arrows = true)
        {
            if (!IsOpen) return false;
            var h = Hud;
            if (h == null || _acc == null || _acc.Model == null || _acc.Model.Visual == null) { IsOpen = false; _acc = null; return false; }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Time.unscaledTime - _openedAt < 0.15f) return true;
            if (Input.GetKeyDown(KeyCode.Alpha1) || (arrows && (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)))) Choose(1);
            else if (Input.GetKeyDown(KeyCode.Alpha2) || (arrows && (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)))) Choose(2);
            else if (Input.GetKeyDown(KeyCode.Alpha3) || (arrows && (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)))) Choose(3);
            else if (Input.GetKeyDown(KeyCode.Alpha4) || (arrows && (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)))) Choose(4);
            else if (TDU2Controls.Pressed("accessories") || Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace)) Close();
#endif
            return true;
        }
    }
}
