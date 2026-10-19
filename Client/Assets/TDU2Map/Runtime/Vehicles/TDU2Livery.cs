using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TDU2.Map.Formats;
using TDU2.Map.IO;
using TDU2.Map.Online;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    // =====================================================================================================================
    //  Lackierung + Beklebung eines Autos (Sticker-Shop ESTICK, MngCarPainting / StickerMap.cpp im Original)
    //
    //  Datenmodell statt Textur: das Original streamt die fertige Stickermap (CRC_R_REQUEST_STICKERS, Mip-Stufen) zwischen
    //  den Spielern. Hier wird nur die Beschreibung gespeichert und übertragen (Farbe, Finish, Liste der Sticker mit Planche,
    //  Nummer, Lage in der Karosserie-Abwicklung UV1, Achsen, Farbe/Alpha) – jeder Client rendert die Stickermap selbst.
    //  ~19 Byte je Sticker, max. 120 Sticker.
    // =====================================================================================================================

    /// <summary>Lack-Finish (Sticker-Shop: /:ColorType, im Spiel GLOSSY / METAL / MATT).</summary>
    public enum PaintFinish : byte { Original = 0, Gloss = 1, Metallic = 2, Matt = 3 }

    /// <summary>Ein aufgeklebter Sticker in der Karosserie-Abwicklung (UV-Satz 1): Mitte (U,V) und Halbachsen A (rechts) / B (oben).</summary>
    public sealed class StickerPlacement
    {
        public byte Board;          // DBStickersBanks._hashcode
        public byte Index;          // 0..23 (MAT_01..MAT_24 der Planche)
        public float U, V;          // Mitte (UV1)
        public Vector2 A, B;        // Halbachsen in UV (A = Bildbreite/2 nach rechts, B = Bildhöhe/2 nach oben)
        public Color32 Tint = new Color32(255, 255, 255, 255);   // Farbe (einfarbige Planchen) + Alpha
        public byte Flags;          // 1 = gespiegelt (nur Info, steckt schon in A)

        public StickerPlacement Clone() => (StickerPlacement)MemberwiseClone();
    }

    public sealed class CarLivery
    {
        public const int MaxStickers = 120;
        public bool HasPaint;
        public Color32 Paint = new Color32(200, 200, 200, 255);
        public Color32 Paint2 = new Color32(30, 30, 30, 255);
        public bool Bicolour;
        public PaintFinish Finish;
        /// <summary>Werks-Beklebung (DBCarPhysicsData._sticker) entfernt ("Effacer").</summary>
        public bool ClearFactory;
        public readonly List<StickerPlacement> Stickers = new List<StickerPlacement>();

        public bool IsEmpty => !HasPaint && Finish == PaintFinish.Original && !ClearFactory && Stickers.Count == 0;

        public CarLivery Clone()
        {
            var c = new CarLivery { HasPaint = HasPaint, Paint = Paint, Paint2 = Paint2, Bicolour = Bicolour, Finish = Finish, ClearFactory = ClearFactory };
            foreach (var s in Stickers) c.Stickers.Add(s.Clone());
            return c;
        }

        // ---------------------------------------------------------------- Binärformat (Version 1)
        // [0] Version, [1] Flags (1 Lack, 2 Bicolor, 4 Werksbeklebung entfernt), [2] Finish, [3..5] Lack RGB, [6..8] Lack 2 RGB,
        // [9] Anzahl, je Sticker 19 Byte: Planche, Nummer, U u16, V u16, Ax/Ay/Bx/By i16 (×2/32767), R, G, B, A, Flags.
        const int Head = 10, Per = 19;
        const float AxisScale = 2f / 32767f;

        public byte[] Encode()
        {
            int n = Math.Min(Stickers.Count, MaxStickers);
            var b = new byte[Head + n * Per];
            b[0] = 1;
            b[1] = (byte)((HasPaint ? 1 : 0) | (Bicolour ? 2 : 0) | (ClearFactory ? 4 : 0));
            b[2] = (byte)Finish;
            b[3] = Paint.r; b[4] = Paint.g; b[5] = Paint.b;
            b[6] = Paint2.r; b[7] = Paint2.g; b[8] = Paint2.b;
            b[9] = (byte)n;
            for (int i = 0; i < n; i++)
            {
                var s = Stickers[i]; int o = Head + i * Per;
                b[o] = s.Board; b[o + 1] = s.Index;
                U16(b, o + 2, Mathf.RoundToInt(Mathf.Clamp01(s.U) * 65535f));
                U16(b, o + 4, Mathf.RoundToInt(Mathf.Clamp01(s.V) * 65535f));
                I16(b, o + 6, s.A.x); I16(b, o + 8, s.A.y); I16(b, o + 10, s.B.x); I16(b, o + 12, s.B.y);
                b[o + 14] = s.Tint.r; b[o + 15] = s.Tint.g; b[o + 16] = s.Tint.b; b[o + 17] = s.Tint.a; b[o + 18] = s.Flags;
            }
            return b;
        }

        public static CarLivery Decode(byte[] b)
        {
            if (b == null || b.Length < Head || b[0] != 1) return null;
            var l = new CarLivery
            {
                HasPaint = (b[1] & 1) != 0, Bicolour = (b[1] & 2) != 0, ClearFactory = (b[1] & 4) != 0,
                Finish = b[2] <= 3 ? (PaintFinish)b[2] : PaintFinish.Original,
                Paint = new Color32(b[3], b[4], b[5], 255), Paint2 = new Color32(b[6], b[7], b[8], 255),
            };
            int n = Math.Min((int)b[9], MaxStickers);
            for (int i = 0; i < n; i++)
            {
                int o = Head + i * Per;
                if (o + Per > b.Length) break;
                l.Stickers.Add(new StickerPlacement
                {
                    Board = b[o], Index = b[o + 1],
                    U = (b[o + 2] | (b[o + 3] << 8)) / 65535f, V = (b[o + 4] | (b[o + 5] << 8)) / 65535f,
                    A = new Vector2(RI16(b, o + 6), RI16(b, o + 8)), B = new Vector2(RI16(b, o + 10), RI16(b, o + 12)),
                    Tint = new Color32(b[o + 14], b[o + 15], b[o + 16], b[o + 17]), Flags = b[o + 18],
                });
            }
            return l;
        }

        static void U16(byte[] b, int o, int v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        static void I16(byte[] b, int o, float f) { int v = Mathf.Clamp(Mathf.RoundToInt(f / AxisScale), -32767, 32767); b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        static float RI16(byte[] b, int o) => (short)(b[o] | (b[o + 1] << 8)) * AxisScale;

        /// <summary>FNV-1a über die Daten (0 = keine Beklebung).</summary>
        public static uint Hash(byte[] d)
        {
            if (d == null || d.Length == 0) return 0;
            uint h = 2166136261;
            foreach (var x in d) { h ^= x; h *= 16777619; }
            return h == 0 ? 1u : h;
        }

        public string ToBase64() => Convert.ToBase64String(Encode());
        public static CarLivery FromBase64(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try { return Decode(Convert.FromBase64String(s)); } catch (Exception) { return null; }
        }
    }

    // =====================================================================================================================
    /// <summary>
    /// Speicher der eigenen Lackierungen (je Auto-Datei), gekauften Planchen und "Meine Farben".
    /// Angemeldet: im Spielstand (SaveData.liveries …, Server); zusätzlich immer als Kopie in den PlayerPrefs
    /// (je Profil) – so gehen sie auch mit einem älteren Server, der die Felder noch nicht kennt, nicht verloren.
    /// </summary>
    public static class TDU2Liveries
    {
        static readonly Dictionary<string, CarLivery> _map = new Dictionary<string, CarLivery>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, byte[]> _blob = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<int> _boards = new HashSet<int>();
        static readonly int[] _fav = { -1, -1, -1, -1, -1, -1 };
        static string _loadedFor;
        static bool _hooked;
        /// <summary>Lackierung eines Autos geändert (Auto-Datei).</summary>
        public static event Action<string> Changed;

        [Serializable] sealed class Prefs { public List<LiveryEntry> liveries = new List<LiveryEntry>(); public List<int> boards = new List<int>(); public List<int> fav = new List<int>(); }

        static string Profile => TDU2Session.LoggedIn && !string.IsNullOrEmpty(TDU2Session.Nick) ? TDU2Session.Nick : "local";
        static string PrefsKey => "TDU2.Liveries." + Profile;

        static void Ensure()
        {
            if (!_hooked) { _hooked = true; TDU2Session.Changed += () => _loadedFor = null; }
            string who = Profile + (TDU2Session.Save != null ? "#s" : "");
            if (_loadedFor == who) return;
            _loadedFor = who;
            _map.Clear(); _blob.Clear(); _boards.Clear();
            for (int i = 0; i < _fav.Length; i++) _fav[i] = -1;
            // 1) lokale Kopie
            try
            {
                var s = PlayerPrefs.GetString(PrefsKey, "");
                if (!string.IsNullOrEmpty(s))
                {
                    var p = JsonUtility.FromJson<Prefs>(s);
                    if (p != null)
                    {
                        foreach (var e in p.liveries) { var l = CarLivery.FromBase64(e.data); if (l != null && !string.IsNullOrEmpty(e.car)) _map[e.car] = l; }
                        foreach (var b in p.boards) _boards.Add(b);
                        for (int i = 0; i < _fav.Length && i < p.fav.Count; i++) _fav[i] = p.fav[i];
                    }
                }
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Lackierungen (lokal): " + ex.Message); }
            // 2) Spielstand des Servers hat Vorrang (wenn er die Felder kennt)
            var sv = TDU2Session.Save;
            if (sv != null)
            {
                if (sv.liveries != null) foreach (var e in sv.liveries) { var l = CarLivery.FromBase64(e.data); if (l != null && !string.IsNullOrEmpty(e.car)) _map[e.car] = l; }
                if (sv.stickerBoards != null) foreach (var b in sv.stickerBoards) _boards.Add(b);
                if (sv.favoriteColors != null && sv.favoriteColors.Count > 0) for (int i = 0; i < _fav.Length && i < sv.favoriteColors.Count; i++) _fav[i] = sv.favoriteColors[i];
                WriteSave(sv);
            }
        }

        static void WriteSave(SaveData sv)
        {
            if (sv == null) return;
            sv.liveries = new List<LiveryEntry>();
            foreach (var kv in _map) if (kv.Value != null && !kv.Value.IsEmpty) sv.liveries.Add(new LiveryEntry { car = kv.Key, data = kv.Value.ToBase64() });
            sv.stickerBoards = new List<int>(_boards);
            sv.favoriteColors = new List<int>(_fav);
        }

        static void Persist(bool server)
        {
            var p = new Prefs();
            foreach (var kv in _map) if (kv.Value != null && !kv.Value.IsEmpty) p.liveries.Add(new LiveryEntry { car = kv.Key, data = kv.Value.ToBase64() });
            p.boards.AddRange(_boards); p.fav.AddRange(_fav);
            try { PlayerPrefs.SetString(PrefsKey, JsonUtility.ToJson(p)); PlayerPrefs.Save(); } catch (Exception) { }
            if (TDU2Session.Save != null)
            {
                WriteSave(TDU2Session.Save);
                if (server && TDU2Session.LoggedIn) TDU2Session.SaveAsync();
            }
        }

        /// <summary>Lackierung eines Autos (Kopie nicht nötig – nicht verändern!), null = Werkszustand.</summary>
        public static CarLivery Get(string carFile)
        {
            if (string.IsNullOrEmpty(carFile)) return null;
            Ensure();
            return _map.TryGetValue(carFile, out var l) ? l : null;
        }

        public static void Set(string carFile, CarLivery l)
        {
            if (string.IsNullOrEmpty(carFile)) return;
            Ensure();
            if (l == null || l.IsEmpty) _map.Remove(carFile); else _map[carFile] = l.Clone();
            _blob.Remove(carFile);
            Persist(true);
            try { Changed?.Invoke(carFile); } catch (Exception ex) { Debug.LogException(ex); }
        }

        /// <summary>Codierte Lackierung (für die Online-Synchronisation), null = keine.</summary>
        public static byte[] Blob(string carFile)
        {
            if (string.IsNullOrEmpty(carFile)) return null;
            Ensure();
            if (_blob.TryGetValue(carFile, out var b)) return b;
            b = _map.TryGetValue(carFile, out var l) && l != null && !l.IsEmpty ? l.Encode() : null;
            _blob[carFile] = b;
            return b;
        }

        public static bool HasBoard(int id) { Ensure(); return _boards.Contains(id); }
        public static void AddBoards(IEnumerable<int> ids) { Ensure(); foreach (var i in ids) _boards.Add(i); Persist(false); }

        public static int Favorite(int slot) { Ensure(); return slot >= 0 && slot < _fav.Length ? _fav[slot] : -1; }
        public static void SetFavorite(int slot, int rgb) { Ensure(); if (slot < 0 || slot >= _fav.Length) return; _fav[slot] = rgb; Persist(false); }
    }

    // =====================================================================================================================
    /// <summary>
    /// Sticker-Planchen (DBStickersBanks, scenarics/Packs/stickers/&lt;_bankfile&gt;.bnk): je Bank 24 Sticker als 512er DXT5
    /// (Material MAT_01..MAT_24 der .2dm bestimmt die Reihenfolge), einfarbige Planchen (_monochrome) sind weiß + Alpha und
    /// werden eingefärbt. Preise: DBStickersPricing (Labour/Paint/Erase). Namen: _name_hc (Textdatenbank).
    /// </summary>
    public static class TDU2StickerLibrary
    {
        public sealed class Board
        {
            public int Id;                  // _hashcode
            public string BankFile;         // z.B. st_basic_nb
            public int Type;                // 1 Shop, 3 DLC, 4 Casino, 5 Verfolgung
            public bool Mono;
            public int Price;
            public ulong NameHc;
            public string Path;
            public Texture2D[] Textures;
            public bool Loading, Loaded, Failed;
            internal Task<List<TextureDef>> Task;
            public int Count => Textures != null ? Textures.Length : 0;
        }

        public static readonly List<Board> Boards = new List<Board>();
        public static int LabourPrice = 1000, PaintPrice = 1000, ErasePrice = 1000;
        /// <summary>Zählt jede fertig geladene Planche (Ansichten rendern dann neu).</summary>
        public static int Version { get; private set; }
        static string _bnkDir;
        static Task<XmbNode> _dbTask;
        static bool _dbReady;
        static Pump _pump;

        sealed class Pump : MonoBehaviour { void Update() => TDU2StickerLibrary.Update(); }

        public static bool Ready => _dbReady;

        /// <summary>Datenbank im Hintergrund lesen (einmal je Bnk-Ordner).</summary>
        public static void Ensure(string bnkDir)
        {
            if (string.IsNullOrEmpty(bnkDir)) return;
            if (_pump == null)
            {
                var go = new GameObject("TDU2 Sticker Library") { hideFlags = HideFlags.HideAndDontSave };
                UnityEngine.Object.DontDestroyOnLoad(go);
                _pump = go.AddComponent<Pump>();
            }
            if (_bnkDir == bnkDir) return;
            _bnkDir = bnkDir; _dbReady = false; Boards.Clear();
            var db = CarDatabase.FindDatabase(bnkDir);
            if (db == null) { Debug.LogWarning("[TDU2] Sticker: db_data.cpr fehlt"); return; }
            _dbTask = System.Threading.Tasks.Task.Run(() => XmbFile.Parse(CprCrypto.Decrypt(File.ReadAllBytes(db))));
        }

        static void Update()
        {
            if (_dbTask != null && _dbTask.IsCompleted)
            {
                var t = _dbTask; _dbTask = null;
                if (t.IsFaulted) Debug.LogWarning("[TDU2] Sticker-Datenbank: " + t.Exception?.GetBaseException().Message);
                else ReadDb(t.Result);
                _dbReady = true;
            }
            foreach (var b in Boards)
            {
                if (b.Task == null || !b.Task.IsCompleted) continue;
                var t = b.Task; b.Task = null; b.Loading = false;
                if (t.IsFaulted || t.Result == null || t.Result.Count == 0)
                {
                    b.Failed = true;
                    Debug.LogWarning("[TDU2] Sticker-Planche " + b.BankFile + ": " + (t.Exception?.GetBaseException().Message ?? "keine Texturen"));
                    continue;
                }
                var tex = new Texture2D[t.Result.Count];
                for (int i = 0; i < tex.Length; i++)
                {
                    tex[i] = t.Result[i]?.CreateTexture(TextureWrapMode.Clamp, TextureWrapMode.Clamp);
                    if (tex[i] != null) { tex[i].name = b.BankFile + "_" + (i + 1).ToString("00"); tex[i].hideFlags = HideFlags.DontSave; }
                }
                b.Textures = tex; b.Loaded = true;
                Version++;
                Debug.Log("[TDU2] Sticker-Planche " + b.BankFile + ": " + tex.Length + " Sticker");
            }
        }

        static void ReadDb(XmbNode root)
        {
            Boards.Clear();
            var banks = root?.Get("DBStickersBanks");
            string dir = Path.Combine(_bnkDir, "scenarics", "packs", "stickers");
            if (banks != null)
                foreach (var r in banks.Items)
                {
                    var b = new Board
                    {
                        Id = r.I("_hashcode"), BankFile = r.S("_bankfile", ""), Type = r.I("_type"), Mono = r.B("_monochrome"),
                        Price = r.I("_price"), NameHc = r.U("_name_hc"),
                    };
                    if (!r.B("_isvalid", true) || b.Id <= 0 || b.Id > 255 || string.IsNullOrEmpty(b.BankFile)) continue;
                    b.Path = CarDatabase.FindFile(dir, b.BankFile + ".bnk");
                    if (b.Path == null) { Debug.LogWarning("[TDU2] Sticker-Planche fehlt: " + b.BankFile + ".bnk"); continue; }
                    Boards.Add(b);
                }
            var pr = root?.Get("DBStickersPricing");
            if (pr != null)
                foreach (var r in pr.Items)
                {
                    string n = (r.S("_actionname", "") ?? "").ToLowerInvariant();
                    if (n == "labour") LabourPrice = r.I("_price", LabourPrice);
                    else if (n == "paint") PaintPrice = r.I("_price", PaintPrice);
                    else if (n == "erase") ErasePrice = r.I("_price", ErasePrice);
                }
            Debug.Log("[TDU2] Sticker-Planchen: " + Boards.Count);
        }

        public static Board ById(int id)
        {
            foreach (var b in Boards) if (b.Id == id) return b;
            return null;
        }

        /// <summary>Planche laden (Hintergrund). true, wenn schon geladen.</summary>
        public static bool Request(Board b)
        {
            if (b == null) return false;
            if (b.Loaded) return true;
            if (b.Loading || b.Failed) return false;
            b.Loading = true;
            string path = b.Path;
            b.Task = System.Threading.Tasks.Task.Run(() => LoadBank(path));
            return false;
        }

        public static Texture2D Texture(int boardId, int index)
        {
            var b = ById(boardId);
            if (b == null || !Request(b) || b.Textures == null || index < 0 || index >= b.Textures.Length) return null;
            return b.Textures[index];
        }

        public static bool IsMono(int boardId) { var b = ById(boardId); return b != null && b.Mono; }

        /// <summary>Texturen der Bank in Sticker-Reihenfolge (MAT_01..MAT_24 der .2dm; sonst nach Dateinamen).</summary>
        static List<TextureDef> LoadBank(string path)
        {
            var bank = BnkArchive.Open(path);
            var byKey = new Dictionary<ulong, TextureDef>();
            var byName = new List<TextureDef>();
            foreach (var e in bank.All(".2db"))
            {
                TextureDef t = null;
                try { t = TextureDef.Parse(bank.Read(e), System.IO.Path.GetFileNameWithoutExtension(e.Name)); } catch (Exception) { }
                if (t == null) continue;
                byKey[t.Key] = t; byName.Add(t);
            }
            var result = new List<TextureDef>();
            var m = bank.FindByExt(".2dm");
            if (m != null)
            {
                Material2DM lib = null;
                try { lib = Material2DM.Parse(bank.Read(m)); } catch (Exception) { }
                var mats = new SortedDictionary<int, ulong>();
                if (lib != null)
                foreach (var def in lib.Materials.Values)
                {
                    var n = def.Name ?? "";
                    if (!n.StartsWith("MAT_", StringComparison.OrdinalIgnoreCase) || !int.TryParse(n.Substring(4), out int num)) continue;
                    foreach (var l in def.Layers) if (l.TextureKey != 0 && byKey.ContainsKey(l.TextureKey)) { mats[num] = l.TextureKey; break; }
                }
                foreach (var kv in mats) result.Add(byKey[kv.Value]);
            }
            if (result.Count == 0)
            {
                byName.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                result.AddRange(byName);
            }
            return result;
        }
    }

    // =====================================================================================================================
    /// <summary>
    /// Zeigt eine <see cref="CarLivery"/> an einem gebauten Auto: Lackfarbe(n) + Finish auf den Lack-Materialien
    /// (CarModel.PaintMaterials: Paint/Paint_LR = Farbe 1, Paint2 = Farbe 2 bei zweifarbiger Lackierung; _Color, _Metallic,
    /// _Glossiness, _ClearCoat, _Flake) und eine selbst gerenderte Stickermap (RenderTexture 1024², UV-Satz 1):
    ///   ohne Werks-Beklebung: transparente Map, Sticker als Dekor über dem Lack (_StickerAlphaOnly = 1)
    ///   mit Werks-Beklebung: Map = Werks-Beklebung + Sticker, ersetzt den Lack wie im Original (_StickerAlphaOnly = 0).
    /// Finish: Glänzend (Uni-Lack mit Klarlack), Metallic (Flakes + Farbverlauf), Matt (stumpf, ohne Klarlack).
    /// Optional Vorschau-Sticker (Sticker-Shop) ohne sie zu speichern.
    /// </summary>
    public sealed class TDU2LiveryView : MonoBehaviour
    {
        public CarModel Model { get; private set; }
        public CarLivery Livery { get; private set; }
        /// <summary>Vorschau (Sticker unter der Maus im Shop), wird nicht gespeichert.</summary>
        public readonly List<StickerPlacement> Preview = new List<StickerPlacement>();
        public static int MapSize = 1024;

        struct Orig { public Material M; public bool Second; public Color Color; public float Metal, Gloss, Coat, Flake, UseSticker, AlphaOnly; public Texture Sticker; }
        readonly List<Orig> _orig = new List<Orig>();
        Texture _factory;
        RenderTexture _rt, _tmp;
        bool _dirty;
        int _libVersion = -1;
        bool _waiting;
        static Material _stampMat;

        /// <summary>Ansicht am Modell (einmal je Modell, sonst die vorhandene).</summary>
        public static TDU2LiveryView For(CarModel model)
        {
            if (model == null || model.Visual == null) return null;
            var v = model.Visual.GetComponent<TDU2LiveryView>();
            if (v == null) { v = model.Visual.AddComponent<TDU2LiveryView>(); v.Init(model); }
            return v;
        }

        /// <summary>Gespeicherte Lackierung des Autos anzeigen (eigenes Auto, Garage, Shop).</summary>
        public static TDU2LiveryView ApplyOwn(CarModel model, string carFile)
        {
            var l = TDU2Liveries.Get(carFile);
            if (l == null) { var ex = model?.Visual != null ? model.Visual.GetComponent<TDU2LiveryView>() : null; if (ex != null) ex.Show(null); return ex; }
            var v = For(model);
            v?.Show(l);
            return v;
        }

        void Init(CarModel model)
        {
            Model = model;
            foreach (var m in model.PaintMaterials)
            {
                if (m == null) continue;
                _orig.Add(new Orig
                {
                    M = m, Second = CarModel.IsPaint2(m), Color = m.HasProperty("_Color") ? m.GetColor("_Color") : Color.white,
                    Metal = F(m, "_Metallic"), Gloss = F(m, "_Glossiness"), Coat = F(m, "_ClearCoat"), Flake = F(m, "_Flake"),
                    UseSticker = F(m, "_UseSticker"), AlphaOnly = F(m, "_StickerAlphaOnly"),
                    Sticker = m.HasProperty("_StickerTex") ? m.GetTexture("_StickerTex") : null,
                });
                if (_factory == null && F(m, "_UseSticker") > 0.5f && m.HasProperty("_StickerTex")) _factory = m.GetTexture("_StickerTex");
            }
        }

        static float F(Material m, string p) => m.HasProperty(p) ? m.GetFloat(p) : 0f;

        /// <summary>Werks-Beklebung vorhanden (DBCarPhysicsData._sticker).</summary>
        public bool HasFactory => _factory != null;
        /// <summary>Grundfarbe des Lacks (für den Farbwähler).</summary>
        public Color OriginalPaint { get { foreach (var o in _orig) if (!o.Second) return o.Color; return _orig.Count > 0 ? _orig[0].Color : Color.gray; } }
        /// <summary>Werks-Farbe 2 (Material Paint2), sonst Farbe 1.</summary>
        public Color OriginalPaint2 { get { foreach (var o in _orig) if (o.Second) return o.Color; return OriginalPaint; } }
        /// <summary>Auto hat eine zweite Lackfläche (Paint2).</summary>
        public bool HasSecondPaint { get { foreach (var o in _orig) if (o.Second) return true; return false; } }

        public void Show(CarLivery l) { Livery = l; _dirty = true; }
        public void Refresh() => _dirty = true;

        void LateUpdate()
        {
            if (_waiting && TDU2StickerLibrary.Version != _libVersion) _dirty = true;
            if (!_dirty) return;
            _dirty = false;
            Render();
        }

        void Render()
        {
            var l = Livery;
            // ---- Lack + Finish
            foreach (var o in _orig)
            {
                var m = o.M; if (m == null) continue;
                // eigene Lackierung: eine Farbe auf allen Lackflächen; zweifarbig = Bicolor-Lack (Farbwechsel je nach Blickwinkel)
                Color c = o.Color;
                if (l != null && l.HasPaint) c = l.Paint;
                c.a = o.Color.a;
                if (m.HasProperty("_Color")) m.SetColor("_Color", c);
                bool bi = l != null && l.HasPaint && l.Bicolour;
                if (m.HasProperty("_Color2")) m.SetColor("_Color2", bi ? (Color)l.Paint2 : c);
                if (m.HasProperty("_Bicolour")) m.SetFloat("_Bicolour", bi ? 1f : 0f);
                float metal = o.Metal, gloss = o.Gloss, coat = o.Coat, flake = o.Flake;
                switch (l != null ? l.Finish : PaintFinish.Original)
                {
                    case PaintFinish.Gloss: metal = 0.05f; gloss = 0.93f; coat = 1f; flake = 0f; break;        // normaler Uni-Lack
                    case PaintFinish.Metallic: metal = 0.35f; gloss = 0.86f; coat = 1f; flake = 0.7f; break;     // Metallic mit Flakes
                    case PaintFinish.Matt: metal = 0.02f; gloss = 0.18f; coat = 0f; flake = 0f; break;         // matt
                }
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metal);
                if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", gloss);
                if (m.HasProperty("_ClearCoat")) m.SetFloat("_ClearCoat", coat);
                if (m.HasProperty("_Flake")) m.SetFloat("_Flake", flake);
            }
            // ---- Stickermap
            bool factory = _factory != null && (l == null || !l.ClearFactory);
            int count = (l != null ? l.Stickers.Count : 0) + Preview.Count;
            _waiting = false;
            if (count == 0)
            {
                foreach (var o in _orig)
                {
                    if (o.M == null || !o.M.HasProperty("_StickerTex")) continue;
                    if (factory) { o.M.SetTexture("_StickerTex", o.Sticker); o.M.SetFloat("_UseSticker", o.UseSticker); o.M.SetFloat("_StickerAlphaOnly", o.AlphaOnly); }
                    else o.M.SetFloat("_UseSticker", 0f);
                }
                return;
            }
            if (_rt == null)
            {
                _rt = new RenderTexture(MapSize, MapSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
                { name = "TDU2 Stickermap", useMipMap = true, autoGenerateMips = true, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
                _rt.Create();
            }
            if (!EnsureMat()) return;
            var prev = RenderTexture.active;
            if (factory)
            {
                // Werks-Beklebung (enthält die Grundfarbe) deckend als Grund, Sticker darüber -> ersetzt den Lack
                RenderTexture.active = _rt;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, MapSize, 0, MapSize);
                GL.Clear(true, true, Color.black);
                DrawQuad(_factory, Color.white, 1, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0f), new Vector2(0f, 0.5f), false);
                if (l != null) foreach (var s in l.Stickers) Stamp(s);
                foreach (var s in Preview) Stamp(s);
                GL.PopMatrix();
            }
            else
            {
                // transparente Map (vormultipliziert gerendert, dann RGB / Alpha) -> Sticker als Dekor über Farbe 1/2
                if (_tmp == null)
                {
                    _tmp = new RenderTexture(MapSize, MapSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { name = "TDU2 Stickermap (Arbeit)", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
                    _tmp.Create();
                }
                RenderTexture.active = _tmp;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, MapSize, 0, MapSize);
                GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
                if (l != null) foreach (var s in l.Stickers) Stamp(s);
                foreach (var s in Preview) Stamp(s);
                GL.PopMatrix();
                RenderTexture.active = _rt;
                GL.PushMatrix();
                GL.LoadPixelMatrix(0, MapSize, 0, MapSize);
                GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
                DrawQuad(_tmp, Color.white, 2, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0f), new Vector2(0f, 0.5f), false);
                GL.PopMatrix();
            }
            RenderTexture.active = prev;
            foreach (var o in _orig)
            {
                if (o.M == null || !o.M.HasProperty("_StickerTex")) continue;
                o.M.SetTexture("_StickerTex", _rt);
                o.M.SetFloat("_UseSticker", 1f);
                o.M.SetFloat("_StickerAlphaOnly", factory ? 0f : 1f);
            }
            _libVersion = TDU2StickerLibrary.Version;
        }

        void Stamp(StickerPlacement s)
        {
            var tex = TDU2StickerLibrary.Texture(s.Board, s.Index);
            if (tex == null) { _waiting = true; return; }
            Color tint = TDU2StickerLibrary.IsMono(s.Board) ? (Color)s.Tint : new Color(1f, 1f, 1f, s.Tint.a / 255f);
            DrawQuad(tex, tint, 0, new Vector2(s.U, s.V), s.A, s.B, true);
        }

        /// <summary>Viereck in der Stickermap: Mitte c (UV), Halbachsen a (rechts) / b (oben); .2db-Textur: Zeile 0 = oben (v = 0).</summary>
        static void DrawQuad(Texture tex, Color tint, int pass, Vector2 c, Vector2 a, Vector2 b, bool raw2db)
        {
            _stampMat.mainTexture = tex;
            _stampMat.color = tint;
            _stampMat.SetPass(pass);
            float S = MapSize;
            Vector2 tl = (c - a + b) * S, tr = (c + a + b) * S, br = (c + a - b) * S, bl = (c - a - b) * S;
            float vt = raw2db ? 0f : 1f, vb = raw2db ? 1f : 0f;
            GL.Begin(GL.QUADS);
            GL.TexCoord2(0f, vt); GL.Vertex3(tl.x, tl.y, 0f);
            GL.TexCoord2(1f, vt); GL.Vertex3(tr.x, tr.y, 0f);
            GL.TexCoord2(1f, vb); GL.Vertex3(br.x, br.y, 0f);
            GL.TexCoord2(0f, vb); GL.Vertex3(bl.x, bl.y, 0f);
            GL.End();
        }

        static bool EnsureMat()
        {
            if (_stampMat != null) return true;
            var sh = Shader.Find("Hidden/TDU2/Sticker");
            if (sh == null) { Debug.LogWarning("[TDU2] Shader Hidden/TDU2/Sticker fehlt (TDU2Sticker.shader)"); return false; }
            _stampMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        void OnDestroy()
        {
            if (_rt != null) { if (RenderTexture.active == _rt) RenderTexture.active = null; _rt.Release(); Destroy(_rt); _rt = null; }
            if (_tmp != null) { if (RenderTexture.active == _tmp) RenderTexture.active = null; _tmp.Release(); Destroy(_tmp); _tmp = null; }
        }
    }
}
