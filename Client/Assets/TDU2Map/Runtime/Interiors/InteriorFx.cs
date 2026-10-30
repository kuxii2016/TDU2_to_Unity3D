using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using TDU2.Formats;
using TDU2.IO;
using TDU2.Racing;
using TDU2.World;
using UnityEngine;

namespace TDU2.Interior
{
    /// <summary>
    /// Effekte der Interieur-Szenen, die beim Aufbau der Welt-Objekte fehlen:
    ///   * Textur-Animationen (.uva in der Interieur-Bank, verknüpft über die .2dm: MAT.-Ebene +28 -> MTP. +20 -> UVA.-Schlüssel),
    ///     z.B. Pulse_DarkBlue (Lichtbänder), artWorks (Bildschirme) – wie RaceProps, je Ebene auf _MainTex_ST bzw. _DetailTex_ST.
    ///   * Glas: Materialien mit Glas-Namen (glass, vitre, verre, window …) immer transparent.
    /// Leuchtende (additive) Materialien werden beim Aufbau eingeschaltet (MaterialFactory.ShowGlowMaterials, siehe TDU2Interiors).
    /// </summary>
    public static class InteriorFx
    {
        public sealed class UvaLink { public string Material, Texture; public bool Detail; public RaceUva Anim; }
        public sealed class LightDef { public Vector3 Pos; public Color Color; public float Intensity, Range; }
        sealed class Prepared { public List<UvaLink> Uva = new List<UvaLink>(); public List<LightDef> Lights = new List<LightDef>(); public Dictionary<string, TextureDef> Night = new Dictionary<string, TextureDef>(StringComparer.OrdinalIgnoreCase); public Dictionary<string, string> MatNight = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); public Dictionary<string, MatInfo> Mats = new Dictionary<string, MatInfo>(StringComparer.OrdinalIgnoreCase); public HashSet<string> Helpers = new HashSet<string>(StringComparer.OrdinalIgnoreCase); public HashSet<string> WaterMats = new HashSet<string>(StringComparer.OrdinalIgnoreCase); public TextureDef Cube; }
        /// <summary>Shader-Parameter eines Materials aus dem MAT.-Shaderblock (+768).</summary>
        sealed class MatInfo { public bool Furniture; public Vector4 BaseColor = Vector4.one; public float Bias; }
        static readonly ConcurrentDictionary<string, Prepared> _prep = new ConcurrentDictionary<string, Prepared>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Innenraum-Lightmaps (lm_*) ohne Sonne/Schatten darstellen (das Licht ist eingebacken).</summary>
        public static bool UnlitLightmaps = true;
        /// <summary>Nacht-Lightmaps (lm_*_night) verwenden: null = nach Sonnenstand, true/false = erzwingen.</summary>
        public static bool? NightLightmaps = null;
        public static float LightIntensity = 1.4f;
        /// <summary>Licht-Entities der Szene als Unity-Punktlichter (aus: im Spiel nur für Figuren, kostet Leistung).</summary>
        public static bool AddSceneLights = true;
        /// <summary>Bodenspiegelung wie im Original (TD_Furnitures gProjectiveMapSampler): zweite Kamera, gespiegelt an der Bodenebene.</summary>
        public static bool PlanarReflections = true;
        /// <summary>Auflösung des Spiegelbilds relativ zum Bildschirm.</summary>
        public static float ReflectionScale = 0.5f;
        /// <summary>Mindest-Spiegelanteil spiegelnder Böden (setzt TDU2Interiors je Raum, z.B. Garage).</summary>
        public static float MinFloorReflection = 0f;

        static readonly ConcurrentDictionary<string, List<UvaLink>> _links = new ConcurrentDictionary<string, List<UvaLink>>(StringComparer.OrdinalIgnoreCase);
        // Welt (Sektoren, Spot-Exteriors): bleibt erhalten. Eine Exterior-Bank steht auf Hawaii oft mehrfach auf der Karte
        // (EGAMHA ×4, EAS_A ×3 …) und wird von parallelen Lade-Threads vorbereitet – mit dem einmaligen Entnehmen aus _links
        // bekam nur das erste Gebäude seine Animationen (bzw. ein Interieur/Kino derselben Bank nahm sie weg).
        static readonly ConcurrentDictionary<string, List<UvaLink>> _worldLinks = new ConcurrentDictionary<string, List<UvaLink>>(StringComparer.OrdinalIgnoreCase);
        static readonly ulong SemDetail = Bin.KeyFromString("DETAIL"), SemDetail1 = Bin.KeyFromString("DETAIL1"), SemColor = Bin.KeyFromString("COLOR");
        static readonly ulong SemReflection = Key8("REFLECTION");
        static ulong Key8(string s)
        {
            var k = new byte[8];
            for (int i = 0; i < s.Length; i++) k[i % 8] = (byte)(k[i % 8] + char.ToUpperInvariant(s[i]));
            return BitConverter.ToUInt64(k, 0);
        }
        // erst im Hauptthread anlegen (Prepare läuft im Lade-Thread, Unity-Objekte dürfen dort nicht entstehen)
        static MaterialPropertyBlock _mpb;
        static Material _poolWater;
        /// <summary>Farbe/Deckkraft des Pool-Wassers in Häusern (Alpha = Deckkraft).</summary>
        public static Color PoolColor = new Color(0.10f, 0.42f, 0.55f, 0.55f);
        static int _idNoClip = -1;
        const ulong SemNightLm = 0x47494CA489949D96UL;   // NIGHTLIGHTMAP (gekürzter Schlüssel)
        const ulong GuidFurnitures = 0xd5013ed0db79afafUL;  // TD_Furnitures.sob
        const ulong GuidWater = 0x0123000000000000UL;       // TD_Water.sob (Pools: COLOR/DETAIL sind Normalmaps, nicht darstellbar)
        const ulong KeyBaseColor = 0x4f4c4f4345534194UL;    // Key8("baseColor")
        const ulong KeyBiasCoef = 0x46454f4353414988UL;     // Key8("biasCoef"): Fresnel-Grundwert der Spiegelung

        /// <summary>Im Lade-Thread: .uva der Bank lesen und den Materialien zuordnen.</summary>
        public static void Prepare(BnkArchive bank)
        {
            if (bank == null || string.IsNullOrEmpty(bank.FilePath)) return;
            PrepareUva(bank);
            PrepareRest(bank);
        }

