using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TDU2.IO;
using UnityEngine;

namespace TDU2.Avatars
{
    /// <summary>
    /// FaceFX (OC3, SDK 1.7.2) der Avatare: Gesichtsanimation und Lippen-Sync der Kinos.
    ///   avatar/avatar_face_fx.bnk: female.fxa / male.fxa = FxActor (42 Gesichtsknochen mit Ruhepose und Posen-Deltas je Knoten,
    ///   Gesichtsgraph mit 445 Knoten), Kino-Bänke: *.fxe = FxAnimSet (Kurven je Knoten, Hermite).
    ///   .sce-Spur "Morph" {u32 REFS-Index (Typ 0x0306 "...\female.fxa::Gruppe:Anim"), u64, f32 Länge, u32 0}: der Schlüssel startet
    ///   die Animation am Darsteller, Ein-/Ausblenden 0,2 s (td2.exe FUN_00b77600 / FUN_00b77b20, OpenTDU2 IntroCinematic::applyFaceFx).
    /// Graph: Knotenwert = Kurve (+ Summe bzw. x Produkt der Eingänge über ihre Link-Funktionen), Korrektur-Links skalieren
    /// (1 - Faktor x Eingang), dann auf [min, max] geklemmt. Knochen = Ruhepose + Summe(Wert x Delta) (Position, Skalierung),
    /// Rotation = Ruhe x Produkt slerp(1, Delta, Wert).
    /// Koordinaten: FaceFX in cm, X gespiegelt zur .xsb, Quaternionen (w x y z) als Spaltenvektor-Drehung:
    /// .xsb-Position = (-x, y, z) / 100, Unity-Rotation = (x, -y, -z, w) (geprüft an allen 42 Ruheposen gegen woman_body.xsb).
    /// </summary>
    public sealed class FaceFxActor
    {
        public sealed class Node
        {
            public int Type, Op;
            public string Name;
            public float Min, Max;
            public int[] Src = Array.Empty<int>();
            public int[] Fn = Array.Empty<int>();
            public float[][] Prm = Array.Empty<float[]>();
        }

        public sealed class Delta { public int Node; public Vector3 Pos, Scl; public Quaternion Rot; }   // Rot: FaceFX-Raum (x y z w)

        public sealed class Bone
        {
            public string Name;
            public Vector3 Pos, Scl;
            public Quaternion Rot;
            public Delta[] Deltas = Array.Empty<Delta>();
        }

        public string Name;
        public Node[] Nodes = Array.Empty<Node>();
        public Bone[] Bones = Array.Empty<Bone>();
        public readonly Dictionary<string, int> NodeByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ Lesen
        internal sealed class Reader
        {
            readonly byte[] _d; public int P;
            public Reader(byte[] d) { _d = d; }
            public bool Ok(int n) => P + n <= _d.Length;
            public uint U32() { uint v = BitConverter.ToUInt32(_d, P); P += 4; return v; }
            public int I32() { int v = BitConverter.ToInt32(_d, P); P += 4; return v; }
            public ushort U16() { ushort v = BitConverter.ToUInt16(_d, P); P += 2; return v; }
            public float F32() { float v = BitConverter.ToSingle(_d, P); P += 4; return v; }
            public string Str()
            {
                int n = (int)U32();
                if (n < 0 || P + n > _d.Length) throw new InvalidDataException("FaceFX: Zeichenkette");
                int len = n; while (len > 0 && _d[P + len - 1] == 0) len--;
                string s = Encoding.ASCII.GetString(_d, P, len); P += n; return s;
            }

            /// <summary>"FACE", Version, Kennungen, Klassenliste, Namenstabelle.</summary>
            public string[] Header()
            {
                if (_d.Length < 16 || _d[0] != 'F' || _d[1] != 'A' || _d[2] != 'C' || _d[3] != 'E') throw new InvalidDataException("FaceFX: kein FACE");
                P = 4; U32(); U32(); Str(); Str(); U32(); U16();
                int nc = (int)U32();
                for (int i = 0; i < nc; i++) { U32(); U32(); Str(); U16(); }
                int nn = (int)U32();
                var names = new string[nn];
                for (int i = 0; i < nn; i++) names[i] = Str();
                return names;
            }

            public string Name(string[] names) { uint i = U32(); return i < names.Length ? names[i] : ""; }
        }

        static void ReadBone(Reader r, string[] names, out string name, out Vector3 pos, out Quaternion rot, out Vector3 scl)
        {
            name = r.Name(names);
            pos = new Vector3(r.F32(), r.F32(), r.F32());
            float w = r.F32(), x = r.F32(), y = r.F32(), z = r.F32();
            rot = new Quaternion(x, y, z, w);
            scl = new Vector3(r.F32(), r.F32(), r.F32());
        }

