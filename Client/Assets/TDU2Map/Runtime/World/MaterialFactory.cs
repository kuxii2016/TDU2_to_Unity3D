using System;
using TDU2.Formats;
using UnityEngine;
using UnityEngine.Rendering;

namespace TDU2.World
{
    /// <summary>Shader-Referenzen (werden vom Streamer gesetzt, sonst per Shader.Find).</summary>
    [Serializable]
    public sealed class TDU2ShaderSet
    {
        public Shader Object, ObjectTransparent, Terrain, Grass, Water, Sprite, Foliage, SpriteGlow;

        public void Resolve()
        {
            if (Object == null) Object = Shader.Find("TDU2/Object");
            if (ObjectTransparent == null) ObjectTransparent = Shader.Find("TDU2/ObjectTransparent");
            if (Terrain == null) Terrain = Shader.Find("TDU2/Terrain");
            if (Grass == null) Grass = Shader.Find("TDU2/Grass");
            if (Water == null) Water = Shader.Find("TDU2/Water");
            if (Sprite == null) Sprite = Shader.Find("TDU2/Sprite");
            if (Foliage == null) Foliage = Shader.Find("TDU2/Foliage");
            if (SpriteGlow == null) SpriteGlow = Shader.Find("TDU2/SpriteGlow");
        }
    }

    public enum EdenBlend { Opaque, Cutout, AlphaBlend, Additive, Decal }

    /// <summary>
    /// Erzeugt Unity-Materialien aus Eden-.2dm-Materialien.
    /// Interpretation der Flag-Bytes (MAT.+0x40, empirisch):
    ///   [3] 2 = einseitig, 3 = zweiseitig
    ///   [5] 0 = Alpha-Test an (Referenz = [7]/255), 1 = aus
    ///   [8],[9] D3DBLEND src/dst: (2,1) = opak, (5,6) = Alpha-Blend, (5,2) = additiv
    /// Unterstützt Built-in RP (eigene Shader) und URP (Universal Render Pipeline/Lit als Fallback).
    /// </summary>
    public static class MaterialFactory
    {
        public static TDU2ShaderSet Shaders = new TDU2ShaderSet();
        public static float VertexColorStrength = Renderconfig.VertexColorStrength;
        public static float ShadowMapStrength = Renderconfig.ShadowMapStrength;
        public static bool ShowGlowMaterials = Renderconfig.ShowGlowMaterials;
        public static bool Interior;
        const ulong ShaderGouraud = 0x73078865ca37336dUL, ShaderNeonDecal = 0x63078165ca37416dUL, ShaderActors = 0xd5013ed0dcacafafUL, ShaderCasinoScroll = 0xfafafafafafafafaUL;
        public static float GlowIntensity = Renderconfig.GlowIntensity;
        public static float HaloIntensity = Renderconfig.HaloIntensity;
        public static float WindStrength = Renderconfig.WindStrength;
        public static float ObjectBrightness = Renderconfig.ObjectBrightness, WrapLighting = Renderconfig.WrapLighting;
        static Material _water, _fallback, _pool;
        /// <summary>Pool-/Brunnenwasser: Farbe (a = Mindest-Deckung) für "TDU2/Water" _PoolTint.</summary>
        public static Color PoolColor = new Color(0.10f, 0.42f, 0.55f, 0.55f);
        public static bool IsSrp => GraphicsSettings.currentRenderPipeline != null;

        static Shader SrpLit()
        {
            var s = Shader.Find("Universal Render Pipeline/Lit");
            if (s == null) s = Shader.Find("HDRP/Lit");
            return s ?? Shader.Find("Standard");
        }

        public static EdenBlend GetBlend(MaterialDef def, out float alphaRef, out bool twoSided)
        {
            var f = def.Flags;
            twoSided = f[3] == 3;
            alphaRef = f[7] / 255f;
            bool alphaTest = f[5] == 0 && f[7] > 2;
            if (f[8] == 5 && f[9] == 2)
            {
                // SrcAlpha/One: Schilder & Markierungen (Decals) vs. Leuchteffekte
                return IsDecalName(def.Name) ? EdenBlend.Decal : EdenBlend.Additive;
            }
            if (alphaTest) return EdenBlend.Cutout;
            if (f[8] == 5 && f[9] == 6)
            {
                // Vegetation mit Alpha-Blend als Cutout rendern (keine Sortierprobleme)
                if (def.Name != null && def.Name.StartsWith("V_", StringComparison.OrdinalIgnoreCase)) { alphaRef = 0.5f; return EdenBlend.Cutout; }
                return EdenBlend.AlphaBlend;
            }
            return EdenBlend.Opaque;
        }

