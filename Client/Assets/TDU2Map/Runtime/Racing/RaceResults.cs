using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TDU2.Flash;
using TDU2.Vehicles;
using UnityEngine;

namespace TDU2.Racing
{
    /// <summary>
    /// Ergebnisbildschirm einer Prüfung über den Original-Film frontend/&lt;res&gt;/results.bnk (results.vmf).
    /// Variablen und Label wie GMCompDrivingSchool in der EXE (0xb5c840..0xb5cc40):
    ///   /:IsPenalty /:final_score /:basetime /:IsTimeOut /:ref_score /:IsRacingSchool=1 /:IsTestScore=1 /:TestName
    ///   /:LicenceName /:Licence_group1 /:Licence_group2 /:penalty ("+…") /:Message (Text 1625/1626/1627)
    ///   /:IsLicensed (0 = nein, 1 = schon vorher, 2 = gerade erworben) /:nb_tests_done /:nb_tests_total
    ///   dann GotoLabel("/", "success_test" | "failed_test").
    /// Der Film meldet über FSCommands: "rest" (nochmal), "nets" (nächster Test), "quit" (zurück zur Akademie),
    /// "dbxo" (Dialog -> hier sofort mit "a" = ja beantwortet), "gofr" (freie Fahrt).
    /// </summary>
    public sealed class RaceResults : MonoBehaviour
    {
        public TDU2VehicleManager Manager;
        public event Action Retry, Next, Quit;
        [Tooltip("Spiel während des Ergebnisses anhalten (aus = Auto rollt mit dem Autopiloten weiter, wie im Spiel).")]
        public bool PauseGame = false;

        VmfPlayer _film;
        string _bank;
        bool _open;
        float _savedTimeScale = 1f;
        CursorLockMode _savedLock; bool _savedCursor;

        public bool IsOpen => _open;
        static int _openCount;
        static readonly System.Collections.Generic.List<RaceResults> _all = new System.Collections.Generic.List<RaceResults>();
        /// <summary>Ein Ergebnis-Film ist offen (aus den lebenden Instanzen bestimmt – ein hängender Zähler darf ESC nicht sperren).</summary>
        public static bool AnyOpen
        {
            get
            {
                if (_openCount <= 0) return false;
                int n = 0;
                for (int i = _all.Count - 1; i >= 0; i--) { if (_all[i] == null) { _all.RemoveAt(i); continue; } if (_all[i]._open) n++; }
                if (n != _openCount) _openCount = n;
                return n > 0;
            }
        }
        void OnEnable() { if (!_all.Contains(this)) _all.Add(this); }
        void OnDisable() { if (_open) { _open = false; _openCount = Mathf.Max(0, _openCount - 1); } _all.Remove(this); }

        bool Load()
        {
            if (_film != null) return true;
            if (Manager == null || string.IsNullOrEmpty(Manager.BnkDir)) return false;
            foreach (var res in new[] { "hires", "medres", "lowres" })
            {
                _bank = CarDatabase.FindFile(Path.Combine(Manager.BnkDir, "frontend", res), "results.bnk");
                if (_bank != null) break;
            }
            if (_bank == null) { Debug.LogWarning("[TDU2] results.bnk nicht gefunden"); return false; }
            try
            {
                var go = new GameObject("TDU2 Results UI");
                go.transform.SetParent(transform, false);
                _film = go.AddComponent<VmfPlayer>();
                _film.GuiDepth = -100;   // über HUD, Minimap und Tacho (IMGUI: kleinere Tiefe = weiter vorn)
                _film.MouseEnabled = true;
                _film.Load(_bank);
                _film.Visible = false;
                _film.FSCommand += OnCommand;
                FlashLocalization.Apply(_film, FlashLocalization.Load(Manager.BnkDir, TDU2PauseMenu.SavedLanguage()));
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TDU2] results.bnk: " + ex.Message);
                if (_film != null) { Destroy(_film.gameObject); _film = null; }
                return false;
            }
        }