        public static FaceFxActor Parse(byte[] d)
        {
            var r = new Reader(d);
            var names = r.Header();
            var a = new FaceFxActor { Name = r.Name(names) };
            r.U32();
            int nb = (int)r.U32();
            a.Bones = new Bone[nb];
            for (int i = 0; i < nb; i++)
            {
                ReadBone(r, names, out var bn, out var bp, out var bq, out var bs);
                var b = new Bone { Name = bn, Pos = bp, Rot = bq, Scl = bs };
                r.F32(); r.F32(); r.F32(); r.F32();   // inverse Ruhedrehung
                r.U32(); r.F32();                      // Index, Gewicht
                int np = (int)r.U32();
                b.Deltas = new Delta[np];
                for (int k = 0; k < np; k++)
                {
                    int node = (int)r.U32();
                    ReadBone(r, names, out _, out var dp, out var dq, out var ds);
                    b.Deltas[k] = new Delta { Node = node, Pos = dp, Rot = dq, Scl = ds };
                }
                a.Bones[i] = b;
            }
            int nn = (int)r.U32();
            a.Nodes = new Node[nn];
            for (int i = 0; i < nn; i++)
            {
                var n = new Node { Type = (int)r.U32(), Name = r.Name(names) };
                n.Min = r.F32(); r.F32(); n.Max = r.F32(); r.F32();
                n.Op = (int)r.U32();
                int nl = (int)r.U32();
                n.Src = new int[nl]; n.Fn = new int[nl]; n.Prm = new float[nl][];
                for (int k = 0; k < nl; k++)
                {
                    n.Src[k] = (int)r.U32(); n.Fn[k] = (int)r.U32();
                    int pc = (int)r.U32();
                    var p = new float[pc];
                    for (int j = 0; j < pc; j++) p[j] = r.F32();
                    n.Prm[k] = p;
                }
                int np = (int)r.U32();
                for (int k = 0; k < np; k++) { r.U32(); r.U32(); r.I32(); r.F32(); r.U32(); r.Str(); }
                a.Nodes[i] = n;
                if (!a.NodeByName.ContainsKey(n.Name)) a.NodeByName[n.Name] = i;
            }
            return a;
        }

        // ------------------------------------------------------------------ Auswerten
        static float Link(int fn, float[] p, float x)
        {
            switch (fn)
            {
                case 1: return p.Length >= 2 ? p[0] * x + p[1] : x;          // linear (m, b)
                case 2: return p.Length >= 1 ? p[0] * x * x : x * x;         // quadratisch
                case 3: return p.Length >= 1 ? p[0] * x * x * x : x * x * x; // kubisch
                case 4: return (p.Length >= 1 ? p[0] : 1f) * Mathf.Sqrt(Mathf.Max(0f, x));
                case 5: return -x;                                            // negieren
                case 6: return Mathf.Abs(x) > 1e-9f ? 1f / x : 0f;            // invers
                case 7: return Mathf.Clamp01(1f - x);                         // "one clamp"
                case 8: return p.Length >= 1 ? p[0] : 0f;                     // konstant
                case 10:                                                      // geklemmt linear (x1, y1, x2, y2)
                    if (p.Length < 4) return x;
                    if (x <= p[0]) return p[1];
                    if (x >= p[2]) return p[3];
                    return p[1] + (p[3] - p[1]) * (x - p[0]) / (p[2] - p[0]);
                default: return x;
            }
        }

        /// <summary>Werte aller Knoten (in Dateireihenfolge: Eingänge stehen immer vor ihrem Knoten).</summary>
        public void Evaluate(FaceFxAnim anim, float t, float[] vals)
        {
            float[] track = anim?.Sample(this, t);
            for (int i = 0; i < Nodes.Length; i++)
            {
                var n = Nodes[i];
                bool hasTrack = track != null && !float.IsNaN(track[i]);
                float tv = hasTrack ? track[i] : 0f;
                float v;
                bool any = false;
                switch (n.Op)
                {
                    case 1:   // Produkt
                        v = hasTrack ? tv : 1f;
                        for (int k = 0; k < n.Src.Length; k++) if (n.Fn[k] != 9) { v *= Link(n.Fn[k], n.Prm[k], vals[n.Src[k]]); any = true; }
                        if (!any && !hasTrack) v = 0f;
                        break;
                    case 2:   // Maximum
                    case 3:   // Minimum
                        v = hasTrack ? tv : (n.Op == 2 ? float.MinValue : float.MaxValue);
                        for (int k = 0; k < n.Src.Length; k++)
                            if (n.Fn[k] != 9) { float x = Link(n.Fn[k], n.Prm[k], vals[n.Src[k]]); v = n.Op == 2 ? Mathf.Max(v, x) : Mathf.Min(v, x); any = true; }
                        if (!any && !hasTrack) v = 0f;
                        break;
                    default:  // Summe
                        v = tv;
                        for (int k = 0; k < n.Src.Length; k++) if (n.Fn[k] != 9) v += Link(n.Fn[k], n.Prm[k], vals[n.Src[k]]);
                        break;
                }
                for (int k = 0; k < n.Src.Length; k++)
                    if (n.Fn[k] == 9 && n.Prm[k].Length >= 1) v *= 1f - n.Prm[k][0] * vals[n.Src[k]];
                vals[i] = v < n.Min ? n.Min : (v > n.Max ? n.Max : v);
            }
        }