        /// <summary>Im Lade-Thread, nur die Textur-Animationen (.uva) – auch für Welt-Sektoren und Spot-Exteriors.</summary>
        public static void PrepareUva(BnkArchive bank)
        {
            if (bank == null || string.IsNullOrEmpty(bank.FilePath)) return;
            var list = new List<UvaLink>();
            try
            {
                var byKey = new Dictionary<ulong, RaceUva>();
                foreach (var e in bank.Entries)
                {
                    if (e.Extension != ".uva") continue;
                    try { var u = RaceUva.Parse(bank.Read(e), System.IO.Path.GetFileNameWithoutExtension(e.Name)); if (u != null) byKey[u.Key] = u; } catch (Exception) { }
                }
                if (byKey.Count > 0)
                {
                    var texName = new Dictionary<ulong, string>();
                    foreach (var e in bank.Entries)
                        if (e.Extension == ".2db") { var hd = bank.ReadPartial(e, 0, 0x28); ulong k = TextureDef.PeekKey(hd); if (k != 0) texName[k] = System.IO.Path.GetFileNameWithoutExtension(e.Name); }
                    foreach (var e in bank.Entries)
                    {
                        if (e.Extension != ".2dm") continue;
                        var d = bank.Read(e);
                        for (int m = 0; m + 0x110 <= d.Length; m++)
                        {
                            if (d[m] != 'M' || d[m + 1] != 'A' || d[m + 2] != 'T' || d[m + 3] != '.') continue;
                            string name = Bin.CString(d, m + 0x20, 32);
                            int n = Math.Min(16, (int)Bin.U16(d, m + 0x10C));
                            for (int i = 0; i < n; i++)
                            {
                                int p = m + 0x110 + i * 32;
                                if (p + 32 > d.Length) break;
                                ulong sem = Bin.U64(d, p), tk = Bin.U64(d, p + 8);
                                int mtp = (int)Bin.U32(d, p + 28);
                                if (mtp <= 0 || mtp + 24 > d.Length) continue;
                                int up = (int)Bin.U32(d, mtp + 20);
                                if (up <= 0 || up + 8 > d.Length) continue;
                                if (!byKey.TryGetValue(Bin.U64(d, up), out var anim)) continue;
                                texName.TryGetValue(tk, out var tn);
                                list.Add(new UvaLink { Material = name, Texture = tn, Detail = sem == SemDetail || sem == SemDetail1, Anim = anim });
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { DebugManager.Error("Interieur-UVA: " + ex.Message); }
            if (list.Count > 0) { _links[bank.FilePath] = list; _worldLinks[bank.FilePath] = list; }
            else { _links.TryRemove(bank.FilePath, out _); _worldLinks.TryRemove(bank.FilePath, out _); }
        }

        static void PrepareRest(BnkArchive bank)
        {
            var prep = new Prepared();
            // Lichtquellen der Szene (ArrayOf_Entity__Light: Diffuse, Intensity, DistanceCutOff)
            try
            {
                var ee = bank.Find("entities.xmb");
                var root = ee != null ? XmbFile.Parse(bank.Read(ee)) : null;
                var et = root?.Get("ExportType_Entity");
                var arr = et?.Get("ArrayOf_Entity__Light");
                if (arr != null)
                    foreach (var it in arr.Items)
                    {
                        var ops = it.Get("orient_pos_scale");
                        var pos = ops != null ? (Vector3)World.BankModel.EntityMatrix(ops).GetColumn(3) : Vector3.zero;
                        var df = it.Get("Diffuse");
                        var col = df != null ? new Color(df.F("X", 1f), df.F("Y", 1f), df.F("Z", 1f), 1f) : Color.white;
                        prep.Lights.Add(new LightDef { Pos = pos, Color = col, Intensity = it.F("Intensity", 1f), Range = Mathf.Max(3f, it.F("DistanceCutOff", 15f)) });
                    }
            }
            catch (Exception ex) { DebugManager.Error("Interieur-Lights: " + ex.Message); }
            // Nacht-Lightmaps: die Textur der NIGHTLIGHTMAP-Ebene jedes Materials (Semantik 0x47494CA489949D96).
            // Namen sind frei: lm_gph_l3a_night, lm_igamha_dlc, irs_a_night, lm_icd_us1 ... ; COLOR ist lm_*/fake_*/<bank>_day.
            // Dazu: Umgebungs-Cubemap der REFLECTION-Ebene (cubecar, cubemap_iorc ...) und Hilfsmaterialien ohne Oberfläche.
            try
            {
                var texEntry = new Dictionary<ulong, BnkEntry>();
                foreach (var e in bank.Entries)
                    if (e.Extension == ".2db") { var hd = bank.ReadPartial(e, 0, 0x28); ulong k = TextureDef.PeekKey(hd); if (k != 0) texEntry[k] = e; }
                var cubeCount = new Dictionary<ulong, int>();
                foreach (var e in bank.Entries)
                {
                    if (e.Extension != ".2dm") continue;
                    var d = bank.Read(e);
                    for (int m = 0; m + 0x110 <= d.Length; m++)
                    {
                        if (d[m] != 'M' || d[m + 1] != 'A' || d[m + 2] != 'T' || d[m + 3] != '.') continue;
                        string name = Bin.CString(d, m + 0x20, 32);
                        int n = Math.Min(16, (int)Bin.U16(d, m + 0x10C));
                        bool surface = false, refl = false;
                        for (int i = 0; i < n; i++)
                        {
                            int p = m + 0x110 + i * 32;
                            if (p + 32 > d.Length) break;
                            ulong sem = Bin.U64(d, p), tk = Bin.U64(d, p + 8);
                            if (tk == 0) continue;
                            // Oberfläche zählt auch, wenn die Textur in einer anderen Bank liegt
                            if (sem == SemColor || sem == SemDetail || sem == SemDetail1) { surface = true; continue; }
                            if (sem == SemReflection) refl = true;
                            if (!texEntry.TryGetValue(tk, out var te)) continue;
                            string stem = System.IO.Path.GetFileNameWithoutExtension(te.Name);
                            if (sem == SemNightLm)
                            {
                                if (!prep.Night.ContainsKey(stem)) { var t = TextureDef.Parse(bank.Read(te), stem); if (t != null && !t.IsCube) prep.Night[stem] = t; }
                                if (prep.Night.ContainsKey(stem)) prep.MatNight[name] = stem;
                                continue;
                            }
                            if (sem == SemReflection) { cubeCount.TryGetValue(tk, out int c); cubeCount[tk] = c + 1; }
                        }
                        // ausschließlich REFLECTION, keine COLOR/DETAIL-Ebene (CubeCar): unsichtbarer Hilfskörper, keine Oberfläche/Kollision
                        if (refl && !surface) prep.Helpers.Add(name);
                    }
                }
                ulong best = 0; int bc = 0;
                foreach (var kv in cubeCount) if (kv.Value > bc) { best = kv.Key; bc = kv.Value; }
                if (best != 0 && texEntry.TryGetValue(best, out var ce))
                {
                    var t = TextureDef.Parse(bank.Read(ce), System.IO.Path.GetFileNameWithoutExtension(ce.Name));
                    if (t != null && t.IsCube) prep.Cube = t;
                }
            }
            catch (Exception ex) { DebugManager.Error("Interieur-Lightmaps: " + ex.Message); }
            // Shader-Parameter (MAT.-Nutzdaten ab Tag + 16: +768 Shader-Set, +778/+780 Parameterzahl, +832 Parameterköpfe, dann Werte)
            try
            {
                foreach (var e in bank.Entries)
                {
                    if (e.Extension != ".2dm") continue;
                    var d = bank.Read(e);
                    for (int m = 0; m + 16 + 832 <= d.Length; m++)
                    {
                        if (d[m] != 'M' || d[m + 1] != 'A' || d[m + 2] != 'T' || d[m + 3] != '.') continue;
                        int pay = m + 16;
                        ulong sg = Bin.U64(d, pay + 768);
                        if (sg == GuidWater) { prep.WaterMats.Add(Bin.CString(d, m + 0x20, 32)); continue; }
                        if (sg != GuidFurnitures) continue;
                        string name = Bin.CString(d, m + 0x20, 32);
                        var info = new MatInfo { Furniture = true };
                        int n = Bin.U16(d, pay + 778) + Bin.U16(d, pay + 780);
                        if (n <= 0 || n > 64) { prep.Mats[name] = info; continue; }
                        int h = pay + 832, v = h + 16 * n;
                        for (int i = 0; i < n; i++)
                        {
                            int sz = (int)Bin.U32(d, h + 16 * i + 12);
                            if (sz <= 0 || sz > 64 || v + sz > d.Length) break;
                            if (Bin.U64(d, h + 16 * i) == KeyBaseColor && sz >= 16)
                                info.BaseColor = new Vector4(BitConverter.ToSingle(d, v), BitConverter.ToSingle(d, v + 4), BitConverter.ToSingle(d, v + 8), BitConverter.ToSingle(d, v + 12));
                            else if (Bin.U64(d, h + 16 * i) == KeyBiasCoef && sz >= 4)
                                info.Bias = BitConverter.ToSingle(d, v);
                            v += sz;
                        }
                        prep.Mats[name] = info;
                    }
                }
            }
            catch (Exception ex) { DebugManager.Error("Interieur-Materialparameter: " + ex.Message); }
            _prep[bank.FilePath] = prep;
        }

        /// <summary>
        /// Häufigste Höhe (Welt, 5-cm-Klassen, gewichtet mit der Dreiecksfläche) der waagerechten, nach oben zeigenden Dreiecke
        /// der Kandidaten; <paramref name="host"/> = Renderer mit dem größten Anteil daran.
        /// </summary>
        /// <summary>
        /// Diagnose (Protokoll): alle Materialien des Interieurs mit Shader, Warteschlange (transparent/additiv), Haupttextur und
        /// Auffälligkeiten (Wasser, Glas, Leuchten, UVs weit außerhalb 0..1, Meshes ohne Normalen) – um Darstellungsfehler ohne
        /// Rückfrage zu finden.
        /// </summary>
        static void LogInventory(Transform root, Prepared prep)
        {
            try
            {
                var seen = new HashSet<Material>();
                var sb = new System.Text.StringBuilder();
                int n = 0, nTrans = 0, nOdd = 0;
                foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                {
                    var mf = r.GetComponent<MeshFilter>(); var mesh = mf != null ? mf.sharedMesh : null;
                    string meshInfo = "";
                    if (mesh != null && mesh.isReadable)
                    {
                        var uv = new List<Vector2>(); mesh.GetUVs(0, uv);
                        float mx = 0f; foreach (var u in uv) mx = Mathf.Max(mx, Mathf.Abs(u.x), Mathf.Abs(u.y));
                        bool noN = mesh.normals == null || mesh.normals.Length == 0;
                        if (mx > 50f || noN) { meshInfo = " MESH-AUFFÄLLIG(" + mesh.name + ": UV bis " + mx.ToString("0") + (noN ? ", keine Normalen" : "") + ", " + mesh.vertexCount + " V)"; nOdd++; }
                    }
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null || (!seen.Add(m) && meshInfo.Length == 0)) continue;
                        bool trans = m.renderQueue >= 2450;
                        if (trans) nTrans++;
                        string tag = m == MaterialFactory.Water || (m.name ?? "").Contains("Water") ? " WASSER" : IsGlass(m.name) ? " GLAS" : "";
                        if (prep != null && prep.WaterMats.Contains(m.name ?? "")) tag += " (Pool)";
                        if (!trans && tag.Length == 0 && meshInfo.Length == 0) continue;   // nur Auffälliges/Transparentes ausführlich
                        if (n++ >= 300) continue;
                        sb.Append("\n    ").Append(r.name).Append(" / ").Append(m.name).Append(" [").Append(m.shader != null ? m.shader.name : "-")
                          .Append(", Queue ").Append(m.renderQueue).Append(", Tex ").Append(m.HasProperty("_MainTex") && m.mainTexture != null ? m.mainTexture.name : "-")
                          .Append(r.enabled ? "" : ", aus").Append(']').Append(tag).Append(meshInfo);
                    }
                }
                DebugManager.Info("Material-Inventar " + root.name + ": " + seen.Count + " Materialien, " + nTrans + " transparent/additiv, " + nOdd + " strange Meshes" + sb);
            }
            catch (Exception ex) { DebugManager.Error("Material-Inventar: " + ex.Message); }
        }

        /// <summary>Diagnose der letzten Bodensuche (Höhenklassen mit Fläche).</summary>
        public static string LastPlaneInfo = "";

        static bool FindFloorPlane(List<Renderer> cands, out Renderer host, out float y)
        {
            host = null; y = 0f;
            var bins = new Dictionary<int, float>();
            var per = new Dictionary<Renderer, Dictionary<int, float>>();
            foreach (var r in cands)
            {
                Mesh mesh = null;
                if (r is MeshRenderer) { var mf = r.GetComponent<MeshFilter>(); mesh = mf != null ? mf.sharedMesh : null; }
                else if (r is SkinnedMeshRenderer sk) mesh = sk.sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                var v = mesh.vertices; var tr = r.transform;
                var mine = new Dictionary<int, float>();
                for (int sm = 0; sm < mesh.subMeshCount; sm++)
                {
                    var t = mesh.GetTriangles(sm);
                    for (int i = 0; i + 2 < t.Length; i += 3)
                    {
                        Vector3 a = tr.TransformPoint(v[t[i]]), b = tr.TransformPoint(v[t[i + 1]]), c = tr.TransformPoint(v[t[i + 2]]);
                        var n = Vector3.Cross(b - a, c - a);
                        float area2 = n.magnitude;
                        if (area2 < 1e-6f) continue;
                        if (Mathf.Abs(n.y) / area2 < 0.97f) continue;   // nur waagerecht (beide Windungen: Spiegelung der Szene)
                        float yy = (a.y + b.y + c.y) / 3f;
                        int k = Mathf.RoundToInt(yy / 0.05f);
                        bins.TryGetValue(k, out float w); bins[k] = w + area2 * 0.5f;
                        mine.TryGetValue(k, out float w2); mine[k] = w2 + area2 * 0.5f;
                    }
                }
                per[r] = mine;
            }
            int best = 0; float bw = 0f;
            foreach (var kv in bins) if (kv.Value > bw) { bw = kv.Value; best = kv.Key; }
            if (bw < 4f) { LastPlaneInfo = "keine waagerechte Fläche >= 4 m²"; return false; }   // unter 4 m² waagerechter Fläche: kein Boden
            // Boden = die UNTERSTE große waagerechte Ebene (Decken, Galerien, Dachflächen sind gleich groß oder größer und lagen
            // sonst oft vorn -> Spiegelung auf der Decke statt dem Boden)
            int lowest = best;
            foreach (var kv in bins) if (kv.Value >= bw * 0.3f && kv.Key < lowest) lowest = kv.Key;
            var top = new List<KeyValuePair<int, float>>(bins); top.Sort((p, q) => q.Value.CompareTo(p.Value));
            var sbp = new System.Text.StringBuilder();
            for (int i = 0; i < top.Count && i < 6; i++) sbp.Append(i == 0 ? "" : ", ").Append((top[i].Key * 0.05f).ToString("0.00")).Append(" m: ").Append(top[i].Value.ToString("0")).Append(" m²");
            LastPlaneInfo = "Ebenen (Welt-Y): " + sbp + " -> größte " + (best * 0.05f).ToString("0.00") + ", gewählt unterste " + (lowest * 0.05f).ToString("0.00");
            best = lowest;
            float hw = 0f;
            foreach (var kv in per)
            {
                float w = 0f;
                for (int d = -1; d <= 1; d++) if (kv.Value.TryGetValue(best + d, out float x)) w += x;
                if (w > hw) { hw = w; host = kv.Key; }
            }
            // genaue Höhe: flächengewichtetes Mittel der Nachbarklassen
            float sw = 0f, sy = 0f;
            for (int d = -1; d <= 1; d++) if (bins.TryGetValue(best + d, out float x)) { sw += x; sy += x * (best + d) * 0.05f; }
            y = sw > 0f ? sy / sw : best * 0.05f;
            return host != null;
        }

        /// <summary>Im Hauptthread nach dem Aufbau: Animationen starten, Glas transparent machen.</summary>
        public static void Apply(string bankPath, Transform root)
        {
            if (root == null) return;
            List<UvaLink> links = null;
            if (!string.IsNullOrEmpty(bankPath)) _links.TryRemove(bankPath, out links);
            Prepared prep = null;
            if (!string.IsNullOrEmpty(bankPath)) _prep.TryRemove(bankPath, out prep);
            // Interieurs: die eigentliche Lichtberechnung steckt in lm_*_night (1024er mit Schatten/Lampen), lm_* ist ein weißer Platzhalter
            bool night = NightLightmaps ?? true;
            var nightTex = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
            OwnedMaterials owned = null;
            if (prep != null && night)
                foreach (var kv in prep.Night)
                {
                    var t = kv.Value.CreateTexture(TextureWrapMode.Clamp, TextureWrapMode.Clamp);
                    if (t == null) continue;
                    t.name = kv.Key;
                    nightTex[kv.Key] = t;
                    if (owned == null) { owned = root.GetComponent<OwnedMaterials>(); if (owned == null) owned = root.gameObject.AddComponent<OwnedMaterials>(); }
                    owned.Textures.Add(t);
                }
            int nLm = 0;
            RaceUvaAnimator anim = null;
            var done = new HashSet<Material>();
            var glassCache = new Dictionary<Material, Material>();
            int nUva = 0, nGlass = 0, nRefl = 0;
            Renderer mirrorFloor = null;
            var mirrorCands = new List<Renderer>();
            int nHelper = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
            {
                // Teil des Interieurs: nicht ausschneiden (pro Renderer, gemeinsame Materialien wie Wasser/Fallback bleiben unberührt)
                if (_mpb == null) { _mpb = new MaterialPropertyBlock(); _idNoClip = Shader.PropertyToID("_TDU2NoClip"); }
                r.GetPropertyBlock(_mpb); _mpb.SetFloat(_idNoClip, 1f); r.SetPropertyBlock(_mpb); _mpb.Clear();
                var mats = r.sharedMaterials; bool changed = false;
                if (prep != null && prep.Helpers.Count > 0)
                {
                    bool helper = mats.Length > 0;
                    foreach (var hm in mats) if (hm == null || !prep.Helpers.Contains(hm.name ?? "")) { helper = false; break; }
                    if (helper) { r.enabled = false; var mc = r.GetComponent<Collider>(); if (mc != null) UnityEngine.Object.Destroy(mc); nHelper++; continue; }
                }
                for (int i = 0; i < mats.Length; i++)
                {
                    var m = mats[i];
                    if (m == null) continue;
                    // Pool/Brunnen ("Water"): MaterialFactory liefert die fast durchsichtige Meeresfläche -> eigene, sichtbare Wasserfläche
                    if (m == MaterialFactory.Water || MaterialFactory.IsShared(m) && m.name == "TDU2_PoolWater" || (prep != null && prep.WaterMats.Contains(m.name ?? "")))
                    {
                        var ws = MaterialFactory.Water;
                        if (ws == null) continue;
                        if (_poolWater == null || _poolWater.shader != ws.shader)
                        {
                            _poolWater = new Material(ws) { name = "TDU2_PoolWater" };
                            if (_poolWater.HasProperty("_Color")) _poolWater.SetColor("_Color", PoolColor);
                            // Meer-Shader nimmt die Farbe sonst vom Untergrund (fast unsichtbar) -> Pool: eigene Wasserfarbe mit Deckung
                            if (_poolWater.HasProperty("_PoolTint")) _poolWater.SetColor("_PoolTint", PoolColor);
                            if (_poolWater.HasProperty("_Glossiness")) _poolWater.SetFloat("_Glossiness", 0.92f);
                        }
                        mats[i] = _poolWater; changed = true;
                        continue;
                    }
                    if (IsGlass(m.name) && !done.Contains(m))
                    {
                        var g = MakeGlass(m, glassCache, root);
                        if (g != m) { mats[i] = g; changed = true; m = g; nGlass++; }
                    }
                    if (done.Contains(m)) continue;
                    done.Add(m);
                    if (m.HasProperty("_DetailAlpha")) m.SetFloat("_DetailAlpha", 1f);   // Pflanzen: Alpha-Test aus der DETAIL-Textur
                    // Lightmap-Materialien (COLOR = lm_* Platzhalter, NIGHTLIGHTMAP = lm_*_night): wie TD_Building
                    //   lerp(night.rgb, lit, 0.44) + night.rgb * night.a
                    var mt = m.mainTexture;
                    Texture2D nt = null;
                    if (prep != null && prep.MatNight.TryGetValue(m.name ?? "", out var stem)) nightTex.TryGetValue(stem, out nt);
                    if (nt == null && nightTex.Count == 1 && mt != null && mt.name != null
                        && (mt.name.StartsWith("lm_", StringComparison.OrdinalIgnoreCase) || mt.name.StartsWith("fake_", StringComparison.OrdinalIgnoreCase)))
                        foreach (var v in nightTex.Values) nt = v;
                    if (nt != null && m.HasProperty("_NightTex"))
                    {
                        m.SetTexture("_NightTex", nt);
                        m.SetFloat("_NightMode", 1f);
                        // Original: Albedo = COLOR x DETAIL x DETAIL2 ohne Verstärkung (das Licht kommt aus der Nacht-Lightmap)
                        if (m.HasProperty("_DetailBoost")) m.SetFloat("_DetailBoost", 1f);
                        if (m.HasProperty("_Brightness")) m.SetFloat("_Brightness", 1f);
                        nLm++;
                    }
                    if (prep != null && prep.Mats.TryGetValue(m.name ?? "", out var mi) && mi.Furniture)
                    {
                        if (m.HasProperty("_Color")) { var bc = m.GetColor("_Color"); m.SetColor("_Color", new Color(mi.BaseColor.x, mi.BaseColor.y, mi.BaseColor.z, bc.a)); }
                        // Spiegelanteil: 1 - baseColor.a; Garagenböden ("Reflection_Floor", Marmor …) haben dort fast 1, ihre Spiegelung
                        // kommt aus dem Fresnel-Grundwert biasCoef (0.33) – der Boden glänzt im Spiel deutlich
                        float mix = Mathf.Clamp01(Mathf.Max(1f - mi.BaseColor.w, mi.Bias * 0.8f));
                        if (mix > 0.001f) mix = Mathf.Max(mix, MinFloorReflection);   // Garage: Boden spiegelt im Spiel deutlich
                        if (PlanarReflections && mix > 0.001f && m.HasProperty("_ReflMix")) { m.SetFloat("_ReflMix", mix); nRefl++; }
                    }
                    if (links == null || links.Count == 0) continue;
                    string mn = m.name ?? "";
                    string tex = m.mainTexture != null ? m.mainTexture.name : null;
                    // Je Material eine Animation für die Hauptebene (COLOR) und eine für DETAIL – z.B. Säulen "Caustik_col":
                    // COLOR = fake_c (Platzhalter), die sichtbare Wasser-Kaustik liegt animiert auf DETAIL
                    UvaLink bestMain = null, bestDetail = null;
                    foreach (var l in links)
                    {
                        if (!mn.StartsWith(l.Material, StringComparison.OrdinalIgnoreCase)) continue;   // Namen im Spiel auf 15 Zeichen gekürzt
                        if (l.Detail) { if (bestDetail == null) bestDetail = l; continue; }
                        if (bestMain == null || (tex != null && string.Equals(l.Texture, tex, StringComparison.OrdinalIgnoreCase))) bestMain = l;
                    }
                    if (bestMain == null && bestDetail == null) continue;
                    if (anim == null) { anim = root.GetComponent<RaceUvaAnimator>(); if (anim == null) anim = root.gameObject.AddComponent<RaceUvaAnimator>(); }
                    if (bestDetail != null && m.HasProperty("_DetailTex"))
                    {
                        var du = root.GetComponent<DetailUva>(); if (du == null) du = root.gameObject.AddComponent<DetailUva>();
                        du.Add(m, bestDetail.Anim);
                        nUva++;
                    }
                    else if (bestDetail != null && bestMain == null) bestMain = bestDetail;
                    if (bestMain != null) { UseNeonMask(m); anim.Items.Add((m, bestMain.Anim)); nUva++; }
                }
                if (changed) r.sharedMaterials = mats;
                // größte waagerechte Fläche mit Spiegelung = Bodenebene
                if (PlanarReflections)
                    foreach (var m in mats)
                        if (m != null && m.HasProperty("_ReflMix") && m.GetFloat("_ReflMix") > 0.001f)
                        {
                            mirrorCands.Add(r);
                            break;
                        }
            }
            // Bodenebene: häufigste Höhe nach oben zeigender Flächen aller spiegelnden Meshes (Garagen: Boden mit Rampen, Sockeln,
            // Rinnen – die Bounds sind dort zu hoch bzw. ein anderes Stück ist größer)
            float planeY = 0f;
            if (PlanarReflections && MinFloorReflection > 0.001f)
            {
                // Garage: im Spiel spiegelt der ganze Boden deutlich. Die Bodenfliesen sind dort oft kein Möbel-Material mit
                // Spiegelanteil (nur Podeste o.ä. hatten einen) -> alle großen waagerechten Flächen als Kandidaten nehmen
                foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                {
                    if (!r.enabled || mirrorCands.Contains(r)) continue;
                    var b = r.bounds;
                    if (b.size.x * b.size.z < 40f) continue;
                    bool has = false;
                    foreach (var m in r.sharedMaterials) if (m != null && m.HasProperty("_ReflMix")) { has = true; break; }
                    if (has) mirrorCands.Add(r);
                }
            }
            if (mirrorCands.Count > 0 && FindFloorPlane(mirrorCands, out mirrorFloor, out planeY))
            {
                var im = mirrorFloor.GetComponent<InteriorMirror>();
                if (im == null) im = mirrorFloor.gameObject.AddComponent<InteriorMirror>();
                im.PlaneY = planeY;
                if (MinFloorReflection > 0.001f)
                {
                    // alles, was auf Bodenhöhe liegt, spiegelt (der Shader begrenzt die Spiegelung selbst auf die Bodenebene)
                    foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                    {
                        var b = r.bounds;
                        if (b.min.y > planeY + 0.1f || b.max.y < planeY - 0.1f) continue;
                        foreach (var m in r.sharedMaterials)
                            if (m != null && m.HasProperty("_ReflMix") && m.GetFloat("_ReflMix") < MinFloorReflection) { m.SetFloat("_ReflMix", MinFloorReflection); nRefl++; }
                    }
                }
                // Diagnose: welche Bodenmaterialien spiegeln, welche können es nicht (anderer Shader)
                var sbm = new System.Text.StringBuilder(); int nNo = 0;
                foreach (var r in root.GetComponentsInChildren<Renderer>(false))
                {
                    var b = r.bounds;
                    if (b.min.y > planeY + 0.1f || b.max.y < planeY - 0.1f) continue;
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null) continue;
                        bool can = m.HasProperty("_ReflMix");
                        if (!can) nNo++;
                        if (sbm.Length < 1500) sbm.Append(" | ").Append(r.name).Append('/').Append(m.name).Append(" [").Append(m.shader != null ? m.shader.name : "-").Append(can ? ", Refl " + m.GetFloat("_ReflMix").ToString("0.00") : ", KEIN Spiegel").Append(']');
                    }
                }
            }
            else if (PlanarReflections)
                DebugManager.Warning("Bodenspiegelung " + root.name + ": keine Bodenebene (" + mirrorCands.Count + " Kandidaten; " + LastPlaneInfo + ")");
            // Umgebungs-Cubemap des Interieurs (REFLECTION-Ebene, z.B. cubecar) als Reflection Probe: Autos spiegeln den Raum statt des Himmels
            bool probe = false;
            if (prep != null && prep.Cube != null)
            {
                var cube = CreateCube(prep.Cube);
                if (cube != null)
                {
                    if (owned == null) { owned = root.GetComponent<OwnedMaterials>(); if (owned == null) owned = root.gameObject.AddComponent<OwnedMaterials>(); }
                    owned.Others.Add(cube);
                    bool haveB = false; var wb = new Bounds();
                    foreach (var r in root.GetComponentsInChildren<Renderer>(false)) { if (!haveB) { wb = r.bounds; haveB = true; } else wb.Encapsulate(r.bounds); }
                    var go = new GameObject("TDU2 Interieur-Reflexion " + prep.Cube.Name);
                    go.transform.SetParent(root, false);
                    go.transform.position = haveB ? wb.center : root.position;
                    var rp = go.AddComponent<ReflectionProbe>();
                    rp.mode = UnityEngine.Rendering.ReflectionProbeMode.Custom;
                    rp.customBakedTexture = cube;
                    rp.size = (haveB ? wb.size : new Vector3(60f, 20f, 60f)) + Vector3.one * 4f;
                    rp.importance = 100;
                    rp.blendDistance = 1f;
                    rp.boxProjection = true;   // Spiegelung passend zur Raumlage (sonst "schwimmt" der Raum unnatürlich im Lack)
                    rp.intensity = 0.7f;
                    probe = true;
                }
            }
            // Echtzeit-Lichter der Szene (RT_Avatar_*) beleuchten im Spiel nur die Figuren -> nicht als Unity-Lichter (kosten viel)
            int nLights = 0;
            if (prep != null && AddSceneLights)
                foreach (var l in prep.Lights)
                {
                    var go = new GameObject("Light " + nLights);
                    go.transform.SetParent(root, false);
                    go.transform.localPosition = l.Pos;
                    var li = go.AddComponent<Light>();
                    li.type = LightType.Point;
                    li.color = l.Color;
                    li.intensity = Mathf.Max(0.2f, l.Intensity) * LightIntensity;
                    li.range = l.Range;
                    li.shadows = LightShadows.None;
                    li.renderMode = LightRenderMode.Auto;
                    nLights++;
                }
            LogInventory(root, prep);
            DebugManager.Info("Interieur " + root.name + ": " + nUva + " Textur-Animation(en), " + nGlass + " Glas, " + nLm + " Lightmap-Material(ien)" + (night ? " (Nacht)" : "") + ", " + nRefl + " Spiegel-Material(ien)" + (mirrorFloor != null ? " (Boden " + mirrorFloor.name + ")" : "") + ", " + nHelper + " Hilfskörper ausgeblendet, Cubemap " + (probe ? prep.Cube.Name : "-") + ", " + nLights + " Licht(er)");
        }

        /// <summary>
        /// Hauptthread: nur die Textur-Animationen einer Bank auf die Renderer unter root legen (Welt-Sektoren, Exteriors).
        /// Gibt die Zahl animierter Materialien zurück.
        /// </summary>
        public static int ApplyUva(string bankPath, Transform root, bool keep = false)
        {
            if (root == null || string.IsNullOrEmpty(bankPath)) return 0;
            List<UvaLink> links;
            if (keep) { if (!_worldLinks.TryGetValue(bankPath, out links) && !_links.TryGetValue(bankPath, out links)) return 0; }
            else if (!_links.TryRemove(bankPath, out links) && !_worldLinks.TryGetValue(bankPath, out links)) return 0;
            if (links == null || links.Count == 0) return 0;
            RaceUvaAnimator anim = null; DetailUva du = null;
            var done = new HashSet<Material>();
            int n = 0;
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null || !done.Add(m)) continue;
                    if (!m.HasProperty("_MainTex")) continue;   // z.B. TDU2/Water
                    string mn = m.name ?? "";
                    string tex = m.mainTexture != null ? m.mainTexture.name : null;
                    UvaLink best = null;
                    foreach (var l in links)
                    {
                        if (string.IsNullOrEmpty(l.Material) || !mn.StartsWith(l.Material, StringComparison.OrdinalIgnoreCase)) continue;
                        if (best == null || (tex != null && string.Equals(l.Texture, tex, StringComparison.OrdinalIgnoreCase))) best = l;
                    }
                    if (best == null) continue;
                    if (best.Detail && m.HasProperty("_DetailTex"))
                    {
                        if (du == null) { du = root.GetComponent<DetailUva>(); if (du == null) du = root.gameObject.AddComponent<DetailUva>(); }
                        du.Add(m, best.Anim);
                    }
                    else
                    {
                        if (anim == null) { anim = root.GetComponent<RaceUvaAnimator>(); if (anim == null) anim = root.gameObject.AddComponent<RaceUvaAnimator>(); }
                        UseNeonMask(m);
                        anim.Items.Add((m, best.Anim));
                    }
                    n++;
                }
            return n;
        }

        /// <summary>
        /// Neon (TD_NEONDECAL): Farbe = Vertexfarbe x Leuchtmaske. Ohne Animation steht die Maske auf Schwarz (Neon aus);
        /// mit .uva die echte NEONGLOW-Maske einsetzen – die Animation verschiebt sie (Lauflicht, blinkende Birnen, Pfeile).
        /// </summary>
        static void UseNeonMask(Material m)
        {
            if (MaterialFactory.TryGetNeonMask(m, out var mask) && m.mainTexture != mask) m.mainTexture = mask;
        }

        /// <summary>Nacht: Sonne unter dem Horizont bzw. schwach.</summary>
        public static bool IsNight()
        {
            var sun = RenderSettings.sun;
            if (sun == null)
                foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) if (l.type == LightType.Directional && l.isActiveAndEnabled) { sun = l; break; }
            if (sun == null) return false;
            return sun.transform.forward.y > -0.05f || sun.intensity < 0.15f || !sun.isActiveAndEnabled;
        }

        /// <summary>Cubemap aus einer .2db (DXT1/DXT5 | 0x40): je Seite die ganze Mip-Kette, Seiten +X, -X, +Y, -Y, +Z, -Z.</summary>
        static Cubemap CreateCube(TextureDef t)
        {
            try
            {
                int fmt = t.Format & ~0x40;
                TextureFormat tf; int bpb;
                if (fmt == 0x84) { tf = TextureFormat.DXT1; bpb = 8; }
                else if (fmt == 0x88 || fmt == 0x86) { tf = TextureFormat.DXT5; bpb = 16; }
                else return null;
                int w = t.Width, mips = Math.Max(1, t.Mips);
                int faceSize = 0;
                for (int i = 0; i < mips; i++) { int s = Math.Max(1, w >> i); faceSize += Math.Max(1, (s + 3) / 4) * Math.Max(1, (s + 3) / 4) * bpb; }
                int baseOff = TextureDef.DataOffset;   // Data = ganze Datei, Bilddaten ab +80
                if (t.Data.Length < baseOff + faceSize * 6) return null;
                var cube = new Cubemap(w, tf, mips > 1) { name = t.Name, hideFlags = HideFlags.DontSave };
                int unityMips = cube.mipmapCount;
                for (int f = 0; f < 6; f++)
                {
                    int off = baseOff + f * faceSize;
                    for (int i = 0; i < mips; i++)
                    {
                        int s = Math.Max(1, w >> i); int sz = Math.Max(1, (s + 3) / 4) * Math.Max(1, (s + 3) / 4) * bpb;
                        if (i < unityMips) cube.SetPixelData(t.Data, i, (CubemapFace)f, off);
                        off += sz;
                    }
                }
                cube.Apply(false, true);
                return cube;
            }
            catch (Exception ex) { DebugManager.Error("Interieur-Cubemap " + t.Name + ": " + ex.Message); return null; }
        }

        static bool IsGlass(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            return n.Contains("glass") || n.Contains("vitre") || n.Contains("verre") || n.Contains("window") || n.Contains("fenetre") || n.Contains("glace");
        }


        /// <summary>Glas-Material transparent (TDU2/ObjectTransparent, Alpha ~0,3 wenn die Textur selbst deckend ist).</summary>
        static Material MakeGlass(Material m, Dictionary<Material, Material> cache, Transform root)
        {
            if (cache.TryGetValue(m, out var g) && g != null) return g;
            var sh = MaterialFactory.Shaders.ObjectTransparent ?? Shader.Find("TDU2/ObjectTransparent");
            if (sh == null) return m;
            bool already = m.shader == sh;
            g = already ? m : new Material(sh) { name = m.name };
            if (!already)
            {
                if (m.mainTexture != null) g.mainTexture = m.mainTexture;
                if (m.HasProperty("_DetailTex") && m.GetTexture("_DetailTex") != null)
                {
                    g.SetTexture("_DetailTex", m.GetTexture("_DetailTex"));
                    g.SetFloat("_DetailMode", 1f);
                    g.SetFloat("_DetailUV", m.HasProperty("_DetailUV") ? m.GetFloat("_DetailUV") : 0f);
                    g.SetFloat("_DetailBoost", m.HasProperty("_DetailBoost") ? m.GetFloat("_DetailBoost") : 2f);
                }
                g.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                g.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                g.SetFloat("_Cull", 0f);
                g.SetFloat("_Lit", 1f);
                g.SetFloat("_Intensity", 1f);
            }
            // deckende Texturen (DXT1 / Alpha aus der Lightmap) -> feste Durchsicht
            var at = g.GetFloat("_AlphaSource") > 0.5f ? g.GetTexture("_DetailTex") as Texture2D : g.mainTexture as Texture2D;
            bool opaqueTex = at == null || at.format == TextureFormat.DXT1 || at.format == TextureFormat.RGB24;
            var c = g.HasProperty("_Color") ? g.GetColor("_Color") : Color.white;
            if (opaqueTex || c.a > 0.95f) { c.a = opaqueTex ? 0.3f : c.a; g.SetColor("_Color", c); }
            g.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            cache[m] = g;
            if (!already) { var own = root.GetComponent<OwnedMaterials>(); if (own == null) own = root.gameObject.AddComponent<OwnedMaterials>(); own.List.Add(g); }
            return g;
        }

        /// <summary>
        /// Bodenspiegelung (im Original gProjectiveMapSampler von TD_Furnitures): rendert die Szene gespiegelt an der
        /// Bodenebene in eine Textur, die TDU2/Object über die Bildschirmposition liest (_TDU2PlanarRefl).
        /// </summary>
        public sealed class InteriorMirror : MonoBehaviour
        {
            static bool _rendering;
            Renderer _r;
            Camera _cam;
            RenderTexture _rt;
            static readonly int IdTex = Shader.PropertyToID("_TDU2PlanarRefl"), IdOn = Shader.PropertyToID("_TDU2PlanarOn"), IdY = Shader.PropertyToID("_TDU2PlanarY");

            Camera _src;
            int _srcFrame = -1;
            int _renders; float _nextLog;
            /// <summary>Höhe der Spiegelebene (Welt beim Aufbau; NaN = Oberkante der Bounds). Intern relativ zum Renderer gespeichert,
            /// damit der Gleitkomma-Ursprung (Verschieben der Welt) sie mitnimmt.</summary>
            public float PlaneY { get => _hasPlane ? transform.TransformPoint(new Vector3(0f, _planeLocalY, 0f)).y : float.NaN; set { _hasPlane = !float.IsNaN(value); if (_hasPlane) _planeLocalY = transform.InverseTransformPoint(new Vector3(transform.position.x, value, transform.position.z)).y; } }
            bool _hasPlane; float _planeLocalY;

            void Awake() { _r = GetComponent<Renderer>(); }

            // Gerendert wird nur für die Spielkamera (Camera.main) vor ihrem Culling – unabhängig davon, ob gerade die Boden-Fläche
            // mit dem InteriorMirror sichtbar ist (sonst bliebe ein altes Bild stehen, das "mit der Kamera wandert"). Alle anderen
            // Kameras (UI, Vorschau, Szene-Ansicht, Render-Texturen) sehen keine Spiegelung, statt das Bild einer fremden Kamera.
            void OnEnable()
            {
                Camera.onPreCull += PreCull;
                Camera.onPreRender += PreRender;
            }

            void OnDisable()
            {
                Camera.onPreCull -= PreCull;
                Camera.onPreRender -= PreRender;
                Shader.SetGlobalFloat(IdOn, 0f);
            }

            bool IsGameCamera(Camera c)
            {
                if (c == null || c == _cam || c.cameraType != CameraType.Game || c.targetTexture != null) return false;
                var main = Camera.main;
                return main == null || c == main;
            }

            bool _rejectLogged;

            void PreCull(Camera c)
            {
                if (!_rendering && c != null && c == Camera.main && !IsGameCamera(c) && !_rejectLogged)
                {
                    _rejectLogged = true;
                }
                if (_rendering || !isActiveAndEnabled || _r == null || !PlanarReflections || !IsGameCamera(c)) return;
                _rendering = true;
                try
                {
                    if (!Render(c)) { _srcFrame = -1; return; }
                    _src = c; _srcFrame = Time.frameCount;
                    _renders++;
                    if (Time.unscaledTime >= _nextLog)
                    {
                        _nextLog = Time.unscaledTime + 10f;
                    }
                }
                catch (Exception ex) { DebugManager.Error("Bodenspiegelung: " + ex.Message); enabled = false; }
                finally { _rendering = false; }
            }

            void PreRender(Camera c)
            {
                if (_rendering || c == _cam) return;
                bool on = PlanarReflections && c == _src && _srcFrame == Time.frameCount && _rt != null;
                Shader.SetGlobalFloat(IdOn, on ? 1f : 0f);
            }

            /// <summary>false = nicht gerendert (Kamera unter/auf der Spiegelebene, z.B. Kino-Kamera unter dem Boden: die schräge
            /// Projektion wäre entartet -> "Screen position out of view frustum").</summary>
            bool Render(Camera src)
            {
                var bb = _r.bounds;
                float planeY = _hasPlane ? PlaneY : bb.max.y;
                if (src.transform.position.y <= planeY + 0.05f || src.nearClipPlane <= 0f || src.farClipPlane <= src.nearClipPlane) return false;
                int w = Mathf.Max(64, (int)(src.pixelWidth * ReflectionScale)), h = Mathf.Max(64, (int)(src.pixelHeight * ReflectionScale));
                if (_rt == null || _rt.width != w || _rt.height != h)
                {
                    if (_rt != null) { _rt.Release(); Destroy(_rt); }
                    _rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "TDU2_PlanarRefl", hideFlags = HideFlags.DontSave };
                }
                if (_cam == null)
                {
                    var go = new GameObject("TDU2 Bodenspiegelung") { hideFlags = HideFlags.HideAndDontSave };
                    go.transform.SetParent(transform, false);
                    _cam = go.AddComponent<Camera>();
                    _cam.enabled = false;
                    _cam.cameraType = CameraType.Reflection;
                }
                _cam.CopyFrom(src);
                _cam.cameraType = CameraType.Reflection;
                _cam.targetTexture = _rt;
                _cam.useOcclusionCulling = false;
                _cam.allowHDR = false; _cam.allowMSAA = false;

                // Ebene: Oberkante des Bodens (Gleitkomma-Ursprung -> jedes Bild neu)
                var b = _r.bounds;
                var pos = new Vector3(0f, _hasPlane ? PlaneY : b.max.y, 0f);
                var normal = Vector3.up;
                float d = -Vector3.Dot(normal, pos);
                var plane = new Vector4(normal.x, normal.y, normal.z, d);
                var refl = Reflection(plane);
                _cam.worldToCameraMatrix = src.worldToCameraMatrix * refl;
                var cp = CameraSpacePlane(_cam, pos, normal, 0.02f);
                // eigene Projektion mit begrenzter Sichtweite (Story-Kameras: Near 0,05 m, Far bis 60 km -> die schräge Matrix
                // verliert sonst die Genauigkeit), dann schräge Clip-Ebene
                _cam.projectionMatrix = Matrix4x4.Perspective(src.fieldOfView, src.aspect, Mathf.Max(0.1f, src.nearClipPlane), Mathf.Min(src.farClipPlane, 3000f));
                _cam.projectionMatrix = _cam.CalculateObliqueMatrix(cp);
                var sp = src.transform.position;
                _cam.transform.position = refl.MultiplyPoint(sp);

                int oldPix = QualitySettings.pixelLightCount;
                var oldShadows = QualitySettings.shadows;
                QualitySettings.pixelLightCount = 0;
                QualitySettings.shadows = UnityEngine.ShadowQuality.Disable;
                GL.invertCulling = true;
                Shader.SetGlobalFloat(IdOn, 0f);   // der Boden selbst liegt hinter der Clip-Ebene; keine Rückkopplung
                try { _cam.Render(); }
                finally
                {
                    GL.invertCulling = false;
                    QualitySettings.pixelLightCount = oldPix;
                    QualitySettings.shadows = oldShadows;
                }
                Shader.SetGlobalTexture(IdTex, _rt);
                Shader.SetGlobalFloat(IdY, pos.y);
                Shader.SetGlobalFloat(IdOn, 1f);
                return true;
            }

            static Vector4 CameraSpacePlane(Camera c, Vector3 pos, Vector3 normal, float offset)
            {
                var m = c.worldToCameraMatrix;
                var cpos = m.MultiplyPoint(pos + normal * offset);
                var cn = m.MultiplyVector(normal).normalized;
                return new Vector4(cn.x, cn.y, cn.z, -Vector3.Dot(cpos, cn));
            }

            static Matrix4x4 Reflection(Vector4 p)
            {
                var m = Matrix4x4.identity;
                m.m00 = 1f - 2f * p.x * p.x; m.m01 = -2f * p.x * p.y; m.m02 = -2f * p.x * p.z; m.m03 = -2f * p.w * p.x;
                m.m10 = -2f * p.y * p.x; m.m11 = 1f - 2f * p.y * p.y; m.m12 = -2f * p.y * p.z; m.m13 = -2f * p.w * p.y;
                m.m20 = -2f * p.z * p.x; m.m21 = -2f * p.z * p.y; m.m22 = 1f - 2f * p.z * p.z; m.m23 = -2f * p.w * p.z;
                m.m30 = 0f; m.m31 = 0f; m.m32 = 0f; m.m33 = 1f;
                return m;
            }

            void OnDestroy()
            {
                Shader.SetGlobalFloat(IdOn, 0f);
                if (_rt != null) { _rt.Release(); Destroy(_rt); }
                if (_cam != null) Destroy(_cam.gameObject);
            }
        }

        /// <summary>Hier erzeugte Materialien mit der Szene zerstören.</summary>
        public sealed class OwnedMaterials : MonoBehaviour
        {
            public readonly List<Material> List = new List<Material>();
            public readonly List<Texture2D> Textures = new List<Texture2D>();
            public readonly List<Texture> Others = new List<Texture>();
            void OnDestroy() { foreach (var m in List) if (m != null) Destroy(m); foreach (var t in Textures) if (t != null) Destroy(t); foreach (var t in Others) if (t != null) Destroy(t); }
        }

        /// <summary>Textur-Animation auf der DETAIL-Ebene (_DetailTex_ST).</summary>
        public sealed class DetailUva : MonoBehaviour
        {
            readonly List<(Material m, RaceUva a)> _items = new List<(Material, RaceUva)>();
            public void Add(Material m, RaceUva a) { if (!_items.Exists(x => x.m == m)) _items.Add((m, a)); }
            void Update()
            {
                float t = Time.time;
                foreach (var (m, a) in _items) if (m != null && a != null) m.SetVector("_DetailTex_ST", a.ST(t));
            }
        }
    }
}