        /// <summary>Ergebnis anzeigen. licensed: 0 nein, 1 schon vorher, 2 gerade erworben.</summary>
        public bool Show(RaceResult r, LicenseInfo lic, int testsDone, int testsTotal, int licensed, string testName)
        {
            if (r == null) return false;
            // Film bei jedem Ergebnis frisch laden: im Spiel wird results.vmf pro Ergebnis neu geladen, und ein offen
            // gebliebenes menu_rs (InRootMenu, Input=false nach "valid") würde sonst die nächste Anzeige blockieren.
            if (_film != null) { _film.FSCommand -= OnCommand; Destroy(_film.gameObject); _film = null; }
            if (!Load()) return false;
            var f = _film;
            f.SetValue("/:WhatPlatform", 1);
            f.SetVariable("/:WhatChaType", "solo");
            f.SetValue("/:IsPenalty", r.PenaltyMs > 0);
            f.SetVariable("/:final_score", RaceHud.FormatRaceTimeMs(r.FinalTimeMs));
            f.SetVariable("/:basetime", RaceHud.FormatRaceTimeMs(r.RawTimeMs));
            f.SetValue("/:IsTimeOut", r.TimedOut);
            f.SetVariable("/:ref_score", r.TargetMs > 0 ? RaceHud.FormatRaceTimeMs(r.TargetMs) : "--'--\"--");
            f.SetValue("/:IsRacingSchool", true);
            f.SetValue("/:IsTestScore", 1);
            f.SetVariable("/:TestName", testName ?? "");
            f.SetVariable("/:LicenceName", lic != null ? lic.DisplayName : "");
            f.SetVariable("/:Licence_group1", lic != null ? lic.Group1 : "empty");
            f.SetVariable("/:Licence_group2", lic != null ? lic.Group2 : "empty");
            if (r.PenaltyMs > 0) f.SetVariable("/:penalty", "+" + RaceHud.FormatRaceTimeMs(r.PenaltyMs));
            ulong msg = !r.Passed ? RaceTexts.MsgTestFailed : licensed == 2 ? RaceTexts.MsgLicenceWon : RaceTexts.MsgTestPassed;
            f.SetVariable("/:Message", RaceTexts.Get(msg, r.Passed ? "Bestanden" : "Nicht bestanden"));
            _champ = false;
            f.SetValue("/:IsLicensed", licensed);
            f.SetVariable("/:nb_tests_done", testsDone.ToString(CultureInfo.InvariantCulture));
            f.SetVariable("/:nb_tests_total", testsTotal.ToString(CultureInfo.InvariantCulture));
            f.SetValue("/:IsReward", false);
            f.SetValue("/:LockReplay", true);    // Replays gibt es im Nachbau nicht
            f.SetValue("/:Input", true);
            f.SetValue("/:fake", 0);
            f.SetValue("/:InRootMenu", false);
            f.SetValue("/:InMiniMenu", false);
            f.SetVariable("/:BtValid", "");
            _lastBtValid = ""; _btValidAt = -1f;
            HideHud(true);

            if (!_open) _openCount++;
            _open = true;
            _savedTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
            if (PauseGame) Time.timeScale = 0f;
            _savedLock = Cursor.lockState; _savedCursor = Cursor.visible;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            f.Visible = true;
            f.GotoLabel("/", r.Passed ? "success_test" : "failed_test", true);
            return true;
        }

        // ------------------------------------------------------------------ Meisterschaftsrennen (results.vmf, WhatChaType "solo", IsCup)
        /// <summary>Zeile einer Ergebnisliste (arrays/results bzw. arrays/contest: line1..8/content: pos, player_name, player_car_name, score).</summary>
        public sealed class Row { public int Place; public string Name, Car, Score; public bool Player; }

        bool _champ;
        float _stuckSince = -1f; string _stuckLabel; int _stuckFrame = -1;

