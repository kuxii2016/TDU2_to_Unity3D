using System;
using System.Collections.Generic;
using TDU2.Map.Formats;
using TDU2.Map.IO;
using TDU2.Map.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace TDU2.Map.Vehicles
{
    /// <summary>Eine geparste Szene (3dd + 3dg + 2dm) mit vorbereiteten Meshdaten (nur LOD 0).</summary>
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

    /// <summary>Alle Daten eines Fahrzeugs (im Hintergrund geladen): Modell, Interieur, Felgen, Reifen, Texturen, Drehmoment, Sound.</summary>
    public sealed class CarAssets
    {
        public CarPhysics Car;
        public RimInfo Rim;
        public CarColor Color;
        public InteriorInfo Interior;
        public CarScene Body, Cockpit, RimFront, RimRear, Tire, TireRear;
        public Material2DM ColorLib;
        /// <summary>
        /// Werks-Beklebung (DBCarPhysicsData._sticker, z.B. "rsa_CCXR" der Rennschul-Autos, Polizei): 1024er Stickermap aus
        /// scenarics/stickers/&lt;name&gt;.2db (EXE: "%sscenarics/Stickers/%s.2db"). Wird auf die Lack-Materialien gelegt.
        /// </summary>
        public TextureDef Sticker;
        public string StickerName;
        /// <summary>common_car.bnk/dirt_scratch.2db: R = Schmutz (außen/unten zuerst), G = Kratzer.</summary>
        public TextureDef DirtMap;
        public Curve TorqueMax, TorqueMin;
        public CarSoundConfig Sound;
        public GaugeConfig Gauges;
        public readonly Dictionary<string, (float[] data, int channels, int rate)> Waves = new Dictionary<string, (float[], int, int)>(StringComparer.OrdinalIgnoreCase);
        public readonly List<LocalTextureSet> TextureSets = new List<LocalTextureSet>();
        public readonly List<string> Log = new List<string>();
        /// <summary>Verkehrsauto (islands/&lt;insel&gt;/vehicules/traffic): ein Modell mit Rädern (DISC_xx) in der Szene, keine Felgen-/Interieur-Bänke.</summary>
        public bool Traffic;

        /// <summary>
        /// Verkehrsauto laden: Modell aus islands/&lt;insel&gt;/vehicules/traffic/&lt;_car_model&gt;&lt;_car_version&gt;.bnk,
        /// Sound aus sound/vehicles/&lt;name&gt;_high.bnk, sonst vehicules/med/generic_traffic_low.bnk.
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

        /// <param name="rimId">Felge (DBRims._hashcode, 0 = Standardfelge des Autos)</param>
        /// <param name="interiorId">Innenraum (DBInterior._hashcode, 0 = erster Innenraum der Lackfarbe)</param>
        public static CarAssets Load(string bnkDir, CarDatabase db, CarPhysics car, int colorIndex, bool loadSound, int rimId = 0, int interiorId = 0)
        {
            var a = new CarAssets { Car = car };
            // Felgen: gewählte Felge (Autohändler) bzw. Standardfelge des Autos (DBRims)
            if (rimId != 0) db.Rims.TryGetValue(rimId, out a.Rim);
            if (a.Rim == null && !db.Rims.TryGetValue(car.DefaultRims, out a.Rim) && db.CarRims.TryGetValue(car.Id, out var rl) && rl.Count > 0)
                db.Rims.TryGetValue(rl[0], out a.Rim);
            // Sondermodelle (Rennschule, Polizei ...) haben eigene IDs ohne Farbtabelle: _duplicate = Basis-Auto (Farben/Felgen)
            int dup = car.Raw != null ? car.Raw.I("_duplicate", 0) : 0;
            if (a.Rim == null && dup != 0 && db.CarRims.TryGetValue(dup, out var drl) && drl.Count > 0) db.Rims.TryGetValue(drl[0], out a.Rim);
            if ((!db.Colors.TryGetValue(car.Id, out var cols) || cols.Count == 0) && dup != 0) db.Colors.TryGetValue(dup, out cols);
            if (cols != null && cols.Count > 0)
            {
                a.Color = cols[((colorIndex % cols.Count) + cols.Count) % cols.Count];
                if (interiorId == 0 || Array.IndexOf(a.Color.Interiors, interiorId) < 0 || !db.Interiors.TryGetValue(interiorId, out a.Interior))
                    db.Interiors.TryGetValue(a.Color.Interiors[0], out a.Interior);
            }

            // Werks-Beklebung
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
                // Schmutz-/Kratzer-Maske der Karosserie (R = Schmutz, G = Kratzer), gleiche Abwicklung wie die Stickermap
                var de = b.Find("dirt_scratch.2db");
                if (de != null) a.DirtMap = TextureDef.Parse(b.Read(de), "dirt_scratch");
            });
            // Instrumentenskalen (hud.ini)
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
        /// Motor-/Fahrsound (CarVSTConfig.xmb + WAVs der Soundbank) in vorhandene Assets laden – z.B. für das Ausstellungsauto
        /// beim Händler (Einsteigen: Motor anlassen, Gas im Stand, Hupe). Ohne Unity-API, darf im Hintergrund laufen.
        /// </summary>
        public static void LoadSound(CarAssets a, string bnkDir, CarPhysics car)
        {
            if (a == null || car == null) return;
            var sb = CarDatabase.SoundBank(bnkDir, car);
            if (sb == null) { a.Log.Add("Keine Soundbank für " + car.FileName); return; }
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
                a.Sound = sound;   // zuletzt: wer Sound sieht, findet auch die WAVs
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

        /// <summary>Main-Thread.</summary>
        /// <summary>Textur nach Dateiname (ohne .2db) aus allen geladenen Bänken (z.B. "shadow" aus common_car.bnk).</summary>
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

    /// <summary>Unity-Materialien für Fahrzeuge (Lack, Chrom, Glas, Interieur) aus den .2dm-Parametern.</summary>
    public static class CarMaterials
    {
        public static Shader Opaque, Glass;
        static readonly ulong SemFlake = Bin.KeyFromString("FLAKE");
        static readonly ulong SemTexLight = Bin.KeyFromString("TEXLIGHT");
        /// <summary>lgt_g: Funktionsmaske der Lichtgläser – R = Bremse, G = Rückfahrlicht, B = Licht (Scheinwerfer/Rücklicht), A = 0 → Blinker.</summary>
        static readonly ulong SemGlossLight = Bin.KeyFromString("GLOSSLIGHT");

        public static Material Create(MaterialDef def, Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> getTex, MaterialDef paint)
        {
            if (Opaque == null) Opaque = Shader.Find("TDU2/Car");
            if (Opaque == null) Opaque = Shader.Find("Standard");
            if (Glass == null) Glass = Shader.Find("TDU2/CarGlass");
            if (Glass == null) Glass = Opaque;
            if (def == null) return new Material(Opaque) { name = "Car_Default" };

            var src = paint ?? def;   // Lackfarbe aus <car>_color.2dm ersetzt das Material "Paint"
            MaterialFactory.GetBlend(def, out float alphaRef, out bool twoSided);
            var f = def.Flags;
            bool additive = f[8] == 5 && f[9] == 2;
            bool alphaBlend = f[8] == 5 && f[9] == 6;
            string name = (def.Name ?? "").ToLowerInvariant();

            Texture2D tex = null, overlay = null;
            MaterialLayer layer;
            // Verkehrsautos (TD_CarDeform_TS): SHADOW = Karosserie-Schattierung (weiß, UV0) x Lackfarbe, DETAIL = Lichter-/Detail-Atlas
            // mit Alpha auf dem zweiten UV-Satz darüber. Vorher wurde der Detail-Atlas als ganze Textur genommen (weiße Flecken).
            // (Bremsscheiben Mat_TS_Disk_xx haben SHADOW = DETAIL = dieselbe Scheibentextur: dann einfach die Textur, kein Overlay)
            if (src.TryGetLayer(MaterialDef.SemShadow, out var shl) && shl.TextureKey != 0 && src.TryGetLayer(MaterialDef.SemDetail, out var dtl) && dtl.TextureKey != 0
                && shl.TextureKey != dtl.TextureKey)
            {
                tex = getTex(shl.TextureKey, TextureDef.WrapFromEden(shl.AddressU), TextureDef.WrapFromEden(shl.AddressV));
                overlay = getTex(dtl.TextureKey, TextureDef.WrapFromEden(dtl.AddressU), TextureDef.WrapFromEden(dtl.AddressV));
            }
            else if (src.TryGetLayer(MaterialDef.SemDetail, out layer) || src.TryGetLayer(MaterialDef.SemColor, out layer))
                tex = getTex(layer.TextureKey, TextureDef.WrapFromEden(layer.AddressU), TextureDef.WrapFromEden(layer.AddressV));

            Color col = Color.white;
            // Reifenprofil (rim/tire/t_<Zoll>p_oh, Material T_SpCa_O): DETAIL ist lr_ambocc_* = gepackte Verschattung (R scharf,
            // G/B weicher) und keine Farbtextur – als RGB gezeigt gab das bunte Streifen. Grundton = Diffusfarbe des Materials (0,3).
            bool aoMask = tex != null && tex.name != null && tex.name.IndexOf("ambocc", StringComparison.OrdinalIgnoreCase) >= 0;
            if (aoMask) col = new Color(0.30f, 0.30f, 0.30f, 1f);
            else if (src.TryGetParam("RAMPCLR1", out var rc) && (rc.x + rc.y + rc.z) > 0.001f) col = new Color(rc.x, rc.y, rc.z, 1f);
            else if (tex == null && src.TryGetParam("RAMPCLR1", out var rc0)) col = new Color(rc0.x, rc0.y, rc0.z, 1f);

            float smooth = 0.35f, metal = 0f;
            if (src.TryGetParam("SPECPARM", out var sp) && sp.x > 0f) smooth = Mathf.Clamp01(Mathf.Log10(Mathf.Max(1f, sp.x)) / 3.1f);
            bool flake = src.TryGetLayer(SemFlake, out _);
            bool isPaint = paint != null || CarModel.IsPaintName(name) || flake;
            if (isPaint) { smooth = 0.88f; metal = flake ? 0.35f : 0.1f; }
            if (name.Contains("chrome") || name.Contains("metal") || name.Contains("alu")) { metal = 0.9f; smooth = Mathf.Max(smooth, 0.8f); }
            if (name.Contains("mirror")) { metal = 1f; smooth = 0.97f; col = new Color(0.8f, 0.8f, 0.8f); }

            Material m;
            // Glas mit deckendem Renderstatus (Blending aus, z.B. Verkehrsautos ohne Innenraum): wie im Spiel undurchsichtig, spiegelnd
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
            // Lichtgläser: TEXLIGHT = eingeschaltete Variante der Lichttextur (lgt_on) -> Emission, per Script geregelt
            if (m.HasProperty("_EmissionTex") && def.TryGetLayer(SemTexLight, out var tl) && tl.TextureKey != 0)
            {
                var on = getTex(tl.TextureKey, TextureWrapMode.Clamp, TextureWrapMode.Clamp);
                if (on != null) { m.SetTexture("_EmissionTex", on); m.SetFloat("_Emission", 0f); m.SetFloat("_EmissionDiff", 1f); }
                if (on != null && m.HasProperty("_LightMask") && def.TryGetLayer(SemGlossLight, out var gl) && gl.TextureKey != 0)
                {
                    var mask = getTex(gl.TextureKey, TextureWrapMode.Clamp, TextureWrapMode.Clamp);
                    if (mask != null) { m.SetTexture("_LightMask", mask); m.SetFloat("_UseMask", 1f); }
                }
            }
            if (m.HasProperty("_Cull")) m.SetFloat("_Cull", twoSided || alphaBlend ? 0f : 2f);
            // Lack: zusätzliche Klarlackschicht (Reflexion + Glanzpunkt), Chrom etwas Glanz
            if (m.HasProperty("_ClearCoat"))
            {
                if (isPaint) m.SetFloat("_ClearCoat", 1f);
                if (isPaint && flake && m.HasProperty("_Flake")) m.SetFloat("_Flake", 0.6f);   // Metallic-Lack (FLAKE-Schicht)
                else if (metal > 0.8f) m.SetFloat("_ClearCoat", 0.35f);
            }
            m.name = def.Name ?? "Car";
            return m;
        }
    }

    /// <summary>
    /// Erzeugt das sichtbare Fahrzeug aus den Bänken. Das Modell liegt in Spielkoordinaten (+X = links) unter einem
    /// an X gespiegelten "Visual"-Knoten, so dass es in Unity seitenrichtig erscheint.
    /// Knoten (demangelte Namen): BODY_HR/MR/LR (LODs), FORK_xx (Radaufhängung), STIRRUP_xx (Lenkung), DISC_xx (Raddrehung),
    /// D_RIM_xx / D_TIRE_xx (Felge/Reifen), STEER_HR (Lenkrad im Interieur), DOOR_L/R, D_SBOX (Schattenbox) ...
    /// </summary>
    public sealed class CarModel
    {
        public GameObject Visual;
        public readonly Dictionary<string, Transform> Nodes = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
        public readonly List<UnityEngine.Object> Owned = new List<UnityEngine.Object>();
        public Bounds BodyBounds;
        public Transform SteeringWheel;
        /// <summary>Lichtgläser (LGT_FL/FR/RL/RR/LGT) mit Leuchttextur (TEXLIGHT) – Helligkeit über "_Emission".</summary>
        public readonly List<Material> LightMaterials = new List<Material>();
        /// <summary>Lack-Materialien der Karosserie (Beklebung, Schmutz).</summary>
        public readonly List<Material> PaintMaterials = new List<Material>();
        /// <summary>Instrumente (HUD*-Materialien) – Beleuchtung per Script.</summary>
        public readonly List<Material> DashMaterials = new List<Material>();
        public Transform CockpitRoot;
        public static float InteriorSelfIllum = 0.04f;
        public static float InteriorOcclusion = 0.4f;
        /// <summary>Höchste Glätte matter Innenraum-Materialien (Stoff, Leder, Kunststoff – im Original ohne Umgebungsspiegelung).</summary>
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
            return false;
        }

        /// <summary>Verkehrsautos: WLR_xx ist der Ersatz-Zylinder für das drehende Rad (DISC_xx wird gezeichnet), Schattenboxen/Dummies weg.</summary>
        static bool SkipTrafficNode(string n)
        {
            string u = n.ToUpperInvariant();
            if (u.StartsWith("WLR_")) return true;
            if (u.Contains("SBOX") || u == "D_SHADOW" || u == "D_HEADLIGHT" || u == "D_REVERSING" || u == "D_BREAKING") return true;
            if (u.StartsWith("EXO_FX")) return true;
            return false;
        }

        /// <summary>
        /// Rad-Platzhalter der Karosseriebank (D_TIRE_xx / WHEEL_xx): grobe Reifen-Geometrie, die im Spiel durch rim/tire/t_&lt;Zoll&gt;p_oh
        /// ersetzt wird. Nur als Anker verwenden – sonst liegt ein zweiter, zackiger Reifen über dem echten.
        /// </summary>
        static bool WheelPlaceholder(string n)
        {
            string u = n.ToUpperInvariant();
            return u.Contains("TIRE") || u.Contains("WHEEL") || u.Contains("PNEU");
        }

        /// <summary>Lichtflare-Punkte (FA_L = Scheinwerfer, FA_B = Bremse, FA_R = Rückfahr, FA_S = Rücklicht, FA_W = Blinker): nur als Anker, ohne Mesh.</summary>
        static bool AnchorOnly(string n) => n.StartsWith("FA_", StringComparison.OrdinalIgnoreCase);

        public static CarModel Build(CarAssets a, Transform parent)
        {
            var cm = new CarModel();
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

            var matCache = new Dictionary<(object, ulong), Material>();
            if (a.Traffic) cm.BuildScene(a.Body, cm.Visual.transform, getTex, matCache, def => null, SkipTrafficNode);
            else cm.BuildScene(a.Body, cm.Visual.transform, getTex, matCache, def => Replace(def, false), n => SkipNode(n, a.Cockpit != null));
            if (!a.Traffic) cm.SetupPaint(a, matCache);
            if (a.Cockpit != null)
            {
                var cockRoot = new GameObject("Cockpit").transform;
                cockRoot.SetParent(cm.Visual.transform, false);
                cm.BuildScene(a.Cockpit, cockRoot, getTex, matCache, def => Replace(def, true),
                    n => n.StartsWith("D_", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("D_ND_", StringComparison.OrdinalIgnoreCase));
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
                        // Innenraum: Himmelslicht und Umgebungsreflexion dämpfen (zu hell ohne Verdeckung durch Dach/Säulen)
                        if (m.HasProperty("_Occlusion")) m.SetFloat("_Occlusion", InteriorOcclusion);
                        // Original (TD_CarMat_NM / TD_CarVelour / TD_CarPaint_NM): Stoffe, Leder, Kunststoff haben ENVCOEFF = 0 – keine
                        // Umgebungsspiegelung, nur ein schwacher, breiter Glanz (SPECPARM 3…10). Standard-PBR mit Glätte ~0,2–0,35 und
                        // Echtzeit-Reflexion ließ sie wie lackiert glänzen -> matt; Chrom/Stahl/Spiegel/Glas/Instrumente bleiben.
                        bool shiny = hud || mn.Contains("CHROME") || mn.Contains("STEEL") || mn.Contains("MIRROR") || mn.Contains("GLASS") || mn.Contains("ALU") || mn.Contains("METAL");
                        if (!shiny)
                        {
                            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", Mathf.Min(m.GetFloat("_Glossiness"), InteriorGloss));
                            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                            if (m.HasProperty("_ClearCoat")) m.SetFloat("_ClearCoat", 0f);
                            if (m.HasProperty("_Flake")) m.SetFloat("_Flake", 0f);
                        }
                        if (hud) cm.DashMaterials.Add(m);
                    }
            }

            // Felgen und Reifen an D_RIM_xx / D_TIRE_xx
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
        /// Karosserie-Bounds im Fahrzeugraum neu berechnen (nur sichtbare Teile). Aus den 8 Ecken der Mesh-Bounds jedes Teils –
        /// unabhängig von der Drehung des Autos in der Welt (Welt-Bounds eines gedrehten Autos wären zu groß und schief).
        /// </summary>
        public Bounds RecomputeBodyBounds()
        {
            var parent = _parent != null ? _parent : Visual.transform.parent;
            var body = Find("BODY_HR") ?? Visual.transform;
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
        public static int StickerUvSet = 1;   // geprüft an Esprit_S3: UV-Satz 1 = Karosserie-Abwicklung (Seiten oben/unten, Dach Mitte); UV 0 = Draufsicht-Projektion

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
                if (sticker == null) Debug.LogWarning("[TDU2] Beklebung " + a.StickerName + ": Texturformat nicht unterstützt");
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

        /// <summary>Lack-Materialien der Karosserie: Paint, Paint2 (zweite Farbe), Paint_LR (LOD), Paint_01 …</summary>
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
            Dictionary<(object, ulong), Material> matCache, Func<MaterialDef, MaterialDef> replace, Func<string, bool> skip)
        {
            if (cs?.Scene == null) return;
            var scene = cs.Scene;
            var goByHie = new Dictionary<int, Transform>();
            var skipped = new HashSet<int>();
            var hier = scene.Hierarchy;
            if (hier.Count == 0)
            {
                foreach (var off in scene.InsOffsets) AddNode(cs, scene.GetIns(off), root, getTex, matCache, replace);
                return;
            }
            foreach (var h in hier)
            {
                if (h.ParentOffset != 0 && skipped.Contains(h.ParentOffset)) { skipped.Add(h.Offset); continue; }
                var ins = scene.GetIns(h.InsOffset);
                string name = NodeName(ins.Key);
                if (skip(name)) { skipped.Add(h.Offset); continue; }
                var parent = h.ParentOffset != 0 && goByHie.TryGetValue(h.ParentOffset, out var pt) ? pt : root;
                goByHie[h.Offset] = AddNode(cs, ins, parent, getTex, matCache, replace, !AnchorOnly(name) && !WheelPlaceholder(name));
            }
        }

        Transform AddNode(CarScene cs, InsRecord ins, Transform parent, Func<ulong, TextureWrapMode, TextureWrapMode, Texture2D> getTex,
            Dictionary<(object, ulong), Material> matCache, Func<MaterialDef, MaterialDef> replace, bool withMesh = true)
        {
            string name = NodeName(ins.Key);
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            ObjectBuilder.ApplyMatrix(go.transform, ins.Matrix);
            if (!Nodes.ContainsKey(name)) Nodes[name] = go.transform;
            if (withMesh && ins.Lods.Length > 0 && cs.Meshes.TryGetValue(ins.Lods[0].ObjKey, out var md) && md != null)
            {
                var bankKeys = cs.Scene.ReadMaterialBank(ins.MaterialBankOffset);
                var mats = new Material[md.SubMeshes.Count];
                for (int i = 0; i < mats.Length; i++)
                {
                    int mi = md.SubMeshMaterialIndex[i];
                    ulong mk = mi >= 0 && mi < bankKeys.Length ? bankKeys[mi] : 0;
                    MaterialDef def = null;
                    if (mk != 0 && cs.Materials != null) cs.Materials.Materials.TryGetValue(mk, out def);
                    var key = ((object)cs, mk);
                    if (!matCache.TryGetValue(key, out var mat))
                    {
                        mat = CarMaterials.Create(def, getTex, replace(def));
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
        /// Reifen: Das Modell aus rim/tire/t_&lt;Zoll&gt;p_oh.bnk ist normiert (Radius 0.48 m). Radial wird es auf
        /// [Felgenradius, Reifenradius] abgebildet, axial auf die Reifenbreite (DBRims: Durchmesser, Breite, Flankenhöhe %).
        /// </summary>
        /// <summary>Außenradius (um die X-Achse) der Felgen-Meshes in Modelleinheiten.</summary>
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
            // Felge sitzt im Reifen: Innenradius = tatsächlicher Felgenradius (Mesh x Knotenskalierung), falls plausibel
            if (rimMeshR > rimR * 0.8f && rimMeshR < rimR * 1.25f) rimR = rimMeshR;   // Außenradius bleibt = Physikradius (Bodenkontakt)
            foreach (var off in cs.Scene.InsOffsets)
            {
                var ins = cs.Scene.GetIns(off);
                if (ins.Lods.Length == 0 || !cs.Meshes.TryGetValue(ins.Lods[0].ObjKey, out var md) || md == null) continue;
                var copy = RevolveTire(md, rimR, tireR);
                if (copy == null) continue;
                var tmp = new CarScene { Scene = cs.Scene, Geo = cs.Geo, Materials = cs.Materials };
                tmp.Meshes[ins.Lods[0].ObjKey] = copy;
                var go = AddNode(tmp, ins, node, getTex, matCache, d => null);
                go.name = "Tire";
            }
        }

        /// <summary>
        /// Reifen als glatter Rotationskörper: Das Original (t_&lt;Zoll&gt;p_oh) hat nur 20 Segmente mit modellierten
        /// Schulterblöcken und ist nicht maßstäblich (Außenradius 0.43–0.51). Aus seinen Profilstationen (UV0.x =
        /// Bogenlänge Wulst→Lauffläche→Wulst) wird die Hüllkurve (Blockoberkanten) entnommen, radial auf
        /// [Felgenradius, Reifenradius] abgebildet und mit 72 Segmenten rotiert; V = Winkel/18° wie im Original,
        /// damit Laufflächen-/Flankentextur unverändert passen.
        /// </summary>
        static MeshData RevolveTire(MeshData md, float rimR, float tireR)
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
            // Profilstationen: je UV.x die äußerste Position (Hüllkurve über Blöcke und Rillen)
            var st = new SortedDictionary<int, Vector2>();   // key = round(u*1000) -> (x, r)
            foreach (int vi in used)
            {
                if (vi < 0 || vi >= md.Positions.Count) continue;
                var p = md.Positions[vi];
                float r = Mathf.Sqrt(p.y * p.y + p.z * p.z), x = p.x - xc;
                int k = Mathf.RoundToInt(md.UV0[vi].x * 1000f);
                if (!st.TryGetValue(k, out var cur) || r > cur.y + 1e-4f || (Mathf.Abs(r - cur.y) <= 1e-4f && Mathf.Abs(x) > Mathf.Abs(cur.x)))
                    st[k] = new Vector2(x, r);
            }
            var keys = new List<int>(st.Keys);
            var prof = new List<Vector3>();   // (u, x, r)
            for (int i = 0; i < keys.Count; i++)
            {
                var v = st[keys[i]];
                // Umlaufende Rillengründe auslassen (tiefer als beide Nachbarn)
                if (i > 0 && i < keys.Count - 1 && v.y < Mathf.Min(st[keys[i - 1]].y, st[keys[i + 1]].y) - 0.005f * rMax) continue;
                prof.Add(new Vector3(keys[i] / 1000f, v.x, v.y));
            }
            if (prof.Count < 3) return null;
            // Radiale Abbildung: Lauffläche/Schulter (obere 30 %) mit fester Tiefe, Flanke linear bis zur Felge
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

            const int N = 72;   // 4 Segmente je Texturwiederholung (18°)
            var o = new MeshData { Name = md.Name + "_rev", HasColors = md.HasColors, HasUV1 = md.HasUV1, HasUV2 = md.HasUV2 };
            for (int j = 0; j <= N; j++)
            {
                float th = 2f * Mathf.PI * j / N, c = Mathf.Cos(th), s = Mathf.Sin(th);
                float vv = j * (20f / N);   // V = Winkel / 18°
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
                // Testdreieck gegen die Profilnormale prüfen
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
