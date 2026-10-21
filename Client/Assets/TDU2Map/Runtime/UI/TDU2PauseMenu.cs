using Assets.TDU2Map.Runtime.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TDU2.Map.Flash;
using TDU2.Map.Online;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Original-Pausemenü (ESC) aus frontend/&lt;res&gt;/main.bnk (MAIN.vmf), gesteuert wie im Spiel:
    ///   - Öffnen: _root-Zustand setzen (Spielmodus, Sperren je Menüpunkt, Texte aus DBLoc) und Wurzel-Label "pause_in" abspielen.
    ///     Die Menülogik (Quadrat-Menü, Optionen, Dialoge) steckt im Film selbst (ActionScript, VmfScript-VM).
    ///   - Eingabe: wie die Spiel-Engine ruft der Player Move("up"/"down"/"left"/"right"), A() (Bestätigen) und B() (Zurück)
    ///     am obersten sichtbaren Clip auf, der diese Funktionen definiert.
    ///   - FSCommands des Films: "paou" (Pause beendet), "exit:vali"/"quit" (Spiel verlassen), "opin"/"opou" (Optionen).
    /// Während das Menü offen ist, steht die Spielzeit (Time.timeScale = 0); der Film läuft mit Echtzeit.
    /// </summary>
    public sealed class TDU2PauseMenu : MonoBehaviour
    {
        public TDU2VehicleManager Manager;
        [Tooltip("Taste zum Öffnen/Zurück (wie im Spiel ESC)")]
        public KeyCode MenuKey = KeyCode.Escape;
        [Tooltip("Ausweichtaste im Interieur (dort steuert ESC den Mauszeiger)")]
        public KeyCode MenuKeyInterior = KeyCode.P;
        VmfPlayer _ui;
        string _bank, _bnkDir;
        Task<Dictionary<string, string>> _textTask;
        Dictionary<string, string> _texts;
        TDU2Options _options;
        /// <summary>Einstellungen (Grafik/Audio/Spiel/Sprache/Steuerung) – auch von eigenen UIs nutzbar.</summary>
        public TDU2Options Options => _options;
        /// <summary>Der geladene Film (main.bnk) – auch für die Profilmenüs des Logins (TDU2Login).</summary>
        public VmfPlayer Ui => _ui;
        /// <summary>Texte (DBLoc) der eingestellten Sprache; null bis geladen.</summary>
        public Dictionary<string, string> Texts => _texts;
        bool _open, _closing;
        float _savedTimeScale = 1f;
        CursorLockMode _savedLock; bool _savedCursor;
        public bool IsOpen => _open;
        /// <summary>Rennen läuft (Meisterschaft): Menü im Rennmodus (WhatGameMode 2) mit "Neu starten" und "Beenden" = Rennen abbrechen.</summary>
        public static Func<bool> RaceActive;
        public static Action RaceQuit, RaceRestart;
        bool _raceMode;
        public string LastCommand { get; private set; }

        // Menüpunkte, die hier funktionieren (alles andere ist im Film gesperrt dargestellt)
        static readonly string[] Unlocked = { "LockResume", "LockOptions", "LockExitGame", "LockQuit", "LockControls", "LockVideo", "LockDisplay",
            "LockAudio", "LockGameSettings", "LockDevice", "LockMouse", "LockWheel", "LockLanguage", "LockSave", "LockPlayers", "LockJoin", "LockGameProgress", "LockInvite" };
        static readonly string[] Locked = { "LockSnapshot", "LockChangePosition", "LockInviteParty", "LockManageParty",
            "LockRestart", "LockEditor", "LockBookmarks", "LockProfiles", "LockNewGame", "LockContinueGame", "LockAchiev", "LockRankings",
            "LockOnline", "LockSocial", "LockStatus" };

        void Update()
        {
            if (Manager == null) Manager = GetComponent<TDU2VehicleManager>();
            EnsureLoaded();
#if ENABLE_LEGACY_INPUT_MANAGER
            if (!_open && Input.GetKeyDown(MenuKey) && (TDU2Login.Active || Online.TDU2GameData.ChatTyping))
                Debug.Log("[TDU2] Pausemenü gesperrt: " + (TDU2Login.Active ? "Login/Profilmenü aktiv" : "Chat-Eingabe aktiv"));
#endif
            if (TDU2Login.Active) return;   // Login/Profilmenü benutzt den Film gerade
            if (Online.TDU2GameData.ChatTyping) return;   // Chat hat die Tastatur (Esc schließt den Chat)
#if ENABLE_LEGACY_INPUT_MANAGER
            var ti = Manager != null ? Manager.GetComponent<TDU2Interiors>() : null;
            bool inside = ti != null && ti.Inside;
            // ESC öffnet das Pausemenü überall (Freie Fahrt, Rennen, Interieur); im Interieur zusätzlich P.
            // Gehört ESC gerade dem Interieur (eigenes Menü, Sticker-Shop), nicht öffnen.
            bool menuKey = Input.GetKeyDown(MenuKey) || (inside && Input.GetKeyDown(MenuKeyInterior));
            if (!_open && inside && ti.EscOwned) menuKey = false;
            if (!_open && TDU2AccessoryMenu.OwnsEscape) menuKey = false;   // ESC schließt das Zubehörmenü (Verdeck/Fenster/Blinker)
            if (TDU2BigMap.IsOpen) { if (menuKey && !_open) Debug.Log("[TDU2] Pausemenü gesperrt: große Karte offen"); return; }   // große Karte offen: ESC gehört ihr
            if (!_open && Racing.RacingSchoolMenu.BlocksPauseMenu) { if (menuKey) Debug.Log("[TDU2] Pausemenü gesperrt: " + Racing.RacingSchoolMenu.BlockReason()); return; }   // Rennschul-Menü/Ergebnis offen: ESC gehört dem Film
            if (_dumpAt > 0f && Time.unscaledTime >= _dumpAt) { _dumpAt = -1f; Dump("nach dem Öffnen"); }
            if (Input.GetKeyDown(KeyCode.F9)) Dump("F9");
            if (!_open)
            {
                if (menuKey)
                {
                    if (_ui != null && _texts != null) Open();
                    else Dump($"ESC ohne Öffnen: ui={(_ui != null)} texts={(_texts != null)} bank={_bank ?? "-"} bnkDir={_bnkDir ?? "-"}");
                }
                return;
            }
            if (_ui == null) return;
            UpdateOwnPhoto();
            UpdateMapMarker();
            UpdatePlayerLists();
            UpdateClock();
            if (Time.unscaledTime < _traceUntil) Trace();
            if (UpdateBinder()) return;   // "Taste zuweisen" wartet auf eine Taste
            if (_pendingDialog) AnswerDialog();
            // Mausrad in Listen (Steuerung)
            var al = _ui.ActiveList;
            if (al != null && al.Active && _ui.IsShown(al.Owner))
            {
                float wheel = Input.mouseScrollDelta.y;
                if (wheel > 0.1f) _ui.ListMove(al, -1); else if (wheel < -0.1f) _ui.ListMove(al, 1);
            }
            // Eingabe wie die Spiel-Engine an den Film weiterreichen
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) _ui.Input("Move", "up");
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) _ui.Input("Move", "down");
            if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) _ui.Input("Move", "left");
            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) _ui.Input("Move", "right");
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space)) _ui.Input("A");
            // Zurück; reagiert der Film nicht (keine B-Funktion erreichbar), Menü direkt schließen – nie im Pausenzustand hängen bleiben
            if (menuKey || Input.GetKeyDown(KeyCode.Backspace))
            {
                if (!_ui.Input("B")) { _log.Add("B nicht verarbeitet -> direkt schließen"); Close(); }
                else if (menuKey) _escAt = Time.unscaledTime;
            }
            // ESC im Hauptmenü muss nach der Schließ-Animation "paou" liefern; blockiert der Film (z.B. _root.Input = false),
            // trotzdem schließen – im Optionsmenü führt ESC nur zurück und bleibt offen
            if (_escAt > 0f && Time.unscaledTime - _escAt > 2f)
            {
                _escAt = -1f;
                var inOpt = _ui.GetVariable("/:IsInOptions");
                if (!string.Equals(inOpt, "true", StringComparison.OrdinalIgnoreCase) && inOpt != "1") { _log.Add("ESC ohne paou -> schließen"); Close(); }
            }
            if (Input.GetKeyDown(KeyCode.F10)) Close();   // Notausgang
            if (Input.GetKeyDown(KeyCode.X)) _ui.Input("X");
            if (Input.GetKeyDown(KeyCode.Y)) _ui.Input("Y");
