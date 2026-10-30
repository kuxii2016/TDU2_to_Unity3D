using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TDU2.Cinematics
{
    /// <summary>
    /// Echtzeit-Kinos des Spiels (Eden): eine Kino-Zeitleiste (.cin, "CIN!") aus Einstellungen (Shots) über eine oder mehrere
    /// Szenen (.sce, "SCE!"), dazu Nachrichten-Spuren (Wetter "weat:", "wmod:", Schatten "shad:", Flash, Ton) und Fx-Ereignisse.
    /// Nach OpenTDU2 (game/cinematic/cinematic_scene.*) und docs/re/intro_spec.md.
    ///
    /// Gemeinsames Spur-Layout: { u16 Kind; u16 Flags; u32 Size (ganze Spur); u32 Id; u32 NumKeys; f32 Duration;
    /// f32 Times[NumKeys]; Werte[NumKeys] }. Spur-Ids = 4-Byte-"Faltnamen" (Zeichen i auf Byte i &amp; 3 addiert).
    ///
    /// Koordinaten: Szenen-Ursprung (D3D-Matrix, Zeilenvektoren) -> Spielraum; Spielraum = Unity-Raum unter MapRoot
    /// (linkshändig, Y oben). Heading-Quaternionen werden wie in <see cref="TDU2.Vehicles.CineScene"/> konjugiert
    /// (lokales +Z = Blick- bzw. Fahrtrichtung).
    /// </summary>
    public static class CinIds
    {
        public const uint TrackPosition = 0xd7e2d8c4, TrackHeading = 0x64c8d3b1, TrackScale = 0x6c6163b8, TrackFov = 0x00766f46;
        public const uint TrackMessage = 0x73d8ccae, TrackFx = 0x00007846, TrackProperty = 0xd3593709;
        public const uint ClassCamera = 0x2a043536, ClassActor = 0xd039fa46;
        public const ushort RefMessage = 0x020b, RefScene = 0x0303;

        /// <summary>4-Byte-Faltname (Eden-Kino-Bezeichner).</summary>
        public static uint Fold(string name)
        {
            var b = new byte[4];
            if (name != null)
                for (int i = 0; i < name.Length; i++) b[i & 3] = (byte)(b[i & 3] + (byte)name[i]);
            return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
        }

        public static readonly uint TrackAnimation = Fold("Animation"), TrackVisibility = Fold("Visibility"), TrackMorph = Fold("Morph");

        /// <summary>Eden-Heading (Rohwerte xyzw) -> Unity-Rotation (Spielraum).</summary>
        public static Quaternion ToUnity(Quaternion raw) => new Quaternion(-raw.x, -raw.y, -raw.z, raw.w);

        /// <summary>
        /// Rotation im Rahmen <paramref name="frame"/> (z.B. MapRoot, gespiegelt mit Skalierung -1 in X) -> Weltrotation:
        /// über die transformierten Achsen (TransformVector berücksichtigt die Spiegelung, die Rotation allein nicht).
        /// </summary>
        public static Quaternion FrameRotation(Transform frame, Quaternion local)
        {
            if (frame == null) return local;
            var f = frame.TransformVector(local * Vector3.forward);
            var u = frame.TransformVector(local * Vector3.up);
            if (f.sqrMagnitude < 1e-10f) return local;
            return Quaternion.LookRotation(f.normalized, u.sqrMagnitude > 1e-10f ? u.normalized : Vector3.up);
        }

        internal static uint U32(byte[] d, int o) => BitConverter.ToUInt32(d, o);
        internal static int I32(byte[] d, int o) => BitConverter.ToInt32(d, o);
        internal static ushort U16(byte[] d, int o) => BitConverter.ToUInt16(d, o);
        internal static float F32(byte[] d, int o) => BitConverter.ToSingle(d, o);

        internal static string CStr(byte[] d, int o, int max)
        {
            int e = o, end = Math.Min(d.Length, o + Math.Max(0, max));
            while (e < end && d[e] != 0) e++;
            return Encoding.ASCII.GetString(d, o, e - o);
        }

        /// <summary>Schlüssel vor der Zeit und Mischanteil zum nächsten.</summary>
        internal static int FindKey(float[] times, float time, out float blend)
        {
            blend = 0f;
            if (times == null || times.Length < 2 || time <= times[0]) return 0;
            if (time >= times[times.Length - 1]) return times.Length - 1;
            for (int i = 0; i + 1 < times.Length; i++)
                if (time < times[i + 1])
                {
                    float span = times[i + 1] - times[i];
                    blend = span > 0f ? (time - times[i]) / span : 0f;
                    return i;
                }
            return times.Length - 1;
        }
    }

    /// <summary>Eine Spur (Position, Heading, Fov …): Zeiten und Werte (Komponenten je Schlüssel).</summary>
    public sealed class CinTrack
    {
        public uint Id;
        public int Components;
        public float[] Times = Array.Empty<float>();
        public float[] Values = Array.Empty<float>();
        /// <summary>Rohwerte (u32) für Index-Spuren (Animation: REFS-Index im ersten Wert).</summary>
        public uint[] Raw = Array.Empty<uint>();
    }

    /// <summary>Text-Eigenschaft eines Darstellers (Spur 0xd3593709, z.B. "LookAtTargetFaceFx").</summary>
    public sealed class CinProperty
    {
        public string Name = "";
        public readonly List<float> Times = new List<float>();
        public readonly List<string> Values = new List<string>();
        public string ValueAt(float time)
        {
            string v = "";
            for (int k = 0; k < Times.Count && k < Values.Count; k++) if (Times[k] <= time) v = Values[k];
            return v;
        }
    }

    /// <summary>Objekt einer .sce-Szene: Kamera oder Darsteller (Auto, Avatar, Requisite).</summary>
    public sealed class CinObject
    {
        public string Name = "";
        public uint ClassHash, NameHash;
        public ulong EntityHashcode;   // __Hc der gespielten Entität (0x6666666666666666 = keine)
        public readonly List<CinTrack> Tracks = new List<CinTrack>();
        public readonly List<CinProperty> Properties = new List<CinProperty>();
        public bool IsCamera => ClassHash == CinIds.ClassCamera;

        public CinTrack Find(uint id)
        {
            foreach (var t in Tracks) if (t.Id == id) return t;
            return null;
        }

        public CinProperty FindProperty(string name)
        {
            foreach (var p in Properties) if (p.Name == name) return p;
            return null;
        }

        /// <summary>Position im Szenenraum.</summary>
        public Vector3 SamplePosition(float time)
        {
            var t = Find(CinIds.TrackPosition);
            if (t == null || t.Components < 3 || t.Times.Length == 0) return Vector3.zero;
            int k = CinIds.FindKey(t.Times, time, out float f);
            int a = k * t.Components;
            var pa = new Vector3(t.Values[a], t.Values[a + 1], t.Values[a + 2]);
            if (f <= 0f || k + 1 >= t.Times.Length) return pa;
            int b = (k + 1) * t.Components;
            return Vector3.LerpUnclamped(pa, new Vector3(t.Values[b], t.Values[b + 1], t.Values[b + 2]), f);
        }

        /// <summary>Heading als Eden-Rohquaternion (xyzw, normalisierte Lerp wie das Original).</summary>
        public Quaternion SampleHeadingRaw(float time)
        {
            var t = Find(CinIds.TrackHeading);
            if (t == null || t.Components < 4 || t.Times.Length == 0) return Quaternion.identity;
            int k = CinIds.FindKey(t.Times, time, out float f);
            int a = k * t.Components;
            var qa = new Quaternion(t.Values[a], t.Values[a + 1], t.Values[a + 2], t.Values[a + 3]);
            if (f <= 0f || k + 1 >= t.Times.Length) return qa;
            int b = (k + 1) * t.Components;
            var qb = new Quaternion(t.Values[b], t.Values[b + 1], t.Values[b + 2], t.Values[b + 3]);
            if (qa.x * qb.x + qa.y * qb.y + qa.z * qb.z + qa.w * qb.w < 0f) qb = new Quaternion(-qb.x, -qb.y, -qb.z, -qb.w);
            var r = new Quaternion(qa.x + (qb.x - qa.x) * f, qa.y + (qb.y - qa.y) * f, qa.z + (qb.z - qa.z) * f, qa.w + (qb.w - qa.w) * f);
            float len = Mathf.Sqrt(r.x * r.x + r.y * r.y + r.z * r.z + r.w * r.w);
            return len > 1e-8f ? new Quaternion(r.x / len, r.y / len, r.z / len, r.w / len) : Quaternion.identity;
        }

        /// <summary>Rotation im Szenenraum (Unity-Konvention, +Z vorne).</summary>
        public Quaternion SampleRotation(float time) => CinIds.ToUnity(SampleHeadingRaw(time));

        public float SampleFloat(uint id, float time, float def)
        {
            var t = Find(id);
            if (t == null || t.Components < 1 || t.Times.Length == 0) return def;
            int k = CinIds.FindKey(t.Times, time, out float f);
            float a = t.Values[k * t.Components];
            if (f <= 0f || k + 1 >= t.Times.Length) return a;
            return a + (t.Values[(k + 1) * t.Components] - a) * f;
        }

        /// <summary>Sichtbarkeit (Spur "Visibility", Stufenwert; ohne Spur sichtbar).</summary>
        public bool VisibleAt(float time)
        {
            var t = Find(CinIds.TrackVisibility);
            if (t == null || t.Raw.Length == 0) return true;
            int k = CinIds.FindKey(t.Times, time, out _);
            return t.Raw[k * Math.Max(1, t.Components)] != 0;
        }

        /// <summary>REFS-Index der Animation am Zeitpunkt (Stufenwert), -1 = keine.</summary>
        public int AnimationRefAt(float time, out float keyTime)
        {
            keyTime = 0f;
            var t = Find(CinIds.TrackAnimation);
            if (t == null || t.Raw.Length == 0) return -1;
            int k = CinIds.FindKey(t.Times, time, out _);
            keyTime = t.Times[k];
            return (int)t.Raw[k * Math.Max(1, t.Components)];
        }
    }

    /// <summary>.sce: Szene mit Ursprung (Spielraum) und Objekten.</summary>
    public sealed class CinScene
    {
        public sealed class Reference { public ushort Type; public ulong Hashcode; public string Name = ""; }

        public string Name = "";
        public float Duration;
        public readonly float[] Origin = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };   // D3D, Zeilen
        public readonly List<CinObject> Objects = new List<CinObject>();
        public readonly List<Reference> References = new List<Reference>();

        /// <summary>Szenenraum -> Spielraum (Zeilenvektor * Ursprung).</summary>
        public Vector3 ToGame(Vector3 p)
        {
            var m = Origin;
            return new Vector3(p.x * m[0] + p.y * m[4] + p.z * m[8] + m[12],
                               p.x * m[1] + p.y * m[5] + p.z * m[9] + m[13],
                               p.x * m[2] + p.y * m[6] + p.z * m[10] + m[14]);
        }

        /// <summary>Rotation des Ursprungs (Spielraum).</summary>
        public Quaternion OriginRotation
        {
            get
            {
                var m = Origin;
                var f = new Vector3(m[8], m[9], m[10]); var u = new Vector3(m[4], m[5], m[6]);
                if (f.sqrMagnitude < 1e-8f || u.sqrMagnitude < 1e-8f) return Quaternion.identity;
                return Quaternion.LookRotation(f.normalized, u.normalized);
            }
        }

        public CinObject Find(string name)
        {
            foreach (var o in Objects) if (string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) return o;
            return null;
        }

        public CinObject FindByHash(uint nameHash)
        {
            foreach (var o in Objects) if (o.NameHash == nameHash || CinIds.Fold(o.Name) == nameHash) return o;
            return null;
        }

        public string ReferenceName(int index) => index >= 0 && index < References.Count ? References[index].Name : null;

        public static CinScene Parse(byte[] d, string name = "")
        {
            var sc = new CinScene { Name = name ?? "" };
            if (d == null || d.Length < 0x90 || d[0] != 'S' || d[1] != 'C' || d[2] != 'E' || d[3] != '!') return sc;
            int dataOffset = 0;
            for (int o = 0x10; o + 8 <= d.Length; o += 4)
                if (d[o] == 'D' && d[o + 1] == 'A' && d[o + 2] == 'T' && d[o + 3] == 'A') { dataOffset = o + 8; break; }
            if (dataOffset == 0 || dataOffset + 0x68 > d.Length) return sc;
            int dataSize = (int)Math.Min(CinIds.U32(d, dataOffset - 4), (uint)(d.Length - dataOffset));
            int c = dataOffset;
            sc.Duration = CinIds.F32(d, c);
            int numObjects = (int)CinIds.U32(d, c + 4);
            for (int i = 0; i < 16; i++) sc.Origin[i] = CinIds.F32(d, c + 0x28 + i * 4);

            int off = 0x68;
            for (int i = 0; i < numObjects && off + 0x3c <= dataSize; i++)
            {
                int objSize = (int)CinIds.U32(d, c + off), hs = (int)CinIds.U32(d, c + off + 4);
                if (objSize < hs || off + objSize > dataSize || objSize <= 0) break;
                var ob = new CinObject
                {
                    ClassHash = CinIds.U32(d, c + off + 8),
                    NameHash = CinIds.U32(d, c + off + 0x0c),
                    Name = CinIds.CStr(d, c + off + 0x14, 32),
                    EntityHashcode = BitConverter.ToUInt64(d, c + off + 0x34),
                };
                int t = off + hs, objEnd = off + objSize;
                while (t + 0x14 <= objEnd)
                {
                    int ts = (int)CinIds.U32(d, c + t + 4);
                    if (ts < 0x14 || t + ts > objEnd) break;
                    uint id = CinIds.U32(d, c + t + 8);
                    int nk = (int)CinIds.U32(d, c + t + 12);
                    if (id == CinIds.TrackProperty && ts >= 0x90)
                    {
                        var pr = new CinProperty { Name = CinIds.CStr(d, c + t + 0x10, 0x80) };
                        int sub = t + 0x90;
                        for (int s = 0; s < nk && sub + 0x14 <= t + ts; s++)
                        {
                            int ss = (int)CinIds.U32(d, c + sub + 4), sk = (int)CinIds.U32(d, c + sub + 12);
                            if (ss < 0x14 || sub + ss > t + ts) break;
                            int key = sub + 0x14 + sk * 4;
                            for (int k = 0; k < sk && key + 8 <= sub + ss; k++)
                            {
                                pr.Times.Add(CinIds.F32(d, c + sub + 0x14 + k * 4));
                                string v = CinIds.CStr(d, c + key + 8, sub + ss - key - 8);
                                pr.Values.Add(v);
                                key += 8 + v.Length + 1;
                            }
                            sub += ss;
                        }
                        ob.Properties.Add(pr);
                    }
                    else if (nk > 0 && nk < 100000)
                    {
                        int valuesSize = ts - 0x14 - nk * 4;
                        int comp = valuesSize > 0 ? valuesSize / nk / 4 : 0;
                        var tr = new CinTrack { Id = id, Components = comp, Times = new float[nk] };
                        for (int k = 0; k < nk; k++) tr.Times[k] = CinIds.F32(d, c + t + 0x14 + k * 4);
                        int vo = t + 0x14 + nk * 4;
                        tr.Values = new float[nk * comp]; tr.Raw = new uint[nk * comp];
                        for (int k = 0; k < nk * comp; k++) { tr.Values[k] = CinIds.F32(d, c + vo + k * 4); tr.Raw[k] = CinIds.U32(d, c + vo + k * 4); }
                        ob.Tracks.Add(tr);
                    }
                    t += ts;
                }
                sc.Objects.Add(ob);
                off += objSize;
            }

            // REFS (nach DATA)
            int e = dataOffset + (int)CinIds.U32(d, dataOffset - 4);
            if (e + 12 <= d.Length && d[e] == 'R' && d[e + 1] == 'E' && d[e + 2] == 'F' && d[e + 3] == 'S')
            {
                int refsSize = (int)CinIds.U32(d, e + 4), num = (int)CinIds.U32(d, e + 8);
                int end = Math.Min(d.Length, e + 8 + refsSize), p = e + 12;
                for (int r = 0; r < num && p + 4 <= end; r++)
                {
                    var rf = new Reference { Type = CinIds.U16(d, p) };
                    int es = CinIds.U16(d, p + 2);
                    if (es < 4 || p + es > end) break;
                    if (rf.Type == CinIds.RefMessage) rf.Name = CinIds.CStr(d, p + 4, es - 4);
                    else if (es >= 12) { rf.Hashcode = BitConverter.ToUInt64(d, p + 4); rf.Name = CinIds.CStr(d, p + 12, es - 12); }
                    sc.References.Add(rf);
                    p += es;
                }
            }
            return sc;
        }
    }

    /// <summary>.cin: Zeitleiste (Shots, Nachrichten, Fx) über Szenen.</summary>
    public sealed class CinData
    {
        public sealed class Reference { public ushort Type; public string Text = ""; public ulong Hashcode; }

        public sealed class Shot
        {
            public float Start, Duration, CameraStart, CameraEnd;
            public int Scene;
            public uint CameraHash;
            public uint FadeType;          // 1 = Einblenden aus einer Farbe
            public float FadeDuration;
            public Color FadeColor = Color.black;

            /// <summary>Szenenzeit zu einer Kinozeit in diesem Shot (Kamerabereich auf die Shot-Dauer abgebildet: Zeitlupe).</summary>
            public float SceneTime(float time)
            {
                float rate = Duration > 0f ? (CameraEnd - CameraStart) / Duration : 1f;
                return CameraStart + Mathf.Max(time - Start, 0f) * rate;
            }
        }

        public sealed class Event
        {
            public float Time, Duration;
            public int Reference = -1;
            public int Track;
            public uint Flags;
            public Matrix4x4 World = Matrix4x4.identity;   // Fx (D3D-Zeilen, wie gelesen)
        }

        public string Name = "";
        public float Duration;
        public readonly List<Reference> References = new List<Reference>();
        public readonly List<int> SceneReferences = new List<int>();
        public readonly List<Shot> Shots = new List<Shot>();
        public readonly List<Event> Messages = new List<Event>();
        public readonly List<Event> FxEvents = new List<Event>();

        public static CinData Parse(byte[] d, string name = "")
        {
            var cd = new CinData { Name = name ?? "" };
            if (d == null || d.Length < 0x20 || d[0] != 'C' || d[1] != 'I' || d[2] != 'N' || d[3] != '!') return null;
            int dataOffset = 0, dataSize = 0;
            for (int off = 0x10; off + 8 <= d.Length;)
            {
                int cs = (int)CinIds.U32(d, off + 4);
                bool refs = d[off] == 'R' && d[off + 1] == 'E' && d[off + 2] == 'F' && d[off + 3] == 'S';
                bool data = d[off] == 'D' && d[off + 1] == 'A' && d[off + 2] == 'T' && d[off + 3] == 'A';
                if (refs)
                {
                    int num = (int)CinIds.U32(d, off + 8), p = off + 12, end = Math.Min(d.Length, off + 8 + cs);
                    for (int i = 0; i < num && p + 4 <= end; i++)
                    {
                        var r = new Reference { Type = CinIds.U16(d, p) };
                        int es = CinIds.U16(d, p + 2);
                        if (es < 4 || p + es > end) break;
                        if (r.Type == CinIds.RefMessage) r.Text = CinIds.CStr(d, p + 4, es - 4);
                        else if (es >= 12)
                        {
                            r.Hashcode = BitConverter.ToUInt64(d, p + 4);
                            if (r.Type == CinIds.RefScene) r.Text = CinIds.CStr(d, p + 12, es - 12);
                        }
                        cd.References.Add(r);
                        p += es;
                    }
                }
                else if (data) { dataOffset = off + 8; dataSize = Math.Min(cs, d.Length - dataOffset); }
                if (cs <= 0) break;
                off += 8 + cs;
            }
            if (dataOffset == 0 || dataSize < 0x3c) return null;
            int c = dataOffset;
            cd.Duration = CinIds.F32(d, c);
            int numScenes = (int)CinIds.U32(d, c + 0x0c);
            int o = 0x3c;
            for (int i = 0; i < numScenes && o + 8 <= dataSize; i++, o += 8) cd.SceneReferences.Add(CinIds.I32(d, c + o + 4));
            int messageTrack = 0;
            while (o + 0x14 <= dataSize)
            {
                int ts = (int)CinIds.U32(d, c + o + 4);
                if (ts < 0x14 || o + ts > dataSize) break;
                uint id = CinIds.U32(d, c + o + 8);
                int nk = (int)CinIds.U32(d, c + o + 12);
                int timesOff = o + 0x14, valuesOff = timesOff + nk * 4;
                int vs = nk > 0 ? (ts - 0x14 - nk * 4) / nk : 0;
                for (int k = 0; k < nk && vs > 0; k++)
                {
                    float time = CinIds.F32(d, c + timesOff + k * 4);
                    int v = c + valuesOff + k * vs;
                    if (id == 0 && vs >= 0x14)
                    {
                        var s = new Shot
                        {
                            Start = time, Duration = CinIds.F32(d, v), Scene = (int)CinIds.U32(d, v + 4), CameraHash = CinIds.U32(d, v + 8),
                            CameraStart = CinIds.F32(d, v + 0x0c), CameraEnd = CinIds.F32(d, v + 0x10),
                        };
                        if (vs >= 0x28)
                        {
                            s.FadeType = CinIds.U32(d, v + 0x14); s.FadeDuration = CinIds.F32(d, v + 0x18);
                            s.FadeColor = new Color(CinIds.F32(d, v + 0x1c), CinIds.F32(d, v + 0x20), CinIds.F32(d, v + 0x24), 1f);
                        }
                        cd.Shots.Add(s);
                    }
                    else if (id == CinIds.TrackMessage && vs >= 8)
                        cd.Messages.Add(new Event { Time = time, Reference = CinIds.I32(d, v), Duration = CinIds.F32(d, v + 4), Track = messageTrack });
                    else if (id == CinIds.TrackFx && vs >= 0x54)
                    {
                        var ev = new Event { Time = time, Reference = CinIds.I32(d, v), Flags = CinIds.U32(d, v + 4), Duration = CinIds.F32(d, v + 0x0c) };
                        var m = new Matrix4x4();
                        for (int r = 0; r < 4; r++) for (int col = 0; col < 4; col++) m[r, col] = CinIds.F32(d, v + 0x14 + (r * 4 + col) * 4);
                        ev.World = m;
                        cd.FxEvents.Add(ev);
                    }
                }
                if (id == CinIds.TrackMessage) messageTrack++;
                o += ts;
            }
            cd.Messages.Sort((a, b) => a.Time.CompareTo(b.Time));
            cd.FxEvents.Sort((a, b) => a.Time.CompareTo(b.Time));
            return cd.Shots.Count > 0 || cd.FxEvents.Count > 0 || cd.Messages.Count > 0 ? cd : null;
        }

        /// <summary>Text der Nachricht (z.B. "weat:SS_Arche").</summary>
        public string MessageText(Event e) => e != null && e.Reference >= 0 && e.Reference < References.Count ? References[e.Reference].Text : null;

        /// <summary>Datei-Stamm der Szene einer Szenen-Referenz (Quellpfad der .sce ohne Ordner und Endung).</summary>
        public string SceneFile(int sceneIndex)
        {
            if (sceneIndex < 0 || sceneIndex >= SceneReferences.Count) return null;
            int r = SceneReferences[sceneIndex];
            if (r < 0 || r >= References.Count) return null;
            string path = References[r].Text ?? "";
            int slash = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
            string file = slash >= 0 ? path.Substring(slash + 1) : path;
            int dot = file.LastIndexOf('.');
            return dot >= 0 ? file.Substring(0, dot) : file;
        }

        public Shot ShotAt(float time)
        {
            Shot s = Shots.Count > 0 ? Shots[0] : null;
            foreach (var sh in Shots) if (time >= sh.Start) s = sh;
            return s;
        }

        /// <summary>Argument der letzten Nachricht mit dem Präfix bis zur Zeit (z.B. "weat:" -> "SS_Arche"), null = keine.</summary>
        public string LastMessage(string prefix, float time, out float at)
        {
            at = -1f; string res = null;
            foreach (var e in Messages)
            {
                if (e.Time > time) break;
                var tx = MessageText(e);
                if (tx != null && tx.StartsWith(prefix, StringComparison.Ordinal)) { res = tx.Substring(prefix.Length); at = e.Time; }
            }
            return res;
        }

        /// <summary>Nachrichten im Zeitfenster (from, to] – für einmalige Auslöser (Ton, Flash, Schatten).</summary>
        public IEnumerable<Event> MessagesBetween(float from, float to)
        {
            foreach (var e in Messages) if (e.Time > from && e.Time <= to) yield return e;
        }

        /// <summary>Aktive Kamera: Spielraum-Position, Unity-Rotation (Spielraum) und vertikales Sichtfeld (Grad).</summary>
        public bool Camera(IList<CinScene> scenes, float time, out Vector3 gamePos, out Quaternion gameRot, out float fovDeg)
        {
            gamePos = Vector3.zero; gameRot = Quaternion.identity; fovDeg = 60f;
            var shot = ShotAt(time);
            if (shot == null || scenes == null || shot.Scene < 0 || shot.Scene >= scenes.Count || scenes[shot.Scene] == null) return false;
            var sc = scenes[shot.Scene];
            var cam = sc.FindByHash(shot.CameraHash);
            if (cam == null) return false;
            float local = Mathf.Min(shot.SceneTime(time), Mathf.Max(shot.CameraEnd, shot.CameraStart));
            gamePos = sc.ToGame(cam.SamplePosition(local));
            gameRot = sc.OriginRotation * cam.SampleRotation(local);
            fovDeg = cam.SampleFloat(CinIds.TrackFov, local, 60f);
            return true;
        }

        /// <summary>Szene und Szenenzeit des aktuellen Shots.</summary>
        public bool SceneTime(IList<CinScene> scenes, float time, out CinScene scene, out float sceneTime)
        {
            scene = null; sceneTime = 0f;
            var shot = ShotAt(time);
            if (shot == null || scenes == null || shot.Scene < 0 || shot.Scene >= scenes.Count) return false;
            scene = scenes[shot.Scene];
            sceneTime = shot.SceneTime(time);
            return scene != null;
        }
    }
}
