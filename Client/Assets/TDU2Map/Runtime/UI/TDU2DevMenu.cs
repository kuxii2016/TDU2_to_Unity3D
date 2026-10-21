using System;
using System.Collections.Generic;
using System.Globalization;
using TDU2.Map.Online;
using TDU2.Map.Traffic;
using TDU2.Map.World;
using UnityEngine;
using UProfiler = UnityEngine.Profiling.Profiler;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Entwicklermenü nach dem Vorbild des Debug-Menüs der Original-exe (edDebug "DEBUG MENU LIBRARIES": Textmenü mit
    /// Bibliotheken, Variablen VAR/FVAR/BOOL/TABLE, Tasten NEXT/PREV VARIABLE, INCREASE/DECREASE VALUE, INCREASE/DECREASE
    /// STEP, NEXT/PREV 10, FIRST/LAST, RESET VALUE, Farben BLUE/PURPLE/GREEN/CYAN/YELLOW) – auf Deutsch, mit den Bereichen
    /// Netzwerk, Autos, Wetter, Welt laden, Streaming, Jobs, Verkehr, Rendering und Auslastung.
    /// Bedienung: F1 öffnen/schließen, ↑/↓ Eintrag, ←/→ Wert (Umschalt = großer Schritt), Enter öffnen/ausführen,
    /// Rücktaste zurück, Bild↑/↓ 10 Einträge, Pos1/Ende erster/letzter Eintrag, Entf Standardwert.
    /// Solange es offen ist, gehen die Fahr-/Spieltasten nicht ans Spiel (TDU2Controls.Blocked).
    /// </summary>
    public sealed class TDU2DevMenu : MonoBehaviour
    {
        public static TDU2DevMenu Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance._open;

        [Tooltip("Taste zum Öffnen/Schließen")] public KeyCode ToggleKey = KeyCode.F1;
        [Tooltip("Größe der Schrift (Pixel bei 1080p)")] public int FontSize = 12;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void AutoCreate()
        {
            if (Instance != null) return;
            var go = new GameObject("TDU2 Dev Menu");
            DontDestroyOnLoad(go);
            go.AddComponent<TDU2DevMenu>();
        }

        // ------------------------------------------------------------------ Menümodell
        sealed class Item
        {
            public string Label;
            public Func<string> Value;              // null = Untermenü/Aktion ohne Wert
            public Action<int> Change;              // ←/→ (Richtung x Schrittweite)
            public Action Reset;                    // Entf
            public Action Activate;                 // Enter
            public Func<List<Item>> Sub;            // Enter: Untermenü
            public bool Header;                     // nur Anzeige
        }

        sealed class Page { public string Title; public Func<List<Item>> Build; public int Sel, Top; }

        readonly List<Page> _stack = new List<Page>();
        List<Item> _items = new List<Item>();
        bool _open, _blockedByMe;
        float _repeatAt; int _repeatDir; KeyCode _repeatKey;
        string _toast; float _toastUntil;
        GUIStyle _style, _boxStyle;
        Texture2D _bg;

        TDU2VehicleManager _mgr;
        TDU2VehicleManager Mgr
        {
            get
            {
                if (_mgr == null)
                {
#if UNITY_2023_1_OR_NEWER
                    _mgr = FindFirstObjectByType<TDU2VehicleManager>();
#else
                    _mgr = FindObjectOfType<TDU2VehicleManager>();
#endif
                }
                return _mgr;
            }
        }

        TDU2Traffic Traffic
        {
            get
            {
#if UNITY_2023_1_OR_NEWER
                return FindFirstObjectByType<TDU2Traffic>();
#else
                return FindObjectOfType<TDU2Traffic>();
#endif
            }
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_blockedByMe) TDU2Controls.Blocked = false;
            if (_bg != null) Destroy(_bg);
            StopRecorders();
        }

        // ------------------------------------------------------------------ Eingabe
        void Update()
        {
            SampleFrame();
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(ToggleKey) && !TDU2GameData.ChatTyping)
            {
                if (_open) Close(); else Open();
                return;
            }
            if (!_open) return;
            // Chat o.ä. hat die Tastatur übernommen
            if (!TDU2Controls.Blocked) { TDU2Controls.Blocked = true; _blockedByMe = true; }

            Rebuild();
            var p = _stack[_stack.Count - 1];
            bool big = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (KeyRepeat(KeyCode.DownArrow)) Move(p, 1);
            if (KeyRepeat(KeyCode.UpArrow)) Move(p, -1);
            if (KeyRepeat(KeyCode.PageDown)) Move(p, 10);
            if (KeyRepeat(KeyCode.PageUp)) Move(p, -10);
            if (Input.GetKeyDown(KeyCode.Home)) { p.Sel = 0; Move(p, 0); }
            if (Input.GetKeyDown(KeyCode.End)) { p.Sel = _items.Count - 1; Move(p, 0); }
            var it = p.Sel >= 0 && p.Sel < _items.Count ? _items[p.Sel] : null;
            if (it != null && !it.Header)
            {
                if (KeyRepeat(KeyCode.RightArrow)) it.Change?.Invoke(big ? 10 : 1);
                if (KeyRepeat(KeyCode.LeftArrow)) it.Change?.Invoke(big ? -10 : -1);
                if (Input.GetKeyDown(KeyCode.Delete)) it.Reset?.Invoke();
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    if (it.Sub != null) _stack.Add(new Page { Title = it.Label, Build = it.Sub });
                    else if (it.Activate != null) it.Activate();
                    else it.Change?.Invoke(1);
                }
            }
            if (Input.GetKeyDown(KeyCode.Backspace))
            {
                if (_stack.Count > 1) _stack.RemoveAt(_stack.Count - 1); else Close();
            }
#endif
        }

        bool KeyRepeat(KeyCode k)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(k)) { _repeatKey = k; _repeatAt = Time.unscaledTime + 0.4f; return true; }
            if (_repeatKey == k && Input.GetKey(k) && Time.unscaledTime >= _repeatAt) { _repeatAt = Time.unscaledTime + 0.06f; return true; }