#endif
        }

        void EnsureLoaded()
        {
            string bnkDir = Manager != null ? Manager.BnkDir : null;
            if (string.IsNullOrEmpty(bnkDir)) return;
            if (_bnkDir != bnkDir)
            {
                _bnkDir = bnkDir;
                string lang = SavedLanguage();
                _textTask = System.Threading.Tasks.Task.Run(() => FlashLocalization.Load(bnkDir, lang));
                _bank = null;
                foreach (var res in new[] { "hires", "medres", "lowres" })
                {
                    _bank = CarDatabase.FindFile(Path.Combine(bnkDir, "frontend", res), "main.bnk");
                    if (_bank != null) break;
                }
                if (_bank == null) { Debug.LogWarning("frontend/<res>/main.bnk not found"); return; }
                try
                {
                    if (_ui == null) { _ui = gameObject.AddComponent<VmfPlayer>(); _ui.GuiDepth = -10; _ui.MouseEnabled = true; }
                    _ui.Load(_bank);
                    _ui.Visible = false;
                    _ui.FSCommand += OnCommand;
                    _ui.ListCreated += OnListCreated;
                    _ui.ListValidated += OnListValidated;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("main.bnk is not useable: " + ex.Message);
                    if (_ui != null) { Destroy(_ui); _ui = null; }
                }
            }
            if (_texts == null && _textTask != null && _textTask.IsCompleted)
            {
                _texts = _textTask.Status == TaskStatus.RanToCompletion ? _textTask.Result : new Dictionary<string, string>();
                FlashLocalization.Apply(_ui, _texts);
                if (_options == null)
                {
                    _options = new TDU2Options((k, d) => _texts != null && _texts.TryGetValue(k, out var v) ? v : d, Manager);
                    _options.LanguageChanged = ChangeLanguage;
                    _options.ApplyAll();
                }
                if (_ui != null) _options.Publish(_ui);
            }
        }

        public static string SavedLanguage()
        {
            string sys = FlashLocalization.LangCode(Application.systemLanguage);
            int i = PlayerPrefs.GetInt("TDU2.Opt.WhatLanguage", Array.IndexOf(TDU2Options.LangCodes, sys));
            return i >= 0 && i < TDU2Options.LangCodes.Length ? TDU2Options.LangCodes[i] : sys;
        }

        void ChangeLanguage(string code)
        {
            string dir = _bnkDir;
            if (string.IsNullOrEmpty(dir) || _ui == null) return;
            try
            {
                _texts = FlashLocalization.Load(dir, code);
                FlashLocalization.Apply(_ui, _texts);
                _options?.Publish(_ui);
            }
            catch (Exception ex) { Debug.LogWarning("Lang: " + code + ": " + ex.Message); }
        }

        int _clockShown = -1, _clockDay = -1;

        /// <summary>
        /// Uhr im Pausemenü (/pause_menu/clock/time): Spielzeit aus TDU2Weather (für alle Spieler gleich).
        /// Der Film liest _root.HDecade/HUnit/MDecade/MUnit in setHour(); Sonne/Mond über die Clips day/night ("on"/"off").
        /// </summary>
        void UpdateClock()
        {
            if (!_open || TDU2.Map.World.TDU2Weather.Instance == null) return;
            float h = TDU2.Map.World.TDU2Weather.Hour;
            int hh = Mathf.FloorToInt(h) % 24, mm = Mathf.FloorToInt((h - Mathf.Floor(h)) * 60f) % 60;
            int code = hh * 100 + mm;
            if (code != _clockShown)
            {
                _clockShown = code;
                _ui.SetValue("/:HDecade", hh / 10); _ui.SetValue("/:HUnit", hh % 10);
                _ui.SetValue("/:MDecade", mm / 10); _ui.SetValue("/:MUnit", mm % 10);
                _ui.SetValue("/:SDecade", 0); _ui.SetValue("/:SUnit", 0);
                _ui.CallClip("/pause_menu/clock/time/disp", "setHour");
            }
            int day = TDU2.Map.World.TDU2Weather.Night < 0.5f ? 1 : 0;
            if (day != _clockDay)
            {
                bool first = _clockDay < 0;
                _clockDay = day;
                _ui.GotoLabel("/pause_menu/clock/time/day", day == 1 ? "on" : "off", !first);
                _ui.GotoLabel("/pause_menu/clock/time/night", day == 1 ? "off" : "on", !first);
            }
        }

        public void Open()
        {
            _clockShown = -1; _clockDay = -1;
            if (_ui == null || _open || TDU2Login.Active) return;
            _open = true; _closing = false;
            // Zustand wie das Spiel ihn vor "pain" setzt
            _raceMode = RaceActive != null && RaceActive();
            _ui.SetValue("/:WhatGameMode", _raceMode ? 2 : 1);   // 1 Freie Fahrt, 2 Rennen (Fortsetzen/Neu starten/…/Beenden)
            _ui.SetValue("/:WhatPlatform", 1);       // PC (0 = Xbox 360, 2/3 = PS3 – dort virtuelle Tastatur)
            _ui.SetVariable("/:MenuType", "pc");
            _ui.SetValue("/:IsPC_X360", true);
            _ui.SetValue("/:IsCasino", 0);
            _ui.SetValue("/:isCasinoStandalone", 0);
            _ui.SetValue("/:IsBootCasino", false);
            _ui.SetValue("/:IsInOptions", false);
            _ui.SetValue("/:ListPlayerCardOpened", false);
            _ui.SetValue("/:Input", true);
            _ui.ForceVisible("/pause_menu/data_extd/objective", false);
            foreach (var n in Unlocked) _ui.SetValue("/:" + n, false);
            foreach (var n in Locked) _ui.SetValue("/:" + n, true);
            if (_raceMode) { _ui.SetValue("/:LockRestart", false); _ui.SetValue("/:LockQuit", false); }
            _ui.SetVariable("/:NbNewsUnread", "0");
            _ui.SetVariable("/:Money", Online.TDU2Session.LoggedIn
                ? Online.TDU2Session.Save.money.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " $"
                : "999 999 999 $");
            if (Online.TDU2Session.LoggedIn)
            {
                _ui.SetVariable("/:CurrentProfile", Online.TDU2Session.Nick);
                _ui.SetVariable("/:NbCars", Online.TDU2Session.OwnedCars.Count.ToString());
            }
            if (!Online.TDU2Session.LoggedIn)
                _ui.SetVariable("/:NbCars", (Manager?.Database?.Cars?.Count ?? 0) > 0 ? "1" : "0");
            _ui.SetVariable("/:NbHouses", "0");
            var veh = Manager != null ? Manager.Current : null;
            if (veh != null && veh.Car != null) _ui.SetVariable("/:VehicleName", ((veh.Car.Brand ?? "") + " " + veh.Car.ModelName).Trim());

            // Insel/Gebiet für die Karte (initMap: erster Buchstabe h = Hawaii, i = Ibiza; Gebiete h1..h4, i1..i2)
            string area = CurrentArea() ?? (IsHawaii() ? "h1" : "i1");
            _ui.SetVariable("/:CurrentArea", area);
            _ui.SetVariable("/:AvatarName", TDU2GameData.Instance.PlayerName);
            _ui.SetVariable("/:IsOnline", TDU2GameData.Instance.Online ? "1" : "0");
            _ui.SetVariable("/:IsLoggedIn", Online.TDU2Session.LoggedIn ? "1" : "0");
            // Ausweis (pause_menu/data_extd/idcard) wie im Film (setInfos beim Öffnen):
            //   Flagge:        idcard.flag.gotoAndStop(_root.Nationality)  -> Bild = Ländernummer (24 = Deutschland), als ZAHL setzen
            //   Online-Symbol: idcard/ic_status spielt selbst loop_on/loop_off nach _root.state_online (1 = online)
            //   Name:          _root.AvatarName; Titel: titles.gotoAndStop(_root.NumTitle)
            var gd = TDU2GameData.Instance;
            _ui.SetNumber("/:Nationality", gd != null ? gd.NationalityId : TDU2CountryHelper.GetSystemCountryId());
            _ui.SetNumber("/:state_online", gd != null && gd.Online ? 1 : 0);
            _ui.SetNumber("/:NumTitle", 1);
            _log.Add($"Open: Gebiet {area}, Spielposition {PlayerGamePos()}");

            _ui.Visible = true;
            bool ok = _ui.GotoLabel("/", "pause_in", true);
            _log.Add($"Open: GotoLabel pause_in = {ok}");
            if (DiagnosticLog)
            {
                _dumpAt = Time.unscaledTime + 6f;
                _traceUntil = Time.unscaledTime + 6f; _lastTrace = null;
            }
            _savedTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
            Time.timeScale = 0f;
            AudioListener.pause = true;
            _savedLock = Cursor.lockState; _savedCursor = Cursor.visible;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }

        // ------------------------------------------------------------------ Inselkarte (wie TestDrive2Dev.exe)
        // Marke: /pause_menu/data_extd/map/island/location/circle (nicht location selbst!) bekommt
        //   x = Breite(lure) * SpielX / (AreasX * 6144),  y = Höhe(lure) * SpielZ / (AreasZ * 6144)
        //   Hawaii 11 x 9 Areas, Ibiza 7 x 6 (Ursprung = linke obere Ecke von lure, kein Versatz).
        // Gebiet: islands/<insel>/level/cruisingzones.bin = ein Polygon (Spiel-X/Z) je Gebiet, Punkt-in-Polygon.
        List<(string label, Vector2[] poly)> _zones;
        string _zonesFor;

        bool IsHawaii()
        {
            string n = Manager?.Streamer?.Index?.IslandName ?? "";
            return n.StartsWith("h", StringComparison.OrdinalIgnoreCase);
        }

        Vector3? PlayerGamePos()
        {
            var veh = Manager != null ? Manager.Current : null;
            Transform t = veh != null ? veh.transform : (Camera.main != null ? Camera.main.transform : null);
            if (t == null) return null;
            var root = Manager?.Streamer?.MapRoot;
            return root != null ? root.InverseTransformPoint(t.position) : t.position;
        }

        void EnsureZones()
        {
            var idx = Manager?.Streamer?.Index;
            if (idx == null || _zonesFor == idx.LevelDir) return;
            _zonesFor = idx.LevelDir;
            _zones = new List<(string, Vector2[])>();
            try
            {
                string f = string.IsNullOrEmpty(idx.LevelDir) ? null : Path.Combine(idx.LevelDir, "cruisingzones.bin");
                if (f == null || !File.Exists(f)) return;
                var b = File.ReadAllBytes(f);
                var polys = new List<List<Vector2>>();
                var cur = new List<Vector2>();
                // ab Offset 36: Floats, Gruppen getrennt durch 0xEFBEADDE; Polygone = Gruppen mit >= 3 Punkten
                var vals = new List<float>();
                void Flush()
                {
                    for (int i = 0; i + 1 < vals.Count; i += 2) cur.Add(new Vector2(vals[i], vals[i + 1]));
                    vals.Clear();
                    if (cur.Count >= 3) polys.Add(cur);
                    cur = new List<Vector2>();
                }
                for (int o = 36; o + 4 <= b.Length; o += 4)
                {
                    uint u = BitConverter.ToUInt32(b, o);
                    if (u == 0xEFBEADDEu) { if (vals.Count > 0) Flush(); continue; }
                    vals.Add(BitConverter.ToSingle(b, o));
                }
                if (vals.Count > 0) Flush();
                // Reihenfolge der Polygone -> Gebiets-Label (nach Lage der Plaketten h1..h4 / i1..i2 im Film bestimmt)
                string[] labels = IsHawaii() ? new[] { "h4", "h1", "h3", "h2" } : new[] { "i1", "i2" };
                for (int i = 0; i < polys.Count && i < labels.Length; i++) _zones.Add((labels[i], polys[i].ToArray()));
                _log.Add($"Gebiete: {_zones.Count} aus {f}");
            }
            catch (Exception ex) { Debug.LogWarning("cruisingzones.bin: " + ex.Message); }
        }

        static bool InPolygon(Vector2[] poly, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var a = poly[i]; var c = poly[j];
                if ((a.y > p.y) != (c.y > p.y) && p.x < (c.x - a.x) * (p.y - a.y) / (c.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        /// <summary>Gebiet (Flash-Label h1..h4 / i1..i2) an der Spielerposition; null außerhalb aller Gebiete.</summary>
        public string CurrentArea()
        {
            EnsureZones();
            var gp = PlayerGamePos();
            if (gp == null || _zones == null) return null;
            var p = new Vector2(gp.Value.x, gp.Value.z);
            foreach (var z in _zones) if (InPolygon(z.poly, p)) return z.label;
            // knapp außerhalb (Küste/Meer): nächstes Gebiet
            string best = null; float bd = float.MaxValue;
            foreach (var z in _zones)
            {
                var c = Vector2.zero; foreach (var q in z.poly) c += q; c /= z.poly.Length;
                float d = (c - p).sqrMagnitude; if (d < bd) { bd = d; best = z.label; }
            }
            return best;
        }

        void UpdateMapMarker()
        {
            var gp = PlayerGamePos();
            if (gp == null) return;
            const string loc = "/pause_menu/data_extd/map/island/location";
            var lure = _ui.Find(loc + "/lure");
            if (lure == null) return;
            var r = _ui.LocalBounds(lure);   // im Koordinatensystem von location (wie GetBounds im Spiel)

            if (!r.HasValue) return;
            bool hawaii = IsHawaii();
            float ax = (hawaii ? 11 : 7) * 6144f, az = (hawaii ? 9 : 6) * 6144f;
            _ui.SetPosition(loc + "/circle", new Vector2(r.Value.width * gp.Value.x / ax, r.Value.height * gp.Value.z / az));
        }

        public void Close()
        {
            _escAt = -1f;
            if (!_open) return;
            if (DiagnosticLog) Dump("beim Schließen");   // Protokoll der ganzen Sitzung in Logs/tdu2_pausemenu.txt
            _open = false;
            if (_ui != null) { _ui.Visible = false; _ui.GotoLabel("/", "default", false); }
            Time.timeScale = _savedTimeScale;
            AudioListener.pause = false;
            Cursor.lockState = _savedLock; Cursor.visible = _savedCursor;
        }

        readonly List<string> _log = new List<string>();
        float _dumpAt = -1f, _traceUntil = -1f, _escAt = -1f;
        [Tooltip("Diagnose: Ablauf-Trace nach dem Öffnen und Protokoll beim Schließen nach Logs/tdu2_pausemenu.txt (F9 schreibt es immer). Kostet Leistung.")]
        public bool DiagnosticLog = false;
        string _lastTrace;

        /// <summary>Zeitverlauf der Menüzustände (nur Änderungen) für die Diagnose.</summary>
        void Trace()
        {
            string Fr(string path)
            {
                var d = _ui.Find(path);
                if (d?.Sprite == null) return path + "=-";
                return $"{path.Substring(path.LastIndexOf('/') + 1)}={d.Sprite.Frame}{(d.Sprite.Playing ? "p" : "")}";
            }
            string line = $"root={_ui.Root?.Frame}{(_ui.Root != null && _ui.Root.Playing ? "p" : "")} {Fr("/pause_menu")} {Fr("/pause_menu/menu_sq")} {Fr("/pause_menu/menu_sq/looper")} " +
                          $"numSq={_ui.GetVariable("/pause_menu/menu_sq:numSq")} loop={_ui.GetVariable("/pause_menu/menu_sq/looper:loop")} " +
                          $"{Fr("/pause_menu/menu_sq/sq0")} {Fr("/pause_menu/menu_sq/sq1")} {Fr("/pause_menu/menu_sq/sq2")} {Fr("/pause_menu/menu_sq/sq5")}";
            if (line != _lastTrace && _log.Count < 1500) { _log.Add($"{Time.unscaledTime:0.00} {line}"); _lastTrace = line; }
        }

        void OnEnable() { Application.logMessageReceived += OnLog; }
        void OnLog(string msg, string stack, LogType type)
        {
            if (msg != null && msg.StartsWith("[TDU2] Flash") && _log.Count < 400) _log.Add(msg);
        }

        /// <summary>Diagnose-Datei Logs/tdu2_pausemenu.txt (Projektordner): Zustand, FSCommands, Anzeigebaum des Films.</summary>
        void Dump(string reason)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[{DateTime.Now:HH:mm:ss}] {reason}  open={_open} bank={_bank} texts={(_texts?.Count ?? -1)} visible={(_ui != null && _ui.Visible)} timeScale={Time.timeScale}");
                foreach (var l in _log) sb.AppendLine("  " + l);
                if (_ui != null) sb.AppendLine(_ui.DumpTree());
                string dir = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Logs");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "tdu2_pausemenu.txt"), sb.ToString());
                Debug.Log("[TDU2] Pausemenü: Diagnose geschrieben (" + reason + ")");
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Pausemenü-Diagnose: " + ex.Message); }
        }

        void OnCommand(string cmd, string args)
        {
            if (TDU2Login.Active) return;   // Befehle des Profilmenüs behandelt TDU2Login
            LastCommand = cmd + (string.IsNullOrEmpty(args) ? "" : " " + args);
            if (_log.Count < 1500) _log.Add($"{Time.unscaledTime:0.00} FSCommand " + LastCommand);
            string c = (cmd ?? "").Trim().ToLowerInvariant();
            if (c.StartsWith("sndp:")) { TDU2FrontendSounds.Command(c); return; }   // Menü-Sounds (fe_snd.bnk)
            if (c == "dbxo") { _pendingDialog = true; return; }
            if (c.StartsWith("mylf:scrl:"))                                // Scrollbalken der Liste (Pfeile)
            {
                var l = _ui.ActiveList;
                if (l != null && c.EndsWith(":prss")) { if (c.Contains(":btnu:")) _ui.ListMove(l, -1); else if (c.Contains(":btnd:")) _ui.ListMove(l, 1); }
                return;
            }      // Bestätigungsdialog (Spiel zeigt ihn, Film wartet auf Antwort)
            if (HandleControlsCommand(c)) return;                     // Steuerung (Tastenbelegung)
            if (c == "pcrd:open") { OpenPlayerCard(); return; }       // Spielerkarte: Daten liefern
            if (HandlePartyCommand(c)) return;                        // Beitreten / Einladen (Spielerkarte)
            if (_options != null && _options.Handle(_ui, c)) return;   // Optionsseiten
            switch (c)
            {
                case "paou":           // Pause beendet (nach der Schließen-Animation)
                    Close();
                    break;
                case "rest" when _raceMode:   // Rennen neu starten
                    Close(); _raceMode = false; RaceRestart?.Invoke();
                    break;
                case "quit" when _raceMode:   // im Rennen: "Beenden" = Rennen abbrechen (nicht das Spiel)
                case "fain" when _raceMode:
                    Close(); _raceMode = false; RaceQuit?.Invoke();
                    break;
                case "exit:vali":      // "Spiel beenden" bestätigt
                case "quit":
                case "egam":
                    Close();
#if UNITY_EDITOR
                    UnityEditor.EditorApplication.isPlaying = false;
#else
                    Application.Quit();
#endif
                    break;
                default:
                    Debug.Log("FSCommand: " + LastCommand);
                    break;
            }
        }

        // ------------------------------------------------------------------ Bestätigungsdialoge ("dbxo")
        // Der Film setzt _root.DgbxCase/Msg1Txt … und meldet "dbxo"; das Spiel zeigt eine Ja/Nein-Box und ruft danach
        // DialogBox() mit _root.PlayerAnswer = "a" (Ja) bzw. "b" (Nein). Bis dahin bleiben Eingabe und Buttons gesperrt.
        // Hier: Ja bestätigen (Sprache wechseln, Steuerung speichern, Grafik übernehmen …).
        bool _pendingDialog;

        void AnswerDialog()
        {
            _pendingDialog = false;
            string kase = _ui.GetVariable("/:DgbxCase") ?? "";
            // Im Rennen: "Beenden"/"Neu starten" direkt ausführen (die Antwortkette des Films kommt nicht immer an)
            if (_raceMode && string.Equals(kase, "confirm_quit_race", StringComparison.OrdinalIgnoreCase)) { Close(); _raceMode = false; RaceQuit?.Invoke(); return; }
            if (_raceMode && string.Equals(kase, "confirm_restart", StringComparison.OrdinalIgnoreCase)) { Close(); _raceMode = false; RaceRestart?.Invoke(); return; }
            var owners = _ui.FunctionOwners("DialogBox");
            string target = null;
            foreach (var o in owners) if (o.IndexOf("options", StringComparison.OrdinalIgnoreCase) >= 0) { target = o; break; }
            if (target == null && owners.Count > 0) target = owners[owners.Count - 1];
            _log.Add($"{Time.unscaledTime:0.00} Dialog '{kase}' -> Ja ({target ?? "kein DialogBox"})");
            _ui.SetVariable("/:PlayerAnswer", "a");
            if (target == null || !_ui.CallClip(target, "DialogBox"))
            {
                // Fallback: Eingabe wieder freigeben, damit das Menü bedienbar bleibt
                _ui.SetValue("/:Input", true);
                _ui.SetValue("/:_buttons", true);
            }
        }

        // ------------------------------------------------------------------ Steuerung: Tastenbelegungs-Liste
        VmfPlayer.FlashList _controlsList;

        void OnListCreated(VmfPlayer.FlashList l)
        {
            if (_log.Count < 1500) _log.Add($"{Time.unscaledTime:0.00} ASFlashList {l.Name} ({VmfPlayer.PathOf(l.Owner)}) Zeilen {l.ViewSize}");
            if (string.Equals(l.Name, "controls_list", StringComparison.OrdinalIgnoreCase))
            {
                _controlsList = l;
                TDU2Controls.Load();
                FillControls(l);
            }
            else if (PlayerLists.Contains(l.Name ?? ""))
            {
                FillPlayers(l);
                _playerLists.RemoveAll(x => x.Owner == l.Owner);
                _playerLists.Add(l);
            }
            _ui.ListRefresh(l);
            _ui.ListLoaded(l);
            if (PlayerLists.Contains(l.Name ?? "")) ApplyPlayerLineStates(l);
        }

        // ------------------------------------------------------------------ Spielermenü (Freunde, Club, Lobby, Zuletzt gesehen)
        // Liste "players_list" (ASFlashList, Name = list_name): Zeilen line1..6 mit PlayerName / PlayerTitle / GlobalLevel,
        // Online-Symbol line%d/content/state (online/offline). A auf einer Zeile -> Spielerkarte (FSCommand "pcrd:open",
        // Daten in _root.listAvatarName/listMoney/… , dann _root.Clip.IsCardLoaded = true).
        static readonly HashSet<string> PlayerLists = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "friends", "club_members", "current_players", "recent_players", "black_list" };
        readonly List<VmfPlayer.FlashList> _playerLists = new List<VmfPlayer.FlashList>();
        readonly Dictionary<VmfPlayer.FlashList, List<Online.PlayerInfo>> _listPlayers = new Dictionary<VmfPlayer.FlashList, List<Online.PlayerInfo>>();
        Online.PlayerInfo _cardPlayer;
        float _nextListRefresh;

        void FillPlayers(VmfPlayer.FlashList l)
        {
            var gd = Online.TDU2GameData.Instance;
            var players = gd != null ? gd.ListFor(l.Name) : new List<Online.PlayerInfo>();
            _listPlayers[l] = players;
            l.ColumnVars = new[] { "PlayerName", "PlayerTitle", "GlobalLevel" };
            l.Rows.Clear();
            foreach (var p in players)
            {
                string title = p.CarName;
                if (l.Name == "recent_players" && !p.Online && p.LastSeen != default) title = (title.Length > 0 ? title + "  –  " : "") + p.LastSeen.ToString("dd.MM. HH:mm");
                l.Rows.Add(new[] { p.Nick, title, Math.Max(1, p.NbCars).ToString() });
            }
            if (l.Selected >= l.Rows.Count) { l.Selected = Math.Max(0, l.Rows.Count - 1); l.Top = 0; }
        }

        void ApplyPlayerLineStates(VmfPlayer.FlashList l)
        {
            if (!_listPlayers.TryGetValue(l, out var players)) return;
            string op = VmfPlayer.PathOf(l.Owner);
            for (int i = 1; i <= l.ViewSize; i++)
            {
                int r = l.Top + i - 1;
                if (r < 0 || r >= players.Count) continue;
                string line = (op == "/" ? "" : op) + "/line" + i;

                _ui.ForceVisible($"/players/players_list/line{i}/content/forbidden", false);
                _ui.GotoLabel(line + "/content/state", players[r].Online ? "online" : "offline", false);


                if (players[r].Nationality > 0) _ui.SetFrame(line + "/content/nationality", players[r].Nationality);   // Flagge = Bild Ländernummer
                // MyTDULife-Foto des Spielers (kommt per op "profile" nach, Version aus der Spielerliste)
                var pl = players[r];
                _ui.SetClipTexture(line + "/content/photo/lure", pl.Photo);
                if (pl.PhotoVer > 0 && (pl.Photo == null || pl.PhotoTexVer != pl.PhotoVer))
                    Online.TDU2GameData.Ensure().FetchLife(pl, _ => { if (_listPlayers.ContainsKey(l)) ApplyPlayerLineStates(l); });
            }
        }

        /// <summary>Offene Spielerliste regelmäßig aktualisieren (Spieler kommen/gehen).</summary>
        void UpdatePlayerLists()
        {
            if (_playerLists.Count == 0 || Time.unscaledTime < _nextListRefresh) return;
            _nextListRefresh = Time.unscaledTime + 1f;
            foreach (var l in _playerLists)
            {
                if (l.Owner == null || !_ui.IsShown(l.Owner)) continue;
                int before = l.Rows.Count;
                FillPlayers(l);
                if (l.Rows.Count != before && l.Active) { _ui.ListRefresh(l); }
                else _ui.ListRefresh(l);
                ApplyPlayerLineStates(l);
            }
        }

        void OnListValidated(VmfPlayer.FlashList l)
        {
            _cardPlayer = null;
            if (l != null && _listPlayers.TryGetValue(l, out var players) && l.Selected >= 0 && l.Selected < players.Count) _cardPlayer = players[l.Selected];
        }

        // ------------------------------------------------------------------ Spielerkarte: Kreise (Level/Fortschritt)
        /// <summary>Werte der Spielerkarte (Kreise "Wettbewerb/Sozial/Entdeckung/Sammlung" und Globales Level).</summary>
        public struct PlayerCardStats
        {
            public int GlobalLevel;            // Zahl in der Mitte (listGlobalLevel)
            public float GlobalProgress;       // Ring um das Globale Level, 0..100 (listGlobalPoints -> maskLevel)
            public int GlobalLevelPoints;      // listGlobalLvlPoints
            public float Racing;               // Wettbewerb 0..100 (listRacingPoints)
            public float Social;               // Sozial 0..100 (listSocialPoints)
            public float Discovery;            // Entdeckung 0..100 (listCruisingPoints)
            public float Collection;           // Sammlung 0..100 (listCollectionPoints)
            public string RacingLevelText;     // z.B. "3/20" (listRacingLevelCount)
            public string CollectionLevelText; // z.B. "12/50" (listCollectionLevelCount)
            public int Houses;                 // listNbHouses
            public string Mood;                // Smiley-Label (listMood), null = unverändert
            public static PlayerCardStats Default => new PlayerCardStats { GlobalLevel = 1, RacingLevelText = "0", CollectionLevelText = "0" };
        }

        /// <summary>
        /// Liefert die Werte für die Spielerkarte eines Spielers (später z.B. aus dem Server-Spielstand). null = Standard (Level 1, leer).
        /// Beispiel: TDU2PauseMenu.CardStatsProvider = p => new TDU2PauseMenu.PlayerCardStats { GlobalLevel = 5, Racing = 40, ... };
        /// </summary>
        public static Func<Online.PlayerInfo, PlayerCardStats> CardStatsProvider;

        /// <summary>Werte in die Spielerkarte schreiben; ist die Karte offen, wird sie neu gezeichnet (setPlayerCard des Films).</summary>
        public void SetPlayerCardValues(PlayerCardStats s) => SetPlayerCardValues(s, true);

        void SetPlayerCardValues(PlayerCardStats s, bool redraw)
        {
            if (_ui == null) return;
            _ui.SetValue("/:listGlobalLevel", Math.Max(1, s.GlobalLevel));
            _ui.SetValue("/:listGlobalPoints", Mathf.Clamp(s.GlobalProgress, 0f, 100f));
            _ui.SetValue("/:listGlobalLvlPoints", s.GlobalLevelPoints);
            _ui.SetVariable("/:GlobalLevelCount", Math.Max(1, s.GlobalLevel).ToString());
            _ui.SetValue("/:listRacingPoints", Mathf.Clamp(s.Racing, 0f, 100f));
            _ui.SetValue("/:listSocialPoints", Mathf.Clamp(s.Social, 0f, 100f));
            _ui.SetValue("/:listCruisingPoints", Mathf.Clamp(s.Discovery, 0f, 100f));
            _ui.SetValue("/:listCollectionPoints", Mathf.Clamp(s.Collection, 0f, 100f));
            _ui.SetVariable("/:listRacingLevelCount", s.RacingLevelText ?? "0");
            _ui.SetVariable("/:listCollectionLevelCount", s.CollectionLevelText ?? "0");
            _ui.SetVariable("/:listNbHouses", s.Houses.ToString());
            if (!string.IsNullOrEmpty(s.Mood)) _ui.SetVariable("/:listMood", s.Mood);
            // offene Karte neu füllen
            string card = _ui.GetVariable("/:PlayerCardPath");
            if (string.IsNullOrEmpty(card)) card = "/players/player_card";
            var ci = redraw ? _ui.Find(card)?.Sprite : null;
            if (ci != null && _ui.IsShown(ci)) _ui.CallClip(card, "setPlayerCard");
        }

        void OpenPlayerCard()
        {
            var p = _cardPlayer;
            var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            _ui.SetVariable("/:listAvatarName", p?.Nick ?? "");
            _ui.SetNumber("/:listNationality", p != null && p.Nationality > 0 ? p.Nationality : 1);
            _ui.SetVariable("/:listMoney", p != null ? p.Money.ToString("N0", de) + " $" : "");
            _ui.SetVariable("/:listNbCars", (p?.NbCars ?? 0).ToString());
            _ui.SetVariable("/:listNbHouses", "0");
            // MyTDULife: Smiley, Stimmungstext und Avatar-Foto (Foto/Profil kommen per op "profile" nach)
            _ui.SetVariable("/:listMood", p != null && !string.IsNullOrEmpty(p.Mood) ? p.Mood : "neutral");
            _ui.SetVariable("/:listMoodTxt", p != null ? (p.MoodTxt ?? "") : "");
            ApplyCardPhoto(p);
            if (p != null) Online.TDU2GameData.Ensure().FetchLife(p, OnCardLife);
            _ui.SetValue("/:listIsConnected", p != null && p.Online);
            var stats = PlayerCardStats.Default;
            try { if (CardStatsProvider != null && p != null) stats = CardStatsProvider(p); } catch (Exception ex) { Debug.LogException(ex); }
            SetPlayerCardValues(stats, false);   // die Karte liest die Werte selbst, sobald IsCardLoaded gesetzt ist
            string card = _ui.GetVariable("/:PlayerCardPath");
            if (string.IsNullOrEmpty(card)) card = "/players/player_card";
            _ui.SetValue(card + ":IsCardLoaded", true);
            // Der Lader der Karte fragt "_root.Clip.IsCardLoaded" ab (Clip = Verweis auf das offene Menü, nicht unbedingt PlayerCardPath)
            _ui.SetValue("_root.Clip.IsCardLoaded", true);
        }

        // ------------------------------------------------------------------ Eigenes MyTDULife-Foto (Ausweis im Pausemenü / Spielfortschritt)
        static readonly string[] OwnPhotoClips = { "/pause_menu/data_extd/idcard/avatar_photo/lure", "/gameprogress_menu/transition_menu/content_menu/main_menu/pbox/idcard/avatar_photo/lure" };
        Texture2D _ownPhoto; int _ownPhotoVer = -1;

        void UpdateOwnPhoto()
        {
            var life = Online.TDU2Session.Save?.myLife;
            int ver = life != null && !string.IsNullOrEmpty(life.photo) ? life.photoVer : 0;
            if (ver == _ownPhotoVer) return;
            _ownPhotoVer = ver;
            if (_ownPhoto != null) { Destroy(_ownPhoto); _ownPhoto = null; }
            if (ver > 0) _ownPhoto = Online.TDU2GameData.DecodePhoto(life.photo);
            foreach (var c in OwnPhotoClips) _ui.SetClipTexture(c, _ownPhoto);
        }

        string CardPath
        {
            get
            {
                string card = _ui != null ? _ui.GetVariable("/:PlayerCardPath") : null;
                return string.IsNullOrEmpty(card) ? "/players/player_card" : card;
            }
        }

        /// <summary>Avatar-Foto des Spielers in die Karte (content/photo/lure); ohne Foto leer.</summary>
        void ApplyCardPhoto(Online.PlayerInfo p)
        {
            if (_ui == null) return;
            _ui.SetClipTexture(CardPath + "/content/photo/lure", p != null ? p.Photo : null);
        }

        /// <summary>MyTDULife ist angekommen: offene Karte desselben Spielers aktualisieren.</summary>
        void OnCardLife(Online.PlayerInfo p)
        {
            if (_ui == null || p == null || p != _cardPlayer) return;
            _ui.SetVariable("/:listMood", !string.IsNullOrEmpty(p.Mood) ? p.Mood : "neutral");
            _ui.SetVariable("/:listMoodTxt", p.MoodTxt ?? "");
            ApplyCardPhoto(p);
            var ci = _ui.Find(CardPath)?.Sprite;
            if (ci != null && _ui.IsShown(ci)) _ui.CallClip(CardPath, "setPlayerCard");
        }

        // ------------------------------------------------------------------ Beitreten / Einladen (Spielerkarte, TDU2Party)
        // Karte: "pcrd:pjoi" (Beitreten) bzw. join_player.joinPlayer() -> "join:wait" (Ladeanzeige wartet auf _root.CanJoinPlayer)
        // -> "pjoi"; Fehler: _root.JoinError + timeOutLoading() (Dialog player_join_fail). "pcrd:pinv" = als Beifahrer einladen.
        static Online.TDU2Party Party
        {
            get
            {
                if (Online.TDU2Party.Instance != null) return Online.TDU2Party.Instance;
                var gd = Online.TDU2GameData.Instance;
                return gd != null ? Online.TDU2Party.Ensure(gd.gameObject) : null;
            }
        }

        /// <summary>
        /// Spieler der Karte als aktueller Online-Eintrag (Zuletzt-gesehen-/Freundes-Einträge sind gespeichert und haben keine
        /// bzw. eine alte Sitzungsnummer -> über den Namen auflösen). null = nicht online.
        /// </summary>
        Online.PlayerInfo LiveCardPlayer()
        {
            var p = _cardPlayer;
            var gd = Online.TDU2GameData.Instance;
            if (gd == null) return null;
            string nick = p?.Nick;
            if (string.IsNullOrEmpty(nick)) nick = _ui.GetVariable("/:listAvatarName");
            if (string.IsNullOrEmpty(nick)) return null;
            foreach (var o in gd.Players.Values)
                if (string.Equals(o.Nick, nick, StringComparison.OrdinalIgnoreCase)) return o;
            return null;
        }

        void PartyNote(string text)
        {
            _log.Add($"{Time.unscaledTime:0.00} " + text);
            Debug.Log("[TDU2] Spielerkarte: " + text);
            Online.TDU2GameData.Instance?.AddChatLine("TDU2", text, 0);
        }

        bool HandlePartyCommand(string c)
        {
            var p = c == "join:wait" || c == "pjoi" || c == "pcrd:pjoi" || c == "pcrd:pinv" ? LiveCardPlayer() : _cardPlayer;
            switch (c)
            {
                case "join:wait":
                    {
                        string err = "Spieler nicht gefunden.";
                        bool ok = p != null && p.Id != 0 && Party != null && Party.CanJoin(p.Id, out err);
                        _log.Add($"{Time.unscaledTime:0.00} Beitreten {p?.Nick ?? "-"}: " + (ok ? "ok" : err));
                        if (ok) _ui.SetValue("/:CanJoinPlayer", true);
                        else
                        {
                            _ui.SetVariable("/:JoinError", err);
                            var owners = _ui.FunctionOwners("timeOutLoading");
                            if (owners.Count == 0 || !_ui.CallClip(owners[owners.Count - 1], "timeOutLoading")) _ui.SetValue("/:Input", true);
                        }
                        return true;
                    }
                case "join:canc":
                    return true;
                case "pjoi":
                case "pcrd:pjoi":
                    if (p != null && p.Id != 0 && Party != null)
                    {
                        int id = p.Id;
                        Close();
                        Party.Join(id);
                    }
                    return true;
                case "pcrd:pinv":
                    if (p == null || p.Id == 0) PartyNote((_cardPlayer?.Nick ?? "Spieler") + " ist nicht online – Einladung nicht möglich.");
                    else if (p.Id == Online.TDU2Session.PlayerId) PartyNote("Du kannst dich nicht selbst einladen.");
                    else if (Party != null) Party.Invite(p.Id);
                    return true;
            }
            return false;
        }

        void FillControls(VmfPlayer.FlashList l)
        {
            bool de = SavedLanguage() == "ge";
            l.Rows.Clear();
            foreach (var a in TDU2Controls.All) l.Rows.Add(new[] { de ? a.Name : a.NameEn, TDU2Controls.BindText(a), "" });
        }

        TDU2Controls.Action SelectedControl()
        {
            var l = _controlsList;
            if (l == null || l.Selected < 0 || l.Selected >= TDU2Controls.All.Count) return null;
            return TDU2Controls.All[l.Selected];
        }

        bool InBinder()
        {
            var o = _controlsList?.Owner;
            if (o == null || !_ui.IsShown(o)) return false;
            var lb = o.Timeline.Labels;
            return lb.TryGetValue("binder_in", out int a) && lb.TryGetValue("binder_out", out int b) && o.Frame >= a && o.Frame < b;
        }

        /// <summary>Wartet das Menü auf eine Taste: nächste Taste zuweisen (ESC = abbrechen). true = Eingabe verbraucht.</summary>
        bool UpdateBinder()
        {
            if (!InBinder()) return false;
            var key = TDU2Controls.AnyKeyDown();
            if (key == KeyCode.None) return true;
            var act = SelectedControl();
            if (key != KeyCode.Escape && act != null)
            {
                TDU2Controls.Bind(act, key);
                FillControls(_controlsList);
                _ui.ListRefresh(_controlsList);
                _log.Add($"{Time.unscaledTime:0.00} Taste {key} -> {act.Id}");
            }
            _ui.GotoLabel(VmfPlayer.PathOf(_controlsList.Owner), "binder_out", true);
            return true;
        }

        bool HandleControlsCommand(string c)
        {
            DebugManager.Debug("Sendet FSCommand: " + c);
            switch (c)
            {
                case "opts:cont:vadt":            // Y: speichern
                    TDU2Controls.Save();
                    return true;
                case "opts:cont:canc":            // verwerfen
                    TDU2Controls.Load();
                    return true;
                case "opts:cont:bind:delt":       // Belegung löschen
                    {
                        var act = SelectedControl();
                        if (act != null && _controlsList != null) { TDU2Controls.Unbind(act); FillControls(_controlsList); _ui.ListRefresh(_controlsList); }
                        return true;
                    }
            }
            return false;
        }

        void OnDisable()
        {
            Application.logMessageReceived -= OnLog;
            if (_open) Close();
        }
    }
}