        static bool IsDecalName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            return n.StartsWith("alpha_") || n.Contains("parking") || n.Contains("sign") || n.Contains("wline") || n.Contains("arrow")
                || n.Contains("mark") || n.Contains("stripe") || n.Contains("logo") || n.Contains("decal");
        }

        static readonly ulong SemMaterial = TDU2.IO.Bin.KeyFromString("MATERIAL");
        static readonly ulong SemTex = TDU2.IO.Bin.KeyFromString("TEX");
        static readonly ulong SemNeon = TDU2.IO.Bin.KeyFromString("NEONGLOW");
        static readonly ulong SemFrontColor = MaterialDef.KeyFromName("FRONTCOLOR"), SemBackColor = MaterialDef.KeyFromName("BACKCOLOR");
        static readonly ulong SemDiffuse = TDU2.IO.Bin.KeyFromString("DIFFUSE");
        static readonly ulong SemMarks = TDU2.IO.Bin.KeyFromString("MARKS");
        static readonly ulong SemMarks2 = TDU2.IO.Bin.KeyFromString("MARKS2");
        static readonly ulong SemGloss = TDU2.IO.Bin.KeyFromString("GLOSS");
        static readonly ulong SemWindow = MaterialDef.KeyFromName("WINDOWMAP");

        /// <summary>Haupt-Farbtextur: COLOR, sonst MATERIAL/TEX/DIFFUSE/NEONGLOW (z.B. Flugzeug, Neonschilder).</summary>
        static bool TryGetColorLayer(MaterialDef def, out MaterialLayer layer)
        {
            if (def.TryGetLayer(MaterialDef.SemColor, out layer)) return true;
            if (def.TryGetLayer(SemMaterial, out layer)) return true;
            if (def.TryGetLayer(SemTex, out layer)) return true;
            if (def.TryGetLayer(SemDiffuse, out layer)) return true;
            if (def.TryGetLayer(SemNeon, out layer)) return true;
            // Schaufenster (Shader-Set 0x18AA57B81C6CFF57, z.B. SB_ShopingA): FRONTCOLOR = komplette Ladenfront
            // (store_front_d, Innenraum schon gemalt; Alpha nur für die Parallax-Rückwand BACKCOLOR) -> als Farbe.
            if (def.TryGetLayer(SemFrontColor, out layer) && layer.TextureKey != 0) return true;
            if (def.TryGetLayer(SemBackColor, out layer) && layer.TextureKey != 0) return true;
            return false;
        }

        /// <summary>
        /// Leuchteffekte, die im Spiel nur nachts an sind (TDU2Weather: outside_street_lightmap / spot_*_lightmap_day_night):
        /// Straßenlampen (SF_Ligh*), Neon/Werbung, Fensterlicht. Ampel-Halos (SF_TLight, SF_Xin, HALO_R/O/G) bleiben immer an.
        /// </summary>
        public static bool IsNightGlow(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string n = name.ToUpperInvariant();
            if (n.Contains("TLIGH") || n.Contains("XIN") || n.Contains("HALO_R") || n.Contains("HALO_O") || n.Contains("HALO_G") || n.Contains("FEU")) return false;
            return true;
        }

        public static Material Fallback
        {
            get
            {
                if (_fallback == null)
                {
                    _fallback = new Material(IsSrp ? SrpLit() : (Shaders.Object ?? Shader.Find("Standard"))) { name = "TDU2_Fallback" };
                    _fallback.color = new Color(0.6f, 0.6f, 0.6f);
                }
                return _fallback;
            }
        }

        public static Material Water
        {
            get
            {
                if (_water == null)
                {
                    var sh = IsSrp ? SrpLit() : (Shaders.Water ?? Shader.Find("Standard"));
                    _water = new Material(sh) { name = "TDU2_Water" };
                    var c = new Color(0.05f, 0.25f, 0.35f, 0.85f);
                    _water.color = c;
                    if (_water.HasProperty("_BaseColor")) _water.SetColor("_BaseColor", c);
                    if (_water.HasProperty("_Smoothness")) _water.SetFloat("_Smoothness", 0.5f);
                }
                return _water;
            }
        }