#endif
            return false;
        }

        void Move(Page p, int d)
        {
            if (_items.Count == 0) { p.Sel = 0; return; }
            int s = Mathf.Clamp(p.Sel + d, 0, _items.Count - 1);
            // Überschriften überspringen
            int dir = d >= 0 ? 1 : -1;
            while (s >= 0 && s < _items.Count && _items[s].Header) s += dir;
            if (s < 0 || s >= _items.Count) { s = Mathf.Clamp(p.Sel, 0, _items.Count - 1); }
            p.Sel = s;
        }

        void Open()
        {
            _open = true;
            _stack.Clear();
            _stack.Add(new Page { Title = "ENTWICKLERMENÜ", Build = MainMenu });
            if (!TDU2Controls.Blocked) { TDU2Controls.Blocked = true; _blockedByMe = true; }
            Rebuild(); Move(_stack[0], 0);
        }

        void Close()
        {
            _open = false;
            if (_blockedByMe) { TDU2Controls.Blocked = false; _blockedByMe = false; }
        }

        void Rebuild()
        {
            var p = _stack[_stack.Count - 1];
            try { _items = p.Build() ?? new List<Item>(); }
            catch (Exception ex) { _items = new List<Item> { Info("Fehler: " + ex.Message) }; }
            if (p.Sel >= _items.Count) p.Sel = Math.Max(0, _items.Count - 1);
            if (_items.Count > 0 && _items[p.Sel].Header) Move(p, 1);
        }

        void Toast(string s) { _toast = s; _toastUntil = Time.unscaledTime + 3f; Debug.Log("[TDU2] Entwicklermenü: " + s); }

        // ------------------------------------------------------------------ Bausteine
        static Item Head(string t) => new Item { Label = t, Header = true };
        static Item Info(string label, Func<string> v = null) => new Item { Label = label, Value = v, Header = true };

        static Item Bool(string label, Func<bool> get, Action<bool> set, bool def) => new Item
        {
            Label = label,
            Value = () => get() ? "AN" : "AUS",
            Change = _ => set(!get()),
            Reset = () => set(def),
        };

        static Item Float(string label, Func<float> get, Action<float> set, float step, float min, float max, float def, string fmt = "0.##", string unit = "") => new Item
        {
            Label = label,
            Value = () => get().ToString(fmt, CultureInfo.GetCultureInfo("de-DE")) + unit,
            Change = d => set(Mathf.Clamp(get() + step * d, min, max)),
            Reset = () => set(def),
        };

        static Item Int(string label, Func<int> get, Action<int> set, int min, int max, int def) => new Item
        {
            Label = label,
            Value = () => get().ToString(CultureInfo.InvariantCulture),
            Change = d => set(Mathf.Clamp(get() + d, min, max)),
            Reset = () => set(def),
        };

        static Item Table(string label, string[] names, Func<int> get, Action<int> set, int def) => new Item
        {
            Label = label,
            Value = () => { int i = get(); return i >= 0 && i < names.Length ? names[i] : i.ToString(); },
            Change = d => set(((get() + Math.Sign(d)) % names.Length + names.Length) % names.Length),
            Reset = () => set(def),
        };

        static Item Action_(string label, Action a) => new Item { Label = label, Activate = a, Value = () => "[Enter]" };
        static Item Menu(string label, Func<List<Item>> sub) => new Item { Label = label, Sub = sub, Value = () => ">>" };

        // ------------------------------------------------------------------ Hauptmenü
        List<Item> MainMenu() => new List<Item>
        {
            Head("BIBLIOTHEKEN"),
            Menu("Netzwerk", NetworkMenu),
            Menu("Autos", CarsMenu),
            Menu("Wetter & Tageszeit", WeatherMenu),
            Menu("Welt laden", WorldMenu),
            Menu("Streaming", StreamingMenu),
            Menu("Jobs & Threads", JobsMenu),
            Menu("Verkehr", TrafficMenu),
            Menu("Rendering", RenderingMenu),
            Menu("Auslastung", UsageMenu),
            Menu("Avatar", AvatarMenu),
            Menu("Flash Debug", FlashMenu),
            Menu("GPS / Lesezeichen", GpsMenu),
            Head(""),
            Info("F1 schließen · ↑↓ Eintrag · ←→ Wert (Umschalt x10) · Enter öffnen · Rücktaste zurück · Entf Standard"),
        };

        // ------------------------------------------------------------------ Flash-Filme (wie der Flash Debugger im Editor)
        List<Item> FlashMenu()
        {
            var l = new List<Item> { Head("LAUFENDE FILME") };
            var players = FindAll<Flash.VmfPlayer>();
            foreach (var p in players)
            {
                if (p == null || p.Movie == null) continue;
                var pp = p;
                string file = System.IO.Path.GetFileName(p.BankPath ?? "");
                l.Add(Menu(p.gameObject.name + "  (" + file + ")" + (p.Visible ? "" : "  [unsichtbar]"), () => FlashPlayerMenu(pp)));
            }
            if (players.Length == 0) l.Add(Info("kein Flash-Film aktiv"));
            l.Add(Head("FILM LADEN (frontend)"));
            l.Add(Menu("Frontend-Bank öffnen …", FlashLoadMenu));
            var viewers = new List<Flash.VmfPlayer>();
            foreach (var p in players) if (p != null && p.gameObject.name.StartsWith("FlashViewer ")) viewers.Add(p);
            if (viewers.Count > 0)
                l.Add(Action_("Geladene Viewer schließen (" + viewers.Count + ")", () => { foreach (var v in viewers) if (v != null) Destroy(v.gameObject); Toast("Viewer geschlossen"); Rebuild(); }));
            return l;
        }

        List<Item> FlashLoadMenu()
        {
            var l = new List<Item>();
            var m = Mgr;
            if (m == null || string.IsNullOrEmpty(m.BnkDir)) { l.Add(Info("Bnk-Ordner unbekannt")); return l; }
            foreach (var res in new[] { "hires", "medres", "lowres" })
            {
                string dir = System.IO.Path.Combine(m.BnkDir, "frontend", res);
                if (!System.IO.Directory.Exists(dir)) continue;
                l.Add(Head(res.ToUpperInvariant()));
                var files = System.IO.Directory.GetFiles(dir, "*.bnk");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    string path = f;
                    l.Add(Action_(System.IO.Path.GetFileNameWithoutExtension(f), () => LoadFlashViewer(path)));
                }
                break;
            }
            return l;
        }

        void LoadFlashViewer(string path)
        {
            var go = new GameObject("FlashViewer " + System.IO.Path.GetFileNameWithoutExtension(path));
            var p = go.AddComponent<Flash.VmfPlayer>();
            p.GuiDepth = -20; p.MouseEnabled = true;
            try
            {
                p.Load(path);
                var pm = Mgr != null ? Mgr.GetComponent<TDU2PauseMenu>() : null;
                if (pm?.Texts != null) Flash.FlashLocalization.Apply(p, pm.Texts);
                Toast("Geladen: " + System.IO.Path.GetFileName(path) + " – Labels unter Flash-Filme > Film > Wurzel");
            }
            catch (Exception ex) { Destroy(go); Toast("Fehler: " + ex.Message); }
        }

        List<Item> FlashPlayerMenu(Flash.VmfPlayer p)
        {
            var l = new List<Item>();
            if (p == null || p.Root == null) { l.Add(Info("Film nicht mehr vorhanden")); return l; }
            l.Add(Head("FILM " + System.IO.Path.GetFileName(p.BankPath ?? "")));
            l.Add(Bool("Sichtbar", () => p.Visible, v => p.Visible = v, true));
            l.Add(Bool("Maus", () => p.MouseEnabled, v => p.MouseEnabled = v, false));
            l.Add(Int("GUI-Tiefe", () => p.GuiDepth, v => p.GuiDepth = v, -100, 100, 0));
            l.Add(Info("Fokus (_root.Clip)", () => p.GetVariable("/:Clip") ?? "-"));
            l.Add(Action_("Film zurücksetzen (neu laden + einrichten)", () => ResetFlash(p)));
            l.Add(Info("Achtung: Labels/Bilder der Wurzel bauen Menüs/HUD um – dann hier zurücksetzen"));
            l.Add(Head("EINGABE (wie das Spiel)"));
            foreach (var (lab, fn, arg) in new (string, string, string)[] { ("▲ hoch", "Move", "up"), ("▼ runter", "Move", "down"), ("◀ links", "Move", "left"), ("▶ rechts", "Move", "right"), ("A", "A", null), ("B", "B", null), ("X", "X", null), ("Y", "Y", null) })
            {
                string f2 = fn, a2 = arg;
                l.Add(Action_(lab, () => { bool ok = a2 != null ? p.Input(f2, a2) : p.Input(f2); Toast(f2 + (a2 != null ? "(" + a2 + ")" : "") + (ok ? " verarbeitet" : ": keine Funktion")); }));
            }
            l.AddRange(FlashClipItems(p, "/", p.Root));
            return l;
        }

        /// <summary>Film neu laden. HUD (Minimap) wird wie beim Start eingerichtet; andere Filme starten im Ausgangszustand.</summary>
        void ResetFlash(Flash.VmfPlayer p)
        {
            if (p == null || string.IsNullOrEmpty(p.BankPath)) return;
            var mm = Mgr != null ? Mgr.Minimap : null;
            if (mm != null && mm.HudPlayer == p) { mm.ReloadHud(); Toast("HUD neu geladen"); Rebuild(); return; }
            try
            {
                bool vis = p.Visible;
                p.Load(p.BankPath);
                p.Visible = vis;
                var pm = Mgr != null ? Mgr.GetComponent<TDU2PauseMenu>() : null;
                if (pm?.Texts != null) Flash.FlashLocalization.Apply(p, pm.Texts);
                Toast("Neu geladen: " + System.IO.Path.GetFileName(p.BankPath) + " (Ausgangszustand – ggf. Menü erneut öffnen)");
            }
            catch (Exception ex) { Toast("Fehler: " + ex.Message); }
            Rebuild();
        }

        static string FlashLabel(Flash.VmfPlayer.Instance inst)
        {
            string lab = null; int best = -1;
            foreach (var kv in inst.Timeline.Labels) if (kv.Value <= inst.Frame && kv.Value > best) { best = kv.Value; lab = kv.Key; }
            return lab;
        }

        /// <summary>Zeitleiste eines Clips: Abspielen/Stoppen, Bild, Labels (gotoAndPlay / gotoAndStop), Unter-Clips.</summary>
        List<Item> FlashClipItems(Flash.VmfPlayer p, string path, Flash.VmfPlayer.Instance inst)
        {
            var l = new List<Item>();
            if (inst == null) { l.Add(Info("Clip nicht mehr vorhanden")); return l; }
            l.Add(Head("CLIP " + path));
            l.Add(Info("Bild / Label", () => (inst.Frame + 1) + " / " + inst.Timeline.FrameCount + "   [" + (FlashLabel(inst) ?? "-") + "]" + (inst.Playing ? "  ▶" : "  ■")));
            l.Add(Bool("Abspielen", () => inst.Playing, v => inst.Playing = v, true));
            l.Add(Int("Bild", () => inst.Frame + 1, v => p.GotoFrame(inst, Mathf.Clamp(v - 1, 0, Math.Max(0, inst.Timeline.FrameCount - 1)), true), 1, Math.Max(1, inst.Timeline.FrameCount), 1));
            var owner = inst.Owner;
            if (owner != null) l.Add(Bool("Sichtbar (_visible)", () => owner.Visible, v => owner.Visible = v, true));
            if (inst.Timeline.Labels.Count > 0)
            {
                l.Add(Head("LABELS (Enter = gotoAndPlay, ←→ = gotoAndStop)"));
                var labs = new List<KeyValuePair<string, int>>(inst.Timeline.Labels);
                labs.Sort((a, b) => a.Value.CompareTo(b.Value));
                foreach (var kv in labs)
                {
                    string lab = kv.Key;
                    l.Add(new Item
                    {
                        Label = lab + "  (Bild " + (kv.Value + 1) + ")",
                        Value = () => FlashLabel(inst) == lab ? "◀ aktiv" : "",
                        Activate = () => { p.GotoLabel(path, lab, true); Toast(path + " → " + lab + " (Play)"); },
                        Change = _ => { p.GotoLabel(path, lab, false); Toast(path + " → " + lab + " (Stop)"); },
                    });
                }
            }
            var kids = new List<Item>();
            foreach (var d in inst.List.Values)
            {
                if (d.Sprite == null) continue;
                string name = d.Name ?? ("#" + d.Depth);
                string cp = (path == "/" ? "/" : path + "/") + name;
                var sub = d.Sprite;
                bool shown = d.Visible && !(d.Cx.Mul.w <= 0.003f && d.Cx.Add.w <= 0.003f);
                kids.Add(Menu(name + (shown ? "" : "  [unsichtbar]") + "  (" + sub.Timeline.FrameCount + " B., " + sub.Timeline.Labels.Count + " Labels)", () => FlashClipItems(p, cp, sub)));
            }
            if (kids.Count > 0) { l.Add(Head("UNTER-CLIPS")); l.AddRange(kids); }
            return l;
        }

        // ------------------------------------------------------------------ Avatare
        static readonly string[] SexNames = { "Mann", "Frau" };
        static readonly string[] EthNames = { "Europäisch", "Asiatisch", "Afrikanisch" };
        static readonly (Avatars.ClothePart part, string name)[] AvatarParts =
        {
            (Avatars.ClothePart.Hair, "Haare"), (Avatars.ClothePart.Top, "Oberteil"), (Avatars.ClothePart.Bottom, "Hose / Rock"),
            (Avatars.ClothePart.Dress, "Kleid"), (Avatars.ClothePart.Shoes, "Schuhe"), (Avatars.ClothePart.Helmet, "Hut / Helm"),
            (Avatars.ClothePart.Glasses, "Brille"), (Avatars.ClothePart.Gloves, "Handschuhe"), (Avatars.ClothePart.Beard, "Bart"),
            (Avatars.ClothePart.Accessories, "Accessoires"), (Avatars.ClothePart.Belt, "Gurt"),
        };
        List<string> _clipNames;

        /// <summary>Globaler Shader-Wert (Shader.SetGlobalFloat), z.B. Feinabstimmung der Avatar-Shader.</summary>
        static Item GlobalFloat(string label, string name, float step, float min, float max, float def = 0f) =>
            Float(label, () => Shader.GetGlobalFloat(name), v => Shader.SetGlobalFloat(name, v), step, min, max, def, "0.00");

        static Avatars.AvatarConfig Cfg => Avatars.AvatarConfigs.Local;
        static void SetCfg(Avatars.AvatarConfig c) => Avatars.AvatarConfigs.SetLocal(c);

        /// <summary>Alle Schalter aus TDU2OnlineSettings (Standardwerte = die in der Datei).</summary>
        List<Item> OnlineSettingsMenu()
        {
            var l = new List<Item>
            {
                Head("MITSPIELER IN INTERIEURS"),
                Bool("Spielfiguren in Interieurs", () => TDU2OnlineSettings.ShowPlayersInInteriors, v => TDU2OnlineSettings.ShowPlayersInInteriors = v, true),
                Bool("Kleidungsläden", () => TDU2OnlineSettings.ShowInClothesShops, v => TDU2OnlineSettings.ShowInClothesShops = v, true),
                Bool("Autohändler", () => TDU2OnlineSettings.ShowInCarDealers, v => TDU2OnlineSettings.ShowInCarDealers = v, true),
                Bool("Motorradhändler", () => TDU2OnlineSettings.ShowInBikeDealers, v => TDU2OnlineSettings.ShowInBikeDealers = v, true),
                Bool("Clubs / Community Racing Center", () => TDU2OnlineSettings.ShowInClubs, v => TDU2OnlineSettings.ShowInClubs = v, true),
                Bool("Casino", () => TDU2OnlineSettings.ShowInCasino, v => TDU2OnlineSettings.ShowInCasino = v, true),
                Bool("Spielhalle", () => TDU2OnlineSettings.ShowInGamblingHall, v => TDU2OnlineSettings.ShowInGamblingHall = v, true),
                Bool("Flughafen", () => TDU2OnlineSettings.ShowInAirport, v => TDU2OnlineSettings.ShowInAirport = v, true),
                Bool("Friseur", () => TDU2OnlineSettings.ShowInHairdresser, v => TDU2OnlineSettings.ShowInHairdresser = v, true),
                Bool("Schönheitsklinik", () => TDU2OnlineSettings.ShowInSurgery, v => TDU2OnlineSettings.ShowInSurgery = v, true),
                Bool("Makler", () => TDU2OnlineSettings.ShowInRealtor, v => TDU2OnlineSettings.ShowInRealtor = v, true),
                Bool("Tuner", () => TDU2OnlineSettings.ShowInTuner, v => TDU2OnlineSettings.ShowInTuner = v, true),
                Bool("Lackiererei / Sticker", () => TDU2OnlineSettings.ShowInStickerShop, v => TDU2OnlineSettings.ShowInStickerShop = v, true),
                Bool("Fahrschule (Gebäude)", () => TDU2OnlineSettings.ShowInRacingSchool, v => TDU2OnlineSettings.ShowInRacingSchool = v, true),
                Bool("Fotograf", () => TDU2OnlineSettings.ShowInPhotograph, v => TDU2OnlineSettings.ShowInPhotograph = v, true),
                Bool("Auktionshaus", () => TDU2OnlineSettings.ShowInAuctionHall, v => TDU2OnlineSettings.ShowInAuctionHall = v, true),
                Bool("Hehler", () => TDU2OnlineSettings.ShowInStolenCarDealer, v => TDU2OnlineSettings.ShowInStolenCarDealer = v, true),
                Bool("Andere Interieurs", () => TDU2OnlineSettings.ShowInOtherInteriors, v => TDU2OnlineSettings.ShowInOtherInteriors = v, true),
                Bool("Häuser", () => TDU2OnlineSettings.ShowInHouses, v => TDU2OnlineSettings.ShowInHouses = v, true),
                Bool("Häuser nur mit Einladung", () => TDU2OnlineSettings.HousesInviteOnly, v => TDU2OnlineSettings.HousesInviteOnly = v, true),
                Head("MITSPIELER-AUTOS"),
                Bool("In Cups/Einzelrennen ausblenden", () => TDU2OnlineSettings.HidePlayersInCups, v => TDU2OnlineSettings.HidePlayersInCups = v, true),
                Bool("In Fahrschul-Prüfungen ausblenden", () => TDU2OnlineSettings.HidePlayersInRacingSchool, v => TDU2OnlineSettings.HidePlayersInRacingSchool = v, true),
                Bool("Im Sticker-Shop fremde Autos ausblenden", () => TDU2OnlineSettings.HideCarsInStickerShop, v => TDU2OnlineSettings.HideCarsInStickerShop = v, true),
                Head("SYNCHRONISATION"),
                Bool("Verschmutzung", () => TDU2OnlineSettings.SyncDirt, v => TDU2OnlineSettings.SyncDirt = v, true),
                Bool("Lackierung / Sticker", () => TDU2OnlineSettings.SyncLiveries, v => TDU2OnlineSettings.SyncLiveries = v, true),
                Info("Standardwerte: Runtime/Online/TDU2OnlineSettings.cs"),
            };
            return l;
        }

        /// <summary>GPS (Ziel, Ansagen zuordnen/testen) und Lesezeichen der großen Karte.</summary>
        List<Item> GpsMenu()
        {
            var gps = TDU2Gps.Instance;
            var l = new List<Item> { Head("GPS") };
            if (gps == null) { l.Add(Info("GPS noch nicht aktiv (Minimap/HUD nicht geladen)")); return l; }
            l.Add(Info("Ziel", () => gps.HasTarget ? gps.TargetGame.ToString("F0") : "keins (große Karte: Rechtsklick oder Mini-Menü)"));
            l.Add(Info("Restweg", () => gps.HasTarget && gps.Remaining >= 0f ? gps.Remaining.ToString("0") + " m (" + gps.Route.Count + " Punkte)" : "-"));
            l.Add(Action_("Ziel entfernen", () => { gps.ClearTarget(); Toast("GPS-Ziel entfernt"); }));
            l.Add(Float("Ziel erreicht unter", () => TDU2GpsSettings.ArriveDistance, v => TDU2GpsSettings.ArriveDistance = v, 5f, 5f, 200f, 30f, "0", " m"));
            l.Add(Float("Pfadbreite", () => TDU2GpsSettings.PathWidth, v => TDU2GpsSettings.PathWidth = v, 0.002f, 0.004f, 0.08f, 0.022f, "0.000"));
            l.Add(Float("Kranz-Öffnung", () => TDU2GpsSettings.RingSpread, v => TDU2GpsSettings.RingSpread = v, 5f, 5f, 180f, 30f, "0", "°"));
            l.Add(Bool("Meilen", () => TDU2GpsSettings.Miles, v => TDU2GpsSettings.Miles = v, false));
            l.Add(Head("ANSAGEN (Sound/Voices/<Sprache>/GPS.bnk)"));
            l.Add(Bool("Ansagen", () => TDU2GpsSettings.Voice, v => TDU2GpsSettings.Voice = v, true));
            l.Add(Float("Lautstärke", () => TDU2GpsSettings.VoiceVolume, v => TDU2GpsSettings.VoiceVolume = v, 0.05f, 0f, 1f, 0.9f, "0.00"));
            var langs = new[] { "", "ge", "us", "fr", "it", "sp", "ja" };
            var langNames = new[] { "Systemsprache", "Deutsch (ge)", "Englisch (us)", "Französisch (fr)", "Italienisch (it)", "Spanisch (sp)", "Japanisch (ja)" };
            l.Add(Table("Sprache", langNames, () => Math.Max(0, Array.IndexOf(langs, TDU2GpsSettings.VoiceLanguage ?? "")), v => TDU2GpsSettings.VoiceLanguage = langs[v], 0));
            int count = Math.Max(17, gps.VoiceCount);
            l.Add(Int("Ziel gesetzt", () => TDU2GpsSettings.VoiceTargetSet, v => TDU2GpsSettings.VoiceTargetSet = v, 0, count, 0));
            l.Add(Int("Ziel erreicht", () => TDU2GpsSettings.VoiceArrived, v => TDU2GpsSettings.VoiceArrived = v, 0, count, 0));
            l.Add(Int("Bald links", () => TDU2GpsSettings.VoiceTurnLeftSoon, v => TDU2GpsSettings.VoiceTurnLeftSoon = v, 0, count, 0));
            l.Add(Int("Bald rechts", () => TDU2GpsSettings.VoiceTurnRightSoon, v => TDU2GpsSettings.VoiceTurnRightSoon = v, 0, count, 0));
            l.Add(Int("Jetzt links", () => TDU2GpsSettings.VoiceTurnLeftNow, v => TDU2GpsSettings.VoiceTurnLeftNow = v, 0, count, 0));
            l.Add(Int("Jetzt rechts", () => TDU2GpsSettings.VoiceTurnRightNow, v => TDU2GpsSettings.VoiceTurnRightNow = v, 0, count, 0));
            l.Add(Int("Wenden", () => TDU2GpsSettings.VoiceUTurn, v => TDU2GpsSettings.VoiceUTurn = v, 0, count, 0));
            l.Add(Head("ANSAGEN ANHÖREN"));
            for (int i = 1; i <= count; i++)
            {
                int k = i;
                l.Add(Action_("VFGPSVoice" + k.ToString("000") + " abspielen", () => { bool on = TDU2GpsSettings.Voice; TDU2GpsSettings.Voice = true; gps.Voice(k); TDU2GpsSettings.Voice = on; Toast("GPS-Ansage " + k); }));
            }
            l.Add(Head("LESEZEICHEN (große Karte)"));
            l.Add(Info("Gespeichert", () => TDU2Bookmarks.All.Count.ToString()));
            foreach (var b in new List<TDU2Bookmarks.Mark>(TDU2Bookmarks.All))
            {
                var bm = b;
                l.Add(Action_("Lesezeichen " + bm.Id + " (" + bm.Island + " " + bm.Pos.ToString("F0") + ") löschen", () => { TDU2Bookmarks.Remove(bm.Id); Toast("Lesezeichen gelöscht"); Rebuild(); }));
            }
            return l;
        }

        List<Item> AvatarMenu()
        {
            var l = new List<Item>();
            if (!Avatars.AvatarLibrary.Ready) { l.Add(Info("Avatar-Daten noch nicht geladen (Datenbank)")); return l; }
            l.Add(Head("EIGENER AVATAR"));
            l.Add(Table("Modell (Geschlecht)", SexNames, () => Cfg.Sex, v => { var c = Avatars.AvatarConfig.Default(v); c.Ethnic = Cfg.Ethnic; SetCfg(c); Toast("Modell: " + SexNames[v]); }, 0));
            l.Add(Table("Herkunft (Kopf, Hautton)", EthNames, () => Cfg.Ethnic, v => { var c = Cfg; c.Ethnic = v; SetCfg(c); }, 0));
            l.Add(Menu("Kleidung", ClothesMenu));
            l.Add(Action_("Standard-Aussehen", () => { var c = Avatars.AvatarConfig.Default(Cfg.Sex); c.Ethnic = Cfg.Ethnic; SetCfg(c); Toast("Standard-Aussehen"); }));
            l.Add(Info("Kodiert", () => { var s = Avatars.AvatarConfigs.LocalCode ?? "-"; return s.Length > 60 ? s.Substring(0, 60) + "…" : s; }));
            l.Add(Head("ANZEIGE"));
            l.Add(Bool("Fahrer anzeigen", () => Avatars.TDU2CarAvatar.ShowDrivers, v => Avatars.TDU2CarAvatar.ShowDrivers = v, true));
            l.Add(Bool("Beifahrer anzeigen", () => Avatars.TDU2CarAvatar.ShowPassengers, v => Avatars.TDU2CarAvatar.ShowPassengers = v, true));
            l.Add(Bool("Figuren in Interieurs", () => TDU2InteriorNpcs.Show, v => TDU2InteriorNpcs.Show = v, true));
            l.Add(Bool("Kopf im Cockpit ausblenden", () => Avatars.TDU2CarAvatar.All.Count == 0 || Avatars.TDU2CarAvatar.All[0].HideHeadInCockpit,
                v => { foreach (var a in Avatars.TDU2CarAvatar.All) a.HideHeadInCockpit = v; }, true));
            l.Add(Head("SHADER (Feinabstimmung, 0 = wie Original)"));
            l.Add(GlobalFloat("Hautton-Rampe + Verstärkung", "_TDU2AvRampAdd", 0.05f, -1.5f, 2f));
            l.Add(GlobalFloat("Hautton-Rampe Zeile (0 = COEFFS.z)", "_TDU2AvRampRow", 0.05f, 0f, 1f));
            l.Add(GlobalFloat("Umgebung auf Lichtseite (0 = Original)", "_TDU2AvAddMix", 0.05f, 0f, 1f));
            l.Add(GlobalFloat("Zusätzliches Umgebungslicht", "_TDU2AvAmbAdd", 0.02f, -0.5f, 1f));
            l.Add(GlobalFloat("Haarglanz zusätzlich", "_TDU2AvHairSheen", 0.05f, -0.33f, 1f));
            var inter = Mgr != null ? Mgr.GetComponent<TDU2Interiors>() : null;
            if (inter != null)
            {
                l.Add(Float("Innen: Deckenlicht", () => inter.AvatarIndoorLight.grayscale, v => inter.AvatarIndoorLight = new Color(v, v * 0.94f, v * 0.86f), 0.02f, 0f, 2f, 0.47f, "0.00"));
                l.Add(Float("Innen: Umgebung oben", () => inter.AvatarIndoorAmbTop.grayscale, v => inter.AvatarIndoorAmbTop = new Color(v, v * 0.96f, v * 0.9f), 0.02f, 0f, 2f, 0.44f, "0.00"));
                l.Add(Float("Innen: Umgebung unten", () => inter.AvatarIndoorAmbBottom.grayscale, v => inter.AvatarIndoorAmbBottom = new Color(v, v * 0.95f, v * 0.9f), 0.02f, 0f, 2f, 0.19f, "0.00"));
            }
            l.Add(Head("ANIMATION"));
            if (_clipNames == null) _clipNames = Avatars.AvatarLibrary.ClipNames();
            l.Add(new Item
            {
                Label = "Clip (avatarsdriver.bnk)",
                Value = () => string.IsNullOrEmpty(Avatars.TDU2CarAvatar.DebugClip) ? "Sitzpose (normal)" : Avatars.TDU2CarAvatar.DebugClip,
                Change = d =>
                {
                    int i = _clipNames.IndexOf(Avatars.TDU2CarAvatar.DebugClip ?? "");
                    int n = _clipNames.Count + 1;   // -1 = normal
                    i = ((i + 1 + d) % n + n) % n - 1;
                    Avatars.TDU2CarAvatar.DebugClip = i < 0 ? null : _clipNames[i];
                },
                Reset = () => Avatars.TDU2CarAvatar.DebugClip = null,
            });
            l.Add(Float("Abspielgeschwindigkeit", () => Avatars.TDU2CarAvatar.DebugSpeed, v => Avatars.TDU2CarAvatar.DebugSpeed = v, 0.1f, 0f, 4f, 1f, "0.0", "x"));
            l.Add(Bool("IK Hände/Füße (Lenkrad, Pedale)", () => Avatars.TDU2CarAvatar.DebugIK, v => Avatars.TDU2CarAvatar.DebugIK = v, true));
            l.Add(Head("INFO / AKTIONEN"));
            l.Add(Info("Avatare in Autos", () => Avatars.TDU2CarAvatar.All.Count.ToString()));
            l.Add(Info("Clips in avatarsdriver.bnk", () => _clipNames.Count.ToString()));
            l.Add(Action_("Alle Avatare neu aufbauen", () =>
            {
                foreach (var a in Avatars.TDU2CarAvatar.All.ToArray())
                {
                    var code = a.ConfigCode != null ? a.ConfigCode() : null;
                    a.Rebuild(Avatars.AvatarConfig.Decode(code) ?? a.Avatar?.Config ?? Cfg);
                }
                Toast("Avatare neu aufgebaut");
            }));
            return l;
        }

        static List<Avatars.ClotheInfo> PartItems(Avatars.ClothePart part, int sex)
        {
            var res = new List<Avatars.ClotheInfo>();
            foreach (var c in Avatars.AvatarLibrary.AllClothes)
                if (c.Part == part && c.Valid && !c.IsNothing && (sex == 1 ? c.ForWoman : c.ForMan) && Avatars.AvatarLibrary.HasBank(c)) res.Add(c);
            res.Sort((a, b) => string.CompareOrdinal(a.File, b.File));
            return res;
        }

        static Avatars.ClotheInfo Worn(Avatars.AvatarConfig c, Avatars.ClothePart part)
        {
            foreach (int h in c.Items) { var ci = Avatars.AvatarLibrary.Clothe(h); if (ci != null && ci.Part == part) return ci; }
            return null;
        }

        List<Item> ClothesMenu()
        {
            var l = new List<Item> { Head("KLEIDUNG  (←→ Stück · Umschalt+←→ 10 weiter · Entf ablegen)") };
            int sex = Cfg.Sex;
            foreach (var (part, name) in AvatarParts)
            {
                var p = part;
                if (p == Avatars.ClothePart.Dress && sex != 1) continue;
                if (p == Avatars.ClothePart.Beard && sex == 1) continue;
                var list = PartItems(p, sex);
                if (list.Count == 0) continue;
                l.Add(new Item
                {
                    Label = name + "  (" + list.Count + ")",
                    Value = () => { var w = Worn(Cfg, p); return w != null ? w.File : "–"; },
                    Change = d =>
                    {
                        var c = Cfg; var w = Worn(c, p);
                        int i = w != null ? list.IndexOf(w) : -1;
                        int n = list.Count + 1;   // -1 = nichts
                        i = ((i + 1 + d) % n + n) % n - 1;
                        RemovePart(c, p);
                        if (i >= 0) c.Set(list[i], 0);
                        SetCfg(c);
                    },
                    Reset = () => { var c = Cfg; RemovePart(c, p); SetCfg(c); },
                });
                l.Add(new Item
                {
                    Label = "    Farbe",
                    Value = () => { var w = Worn(Cfg, p); return w != null ? (Cfg.ColorOf(w.Hash) + 1) + " / " + Mathf.Max(1, w.NbColors) : "–"; },
                    Change = d =>
                    {
                        var c = Cfg; var w = Worn(c, p);
                        if (w == null) return;
                        int n = Mathf.Max(1, w.NbColors);
                        int col = ((c.ColorOf(w.Hash) + Math.Sign(d)) % n + n) % n;
                        c.Set(w, col); SetCfg(c);
                    },
                    Reset = () => { var c = Cfg; var w = Worn(c, p); if (w != null) { c.Set(w, 0); SetCfg(c); } },
                });
            }
            return l;
        }

        static void RemovePart(Avatars.AvatarConfig c, Avatars.ClothePart p)
        {
            for (int i = c.Items.Count - 1; i >= 0; i--)
                if (Avatars.AvatarLibrary.Clothe(c.Items[i])?.Part == p) { c.Items.RemoveAt(i); if (i < c.Colors.Count) c.Colors.RemoveAt(i); }
        }

        // ------------------------------------------------------------------ Netzwerk
        List<Item> NetworkMenu()
        {
            var gd = TDU2GameData.Instance;
            var mp = Mgr != null ? Mgr.GetComponent<TDU2Multiplayer>() : null;
            var l = new List<Item>
            {
                Head("STATUS"),
                Info("Angemeldet", () => TDU2Session.LoggedIn ? "JA (" + TDU2Session.Nick + ", Id " + TDU2Session.PlayerId + ")" : "NEIN"),
                Info("Server", () => gd != null ? gd.ServerHost + "  TCP " + gd.TcpPort + "  UDP " + gd.UdpPort : "-"),
                Info("Online", () => gd != null && gd.Online ? "JA" : "NEIN"),
                Info("Spieler online", () => gd != null ? gd.PlayerCount.ToString() : "-"),
                Info("Senden möglich (UDP)", () => mp != null && mp.CanSend ? "JA" : "NEIN"),
                Info("Serverzeit", () => DateTimeOffset.FromUnixTimeMilliseconds((long)(TDU2Session.ServerSeconds * 1000.0)).ToLocalTime().ToString("HH:mm:ss")),
                Head("EINSTELLUNGEN"),
                Bool("Mitspieler ausblenden", () => TDU2Multiplayer.Hidden, v => TDU2Multiplayer.Hidden = v, false),
                Menu("Sichtbarkeit / Synchronisation (TDU2OnlineSettings)", OnlineSettingsMenu),
            };
            if (gd != null)
            {
                l.Add(Bool("Namen über Mitspielern", () => gd.ShowNames, v => gd.ShowNames = v, true));
                l.Add(Bool("Kollision mit Mitspielern", () => gd.PlayerCollisions, v => gd.PlayerCollisions = v, true));
                l.Add(Bool("Motorgeräusche der Mitspieler", () => gd.PlayerSounds, v => gd.PlayerSounds = v, true));
                l.Add(Bool("Chat", () => gd.EnableChat, v => gd.EnableChat = v, true));
                l.Add(Float("Senderate", () => gd.SendRate, v => gd.SendRate = v, 1f, 2f, 60f, 20f, "0", " /s"));
                l.Add(Float("Interpolation", () => gd.InterpDelay, v => gd.InterpDelay = v, 0.01f, 0f, 1f, 0.12f, "0.00", " s"));
            }
            var tr = Traffic;
            if (tr != null) l.Add(Bool("Verkehr synchronisieren", () => tr.Sync, v => tr.Sync = v, true));
            l.Add(Head("AKTIONEN"));
            l.Add(Action_("Spielstand jetzt speichern", () =>
            {
                if (!TDU2Session.LoggedIn) { Toast("Nicht angemeldet"); return; }
                TDU2Session.SaveAsync(); Toast("Spielstand wird gespeichert");
            }));
            l.Add(Menu("Mitspieler", PlayersMenu));
            return l;
        }

        List<Item> PlayersMenu()
        {
            var l = new List<Item> { Head("MITSPIELER  (Enter = hinfahren)") };
            var gd = TDU2GameData.Instance;
            if (gd == null || gd.Players.Count == 0) { l.Add(Info("keine Mitspieler online")); return l; }
            var root = Mgr != null && Mgr.Streamer != null ? Mgr.Streamer.MapRoot : null;
            foreach (var p in gd.Players.Values)
            {
                var pi = p;
                string where = !pi.SameIsland ? (pi.Island == 1 ? "Ibiza" : pi.Island == 2 ? "Hawaii" : "andere Insel") : "";
                float dist = -1f;
                if (root != null && pi.HasPos && Mgr.Current != null) dist = Vector3.Distance(root.TransformPoint(pi.GamePos), Mgr.Current.transform.position);
                l.Add(new Item
                {
                    Label = pi.Nick + (string.IsNullOrEmpty(pi.CarName) ? "" : "  (" + pi.CarName + ")"),
                    Value = () => where != "" ? where : dist >= 0f ? (dist / 1000f).ToString("0.0", CultureInfo.GetCultureInfo("de-DE")) + " km" : "-",
                    Activate = () => TeleportToPlayer(pi),
                });
            }
            return l;
        }

        void TeleportToPlayer(PlayerInfo p)
        {
            var m = Mgr; var root = m != null && m.Streamer != null ? m.Streamer.MapRoot : null;
            if (m == null || root == null || m.Current == null) { Toast("Kein eigenes Auto"); return; }
            if (!p.SameIsland) { Toast(p.Nick + " ist auf einer anderen Insel"); return; }
            if (!p.HasPos) { Toast("Position von " + p.Nick + " unbekannt"); return; }
            var fwd = root.TransformDirection(p.GameForward); fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            var pos = root.TransformPoint(p.GamePos) - fwd * 12f;
            pos.y = m.GroundHeight(pos) + 0.6f;
            PlaceCar(pos, Quaternion.LookRotation(fwd, Vector3.up));
            Toast("Zu " + p.Nick + " gefahren");
        }

        void PlaceCar(Vector3 pos, Quaternion rot)
        {
            var v = Mgr.Current;
            var rb = v.GetComponent<Rigidbody>();
            if (rb != null)
            {
#if UNITY_6000_0_OR_NEWER
                rb.linearVelocity = Vector3.zero;
#else
                rb.velocity = Vector3.zero;
#endif
                rb.angularVelocity = Vector3.zero;
                rb.position = pos; rb.rotation = rot;
            }
            v.transform.SetPositionAndRotation(pos, rot);
        }

        // ------------------------------------------------------------------ Autos
        static readonly string[] AidNames = { "Sicher", "Sport", "Hypersport", "Aus" };

        List<Item> CarsMenu()
        {
            var m = Mgr;
            var l = new List<Item> { Head("AKTUELLES AUTO") };
            if (m == null) { l.Add(Info("kein Fahrzeug-Manager in der Szene")); return l; }
            l.Add(Info("Auto", () => m.Current != null && m.Current.Car != null ? ((m.Current.Car.Brand ?? "") + " " + m.Current.Car.ModelName).Trim() + "  [" + m.Current.Car.FileName + ", Id " + m.Current.Car.Id + "]" : "-"));
            l.Add(Info("Geschwindigkeit", () => m.Current != null ? m.Current.SpeedKmh.ToString("0") + " km/h" : "-"));
            l.Add(Table("Fahrhilfen", AidNames, () => (int)m.Aids, i => { m.Aids = (DrivingAids)i; if (m.Current != null) m.Current.Aids = m.Aids; }, (int)DrivingAids.Sport));
            l.Add(Bool("Automatikgetriebe", () => m.AutomaticGearbox, v => { m.AutomaticGearbox = v; if (m.Current != null) m.Current.AutomaticGearbox = v; }, true));
            l.Add(Action_("Auto waschen (Schmutz/Kratzer weg)", () =>
            {
                var d = m.Current != null ? m.Current.GetComponentInChildren<TDU2CarDirt>() : null;
                if (d == null) { Toast("Kein Schmutz-System am Auto"); return; }
                d.Wash(); Toast("Auto gewaschen");
            }));
            l.Add(Action_("Auto aufrichten / zurücksetzen", () =>
            {
                if (m.Current == null) return;
                var t = m.Current.transform; var f = t.forward; f.y = 0f; if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
                var p = t.position; p.y = m.GroundHeight(p) + 0.6f;
                PlaceCar(p, Quaternion.LookRotation(f.normalized, Vector3.up));
                Toast("Auto zurückgesetzt");
            }));
            l.Add(Head("AUTO WECHSELN"));
            l.Add(Menu("Alle Autos (" + m.Available.Count + ")", () => CarList(false)));
            l.Add(Menu("Eigene Autos", () => CarList(true)));
            l.Add(Head(""));
            l.Add(Menu("Verkehr", TrafficMenu));
            return l;
        }

        // ------------------------------------------------------------------ Verkehr
        List<Item> TrafficMenu()
        {
            var tr = Traffic;
            var l = new List<Item> { Head("STATUS") };
            if (tr == null) { l.Add(Info("kein Verkehr (TDU2Traffic) in der Szene")); return l; }
            l.Add(Info("Autos im Verkehr", () => tr.CarCount + " / " + tr.MaxCars + "  (eigene " + tr.OwnedCount + ")"));
            l.Add(Info("Zustand", () => tr.Status));
            l.Add(Head("VERKEHR"));
            l.Add(Bool("Verkehr", () => tr.EnableTraffic, v => tr.EnableTraffic = v, true));
            l.Add(Int("Autos maximal", () => tr.MaxCars, v => tr.MaxCars = v, 0, 40, 12));
            l.Add(Bool("Dichte nach Tageszeit", () => tr.DensityByTime, v => tr.DensityByTime = v, false));
            l.Add(Float("Tempo-Faktor", () => tr.SpeedFactor, v => tr.SpeedFactor = v, 0.02f, 0.5f, 1.3f, 0.92f, "0.00"));
            l.Add(Float("Spawn-Abstand min", () => tr.SpawnMin, v => tr.SpawnMin = Mathf.Min(v, tr.SpawnMax - 20f), 10f, 40f, 1000f, 180f, "0", " m"));
            l.Add(Float("Spawn-Abstand max", () => tr.SpawnMax, v => tr.SpawnMax = Mathf.Max(v, tr.SpawnMin + 20f), 10f, 80f, 1500f, 400f, "0", " m"));
            l.Add(Float("Entfernen ab", () => tr.DespawnDistance, v => tr.DespawnDistance = Mathf.Max(v, tr.SpawnMax + 20f), 10f, 100f, 2000f, 520f, "0", " m"));
            l.Add(Head("PHYSIK & TON"));
            l.Add(Int("Autos mit Physik (max)", () => tr.PhysicsMax, v => tr.PhysicsMax = v, 0, 30, 6));
            l.Add(Float("Physik-Radius", () => tr.PhysicsRadius, v => tr.PhysicsRadius = v, 10f, 20f, 500f, 100f, "0", " m"));
            l.Add(Float("Unfall ab", () => tr.CrashSpeed, v => tr.CrashSpeed = v, 0.5f, 0.5f, 20f, 3.5f, "0.0", " m/s"));
            l.Add(Bool("Verkehrsgeräusche", () => tr.Sounds, v => tr.Sounds = v, true));
            l.Add(Int("Geräusche maximal", () => tr.SoundMax, v => tr.SoundMax = v, 0, 16, 3));
            l.Add(Float("Geräusch-Radius", () => tr.SoundRadius, v => tr.SoundRadius = v, 5f, 10f, 300f, 70f, "0", " m"));
            l.Add(Head("MEHRSPIELER"));
            l.Add(Bool("Verkehr synchronisieren", () => tr.Sync, v => tr.Sync = v, true));
            l.Add(Float("Senderate", () => tr.SendRate, v => tr.SendRate = v, 1f, 1f, 30f, 8f, "0", " /s"));
            l.Add(Float("Relevanz-Abstand", () => tr.RelevantDistance, v => tr.RelevantDistance = v, 50f, 200f, 3000f, 900f, "0", " m"));
            l.Add(Head("DEBUG"));
            l.Add(Bool("Verkehr-Debuganzeige", () => tr.ShowDebug, v => tr.ShowDebug = v, false));
            l.Add(Menu("Autoliste", () =>
            {
                var cl = new List<Item> { Head("VERKEHRSAUTOS  (● eigene · ○ fremde)") };
                foreach (var c in tr.Info())
                    cl.Add(Info($"{(c.mine ? "●" : "○")} {c.id & 0xFFFF:X4} {c.model}", () => $"{c.mode}  {c.kmh:0} km/h  {c.dist:0} m  #{c.owner}"));
                if (cl.Count == 1) cl.Add(Info("keine Autos"));
                return cl;
            }));
            return l;
        }

        // ------------------------------------------------------------------ Welt laden / Streaming
        TDU2MapStreamer Streamer
        {
            get
            {
                var s = Mgr != null ? Mgr.Streamer : null;
                if (s == null && DebugManager.Instance != null) s = DebugManager.Instance.TDU2MapStreamer;
#if UNITY_2023_1_OR_NEWER
                if (s == null) s = FindFirstObjectByType<TDU2MapStreamer>();
#else
                if (s == null) s = FindObjectOfType<TDU2MapStreamer>();
#endif
                return s;
            }
        }

        static string Num(float v, string fmt = "0") => v.ToString(fmt, CultureInfo.GetCultureInfo("de-DE"));

        List<Item> WorldMenu()
        {
            var s = Streamer;
            var l = new List<Item> { Head("STATUS") };
            if (s == null || s.Index == null) { l.Add(Info("keine Insel geladen (TDU2MapStreamer)")); return l; }
            s.RefreshStats();
            l.Add(Info("Insel", () => TDU2IslandTravel.DisplayName(s.Index.IslandName) + "  (" + s.Status + ")"));
            l.Add(Info("Position (Spiel)", () => Num(s.ViewerGamePosition.x) + " / " + Num(s.ViewerGamePosition.y) + " / " + Num(s.ViewerGamePosition.z)));
            l.Add(Info("Sektor", () => WorldLayout.SectorAt(s.ViewerGamePosition.x, s.ViewerGamePosition.z).ToString()));
            l.Add(Info("Areas fertig / geladen", () => s.builtA + " / " + s.areaCount + "  (Bänke " + s.Index.AreaBanks.Count + (s.failedA > 0 ? ", Fehler " + s.failedA : "") + ")"));
            l.Add(Info("Sektoren fertig / geladen", () => s.builtS + " / " + s.sectorCount + "  (Bänke " + s.Index.SectorBanks.Count + (s.failedS > 0 ? ", Fehler " + s.failedS : "") + ")"));
            l.Add(Info("Instanzen (Bäume/Objekte)", () => s.inst.ToString("N0", CultureInfo.GetCultureInfo("de-DE"))));
            l.Add(Info("Objekt-Bibliothek", () => s.lib));
            l.Add(Info("World-Actors", () => s.ActorsState));
            l.Add(Info("Spots", () => s.Spots != null ? s.Spots.Spots.Count.ToString() : "-"));
            l.Add(Head("INHALTE  (gilt für neu geladene Kacheln – siehe 'Welt neu laden')"));
            l.Add(Bool("Inselmodell (Fern-LOD)", () => s.ShowIslandLod, v => s.ShowIslandLod = v, true));
            l.Add(Bool("Area-Objekte", () => s.LoadAreaObjects, v => s.LoadAreaObjects = v, true));
            l.Add(Bool("Sektor-Objekte", () => s.LoadSectorObjects, v => s.LoadSectorObjects = v, true));
            l.Add(Bool("Vegetation", () => s.LoadVegetation, v => s.LoadVegetation = v, true));
            l.Add(Bool("Weitere Platzierungen", () => s.LoadOtherPlacements, v => s.LoadOtherPlacements = v, true));
            l.Add(Bool("Actors", () => s.LoadActors, v => s.LoadActors = v, false));
            l.Add(Bool("World-Actors (Ampeln, Schilder)", () => s.LoadWorldActors, v => s.LoadWorldActors = v, true));
            l.Add(Bool("Gras", () => s.LoadGrass, v => s.LoadGrass = v, true));
            l.Add(Bool("POIs", () => s.LoadPois, v => s.LoadPois = v, true));
            l.Add(Bool("Spot-Gebäude (außen)", () => s.LoadSpotExteriors, v => s.LoadSpotExteriors = v, true));
            l.Add(Bool("Interieurs", () => s.LoadInteriors, v => s.LoadInteriors = v, true));
            l.Add(Bool("Wasser", () => s.ShowWater, v => s.ShowWater = v, true));
            l.Add(Bool("Kollisionen erzeugen", () => s.GenerateColliders, v => s.GenerateColliders = v, true));
            l.Add(Bool("Tunnelportale öffnen", () => s.CutTunnelPortals, v => s.CutTunnelPortals = v, true));
            l.Add(Head("AKTIONEN"));
            l.Add(Action_("Welt neu laden (alle Kacheln)", () =>
            {
                if (Mgr != null && Mgr.Suspended) { Toast("Gerade nicht möglich (Interieur/Menü)"); return; }
                s.ReloadWorld(); Toast("Welt wird neu geladen");
            }));
            l.Add(Action_("Speicher aufräumen (GC + unbenutzte Assets)", CleanMemory));
            l.Add(Head("ANZEIGE"));
            var dm = DebugManager.Instance;
            if (dm != null)
            {
                l.Add(Bool("Sektor-Debug", () => dm.isSectorDebug, v => dm.isSectorDebug = v, false));
                l.Add(Bool("POI-Marker", () => dm.isPOIMarker, v => dm.isPOIMarker = v, false));
            }
            return l;
        }

        List<Item> StreamingMenu()
        {
            var s = Streamer;
            var l = new List<Item> { Head("STATUS") };
            if (s == null || s.Index == null) { l.Add(Info("keine Insel geladen (TDU2MapStreamer)")); return l; }
            s.RefreshStats();
            l.Add(Info("Lade-Jobs", () => s.Jobs + " / " + s.MaxParallelLoads));
            l.Add(Info("Areas / Sektoren", () => s.builtA + "/" + s.areaCount + "  ·  " + s.builtS + "/" + s.sectorCount));
            l.Add(Info("Floating Origin", () => s.UseFloatingOrigin ? "AN" : "AUS"));
            l.Add(Head("LADEN"));
            l.Add(Int("Gleichzeitige Lade-Jobs", () => s.MaxParallelLoads, v => s.MaxParallelLoads = v, 1, 8, 3));
            l.Add(Float("Area laden bis", () => s.AreaLoadDistance, v => s.AreaLoadDistance = v, 100f, 500f, 10000f, 2500f, "0", " m"));
            l.Add(Float("Area entladen (+)", () => s.AreaUnloadMargin, v => s.AreaUnloadMargin = v, 100f, 100f, 5000f, 1000f, "0", " m"));
            l.Add(Float("Sektor laden bis", () => s.SectorLoadDistance, v => s.SectorLoadDistance = v, 50f, 100f, 5000f, 700f, "0", " m"));
            l.Add(Float("Sektor entladen (+)", () => s.SectorUnloadMargin, v => s.SectorUnloadMargin = v, 50f, 50f, 3000f, 300f, "0", " m"));
            l.Add(Head("DETAIL  (neu geladene Kacheln)"));
            l.Add(Float("LOD-Distanz Objekte", () => s.LodDistanceScale, v => s.LodDistanceScale = v, 0.25f, 0.25f, 10f, 3f, "0.00", " x"));
            l.Add(Bool("Letzte LOD-Stufe ausblenden", () => s.CullLastLod, v => s.CullLastLod = v, false));
            l.Add(Float("Vegetation-Sichtweite", () => s.VegetationDistance, v => s.VegetationDistance = v, 50f, 100f, 5000f, 900f, "0", " m"));
            l.Add(Float("Gras-Radius", () => s.GrassRadius, v => { s.GrassRadius = v; foreach (var g in FindAll<GrassRenderer>()) g.Radius = v; }, 10f, 20f, 500f, 120f, "0", " m"));
            l.Add(Head("AKTIONEN"));
            l.Add(Action_("Welt neu laden (alle Kacheln)", () =>
            {
                if (Mgr != null && Mgr.Suspended) { Toast("Gerade nicht möglich (Interieur/Menü)"); return; }
                s.ReloadWorld(); Toast("Welt wird neu geladen");
            }));
            return l;
        }

        static T[] FindAll<T>() where T : UnityEngine.Object
        {
#if UNITY_2023_1_OR_NEWER
            return FindObjectsByType<T>(FindObjectsSortMode.None);
#else
            return FindObjectsOfType<T>();
#endif
        }

        // ------------------------------------------------------------------ Jobs & Threads
        List<Item> JobsMenu()
        {
            var s = Streamer;
            var l = new List<Item> { Head("STREAMING") };
            if (s != null && s.Index != null)
            {
                s.RefreshStats();
                l.Add(Info("Lade-Jobs (Area/Sektor)", () => s.Jobs + " / " + s.MaxParallelLoads));
                l.Add(Int("Gleichzeitige Lade-Jobs", () => s.MaxParallelLoads, v => s.MaxParallelLoads = v, 1, 8, 3));
                l.Add(Info("Objekt-Bibliothek", () => s.lib));
                l.Add(Info("World-Actors", () => s.ActorsState));
            }
            else l.Add(Info("keine Insel geladen"));
            l.Add(Head("UNITY JOB SYSTEM"));
            l.Add(Int("Job-Worker", () => Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount,
                v => Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerCount = v, 1, Math.Max(1, Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerMaximumCount), Math.Max(1, SystemInfo.processorCount - 1)));
            l.Add(Info("Job-Worker maximal", () => Unity.Jobs.LowLevel.Unsafe.JobsUtility.JobWorkerMaximumCount.ToString()));
            l.Add(Head(".NET THREADPOOL  (Task.Run: Bänke, Texturen, Autos, Musik)"));
            l.Add(Info("Worker frei / max", () => { System.Threading.ThreadPool.GetAvailableThreads(out int w, out _); System.Threading.ThreadPool.GetMaxThreads(out int mw, out _); return w + " / " + mw + "  (belegt " + (mw - w) + ")"; }));
            l.Add(Info("E/A frei / max", () => { System.Threading.ThreadPool.GetAvailableThreads(out _, out int io); System.Threading.ThreadPool.GetMaxThreads(out _, out int mio); return io + " / " + mio; }));
            l.Add(Head("SYSTEM"));
            l.Add(Info("CPU", () => SystemInfo.processorType + "  (" + SystemInfo.processorCount + " Threads, " + SystemInfo.processorFrequency + " MHz)"));
            l.Add(Info("Hauptthread", () => Num(_fps, "0") + " FPS  ·  " + Num(_ftAvg, "0.0") + " ms"));
            return l;
        }

        // ------------------------------------------------------------------ Rendering
        static readonly string[] ShadowNames = { "Aus", "Nur harte", "Alle" };
        static readonly string[] ShadowResNames = { "Niedrig", "Mittel", "Hoch", "Sehr hoch" };
        static readonly string[] AnisoNames = { "Aus", "Je Textur", "Erzwungen" };
        static readonly string[] FpsNames = { "Aus", "30", "60", "120", "144", "240" };
        static readonly int[] FpsValues = { -1, 30, 60, 120, 144, 240 };
        static readonly string[] TexNames = { "Voll", "1/2", "1/4", "1/8" };

        List<Item> RenderingMenu()
        {
            var s = Streamer;
            var cam = Camera.main;
            var l = new List<Item> { Head("BILD") };
            l.Add(Info("Auflösung", () => Screen.width + " x " + Screen.height + "  (" + Screen.fullScreenMode + ")"));
            l.Add(Info("Grafik", () => SystemInfo.graphicsDeviceName + "  (" + SystemInfo.graphicsDeviceType + ")"));
            l.Add(Table("Qualitätsstufe", QualitySettings.names, QualitySettings.GetQualityLevel, i => QualitySettings.SetQualityLevel(i, true), QualitySettings.GetQualityLevel()));
            l.Add(Int("VSync", () => QualitySettings.vSyncCount, v => QualitySettings.vSyncCount = v, 0, 2, 1));
            l.Add(Table("FPS-Begrenzung", FpsNames, () => Math.Max(0, Array.IndexOf(FpsValues, Application.targetFrameRate)), i => Application.targetFrameRate = FpsValues[i], 0));
            if (cam != null)
            {
                l.Add(Float("Sichtweite (Kamera)", () => cam.farClipPlane, v => cam.farClipPlane = v, 500f, 1000f, 60000f, 60000f, "0", " m"));
                l.Add(Float("Sichtfeld", () => cam.fieldOfView, v => cam.fieldOfView = v, 1f, 30f, 110f, 60f, "0", "°"));
            }
            l.Add(Head("DETAIL"));
            l.Add(Float("LOD-Bias (sofort)", () => QualitySettings.lodBias, v => QualitySettings.lodBias = v, 0.25f, 0.25f, 8f, 2f, "0.00"));
            if (s != null) l.Add(Float("LOD-Distanz Objekte (neu geladen)", () => s.LodDistanceScale, v => s.LodDistanceScale = v, 0.25f, 0.25f, 10f, 3f, "0.00", " x"));
#if UNITY_2022_2_OR_NEWER
            l.Add(Table("Texturauflösung", TexNames, () => Mathf.Clamp(QualitySettings.globalTextureMipmapLimit, 0, 3), i => QualitySettings.globalTextureMipmapLimit = i, 0));
#else
            l.Add(Table("Texturauflösung", TexNames, () => Mathf.Clamp(QualitySettings.masterTextureLimit, 0, 3), i => QualitySettings.masterTextureLimit = i, 0));
#endif
            l.Add(Table("Anisotrope Filterung", AnisoNames, () => (int)QualitySettings.anisotropicFiltering, i => QualitySettings.anisotropicFiltering = (AnisotropicFiltering)i, (int)AnisotropicFiltering.Enable));
            l.Add(Int("Pixel-Lichter", () => QualitySettings.pixelLightCount, v => QualitySettings.pixelLightCount = v, 0, 32, 4));
            l.Add(Head("SCHATTEN"));
            l.Add(Table("Schatten", ShadowNames, () => (int)QualitySettings.shadows, i => QualitySettings.shadows = (ShadowQuality)i, (int)ShadowQuality.All));
            l.Add(Table("Schattenauflösung", ShadowResNames, () => (int)QualitySettings.shadowResolution, i => QualitySettings.shadowResolution = (ShadowResolution)i, (int)ShadowResolution.High));
            l.Add(Float("Schattendistanz", () => QualitySettings.shadowDistance, v => QualitySettings.shadowDistance = v, 25f, 0f, 3000f, 150f, "0", " m"));
            l.Add(Int("Schatten-Kaskaden", () => QualitySettings.shadowCascades, v => QualitySettings.shadowCascades = v <= 1 ? 1 : v <= 2 ? 2 : 4, 1, 4, 4));
            l.Add(Head("WELT"));
            l.Add(Bool("Gras anzeigen", () => _grassOn, v => { _grassOn = v; foreach (var g in FindAll<GrassRenderer>()) g.enabled = v; }, true));
            l.Add(Bool("Vegetation anzeigen", () => _vegOn, v => { _vegOn = v; foreach (var g in FindAll<VegetationRenderer>()) g.enabled = v; }, true));
            l.Add(Bool("Leuchteffekte (neu geladen)", () => MaterialFactory.ShowGlowMaterials, v => { MaterialFactory.ShowGlowMaterials = v; if (s != null) s.ShowGlowMaterials = v; }, true));
            if (s != null) l.Add(Float("Vegetation-Sichtweite (neu geladen)", () => s.VegetationDistance, v => s.VegetationDistance = v, 50f, 100f, 5000f, 900f, "0", " m"));
            return l;
        }
        bool _grassOn = true, _vegOn = true;

        // ------------------------------------------------------------------ Auslastung
        float _fps, _ftAvg, _ftMin, _ftMax;
        int _sampleFrames; float _sampleTime, _sampleMin = float.MaxValue, _sampleMax;
        float _countAt; int _renderers, _renderersOn, _visible, _gameObjects;

        void SampleFrame()
        {
            float dt = Time.unscaledDeltaTime;
            _sampleFrames++; _sampleTime += dt;
            _sampleMin = Mathf.Min(_sampleMin, dt); _sampleMax = Mathf.Max(_sampleMax, dt);
            if (_sampleTime >= 0.5f)
            {
                _fps = _sampleFrames / _sampleTime;
                _ftAvg = _sampleTime / _sampleFrames * 1000f; _ftMin = _sampleMin * 1000f; _ftMax = _sampleMax * 1000f;
                _sampleFrames = 0; _sampleTime = 0f; _sampleMin = float.MaxValue; _sampleMax = 0f;
            }
        }

