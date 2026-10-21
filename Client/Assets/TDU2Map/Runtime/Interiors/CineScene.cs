using System;
using System.Collections.Generic;
using System.Text;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Cinematic-Szene (.sce, "SCE!", Spezifikation §10.5): Objekte (Kameras, Darsteller) mit dem ersten Schlüssel ihrer Kanäle
    /// Position / Heading / Fov / Animation. Koordinaten im Rahmen der Interieur-Szene (Spielraum, wie die Entitäten); Heading
    /// wie die Clips konjugiert (lokales +Z = Blick- bzw. Laufrichtung). Animation = Name des .anm-Clips aus den REFS (Art 2).
    /// Verwendet für Kleidungsladen, Friseur, Garderobe (Avatar-Platz, Kamera je Kategorie, Verkäufer-Pose).
    /// </summary>
    public sealed class CineScene
    {
        public sealed class Obj
        {
            public string Name = "";
            public bool IsCamera;
            public ulong Bind;          // __Hc der gespielten Entität (0x6666666666666666 = keine)
            public Vector3 Position;
            public bool HasPosition;
            public Quaternion Rotation = Quaternion.identity;   // Spielraum, schon konjugiert
            public float Fov = 40f;     // vertikal, Grad (Kameras)
            public string Clip;         // erster Animations-Schlüssel (Darsteller)
        }

        public string Name = "";
        public float Duration;
        public readonly List<Obj> Objects = new List<Obj>();

        const uint ClassCamera = 0x2a043536, ClassActor = 0xd039fa46;
        const uint ChPosition = 0xd7e2d8c4, ChHeading = 0x64c8d3b1, ChFov = 0x00766f46, ChAnimation = 0xdcd2e210;

        public Obj Find(string name)
        {
            foreach (var o in Objects) if (string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) return o;
            return null;
        }

        public Obj FirstCamera { get { foreach (var o in Objects) if (o.IsCamera) return o; return null; } }

        static int Tag(byte[] d, string tag)
        {
            var t = Encoding.ASCII.GetBytes(tag);
            for (int i = 0; i + 4 <= d.Length; i++)
                if (d[i] == t[0] && d[i + 1] == t[1] && d[i + 2] == t[2] && d[i + 3] == t[3]) return i;
            return -1;
        }

        static uint U32(byte[] d, int o) => BitConverter.ToUInt32(d, o);
        static float F32(byte[] d, int o) => BitConverter.ToSingle(d, o);

        public static CineScene Parse(byte[] d, string name = "")
        {
            var sc = new CineScene { Name = name ?? "" };
            if (d == null || d.Length < 16 || d[0] != 'S' || d[1] != 'C' || d[2] != 'E' || d[3] != '!') return sc;
            // REFS: Clip-Namen (Art 2)
            var refs = new List<string>();
            int r = Tag(d, "REFS");
            if (r >= 0 && r + 12 <= d.Length)
            {
                int cnt = (int)U32(d, r + 8), p = r + 12;
                for (int i = 0; i < cnt && p + 4 <= d.Length; i++)
                {
                    int kind = d[p], size = d[p + 2] | (d[p + 3] << 8);
                    string s = null;
                    if (kind == 2 && size > 12 && p + size <= d.Length)
                    {
                        int e = p + 12; while (e < p + size && d[e] != 0) e++;
                        s = Encoding.ASCII.GetString(d, p + 12, e - (p + 12));
                    }
                    refs.Add(s);
                    if (size < 4) break;
                    p += size;
                }
            }
            int data = Tag(d, "DATA");
            if (data < 0) return sc;
            int b = data + 8;
            if (b + 104 > d.Length) return sc;
            sc.Duration = F32(d, b);
            int nobj = (int)U32(d, b + 4);
            int o = b + 104;
            for (int k = 0; k < nobj && o + 20 <= d.Length; k++)
            {
                int size = (int)U32(d, o), hs = (int)U32(d, o + 4);
                uint cls = U32(d, o + 8);
                int nch = (int)U32(d, o + 16);
                if (size <= 0 || o + size > d.Length) break;
                var ob = new Obj { IsCamera = cls == ClassCamera };
                int ne = o + 20; while (ne < o + 52 && d[ne] != 0) ne++;
                ob.Name = Encoding.ASCII.GetString(d, o + 20, ne - (o + 20));
                if (hs >= 60) ob.Bind = BitConverter.ToUInt64(d, o + 52);
                int q = o + hs;
                for (int c = 0; c < nch && q + 20 <= o + size; c++)
                {
                    int mode = d[q] | (d[q + 1] << 8);
                    int csz = (int)U32(d, q + 4);
                    uint cn = U32(d, q + 8);
                    int n = (int)U32(d, q + 12);
                    if (csz < 20 || q + csz > o + size) break;
                    int v = q + 20 + 4 * n;   // Werte nach end + times[n]
                    if (n > 0 && v + 4 <= q + csz)
                    {
                        if (cn == ChPosition && mode == 1 && v + 12 <= q + csz) { ob.Position = new Vector3(F32(d, v), F32(d, v + 4), F32(d, v + 8)); ob.HasPosition = true; }
                        else if (cn == ChHeading && mode == 0 && v + 16 <= q + csz)
                            ob.Rotation = new Quaternion(-F32(d, v), -F32(d, v + 4), -F32(d, v + 8), F32(d, v + 12));
                        else if (cn == ChFov) ob.Fov = F32(d, v);
                        else if (cn == ChAnimation)
                        {
                            int ri = (int)U32(d, v);
                            if (ri >= 0 && ri < refs.Count && !string.IsNullOrEmpty(refs[ri]) && refs[ri] != "none") ob.Clip = refs[ri];
                        }
                    }
                    q += csz;
                }
                if (cls == ClassCamera || cls == ClassActor) sc.Objects.Add(ob);
                o += size;
            }
            return sc;
        }

        /// <summary>Alle .sce einer Bank (Name ohne Endung -> Szene).</summary>
        public static Dictionary<string, CineScene> LoadAll(BnkArchive bank)
        {
            var res = new Dictionary<string, CineScene>(StringComparer.OrdinalIgnoreCase);
            if (bank == null) return res;
            foreach (var e in bank.All(".sce"))
            {
                try { var s = Parse(bank.Read(e), e.Name); res[System.IO.Path.GetFileNameWithoutExtension(e.Name)] = s; }
                catch (Exception ex) { Debug.LogWarning("[TDU2] .sce " + e.Name + ": " + ex.Message); }
            }
            return res;
        }

        /// <summary>.anm-Clip aus einer Bank (Interieur: clot_*, dr_*, Figuren-Posen); null = fehlt.</summary>
        public static Avatars.AnmClip LoadClip(BnkArchive bank, string name)
        {
            if (bank == null || string.IsNullOrEmpty(name)) return null;
            try { var e = bank.Find(name + ".anm"); return e != null ? Avatars.AnmClip.Parse(bank.Read(e), name) : null; }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Clip " + name + ": " + ex.Message); return null; }
        }

        /// <summary>Weltlage eines Objekts der Szene (Rahmen = Interieur-Szene, Spielraum).</summary>
        public static void ToWorld(Transform scene, Obj ob, out Vector3 pos, out Quaternion rot)
        {
            pos = scene.TransformPoint(ob.Position);
            var f = scene.TransformVector(ob.Rotation * Vector3.forward);
            var u = scene.TransformVector(ob.Rotation * Vector3.up);
            rot = Quaternion.LookRotation(f.sqrMagnitude > 1e-8f ? f.normalized : Vector3.forward, u.sqrMagnitude > 1e-8f ? u.normalized : Vector3.up);
        }
    }
}
