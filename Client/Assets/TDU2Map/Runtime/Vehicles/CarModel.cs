using System;
using System.Collections.Generic;
using TDU2.Map.Formats;
using TDU2.Map.IO;
using TDU2.Map.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace TDU2.Map.Vehicles
{
    /// <summary>A parsed scene (3dd + 3dg + 2dm) with pre-processed mesh data (LOD 0 only).</summary>
    public sealed class CarScene
    {
        public Scene3DD Scene;
        public Geometry3DG Geo;
        public Material2DM Materials;
        public readonly Dictionary<ulong, MeshData> Meshes = new Dictionary<ulong, MeshData>();

        public static CarScene Load(BnkArchive bank, string stemHint = null)
        {
            if (bank == null) return null;
            BnkEntry dd = null, dg = null, dm = null;
            foreach (var e in bank.Entries)
            {
                string stem = e.Name.Substring(0, Math.Max(0, e.Name.Length - e.Extension.Length));
                if (stemHint != null && !stem.Equals(stemHint, StringComparison.OrdinalIgnoreCase)) continue;
                if (e.Extension == ".3dd" && dd == null) dd = e;
                else if (e.Extension == ".3dg" && dg == null) dg = e;
                else if (e.Extension == ".2dm" && dm == null && !stem.EndsWith("_color", StringComparison.OrdinalIgnoreCase)) dm = e;
            }
            if (dd == null || dg == null) return stemHint != null ? Load(bank, null) : null;
            var s = new CarScene { Scene = Scene3DD.Parse(bank.Read(dd)), Geo = Geometry3DG.Parse(bank.Read(dg)) };
            if (dm != null) s.Materials = Material2DM.Parse(bank.Read(dm));
            foreach (var off in s.Scene.InsOffsets)
            {
                var ins = s.Scene.GetIns(off);
                if (ins.Lods.Length == 0) continue;
                var k = ins.Lods[0].ObjKey;
                if (s.Meshes.ContainsKey(k)) continue;
                try { s.Meshes[k] = s.Geo.BuildMesh(k); } catch (Exception) { s.Meshes[k] = null; }
            }
            return s;
        }
    }

    /// <summary>All vehicle data (loaded in the background): model, interior, rims, tires, textures, torque, sound.</summary>
    public sealed class CarAssets
    {
        public CarPhysics Car;
        public RimInfo Rim;
        public CarColor Color;
        public InteriorInfo Interior;
        public CarScene Body, Cockpit, RimFront, RimRear, Tire, TireRear;
        public Material2DM ColorLib;
        /// <summary>
        /// Factory decals (DBCarPhysicsData._sticker, e.g., “rsa_CCXR” for racing school cars, police): 1024-pixel decal map from
        /// scenarics/stickers/&lt;name&gt;.2db (EXE: “%sscenarics/Stickers/%s.2db”). Applied to the paint materials.
        /// </summary>
        public TextureDef Sticker;
        public string StickerName;
        /// <summary>common_car.bnk/dirt_scratch.2db: R = Dirt (outside/bottom first), G = Scratches.</summary>
        public TextureDef DirtMap;
        public Curve TorqueMax, TorqueMin;
        public CarSoundConfig Sound;
        public GaugeConfig Gauges;
        public readonly Dictionary<string, (float[] data, int channels, int rate)> Waves = new Dictionary<string, (float[], int, int)>(StringComparer.OrdinalIgnoreCase);
        public readonly List<LocalTextureSet> TextureSets = new List<LocalTextureSet>();
        public readonly List<string> Log = new List<string>();
        /// <summary>Traffic car (islands/island/vehicules/traffic): a model with wheels (DISC_xx) in the scene, no rims or interior seats.</summary>
        public bool Traffic;

        /// <summary>
        /// Load traffic car: Model from islands/island/vehicles/traffic/_car_model/_car_version/.bnk,
        /// Sound from sound/vehicles/name&gt;_high.bnk, otherwise vehicles/med/generic_traffic_low.bnk.
        /// </summary>
        public static CarAssets LoadTraffic(string bankPath, string soundBank, CarPhysics car)
        {
            var a = new CarAssets { Car = car, Traffic = true };
            var bank = BnkArchive.Open(bankPath);
            string stem = System.IO.Path.GetFileNameWithoutExtension(bankPath);
            a.Body = CarScene.Load(bank, stem);
            a.AddTextures(bank);
            if (soundBank != null)
            {
                try
                {
                    var sb = BnkArchive.Open(soundBank);
                    var cfgEntry = sb.Find("CarVSTConfig.xmb");
                    if (cfgEntry != null) a.Sound = CarSoundConfig.Parse(sb.Read(cfgEntry));
                    if (a.Sound != null)
                    {
                        var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var s in a.Sound.Samples) if (s.Wave != null) needed.Add(s.Wave);
                        foreach (var e in sb.Entries)
                        {
                            if (!needed.Contains(e.Name)) continue;
                            if (WavDecoder.TryDecode(sb.Read(e), out var data, out int ch, out int rate)) a.Waves[e.Name] = (data, ch, rate);
                        }
                    }
                }
                catch (Exception ex) { a.Log.Add("Sound: " + ex.Message); }
            }
            return a;
        }

        /// <param name="rimId">Wheel (DBRims._hashcode, 0 = car's default wheel)</param>
        /// <param name="interiorId">Interior (DBInterior._hashcode, 0 = first interior for the paint color)</param>
        public static CarAssets Load(string bnkDir, CarDatabase db, CarPhysics car, int colorIndex, bool loadSound, int rimId = 0, int interiorId = 0)
        {
            var a = new CarAssets { Car = car };
            // Rims: selected rim (car dealer) or the car's standard rim (DBRims)
            if (rimId != 0) db.Rims.TryGetValue(rimId, out a.Rim);
            if (a.Rim == null && !db.Rims.TryGetValue(car.DefaultRims, out a.Rim) && db.CarRims.TryGetValue(car.Id, out var rl) && rl.Count > 0)
                db.Rims.TryGetValue(rl[0], out a.Rim);
            // Special models (racing school, police, etc.) have their own IDs without a color table: _duplicate = base car (colors/rims)
            int dup = car.Raw != null ? car.Raw.I("_duplicate", 0) : 0;
            if (a.Rim == null && dup != 0 && db.CarRims.TryGetValue(dup, out var drl) && drl.Count > 0) db.Rims.TryGetValue(drl[0], out a.Rim);
            if ((!db.Colors.TryGetValue(car.Id, out var cols) || cols.Count == 0) && dup != 0) db.Colors.TryGetValue(dup, out cols);
            if (cols != null && cols.Count > 0)
            {
                a.Color = cols[((colorIndex % cols.Count) + cols.Count) % cols.Count];
                if (interiorId == 0 || Array.IndexOf(a.Color.Interiors, interiorId) < 0 || !db.Interiors.TryGetValue(interiorId, out a.Interior))
                    db.Interiors.TryGetValue(a.Color.Interiors[0], out a.Interior);
            }

            string sticker = car.Raw != null ? car.Raw.S("_sticker", "") : "";
            if (!string.IsNullOrEmpty(sticker) && sticker != "##")
            {
                try
                {
                    var sf = CarDatabase.FindFile(System.IO.Path.Combine(bnkDir, "scenarics", "stickers"), sticker + ".2db");
                    if (sf != null) { a.Sticker = TextureDef.Parse(System.IO.File.ReadAllBytes(sf), sticker); a.StickerName = sticker; }
                    else a.Log.Add("Beklebung " + sticker + ".2db nicht gefunden (scenarics/stickers)");
                }
                catch (Exception ex) { a.Log.Add("Beklebung " + sticker + ": " + ex.Message); }
            }

            var carBankPath = CarDatabase.CarBank(bnkDir, car) ?? throw new System.IO.FileNotFoundException("Fahrzeugbank fehlt: vehicules/high/" + car.FileName + ".bnk");
            var carBank = BnkArchive.Open(carBankPath);
            a.Body = CarScene.Load(carBank, car.FileName);
            a.AddTextures(carBank);
            foreach (var e in carBank.Entries)
            {
                var n = e.Name.ToLowerInvariant();
                if (e.Extension == ".2dm" && n.EndsWith("_color.2dm")) a.ColorLib = Material2DM.Parse(carBank.Read(e));
                else if (n.EndsWith("_max.xmb")) { try { a.TorqueMax = TorqueCurves.Parse(carBank.Read(e)); } catch (Exception ex) { a.Log.Add(ex.Message); } }
                else if (n.EndsWith("_min.xmb")) { try { a.TorqueMin = TorqueCurves.Parse(carBank.Read(e)); } catch (Exception ex) { a.Log.Add(ex.Message); } }
            }
            if (a.TorqueMax == null) a.TorqueMax = TorqueCurves.Synthetic(car);
            if (a.TorqueMin == null) a.TorqueMin = TorqueCurves.SyntheticBrake(car);

            TryLoad(a, CarDatabase.InteriorBank(bnkDir, car), b => a.Cockpit = CarScene.Load(b));
            TryLoad(a, CarDatabase.RimBank(bnkDir, a.Rim, true), b => a.RimFront = CarScene.Load(b));
            if (a.Rim != null && !string.Equals(a.Rim.FileFront, a.Rim.FileRear, StringComparison.OrdinalIgnoreCase))
                TryLoad(a, CarDatabase.RimBank(bnkDir, a.Rim, false), b => a.RimRear = CarScene.Load(b));
            TryLoad(a, CarDatabase.TireBank(bnkDir, a.Rim != null ? a.Rim.DiameterFront : 18f), b => a.Tire = CarScene.Load(b));
            if (a.Rim != null && Mathf.RoundToInt(a.Rim.DiameterRear) != Mathf.RoundToInt(a.Rim.DiameterFront))
                TryLoad(a, CarDatabase.TireBank(bnkDir, a.Rim.DiameterRear), b => a.TireRear = CarScene.Load(b));
            TryLoad(a, CarDatabase.CommonCarBank(bnkDir), b =>
            {
                // Dirt/scratch mask for the car body (R = dirt, G = scratches), same process as the sticker map
                var de = b.Find("dirt_scratch.2db");
                if (de != null) a.DirtMap = TextureDef.Parse(b.Read(de), "dirt_scratch");
            });
            try
            {
                var gb = CarDatabase.GaugeBank(bnkDir, car);
                if (gb != null)
                {
                    var bank = BnkArchive.Open(gb);
                    var ini = bank.Find("hud.ini");
                    if (ini != null) a.Gauges = GaugeConfig.Parse(System.Text.Encoding.ASCII.GetString(bank.Read(ini)));
                }
            }
            catch (Exception ex) { a.Log.Add("hud.ini: " + ex.Message); }

            if (loadSound) LoadSound(a, bnkDir, car);
            return a;
        }

        /// <summary>
        /// Load engine/driving sounds (CarVSTConfig.xmb + WAV files from the sound bank) into existing assets—e.g., for the display car
        /// at the dealership (getting in: start the engine, accelerate from a standstill, honk the horn). No Unity API required; may run in the background.
        /// </summary>
        public static void LoadSound(CarAssets a, string bnkDir, CarPhysics car)
        {
            if (a == null || car == null) return;
            var sb = CarDatabase.SoundBank(bnkDir, car);
            if (sb == null) { a.Log.Add("Keine Soundbank fÃ¼r " + car.FileName); return; }
            try
            {
                var bank = BnkArchive.Open(sb);
                var cfgEntry = bank.Find("CarVSTConfig.xmb");
                var sound = cfgEntry != null ? CarSoundConfig.Parse(bank.Read(cfgEntry)) : null;
                if (sound == null) return;
                var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in sound.Samples) if (s.Wave != null) needed.Add(s.Wave);
                foreach (var e in bank.Entries)
                {
                    if (!needed.Contains(e.Name)) continue;
                    if (WavDecoder.TryDecode(bank.Read(e), out var data, out int ch, out int rate)) a.Waves[e.Name] = (data, ch, rate);
                    else a.Log.Add("WAV nicht lesbar: " + e.Name);
                }
                a.Sound = sound;
            }
            catch (Exception ex) { a.Log.Add("Sound: " + ex.Message); }
        }

        static void TryLoad(CarAssets a, string path, Action<BnkArchive> use)
        {
            if (path == null) return;
            try
            {
                var b = BnkArchive.Open(path);
                use(b);
                a.AddTextures(b);
            }
            catch (Exception ex) { a.Log.Add(System.IO.Path.GetFileName(path) + ": " + ex.Message); }
        }

        void AddTextures(BnkArchive bank)
        {
            var set = new LocalTextureSet();
            foreach (var e in bank.All(".2db"))
            {
                var def = TextureDef.Parse(bank.Read(e), System.IO.Path.GetFileNameWithoutExtension(e.Name));
                if (def != null) set.Add(def);
            }
            TextureSets.Add(set);
        }

        /// <summary>Main thread.</summary>
        /// <summary>Textures by filename (without .2db) from all loaded banks (e.g., “shadow” from common_car.bnk).</summary>
        public Texture2D FindTexture(string name, TextureWrapMode u = TextureWrapMode.Clamp, TextureWrapMode v = TextureWrapMode.Clamp)
        {
            foreach (var s in TextureSets)
                foreach (var kv in s.Defs)
                    if (string.Equals(kv.Value.Name, name, StringComparison.OrdinalIgnoreCase) && s.TryGet(kv.Key, u, v, out var t)) return t;
            return null;
        }

        public Texture2D GetTexture(ulong key, TextureWrapMode u, TextureWrapMode v)
        {
            if (key == 0) return null;
            foreach (var s in TextureSets) if (s.TryGet(key, u, v, out var t)) return t;
            return null;
        }

        public void DestroyTextures()
        {
            foreach (var s in TextureSets) s.Destroy();
        }
    }

    /// <summary>Unity materials for vehicles (paint, chrome, glass, interior) based on the .2dm parameters.</summary>
    public static class CarMaterials
    {
        public static Shader Opaque, Glass;
        static readonly ulong SemFlake = Bin.KeyFromString("FLAKE");
        static readonly ulong SemTexLight = Bin.KeyFromString("TEXLIGHT");
        /// <summary>lgt_g: Function mask for the light lenses – R = Brake light, G = Reverse light, B = Light (headlight/taillight), A = 0 → Turn signal.</summary>
        static readonly ulong SemGlossLight = MaterialDef.KeyFromName("GLOSSLIGHT");

        public static Material Create(MaterialDef def, Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> getTex, MaterialDef paint, MaterialDef tint = null, bool interior = false)
        {
            if (Opaque == null) Opaque = Shader.Find("TDU2/Car");
            if (Opaque == null) Opaque = Shader.Find("Standard");
            if (Glass == null) Glass = Shader.Find("TDU2/CarGlass");
            if (Glass == null) Glass = Opaque;
            if (def == null) return new Material(Opaque) { name = "Car_Default" };

            var src = paint ?? def;   // The paint color from <car>_color.2dm replaces the “Paint” material
            // Interior: Apply only the color; retain the texture of the original cockpit material (seat/door stitching,
            // “Dots” perforations, etc., come from the cockpit texture, not from the color material).
            var colorSrc = tint ?? src;
            if (tint != null) src = def;
            MaterialFactory.GetBlend(def, out float alphaRef, out bool twoSided);
            var f = def.Flags;
            bool additive = f[8] == 5 && f[9] == 2;
            bool alphaBlend = f[8] == 5 && f[9] == 6;
            string name = (def.Name ?? "").ToLowerInvariant();

            Texture2D tex = null, overlay = null;
            MaterialLayer layer;
            // Road cars (TD_CarDeform_TS): SHADOW = body shading (white, UV0) x paint color, DETAIL = highlights/detail atlas
            // with alpha on the second UV set on top. Previously, the detail atlas was used as a single texture (white spots).
            // (Brake discs Mat_TS_Disk_xx have SHADOW = DETAIL = the same disc texture: in that case, just the texture, no overlay)
            if (src.TryGetLayer(MaterialDef.SemShadow, out var shl) && shl.TextureKey != 0 && src.TryGetLayer(MaterialDef.SemDetail, out var dtl) && dtl.TextureKey != 0
                && shl.TextureKey != dtl.TextureKey)
            {
                tex = getTex(shl.TextureKey, TextureDef.WrapFromEden(shl.AddressU), TextureDef.WrapFromEden(shl.AddressV));
                overlay = getTex(dtl.TextureKey, TextureDef.WrapFromEden(dtl.AddressU), TextureDef.WrapFromEden(dtl.AddressV));
            }
            else if (src.TryGetLayer(MaterialDef.SemDetail, out layer) || src.TryGetLayer(MaterialDef.SemColor, out layer))
                tex = getTex(layer.TextureKey, TextureDef.WrapFromEden(layer.AddressU), TextureDef.WrapFromEden(layer.AddressV));

            // Base color: RAMPCLR1 (ramp color) > material's diffuse color (MAT.+0xC0) > texture/white.
            // Interior materials (e.g., “Plastic1_Int” 0.196 gray, “Black_Leather” 0.322, “A_C1_*” beige, “RedPlastic”)
            // carry their color ONLY in the diffuse block, not as RAMPCLR1 (which is (0,0,0) there). Outside the interior,
            // the following still applies: no RAMPCLR1 + no color texture = black (tire/rim materials).
            Color col = Color.white;
            // Tire profile (rim/tire/t_<inches>p_oh, material T_SpCa_O): DETAIL is lr_ambocc_* = packed shading (R sharp,
            // G/B softer) and no color texture—showing it as RGB resulted in colorful stripes. Base color = diffuse color of the material (0.3).
            bool aoMask = tex != null && tex.name != null && tex.name.IndexOf("ambocc", StringComparison.OrdinalIgnoreCase) >= 0;
            if (aoMask) col = new Color(0.30f, 0.30f, 0.30f, 1f);
            else if (colorSrc.TryGetParam("RAMPCLR1", out var rc) && (rc.x + rc.y + rc.z) > 0.001f) col = new Color(rc.x, rc.y, rc.z, 1f);
            else if (interior && (colorSrc.Diffuse.r + colorSrc.Diffuse.g + colorSrc.Diffuse.b) > 0.001f)
                col = new Color(colorSrc.Diffuse.r, colorSrc.Diffuse.g, colorSrc.Diffuse.b, 1f);
            else if (tex == null && colorSrc.TryGetParam("RAMPCLR1", out var rc0)) col = new Color(rc0.x, rc0.y, rc0.z, 1f);

            float smooth = 0.35f, metal = 0f;
            if (src.TryGetParam("SPECPARM", out var sp) && sp.x > 0f) smooth = Mathf.Clamp01(Mathf.Log10(Mathf.Max(1f, sp.x)) / 3.1f);
            bool flake = src.TryGetLayer(SemFlake, out _);
            bool isPaint = (paint != null && tint == null) || CarModel.IsPaintName(name) || flake;
            if (isPaint) { smooth = 0.88f; metal = flake ? 0.35f : 0.1f; }
            if (name.Contains("chrome") || name.Contains("metal") || name.Contains("alu")) { metal = 0.9f; smooth = Mathf.Max(smooth, 0.8f); }
            if (name.Contains("mirror")) { metal = 1f; smooth = 0.97f; col = new Color(0.8f, 0.8f, 0.8f); }

            Material m;
            // Glass with an opaque render status (blending off, e.g., traffic cars without interiors): opaque and reflective, just like in the game
            bool opaqueGlass = name.Contains("glass") && !alphaBlend && !additive && f[10] == 6 && f[8] == 2 && f[9] == 1
                               && (tex == null || tex.format == TextureFormat.DXT1);   // Glastextur ohne Alpha
            if (opaqueGlass)
            {
                m = new Material(Opaque);
                if (tex == null) col = new Color(0.08f, 0.09f, 0.1f, 1f);
                m.SetFloat("_Glossiness", 0.95f);
                m.SetFloat("_Metallic", 0.2f);
                if (m.HasProperty("_ClearCoat")) m.SetFloat("_ClearCoat", 0.8f);
            }
            else if (alphaBlend || additive || name.Contains("glass"))
            {
                m = new Material(Glass);
                if (tex == null && col == Color.white) col = new Color(0.12f, 0.13f, 0.14f, 1f);
                float alpha = tex != null && tex.format != TextureFormat.DXT1 ? 1f : 0.35f;
                col.a = alpha;
                if (additive) { col.a = 0.6f; }
                m.SetFloat("_Glossiness", 0.95f);
                m.renderQueue = (int)RenderQueue.Transparent;
            }
            else
            {
                m = new Material(Opaque);
                m.SetFloat("_Glossiness", smooth);
                m.SetFloat("_Metallic", metal);
                bool cutout = f[5] == 0 && f[7] > 2;
                if (cutout && m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", Mathf.Clamp(alphaRef, 0.05f, 0.9f));
            }
            m.color = col;
            if (m.HasProperty("_Color")) m.SetColor("_Color", col);
            if (tex != null) m.mainTexture = tex;
            if (aoMask && m.HasProperty("_MainGray")) { m.SetFloat("_MainGray", 1f); m.SetFloat("_Glossiness", 0.18f); m.SetFloat("_Metallic", 0f); }
            if (overlay != null && m.HasProperty("_StickerTex"))
            {
                m.SetTexture("_StickerTex", overlay);
                m.SetFloat("_UseSticker", 1f);
                m.SetFloat("_StickerAlphaOnly", 1f);
                m.SetFloat("_StickerUV", 1f);
            }
            // Light glasses: TEXLIGHT = “on” version of the light texture (lgt_on) -> emission, controlled by script
            if (m.HasProperty("_EmissionTex") && def.TryGetLayer(SemTexLight, out var tl) && tl.TextureKey != 0)
            {
                var on = getTex(tl.TextureKey, TextureWrapMode.Clamp, TextureWrapMode.Clamp);
                if (on != null) { m.SetTexture("_EmissionTex", on); m.SetFloat("_Emission", 0f); m.SetFloat("_EmissionDiff", 1f); }
                if (on != null && m.HasProperty("_LightMask") && def.TryGetLayer(SemGlossLight, out var gl) && gl.TextureKey != 0)
                {
                    var mask = getTex(gl.TextureKey, TextureWrapMode.Clamp, TextureWrapMode.Clamp);
                    if (mask != null)
                    {
                        // Function mask: sample sharply; otherwise, neighboring regions
                        // (e.g., the turn signal) will glow slightly due to bilinear filtering.
                        mask.filterMode = FilterMode.Point;
                        mask.anisoLevel = 0;
                        m.SetTexture("_LightMask", mask);
                        m.SetFloat("_UseMask", 1f);
                    }
                }
            }
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", twoSided || alphaBlend ? 0f : 2f);
            // Paint: additional clear coat (reflection + highlight), chrome with a slight sheen
            if (m.HasProperty("_ClearCoat"))
            {
                if (isPaint) m.SetFloat("_ClearCoat", 1f);
                if (isPaint && flake && m.HasProperty("_Flake")) m.SetFloat("_Flake", 0.6f);   // Metallic-Lack (FLAKE-Schicht)
                else if (metal > 0.8f) m.SetFloat("_ClearCoat", 0.35f);
            }
            m.name = def.Name ?? "Car";
            return m;
        }

        /// <summary>
        /// Illuminated headlight lens of a passenger car. The original materials (FRL_*) are untextured
        /// reflective/glass surfaces without emission parameters; dark as visible glass, lighting provided solely via
        /// “_Emission” (white glow texture * _EmissionTint * _Emission, see TDU2/Car).
        /// </summary>
        public static Material CreateLamp(Color tint)
        {
            if (Opaque == null) Opaque = Shader.Find("TDU2/Car");
            if (Opaque == null) Opaque = Shader.Find("Standard");
            var m = new Material(Opaque) { name = "TrafficLamp" };
            var dark = new Color(0.06f, 0.06f, 0.06f, 1f);
            m.color = dark;
            if (m.HasProperty("_Color")) m.SetColor("_Color", dark);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.9f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            if (m.HasProperty("_EmissionTex")) m.SetTexture("_EmissionTex", Texture2D.whiteTexture);
            if (m.HasProperty("_EmissionTint")) m.SetColor("_EmissionTint", tint);
            if (m.HasProperty("_Emission")) m.SetFloat("_Emission", 0f);
            if (m.HasProperty("_EmissionDiff")) m.SetFloat("_EmissionDiff", 0f);
            if (m.HasProperty("_UseMask")) m.SetFloat("_UseMask", 0f);
            if (m.HasProperty("_SelfIllum")) m.SetFloat("_SelfIllum", 0f);
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);
            return m;
        }
    }

    /// <summary>
    /// Creates the visible vehicle from the banks. The model is located in game coordinates (+X = left) under a
    /// “Visual” node mirrored along the X-axis, so that it appears oriented correctly in Unity.
    /// Nodes (missing names): BODY_HR/MR/LR (LODs), FORK_xx (suspension), STIRRUP_xx (steering), DISC_xx (wheel rotation),
    /// D_RIM_xx / D_TIRE_xx (rim/tire), STEER_HR (steering wheel in the interior), DOOR_L/R, D_SBOX (shadow box) ...
    /// </summary>
    public sealed class CarModel
    {
        public GameObject Visual;
        public readonly Dictionary<string, Transform> Nodes = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
        public readonly List<UnityEngine.Object> Owned = new List<UnityEngine.Object>();
        public Bounds BodyBounds;
        public Transform SteeringWheel;
        /// <summary>Light Glasses (LGT_FL/FR/RL/RR/LGT) with a luminous texture (TEXLIGHT) – brightness controlled via “_Emission”.</summary>
        public readonly List<Material> LightMaterials = new List<Material>();

        /// <summary>Lighting function of a traffic car light panel (derived from the node name FA_xx).</summary>
        public enum LampKind { Head, Tail, Brake, Reverse, Indicator, Beacon }

        /// <summary>Lighting material for a single lamp (traffic cars: separate material instance for each node).</summary>
        public struct TrafficLamp { public Material Mat; public LampKind Kind; public int Side; }

        /// <summary>
        /// Lights on traffic cars. The traffic models do not have a TEXLIGHT mask, but instead have their own
        /// light quads (FRL_L/FRL_S/FRL_F/FRL_W/FRL_G + headlight) attached to the FA_ nodes for each function. These are grouped as individual
        /// material instances so that the left and right turn signals can light up separately.
        /// </summary>
        public readonly List<TrafficLamp> TrafficLamps = new List<TrafficLamp>();

        /// <summary>Set during assembly: Body of a road car (then include FA_-lamp quads).</summary>
        bool _traffic;
        /// <summary>Paint materials on the car body (decals, dirt).</summary>
        public readonly List<Material> PaintMaterials = new List<Material>();
        /// <summary>Instruments (HUD* materials) – Lighting via script.</summary>
        public readonly List<Material> DashMaterials = new List<Material>();
        public Transform CockpitRoot;
        public static float InteriorSelfIllum = 0.04f;
        public static float InteriorOcclusion = 0.4f;
        /// <summary>Ultra-smooth matte interior materials (fabric, leather, plastic—as shown, without ambient reflections).</summary>
        public static float InteriorGloss = 0.1f;

        static readonly string[] KnownNames =
        {
            "D_SHADOW","D_SBOX","EXHAUST_L","EXHAUST_R","EXHAUST_HL","EXHAUST_HR","BUMP_F_LR","BUMP_R_LR","BUMP_F_MR","BUMP_R_MR","BUMP_F_HR","BUMP_R_HR",
            "STIRRUP_FL","STIRRUP_FR","STIRRUP_RL","STIRRUP_RR","D_REVERSING","D_BREAKING","D_HEADLIGHT","D_WHEEL_FL","D_WHEEL_FR","D_WHEEL_RL","D_WHEEL_RR",
            "D_TIRE_FL","D_TIRE_FR","D_TIRE_RL","D_TIRE_RR","REVERSING","BREAKING","HEADLIGHT","SCENE01","D_INT_HR_L","D_INT_HR_R","DVRSEAT_LR","DVRSEAT_HR",
            "PSGSEAT_LR","PSGSEAT_HR","SEAT_L_HR","SEAT_R_HR","SEAT_L_LR","SEAT_R_LR","HAND_LR_L","HAND_LR_R","HAND_HR_L","HAND_HR_R","FOOT_LR_L","FOOT_LR_R",
            "FOOT_HR_L","FOOT_HR_R","BACK_LR_L","BACK_LR_R","BACK_HR_L","BACK_HR_R","DOOR_L_COL","DOOR_R_COL","FA_B_RL_C","FA_B_RR_C","FA_L_FL_C","FA_L_FR_C",
            "FA_L_FL1_C","FA_L_FR1_C","FA_R_RL_C","FA_R_RR_C","FA_S_RL_C","FA_S_RR_C","FA_S_RL1_C","FA_S_RR1_C","FA_W_FL_C","FA_W_FR_C","FA_W_RL_C","FA_W_RR_C",
            "PSGFOOT_LR_L","PSGFOOT_LR_R","PSGFOOT_HR_L","PSGFOOT_HR_R","STEER_HR","STEER_LR","SEAT_HR_L","SEAT_HR_R","SEAT_LR_L","SEAT_LR_R","HARDTOP_MR","HARDTOP_LR",
            "GLASS_TOP","GLASS_LEFT","GLASS_RIGHT","HANDLE_DOOR_L","HANDLE_DOOR_R","LGT_FL_MR","LGT_FR_MR","LGT_RL_MR","LGT_RR_MR",
            "BIKE_STAND","BRAKE_LEVER",
        };
        static Dictionary<ulong, string> _names;

        public static string NodeName(ulong key)
        {
            if (_names == null)
            {
                _names = new Dictionary<ulong, string>();
                foreach (var n in KnownNames) _names[MaterialDef.KeyFromName(n)] = n;
            }
            if (_names.TryGetValue(key, out var s)) return s;
            return Bin.KeyToString(key);
        }

        static bool SkipNode(string n, bool hasCockpit)
        {
            string u = n.ToUpperInvariant();
            if (u == "COCK_LR" || u.StartsWith("INT_LR")) return hasCockpit;   // Low-Poly-Cockpit nur ohne Interieur-Bank
            if (u == "BODY_MR" || u == "BODY_LR" || u.EndsWith("_MR") || u.EndsWith("_LR") || u.Contains("_LR_") || u.Contains("_MR_")) return true;
            if (u == "D_SBOX" || u == "D_SHADOW" || u == "D_HEADLIGHT" || u == "D_REVERSING" || u == "D_BREAKING") return true;
            if (u.StartsWith("D_WHEEL")) return true;                            // Hilfs-Zylinder (Radvolumen, verzerrt skaliert)
            if (u.StartsWith("EXO_FX")) return true;                             // Auspuff-Effekte
            if (u == "BIKE_STAND") return true;                                  // Motorrad-Ständer: im Spiel eingeklappt
            if(u == "D_SPLANE") return true;
            return false;
        }

        /// <summary>Traffic cars: WLR_xx is the replacement cylinder for the rotating wheel (DISC_xx is drawn), shadow boxes/dummies removed.</summary>
        static bool SkipTrafficNode(string n)
        {
            string u = n.ToUpperInvariant();
            if (u.StartsWith("WLR_")) return true;
            if (u.Contains("SBOX") || u == "D_SHADOW" || u == "D_HEADLIGHT" || u == "D_REVERSING" || u == "D_BREAKING") return true;
            if (u.StartsWith("EXO_FX")) return true;
            return false;
        }

        /// <summary>
        /// Wheel placeholder for the body bench (D_TIRE_xx / WHEEL_xx): rough tire geometry, which is replaced in the game by rim/tire/t_&lt;inches&gt;p_oh
        /// Use only as an anchor—otherwise, a second, jagged tire will appear on top of the real one.
        /// </summary>
        static bool WheelPlaceholder(string n)
        {
            string u = n.ToUpperInvariant();
            return u.Contains("TIRE") || u.Contains("WHEEL") || u.Contains("PNEU");
        }

        /// <summary>Light flare points (FA_L = headlight, FA_B = brake light, FA_R = reverse light, FA_S = taillight, FA_W = turn signal): used only as anchors, without a mesh.</summary>
        static bool AnchorOnly(string n) => n.StartsWith("FA_", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Headlight projection surface (“headlight”, headlightcaustics.2db): a quad approximately 9x11 m in size in front of the car,
        /// which, in the original, projects the beam of light onto the ground. In the Unity recreation, it is visible “bodywork” that
        /// hangs below the ground, so it should never be rendered.
        /// </summary>
        public static bool IsLightPlane(MaterialDef def)
        {
            if (def == null || def.Name == null) return false;
            string n = def.Name.ToLowerInvariant();
            return n == "headlight" || n.StartsWith("headlight") || n.Contains("caustics");
        }

        /// <summary>
        /// Light function + side based on the FA_-node names in the traffic models:
        ///   FA_L_* = headlights, FA_B_* = brake lights, FA_R_* = reverse lights, FA_S_* = taillights/brake lights,
        ///   FA_W_* = turn signal, FA_G_* = police rotating beacon; ...HEADLI... = headlights.
        /// Side: L (left) = 1, R (right) = 2, otherwise 0.
        /// </summary>
        public static bool TryTrafficLamp(string name, out LampKind kind, out int side)
        {
            kind = LampKind.Head; side = 0;
            if (string.IsNullOrEmpty(name)) return false;
            string u = name.ToUpperInvariant();
            if (u.StartsWith("FA_") && u.Length >= 4)
            {
                switch (u[3])
                {
                    case 'L': kind = LampKind.Head; break;
                    case 'B': kind = LampKind.Brake; break;
                    case 'R': kind = LampKind.Reverse; break;
                    case 'S': kind = LampKind.Tail; break;
                    case 'W': kind = LampKind.Indicator; break;
                    case 'G': kind = LampKind.Beacon; break;
                    default: return false;
                }
                side = GetSide(u);
                return true;
            }
            if (u.Contains("HEADLI")) { kind = LampKind.Head; return true; }
            return false;
        }

        static int GetSide(string u)
        {
            if (u.Contains("_FL") || u.Contains("_RL") || u.EndsWith("_L") || u.Contains("_L_")) return 1;
            if (u.Contains("_FR") || u.Contains("_RR") || u.EndsWith("_R") || u.Contains("_R_")) return 2;
            return 0;
        }

        /// <summary>Base color of a traffic light (emission color).</summary>
        public static Color LampColor(LampKind kind)
        {
            switch (kind)
            {
                case LampKind.Head: return new Color(1f, 0.97f, 0.9f);
                case LampKind.Tail: return new Color(1f, 0.06f, 0.03f);
                case LampKind.Brake: return new Color(1f, 0.04f, 0.02f);
                case LampKind.Reverse: return new Color(1f, 1f, 1f);
                case LampKind.Indicator: return new Color(1f, 0.45f, 0.02f);
                default: return new Color(0.2f, 0.4f, 1f);
            }
        }

        public static CarModel Build(CarAssets a, Transform parent)
        {
            var cm = new CarModel();
            cm._traffic = a.Traffic;
            cm.Visual = new GameObject("Visual (" + a.Car.FileName + ")");
            cm.Visual.transform.SetParent(parent, false);
            cm.Visual.transform.localScale = new Vector3(-1f, 1f, 1f);   // Spiel ist rechtshändig (+X = links)

            Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> getTex = a.GetTexture;
            MaterialDef paintDef = null, paintDef2 = null;
            if (a.ColorLib != null && a.Color != null && !string.IsNullOrEmpty(a.Color.ColorId))
                a.ColorLib.Materials.TryGetValue(MaterialDef.KeyFromName(a.Color.ColorId), out paintDef);
            // zweite Lackfarbe (DBCarsAndColors._color_id_2, z.B. "White on Mark IV Red") -> Material "Paint2"
            if (a.ColorLib != null && a.Color != null && !string.IsNullOrEmpty(a.Color.ColorId2) && a.Color.ColorId2 != "##")
                a.ColorLib.Materials.TryGetValue(MaterialDef.KeyFromName(a.Color.ColorId2), out paintDef2);
            if (paintDef2 == null) paintDef2 = paintDef;

            // Interieurfarben: A_C1*/C1* -> Farbe 1, A_C2*/C2* -> Farbe 2, A_C3*/C3* -> Farbe 3, MAT* -> Materialvariante
            var interiorRepl = new Dictionary<int, MaterialDef>();
            if (a.ColorLib != null && a.Interior != null)
            {
                for (int i = 0; i < 3; i++)
                    if (!string.IsNullOrEmpty(a.Interior.Colors[i]) && a.ColorLib.Materials.TryGetValue(MaterialDef.KeyFromName(a.Interior.Colors[i]), out var d)) interiorRepl[i + 1] = d;
                if (!string.IsNullOrEmpty(a.Interior.Material) && a.ColorLib.Materials.TryGetValue(MaterialDef.KeyFromName(a.Interior.Material), out var md)) interiorRepl[4] = md;
            }
            MaterialDef Replace(MaterialDef def, bool interior)
            {
                if (def == null || def.Name == null) return null;
                string n = def.Name.ToUpperInvariant();
                if (n == "PAINT2") return paintDef2;
                if (n == "PAINT_LR" || (n.StartsWith("PAINT_0") && n.Length <= 8)) return paintDef;   // LOD-/Zusatz-Lack wie Paint
                if (!interior && n == "PAINT") return paintDef;
                if (!interior) return null;
                if (n == "PAINT") return paintDef;
                for (int i = 1; i <= 3; i++)
                    if ((n.StartsWith("A_C" + i) || n.StartsWith("C" + i + "_")) && interiorRepl.TryGetValue(i, out var r)) return r;
                if (n.StartsWith("MAT") && interiorRepl.TryGetValue(4, out var mr)) return mr;
                return null;
            }
            // Interieurfarbe: Farbmaterial, dessen Grundfarbe auf das Cockpitmaterial getönt wird (Textur bleibt erhalten).
            MaterialDef InteriorTint(MaterialDef def)
            {
                if (def == null || def.Name == null) return null;
                string n = def.Name.ToUpperInvariant();
                for (int i = 1; i <= 3; i++)
                    if ((n.StartsWith("A_C" + i) || n.StartsWith("C" + i + "_")) && interiorRepl.TryGetValue(i, out var r)) return r;
                if (n.StartsWith("MAT") && interiorRepl.TryGetValue(4, out var mr)) return mr;
                return null;
            }

            var matCache = new Dictionary<(object, ulong), Material>();
            bool bikeModel = a.Car != null && a.Car.IsBike;
            if (a.Traffic) cm.BuildScene(a.Body, cm.Visual.transform, getTex, matCache, def => null, SkipTrafficNode);
            else cm.BuildScene(a.Body, cm.Visual.transform, getTex, matCache, def => Replace(def, false),
                n => SkipNode(n, a.Cockpit != null));
            if (!a.Traffic) cm.SetupPaint(a, matCache);
            if (a.Cockpit != null)
            {
                var cockRoot = new GameObject("Cockpit").transform;
                cockRoot.SetParent(cm.Visual.transform, false);
                cm.BuildScene(a.Cockpit, cockRoot, getTex, matCache, def => Replace(def, true),
                    n => n.StartsWith("D_", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("D_ND_", StringComparison.OrdinalIgnoreCase),
                    InteriorTint, true);
                cm.CockpitRoot = cockRoot;
                // Interieur liegt im Schatten der Karosserie: Grundhelligkeit wie im Spiel (vorberechnetes Licht in den Texturen)
                var seen = new HashSet<Material>();
                foreach (var r in cockRoot.GetComponentsInChildren<MeshRenderer>(true))
                    foreach (var m in r.sharedMaterials)
                    {
                        if (m == null || !seen.Add(m) || !m.HasProperty("_SelfIllum")) continue;
                        string mn = m.name.ToUpperInvariant();
                        bool hud = mn.StartsWith("HUD");
                        m.SetFloat("_SelfIllum", hud ? 0.12f : InteriorSelfIllum);
                        if (m.HasProperty("_Occlusion")) m.SetFloat("_Occlusion", InteriorOcclusion);
                        // Original (TD_CarMat_NM / TD_CarVelour / TD_CarPaint_NM): Stoffe, Leder, Kunststoff haben ENVCOEFF = 0 “ keine
                        // Umgebungsspiegelung, nur ein schwacher, breiter Glanz (SPECPARM). Standard-PBR mit Glätte ~0,2 0,35 und
                        // Echtzeit-Reflexion ließ sie wie lackiert glänzen -> matt; Chrom/Stahl/Spiegel/Glas/Instrumente bleiben.
                        bool shiny = hud || mn.Contains("CHROME") || mn.Contains("STEEL") || mn.Contains("MIRROR") || mn.Contains("GLASS") || mn.Contains("ALU") || mn.Contains("METAL");
                        if (!shiny)
                        {
                            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", Mathf.Min(m.GetFloat("_Glossiness"), InteriorGloss));
                            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                            if (m.HasProperty("_ClearCoat")) m.SetFloat("_ClearCoat", 0f);
                            if (m.HasProperty("_Flake")) m.SetFloat("_Flake", 0f);
                        }

                        if (mn.Contains("MIRROR"))
                        {
                            //TODO: wir brauchen einen Shader um das ausen Herrunm zu Verdecken
                        }

                        if (hud) cm.DashMaterials.Add(m);
                    }
            }

            // Felgen und Reifen an D_RIM_xx / D_TIRE_xx
            bool isBikeModel = bikeModel;
            if (isBikeModel)
            {
                foreach (var w in new[] { "F", "R" })
                {
                    bool front = w == "F";
                    var rimScene = front ? a.RimFront : (a.RimRear ?? a.RimFront);
                    float rimMeshR = 0f;
                    Transform rimNode = cm.Find("D_RIM_" + w);
                    if (rimNode == null) rimNode = cm.Find("D_RIM_" + (front ? "FL" : "RL"));
                    if (rimNode == null) rimNode = cm.Find(front ? "WHR_F" : "WHR_R");
                    if (rimScene != null && rimNode != null)
                    {
                        cm.BuildScene(rimScene, rimNode, getTex, matCache, d => null, n => false);
                        rimMeshR = RimRadius(rimScene) * Mathf.Abs(rimNode.localScale.y);
                    }
                    var tireScene = front ? a.Tire : (a.TireRear ?? a.Tire);
                    Transform tireNode = cm.Find("TIRE_" + w);
                    if (tireNode == null) tireNode = cm.Find("D_TIRE_" + w);
                    if (tireNode == null) tireNode = cm.Find("D_TIRE_" + (front ? "FL" : "RL"));
                    if (tireNode == null) tireNode = cm.Find(front ? "WHR_F" : "WHR_R");
                    if (tireNode == null && rimNode != null) tireNode = rimNode;
                    if (tireScene != null && tireNode != null)
                        cm.BuildTire(a, tireScene, front, tireNode, rimMeshR, getTex, matCache);
                }
            }
            else
            {
                foreach (var w in new[] { "FL", "FR", "RL", "RR" })
                {
                    bool front = w[0] == 'F';
                    var rimScene = front ? a.RimFront : (a.RimRear ?? a.RimFront);
                    float rimMeshR = 0f;
                    if (rimScene != null && cm.Nodes.TryGetValue("D_RIM_" + w, out var rimNode))
                    {
                        cm.BuildScene(rimScene, rimNode, getTex, matCache, d => null, n => false);
                        rimMeshR = RimRadius(rimScene) * Mathf.Abs(rimNode.localScale.y);
                    }
                    var tireScene = front ? a.Tire : (a.TireRear ?? a.Tire);
                    if (tireScene != null && cm.Nodes.TryGetValue("D_TIRE_" + w, out var tireNode))
                        cm.BuildTire(a, tireScene, front, tireNode, rimMeshR, getTex, matCache);
                }
            }

            cm.SteeringWheel = cm.Find("STEER_HR") ?? cm.Find("STEER_LR");

            // Karosserie-Bounds im Fahrzeugraum (parent)
            cm._parent = parent;
            cm._fallbackSize = new Vector3(a.Car.Width, a.Car.Height, a.Car.Length) * 0.001f;
            cm.RecomputeBodyBounds();
            return cm;
        }

        Transform _parent;
        Vector3 _fallbackSize;

        /// <summary>
        /// Karosserie-Bounds im Fahrzeugraum neu berechnen (nur sichtbare Teile). Aus den 8 Ecken der Mesh-Bounds jedes Teils 
        /// unabhängig von der Drehung des Autos in der Welt (Welt-Bounds eines gedrehten Autos wären zu groß und schief).
        /// </summary>
        public Bounds RecomputeBodyBounds()
        {
            var parent = _parent != null ? _parent : Visual.transform.parent;
            var body = Find("BODY_HR") ?? Find("BODY") ?? Visual.transform;
            bool any = false;
            Vector3 mn = Vector3.zero, mx = Vector3.zero;
            foreach (var r in body.GetComponentsInChildren<MeshRenderer>())
            {
                if (!r.enabled) continue;
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var lb = mf.sharedMesh.bounds;
                var m = parent.worldToLocalMatrix * r.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    var c = new Vector3((i & 1) != 0 ? lb.max.x : lb.min.x, (i & 2) != 0 ? lb.max.y : lb.min.y, (i & 4) != 0 ? lb.max.z : lb.min.z);
                    var p = m.MultiplyPoint3x4(c);
                    if (!any) { mn = mx = p; any = true; } else { mn = Vector3.Min(mn, p); mx = Vector3.Max(mx, p); }
                }
            }
            BodyBounds = any ? new Bounds((mn + mx) * 0.5f, mx - mn) : new Bounds(new Vector3(0, 0.6f, 0), _fallbackSize);
            return BodyBounds;
        }

        /// <summary>UV-Satz der Stickermap/Schmutzmaske auf der Karosserie: -1 = automatisch, 0 = erster, 1 = zweiter UV-Satz.</summary>
        public static int StickerUvSet = 1;

        /// <summary>
        /// UV-Satz der Karosserie-Abwicklung bestimmen: der Satz, dessen Koordinaten auf den Lackflächen im Bereich 0..1 liegen
        /// und die größte Fläche abdecken (der andere Satz ist gekachelt, z.B. für Flakes).
        /// </summary>
        int DetectBodyUvSet()
        {
            var score = new float[2]; var inRange = new int[2]; int total = 0;
            var mn = new[] { new Vector2(float.MaxValue, float.MaxValue), new Vector2(float.MaxValue, float.MaxValue) };
            var mx = new[] { new Vector2(float.MinValue, float.MinValue), new Vector2(float.MinValue, float.MinValue) };
            var uv0 = new List<Vector2>(); var uv1 = new List<Vector2>();
            bool differ = false;
            foreach (var r in Visual.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable) continue;
                var mats = r.sharedMaterials; var mesh = mf.sharedMesh;
                uv0.Clear(); uv1.Clear();
                mesh.GetUVs(0, uv0); mesh.GetUVs(1, uv1);
                if (uv0.Count == 0) continue;
                for (int si = 0; si < mats.Length && si < mesh.subMeshCount; si++)
                {
                    if (!PaintMaterials.Contains(mats[si])) continue;
                    foreach (int vi in mesh.GetTriangles(si))
                    {
                        total++;
                        for (int set = 0; set < 2; set++)
                        {
                            var list = set == 0 ? uv0 : uv1;
                            if (vi >= list.Count) continue;
                            var uv = list[vi];
                            if (uv.x >= -0.01f && uv.x <= 1.01f && uv.y >= -0.01f && uv.y <= 1.01f) inRange[set]++;
                            mn[set] = Vector2.Min(mn[set], uv); mx[set] = Vector2.Max(mx[set], uv);
                        }
                        if (vi < uv1.Count && (uv0[vi] - uv1[vi]).sqrMagnitude > 1e-6f) differ = true;
                    }
                }
            }
            if (total == 0 || !differ) return 0;
            for (int set = 0; set < 2; set++)
            {
                var size = mx[set] - mn[set];
                float area = Mathf.Clamp01(size.x) * Mathf.Clamp01(size.y);
                score[set] = (float)inRange[set] / total * 2f + area;
            }
            int pick = score[1] > score[0] ? 1 : 0;
            Debug.Log($"[TDU2] Karosserie-UV: Satz {pick} (UV0 {inRange[0] * 100 / total}% in 0..1, UV1 {inRange[1] * 100 / total}%)");
            return pick;
        }

        /// <summary>
        /// Lack-Materialien sammeln und Werks-Beklebung (Stickermap) sowie Schmutzmaske (dirt_scratch) auflegen.
        /// Schmutzstufe setzt <see cref="TDU2CarDirt"/> (_DirtLevel 0..1).
        /// </summary>
        void SetupPaint(CarAssets a, Dictionary<(object, ulong), Material> matCache)
        {
            foreach (var kv in matCache)
            {
                var m = kv.Value;
                if (m != null && m.HasProperty("_StickerTex") && IsPaintName(m.name) && !PaintMaterials.Contains(m))
                    PaintMaterials.Add(m);
            }
            if (PaintMaterials.Count == 0) { if (a.Sticker != null) Debug.LogWarning("[TDU2] Beklebung " + a.StickerName + ": kein Lack-Material (\"Paint\") gefunden"); return; }

            Texture2D sticker = null, dirt = null;
            if (a.Sticker != null)
            {
                sticker = a.Sticker.CreateTexture(TextureWrapMode.Clamp, TextureWrapMode.Clamp);
                if (sticker == null) Debug.LogWarning("[TDU2] Beklebung " + a.StickerName + ": Texturformat nicht unterstÃ¼tzt");
                else { sticker.name = a.StickerName; Owned.Add(sticker); }
            }
            if (a.DirtMap != null)
            {
                dirt = a.DirtMap.CreateTexture(TextureWrapMode.Clamp, TextureWrapMode.Clamp, true);
                if (dirt != null) { dirt.name = "dirt_scratch"; Owned.Add(dirt); }
            }
            int uvSet = StickerUvSet >= 0 ? StickerUvSet : DetectBodyUvSet();
            foreach (var m in PaintMaterials)
            {
                m.SetFloat("_StickerUV", uvSet);
                if (sticker != null) { m.SetTexture("_StickerTex", sticker); m.SetFloat("_UseSticker", 1f); }
                if (dirt != null && m.HasProperty("_DirtTex")) { m.SetTexture("_DirtTex", dirt); m.SetFloat("_DirtLevel", 0f); }
            }
            if (sticker != null) Debug.Log("[TDU2] Beklebung " + a.StickerName + " auf " + PaintMaterials.Count + " Lack-Material(ien)");
        }

        /// <summary>Lack-Materialien der Karosserie: Paint, Paint2 (zweite Farbe), Paint_LR (LOD), Paint_01 â€¦</summary>
        public static bool IsPaintName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            n = n.ToLowerInvariant();
            return n == "paint" || n == "paint2" || n == "paint_lr" || (n.StartsWith("paint_0") && n.Length <= 8);
        }

        /// <summary>Lackfarbe 2 (Material Paint2)?</summary>
        public static bool IsPaint2(Material m) => m != null && string.Equals(m.name, "paint2", StringComparison.OrdinalIgnoreCase);

        public Transform Find(string name) => Nodes.TryGetValue(name, out var t) ? t : null;

        void BuildScene(CarScene cs, Transform root, Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> getTex,
            Dictionary<(object, ulong), Material> matCache, Func<MaterialDef, MaterialDef> replace, Func<string, bool> skip,
            Func<MaterialDef, MaterialDef> tint = null, bool interior = false)
        {
            if (cs?.Scene == null) return;
            var scene = cs.Scene;
            var goByHie = new Dictionary<int, Transform>();
            var skipped = new HashSet<int>();
            var hier = scene.Hierarchy;
            if (hier.Count == 0)
            {
                foreach (var off in scene.InsOffsets) AddNode(cs, scene.GetIns(off), root, getTex, matCache, replace, tint, true, interior);
                return;
            }
            // Traffic cars: Build only the “D_CAR” car tree. At the scene level (“SCENE01”), there are also
            // the headlight projection surface (“headlight”), shadow boxes, and open doors/attachments, which in the
            // original are only auxiliary elements/projections—as rendered geometry, they hang beneath the car.
            int carRoot = 0;
            if (_traffic)
            {
                foreach (var h in hier)
                    if (NodeName(scene.GetIns(h.InsOffset).Key).Equals("D_CAR", StringComparison.OrdinalIgnoreCase)) { carRoot = h.Offset; break; }
            }
            if (carRoot != 0)
            {
                var byOff = new Dictionary<int, HieRecord>();
                foreach (var h in hier) byOff[h.Offset] = h;
                // Ancestors of D_CAR (e.g., SCENE01) are only containers and must not be skipped;
                // otherwise, the entire D_CAR subtree will be removed as well.
                var keep = new HashSet<int> { carRoot };
                for (var a = byOff[carRoot]; a.ParentOffset != 0 && byOff.TryGetValue(a.ParentOffset, out var pa); a = pa)
                { if (!keep.Add(a.ParentOffset)) break; }
                foreach (var h in hier)
                {
                    if (keep.Contains(h.Offset)) continue;
                    bool under = false; var cur = h; int guard = 0;
                    while (cur.ParentOffset != 0 && guard++ < 64)
                    {
                        if (cur.ParentOffset == carRoot) { under = true; break; }
                        if (!byOff.TryGetValue(cur.ParentOffset, out var p)) break;
                        cur = p;
                    }
                    if (!under) skipped.Add(h.Offset);
                }
            }
            foreach (var h in hier)
            {
                if (h.ParentOffset != 0 && skipped.Contains(h.ParentOffset)) { skipped.Add(h.Offset); continue; }
                if (skipped.Contains(h.Offset)) continue;
                var ins = scene.GetIns(h.InsOffset);
                string name = NodeName(ins.Key);
                if (skip(name)) { skipped.Add(h.Offset); continue; }
                var parent = h.ParentOffset != 0 && goByHie.TryGetValue(h.ParentOffset, out var pt) ? pt : root;
                bool lampMesh = _traffic && AnchorOnly(name);
                goByHie[h.Offset] = AddNode(cs, ins, parent, getTex, matCache, replace, tint, lampMesh || (!AnchorOnly(name) && !WheelPlaceholder(name)), interior);
            }
        }

        Transform AddNode(CarScene cs, InsRecord ins, Transform parent, Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> getTex,
            Dictionary<(object, ulong), Material> matCache, Func<MaterialDef, MaterialDef> replace, Func<MaterialDef, MaterialDef> tint = null, bool withMesh = true, bool interior = false)
        {
            string name = NodeName(ins.Key);
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            ObjectBuilder.ApplyMatrix(go.transform, ins.Matrix);
            if (!Nodes.ContainsKey(name)) Nodes[name] = go.transform;
            if (withMesh && ins.Lods.Length > 0 && cs.Meshes.TryGetValue(ins.Lods[0].ObjKey, out var md) && md != null)
            {
                // Headlight projection surface (“headlight”): Do not render (otherwise it will hang under the car).
                if (md.SubMeshMaterialIndex.Count > 0 && cs.Materials != null)
                {
                    var bankKeys0 = cs.Scene.ReadMaterialBank(ins.MaterialBankOffset);
                    int mi0 = md.SubMeshMaterialIndex[0];
                    if (mi0 >= 0 && mi0 < bankKeys0.Length && cs.Materials.Materials.TryGetValue(bankKeys0[mi0], out var def0) && IsLightPlane(def0))
                        return go.transform;
                }
                bool isLampNode = _traffic && AnchorOnly(name) && TryTrafficLamp(name, out _, out _);
                int lampSide = 0; LampKind lampKind = LampKind.Head;
                if (isLampNode) TryTrafficLamp(name, out lampKind, out lampSide);
                var bankKeys = cs.Scene.ReadMaterialBank(ins.MaterialBankOffset);
                var mats = new Material[md.SubMeshes.Count];
                for (int i = 0; i < mats.Length; i++)
                {
                    int mi = md.SubMeshMaterialIndex[i];
                    ulong mk = mi >= 0 && mi < bankKeys.Length ? bankKeys[mi] : 0;
                    MaterialDef def = null;
                    if (mk != 0 && cs.Materials != null) cs.Materials.Materials.TryGetValue(mk, out def);
                    var key = ((object)cs, mk);
                    Material mat;
                    if (isLampNode)
                    {
                        // Separate material instance for each lamp so that the left and right turn signals light up independently.
                        // The original FRL_ materials are untextured reflector/glass surfaces without _EmissionTex—
                        // therefore, create a separate, unlit emission material based on the Auto-Shader.
                        mat = CarMaterials.CreateLamp(LampColor(lampKind));
                        Owned.Add(mat);
                        if (mat != null && mat.HasProperty("_EmissionTex"))
                        {
                            mat.SetTexture("_EmissionTex", Texture2D.whiteTexture);
                            mat.SetFloat("_EmissionDiff", 0f);
                            mat.SetFloat("_Emission", 0f);
                            if (mat.HasProperty("_EmissionTint")) mat.SetColor("_EmissionTint", LampColor(lampKind));
                            TrafficLamps.Add(new TrafficLamp { Mat = mat, Kind = lampKind, Side = lampSide });
                        }
                    }
                    else if (!matCache.TryGetValue(key, out mat))
                    {
                        mat = CarMaterials.Create(def, getTex, replace(def), tint != null ? tint(def) : null, interior);
                        matCache[key] = mat;
                        Owned.Add(mat);
                        if (mat != null && mat.HasProperty("_EmissionTex") && mat.GetTexture("_EmissionTex") != null) LightMaterials.Add(mat);
                    }
                    mats[i] = mat;
                }
                var mesh = md.ToMesh();
                Owned.Add(mesh);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = mats;
            }
            return go.transform;
        }

        /// <summary>
        /// Tire: The model from rim/tire/t_&lt;inches&gt;p_oh.bnk is normalized (radius 0.48 m). Radially, it is mapped to
        /// [rim radius, tire radius], and axially to the tire width (DBRims: diameter, width, sidewall height %).
        /// </summary>
        /// <summary>Outer radius (around the X-axis) of the rim meshes in model units.</summary>
        static float RimRadius(CarScene cs)
        {
            float r = 0f;
            foreach (var md in cs.Meshes.Values)
                if (md != null) foreach (var p in md.Positions) r = Mathf.Max(r, Mathf.Sqrt(p.y * p.y + p.z * p.z));
            return r;
        }

        void BuildTire(CarAssets a, CarScene cs, bool front, Transform node, float rimMeshR, Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> getTex,
            Dictionary<(object, ulong), Material> matCache)
        {
            var rim = a.Rim;
            float rimR = rim != null ? (front ? rim.DiameterFront : rim.DiameterRear) * 0.0254f * 0.5f : 0.23f;
            float tireR = rim != null ? rim.TireRadius(front) : 0.33f;
            float targetWidth = 0f;
            if (a.Car != null && a.Car.IsBike && rim != null)
                targetWidth = Mathf.Max(0.06f, (front ? rim.WidthFront : rim.WidthRear) * 0.001f);
            if (rimMeshR > rimR * 0.8f && rimMeshR < rimR * 1.25f) rimR = rimMeshR;
            foreach (var off in cs.Scene.InsOffsets)
            {
                var ins = cs.Scene.GetIns(off);
                if (ins.Lods.Length == 0 || !cs.Meshes.TryGetValue(ins.Lods[0].ObjKey, out var md) || md == null) continue;
                var copy = RevolveTire(md, rimR, tireR, targetWidth);
                if (copy == null) continue;
                var tmp = new CarScene { Scene = cs.Scene, Geo = cs.Geo, Materials = cs.Materials };
                tmp.Meshes[ins.Lods[0].ObjKey] = copy;
                var go = AddNode(tmp, ins, node, getTex, matCache, d => null);
                go.name = "Tire";
            }
        }

        /// <summary>
        /// Tire as a smooth solid of revolution: The original (t_&lt;inches&gt;p_oh) has only 20 segments with modeled
        /// shoulder blocks and is not to scale (outer radius 0.43–0.51). The envelope curve (block top edges) is derived from its profile stations (UV0.x =
        /// arc length bead→tread→bead), mapped radially to
        /// [rim radius, tire radius], and rotated with 72 segments; V = angle/18°, as in the original,
        /// so that the tread and sidewall textures fit unchanged.
        /// </summary>
        static MeshData RevolveTire(MeshData md, float rimR, float tireR, float targetWidth = 0f)
        {
            if (md.Positions.Count == 0 || md.UV0.Count != md.Positions.Count || md.SubMeshes.Count == 0) return null;
            // Hauptsubmesh = das mit den meisten Indizes (das zweite ist die flache Abdeckscheibe)
            int main = 0;
            for (int i = 1; i < md.SubMeshes.Count; i++) if (md.SubMeshes[i].Count > md.SubMeshes[main].Count) main = i;
            var used = new HashSet<int>(md.SubMeshes[main]);
            float rMin = float.MaxValue, rMax = 0f, xMin = float.MaxValue, xMax = float.MinValue;
            foreach (int vi in used)
            {
                if (vi < 0 || vi >= md.Positions.Count) continue;
                var p = md.Positions[vi];
                float r = Mathf.Sqrt(p.y * p.y + p.z * p.z);
                rMin = Mathf.Min(rMin, r); rMax = Mathf.Max(rMax, r);
                xMin = Mathf.Min(xMin, p.x); xMax = Mathf.Max(xMax, p.x);
            }
            if (rMax - rMin < 1e-3f) return null;
            float xc = (xMin + xMax) * 0.5f;
            float xScale = targetWidth > 0.01f && (xMax - xMin) > 0.01f ? targetWidth / (xMax - xMin) : 1f;
            var st = new SortedDictionary<int, Vector2>();   // key = round(u*1000) -> (x, r)
            foreach (int vi in used)
            {
                if (vi < 0 || vi >= md.Positions.Count) continue;
                var p = md.Positions[vi];
                float r = Mathf.Sqrt(p.y * p.y + p.z * p.z), x = (p.x - xc) * xScale;
                int k = Mathf.RoundToInt(md.UV0[vi].x * 1000f);
                if (!st.TryGetValue(k, out var cur) || r > cur.y + 1e-4f || (Mathf.Abs(r - cur.y) <= 1e-4f && Mathf.Abs(x) > Mathf.Abs(cur.x)))
                    st[k] = new Vector2(x, r);
            }
            var keys = new List<int>(st.Keys);
            var prof = new List<Vector3>();   // (u, x, r)
            for (int i = 0; i < keys.Count; i++)
            {
                var v = st[keys[i]];
                if (i > 0 && i < keys.Count - 1 && v.y < Mathf.Min(st[keys[i - 1]].y, st[keys[i + 1]].y) - 0.005f * rMax) continue;
                prof.Add(new Vector3(keys[i] / 1000f, v.x, v.y));
            }
            if (prof.Count < 3) return null;
            const float treadOut = 0.03f;
            float rS = rMin + 0.7f * (rMax - rMin);
            float kT = treadOut / Mathf.Max(1e-4f, rMax - rS);
            float rSo = tireR - treadOut, rB = Mathf.Min(rimR * 0.97f, rSo - 0.01f);
            for (int i = 0; i < prof.Count; i++)
            {
                var q = prof[i];
                float nr = q.z >= rS ? tireR - (rMax - q.z) * kT : Mathf.Lerp(rB, rSo, (q.z - rMin) / Mathf.Max(1e-4f, rS - rMin));
                prof[i] = new Vector3(q.x, q.y, nr);
            }
            // Profilnormalen (x, r) aus den Nachbarn
            int M = prof.Count;
            var pn = new Vector2[M];
            for (int i = 0; i < M; i++)
            {
                var a = prof[Mathf.Max(0, i - 1)]; var b = prof[Mathf.Min(M - 1, i + 1)];
                var t = new Vector2(b.y - a.y, b.z - a.z);
                var n = new Vector2(t.y, -t.x);
                pn[i] = n.sqrMagnitude > 1e-12f ? n.normalized : Vector2.up;
            }
            // Wicklung des Originals bestimmen (Kreuzprodukt vs. gespeicherte Normale)
            float sign = 0f;
            if (md.Normals.Count == md.Positions.Count)
            {
                var tl = md.SubMeshes[main];
                for (int i = 0; i + 2 < tl.Count && i < 3000; i += 3)
                {
                    var A = md.Positions[tl[i]]; var B = md.Positions[tl[i + 1]]; var C = md.Positions[tl[i + 2]];
                    sign += Vector3.Dot(Vector3.Cross(B - A, C - A), md.Normals[tl[i]] + md.Normals[tl[i + 1]] + md.Normals[tl[i + 2]]) > 0f ? 1f : -1f;
                }
            }
            bool wantPositive = sign >= 0f;

            const int N = 72;   // 4 Segmente je Texturwiederholung (180°)
            var o = new MeshData { Name = md.Name + "_rev", HasColors = md.HasColors, HasUV1 = md.HasUV1, HasUV2 = md.HasUV2 };
            for (int j = 0; j <= N; j++)
            {
                float th = 2f * Mathf.PI * j / N, c = Mathf.Cos(th), s = Mathf.Sin(th);
                float vv = j * (20f / N);   // V = Winkel / 180°
                for (int i = 0; i < M; i++)
                {
                    var q = prof[i];
                    o.Positions.Add(new Vector3(q.y, q.z * c, q.z * s));
                    o.Normals.Add(new Vector3(pn[i].x, pn[i].y * c, pn[i].y * s));
                    var uv = new Vector2(q.x, vv);
                    o.UV0.Add(uv);
                    if (md.UV1.Count == md.Positions.Count) o.UV1.Add(uv);
                    if (md.UV2.Count == md.Positions.Count) o.UV2.Add(uv);
                    if (md.Colors.Count == md.Positions.Count) o.Colors.Add(new Color32(255, 255, 255, 255));
                }
            }
            var tris = new List<int>(N * (M - 1) * 6);
            bool flip = false;
            {
                var A = o.Positions[0]; var B = o.Positions[M]; var C = o.Positions[1];
                var nn = Vector3.Cross(B - A, C - A);
                bool pos = Vector3.Dot(nn, o.Normals[0] + o.Normals[1]) > 0f;
                flip = pos != wantPositive;
            }
            for (int j = 0; j < N; j++)
                for (int i = 0; i < M - 1; i++)
                {
                    int a = j * M + i, b = a + 1, c = a + M, d = c + 1;
                    if (!flip) { tris.Add(a); tris.Add(c); tris.Add(b); tris.Add(b); tris.Add(c); tris.Add(d); }
                    else { tris.Add(a); tris.Add(b); tris.Add(c); tris.Add(b); tris.Add(d); tris.Add(c); }
                }
            o.SubMeshes.Add(tris);
            o.SubMeshMaterialIndex.Add(main < md.SubMeshMaterialIndex.Count ? md.SubMeshMaterialIndex[main] : 0);
            if (main < md.SubMeshIsSprite.Count) o.SubMeshIsSprite.Add(false);
            if (main < md.SubMeshUvSets.Count) o.SubMeshUvSets.Add(md.SubMeshUvSets[main]);
            return o;
        }

        public void Destroy()
        {
            foreach (var o in Owned) if (o != null) UnityUtil.SafeDestroy(o);
            Owned.Clear();
            if (Visual != null) UnityUtil.SafeDestroy(Visual);
        }
    }
}