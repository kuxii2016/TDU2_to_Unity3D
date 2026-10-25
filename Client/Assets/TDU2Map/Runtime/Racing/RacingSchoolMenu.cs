using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using TDU2.Flash;
using TDU2.Formats;
using TDU2.Online;
using TDU2.Vehicles;
using UnityEngine;

namespace TDU2.Racing
{
    /// <summary>
    /// Rennschule (Interieur-Auslöser Typ 20 = Instruktor). Oberfläche ist der Original-Film frontend/&lt;res&gt;/r_school.bnk.
    ///
    /// Ablauf und Schnittstelle wie in der EXE (Menü-Klasse bei 0xb8fe60..0xb92600, FSCommand-Handler 0xb91ae0):
    ///   Öffnen        GotoLabel("/", "open_school"); danach öffnet die Root-Funktion Open() das Lizenzrad (im Spiel per
    ///                 nativem _root.rschoolOpen() nach Auswahl des Instruktors).
    ///   rsch:stli     Lizenzliste: /:li%d_state ("done"/"current"/"locked"), /:li%d_gp1, /:li%d_gp2 ("A7","A6" / "empty"),
    ///                 /:nb_licences, /:num_licence (erste offene Lizenz)
    ///   rsch:dili     Lizenzkarte: /:LicenceName, /:LicenceTxt, /:LicenceLevel, /:LicencePlayerName, /:LicenceDate ("25SEP1986"),
    ///                 /:LicenceNumber, /:nb_tests_done, /:num_test = 0, /:nb_tests_total, /:licence_group1/2
    ///   rsch:dits     Prüfungsliste: /tests/line%d/:done, :t_name, :meteo ("sun"/"rain"/"night", ig2 rswh),
    ///                 :place ("r_school"/"road", rsl1), :type ("turn".."jump", rsl2)
    ///   rsch:dtup     /:TestDesc der markierten Prüfung (/:num_test)
    ///   rsch:tspr     Prüfung /:num_test der Lizenz /:num_licence starten
    ///   rsch:exit     Schule verlassen;  dbxo = Ja/Nein-Dialog (hier sofort "ja")
    /// Welche Lizenzen eine Schule zeigt: Schultyp aus dem Spot-Code (ERS_A Asphalt, ERS_B Dirt, ERS_C Classic) und die Insel.
    /// Freischaltung: <see cref="RaceDatabase.RequiredCategory"/>. Fortschritt: <see cref="RaceProgress"/>.
    ///
    /// Prüfung starten: Auto der Prüfung (ig2 "VMod") laden, auf den Startpunkt (spotworld.bin) setzen, nachdem die Strecke
    /// gestreamt ist; <see cref="TDU2RaceManager"/> übernimmt Countdown, Zeit, Tore und Props, <see cref="RaceHud"/> das HUD,
    /// danach <see cref="RaceResults"/> (results.bnk). "Zurück zur Akademie" setzt das eigene Auto vor die Schule und öffnet
    /// das Menü wieder (nach einer gewonnenen Lizenz mit der Lizenz-Animation, /:back_win = 1).
    /// </summary>
    public sealed class RacingSchoolMenu : MonoBehaviour
    {
        public TDU2VehicleManager Manager;
        [Tooltip("Interieur, aus dem heraus die Rennschule geöffnet wird (für das Verlassen vor dem Start).")]
        public TDU2Interiors Interiors;
        [Tooltip("Original-Film (r_school.bnk) laden.")]
        public bool UseOriginalFilm = true;
        [Tooltip("Alle Lizenzen freischalten (Test/Debug) – im Original nur über die Voraussetzungen.")]
        public bool UnlockAllLicences = false;
        [Tooltip("Immer das Auto der Prüfung verwenden (ig2 VMod) wie im Spiel.")]
        public bool UseTestCar = true;
        [Tooltip("Ab so vielen Fehlversuchen hintereinander gibt es mehr Zeit (0 = nie).")]
        public int EaseAfterFails = 3;
        [Tooltip("Zusätzliche Zeit je weiterem Fehlversuch (Anteil der Original-Zeit, 0.05 = +5 %).")]
        public float EaseStep = 0.05f;
        [Tooltip("Höchstens so viel zusätzliche Zeit (Anteil).")]
        public float EaseMax = 0.30f;
        [Tooltip("Schule, falls der Spot-Code nicht ermittelt werden kann ('A', 'B', 'C'; leer = alle der Insel).")]
        public string FallbackSchool = "";

        VmfPlayer _film;
        string _bank, _bnkDir;
        RaceRepository _repo;
        int _island = 1;
        char _school = '\0';

        TDU2RaceManager _race;
        RaceHud _hud;
        RaceResults _results;
        GameObject _raceGo;

        bool _open, _fromInterior;
        readonly List<LicenseInfo> _licences = new List<LicenseInfo>();
        List<RaceDefinition> _tests = new List<RaceDefinition>();
        int _licIndex;
        int _testIndex;
        bool _backWin;

        // Spot (für die Rückkehr): Ausfahrt in Inselkoordinaten
        string _spotCode;
        Vector3 _spotExit; float _spotHeading; bool _haveSpot;
        CarPhysics _playerCar;

        // Startvorgang
        enum Phase { None, StreamToStart, StreamToSchool, EnterSchool }
        Spot _spotObj;
        [Tooltip("\"Zurück zur Akademie\": wieder ins Rennschul-Interieur (wie im Spiel) statt vor die Schule.")]
        public bool ReturnIntoInterior = true;
        Phase _phase;
        float _phaseT;
        RaceDefinition _pending;
        CarPhysics _pendingCar;
        float _openDelay = -1f;
        float _resultDelay = -1f;

        float _savedTimeScale = 1f;
        CursorLockMode _savedLock;
        bool _savedCursor;