        /// <summary>
        /// Ergebnis eines Meisterschaftsrennens wie im Spiel: "results" -> Ergebniskarte ("score": Platz, Zeit, Medaille, Preisgeld)
        /// -> A -> Rennergebnis (challenge_rank, arrays/results, FSCommand rcha) -> A -> Meisterschaftswertung (contest_rank,
        /// arrays/contest, rcon) -> A -> Menü (end_menu: Weiter / Wiederholen / Beenden).
        /// </summary>
        public bool ShowChampionship(string raceTitle, string cupName, string whatcha, string cupIcon, int place, string finalScore,
                                     long reward, List<Row> race, List<Row> cup)
        {
            if (_film != null) { _film.FSCommand -= OnCommand; Destroy(_film.gameObject); _film = null; }
            if (!Load()) return false;
            var f = _film;
            _champ = true;
            f.SetValue("/:WhatPlatform", 1);
            f.SetVariable("/:MenuType", "pc");
            f.SetValue("/:fake", 0);
            f.SetVariable("/:WhatChaType", "solo");
            f.SetValue("/:IsCup", true);
            f.SetValue("/:IsCupReward", reward > 0);
            f.SetValue("/:IsCupSCR", false);
            f.SetValue("/:IsORC", false);
            f.SetValue("/:ORCTraining", false);
            f.SetValue("/:InterClub", false);
            f.SetValue("/:IsTraining", false);
            f.SetValue("/:IsRacingSchool", false);
            f.SetValue("/:IsRaceOver", 1);
            f.SetVariable("/:whatcha", whatcha ?? "timeAttack");
            f.SetVariable("/:ChallengeName", raceTitle ?? "");
            f.SetVariable("/:ContestName", cupName ?? "");
            f.SetVariable("/:Championship_icon", cupIcon ?? "asphalt");
            f.SetValue("/:Championship_num", 1);
            f.SetValue("/:PlayerRanking", place);
            f.SetVariable("/:Exposant", "");
            f.SetValue("/:WhatCup", place >= 1 && place <= 3 ? place : 0);
            f.SetVariable("/:final_score", finalScore ?? "");
            f.SetVariable("/:BaseScore", finalScore ?? "");
            f.SetVariable("/:Reward", reward > 0 ? reward.ToString("N0", CultureInfo.GetCultureInfo("de-DE")) + " $" : "0 $");
            f.SetValue("/:IsPenalty", false);
            f.SetValue("/:GME_RefTime", false);
            f.SetValue("/:LockReplay", true);    // keine Replays im Nachbau
            f.SetValue("/:IsRankings", 0);       // keine Online-Ranglisten
            f.SetValue("/:LockRankings", 1);
            f.SetValue("/:IsRetry", 0);          // 0 = Wiederholen nicht gesperrt
            f.SetValue("/:LooseCup", false);
            f.SetValue("/:CanReturnToLobby", false);
            f.SetValue("/:Input", true);
            f.SetValue("/:InRootMenu", false);
            f.SetValue("/:InMiniMenu", false);
            f.SetVariable("/:BtValid", "");
            FillLines("/arrays/results", race);
            FillLines("/arrays/contest", cup);
            _race = race; _cup = cup;
            _lastBtValid = ""; _btValidAt = -1f; _stuckSince = -1f;
            HideHud(true);
            if (!_open) _openCount++;
            _open = true;
            _savedTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
            if (PauseGame) Time.timeScale = 0f;
            _savedLock = Cursor.lockState; _savedCursor = Cursor.visible;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            f.Visible = true;
            f.GotoLabel("/", "results", true);
            TDU2DebugLog.Event("Ergebnis Meisterschaft (results.vmf): Platz " + place + ", " + (race?.Count ?? 0) + " Fahrer");
            return true;
        }

        List<Row> _race, _cup;