        static Quaternion SlerpFromIdentity(Quaternion q, float w)
        {
            if (q.w < 0f) q = new Quaternion(-q.x, -q.y, -q.z, -q.w);
            return Quaternion.SlerpUnclamped(Quaternion.identity, q, w);
        }

        float[] _neutral;

        /// <summary>
        /// Knotenwerte ohne Animation (Ruhe des Graphen). Einige Knoten stehen dort nicht auf 0 (beim Mann "Focus" = -1 = Augäpfel 28°
        /// gedreht, "TeethHeigh" 0,1, "tBack" 0,05): ohne FaceFX zeigt das Spiel die Ruhepose des Skeletts, also werden nur die
        /// Abweichungen von diesen Ruhewerten angewendet – sonst schielen die Figuren, sobald eine Animation läuft.
        /// </summary>
        public float[] Neutral
        {
            get
            {
                if (_neutral == null) { var v = new float[Nodes.Length]; Evaluate(null, 0f, v); _neutral = v; }
                return _neutral;
            }
        }

        /// <summary>Lokale Knochenlage (Unity-Konvention des .xsb-Skeletts: Position in m, gespiegeltes X, Rotation konjugiert).</summary>
        public void Pose(int bone, float[] vals, out Vector3 pos, out Quaternion rot, out Vector3 scl)
        {
            var b = Bones[bone];
            var p = b.Pos; var s = b.Scl; var q = b.Rot;
            var n0 = Neutral;
            foreach (var dl in b.Deltas)
            {
                if (dl.Node < 0 || dl.Node >= vals.Length) continue;
                float w = vals[dl.Node] - (dl.Node < n0.Length ? n0[dl.Node] : 0f);
                if (Mathf.Abs(w) < 1e-5f) continue;
                p += dl.Pos * w;
                s += dl.Scl * w;
                q = q * SlerpFromIdentity(dl.Rot, w);
            }
            pos = new Vector3(-p.x, p.y, p.z) * 0.01f;
            float l = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (l < 1e-6f) q = Quaternion.identity; else q = new Quaternion(q.x / l, q.y / l, q.z / l, q.w / l);
            rot = new Quaternion(q.x, -q.y, -q.z, q.w);
            scl = new Vector3(Mathf.Abs(s.x) < 1e-4f ? 1f : s.x, Mathf.Abs(s.y) < 1e-4f ? 1f : s.y, Mathf.Abs(s.z) < 1e-4f ? 1f : s.z);
        }
    }

    /// <summary>Eine FaceFX-Animation (.fxe): Hermite-Kurven je Knoten.</summary>
    public sealed class FaceFxAnim
    {
        public string Name;
        public float BlendIn, BlendOut, Start, End;
        public string[] CurveNodes = Array.Empty<string>();
        public float[][] Keys = Array.Empty<float[]>();   // je Kurve: t, Wert, Steigung ein, Steigung aus (x Anzahl)
        int[] _map; FaceFxActor _mapFor;
        float[] _buf;

        public float Duration => Mathf.Max(0f, End);

        static float Hermite(float[] k, float t)
        {
            int n = k.Length / 4;
            if (n == 0) return float.NaN;
            if (t <= k[0]) return k[1];
            if (t >= k[(n - 1) * 4]) return k[(n - 1) * 4 + 1];
            for (int i = 0; i < n - 1; i++)
            {
                int a = i * 4, b = a + 4;
                if (t > k[b]) continue;
                float dt = k[b] - k[a];
                float s = dt > 1e-6f ? (t - k[a]) / dt : 0f, s2 = s * s, s3 = s2 * s;
                return (2 * s3 - 3 * s2 + 1) * k[a + 1] + (s3 - 2 * s2 + s) * dt * k[a + 3] + (-2 * s3 + 3 * s2) * k[b + 1] + (s3 - s2) * dt * k[b + 2];
            }
            return k[(n - 1) * 4 + 1];
        }