        public bool IsOpen => _open;
        static int _openMenus, _lastUiFrame = -10;
        /// <summary>
        /// true, solange Rennschul-Menü oder Ergebnis offen sind (und 2 Frames danach): ESC gehört dann diesen Filmen,
        /// das Pausemenü darf nicht zusätzlich aufgehen (sonst merkt es sich timeScale 0 und das Spiel bleibt stehen).
        /// </summary>
        public static bool BlocksPauseMenu => _openMenus > 0 || RaceResults.AnyOpen || TDU2Championships.ResultsOpen || TDU2Airport.IsOpen || TDU2StickerShop.Busy || TDU2Interiors.CasinoMenuOpen || Time.frameCount - _lastUiFrame <= 2;
        internal static void NoteUiClosed() => _lastUiFrame = Time.frameCount;
        /// <summary>Grund, warum das Pausemenü gerade gesperrt ist (Diagnose), null = frei.</summary>
        public static string BlockReason()
        {
            if (_openMenus > 0) return "Rennschul-Menü offen (" + _openMenus + ")";
            if (RaceResults.AnyOpen) return "Ergebnis-Film offen";
            if (TDU2Championships.ResultsOpen) return "Meisterschaft: Ergebnisphase";
            if (TDU2Airport.IsOpen) return "Flughafen offen";
            if (TDU2StickerShop.IsOpen) return "Sticker-Shop offen";
            if (Time.frameCount - _lastUiFrame <= 2) return "Menü gerade geschlossen";
            return null;
        }
        bool _mutedRadio;
        public TDU2RaceManager Race => _race;
        public RaceHud Hud => _hud;
        public string LastFilmCommand { get; private set; }
        public bool InTest => _race != null && _race.State != RaceState.Idle && _race.State != RaceState.Finished;

        // ------------------------------------------------------------------ Update / Eingabe

        void Update()
        {
            if (Manager == null) Manager = GetComponent<TDU2VehicleManager>();
            if (_hud != null && _hud.Minimap == null && Manager != null) _hud.Minimap = Manager.Minimap;
            TickPhase();
            if (_resultDelay >= 0f) { _resultDelay -= Time.unscaledDeltaTime; if (_resultDelay < 0f) ShowResults(); }
            if (!_open) return;
            if (Watchdog()) return;

            if (_openDelay >= 0f)
            {
                _openDelay -= Time.unscaledDeltaTime;
                if (_openDelay < 0f && _film != null && !_backWin) _film.CallClip("/", "Open");   // wie _root.rschoolOpen()
            }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (_film == null)
            {
                // ohne Film: einfache Tastatursteuerung (Pfeile Lizenz/Prüfung, Enter starten, Esc schließen)
                if (Input.GetKeyDown(KeyCode.LeftArrow)) SelectLicence((_licIndex + _licences.Count - 1) % Mathf.Max(1, _licences.Count));
                if (Input.GetKeyDown(KeyCode.RightArrow)) SelectLicence((_licIndex + 1) % Mathf.Max(1, _licences.Count));
                if (Input.GetKeyDown(KeyCode.UpArrow)) _testIndex = Mathf.Max(0, _testIndex - 1);
                if (Input.GetKeyDown(KeyCode.DownArrow)) _testIndex = Mathf.Min(_tests.Count - 1, _testIndex + 1);
                if (Input.GetKeyDown(KeyCode.Return)) StartTest(_testIndex);
                if (Input.GetKeyDown(KeyCode.Escape)) Close();
                return;
            }
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) _film.Input("Move", "up");
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) _film.Input("Move", "down");
            if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) _film.Input("Move", "left");
            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) _film.Input("Move", "right");
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space))
            {
                if (!_film.Input("A")) _film.CallClip("/", "Open");   // auf der Schulübersicht: Lizenzrad wieder öffnen
            }
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace))
            {
                if (!_film.Input("B")) Close();
            }
            if (Input.GetKeyDown(KeyCode.F10)) Close();   // Notausgang
#endif
        }

        // ------------------------------------------------------------------ Absicherung gegen hängendes Menü
        float _exitSeen = -1f, _lastEsc = -10f;

        /// <summary>
        /// Das Menü hält die Spielzeit an (timeScale 0). Bleibt es offen, obwohl man nicht mehr in der Schule ist oder der Film
        /// den Ausgang schon gespielt hat (Label school_out / SelItemType = exit) ohne "rsch:exit" zu senden, steht das Spiel.
        /// Dann hier schließen. Doppeltes ESC (innerhalb 1,5 s) schließt immer.
        /// </summary>
        bool Watchdog()
        {
            if (_fromInterior && Interiors != null && !Interiors.Inside && !InTest)
            {
                TDU2DebugLog.Event("Watchdog: Interieur verlassen, Menü noch offen -> schließen");
                Close(); return true;
            }
            if (_film != null)
            {
                string lab = _film.Label("/") ?? "";
                string sel = _film.GetVariable("/:SelItemType") ?? "";
                bool exiting = string.Equals(lab, "school_out", StringComparison.OrdinalIgnoreCase) || string.Equals(sel, "exit", StringComparison.OrdinalIgnoreCase);
                if (exiting) { if (_exitSeen < 0f) _exitSeen = Time.unscaledTime; }
                else _exitSeen = -1f;
                if (_exitSeen >= 0f && Time.unscaledTime - _exitSeen > 1.5f)
                {
                    TDU2DebugLog.Event("Watchdog: Film im Ausgang (" + lab + "/" + sel + ") ohne rsch:exit -> schließen");
                    _exitSeen = -1f; _film.SetVariable("/:SelItemType", "");
                    Close(); return true;
                }
            }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (Time.unscaledTime - _lastEsc < 1.5f) { TDU2DebugLog.Event("Watchdog: doppeltes ESC -> schließen"); _lastEsc = -10f; Close(); return true; }
                _lastEsc = Time.unscaledTime;
            }