        void FillLines(string array, List<Row> rows)
        {
            for (int i = 1; i <= 8; i++)
            {
                string c = array + "/line" + i + "/content";
                var r = rows != null && i - 1 < rows.Count ? rows[i - 1] : null;
                _film.SetVariable(c + ":pos", r != null ? r.Place.ToString(CultureInfo.InvariantCulture) : "");
                _film.SetVariable(c + ":player_name", r?.Name ?? "");
                _film.SetVariable(c + ":player_car_name", r?.Car ?? "");
                _film.SetVariable(c + ":score", r?.Score ?? "");
                _film.SetValue(c + ":isplayer", r != null && r.Player ? 1 : 0);
            }
        }

        /// <summary>
        /// Rückfall für die Animationsketten (Ende von score/arrays ruft _root.gotoAndPlay(...)): bleibt der Film nach einer
        /// Eingabe an derselben Stelle stehen (Input aus), selbst zum nächsten Schritt.
        /// </summary>
        void WatchChampionship()
        {
            var root = _film.Root;
            if (root == null) return;
            string lab = _film.Label("/") ?? "";
            bool input = Truthy(_film.GetVariable("/:Input"));
            if (lab != _stuckLabel || root.Frame != _stuckFrame || input && lab != "results" && lab != "loop")
            {
                _stuckLabel = lab; _stuckFrame = root.Frame; _stuckSince = Time.unscaledTime;
                return;
            }
            if (Time.unscaledTime - _stuckSince < (lab == "results" || lab == "loop" ? 1.5f : 3f)) return;
            _stuckSince = Time.unscaledTime;
            string next = null;
            switch (lab)
            {
                case "results": case "loop": next = "score"; break;
                case "score": next = "challenge_rank"; break;
                case "challenge_rank": next = "contest_rank"; break;
                case "contest_rank": case "champ_reward": next = "end_menu"; break;
            }
            if (next == null) return;
            Debug.Log("[TDU2] Ergebnis: Film hängt in '" + lab + "' – weiter mit '" + next + "'");
            _film.SetValue("/:Input", true);
            _film.GotoLabel("/", next, true);
        }

        public void Close()
        {
            if (!_open) return;
            _open = false;
            _openCount = Mathf.Max(0, _openCount - 1);
            RacingSchoolMenu.NoteUiClosed();
            if (PauseGame) Time.timeScale = _savedTimeScale;
            Cursor.lockState = _savedLock; Cursor.visible = _savedCursor;
            if (_film != null) _film.Visible = false;
            HideHud(false);
        }

        // Fahr-HUD (Minimap/Tacho, Flash) während des Ergebnisses ausblenden: HUD-Film unsichtbar und die Minimap-Komponente
        // ganz aus (sie zeichnet ihr Kartenbild selbst und liegt sonst über dem Ergebnis)
        VmfPlayer _hudHidden; bool _hudWasVisible;
        Behaviour _mapHidden; bool _mapWasEnabled;
        void HideHud(bool hide)
        {
            var mm = Manager != null ? Manager.Minimap : null;
            if (hide)
            {
                var h = mm != null ? mm.HudPlayer : null;
                if (h != null && h != _film) { _hudHidden = h; _hudWasVisible = h.Visible; h.Visible = false; }
                if (mm != null) { _mapHidden = mm; _mapWasEnabled = mm.enabled; mm.enabled = false; }
            }
            else
            {
                if (_hudHidden != null) { _hudHidden.Visible = _hudWasVisible; _hudHidden = null; }
                if (_mapHidden != null) { _mapHidden.enabled = _mapWasEnabled; _mapHidden = null; }
            }
            TDU2DebugLog.Event("Ergebnis-HUD " + (hide ? "aus" : "an") + ": Minimap " + (mm != null ? "gefunden" : "fehlt"));
        }