        /// <summary>Kurvenwerte je Knoten des Darstellers (NaN = keine Kurve).</summary>
        public float[] Sample(FaceFxActor actor, float t)
        {
            if (_mapFor != actor)
            {
                _mapFor = actor;
                _map = new int[CurveNodes.Length];
                for (int i = 0; i < CurveNodes.Length; i++) _map[i] = actor.NodeByName.TryGetValue(CurveNodes[i], out int n) ? n : -1;
                _buf = new float[actor.Nodes.Length];
            }
            for (int i = 0; i < _buf.Length; i++) _buf[i] = float.NaN;
            for (int i = 0; i < CurveNodes.Length; i++)
            {
                if (_map[i] < 0 || Keys[i].Length < 4) continue;
                _buf[_map[i]] = Hermite(Keys[i], t);
            }
            return _buf;
        }
    }

    /// <summary>FxAnimSet (.fxe) einer Kino-Bank.</summary>
    public sealed class FaceFxAnimSet
    {
        public string Actor;
        public readonly Dictionary<string, FaceFxAnim> Anims = new Dictionary<string, FaceFxAnim>(StringComparer.OrdinalIgnoreCase);
        public bool IsFemale => Actor != null && Actor.IndexOf("female", StringComparison.OrdinalIgnoreCase) >= 0;

        public static FaceFxAnimSet Parse(byte[] d)
        {
            var r = new FaceFxActor.Reader(d);
            var names = r.Header();
            var s = new FaceFxAnimSet();
            r.Name(names); r.U32(); s.Actor = r.Name(names);
            r.Name(names);
            int na = (int)r.U32();
            for (int i = 0; i < na; i++)
            {
                var a = new FaceFxAnim { Name = r.Name(names) };
                int nc = (int)r.U32();
                a.CurveNodes = new string[nc];
                for (int k = 0; k < nc; k++) { a.CurveNodes[k] = r.Name(names); r.U32(); }
                int nk = (int)r.U32();
                var all = new float[nk * 4];
                for (int k = 0; k < all.Length; k++) all[k] = r.F32();
                int ncnt = (int)r.U32();
                a.Keys = new float[nc][];
                int o = 0;
                float start = float.MaxValue, end = 0f;
                for (int k = 0; k < ncnt; k++)
                {
                    int c = (int)r.U32();
                    if (k >= nc) continue;
                    c = Mathf.Clamp(c, 0, nk - o);
                    a.Keys[k] = new float[c * 4];
                    Array.Copy(all, o * 4, a.Keys[k], 0, c * 4);
                    if (c > 0) { start = Mathf.Min(start, all[o * 4]); end = Mathf.Max(end, all[(o + c - 1) * 4]); }
                    o += c;
                }
                for (int k = 0; k < nc; k++) if (a.Keys[k] == null) a.Keys[k] = Array.Empty<float>();
                a.BlendIn = r.F32(); a.BlendOut = r.F32();
                r.U32(); r.U32(); r.U32(); r.I32();
                a.Start = start == float.MaxValue ? 0f : start; a.End = end;
                s.Anims[a.Name] = a;
            }
            return s;
        }
    }

    /// <summary>Darsteller (female.fxa / male.fxa aus avatar/avatar_face_fx.bnk), einmal geladen.</summary>
    public static class FaceFxLibrary
    {
        static FaceFxActor _female, _male;
        static bool _tried;
        static readonly object _lock = new object();

        public static FaceFxActor Actor(bool woman)
        {
            lock (_lock)
            {
                if (!_tried)
                {
                    _tried = true;
                    try
                    {
                        var p = Path.Combine(AvatarLibrary.BnkDir ?? "", "avatar", "avatar_face_fx.bnk");
                        if (File.Exists(p))
                        {
                            var b = BnkArchive.Open(p);
                            var ef = b.Find("female.fxa"); if (ef != null) _female = FaceFxActor.Parse(b.Read(ef));
                            var em = b.Find("male.fxa"); if (em != null) _male = FaceFxActor.Parse(b.Read(em));
                            DebugManager.Info($"[TDU2] FaceFX: {(_female != null ? _female.Nodes.Length : 0)} / {(_male != null ? _male.Nodes.Length : 0)} Knoten (Frau/Mann)");
                        }
                        else DebugManager.Warning("[TDU2] FaceFX: avatar/avatar_face_fx.bnk fehlt");
                    }
                    catch (Exception ex) { DebugManager.Error("[TDU2] FaceFX: " + ex.Message); }
                }
                return woman ? (_female ?? _male) : (_male ?? _female);
            }
        }
    }
}