#if UNITY_2020_2_OR_NEWER
        Unity.Profiling.ProfilerRecorder _recDraw, _recBatch, _recSetPass, _recTris, _recVerts;
        bool _recStarted;
        void StartRecorders()
        {
            if (_recStarted) return;
            _recStarted = true;
            _recDraw = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Draw Calls Count");
            _recBatch = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Batches Count");
            _recSetPass = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "SetPass Calls Count");
            _recTris = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Triangles Count");
            _recVerts = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, "Vertices Count");
        }
        void StopRecorders()
        {
            if (!_recStarted) return;
            _recStarted = false;
            _recDraw.Dispose(); _recBatch.Dispose(); _recSetPass.Dispose(); _recTris.Dispose(); _recVerts.Dispose();
        }
        static string Rec(Unity.Profiling.ProfilerRecorder r) => r.Valid ? r.LastValue.ToString("N0", CultureInfo.GetCultureInfo("de-DE")) : "– (nur Editor/Development-Build)";
#else
        void StartRecorders() { }
        void StopRecorders() { }
#endif

        static string MB(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("N0", CultureInfo.GetCultureInfo("de-DE")) + " MB";

        void CountObjects()
        {
            if (Time.unscaledTime < _countAt) return;
            _countAt = Time.unscaledTime + 2f;
            var rs = FindAll<Renderer>();
            _renderers = rs.Length; _renderersOn = 0; _visible = 0;
            foreach (var r in rs) { if (r.enabled && r.gameObject.activeInHierarchy) { _renderersOn++; if (r.isVisible) _visible++; } }
            _gameObjects = FindAll<Transform>().Length;
        }

        void CleanMemory()
        {
            GC.Collect();
            Resources.UnloadUnusedAssets();
            Toast("Speicher wird aufgeräumt");
        }

        List<Item> UsageMenu()
        {
            StartRecorders();
            CountObjects();
            var l = new List<Item>
            {
                Head("BILDRATE"),
                Info("FPS", () => Num(_fps, "0.0")),
                Info("Frame-Zeit Ø / min / max", () => Num(_ftAvg, "0.0") + " / " + Num(_ftMin, "0.0") + " / " + Num(_ftMax, "0.0") + " ms"),
#if UNITY_2020_2_OR_NEWER
                Head("RENDERING (letzter Frame)"),
                Info("Draw Calls", () => Rec(_recDraw)),
                Info("Batches", () => Rec(_recBatch)),
                Info("SetPass Calls", () => Rec(_recSetPass)),
                Info("Dreiecke", () => Rec(_recTris)),
                Info("Vertices", () => Rec(_recVerts)),
#endif
                Info("Renderer aktiv / sichtbar / gesamt", () => _renderersOn + " / " + _visible + " / " + _renderers),
                Info("GameObjects", () => _gameObjects.ToString("N0", CultureInfo.GetCultureInfo("de-DE"))),
                Head("SPEICHER"),
                Info("Unity belegt / reserviert", () => MB(UProfiler.GetTotalAllocatedMemoryLong()) + " / " + MB(UProfiler.GetTotalReservedMemoryLong())),
                Info("Mono-Heap belegt / gesamt", () => MB(UProfiler.GetMonoUsedSizeLong()) + " / " + MB(UProfiler.GetMonoHeapSizeLong())),
                Info("Grafiktreiber", () => MB(UProfiler.GetAllocatedMemoryForGraphicsDriver())),
                Info("Texturen", () => MB((long)Texture.currentTextureMemory) + "  (gewünscht " + MB((long)Texture.desiredTextureMemory) + ")"),
                Info("GC-Durchläufe", () => GC.CollectionCount(0) + " / " + GC.CollectionCount(1) + " / " + GC.CollectionCount(2)),
                Head("SYSTEM"),
                Info("Arbeitsspeicher", () => SystemInfo.systemMemorySize.ToString("N0", CultureInfo.GetCultureInfo("de-DE")) + " MB"),
                Info("Grafikspeicher", () => SystemInfo.graphicsMemorySize.ToString("N0", CultureInfo.GetCultureInfo("de-DE")) + " MB  (" + SystemInfo.graphicsDeviceName + ")"),
                Info("CPU", () => SystemInfo.processorType + "  (" + SystemInfo.processorCount + " Threads)"),
                Info("Laufzeit", () => TimeSpan.FromSeconds(Time.realtimeSinceStartup).ToString(@"hh\:mm\:ss")),
                Head("AKTIONEN"),
                Action_("Speicher aufräumen (GC + unbenutzte Assets)", CleanMemory),
            };
            return l;
        }

        List<Item> CarList(bool ownOnly)
        {
            var m = Mgr;
            var l = new List<Item> { Head(ownOnly ? "EIGENE AUTOS  (Enter = einsteigen)" : "ALLE AUTOS  (Enter = einsteigen, Entf = zu eigenen Autos)") };
            if (m == null) return l;
            var cars = new List<CarPhysics>();
            if (ownOnly) { foreach (var f in TDU2Session.OwnedCars) { var c = m.FindCar(f); if (c != null && !cars.Contains(c)) cars.Add(c); } }
            else cars.AddRange(m.Available);
            cars.Sort((a, b) => string.Compare(((a.Brand ?? "") + a.ModelName), ((b.Brand ?? "") + b.ModelName), StringComparison.OrdinalIgnoreCase));
            if (cars.Count == 0) { l.Add(Info(ownOnly ? "keine eigenen Autos (nicht angemeldet?)" : "keine Autos geladen")); return l; }
            foreach (var c in cars)
            {
                var car = c;
                bool own = false; foreach (var f in TDU2Session.OwnedCars) if (string.Equals(f, car.FileName, StringComparison.OrdinalIgnoreCase)) { own = true; break; }
                l.Add(new Item
                {
                    Label = ((car.Brand ?? "") + " " + car.ModelName).Trim(),
                    Value = () => (own ? "eigen  " : "") + car.FileName,
                    Activate = () => SwitchCar(car),
                    Reset = ownOnly ? (Action)null : () =>
                    {
                        if (!TDU2Session.LoggedIn) { Toast("Nicht angemeldet"); return; }
                        TDU2Session.AddOwned(car.FileName); TDU2Session.SaveAsync(); Toast(car.ModelName + " zu den eigenen Autos hinzugefügt");
                    },
                });
            }
            return l;
        }

        void SwitchCar(CarPhysics car)
        {
            var m = Mgr;
            if (m == null || m.Suspended) { Toast("Gerade nicht möglich (Interieur/Menü)"); return; }
            Vector3 pos; Quaternion rot;
            if (m.Current != null)
            {
                var t = m.Current.transform; var f = t.forward; f.y = 0f; if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
                pos = t.position; pos.y = m.GroundHeight(pos) + 0.6f; rot = Quaternion.LookRotation(f.normalized, Vector3.up);
            }
            else if (Camera.main != null)
            {
                var t = Camera.main.transform; var f = t.forward; f.y = 0f; if (f.sqrMagnitude < 1e-4f) f = Vector3.forward;
                pos = t.position + f.normalized * 6f; pos.y = m.GroundHeight(pos) + 0.6f; rot = Quaternion.LookRotation(f.normalized, Vector3.up);
            }
            else return;
            m.SpawnCar(car, pos, rot);
            if (TDU2Session.LoggedIn) TDU2Session.SetCurrentCar(car.FileName);
            Toast("Auto: " + ((car.Brand ?? "") + " " + car.ModelName).Trim());
        }

        // ------------------------------------------------------------------ Wetter
        static readonly string[] KindNames = { "Automatisch", "Sonne", "Wolken", "Regen", "Vor Regen", "Nach Regen", "Gewitter" };

        List<Item> WeatherMenu()
        {
            var w = TDU2Weather.Instance;
            var l = new List<Item>
            {
                Head("AKTUELL"),
                Info("Uhrzeit", () => HourText(TDU2Weather.Hour)),
                Info("Wetter", () => KindNames[(int)TDU2Weather.Kind + 1]),
                Info("Nacht / Regen / Nässe", () => (TDU2Weather.Night * 100f).ToString("0") + " % / " + (TDU2Weather.Rain * 100f).ToString("0") + " % / " + (TDU2Weather.GroundWet * 100f).ToString("0") + " %"),
            };
            if (w == null) { l.Add(Info("Wetter ist nicht aktiv (TDU2MapStreamer.Weather)")); return l; }
            l.Add(Head("STEUERUNG  (lokal – im Mehrspieler sonst synchron zur Serverzeit)"));
            l.Add(new Item
            {
                Label = "Feste Uhrzeit",
                Value = () => w.ForceHour < 0f ? "Automatisch" : HourText(w.ForceHour),
                Change = d => { float h = w.ForceHour < 0f ? Mathf.Floor(TDU2Weather.Hour * 2f) / 2f : w.ForceHour; h += 0.5f * Mathf.Sign(d) * (Mathf.Abs(d) >= 10 ? 6f : 1f); w.ForceHour = (h % 24f + 24f) % 24f; },
                Reset = () => w.ForceHour = -1f,
            });
            l.Add(Table("Feste Wetterart", KindNames, () => w.ForceKind + 1, i => w.ForceKind = i - 1, 0));
            l.Add(Float("Uhr verschieben", () => w.HourOffset, v => w.HourOffset = v, 0.5f, -12f, 12f, 0f, "+0.0;-0.0;0", " h"));
            l.Add(Float("Länge eines Spieltags", () => w.DayMinutes, v => w.DayMinutes = v, 5f, 5f, 1440f, 120f, "0", " min"));
            l.Add(Float("Dauer eines Wetterabschnitts", () => w.WeatherMinutes, v => w.WeatherMinutes = v, 1f, 1f, 120f, 14f, "0", " min"));
            l.Add(Float("Wetterübergang", () => w.TransitionSeconds, v => w.TransitionSeconds = v, 10f, 0f, 600f, 120f, "0", " s"));
            l.Add(Head("DARSTELLUNG"));
            l.Add(Bool("Nebel", () => w.ApplyFog, v => w.ApplyFog = v, true));
            l.Add(Float("Nebelstärke", () => w.FogStrength, v => w.FogStrength = v, 0.05f, 0f, 1f, 0.8f, "0.00"));
            l.Add(Float("Sonnenlicht", () => w.DiffuseScale, v => w.DiffuseScale = v, 0.05f, 0f, 2f, 0.65f, "0.00"));
            l.Add(Float("Umgebungslicht", () => w.AmbientScale, v => w.AmbientScale = v, 0.05f, 0f, 2f, 0.75f, "0.00"));
            l.Add(Bool("Scheinwerfer automatisch", () => w.AutoHeadlights, v => w.AutoHeadlights = v, true));
            l.Add(Int("Straßenlampen mit Licht", () => w.LampLights, v => w.LampLights = v, 0, 64, 16));
            l.Add(Float("Lampenhelligkeit", () => w.LampIntensity, v => w.LampIntensity = v, 0.1f, 0f, 2f, 1f, "0.0"));
            l.Add(Float("Straßenlicht im Gelände", () => w.StreetLightStrength, v => w.StreetLightStrength = v, 0.05f, 0f, 1f, 0.5f, "0.00"));
            l.Add(Head("SCHNELLWAHL"));
            l.Add(Action_("Sonnenaufgang (6:30)", () => { w.ForceHour = 6.5f; Toast("Uhrzeit 6:30"); }));
            l.Add(Action_("Mittag (12:00)", () => { w.ForceHour = 12f; Toast("Uhrzeit 12:00"); }));
            l.Add(Action_("Abenddämmerung (19:30)", () => { w.ForceHour = 19.5f; Toast("Uhrzeit 19:30"); }));
            l.Add(Action_("Nacht (23:00)", () => { w.ForceHour = 23f; Toast("Uhrzeit 23:00"); }));
            l.Add(Action_("Alles automatisch (synchron)", () => { w.ForceHour = -1f; w.ForceKind = -1; w.HourOffset = 0f; Toast("Wetter/Uhrzeit wieder synchron"); }));
            return l;
        }

        static string HourText(float h)
        {
            h = (h % 24f + 24f) % 24f;
            int hh = Mathf.FloorToInt(h), mm = Mathf.FloorToInt((h - hh) * 60f);
            return hh.ToString("00") + ":" + mm.ToString("00");
        }

        // ------------------------------------------------------------------ Zeichnen (Stil des edDebug-Textmenüs)
        static readonly Color CYellow = new Color(1f, 0.9f, 0.2f), CCyan = new Color(0.3f, 0.95f, 1f), CGreen = new Color(0.45f, 1f, 0.45f),
                              CPurple = new Color(0.85f, 0.55f, 1f), CBlue = new Color(0.55f, 0.7f, 1f), CWhite = new Color(0.92f, 0.92f, 0.92f);

        void OnGUI()
        {
            if (!_open && (_toast == null || Time.unscaledTime > _toastUntil)) return;
            GUI.depth = -1000;
            float k = Screen.height / 1080f;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.label) { richText = false, wordWrap = false, clipping = TextClipping.Clip };
                _style.font = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Lucida Console", "Courier New" }, 16);
                _bg = new Texture2D(1, 1); _bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.78f)); _bg.Apply();
                _boxStyle = new GUIStyle(); _boxStyle.normal.background = _bg;
            }
            _style.fontSize = Mathf.Max(10, Mathf.RoundToInt(FontSize * k));
            float lh = _style.fontSize * 1.35f;

            if (!_open)
            {
                var tr = new Rect(20f * k, Screen.height - 60f * k, 900f * k, lh + 8f);
                GUI.Box(tr, GUIContent.none, _boxStyle);
                _style.normal.textColor = CYellow;
                GUI.Label(new Rect(tr.x + 8f, tr.y + 4f, tr.width - 16f, lh), _toast, _style);
                return;
            }

            var p = _stack[_stack.Count - 1];
            int rows = Mathf.Max(5, Mathf.FloorToInt((Screen.height * 0.8f - lh * 3f) / lh));
            if (p.Sel < p.Top) p.Top = p.Sel;
            if (p.Sel >= p.Top + rows) p.Top = p.Sel - rows + 1;
            p.Top = Mathf.Clamp(p.Top, 0, Math.Max(0, _items.Count - rows));
            int shown = Math.Min(rows, _items.Count - p.Top);
            float w = Mathf.Min(Screen.width - 40f * k, 1000f * k);
            var r = new Rect(20f * k, 20f * k, w, lh * (shown + 3) + 12f);
            GUI.Box(r, GUIContent.none, _boxStyle);
            float x = r.x + 10f, y = r.y + 6f, valX = r.x + w * 0.55f;

            // Titelzeile: Pfad der Menüs
            var path = new System.Text.StringBuilder("TDU2 · ");
            for (int i = 0; i < _stack.Count; i++) { if (i > 0) path.Append(" > "); path.Append(_stack[i].Title); }
            _style.normal.textColor = CPurple;
            GUI.Label(new Rect(x, y, w - 20f, lh), path.ToString().ToUpperInvariant(), _style);
            y += lh * 1.4f;

            for (int i = p.Top; i < p.Top + shown; i++)
            {
                var it = _items[i];
                bool sel = i == p.Sel;
                if (it.Header)
                {
                    _style.normal.textColor = it.Value != null ? CWhite : CBlue;
                    GUI.Label(new Rect(x, y, valX - x, lh), (it.Value != null ? "   " : "") + it.Label, _style);
                    if (it.Value != null) { _style.normal.textColor = CGreen; GUI.Label(new Rect(valX, y, r.xMax - valX - 10f, lh), Safe(it.Value), _style); }
                }
                else
                {
                    _style.normal.textColor = sel ? CCyan : CWhite;
                    GUI.Label(new Rect(x, y, valX - x, lh), (sel ? "-> " : "   ") + it.Label, _style);
                    if (it.Value != null) { _style.normal.textColor = sel ? CYellow : CGreen; GUI.Label(new Rect(valX, y, r.xMax - valX - 10f, lh), Safe(it.Value), _style); }
                }
                y += lh;
            }
            // Fußzeile
            _style.normal.textColor = CBlue;
            string more = _items.Count > rows ? "  (" + (p.Sel + 1) + "/" + _items.Count + ")" : "";
            //GUI.Label(new Rect(x, r.yMax - lh - 4f, w - 20f, lh), "F1 Schließen  ↑↓ Eintrag  ←→ Wert  Enter Öffnen  Rücktaste Zurück  Entf Standard" + more, _style);

            if (_toast != null && Time.unscaledTime <= _toastUntil)
            {
                var tr = new Rect(r.x, r.yMax + 6f, w, lh + 8f);
                GUI.Box(tr, GUIContent.none, _boxStyle);
                _style.normal.textColor = CYellow;
                GUI.Label(new Rect(tr.x + 8f, tr.y + 4f, tr.width - 16f, lh), _toast, _style);
            }
        }

        static string Safe(Func<string> f)
        {
            try { return f() ?? ""; } catch (Exception) { return "?"; }
        }
    }
}