        // Rückfall: Knopf gewählt (_root.BtValid), aber der Film meldet kein FSCommand (Animationskette menu_rs -> close_loop -> ValidMenu)
        string _lastBtValid = ""; float _btValidAt = -1f;
        const float ValidTimeout = 1.5f;
        void WatchValidation()
        {
            string v = (_film.GetVariable("/:BtValid") ?? "").Trim().ToLowerInvariant();
            if (v != _lastBtValid) { _lastBtValid = v; _btValidAt = v.Length > 0 ? Time.unscaledTime : -1f; return; }
            if (_btValidAt < 0f || Time.unscaledTime - _btValidAt < ValidTimeout) return;
            _btValidAt = -1f;
            Debug.Log("[TDU2] Ergebnis: Knopf " + v + " ohne FSCommand – führe selbst aus");
            switch (v)
            {
                case "retrytest": case "retry": Fire(Retry); break;
                case "nexttest": case "next": Fire(Next); break;
                case "rankings": case "replay": if (_champ) Fire(Next); break;
                case "quittest": case "quit": Fire(Quit); break;
            }
        }

        void Update()
        {
            if (!_open || _film == null) return;
            WatchValidation();
            if (!_open) return;
            if (_champ) WatchChampionship();
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) Send("Move", "up");
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) Send("Move", "down");
            if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) Send("Move", "left");
            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) Send("Move", "right");
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space)) Send("A");
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace)) { if (!Send("B")) Fire(Quit); }
            if (Input.GetKeyDown(KeyCode.F10)) Fire(Quit);   // Notausgang
#endif
        }

        /// <summary>
        /// Eingabe so verteilen wie die EXE (vgl. Hilfeleisten-Button 7343 in results.vmf):
        /// _root.InRootMenu → _root.RootMenuPath (Clip "menu_rs"), _root.InMiniMenu → _root.MiniMenuPath, sonst _root.Clip.
        /// Ohne diese Weiche landete "A" immer wieder in _root.A() (Ergebnis → menu_rs neu öffnen) und die Knöpfe
        /// Wiederholen/Nächster Test/Beenden reagierten nie.
        /// </summary>
        bool Send(string fn, params object[] args)
        {
            if (Truthy(_film.GetVariable("/:InRootMenu")))
            {
                foreach (var path in RootMenuPaths)
                    if (_film.Exists(path) && _film.CallClip(path, fn, args)) return true;
            }
            if (Truthy(_film.GetVariable("/:InMiniMenu")) && _film.Exists("/mini_menu") && _film.CallClip("/mini_menu", fn, args)) return true;
            return _film.Input(fn, args);
        }

        static readonly string[] RootMenuPaths = { "/menu_rs", "/menu" };

        static bool Truthy(string v) => v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

        void OnCommand(string cmd, string args)
        {
            string c = (cmd ?? "").Trim().ToLowerInvariant();
            if (c.StartsWith("sndp:")) { TDU2FrontendSounds.Command(c); return; }
            Debug.Log("[TDU2] Ergebnis FSCommand: " + cmd);
            switch (c)
            {
                case "rest": Fire(Retry); break;
                case "nets": Fire(Next); break;
                case "quit":
                case "gofr":
                    {
                        // Meisterschaft: "Weiter" (BtValid next) = zurück in den Wettbewerb, "Wiederholen", sonst freie Fahrt
                        string bt = (_film.GetVariable("/:BtValid") ?? "").Trim().ToLowerInvariant();
                        if (_champ && bt == "next") Fire(Next);
                        else if (_champ && bt.StartsWith("retry")) Fire(Retry);
                        else Fire(Quit);
                        break;
                    }
                case "rcha": if (_champ) FillLines("/arrays/results", _race); break;     // Spiel liefert die Rennliste
                case "rcon": if (_champ) FillLines("/arrays/contest", _cup); break;      // ... und die Meisterschaftswertung
                case "dbxo":
                    // Bestätigungsdialog (z.B. "Beenden?"): wie das Spiel mit "ja" beantworten
                    _film.SetVariable("/:PlayerAnswer", "a");
                    _film.CallClip("/", "DialogBox");
                    break;
            }
        }

        void Fire(Action a) { Close(); a?.Invoke(); }

        void OnDestroy() { if (_film != null) Destroy(_film.gameObject); }
    }
}