#endif
            return false;
        }

        // ------------------------------------------------------------------ Öffnen / Schließen

        public void Open()
        {
            if (_open) return;
            _fromInterior = Interiors != null && Interiors.Inside;
            if (_fromInterior) CaptureSpot();
            // Menü ohne Interieur (Rückkehr nach einer Prüfung): Autoradio aus wie im Spiel (Interieur regelt das selbst)
            _mutedRadio = !_fromInterior && TDU2Radio.Instance != null && !TDU2Radio.Instance.Muted;
            if (_mutedRadio) TDU2Radio.Instance.Muted = true;
            EnsureData();
            EnsureRace();
            LoadFilm();
            _open = true;
            _openMenus++;
            _savedTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
            Time.timeScale = 0f;
            _savedLock = Cursor.lockState; _savedCursor = Cursor.visible;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            FillLicences(true);
            if (_film != null)
            {
                PublishBase();
                // back_win = 1 spielt im Film die Lizenz-Animation (licences "win" + win_message). Deren Bestätigung (A) wird im
                // Nachbau nicht sauber zugestellt -> Menü bleibt hängen. Die Lizenz zeigt schon das Ergebnis-Fenster, hier normal öffnen.
                _film.SetValue("/:back_win", 0);
                _film.Visible = true;
                _film.GotoLabel("/", "open_school", true);
                _openDelay = 0.35f;
            }
            Debug.Log("[TDU2] Rennschule geöffnet: Spot " + (_spotCode ?? "?") + ", Schule " + (_school == '\0' ? "alle" : _school.ToString()) +
                      ", Insel " + _island + ", Lizenzen " + _licences.Count + ", Film " + (_bank != null ? "ja" : "nein") + (_backWin ? ", Lizenz gewonnen" : ""));
            _backWin = false;
        }

        public void Close()
        {
            if (!_open) return;
            _open = false;
            _openMenus = Mathf.Max(0, _openMenus - 1);
            NoteUiClosed();
            _openDelay = -1f;
            if (_mutedRadio && TDU2Radio.Instance != null) TDU2Radio.Instance.Muted = false;
            _mutedRadio = false;
            Time.timeScale = _savedTimeScale;
            Cursor.lockState = _savedLock;
            Cursor.visible = _savedCursor;
            TDU2DebugLog.Event("Rennschul-Menü geschlossen: timeScale -> " + _savedTimeScale + ", Cursor -> " + _savedLock);
            if (_film != null) { _film.Visible = false; _film.GotoLabel("/", "default", false); }
        }

        void OnDestroy()
        {
            if (_private) PrivateSession(false);
            if (_film != null) { Destroy(_film.gameObject); _film = null; }
            if (_race != null) _race.Abort();
            if (_autopilot != null) _autopilot.End();
            if (_raceGo != null) { Destroy(_raceGo); _raceGo = null; }
        }

        // ------------------------------------------------------------------ Daten

        void EnsureData()
        {
            string bnkDir = Manager != null ? Manager.BnkDir : null;
            if (string.IsNullOrEmpty(bnkDir)) return;
            if (_repo == null || _bnkDir != bnkDir)
            {
                _bnkDir = bnkDir;
                _repo = RaceRepository.Load(bnkDir);
            }
            RaceTexts.Ensure(bnkDir, TDU2PauseMenu.SavedLanguage());
            _island = CurrentIsland();
            _school = SchoolFromSpot(_spotCode);

            if (_school == '\0' && !string.IsNullOrEmpty(FallbackSchool)) _school = char.ToUpperInvariant(FallbackSchool[0]);
            _licences.Clear();
            if (_repo?.Database != null) _licences.AddRange(_repo.Database.RacingSchools(_island, _school));
            if (_licences.Count == 0 && _repo?.Database != null && _school != '\0') _licences.AddRange(_repo.Database.RacingSchools(_island));
        }

        /// <summary>ERS_A = Asphalt, ERS_B = Dirt, ERS_C = Classic.</summary>
        static char SchoolFromSpot(string code)
        {
            if (string.IsNullOrEmpty(code)) return '\0';
            var c = code.ToUpperInvariant();
            int i = c.IndexOf("ERS_", StringComparison.Ordinal);
            if (i >= 0 && i + 4 < c.Length && "ABC".IndexOf(c[i + 4]) >= 0) return c[i + 4];
            return '\0';
        }

        /// <summary>Aktuellen Spot des Interieurs lesen (privates Feld _spot von TDU2Interiors, ohne die Datei zu ändern).</summary>
        void CaptureSpot()
        {
            try
            {
                var f = typeof(TDU2Interiors).GetField("_spot", BindingFlags.NonPublic | BindingFlags.Instance);
                if (f?.GetValue(Interiors) is Spot s)
                {
                    _spotObj = s;
                    _spotCode = s.Code;
                    _spotExit = s.Position2.sqrMagnitude > 1f ? s.Position2 : s.Position;
                    _spotHeading = s.Heading2;
                    _haveSpot = true;
                }
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Rennschule: Spot nicht lesbar: " + ex.Message); }
        }

        /// <summary>Aktuelle Insel als DBLicense._island (1 = Hawaii, 0 = Ibiza).</summary>
        int CurrentIsland()
        {
            string n = Manager != null && Manager.Streamer != null && Manager.Streamer.Index != null ? Manager.Streamer.Index.IslandName : "";
            if (n.StartsWith("i", StringComparison.OrdinalIgnoreCase)) return 0;
            return 1;
        }

        // ------------------------------------------------------------------ Lizenzen / Prüfungen

        bool IsLicenceWon(LicenseInfo l)
        {
            if (l == null) return false;
            if (RaceProgress.IsLicenceWon(l.Hash)) return true;
            // alter Fortschritt: alle Prüfungen bestanden
            if (l.ChallengeFiles.Count == 0) return false;
            foreach (var t in l.ChallengeFiles) if (!RaceProgress.IsTestDone(t)) return false;
            return true;
        }

        bool IsUnlocked(LicenseInfo l)
        {
            if (UnlockAllLicences) return true;
            int req = RaceDatabase.RequiredCategory(l.VehicleCategory1);
            if (req == 0) return true;
            if (req < 0) return false;
            var prev = _repo?.Database?.SchoolLicenseWithCategory(req);
            return prev != null && IsLicenceWon(prev);
        }

        string LicenceState(LicenseInfo l) => !IsUnlocked(l) ? "locked" : IsLicenceWon(l) ? "done" : "current";

        /// <summary>rsch:stli – Lizenzliste in den Film schreiben, aktuelle Lizenz bestimmen.</summary>
        void FillLicences(bool chooseCurrent)
        {
            if (chooseCurrent)
            {
                int cur = -1;
                for (int i = 0; i < _licences.Count && cur < 0; i++) if (LicenceState(_licences[i]) == "current") cur = i;
                if (cur < 0) cur = Mathf.Clamp(_licIndex, 0, Mathf.Max(0, _licences.Count - 1));
                SelectLicence(cur);
            }
            if (_film == null) return;
            for (int i = 0; i < _licences.Count; i++)
            {
                var l = _licences[i];
                _film.SetVariable("/:li" + i + "_state", LicenceState(l));
                _film.SetVariable("/:li" + i + "_gp1", l.Group1);
                _film.SetVariable("/:li" + i + "_gp2", l.Group2);
            }
            _film.SetValue("/:nb_licences", _licences.Count);
            _film.SetValue("/:num_licence", _licIndex);
        }

        void SelectLicence(int index)
        {
            if (_licences.Count == 0) { _tests = new List<RaceDefinition>(); _licIndex = 0; _testIndex = -1; return; }
            _licIndex = Mathf.Clamp(index, 0, _licences.Count - 1);
            _tests = _repo.BuildForLicense(_licences[_licIndex]);
            _testIndex = FirstUndone();
        }

        int FirstUndone()
        {
            for (int i = 0; i < _tests.Count; i++) if (!RaceProgress.IsTestDone(_tests[i].Name)) return i;
            return 0;
        }

        int DoneCount() { int n = 0; foreach (var t in _tests) if (RaceProgress.IsTestDone(t.Name)) n++; return n; }

        /// <summary>rsch:dili – Lizenzkarte der Lizenz /:num_licence.</summary>
        void FillLicenceCard()
        {
            if (_film == null || _licences.Count == 0) return;
            var l = _licences[_licIndex];
            var gd = TDU2GameData.Instance;
            string player = gd != null && !string.IsNullOrEmpty(gd.PlayerName) ? gd.PlayerName : "Player";
            string name = (_school == 'B' ? RaceTexts.Get(1744, "Dirt") : _school == 'C' ? RaceTexts.Get(1745, "Classic") : RaceTexts.Get(1743, "Asphalt"));
            _film.SetVariable("/:LicenceName", l.DisplayName);
            _film.SetVariable("/:LicenceTxt", RaceTexts.Get(l.DescHc, name));
            _film.SetVariable("/:LicenceLevel", "");
            _film.SetVariable("/:LicencePlayerName", player);
            var date = RaceProgress.LicenceDate(l.Hash) ?? DateTime.Now;
            string[] mon = { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };
            _film.SetVariable("/:LicenceDate", date.Day.ToString("00", CultureInfo.InvariantCulture) + mon[date.Month - 1] + date.Year.ToString(CultureInfo.InvariantCulture));
            _film.SetVariable("/:LicenceNumber", "411 14 38 42 6116");
            _film.SetVariable("/:licence_group1", l.Group1);
            _film.SetVariable("/:licence_group2", l.Group2);
            _film.SetVariable("/:Licence_group1", l.Group1);
            _film.SetVariable("/:Licence_group2", l.Group2);
            _film.SetVariable("/:nb_tests_done", DoneCount().ToString(CultureInfo.InvariantCulture));
            _film.SetVariable("/:nb_tests_total", _tests.Count.ToString(CultureInfo.InvariantCulture));
            _film.SetValue("/:nb_tests_total", _tests.Count);
            _film.SetValue("/:num_test", 0);
            _film.SetValue("/:IsLicensed", IsLicenceWon(l) ? 1 : 0);
        }

        static readonly string[] TypeLabels = { "turn", "turn90", "turn180", "slalom", "overtaking", "drift", "acceleration", "braking", "mixed", "circuit", "manual", "obstable", "jump" };
        static readonly string[] MeteoLabels = { "sun", "rain", "night" };

        /// <summary>rsch:dits – Prüfungsliste der Lizenz /:num_licence.</summary>
        void FillTests()
        {
            if (_film == null) return;
            for (int i = 0; i < _tests.Count && i < 8; i++)
            {
                var t = _tests[i];
                string p = "/tests/line" + i;
                _film.SetVariable(p + ":done", RaceProgress.IsTestDone(t.Name) ? "1" : "0");
                _film.SetVariable(p + ":t_name", RaceTexts.Get(t.NameHc, t.Title));
                _film.SetVariable(p + ":meteo", MeteoLabels[Mathf.Clamp((int)t.Weather, 0, 2)]);
                _film.SetVariable(p + ":place", t.Place == SchoolTestPlace.Road ? "road" : "r_school");
                _film.SetVariable(p + ":type", TypeLabels[Mathf.Clamp((int)t.TestType, 0, TypeLabels.Length - 1)]);
            }
            _film.SetValue("/:nb_tests_total", _tests.Count);
            _film.SetValue("/:num_test", Mathf.Clamp(_testIndex, 0, Mathf.Max(0, _tests.Count - 1)));
        }

        /// <summary>rsch:dtup – Beschreibung der markierten Prüfung.</summary>
        void FillTestDesc(int i)
        {
            if (_film == null || i < 0 || i >= _tests.Count) return;
            var t = _tests[i];
            string desc = RaceTexts.Get(t.DescHc, "");
            string limit = t.HasTimeLimit ? RaceHud.FormatRaceTimeMs(t.TimeLimitMs) : t.TargetTimeMs > 0 ? RaceHud.FormatRaceTimeMs(t.TargetTimeMs) : "";
            int best = RaceProgress.BestTimeMs(t.Name);
            if (limit.Length > 0) desc += "\n\n" + (t.HasTimeLimit ? "Zeitlimit: " : "Zielzeit: ") + limit;
            if (best > 0) desc += "   Bestzeit: " + RaceHud.FormatRaceTimeMs(best);
            _film.SetVariable("/:TestDesc", desc);
        }

        void PublishBase()
        {
            if (_film == null) return;
            FlashLocalization.Apply(_film, FlashLocalization.Load(_bnkDir, TDU2PauseMenu.SavedLanguage()));
            _film.SetValue("/:WhatPlatform", 1);
            _film.SetValue("/:fake", 0);
            _film.SetValue("/:FirstTime", false);
            _film.SetValue("/:Input", true);
            _film.SetValue("/:IsRacingSchool", true);
            _film.SetVariable("/:WhatPage", "racing_school");
        }

        // ------------------------------------------------------------------ Original-Film (r_school.bnk)

        void LoadFilm()
        {
            if (!UseOriginalFilm || _film != null || Manager == null || string.IsNullOrEmpty(Manager.BnkDir)) return;
            string bank = null;
            foreach (var res in new[] { "hires", "medres", "lowres" })
            {
                bank = CarDatabase.FindFile(Path.Combine(Manager.BnkDir, "frontend", res), "r_school.bnk");
                if (bank != null) break;
            }
            if (bank == null) { Debug.LogWarning("[TDU2] r_school.bnk nicht gefunden"); return; }
            try
            {
                var go = new GameObject("TDU2 Racing School UI");
                go.transform.SetParent(transform, false);
                _film = go.AddComponent<VmfPlayer>();
                _film.GuiDepth = -2;
                _film.MouseEnabled = true;
                _film.Load(bank);
                _film.Visible = false;
                _film.FSCommand += OnFilmCommand;
                _bank = bank;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TDU2] r_school.bnk: " + ex.Message);
                if (_film != null) { Destroy(_film.gameObject); _film = null; }
            }
        }

        int FilmInt(string name, int def)
        {
            if (_film == null) return def;
            var v = _film.GetVariable(name);
            if (string.IsNullOrEmpty(v)) return def;
            return double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : def;
        }

        void OnFilmCommand(string cmd, string args)
        {
            LastFilmCommand = cmd + (string.IsNullOrEmpty(args) ? "" : " " + args);
            string c = (cmd ?? "").Trim().ToLowerInvariant();
            if (c.StartsWith("sndp:")) { TDU2FrontendSounds.Command(c); return; }   // Menü-Sounds (fe_snd.bnk)
            Debug.Log("[TDU2] Rennschule FSCommand: " + LastFilmCommand);
            switch (c)
            {
                case "rsin": PublishBase(); FillLicences(false); return;
                case "rsch:stli": FillLicences(true); return;
                case "rsch:dili":
                    {
                        int li = FilmInt("/:num_licence", _licIndex);
                        if (li != _licIndex || _tests.Count == 0) SelectLicence(li);
                        FillLicenceCard();
                        return;
                    }
                case "rsch:dits":
                    {
                        int li = FilmInt("/:num_licence", _licIndex);
                        if (li != _licIndex) SelectLicence(li);
                        FillTests();
                        return;
                    }
                case "rsch:dtup": FillTestDesc(FilmInt("/:num_test", _testIndex)); return;
                case "rsch:tspr":
                    {
                        int li = FilmInt("/:num_licence", _licIndex);
                        if (li != _licIndex) SelectLicence(li);
                        StartTest(FilmInt("/:num_test", _testIndex));
                        return;
                    }
                case "rsch:exit": Close(); return;
                // "Zurück" vom Lizenzrad: das Spiel wechselt in die Schulansicht (Kamera/Instruktor wählen, natives 3D-Menü).
                // Die gibt es im Nachbau nicht -> Menü schließen, man steht wieder im Interieur (Instruktor erneut ansprechen).
                case "wmon": TDU2DebugLog.Event("Rennschule: wmon (Schulansicht) -> Menü schließen"); Close(); return;
                case "dbxo":
                    _film.SetVariable("/:PlayerAnswer", "a");
                    _film.CallClip("/", "DialogBox");
                    return;
            }
        }

        // ------------------------------------------------------------------ Rennkomponenten

        void EnsureRace()
        {
            if (_race != null) return;
            _raceGo = new GameObject("TDU2 Race");
            _raceGo.transform.SetParent(transform, false);
            _raceGo.SetActive(false);   // Felder setzen, bevor OnEnable (RaceHud) bindet
            _race = _raceGo.AddComponent<TDU2RaceManager>();
            _race.Streamer = Manager != null ? Manager.Streamer : null;
            _race.Vehicles = Manager;
            _hud = _raceGo.AddComponent<RaceHud>();
            _hud.Race = _race;
            _hud.Minimap = Manager != null ? Manager.Minimap : null;
            _results = _raceGo.AddComponent<RaceResults>();
            _results.Manager = Manager;
            _autopilot = _raceGo.AddComponent<RaceAutopilot>();
            _results.Retry += () => { if (_pending != null) BeginTest(_pending); };
            _results.Next += NextTest;
            _results.Quit += ReturnToSchool;
            _race.Completed += OnRaceEnded;
            _race.Failed += OnRaceEnded;
            _race.Aborted += r => ReturnToSchool();
            _raceGo.SetActive(true);
        }

        // ------------------------------------------------------------------ Prüfung starten

        public void StartIndex(int i) => StartTest(i);

        void StartTest(int i)
        {
            if (_tests == null || i < 0 || i >= _tests.Count) return;
            _testIndex = i;
            Close();
            if (Interiors != null && Interiors.Inside)
            {
                var car = Interiors.ExitInterior(null, false);   // eigenes Auto merken (wird nicht gespawnt)
                RememberPlayerCar(car);
            }
            else if (Manager != null && Manager.Current != null) RememberPlayerCar(Manager.Current.Car);
            BeginTest(_tests[i]);
        }

        /// <summary>
        /// Prüfung direkt starten (z.B. aus dem Editor-Fenster "TDU2/Rennen"): ohne Menü, eigenes Auto wird gemerkt.
        /// Gehört die Prüfung zur aktuell gewählten Lizenz, laufen "Nächster Test"/Fortschritt normal weiter.
        /// </summary>
        public bool StartTestDirect(RaceDefinition def)
        {
            if (def == null || Manager == null) return false;
            EnsureData();
            int i = _tests != null ? _tests.FindIndex(t => string.Equals(t.Name, def.Name, StringComparison.OrdinalIgnoreCase)) : -1;
            if (i < 0) { _tests = new List<RaceDefinition> { def }; i = 0; }
            if (_open) Close();
            _testIndex = i;
            if (Interiors != null && Interiors.Inside)
            {
                CaptureSpot();
                var car = Interiors.ExitInterior(null, false);
                RememberPlayerCar(car);
            }
            else if (Manager.Current != null) RememberPlayerCar(Manager.Current.Car);
            BeginTest(_tests[i]);
            return true;
        }

        // ------------------------------------------------------------------ Prüfung = private Sitzung
        TDU2.Traffic.TDU2Traffic _traffic; bool _trafficWas, _private;

        /// <summary>
        /// Während einer Prüfung: kein Verkehr (Rennschul-Prüfungen haben keinen), Mitspieler ausgeblendet und man selbst für
        /// die anderen unsichtbar (TDU2Multiplayer.Hidden). Danach alles wie vorher.
        /// </summary>
        void PrivateSession(bool on)
        {
            if (on == _private) return;
            _private = on;
            TDU2DebugLog.Event("Private Sitzung " + (on ? "an" : "aus"));
            if (_traffic == null)
            {
#if UNITY_2023_1_OR_NEWER
                _traffic = FindFirstObjectByType<TDU2.Traffic.TDU2Traffic>();
#else
                _traffic = FindObjectOfType<TDU2.Traffic.TDU2Traffic>();
#endif
            }
            if (_traffic != null)
            {
                if (on) { _trafficWas = _traffic.EnableTraffic; _traffic.EnableTraffic = false; }
                else _traffic.EnableTraffic = _trafficWas;
            }
            TDU2.Online.TDU2Multiplayer.Hidden = on && TDU2.Online.TDU2OnlineSettings.HidePlayersInRacingSchool;
        }


        bool _loggedPose;

        void BeginTest(RaceDefinition def)
        {
            if (def == null || Manager == null) return;
            _loggedPose = false;
            PrivateSession(true);
            EnsureRace();
            _autopilot.End();
            _race.Cleanup();
            _hud.Minimap = Manager.Minimap;
            _pending = def;
            if (def.RoadPath == null) { try { RaceRoadPath.Compute(def); } catch (Exception ex) { Debug.LogWarning("[TDU2] Straßenroute: " + ex.Message); } }
            RaceRoute.RoadPath = def.RoadPath; RaceRoute.RoadPathSeg = def.RoadPathSeg;
            _pendingCar = TestCar(def);
            if (_pendingCar == null) { Debug.LogWarning("[TDU2] Rennschule: kein Fahrzeug verfügbar"); return; }
            if (def.Weather != SchoolTestWeather.Sun) Debug.Log("[TDU2] Prüfung " + def.Name + ": Wetter im Original " + def.Weather + " (DBChallWeather) – hier nicht nachgebildet");
            RaceProgress.AddTry(def.Name);
            ApplyEase(def);

            // Kamera zum Start, damit der Streamer die Strecke lädt; Auto erst spawnen, wenn der Boden da ist
            Manager.RemoveCar();
            StartPose(def, out var pos, out var rot);
            var cam = Camera.main;
            if (cam != null) cam.transform.SetPositionAndRotation(pos - rot * Vector3.forward * 8f + Vector3.up * 3f, rot);
            _phase = Phase.StreamToStart;
            _phaseT = 0f;
            Debug.Log("[TDU2] Prüfung " + def.Name + " (" + RaceTexts.Get(def.NameHc, def.Title) + "): Auto " + _pendingCar.ModelName +
                      ", " + def.Checkpoints.Count + " Checkpoints, " + def.Decors.Count + " Deko-Objekte, " + def.Opponents.Count + " Fahrzeuge, " +
                      (def.HasTimeLimit ? "Zeitlimit " + RaceHud.FormatRaceTimeMs(def.TimeLimitMs) : "Zielzeit " + RaceHud.FormatRaceTimeMs(def.TargetTimeMs)));
        }

        /// <summary>
        /// Prüfungsauto = ig2 "VMod" = DBCarPhysicsData._id_car. Das sind eigene Sondermodelle (z.B. #626 "CCXR Edition Racing School
        /// Asphalt", _duplicate = 383, _sticker = "rsa_CCXR") mit eigener Physik und Beklebung. Sie teilen sich die Fahrzeugbank mit
        /// dem Serienauto und stehen deshalb nicht in Manager.Available – geladen wird genau dieser Datensatz (SpawnCarExact).
        /// </summary>
        CarPhysics TestCar(RaceDefinition def)
        {
            CarPhysics car = null;
            if (UseTestCar && def.CarId > 0 && Manager.Database != null)
            {
                if (Manager.Database.ById.TryGetValue(def.CarId, out var c))
                {
                    if (CarDatabase.CarBank(Manager.BnkDir, c) != null) car = c;
                    else Debug.LogWarning("[TDU2] Prüfungsauto " + c.FileName + " (#" + def.CarId + ") hat keine Fahrzeugbank (vehicules/high) – nehme eigenes Auto");
                }
                else Debug.LogWarning("[TDU2] Prüfungsauto #" + def.CarId + " steht nicht in DBCarPhysicsData – nehme eigenes Auto");
            }
            if (car == null) car = _playerCar ?? (Manager.Current != null ? Manager.Current.Car : null) ?? Manager.FindCar(Manager.StartCar);
            return car;
        }

        // ------------------------------------------------------------------ mehr Zeit nach Fehlversuchen
        readonly Dictionary<string, (int target, int limit)> _origTimes = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> _failStreak = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Zeitbonus der Prüfung (Anteil, 0 = Original). Ab EaseAfterFails Fehlversuchen hintereinander je Versuch +EaseStep.</summary>
        public float EaseFor(string test)
        {
            if (EaseAfterFails <= 0 || string.IsNullOrEmpty(test) || !_failStreak.TryGetValue(test, out int f) || f < EaseAfterFails) return 0f;
            return Mathf.Min(EaseMax, EaseStep * (f - EaseAfterFails + 1));
        }

        void ApplyEase(RaceDefinition def)
        {
            if (!_origTimes.TryGetValue(def.Name, out var o)) { o = (def.TargetTimeMs, def.TimeLimitMs); _origTimes[def.Name] = o; }
            float k = 1f + EaseFor(def.Name);
            def.TargetTimeMs = o.target > 0 ? Mathf.RoundToInt(o.target * k) : o.target;
            def.TimeLimitMs = o.limit > 0 ? Mathf.RoundToInt(o.limit * k) : o.limit;
            if (k > 1.0001f)
            {
                int fails = _failStreak.TryGetValue(def.Name, out int f) ? f : 0;
                Debug.Log("[TDU2] Prüfung " + def.Name + ": " + fails + " Fehlversuche hintereinander – " + Mathf.RoundToInt((k - 1f) * 100f) + " % mehr Zeit (" +
                          RaceHud.FormatRaceTimeMs(def.HasTimeLimit ? def.TimeLimitMs : def.TargetTimeMs) + " statt " + RaceHud.FormatRaceTimeMs(def.HasTimeLimit ? o.limit : o.target) + ")");
            }
        }

        void NoteAttempt(RaceResult res)
        {
            if (res?.Race == null || res.Aborted) return;
            string n = res.Race.Name;
            if (res.Passed) _failStreak.Remove(n);
            else _failStreak[n] = (_failStreak.TryGetValue(n, out int f) ? f : 0) + 1;
        }

        /// <summary>Prüfungsautos (Rennschul-Sondermodelle) nie als "eigenes Auto" merken (auch Spielstand/Garage nutzen das).</summary>
        public static bool IsSchoolCar(CarPhysics c)
        {
            if (c == null || c.Raw == null) return false;
            var st = c.Raw.S("_sticker", "") ?? "";
            var ver = c.Raw.S("_version_name", "") ?? "";
            return st.StartsWith("rs", StringComparison.OrdinalIgnoreCase) && st.Length > 3 && st[3] == '_' || ver.IndexOf("Racing School", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        void RememberPlayerCar(CarPhysics c)
        {
            if (c != null && !IsSchoolCar(c)) _playerCar = c;
        }

        CarPhysics PlayerCar()
        {
            if (_playerCar != null && !IsSchoolCar(_playerCar)) return _playerCar;
            var last = Interiors != null ? LastInteriorCar() : null;
            if (last != null && !IsSchoolCar(last)) return last;
            return Manager.FindCar(Manager.StartCar);
        }

        CarPhysics LastInteriorCar()
        {
            try
            {
                var f = typeof(TDU2Interiors).GetField("_lastCar", BindingFlags.NonPublic | BindingFlags.Instance);
                return f != null ? f.GetValue(Interiors) as CarPhysics : null;
            }
            catch (Exception) { return null; }
        }

        bool SpawnExact(CarPhysics car, Vector3 pos, Quaternion rot)
        {
            TDU2DebugLog.Event("SpawnExact " + (car != null ? car.FileName + "#" + car.Id : "-") + " loading=" + Manager.IsLoadingCar + " inAvailable=" + (car != null && Manager.Available.Contains(car)));
            if (car == null) return true;
            if (Manager.Available.Contains(car)) { if (Manager.IsLoadingCar) return false; Manager.SpawnCar(car, pos, rot); return true; }
            return Manager.SpawnCarExact(car, pos, rot);
        }

        /// <summary>Startposition/-richtung (Welt): spotworld-Start, Richtung der Startlinie zur ersten Zielmarke hin.</summary>
        void StartPose(RaceDefinition def, out Vector3 pos, out Quaternion rot)
        {
            var root = Manager.Streamer != null ? Manager.Streamer.MapRoot : null;
            Vector3 start = def.StartPosition;
            Vector3 f = def.Start != null ? def.Start.Forward : new Vector3(Mathf.Sin(def.StartHeading), 0f, Mathf.Cos(def.StartHeading));
            var seq = def.TargetSequence();
            // Startrichtung = Startlinie (CHK2 #0, gleich der spotworld-Richtung). Nur wenn sie klar vom ersten Ziel wegzeigt
            // (> 100°, Ziel mindestens 15 m entfernt), umdrehen. Die Straßenroute wird hier bewusst nicht benutzt (kann am Start
            // auf der Gegenspur beginnen).
            Vector3 aim = Vector3.zero;
            foreach (int k in seq) { var d = def.Checkpoints[k].Position - start; d.y = 0f; if (d.sqrMagnitude > 225f) { aim = d; break; } }
            float ang = aim != Vector3.zero ? Vector3.Angle(new Vector3(f.x, 0f, f.z), aim) : 0f;
            if (ang > 100f) f = -f;
            if (!_loggedPose)
            {
                _loggedPose = true;
                TDU2DebugLog.Event($"Startpose {def.Name}: Start {start} Startlinie {(def.Start != null ? def.Start.Forward.ToString() : "-")} spotHeading {def.StartHeading:0.000} ersteZiel {aim} Winkel {ang:0} -> Richtung {f}");
            }
            f.y = 0f; if (f.sqrMagnitude < 1e-6f) f = Vector3.forward;
            pos = root != null ? root.TransformPoint(start) : start;
            var wf = root != null ? root.TransformVector(f.normalized) : f.normalized;   // nicht TransformDirection: die ignoriert die Spiegelung (Skala -1) des MapRoot
            wf.y = 0f; if (wf.sqrMagnitude < 1e-6f) wf = Vector3.forward;
            rot = Quaternion.LookRotation(wf.normalized, Vector3.up);
        }

        void TickPhase()
        {
            if (_phase == Phase.None || Manager == null) return;
            _phaseT += Time.unscaledDeltaTime;
            if (_phase == Phase.StreamToStart)
            {
                StartPose(_pending, out var pos, out var rot);
                if (!GroundReady(pos) && _phaseT < 15f) return;
                pos.y = Manager.GroundHeight(pos) + 0.35f;
                if (!SpawnExact(_pendingCar, pos, rot) && _phaseT < 20f) return;   // anderes Auto lädt noch -> nächster Frame
                _phase = Phase.None;
                Debug.Log("[TDU2] Prüfungsauto: #" + _pendingCar.Id + " " + _pendingCar.FileName + " " + _pendingCar.ModelName +
                          (_pendingCar.Raw != null ? " (" + _pendingCar.Raw.S("_version_name", "") + ", Beklebung " + _pendingCar.Raw.S("_sticker", "-") + ")" : ""));
                bool ok = _race.StartRace(_pending);
                if (!ok) Debug.LogWarning("[TDU2] Prüfung " + _pending.Name + " konnte nicht gestartet werden (" + _pending.Checkpoints.Count + " Checkpoints)");
            }
            else if (_phase == Phase.EnterSchool)
            {
                if (Interiors == null || !Interiors.Inside) { _phase = Phase.None; return; }
                if (!Interiors.SceneReady && _phaseT < 20f) return;
                if (_phaseT < 0.5f) return;
                _phase = Phase.None;
                Debug.Log("[TDU2] Rennschule: zurück in der Akademie (" + _spotCode + "), eigenes Auto beim Verlassen: " + (PlayerCar()?.FileName ?? "-"));
                Open();
            }
            else if (_phase == Phase.StreamToSchool)
            {
                var root = Manager.Streamer != null ? Manager.Streamer.MapRoot : null;
                var p = root != null ? root.TransformPoint(_spotExit) : _spotExit;
                if (!GroundReady(p) && _phaseT < 15f) return;
                _phase = Phase.None;
                var fwd = root != null ? root.TransformDirection(new Vector3(Mathf.Sin(_spotHeading), 0f, Mathf.Cos(_spotHeading))) : Vector3.forward;
                fwd.y = 0f; if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                p.y = Manager.GroundHeight(p) + 0.4f;
                var car = PlayerCar();
                if (car != null && !SpawnExact(car, p, Quaternion.LookRotation(fwd.normalized, Vector3.up)) && _phaseT < 20f) { _phase = Phase.StreamToSchool; return; }
                Debug.Log("[TDU2] Rennschule: eigenes Auto zurück: " + (car != null ? car.FileName + " #" + car.Id : "-"));
                Open();
            }
        }

        static bool GroundReady(Vector3 world) =>
            Physics.Raycast(world + Vector3.up * 50f, Vector3.down, 200f, ~(1 << 2), QueryTriggerInteraction.Ignore);

        // ------------------------------------------------------------------ Ergebnis

        RaceResult _lastResult;
        RaceAutopilot _autopilot;
        int _lastLicensed;

        void OnRaceEnded(TDU2RaceManager r)
        {
            var res = r.Result;
            _lastResult = res;
            NoteAttempt(res);
            _lastLicensed = 0;
            var lic = _licences.Count > 0 ? _licences[_licIndex] : null;
            if (res != null && res.Passed && res.Race != null)
            {
                RaceProgress.MarkTestDone(res.Race.Name);
                RaceProgress.ReportTime(res.Race.Name, res.FinalTimeMs);
            }
            if (lic != null)
            {
                bool wasWon = RaceProgress.IsLicenceWon(lic.Hash);
                if (!wasWon && IsLicenceWon(lic)) { RaceProgress.MarkLicenceWon(lic.Hash); _lastLicensed = 2; _backWin = true; }
                else if (wasWon) _lastLicensed = 1;
            }
            Debug.Log("[TDU2] Prüfung " + (res?.Race?.Name ?? "") + (res != null && res.Passed ? " bestanden" : " nicht bestanden") +
                      ": " + RaceHud.FormatRaceTimeMs(res?.FinalTimeMs ?? 0) + (res != null && res.PenaltyMs > 0 ? " (inkl. " + RaceHud.FormatRaceTimeMs(res.PenaltyMs) + " Strafe)" : "") +
                      (res != null && res.TimedOut ? " – Zeit abgelaufen" : "") + (_lastLicensed == 2 ? " – Lizenz " + lic.DisplayName + " erworben!" : ""));
            // wie im Spiel: nach dem Ziel übernimmt der Autopilot, bremst ab und folgt der Straße
            if (_autopilot != null && Manager != null && Manager.Current != null) _autopilot.Begin(Manager);
            _resultDelay = 1.2f;   // im Original läuft hier die Sieg-/Niederlage-Cinematic (pack_racingschool)
        }

        void ShowResults()
        {
            var res = _lastResult;
            if (res == null) { ReturnToSchool(); return; }
            var lic = _licences.Count > 0 ? _licences[_licIndex] : null;
            string name = res.Race != null ? RaceTexts.Get(res.Race.NameHc, res.Race.Title) : "";
            if (_results == null || !_results.Show(res, lic, DoneCount(), _tests.Count, _lastLicensed, name))
            {
                // ohne results.bnk: direkt zurück zur Schule
                ReturnToSchool();
            }
        }

        void NextTest()
        {
            int next = _testIndex + 1;
            if (_tests == null || next >= _tests.Count) { ReturnToSchool(); return; }
            _testIndex = next;
            BeginTest(_tests[next]);
        }

        /// <summary>"Zurück zur Renn-Akademie": eigenes Auto vor die Schule setzen, Menü wieder öffnen.</summary>
        void ReturnToSchool()
        {
            if (_results != null && _results.IsOpen) _results.Close();
            if (_race != null) _race.Cleanup();
            if (_autopilot != null) _autopilot.End();
            _resultDelay = -1f;
            if (Manager == null) return;
            TDU2DebugLog.Event("ReturnToSchool: spotObj=" + (_spotObj != null ? _spotObj.Code : "-") + " haveSpot=" + _haveSpot + " playerCar=" + (PlayerCar()?.FileName ?? "-") + " current=" + (Manager.Current?.Car?.FileName ?? "-"));
            PrivateSession(false);
            // wie im Spiel: zurück ins Interieur der Akademie, dort öffnet sich das Menü wieder
            if (ReturnIntoInterior && _spotObj != null && Interiors != null && Interiors.EnterSpot(_spotObj, PlayerCar()))
            {
                _phase = Phase.EnterSchool;
                _phaseT = 0f;
                return;
            }
            if (!_haveSpot)
            {
                // Spot unbekannt: am aktuellen Ort weiterfahren
                var car = PlayerCar();
                if (car != null && Manager.Current != null && Manager.Current.Car != car)
                {
                    var t = Manager.Current.transform;
                    SpawnExact(car, t.position + Vector3.up * 0.3f, Quaternion.LookRotation(Vector3.ProjectOnPlane(t.forward, Vector3.up).normalized, Vector3.up));
                }
                return;
            }
            Manager.RemoveCar();
            var root = Manager.Streamer != null ? Manager.Streamer.MapRoot : null;
            var p = root != null ? root.TransformPoint(_spotExit) : _spotExit;
            var cam = Camera.main;
            if (cam != null) cam.transform.position = p + Vector3.up * 6f;
            _phase = Phase.StreamToSchool;
            _phaseT = 0f;
        }
    }
}