        /// <summary>
        /// Wasserfläche für Pools/Brunnen (TD_Water: "Oasis_*", "Water"): Meer-Shader mit eigener Wasserfarbe und Deckung. Die
        /// Originalmaterialien tragen in COLOR/NORMAL nur Normalmaps und Kacheln (Spiegelung/Brechung als Render-Targets) – als
        /// gewöhnliches Material gezeichnet sind sie dunkel/schwarz, als Meer (Farbe aus der Gelände-Colormap) fast unsichtbar.
        /// </summary>
        public static Material PoolWater
        {
            get
            {
                var w = Water;
                if (_pool == null || _pool.shader != w.shader)
                {
                    if (_pool != null) UnityUtil.SafeDestroy(_pool);
                    _pool = new Material(w) { name = "TDU2_PoolWater" };
                    if (_pool.HasProperty("_PoolTint")) _pool.SetColor("_PoolTint", PoolColor);
                    else { _pool.color = PoolColor; if (_pool.HasProperty("_BaseColor")) _pool.SetColor("_BaseColor", PoolColor); }
                }
                return _pool;
            }
        }

        /// <summary>Gemeinsame Materialien (nicht pro Objekt besitzen/zerstören).</summary>
        public static bool IsShared(Material m) => m != null && (m == _water || m == _fallback || m == _pool);

        /// <param name="getTexture">key, wrapU, wrapV -> Texture2D (lokal oder global)</param>
        public static Material CreateObjectMaterial(MaterialDef def, Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> getTexture, bool sprite = false, bool tintUv = false, bool fourUv = false)
        {
            if (def == null) return Fallback;
            if (def.IsOcean) return Water;
            // Pools/Brunnen ("Water"): im Spiel Reflexions-Shader mit Normalmaps -> eigene Wasserfläche verwenden
            if (def.IsPoolWater || (def.Name != null && def.Name.Equals("Water", StringComparison.OrdinalIgnoreCase))) return PoolWater;

            var blend = GetBlend(def, out float alphaRef, out bool twoSided);
            Texture2D color = null, shadow = null, detail = null;
            if (TryGetColorLayer(def, out var cl))
                color = getTexture(cl.TextureKey, TextureDef.WrapFromEden(cl.AddressU), TextureDef.WrapFromEden(cl.AddressV));
            // SHADOW mit Adressmodus 3/3 ist die projizierte Sektor-Schattenkarte (kein UV-Satz) -> weglassen
            if (def.TryGetLayer(MaterialDef.SemShadow, out var sl) && !(sl.AddressU == 3 && sl.AddressV == 3))
                shadow = getTexture(sl.TextureKey, TextureDef.WrapFromEden(sl.AddressU), TextureDef.WrapFromEden(sl.AddressV));
            // DETAIL: eigentliche Oberflächentextur (Beton, Holz, Asphalt ...). COLOR ist dann Schmutz-/Farbvariation
            // oder eine Lightmap (lm_*.2db bei Spot-Gebäuden) auf UV0. Straßen nutzen den letzten UV-Satz, sonst UV-Satz 2.
            // (Interieur-Böden wie Mat_ground nennen die Ebene DETAIL1)
            if (def.TryGetLayer(MaterialDef.SemDetail, out var dl) || def.TryGetLayer(MaterialDef.SemDetail1, out dl))
                detail = getTexture(dl.TextureKey, TextureDef.WrapFromEden(dl.AddressU), TextureDef.WrapFromEden(dl.AddressV));
            // DETAIL2 = Farbpalette ("nuancier") auf UV-Satz 3 (nur wenn das Mesh 3+ UV-Sätze hat), MARKS = Fahrbahnmarkierung (UV-Satz 2)
            Texture2D tint = null, marks = null, marks2 = null;
            if (tintUv && def.TryGetLayer(MaterialDef.SemDetail2, out var t2))
                tint = getTexture(t2.TextureKey, TextureDef.WrapFromEden(t2.AddressU), TextureDef.WrapFromEden(t2.AddressV));
            if (def.IsRoad && def.TryGetLayer(SemMarks, out var mk))
                marks = getTexture(mk.TextureKey, TextureDef.WrapFromEden(mk.AddressU), TextureDef.WrapFromEden(mk.AddressV));
            // MARKS2 (Pfeile, Startraster, Reifenspuren der Fahrschule) liegt auf dem 3. UV-Satz – nur bei Meshes mit 4 Sätzen
            if (def.IsRoad && fourUv && def.TryGetLayer(SemMarks2, out var mk2))
                marks2 = getTexture(mk2.TextureKey, TextureDef.WrapFromEden(mk2.AddressU), TextureDef.WrapFromEden(mk2.AddressV));
            if (blend == EdenBlend.Cutout || blend == EdenBlend.AlphaBlend) twoSided = true;

            if (sprite && !IsSrp && (blend == EdenBlend.Additive || blend == EdenBlend.AlphaBlend) && Shaders.SpriteGlow != null)
            {
                // Halos (SF_TLight_Halo ...): additives Billboard statt beleuchteter Cutout-Karte (die wäre schwarz)
                var gm = new Material(Shaders.SpriteGlow) { name = (def.Name ?? "Halo") + "_Glow", enableInstancing = true };
                if (color != null) gm.mainTexture = color;
                gm.SetFloat("_Intensity", blend == EdenBlend.Additive ? HaloIntensity : 1f);
                gm.SetFloat("_DstBlend", (float)(blend == EdenBlend.Additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
                if (IsNightGlow(def.Name)) gm.SetFloat("_NightOnly", 1f);
                return gm;
            }
            if (sprite && !IsSrp && Shaders.Sprite != null)
            {
                var sm = new Material(Shaders.Sprite) { name = (def.Name ?? "Sprite") + "_Sprite", enableInstancing = true };
                if (color != null) sm.mainTexture = color;
                sm.SetFloat("_Cutoff", blend == EdenBlend.Cutout ? Mathf.Clamp(alphaRef, 0.05f, 0.95f) : 0.5f);
                // TD_VegetationSPR: RAMPCLR1.x = Höhe der Kronenmitte (kugelförmige Normalen für die Beleuchtung)
                if (def.TryGetParam("RAMPCLR1", out var ramp) && sm.HasProperty("_CrownHeight"))
                {
                    sm.SetFloat("_CrownHeight", ramp.x);
                    sm.SetFloat("_CrownOn", 1f);
                }
                return sm;
            }

            Material m;
            if (IsSrp)
            {
                m = new Material(SrpLit());
                var baseTex = detail != null ? detail : color;
                if (baseTex != null) { m.SetTexture("_BaseMap", baseTex); m.mainTexture = baseTex; }
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.1f);
                if (twoSided && m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
                if (blend == EdenBlend.Cutout)
                {
                    m.SetFloat("_AlphaClip", 1f);
                    m.SetFloat("_Cutoff", alphaRef);
                    m.EnableKeyword("_ALPHATEST_ON");
                    m.renderQueue = (int)RenderQueue.AlphaTest;
                }
                else if (blend != EdenBlend.Opaque)
                {
                    m.SetFloat("_Surface", 1f);
                    m.SetFloat("_Blend", blend == EdenBlend.Additive ? 2f : 0f);
                    m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    m.SetFloat("_DstBlend", (float)(blend == EdenBlend.Additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
                    m.SetFloat("_ZWrite", 0f);
                    m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    m.renderQueue = (int)RenderQueue.Transparent;
                }
            }
            else
            {
                bool transparent = blend == EdenBlend.AlphaBlend || blend == EdenBlend.Additive || blend == EdenBlend.Decal;
                bool lightmap = color != null && color.name != null && color.name.StartsWith("lm_", StringComparison.OrdinalIgnoreCase);
                bool foliage = blend == EdenBlend.Cutout && Shaders.Foliage != null && def.Name != null
                               && def.Name.StartsWith("V_", StringComparison.OrdinalIgnoreCase) && detail == null;
                if (blend == EdenBlend.Additive && !ShowGlowMaterials) return null; // Leuchteffekte (nur nachts) ausblenden

                var sh = transparent ? Shaders.ObjectTransparent : foliage ? Shaders.Foliage : Shaders.Object;
                m = new Material(sh ?? Shader.Find("Standard"));
                if (color != null) m.mainTexture = color;
                if (m.HasProperty("_ShadowTex"))
                {
                    m.SetTexture("_ShadowTex", shadow != null ? shadow : Texture2D.whiteTexture);
                    m.SetFloat("_ShadowStrength", shadow != null ? ShadowMapStrength : 0f);
                }
                if (m.HasProperty("_VertexColorStrength")) m.SetFloat("_VertexColorStrength", VertexColorStrength);
                if (foliage && m.HasProperty("_WindStrength")) { m.SetFloat("_WindStrength", WindStrength); m.SetFloat("_FlutterStrength", WindStrength); }
                // Vegetation, die nicht über TDU2/Foliage läuft (V_*-Materialien mit DETAIL-Ebene, z.B. Büsche):
                // gleiche Beleuchtung wie Laub, Gras und Boden (Normale oben, keine Vertexfarbe – wie TD_Vegetation)
                bool plant = !foliage && !transparent && blend == EdenBlend.Cutout && def.Name != null && def.Name.StartsWith("V_", StringComparison.OrdinalIgnoreCase);
                if (plant && m.HasProperty("_PlantMode"))
                {
                    m.SetFloat("_PlantMode", 1f);
                    if (m.HasProperty("_VertexColorStrength")) m.SetFloat("_VertexColorStrength", 0f);
                }
                if (detail != null && m.HasProperty("_DetailTex"))
                {
                    m.SetTexture("_DetailTex", detail);
                    m.SetFloat("_DetailMode", 1f);
                    m.SetFloat("_DetailUV", def.IsRoad ? 1f : 0f);
                    if (m.HasProperty("_ColorStrength")) m.SetFloat("_ColorStrength", def.IsRoad ? 0.35f : 1f);
                    // COLOR als Lightmap (lm_*) bzw. Straßen-Variation um 0.5 -> x2, sonst Schmutz-/Farbvariation nahe 1
                    bool packed = IsPackedDetail(detail);
                    m.SetFloat("_DetailBoost", lightmap || def.IsRoad ? 2f : packed ? 1f : 1.2f);
                    if (m.HasProperty("_DetailGray")) m.SetFloat("_DetailGray", packed ? 1f : 0f);
                    // TD_Ground (Original-Pixelshader): Farbe = COLOR x DETAIL.r – G/B der Detailtextur sind andere Daten.
                    // Mit RGB multipliziert wurden Plätze/Gehwege (z.B. Fahrschulen) fleckig-matschig.
                    if (def.IsGround && !lightmap)
                    {
                        if (m.HasProperty("_DetailGray")) m.SetFloat("_DetailGray", 1f);
                        m.SetFloat("_DetailBoost", 1.25f / 1.8f);   // Shader: R x 1.8 x Boost -> COLOR x DETAIL.r x 1.25
                    }
                }
                if (tint != null && m.HasProperty("_TintTex"))
                {
                    m.SetTexture("_TintTex", tint); m.SetFloat("_TintMode", 1f);
                    // Palette (nuancier, Mittel ~0.5) -> x2; Schmutz-Overlays (d2_coulure ~0.83) -> ~x1.2
                    float mean = TextureDef.MeanLuminance.TryGetValue(tint.GetInstanceID(), out var ml) ? ml : 0.5f;
                    if (m.HasProperty("_TintScale")) m.SetFloat("_TintScale", Mathf.Clamp(1f / Mathf.Max(0.05f, mean), 1f, 2f));
                }
                if (m.HasProperty("_Brightness")) m.SetFloat("_Brightness", ObjectBrightness);
                if (m.HasProperty("_Wrap")) m.SetFloat("_Wrap", WrapLighting);
                if (marks != null && m.HasProperty("_MarksTex")) { m.SetTexture("_MarksTex", marks); m.SetFloat("_MarksMode", 1f); }
                if (marks2 != null && m.HasProperty("_Marks2Tex")) { m.SetTexture("_Marks2Tex", marks2); m.SetFloat("_Marks2Mode", 1f); }
                // Wetter: Straßen/Plätze werden bei Regen nass; Leuchteffekte (Neon, Lampenschein) nur nachts
                if ((def.IsRoad || def.IsGround) && m.HasProperty("_WetSurface")) m.SetFloat("_WetSurface", 1f);
                // Nachtlicht: Lampenköpfe (TD_Lamp) und Neon (TD_pubneon) leuchten, Gebäude bekommen Nachtfenster
                if ((def.ShaderSet == 0xc913741cbe17490dUL || def.ShaderSet == 0x18315cacac6cff57UL) && m.HasProperty("_NightEmit"))
                    m.SetFloat("_NightEmit", def.ShaderSet == 0xc913741cbe17490dUL ? 2.5f : 1.5f);
                if ((def.ShaderSet == 0x183157b81c6cff57UL || def.ShaderSet == 0x18aa57b81c6cff57UL) && m.HasProperty("_WinMode")
                    && def.TryGetLayer(SemGloss, out var gl) && def.TryGetLayer(SemWindow, out var wl)
                    && def.Params.TryGetValue(MaterialDef.KeyFromName("luminance_power"), out var lp) && lp.x > 0f)
                {
                    var gt = getTexture(gl.TextureKey, TextureWrapMode.Repeat, TextureWrapMode.Repeat);
                    var wt = getTexture(wl.TextureKey, TextureWrapMode.Repeat, TextureWrapMode.Repeat);
                    if (gt != null && wt != null)
                    {
                        m.SetTexture("_GlossTex", gt); m.SetTexture("_WinTex", wt);
                        m.SetVector("_WinParams", lp); m.SetFloat("_WinMode", 1f);
                    }
                }
                if (blend == EdenBlend.Additive && m.HasProperty("_NightOnly") && IsNightGlow(def.Name) && !Interior) m.SetFloat("_NightOnly", 1f);
                if (m.HasProperty("_Cull")) m.SetFloat("_Cull", twoSided || transparent ? 0f : 2f);
                if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", blend == EdenBlend.Cutout ? Mathf.Clamp(alphaRef, 0.05f, 0.95f) : (foliage ? 0.5f : 0f));
                if (transparent && m.HasProperty("_SrcBlend"))
                {
                    m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    m.SetFloat("_DstBlend", (float)(blend == EdenBlend.Additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
                    // Glas/Fenster: Alpha aus der DETAIL-Textur (vitre, glass_*), COLOR ist die Lightmap
                    bool detailAlpha = detail != null && detail.format != TextureFormat.DXT1;
                    m.SetFloat("_AlphaSource", detailAlpha ? 1f : 0f);
                    // TD_Ground (Original-Pixelshader, Varianten mit DetailMap): Farbe = COLOR x DETAIL.r, Alpha = DETAIL.a –
                    // nie COLOR.a (Colormap/Lightmap). DXT1-Detail = deckend.
                    if (def.IsGround && detail != null)
                    {
                        m.SetFloat("_AlphaSource", 1f);
                        if (m.HasProperty("_DetailGray")) m.SetFloat("_DetailGray", 1f);
                        m.SetFloat("_DetailBoost", lightmap ? 2f : 1.25f);
                    }
                    m.SetFloat("_Lit", lightmap ? 0f : 1f);
                    m.SetFloat("_Intensity", blend == EdenBlend.Additive ? GlowIntensity : 1f);
                    if (blend == EdenBlend.Additive) m.SetFloat("_Lit", 0f);
                }
                ApplyOriginalShader(def, m, transparent);
            }
            m.name = def.Name ?? "TDU2Mat";
            m.enableInstancing = true;
            // Textur noch nicht bekannt (liegt in einer Bank, die erst später geladen/indiziert wird) -> weiße Fläche.
            // Merken und nachreichen, sobald der globale Texturindex sie kennt (RetryPendingTextures).
            if (!IsSrp)
            {
                if (color == null && cl.TextureKey != 0 && m.mainTexture == null && m.HasProperty("_MainTex"))
                    AddPending(m, "_MainTex", cl, def, false);
                if (detail == null && dl.TextureKey != 0 && m.HasProperty("_DetailTex"))
                    AddPending(m, "_DetailTex", dl, def, true);
            }
            return m;
        }

        // ------------------------------------------------------------------ fehlende Texturen nachreichen
        sealed class PendingTex { public Material Mat; public string Prop; public ulong Key; public TextureWrapMode U, V; public bool Detail, Road; public string Name; public float Since; }
        static readonly System.Collections.Generic.List<PendingTex> _pending = new System.Collections.Generic.List<PendingTex>();

        /// <summary>Materialien, deren Textur noch fehlt (weiß dargestellt).</summary>
        public static int PendingTextures => _pending.Count;

        static void AddPending(Material m, string prop, MaterialLayer l, MaterialDef def, bool detail)
        {
            _pending.Add(new PendingTex
            {
                Mat = m, Prop = prop, Key = l.TextureKey, U = TextureDef.WrapFromEden(l.AddressU), V = TextureDef.WrapFromEden(l.AddressV),
                Detail = detail, Road = def.IsRoad, Name = def.Name ?? "?", Since = Time.unscaledTime,
            });
        }

        /// <summary>
        /// Main-Thread, regelmäßig (TDU2MapStreamer): fehlende Texturen aus dem globalen Index nachladen und einsetzen.
        /// </summary>
        public static int RetryPendingTextures(Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> get)
        {
            if (_pending.Count == 0 || get == null) return 0;
            int fixedN = 0;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var p = _pending[i];
                if (p.Mat == null) { _pending.RemoveAt(i); continue; }
                Texture2D t = null;
                try { t = get(p.Key, p.U, p.V); } catch (Exception) { }
                if (t == null) continue;
                p.Mat.SetTexture(p.Prop, t);
                if (p.Detail)
                {
                    if (p.Mat.HasProperty("_DetailMode")) p.Mat.SetFloat("_DetailMode", 1f);
                    if (p.Mat.HasProperty("_DetailUV")) p.Mat.SetFloat("_DetailUV", p.Road ? 1f : 0f);
                }
                _pending.RemoveAt(i);
                fixedN++;
            }
            return fixedN;
        }


        /// <summary>
        /// Original-Pixelshader der Interieur-/Casino-Sets (Euro/Shaders/*.sob disassembliert):
        ///   td_actors:        Farbe = COLOR(UV0, Okklusion/Licht) x DETAIL1(UV1) x baseColor  (nightlmap.x = 0 -> kein Echtzeitlicht)
        ///                     + Glanz + Reflexion x matParam.x
        ///   td_casino_scroll: wie td_actors (Walzen/Figuren der Spielautomaten, DETAIL mit Bewegungsunschärfe)
        ///   TD_Gouraud:       Farbe = Textur x Vertexfarbe, unbeleuchtet (Neon, LEDs, Bildschirme, Leuchtschilder)
        ///   TD_NEONDECAL:     Farbe = Vertexfarbe x rgbExposure (x Leuchtmaske), unbeleuchtet (Gewinn-/Neon-Anzeigen)
        /// </summary>
        // TD_NEONDECAL: Leuchtmaske (NEONGLOW-Ebene) je Material. Ohne Animation bleibt das Neon aus (schwarze Maske);
        // hat die Bank eine .uva für die Ebene (Fahrschulen, Casino …), setzt InteriorFx die Maske ein und lässt sie laufen.
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Material, Texture> _neonMasks = new System.Runtime.CompilerServices.ConditionalWeakTable<Material, Texture>();

        /// <summary>Leuchtmaske eines Neon-Materials (TD_NEONDECAL mit NEONGLOW-Ebene), sonst false.</summary>
        public static bool TryGetNeonMask(Material m, out Texture mask)
        {
            mask = null;
            return m != null && _neonMasks.TryGetValue(m, out mask) && mask != null;
        }

        static void ApplyOriginalShader(MaterialDef def, Material m, bool transparent)
        {
            ulong ss = def.ShaderSet;
            if (ss == ShaderNeonDecal && m.mainTexture != null && def.TryGetLayer(TDU2.IO.Bin.KeyFromString("NEONGLOW"), out _))
            {
                try { _neonMasks.Remove(m); _neonMasks.Add(m, m.mainTexture); } catch (Exception) { }
            }
            if (ss == ShaderActors || ss == ShaderCasinoScroll)
            {
                if (transparent) { if (m.HasProperty("_Lit")) m.SetFloat("_Lit", 0f); return; }
                if (def.Params.TryGetValue(MaterialDef.KeyFromName("baseColor"), out var bc) && m.HasProperty("_Color")) m.SetColor("_Color", new Color(bc.x, bc.y, bc.z, 1f));
                if (m.HasProperty("_Unlit")) m.SetFloat("_Unlit", 1f);
                if (m.HasProperty("_DetailBoost")) m.SetFloat("_DetailBoost", 1f);
                if (m.HasProperty("_ColorStrength")) m.SetFloat("_ColorStrength", 1f);
                if (m.HasProperty("_DetailGray")) m.SetFloat("_DetailGray", 0f);
                if (m.HasProperty("_VertexColorStrength")) m.SetFloat("_VertexColorStrength", 0f);
                if (m.HasProperty("_Brightness")) m.SetFloat("_Brightness", 1f);
                return;
            }
            if (ss == ShaderGouraud && Interior)
            {
                if (transparent) { if (m.HasProperty("_Lit")) m.SetFloat("_Lit", 0f); if (m.HasProperty("_VcMul")) m.SetFloat("_VcMul", 1f); return; }
                if (m.HasProperty("_Unlit")) m.SetFloat("_Unlit", 1f);
                if (m.HasProperty("_VertexColorStrength")) m.SetFloat("_VertexColorStrength", 1f);
                if (m.HasProperty("_VcScale")) m.SetFloat("_VcScale", 1f);
                if (m.HasProperty("_Brightness")) m.SetFloat("_Brightness", 1f);
                if (m.HasProperty("_DetailMode")) m.SetFloat("_DetailMode", 0f);
                return;
            }
            if (ss == ShaderNeonDecal)
            {
                // Leuchtfarbe steckt in der Vertexfarbe; die NEONGLOW-Ebene ist nur eine (animierte) Maske
                if (transparent)
                {
                    if (m.HasProperty("_Lit")) m.SetFloat("_Lit", 0f);
                    if (m.HasProperty("_VcMul")) m.SetFloat("_VcMul", 1f);
                    if (m.HasProperty("_DetailMode")) m.SetFloat("_DetailMode", 0f);
                    if (def.TryGetLayer(TDU2.IO.Bin.KeyFromString("NEONGLOW"), out _)) m.mainTexture = Texture2D.blackTexture;
                    else m.mainTexture = Texture2D.whiteTexture;
                    return;
                }
                // mit NEONGLOW-Ebene: Leuchten nur über die animierte Maske (AnimValue, setzt das Spiel zur Laufzeit) -> aus
                bool masked = def.TryGetLayer(TDU2.IO.Bin.KeyFromString("NEONGLOW"), out _);
                m.mainTexture = masked ? Texture2D.blackTexture : Texture2D.whiteTexture;
                if (m.HasProperty("_Unlit")) m.SetFloat("_Unlit", 1f);
                if (m.HasProperty("_VertexColorStrength")) m.SetFloat("_VertexColorStrength", 1f);
                if (m.HasProperty("_VcScale")) m.SetFloat("_VcScale", 1f);
                if (m.HasProperty("_Brightness")) m.SetFloat("_Brightness", 1f);
                if (m.HasProperty("_DetailMode")) m.SetFloat("_DetailMode", 0f);
            }
        }

        /// <summary>Terrain-Detailtexturen (d1_cliff_rocks, d2_earth ...): R = Graustufen-Detail, G/B = gepackte Daten.</summary>
        public static bool IsPackedDetail(Texture2D t)
        {
            if (t == null) return false;
            return TextureDef.PackedTextures.Contains(t.GetInstanceID());
        }

        /// <summary>Terrain-Material: Colormap + optional Splat/Shadow-Map + Detailtexturen.</summary>
        public static Material CreateTerrainMaterial(Texture2D colorMap, Texture2D splatMap, Texture2D detail1, Texture2D detail2, float detailTiling, float detailStrength)
        {
            Material m;
            if (IsSrp || Shaders.Terrain == null)
            {
                m = new Material(IsSrp ? SrpLit() : Shader.Find("Standard"));
                if (colorMap != null) { m.mainTexture = colorMap; if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", colorMap); }
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0f);
            }
            else
            {
                m = new Material(Shaders.Terrain);
                if (colorMap != null) m.SetTexture("_MainTex", colorMap);
                m.SetTexture("_SplatTex", splatMap != null ? splatMap : Texture2D.whiteTexture);
                m.SetFloat("_UseSplat", splatMap != null ? 1f : 0f);
                m.SetFloat("_ShadowStrength", ShadowMapStrength);
                m.SetTexture("_Detail1", detail1 != null ? detail1 : Texture2D.grayTexture);
                m.SetTexture("_Detail2", detail2 != null ? detail2 : Texture2D.grayTexture);
                m.SetFloat("_DetailTiling", detailTiling);
                m.SetFloat("_DetailStrength", (detail1 != null || detail2 != null) ? detailStrength : 0f);
            }
            m.name = colorMap != null ? "Terrain_" + colorMap.name : "Terrain";
            return m;
        }

        public static Material CreateGrassMaterial(Texture2D atlas, float cutoff)
        {
            Material m;
            if (IsSrp || Shaders.Grass == null)
            {
                m = new Material(IsSrp ? SrpLit() : Shader.Find("Standard"));
                if (atlas != null) { m.mainTexture = atlas; if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", atlas); }
                if (m.HasProperty("_AlphaClip")) { m.SetFloat("_AlphaClip", 1f); m.EnableKeyword("_ALPHATEST_ON"); }
                if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", cutoff);
                if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            }
            else
            {
                m = new Material(Shaders.Grass);
                if (atlas != null) m.mainTexture = atlas;
                m.SetFloat("_Cutoff", cutoff);
            }
            m.name = "TDU2_Grass";
            m.renderQueue = (int)RenderQueue.AlphaTest;
            return m;
        }

        public static void Reset()
        {
            if (_water != null) UnityUtil.SafeDestroy(_water);
            if (_fallback != null) UnityUtil.SafeDestroy(_fallback);
            if (_pool != null) UnityUtil.SafeDestroy(_pool);
            _water = null; _fallback = null; _pool = null;
        }
    }
}
