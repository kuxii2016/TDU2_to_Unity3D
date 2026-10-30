using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TDU2.Flash;
using TDU2.Formats;
using TDU2.Online;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Anmeldung wie im Spiel, mit der Original-Oberfläche:
    ///   1. frontend/&lt;res&gt;/log_in2.bnk: "Press Start" (Label press_start, Taste → Label next).
    ///   2. main.bnk /menus/profile (Profilliste, Label profiles_in von /menus): FSCommands popn (Liste füllen), psel (Profil gewählt),
    ///      X = neues Profil → /menus/profile/new_profile (Popup mit /:nickname, /:password; "tfoc" + /:TextToFocus = Eingabefeld).
    ///      Profile = Konten auf dem TDU2Server; auf dem PC wird je Profil nur ein Merk-Token gespeichert.
    ///   3. Spielstand vom Server: Auto + Position laden – oder (neues Konto) beim Gebrauchtwagenhändler Ibiza (ESTOL_B) starten
    ///      und eines der Startautos (Server-config.json "starterCars") wählen.
    ///   Danach Autosave (Position, Auto, eigene Autos, Kilometerstände) und Online-/Spielerzahl in der Minimap.
    /// Tasten im Profilmenü: Enter wählen, Pfeile hoch/runter, X neues Profil, Entf Profil vom PC entfernen, Esc beenden.
    /// Im Popup: Tippen, Tab/Pfeile Feld wechseln, Enter bestätigen, Esc abbrechen.
    /// </summary>
    public sealed class TDU2Login : MonoBehaviour
    {
        public TDU2VehicleManager Manager;
        // Server-Adresse: TDU2GameData (Inspector "Server Host"/"Tcp Port" oder Startparameter -server)
        float AutosaveSeconds => TDU2GameData.Instance != null ? TDU2GameData.Instance.AutosaveSeconds : 30f;   // Online Manager
        [Tooltip("FSCommands des Login/Profilmenüs in die Konsole schreiben")]
        public bool LogCommands = false;
        [Tooltip("Neue Konten wie im Spiel nach GamePC.cpr starten: Insel (AddLevel), im Standard-Haus (DefaultHouse), " +
                 "Auto DefaultCarConfig + Garage DefaultGarageConfig, Geld DefaultMoney. Aus = Startauto beim Händler wählen (Server starterCars).")]
        public bool GamePcStart = true;
        [Tooltip("Nach der Profilwahl das Hauptmenü des Originals zeigen (main.vmf /menus main_in, WhatGameMode 0): " +
                 "Spiel fortsetzen / Neues Spiel / Profile / Spiel beenden. Aus = direkt weiterspielen.")]
        public bool ShowMainMenu = true;
        [Tooltip("Hauptmenü wirklich nach der Profilwahl öffnen (hing im Ladebildschirm) – aus = direkt weiterspielen")]
        public bool MainMenuAfterLogin = false;

        /// <summary>true, solange Anmeldung/Start läuft – Pausemenü, Karte und Fahrtasten sind dann gesperrt.</summary>
        public static bool Active { get; private set; }

        enum St { Init, PressStart, PressStartOut, Profiles, NewProfile, Busy, Closing, MainMenu, Spawning, Playing, Story }
        St _st = St.Init;

        TDU2PauseMenu _pause;
        VmfPlayer _intro;
        GameObject _introGo;
        bool _introTried, _introTexts, _subscribed;
        float _t;

        List<TDU2Session.LocalProfile> _profiles = new List<TDU2Session.LocalProfile>();
        string _last = "";
        Task<NetResponse> _task;
        Action<NetResponse> _then;
        St _afterBusy;

        FlyCamera _fly;
        bool _flyWasEnabled;

        VmfPlayer Ui => _pause != null ? _pause.Ui : null;
        bool German => TDU2PauseMenu.SavedLanguage() == "ge";

        const string ProfilePath = "/menus/profile";
        const string NewProfilePath = "/menus/profile/new_profile";

        void Start()
        {
            if (Manager == null) Manager = GetComponent<TDU2VehicleManager>();
            var gd = TDU2GameData.Ensure();
            if (Manager == null || !Manager.EnablePauseMenu || !gd.UseLogin) { enabled = false; return; }   // ohne main.bnk keine Profilmenüs / Login aus
            Active = true;
            TDU2Session.Connect(gd.ServerHost, gd.TcpPort);
            // Inselwechsel (Flug): Sitzung läuft weiter -> ohne Intro/Profilmenü direkt fortsetzen
            _resume = TDU2IslandTravel.Pending && TDU2Session.LoggedIn && TDU2Session.Config != null;
        }

        bool _resume;

        void OnDisable()
        {
            if (_st != St.Playing) Active = false;
            TDU2TitleScene.End();
            Unsubscribe();
            DestroyIntro();
        }

        // ------------------------------------------------------------------ Ablauf
        void Update()
        {
            if (Manager == null) return;
            if (_pause == null) _pause = Manager.GetComponent<TDU2PauseMenu>();
            if (_st != St.Playing)
            {
                Manager.Suspended = true;
                HoldCamera();
            }
            PollTask();
            UpdateTitleScene();
            switch (_st)
            {
                case St.Init when _resume:
                    if (Ready) { _resume = false; StartGame(TDU2Session.Config); TDU2IslandTravel.Done(); }
                    break;
                case St.Init:
                    EnsureIntro();
                    TDU2FrontendMusic.Play(Manager.BnkDir, "LOGIN");   // sounds.ini: SetMusic "LOGIN" "login.wav"
                    if (_intro != null) { _st = St.PressStart; Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
                    else if (_introTried && Ready) OpenProfiles();
                    break;
                case St.PressStart:
                    ApplyIntroTexts();
#if ENABLE_LEGACY_INPUT_MANAGER
                    if (Ready && (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space) || Input.GetMouseButtonDown(0)))
                    {
                        _intro.GotoLabel("/", "next", true);
                        _st = St.PressStartOut; _t = Time.unscaledTime;
                    }
#endif
                    break;
                case St.PressStartOut:
                    if (Time.unscaledTime - _t > 2.4f || (_intro?.Root != null && _intro.Root.Frame >= 120)) { DestroyIntro(); OpenProfiles(); }
                    break;
                case St.Profiles:
                    UpdateProfiles();
                    break;
                case St.NewProfile:
                    UpdateNewProfile();
                    break;
                case St.Closing:
                    UpdateClosing();
                    break;
                case St.MainMenu:
                    UpdateMainMenu();
                    break;
                case St.Spawning:
                    UpdateSpawning();
                    break;
                case St.Playing:
                    UpdatePlaying();
                    break;
            }
        }

        // ------------------------------------------------------------------ 3D-Hintergrund (Press-Start-Kino der Insel)
        bool _titleTried;

        /// <summary>Wie im Original (GMLogin): hinter Press Start und den Profilmenüs läuft eines der Press-Start-Kinos.</summary>
        void UpdateTitleScene()
        {
            bool front = _st == St.PressStart || _st == St.PressStartOut || _st == St.Profiles || _st == St.NewProfile || _st == St.Busy
                         || _st == St.Closing || _st == St.MainMenu;
            if (!front || _resume) return;
            if (!_titleTried && Manager.Database != null && Manager.Streamer != null && Manager.Streamer.MapRoot != null && Manager.Streamer.Index != null)
            {
                _titleTried = true;
                TDU2TitleScene.Begin(Manager);
            }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (_st == St.PressStart && Input.GetKeyDown(KeyCode.R)) TDU2TitleScene.Next();   // OpenTDU2: nächstes Kino
#endif
        }

        bool Ready => Manager.Database != null && Manager.Available.Count > 0 && _pause != null && _pause.Ui != null && _pause.Texts != null
                      && Manager.Streamer != null && Manager.Streamer.MapRoot != null && Manager.Streamer.Spots != null;

        /// <summary>Freiflug-Kamera während des Logins anhalten (WASD würde beim Tippen fliegen).</summary>
        void HoldCamera()
        {
            if (_fly == null && Camera.main != null)
            {
                _fly = Camera.main.GetComponent<FlyCamera>();
                if (_fly != null) _flyWasEnabled = _fly.enabled;
            }
            if (_fly != null && _fly.enabled) _fly.enabled = false;
        }

        void PollTask()
        {
            if (_task == null || !_task.IsCompleted) return;
            var t = _task; var then = _then; _task = null; _then = null;
            NetResponse r = t.Status == TaskStatus.RanToCompletion && t.Result != null ? t.Result : NetResponse.Error("net:" + t.Exception?.GetBaseException().Message);
            TDU2Diag.Log("LOGIN", $"Server-Antwort nach {Time.unscaledTime - _sentAt:0.0} s: ok={r.ok} Fehler={r.error ?? "-"} nick={r.nick ?? "-"}");
            if (_st == St.Busy) _st = _afterBusy;
            try { then?.Invoke(r); }
            catch (Exception ex) 
            { 
                Debug.LogError("[TDU2] Login: " + ex); 
            }
        }

        void Request(NetRequest req, Action<NetResponse> then)
        {
            var gd = TDU2GameData.Ensure();
            TDU2Session.Connect(gd.ServerHost, gd.TcpPort);   // Adresse evtl. im Inspector geändert
            _afterBusy = _st;
            _st = St.Busy;
            _then = then;
            _sentAt = Time.unscaledTime;
            TDU2Diag.Log("LOGIN", $"Anfrage {req.op} für {req.nick} an {gd.ServerHost}:{gd.TcpPort}");
            _task = TDU2Session.Client.Send(req);
        }

        // ------------------------------------------------------------------ 1. log_in2.bnk (Press Start)
        void EnsureIntro()
        {
            if (_introTried || string.IsNullOrEmpty(Manager.BnkDir)) return;
            _introTried = true;
            string bank = null;
            foreach (var res in new[] { "hires", "medres", "lowres" })
            {
                bank = CarDatabase.FindFile(Path.Combine(Manager.BnkDir, "frontend", res), "log_in2.bnk");
                if (bank != null) break;
            }
            if (bank == null) { Debug.LogWarning("[TDU2] Login: frontend/<res>/log_in2.bnk nicht gefunden – direkt zur Profilauswahl"); return; }
            try
            {
                _introGo = new GameObject("TDU2 Login (log_in2.bnk)");
                _intro = _introGo.AddComponent<VmfPlayer>();
                _intro.GuiDepth = -20;
                _intro.SetValue("/:IsCasinoStandAlone", false);
                _intro.SetValue("/:NAT_msg", false);
                _intro.Load(bank);
                _intro.FSCommand += (c, a) => { if (LogCommands) Debug.Log("[TDU2] log_in2 FSCommand " + c + " " + a); TDU2FrontendSounds.Command(c); };
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TDU2] Login: log_in2.bnk nicht nutzbar: " + ex.Message);
                DestroyIntro();
            }
        }

        void ApplyIntroTexts()
        {
            if (_introTexts || _intro == null || _pause?.Texts == null) return;
            _introTexts = true;
            FlashLocalization.Apply(_intro, _pause.Texts);
        }

        void DestroyIntro()
        {
            if (_introGo != null) UnityUtil.SafeDestroy(_introGo);
            _introGo = null; _intro = null;
        }

        // ------------------------------------------------------------------ 2. Profilliste (main.bnk /menus/profile)
        string _avertText;

        void OpenProfiles()
        {
            var ui = Ui;
            if (ui == null) return;
            if (!_subscribed) { ui.FSCommand += OnCommand; ui.HintClicked += OnHintClicked; ui.TextFieldClicked += OnFieldClicked; _subscribed = true; }
            _profiles = TDU2Session.LoadProfiles(out _last);
            if (_avertText == null) _avertText = ui.GetVariable("/:m_AvertAtariAccount_") ?? "";
            ///menus/profile/profile1/checkbox
            // Zustand wie beim Spielstart (Frontend: WhatGameMode 0, PC)
            ui.SetValue("/:WhatGameMode", 0);
            ui.SetValue("/:WhatPlatform", 1);
            ui.SetVariable("/:MenuType", "pc");
            ui.SetValue("/:IsPC_X360", true);
            ui.SetValue("/:Input", true);
            ui.SetValue("/:PauseBack", false);
            ui.SetValue("/:EditProfile", false);
            ui.SetValue("/:EditPassOnly", false);
            ui.SetValue("/:ForceSelection", 1);   // ohne Profil kein Spiel: B/Esc = Beenden
            ui.SetValue("/:WhatMouseButton", 0);  // Profilzeile anklicken = wählen (sonst prüft der Film "löschen")
            ui.SetValue("/:NoProfile", _profiles.Count == 0 ? 1 : 0);
            ui.SetVariable("/:CurrentProfile", _last ?? "");

            ui.Visible = true;
            ui.GotoLabel("/", "root_stop", false);
            ui.SetVisible("/menus", true);
            ui.SetVisible("/help", true);
            ui.GotoLabel("/menus", "profiles_in", true);
            _st = St.Profiles;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }

        void Unsubscribe()
        {
            var ui = Ui;
            if (_subscribed && ui != null)
            {
                ui.FSCommand -= OnCommand; ui.HintClicked -= OnHintClicked; ui.TextFieldClicked -= OnFieldClicked;
                ui.ReleaseForced(ProfilePath);
            }
            _subscribed = false;
        }

        /// <summary>Mausklick auf die Tasten-Symbole (OK = a_, Abbrechen/Beenden = b_, Neues Profil = x_).</summary>
        void OnHintClicked(string key)
        {
            if (!Active) return;
            if (_st == St.NewProfile)
            {
                if (key == "A") SubmitNewProfile();
                else if (key == "B") CancelNewProfile();
                return;
            }
            if (_st == St.Profiles || _st == St.MainMenu) Ui.Input(key);
        }

        /// <summary>Mausklick auf ein Eingabefeld des Popups: Fokus dorthin.</summary>
        void OnFieldClicked(string variable)
        {
            if (_st != St.NewProfile) return;
            int i = _fields.FindIndex(f => string.Equals(f, variable, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) { _focus = i; RenderFields(); }
        }

        void OnCommand(string cmd, string args)
        {
            if (!Active) return;
            string c = (cmd ?? "").Trim().ToLowerInvariant();
            if (LogCommands) Debug.Log("[TDU2] Login FSCommand " + cmd + " " + args);
            TDU2Diag.Log("LOGIN", $"FSCommand {cmd} {args} (Zustand {_st}, Zeile {CurLineSafe()}, Profile {_profiles.Count})");
            if (TDU2FrontendSounds.Command(c)) return;   // Menü-Sounds (fe_snd.bnk: move/valid/back …)
            switch (c)
            {
                case "popn": FillProfiles(); break;
                case "psel": OnProfileSelected(); break;
                case "tfoc": FocusField(Ui.GetVariable("/:TextToFocus")); break;
                case "quit": QuitGame(); break;
                // Hauptmenü (WhatGameMode 0): "Spiel fortsetzen" -> reco, "Neues Spiel" -> ngam (auch Y in der Profilliste),
                // "Profile" -> prof (Film geht selbst zu profiles_in), "Spiel beenden" -> exit:open + Bestätigung confirm_exit_game
                case "reco": if (_st == St.MainMenu) { Debug.Log("[TDU2] Hauptmenü: Spiel fortsetzen"); FinishLogin(false); } break;
                case "ngam":
                    if (_st == St.MainMenu) FinishLogin(true);
                    else if (_st == St.Profiles && _profiles.Count > 0) { _newGame = true; OnProfileSelected(); }
                    break;
                case "prof": if (_st == St.MainMenu) _st = St.Profiles; break;
                case "exit:open":
                case "egam":
                case "exit:vali": if (_st == St.MainMenu) QuitGame(); break;
            }
        }

        void FillProfiles()
        {
            var ui = Ui;
            int n = Mathf.Min(8, _profiles.Count);
            int cur = _profiles.FindIndex(p => string.Equals(p.nick, _last, StringComparison.OrdinalIgnoreCase)) + 1;
            if (cur < 1 || cur > n) cur = 1;
            ui.SetValue(ProfilePath + ":nb_profiles", n);
            ui.SetValue(ProfilePath + ":cur_profile", cur);
            for (int i = 1; i <= 8; i++)
            {
                string clip = $"{ProfilePath}/profile{i}";
                string name = i <= n ? _profiles[i - 1].nick : "";
                ui.SetVariable(clip + ":dummy", name);
                ui.SetVariable(clip + ":name", name);
                // Häkchen = Online-Profil (Konto auf dem Server): der Film liest connected_profileN in Bild 1 von /menus/profile
                // und spielt dann checkbox "on" (Bild 10) bzw. "off" (Bild 1) – deshalb nur den Wert setzen.
                ui.SetNumber($"{ProfilePath}:connected_profile{i}", i <= n ? 1 : 0);
                // leere Zeilen ausblenden (der Film setzt _visible bei jedem Öffnen wieder auf true -> festhalten)
                ui.ForceVisible(clip, i <= n ? (bool?)null : false);
                ui.ForceVisible($"{ProfilePath}/barre/sur{i}", i <= n ? (bool?)null : false);
            }
            ui.SetValue("/:NoProfile", n == 0 ? 1 : 0);
            ui.SetVariable("/:CurrentProfile", n > 0 ? _profiles[cur - 1].nick : "");
            if (n == 0) _openNewNext = true;   // noch kein Profil: gleich "Neues Profil" (wie im Spiel beim ersten Start)
        }

        bool _openNewNext;

        int CurLine()
        {
            var s = Ui.GetVariable(ProfilePath + ":cur_line");
            return int.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out int v) ? v : (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? (int)d : 1);
        }

        void UpdateProfiles()
        {
            var ui = Ui;
            if (_openNewNext) { _openNewNext = false; OpenNewProfile(false, ""); return; }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) MoveProfile(-1);
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) MoveProfile(1);
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space)) ui.Input("A");
            if (Input.GetKeyDown(KeyCode.X) || Input.GetKeyDown(KeyCode.Insert) || Input.GetKeyDown(KeyCode.N)) ui.Input("X");
            if (Input.GetKeyDown(KeyCode.Y)) ui.Input("Y");   // Original: Y = Neues Spiel (FSCommand ngam) für das gewählte Profil
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace)) ui.Input("B");
            if (Input.GetKeyDown(KeyCode.Delete)) RemoveProfile(CurLine() - 1);
#endif
        }

        void MoveProfile(int delta)
        {
            var ui = Ui;
            int n = Mathf.Min(8, _profiles.Count);
            if (n == 0 || !IsTrue(ui.GetVariable("/:Input"))) return;
            int cur = Mathf.Clamp(CurLine(), 1, n), nw = Mathf.Clamp(cur + delta, 1, n);
            if (nw == cur) return;
            ui.GotoLabel($"{ProfilePath}/barre/sur{cur}", "out", true);
            ui.GotoLabel($"{ProfilePath}/barre/sur{nw}", "change", true);
            ui.GotoLabel($"{ProfilePath}/profile{cur}", "normal", false);
            ui.GotoLabel($"{ProfilePath}/profile{nw}", "selected", false);
            ui.SetValue(ProfilePath + ":cur_line", nw);
            ui.SetVariable("/:CurrentProfile", _profiles[nw - 1].nick);
        }

        static bool IsTrue(string v) => v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);

        void RemoveProfile(int index)
        {
            if (index < 0 || index >= _profiles.Count) return;
            var p = _profiles[index];
            _profiles.RemoveAt(index);
            TDU2Session.StoreProfiles(_profiles, _last);
            var client = TDU2Session.Client;
            System.Threading.Tasks.Task.Run(() => client.SendBlocking(new NetRequest { op = "forget", nick = p.nick, token = p.token }));
            Ui.GotoLabel(ProfilePath, "open", true);   // Liste neu aufbauen (popn)
        }

        void OnProfileSelected()
        {
            int i = CurLine() - 1;
            TDU2Diag.Log("LOGIN", $"Profil gewählt: Zeile {i + 1} von {_profiles.Count} (Zustand {_st})");
            if (i < 0 || i >= _profiles.Count) { if (_profiles.Count > 0) OpenProfiles(); return; }   // keine gültige Zeile: Liste neu öffnen statt hängen
            var p = _profiles[i];
            Ui.SetVariable("/:CurrentProfile", (German ? "Anmelden: " : "Logging in: ") + p.nick);
            Request(new NetRequest { op = "login", nick = p.nick, token = p.token, nationality = TDU2GameData.Ensure().NationalityId }, r =>
            {
                if (r.ok) { RememberProfile(r.nick, r.token); CloseMenus(false, r); return; }
                if (r.error == "bad_token") { OpenNewProfile(true, p.nick); return; }
                // Fehler (Server nicht erreichbar …): der Film hat die Liste beim Bestätigen schon geschlossen -> wieder öffnen, sonst
                // steht man ohne Menü da. Meldung in der Profilzeile.
                Debug.LogWarning("[TDU2] Login " + p.nick + ": " + r.error);
                _newGame = false;
                OpenProfiles();
                Ui.SetVariable("/:CurrentProfile", p.nick + " – " + TDU2Session.ErrorText(r.error, German));
            });
        }

        float _sentAt;

        string CurLineSafe() { try { return Ui != null ? Ui.GetVariable(ProfilePath + ":cur_line") : "-"; } catch { return "?"; } }

        void RememberProfile(string nick, string token)
        {
            int i = _profiles.FindIndex(p => string.Equals(p.nick, nick, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _profiles.RemoveAt(i);
            _profiles.Insert(0, new TDU2Session.LocalProfile { nick = nick, token = token });
            if (_profiles.Count > 8) _profiles.RemoveRange(8, _profiles.Count - 8);
            _last = nick;
            TDU2Session.StoreProfiles(_profiles, _last);
        }

        // ------------------------------------------------------------------ Popup "Neues Profil" / Passwort (new_profile/popup)
        bool _editPassOnly;
        readonly List<string> _fields = new List<string>();
        readonly Dictionary<string, string> _text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int _focus;

        void OpenNewProfile(bool passwordOnly, string nick)
        {
            var ui = Ui;
            _editPassOnly = passwordOnly;
            ui.SetValue("/:EditProfile", false);
            ui.SetValue("/:EditPassOnly", passwordOnly);
            ui.SetValue(ProfilePath + ":isNewProfile", true);
            _text["/:nickname"] = nick ?? "";
            _text["/:password"] = "";
            ui.GotoLabel(NewProfilePath, "open", true);
            // Liste offen (Passwort-Abfrage nach psel): wie X im Film die Liste schließen, Popup bleibt (isNewProfile)
            var list = ui.Find(ProfilePath)?.Sprite;
            if (list != null && list.Frame >= 1 && list.Frame < 9) ui.GotoLabel(ProfilePath, "close", true);
        }

        /// <summary>"tfoc": Film setzt /:TextToFocus auf das Eingabefeld (/:nickname, /:email, /:password).</summary>
        void FocusField(string var)
        {
            if (string.IsNullOrEmpty(var)) return;
            if (_st != St.NewProfile && _st != St.Busy)
            {
                // Popup gerade geöffnet (X im Film oder OpenNewProfile)
                _editPassOnly = IsTrue(Ui.GetVariable("/:EditPassOnly"));
                _fields.Clear();
                // Popup gamespy_on: Spitzname (/:nickname), Benutzername/E-Mail (/:email), Passwort (/:password)
                if (_editPassOnly) _fields.Add("/:password");
                else { _fields.Add("/:nickname"); _fields.Add("/:email"); _fields.Add("/:password"); }
                if (!_editPassOnly) { _text["/:nickname"] = ""; _text["/:email"] = ""; }   // Passwort-Abfrage: Name bleibt (OpenNewProfile)
                _text["/:password"] = "";
                _focus = 0;
                ShowPopupMessage(null);
                _st = St.NewProfile;
            }
            int i = _fields.FindIndex(f => string.Equals(f, var, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) _focus = i;
            RenderFields();
        }

        void UpdateNewProfile()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Escape)) { CancelNewProfile(); return; }
            if (Input.GetKeyDown(KeyCode.Tab) || Input.GetKeyDown(KeyCode.DownArrow)) _focus = (_focus + 1) % _fields.Count;
            if (Input.GetKeyDown(KeyCode.UpArrow)) _focus = (_focus + _fields.Count - 1) % _fields.Count;
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                if (_focus < _fields.Count - 1 && string.IsNullOrEmpty(Value(_fields[_focus + 1]))) _focus++;   // erst alle Felder ausfüllen
                else { SubmitNewProfile(); return; }
            }
            string f = _fields.Count > 0 ? _fields[_focus] : null;
            if (f != null)
            {
                string v = Value(f);
                int max = f.EndsWith("password") ? 32 : 16;
                foreach (char ch in Input.inputString)
                {
                    if (ch == '\b') { if (v.Length > 0) v = v.Substring(0, v.Length - 1); }
                    else if (ch == '\n' || ch == '\r' || ch == '\t') continue;
                    else if (!char.IsControl(ch) && v.Length < max) v += ch;
                }
                _text[f] = v;
            }
#endif
            RenderFields();
        }

        string Value(string field) => _text.TryGetValue(field, out var v) ? v ?? "" : "";

        void RenderFields()
        {
            var ui = Ui;
            bool blink = (Time.unscaledTime % 1f) < 0.55f;
            foreach (var f in new[] { "/:nickname", "/:email", "/:password" })
            {
                string v = Value(f);
                string shown = f.EndsWith("password") ? new string('*', v.Length) : v;
                bool focused = _st == St.NewProfile && _fields.Count > 0 && string.Equals(_fields[_focus], f, StringComparison.OrdinalIgnoreCase);
                ui.SetVariable(f, shown + (focused && blink ? "_" : ""));
            }
        }

        void ShowPopupMessage(string msg) => Ui.SetVariable("/:m_AvertAtariAccount_", string.IsNullOrEmpty(msg) ? (_avertText ?? "") : msg);

        static readonly Regex NickRx = new Regex("^[A-Za-z0-9_.\\-]{3,16}$");

        void SubmitNewProfile()
        {
            if (_st != St.NewProfile) return;
            string nick = Value("/:nickname").Trim(), pw = Value("/:password"), email = Value("/:email").Trim();
            if (!NickRx.IsMatch(nick)) { ShowPopupMessage(TDU2Session.ErrorText("invalid_nick", German)); _focus = 0; RenderFields(); return; }
            if (!_editPassOnly && email.Length > 0 && (email.IndexOf('@') < 1 || email.IndexOf('.') < 0)) { ShowPopupMessage(German ? "E-Mail-Adresse ungültig" : "Invalid e-mail address"); _focus = Math.Max(0, _fields.IndexOf("/:email")); RenderFields(); return; }
            if (pw.Length < 4) { ShowPopupMessage(TDU2Session.ErrorText("weak_password", German)); _focus = _fields.Count - 1; RenderFields(); return; }
            var gd = TDU2GameData.Ensure();
            ShowPopupMessage((German ? "Verbinde mit " : "Connecting to ") + gd.ServerHost + ":" + gd.TcpPort + " ...");
            var req = _editPassOnly ? new NetRequest { op = "login", nick = nick, password = pw, nationality = gd.NationalityId } : new NetRequest { op = "register", nick = nick, password = pw, email = email, nationality = gd.NationalityId };
            Request(req, r =>
            {
                if (!r.ok && !_editPassOnly && r.error == "nick_taken")
                {
                    // Konto gibt es schon (z.B. Profil auf diesem PC verloren): mit Name + Passwort anmelden, das lokale Profil
                    // (Merk-Token) wird neu angelegt, der Spielstand kommt wie immer vom Server
                    ShowPopupMessage(German ? "Konto \"" + nick + "\" gefunden – melde an ..." : "Account \"" + nick + "\" found – logging in ...");
                    Request(new NetRequest { op = "login", nick = nick, password = pw, nationality = gd.NationalityId }, r2 =>
                    {
                        if (!r2.ok)
                        {
                            ShowPopupMessage(r2.error == "bad_password"
                                ? (German ? "Den Namen gibt es schon – Passwort stimmt nicht" : "Name already exists – wrong password")
                                : TDU2Session.ErrorText(r2.error, German));
                            _focus = _fields.Count - 1; _text["/:password"] = ""; RenderFields();
                            return;
                        }
                        Debug.Log("[TDU2] Login: bestehendes Konto " + r2.nick + " wiederhergestellt (lokales Profil neu angelegt)");
                        FinishPopupLogin(r2);
                    });
                    return;
                }
                if (!r.ok) { ShowPopupMessage(TDU2Session.ErrorText(r.error, German)); return; }
                FinishPopupLogin(r);
            });
        }

        void FinishPopupLogin(NetResponse r)
        {
            RememberProfile(r.nick, r.token);
            _text["/:password"] = "";
            ShowPopupMessage(null);
            Ui.SetValue(NewProfilePath + ":IsValidNewProfile", true);
            Ui.GotoLabel(NewProfilePath, "close", true);
            CloseMenus(true, r);
        }

        float _cancelAt = -10f;

        void CancelNewProfile()
        {
            var ui = Ui;
            if (_profiles.Count == 0 && !_editPassOnly)
            {
                // ohne Profil gibt es nichts zurück – erst ein zweites Abbrechen beendet das Spiel (wie "Beenden" im Film)
                if (Time.unscaledTime - _cancelAt < 2f) { QuitGame(); return; }
                _cancelAt = Time.unscaledTime;
                ShowPopupMessage(German ? "Neues Konto anlegen oder mit bestehendem Konto anmelden (Name + Passwort). Nochmal Abbrechen = Beenden."
                                        : "Create an account or log in with an existing one (name + password). Cancel again = quit.");
                return;
            }
            _text["/:password"] = "";
            ShowPopupMessage(null);
            ui.SetValue("/:EditPassOnly", false);
            ui.SetValue(NewProfilePath + ":IsValidNewProfile", false);
            ui.GotoLabel(NewProfilePath, "close", true);   // Film öffnet danach die Profilliste wieder (popn)
            _st = St.Profiles;
            RenderFields();
        }

        // ------------------------------------------------------------------ Menüs schließen, dann Spiel starten
        NetResponse _login;
        bool _fromPopup, _menusClosed;

        void CloseMenus(bool fromPopup, NetResponse r)
        {
            _login = r; _fromPopup = fromPopup; _menusClosed = false;
            SyncSavegame(r);
            _st = St.Closing; _t = Time.unscaledTime;
            var ui = Ui;
            ui.SetValue("/:EditPassOnly", false);
            ui.SetVariable("/:CurrentProfile", r.nick);
            if (!fromPopup) { ui.GotoLabel(ProfilePath, "close", true); _menusClosed = true; }   // → pcls + profiles_close
        }

        bool _pushImported;

        /// <summary>
        /// Lokaler Spielstand im Original-Format (TDU2SaveGame, Dokumente/KuxiiSoft/TDU2toUnity3D/savegame/&lt;Profil&gt;/PLAYERSAVE/DATA).
        /// Der Server ist die Quelle: hat er Fortschritt, wird die lokale DATA daraus (neu) gebaut. Ist der Server-Spielstand leer,
        /// die lokale DATA hat aber Fortschritt, wird sie übernommen und beim Spielstart an den Server geschickt.
        /// Außerdem: Story-Schritt aus dem Server-Spielstand, wenn er auf diesem PC (PlayerPrefs) fehlt.
        /// </summary>
        void SyncSavegame(NetResponse r)
        {
            _pushImported = false;
            if (r == null || string.IsNullOrEmpty(r.nick)) return;
            try
            {
                var db = Manager.Database;
                TDU2SaveGame.Database = db;
                if (r.save == null) r.save = new SaveData();
                var sv = r.save;
                int pref = Story.TDU2StoryProgress.Get(r.nick);
                if (pref < 0 && sv.story >= 0)
                {
                    Story.TDU2StoryProgress.Set(r.nick, sv.story);
                    Debug.Log($"[TDU2] Savegame: Story-Schritt {sv.story} aus dem Server-Spielstand übernommen");
                }
                bool server = sv.starterChosen || (sv.ownedCars?.Count ?? 0) > 0 || sv.hasPosition || sv.story > 0;
                bool exists = TDU2SaveGame.Exists(r.nick);
                Debug.Log($"[TDU2] Savegame {r.nick}: Server-Fortschritt={server} (Autos {sv.ownedCars?.Count ?? 0}, Story {sv.story}), lokale DATA vorhanden={exists} ({TDU2SaveGame.PathFor(r.nick)})");
                if (server)
                {
                    TDU2SaveGame.WriteAsync(r.nick, sv, db, exists ? "lokale DATA aus dem Server-Spielstand aktualisiert" : "lokale DATA fehlte – aus dem Server-Spielstand neu gebaut");
                    return;
                }
                if (!exists) { TDU2SaveGame.WriteAsync(r.nick, sv, db, "neues Profil – lokale DATA angelegt"); return; }
                if (!TDU2SaveGame.TryRead(r.nick, db, out var local, out string why))
                {
                    Debug.LogWarning("[TDU2] Savegame: lokale DATA nicht lesbar (" + why + ") – Server-Spielstand bleibt");
                    return;
                }
                Debug.Log("[TDU2] Savegame: lokale DATA gelesen – " + why);
                bool localProgress = local.starterChosen || local.ownedCars.Count > 0 || local.hasPosition || local.story > 0;
                if (!localProgress) { Debug.Log("[TDU2] Savegame: lokale DATA ohne Fortschritt – nichts übernommen"); return; }
                TDU2SaveGame.KeepServerOnly(local, sv);
                r.save = local;
                if (local.story >= 0 && Story.TDU2StoryProgress.Get(r.nick) < local.story) Story.TDU2StoryProgress.Set(r.nick, local.story);
                _pushImported = true;
                Debug.Log("[TDU2] Savegame: Server-Spielstand war leer – lokale DATA übernommen, wird an den Server geschickt");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[TDU2] Savegame: Abgleich fehlgeschlagen: " + e.Message);
            }
        }

        void UpdateClosing()
        {
            var ui = Ui;
            float dt = Time.unscaledTime - _t;
            if (_fromPopup && !_menusClosed && dt > 0.5f) { _menusClosed = true; ui.GotoLabel("/menus", "profiles_close", true); }
            if (dt < 1.6f) return;
            if (_newGame) { FinishLogin(true); return; }   // Y in der Profilliste
            // Ohne Spielfortschritt gibt es nichts fortzusetzen: direkt das Intro (Neues Spiel). Mit Fortschritt das Hauptmenü
            // des Originals (Spiel fortsetzen / Neues Spiel / Profile / Beenden).
            if (!HasProgress(out string why))
            {
                Debug.Log($"[TDU2] Profil {_login?.nick}: kein Spielfortschritt ({why}) – Neues Spiel (Intro)");
                FinishLogin(true);
                return;
            }
            Debug.Log($"[TDU2] Profil {_login?.nick}: Spielfortschritt vorhanden ({why})");
            // Aus dem Neues-Profil-Popup (Konto angelegt/wiederhergestellt, Spielstand z.B. aus der lokalen DATA): der Film startet
            // nach "close" mit IsValidNewProfile selbst das Laden – das Hauptmenü (main_in) passt dann nicht mehr dazu (endloser
            // Ladebildschirm). Wie im Original direkt weiterspielen.
            // Mit Fortschritt direkt weiterspielen (Neues Spiel: Y in der Profilliste). Das Hauptmenü (main_in) blieb nach der
            // Profilwahl im Ladebildschirm hängen – nur noch auf ausdrücklichen Wunsch (ShowMainMenu + MainMenuAfterLogin).
            if (ShowMainMenu && MainMenuAfterLogin && !_fromPopup) { EnterMainMenu(); return; }
            Debug.Log("[TDU2] Profil " + _login?.nick + ": Spiel wird fortgesetzt");
            FinishLogin(false);
        }

        // ------------------------------------------------------------------ Hauptmenü (Original main.vmf, WhatGameMode 0)
        bool _newGame;

        void EnterMainMenu()
        {
            var ui = Ui;
            var save = _login?.save;
            // Ins Hauptmenü kommt nur ein Profil mit Fortschritt (UpdateClosing) -> "Spiel fortsetzen" immer frei
            bool progress = HasProgress(out _);
            foreach (var n in new[] { "LockSnapshot", "LockChangePosition", "LockInviteParty", "LockManageParty", "LockRestart", "LockEditor",
                                      "LockBookmarks", "LockAchiev", "LockRankings", "LockOnline", "LockSocial", "LockStatus", "LockResume",
                                      "LockQuit", "LockPlayers", "LockJoin", "LockGameProgress", "LockInvite", "LockSave" })
                ui.SetValue("/:" + n, true);
            ui.SetValue("/:LockContinueGame", !progress);
            ui.SetValue("/:LockNewGame", false);
            ui.SetValue("/:LockProfiles", false);
            ui.SetValue("/:LockOptions", true);    // Optionsseiten laufen über das Pausemenü (im Spiel erreichbar)
            ui.SetValue("/:LockExitGame", false);
            ui.SetValue("/:WhatGameMode", 0);
            ui.SetValue("/:WhatPlatform", 1);
            ui.SetValue("/:Input", true);
            ui.SetVariable("/:CurrentProfile", _login?.nick ?? "");
            ui.Visible = true;
            ui.GotoLabel("/menus", "main_in", true);
            _st = St.MainMenu;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }

        void UpdateMainMenu()
        {
            var ui = Ui;
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) ui.Input("Move", "up");
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) ui.Input("Move", "down");
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.E)) ui.Input("A");
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace)) ui.Input("B");
#endif
        }

        /// <summary>
        /// Hat das Profil etwas zum Fortsetzen? Spielstand vom Server (Startauto gewählt, Autos, gespeicherte Position) oder die
        /// Geschichte ist über die Avatar-Wahl hinaus (Schritt &gt; 0). why = Begründung fürs Log.
        /// </summary>
        bool HasProgress(out string why)
        {
            var save = _login?.save;
            int step = Story.TDU2StoryProgress.Get(_login?.nick);
            int cars = save?.ownedCars?.Count ?? 0;
            bool starter = save != null && save.starterChosen, pos = save != null && save.hasPosition;
            why = $"Startauto={starter}, Autos={cars}, Position={pos}, Geschichte Schritt {step}";
            return starter || cars > 0 || pos || step > 0;
        }

        /// <summary>Anmeldung abschließen und spielen; newGame = Spielstand und Geschichte auf Anfang (Original "Neues Spiel").</summary>
        void FinishLogin(bool newGame)
        {
            var ui = Ui;
            if (newGame) ResetForNewGame(_login);
            _newGame = false;
            ui.SetValue("/:Input", true);
            ui.GotoLabel("/", "default", false);
            ui.Visible = false;
            Unsubscribe();   // Klicks/Befehle gehören wieder dem Pausemenü
            StartGame(_login);
        }

        /// <summary>
        /// Neues Spiel: frischer Spielstand (Autos, Geld, Position, Häuser, Kleidung …), Geschichte ab Schritt 0 (Intro/Avatar-Wahl).
        /// Behalten werden nur Profildaten, die kein Spielfortschritt sind (MyTDULife, Lesezeichen, Lieblings-Spots, eigene Farben).
        /// Der Server erhält den neuen Stand mit dem nächsten Autosave.
        /// </summary>
        void ResetForNewGame(NetResponse r)
        {
            if (r == null) return;
            var old = r.save ?? new SaveData();
            r.save = new SaveData
            {
                myLife = old.myLife ?? new MyLifeData(),
                bookmarks = old.bookmarks ?? new List<BookmarkEntry>(),
                favoriteSpots = old.favoriteSpots ?? new List<string>(),
                favoriteColors = old.favoriteColors ?? new List<int>(),
            };
            Story.TDU2StoryProgress.Set(r.nick, 0);
            Story.TDU2StoryProgress.SetGaveStoryCar(r.nick, false);
            Story.TDU2StoryProgress.SetIntroAvatar(r.nick, "");
            _storyDone = false;
            r.save.story = 0;
            TDU2SaveGame.WriteAsync(r.nick, r.save, Manager.Database, "Neues Spiel – lokale DATA zurückgesetzt");
            DebugManager.Info($"Neues Spiel für {r.nick}: Spielstand zurückgesetzt, Geschichte ab Schritt 0");
        }

        // ------------------------------------------------------------------ 3. Spielstart (Spielstand vom Server)
        Vector3 _spawnGame;
        Vector3 _spawnDirGame;
        bool _spawnDirFromSpot;
        float _spawnUntil;
        Action<Vector3, Quaternion> _spawnAction;

        void StartGame(NetResponse r)
        {
            TDU2TitleScene.End();
            TDU2FrontendMusic.Stop();
            TDU2Session.Begin(r);
            ApplyOdometers();
            var save = TDU2Session.Save;
            if (_pushImported)
            {
                _pushImported = false;
                TDU2Session.SaveAsync();
                Debug.Log("[TDU2] Savegame: übernommener lokaler Spielstand an den Server geschickt");
            }
            string island = Manager.Streamer?.Index?.IslandName ?? Manager.Streamer?.Island ?? "";
            if (string.IsNullOrEmpty(save.island)) save.island = island;
            DebugManager.Info($"Angemeldet als {r.nick} – {save.ownedCars.Count} Autos, Startauto gewählt: {save.starterChosen}, {r.online} online");

            // Geschichte (GamePC.cpr SCENARIOTUTORIAL = ON) noch nicht gespielt bzw. mittendrin: Intro und Missionen statt des direkten
            // Starts – wie im Original auf Ibiza (Party-Haus EPH_L3DI_5067I)
            if (!_storyDone && Story.TDU2Story.ShouldRun(TDU2GamePC.Load(Manager.BnkDir), r.nick, save.starterChosen))
            {
                if (!island.StartsWith("ibiza", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TDU2IslandTravel.Pending && TDU2IslandTravel.CanTravelTo("ibiza", Manager.BnkDir)) { save.hasPosition = false; TDU2IslandTravel.Go("ibiza", false); return; }
                }
                else { BeginStory(r); return; }
            }

            // Spielstand auf der anderen Insel: dorthin wechseln (Szene mit der Insel neu laden, danach geht es hier weiter).
            // Auch ohne gespeicherte Fahrposition, wenn man zuletzt in einem Interieur (Haus …) der anderen Insel war – sonst blieb man
            // nach dem Neustart auf Ibiza, das Hawaii-Haus wurde dort nicht gefunden und man stand irgendwo auf der Straße.
            Debug.Log($"[TDU2] Spielstand: Insel {save.island} (Szene {island}), Position={save.hasPosition}, Interieur='{save.interior}', Auto {save.currentCar}");
            if (!TDU2IslandTravel.Pending && save.starterChosen && (save.hasPosition || !string.IsNullOrEmpty(save.interior)) && !string.IsNullOrEmpty(save.island)
                && !string.Equals(save.island, island, StringComparison.OrdinalIgnoreCase) && TDU2IslandTravel.CanTravelTo(save.island, Manager.BnkDir))
            {
                TDU2IslandTravel.Go(save.island, false);
                return;
            }

            CarPhysics car = null;
            if (save.starterChosen)
            {
                // Rennschul-Prüfungsautos (gleicher Dateiname wie das Serienauto, z.B. Mustang_Fastback_68) wurden früher beim
                // Autosave als aktuelles Auto gespeichert -> nur eigene Autos nehmen
                if (save.ownedCars.Count > 0 && !save.ownedCars.Exists(c => string.Equals(c, save.currentCar, StringComparison.OrdinalIgnoreCase)))
                {
                    Debug.LogWarning("Spielstand: aktuelles Auto " + save.currentCar + " gehört nicht zu den eigenen Autos – nehme " + save.ownedCars[0]);
                    save.currentCar = save.ownedCars[0];
                }
                car = Manager.FindCar(save.currentCar);
                if (car == null) foreach (var n in save.ownedCars) { car = Manager.FindCar(n); if (car != null) break; }
            }
            // Startpunkt wie im Spiel (GamePC.cpr): neues Konto auf der Startinsel (AddLevel) im Standard-Haus (DefaultHouse)
            var gp = GamePcStart ? TDU2GamePC.Load(Manager.BnkDir) : null;
            if (gp != null && !save.starterChosen && !TDU2IslandTravel.Pending && !string.IsNullOrEmpty(gp.StartIsland)
                && !island.StartsWith(gp.StartIsland, StringComparison.OrdinalIgnoreCase) && TDU2IslandTravel.CanTravelTo(gp.StartIsland, Manager.BnkDir))
            {
                DebugManager.Debug("Neues Konto: Startinsel laut GamePC.cpr ist " + TDU2IslandTravel.DisplayName(gp.StartIsland));
                save.hasPosition = false;
                TDU2IslandTravel.Go(gp.StartIsland, false);
                return;
            }
            var home = gp?.FindHouse(Manager.Streamer?.Spots?.Spots);
            if (gp != null && home == null && gp.Houses.Count > 0) Debug.LogWarning("GamePC.cpr: Standard-Haus " + string.Join("/", gp.Houses) + " liegt nicht auf " + island);
            var dealer = FindSpot(string.IsNullOrEmpty(r.startSpot) ? "ESTOL_B" : r.startSpot);
            var spot = home ?? dealer;
            if (TDU2IslandTravel.Arriving)
            {
                // Ankunft nach dem Flug: Flughafen der Insel (Spot EAIR*) bzw. Landeplatz (ELAND*), mit dem mitgenommenen Auto davor
                var air = AirportSpot();
                if (air != null) { spot = air; save.hasPosition = false; }
                DebugManager.Debug("Ankunft auf " + island + (air != null ? " am Flughafen " + air.Code : " (kein Flughafen-Spot gefunden)"));
            }

            // Gespeicherte Position muss auf dieser Insel liegen (nach einem Absturz während eines Inselwechsels konnten
            // Insel und Position auseinanderfallen -> Auto mitten im Nirgendwo / falsche Insel): sonst am Start-Spot beginnen
            if (save.hasPosition && string.Equals(save.island, island, StringComparison.OrdinalIgnoreCase) && !PositionOnIsland(new Vector3(save.px, save.py, save.pz)))
            {
                Debug.LogWarning($"Spielstand: Position ({save.px:F0}, {save.pz:F0}) liegt nicht auf {island} – starte am Spot");
                save.hasPosition = false;
            }

            // Zuletzt im Interieur (Haus, Garage, Laden …): dort weitermachen wie im Spiel, nicht auf der Straße davor
            if (car != null && !TDU2IslandTravel.Arriving && !string.IsNullOrEmpty(save.interior))
            {
                var parts = save.interior.Split('|');
                Spot last = null;
                var all = Manager.Streamer?.Spots?.Spots;
                // Haus über den vollen DBHouse-Namen (3. Teil): mehrere Häuser teilen sich den Code (z.B. EPH_L3A), sonst das erste
                if (all != null && parts.Length > 2 && parts[2].Length > 0) last = TDU2GamePC.FindSpot(all, parts[2]);
                if (all != null && last == null)
                    foreach (var s in all)
                        if (string.Equals(s.Code, parts[0], StringComparison.OrdinalIgnoreCase)) { last = s; break; }
                var interiors = Manager.GetComponent<TDU2Interiors>();
                Debug.Log($"[TDU2] Spielstand: weiter im Interieur {save.interior} – Spot {(last != null ? "gefunden" : "NICHT gefunden")} auf {island}");
                if (last != null && interiors != null)
                {
                    var inCar = car;
                    string stem = parts.Length > 1 ? parts[1] : "";
                    var at = last.Position2.sqrMagnitude > 1f ? last.Position2 : last.Position;
                    DebugManager.Debug("Spielstand: weiter im Interieur " + last.Code + (stem.Length > 0 ? " (" + stem + ")" : ""));
                    BeginSpawn(at, new Vector3(Mathf.Sin(last.Heading2), 0f, Mathf.Cos(last.Heading2)), true, (p, q) =>
                    {
                        if (!interiors.EnterSpotRoom(last, stem, inCar)) { Debug.LogWarning("[TDU2] Spielstand: Interieur " + last.Code + " (" + stem + ") nicht betretbar – Auto davor"); SpawnCar(inCar, p, q); }
                    });
                    return;
                }
                save.interior = "";
            }

            // Casino-Insel: wie im Spiel steht man nach dem Flug (und beim Fortsetzen) in der Casino-Halle, nicht draußen
            if (car != null && island.StartsWith("cas", StringComparison.OrdinalIgnoreCase))
            {
                Spot cas = null;
                var all = Manager.Streamer?.Spots?.Spots;
                if (all != null)
                    foreach (var s in all)
                        if (s.Code != null && s.Code.StartsWith("ECAS", StringComparison.OrdinalIgnoreCase)) { cas = s; break; }
                var interiors = Manager.GetComponent<TDU2Interiors>();
                if (cas != null && interiors != null)
                {
                    var casCar = car;
                    var at = cas.Position2.sqrMagnitude > 1f ? cas.Position2 : cas.Position;
                    BeginSpawn(at, new Vector3(Mathf.Sin(cas.Heading2), 0f, Mathf.Cos(cas.Heading2)), true, (p, q) =>
                    {
                        if (!interiors.EnterSpot(cas, casCar)) SpawnCar(casCar, p, q);
                    });
                    return;
                }
            }

            if (car != null)
            {
                if (save.hasPosition && string.Equals(save.island, island, StringComparison.OrdinalIgnoreCase))
                {
                    float h = save.heading * Mathf.Deg2Rad;
                    BeginSpawn(new Vector3(save.px, save.py, save.pz), new Vector3(Mathf.Sin(h), 0f, Mathf.Cos(h)), false, (p, q) => SpawnCar(car, p, q));
                }
                else if (spot != null)
                    BeginSpawn(spot.Position2.sqrMagnitude > 1f ? spot.Position2 : spot.Position, new Vector3(Mathf.Sin(spot.Heading2), 0f, Mathf.Cos(spot.Heading2)), true, (p, q) => SpawnCar(car, p, q));
                else
                    BeginSpawn(Manager.Streamer.ViewerGamePosition, Vector3.forward, false, (p, q) => SpawnCar(car, p, q));
                return;
            }

            // neues Konto wie im Spiel (GamePC.cpr): Auto + Garagenautos, Startgeld, Beginn im Standard-Haus
            if (gp != null && StartFromGamePc(gp, home, dealer)) return;

            // sonst: Gebrauchtwagenhändler mit den Startautos
            spot = dealer;
            var starters = new List<CarPhysics>();
            foreach (var n in r.starterCars ?? new List<string>()) { var c = Manager.FindCar(n); if (c != null && !starters.Contains(c)) starters.Add(c); }
            if (starters.Count == 0) { var c = Manager.FindCar(Manager.StartCar) ?? Manager.Available[0]; starters.Add(c); }
            var inter = Manager.GetComponent<TDU2Interiors>();
            if (spot == null || inter == null)
            {
                Debug.LogWarning($"Start-Spot {r.startSpot} nicht gefunden – Startauto direkt");
                OnStarterChosen(starters[0]);
                BeginSpawn(Manager.Streamer.ViewerGamePosition, Vector3.forward, false, (p, q) => SpawnCar(starters[0], p, q));
                return;
            }
            BeginSpawn(spot.Position, Vector3.forward, false, (p, q) =>
            {
                Active = false;
                _st = St.Playing;
                inter.EnterStarterDealer(spot, starters, OnStarterChosen);
            });
        }

        /// <summary>
        /// Neues Konto nach GamePC.cpr: DefaultCarConfig = aktuelles Auto, DefaultGarageConfig (_id_car) = weitere eigene Autos,
        /// DefaultMoney = Startgeld; man beginnt im Wohnbereich des DefaultHouse (Ausgang über die Garage mit dem Auto),
        /// ohne Haus an DEFAULTSTARTPOSITION bzw. am Händler-Spot. false = keine Autos gefunden (dann Händler-Auswahl).
        /// </summary>
        bool StartFromGamePc(TDU2GamePC gp, Spot home, Spot fallback)
        {
            var save = TDU2Session.Save;
            if (save == null) return false;
            var cars = new List<CarPhysics>();
            var main = FindCarConfig(gp.DefaultCar);
            if (main != null) cars.Add(main);
            foreach (int id in gp.GarageCars)
            {
                var c = Manager.Available.Find(x => x.Id == id);
                if (c != null && !cars.Contains(c)) cars.Add(c);
            }
            if (cars.Count == 0)
            {
                Debug.LogWarning("GamePC.cpr: weder DefaultCarConfig (" + string.Join(" ", gp.DefaultCar) + ") noch DefaultGarageConfig gefunden – Startauto beim Händler");
                return false;
            }
            if (gp.DefaultMoney > 0) save.money = gp.DefaultMoney;
            for (int i = 1; i < cars.Count; i++) TDU2Session.AddOwned(cars[i].FileName);
            var car = cars[0];
            OnStarterChosen(car);
            DebugManager.Debug($"Neues Konto (GamePC.cpr): {car.FileName}" + (cars.Count > 1 ? " + Garage " + string.Join(", ", cars.GetRange(1, cars.Count - 1).ConvertAll(c => c.FileName)) : "") +
                      $", {save.money} Geld, Start " + (home != null ? "im Haus " + string.Join("/", gp.Houses) : gp.HasStartPosition ? "an " + gp.StartPosition : "am Spot"));

            var interiors = Manager.GetComponent<TDU2Interiors>();
            if (home != null)
            {
                var at = home.Position2.sqrMagnitude > 1f ? home.Position2 : home.Position;
                BeginSpawn(at, new Vector3(Mathf.Sin(home.Heading2), 0f, Mathf.Cos(home.Heading2)), true, (p, q) =>
                {
                    if (interiors == null || !interiors.EnterSpot(home, car)) SpawnCar(car, p, q);
                });
                return true;
            }
            if (gp.HasStartPosition) { BeginSpawn(gp.StartPosition, Vector3.forward, false, (p, q) => SpawnCar(car, p, q)); return true; }
            if (fallback != null)
                BeginSpawn(fallback.Position2.sqrMagnitude > 1f ? fallback.Position2 : fallback.Position, new Vector3(Mathf.Sin(fallback.Heading2), 0f, Mathf.Cos(fallback.Heading2)), true, (p, q) => SpawnCar(car, p, q));
            else
                BeginSpawn(Manager.Streamer.ViewerGamePosition, Vector3.forward, false, (p, q) => SpawnCar(car, p, q));
            return true;
        }

        /// <summary>DefaultCarConfig "Ferrari" "F430" "Scuderia" -> Auto (Dateiname Modell_Version, sonst Modell-/Versionsname).</summary>
        CarPhysics FindCarConfig(string[] cfg)
        {
            if (cfg == null || cfg.Length < 2) return null;
            string model = cfg[1].Trim(), version = cfg.Length > 2 ? cfg[2].Trim() : "";
            string file = (model + (version.Length > 0 ? "_" + version : "")).Replace(' ', '_');
            var c = Manager.FindCar(file);
            if (c != null) return c;
            if (version.Length > 0)
                foreach (var x in Manager.Available)
                {
                    string mn = x.ModelName ?? "";
                    if (mn.IndexOf(model, StringComparison.OrdinalIgnoreCase) >= 0 && mn.IndexOf(version, StringComparison.OrdinalIgnoreCase) >= 0) return x;
                }
            return Manager.FindCar(model);
        }

        /// <summary>Liegt eine Spielposition auf der geladenen Insel? (nächster Spot höchstens 15 km entfernt)</summary>
        bool PositionOnIsland(Vector3 game)
        {
            var spots = Manager.Streamer?.Spots?.Spots;
            if (spots == null || spots.Count == 0) return true;
            if (float.IsNaN(game.x) || float.IsNaN(game.z) || float.IsInfinity(game.x) || float.IsInfinity(game.z)) return false;
            const float Max = 15000f;
            foreach (var s in spots)
            {
                float dx = s.Position.x - game.x, dz = s.Position.z - game.z;
                if (dx * dx + dz * dz < Max * Max) return true;
            }
            return false;
        }

        Spot AirportSpot()
        {
            var spots = Manager.Streamer?.Spots?.Spots;
            if (spots == null) return null;
            string pre = TDU2IslandTravel.ArrivalPrefix ?? "EAIR";
            foreach (var s in spots) if (s.Code != null && s.Code.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return s;
            if (pre != "EAIR") foreach (var s in spots) if (s.Code != null && s.Code.StartsWith("EAIR", StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        // ------------------------------------------------------------------ Geschichte (Story/TDU2Story.cs)
        NetResponse _storyLogin;
        bool _storyDone;

        void BeginStory(NetResponse r)
        {
            _storyLogin = r;
            _st = St.Story;   // Login bleibt aktiv (Pausemenü/Karte gesperrt), bis die Geschichte ein Auto setzt
            DebugManager.Info("Geschichte: Schritt " + Story.TDU2StoryProgress.Get(r.nick) + " – " + Story.TDU2StoryProgress.StepName(Mathf.Max(0, Story.TDU2StoryProgress.Get(r.nick))));
            var s = Story.TDU2Story.Begin(Manager, r.nick, StorySpawn);
            s.StarterDealer = StoryDealer;
        }

        /// <summary>Die Geschichte setzt das Auto (Weltlage) und beginnt die freie Fahrt.</summary>
        void StorySpawn(CarPhysics car, Vector3 world, Quaternion rot)
        {
            if (car == null) return;
            var root = Manager.Streamer.MapRoot;
            var game = root.InverseTransformPoint(world);
            var dir = root.InverseTransformVector(rot * Vector3.forward);
            BeginSpawn(game, dir, false, (p, q) =>
            {
                var save = TDU2Session.Save;
                bool owned = save != null && save.ownedCars.Exists(c => string.Equals(c, car.FileName, StringComparison.OrdinalIgnoreCase));
                if (!owned || save == null || !save.starterChosen) OnStarterChosen(car);
                SpawnCar(car, p, q);
            });
        }

        /// <summary>Story-Schritt 7: Gebrauchtwagenhändler (ESTOL_B) mit den Startautos des Servers.</summary>
        void StoryDealer(Action<CarPhysics> chosen)
        {
            var r = _storyLogin ?? TDU2Session.Config;
            var spot = FindSpot(r != null && !string.IsNullOrEmpty(r.startSpot) ? r.startSpot : "ESTOL_B") ?? FindSpot("ESTOL_B");
            var starters = new List<CarPhysics>();
            if (r != null) foreach (var n in r.starterCars ?? new List<string>()) { var c = Manager.FindCar(n); if (c != null && !starters.Contains(c)) starters.Add(c); }
            if (starters.Count == 0) { var c = Manager.FindCar(Manager.StartCar) ?? Manager.Available[0]; starters.Add(c); }
            var inter = Manager.GetComponent<TDU2Interiors>();
            Action<CarPhysics> done = car => { OnStarterChosen(car); chosen?.Invoke(car); };
            if (spot == null || inter == null)
            {
                done(starters[0]);
                BeginSpawn(Manager.Streamer.ViewerGamePosition, Vector3.forward, false, (p, q) => SpawnCar(starters[0], p, q));
                return;
            }
            BeginSpawn(spot.Position, Vector3.forward, false, (p, q) => inter.EnterStarterDealer(spot, starters, done));
        }

        Spot FindSpot(string code)
        {
            var spots = Manager.Streamer?.Spots?.Spots;
            if (spots == null || string.IsNullOrEmpty(code)) return null;
            foreach (var s in spots) if (string.Equals(s.Code, code, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        void BeginSpawn(Vector3 game, Vector3 dirGame, bool dirFromSpot, Action<Vector3, Quaternion> action)
        {
            _spawnGame = game; _spawnDirGame = dirGame; _spawnDirFromSpot = dirFromSpot; _spawnAction = action;
            _spawnUntil = Time.unscaledTime + 15f;
            _st = St.Spawning;
            PlaceCamera();
        }

        Vector3 SpawnWorld() => Manager.Streamer.MapRoot.TransformPoint(_spawnGame);

        Vector3 SpawnDirWorld()
        {
            var root = Manager.Streamer.MapRoot;
            // Spot-Richtung wie TDU2Interiors.ExitInterior (an den Straßen geprüft), eigene Richtung über TransformVector (Spiegelung)
            var f = _spawnDirFromSpot ? root.TransformDirection(_spawnDirGame) : root.TransformVector(_spawnDirGame);
            f.y = 0f;
            return f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
        }

        void PlaceCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var p = SpawnWorld(); var f = SpawnDirWorld();
            cam.transform.SetPositionAndRotation(p - f * 7f + Vector3.up * 3f, Quaternion.LookRotation(f, Vector3.up));
        }

        void UpdateSpawning()
        {
            PlaceCamera();   // Streaming lädt um die Kamera herum
            var p = SpawnWorld();
            bool ground = UnityEngine.Physics.Raycast(p + Vector3.up * 60f, Vector3.down, out _, 200f, ~(1 << 2), QueryTriggerInteraction.Ignore);
            if (!ground && Time.unscaledTime < _spawnUntil) return;
            var f = SpawnDirWorld();
            p.y = Manager.GroundHeight(p) + 0.4f;
            var act = _spawnAction; _spawnAction = null;
            _st = St.Playing;
            Active = false;
            Manager.Suspended = false;
            _nextSave = Time.unscaledTime + AutosaveSeconds;
            act?.Invoke(p, Quaternion.LookRotation(f, Vector3.up));
        }

        void SpawnCar(CarPhysics car, Vector3 p, Quaternion q)
        {
            Manager.SpawnCar(car, p, q);
            TDU2Session.SetCurrentCar(car.FileName);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }

        void OnStarterChosen(CarPhysics car)
        {
            if (car == null || TDU2Session.Save == null) return;
            TDU2Session.AddOwned(car.FileName);
            TDU2Session.SetCurrentCar(car.FileName);
            TDU2Session.Save.starterChosen = true;
            DebugManager.Info("Startauto gewählt: " + car.FileName);
            _nextSave = Time.unscaledTime + 3f;   // kurz nach dem Losfahren speichern (dann mit Position)
            TDU2Session.SaveAsync();
        }

        // ------------------------------------------------------------------ Im Spiel: Autosave (Spielerliste/-zahl: TDU2GameData)
        float _nextSave;

        void UpdatePlaying()
        {
            if (!TDU2Session.LoggedIn) return;
            if (Time.unscaledTime >= _nextSave)
            {
                _nextSave = Time.unscaledTime + Mathf.Max(5f, AutosaveSeconds);
                CaptureState();
                TDU2Session.SaveAsync();
            }
        }

        /// <summary>Aktuellen Stand (Auto, Position, Richtung, Kilometerstände) in den Spielstand übernehmen.</summary>
        void CaptureState()
        {
            var save = TDU2Session.Save;
            if (save == null) return;
            // Inselwechsel läuft: der Spielstand zeigt schon auf die Zielinsel – nicht mit der alten Position/Insel überschreiben
            // (sonst landet man nach dem Neuladen bzw. nach einem Absturz mit Ibiza-Koordinaten auf Hawaii oder umgekehrt)
            if (TDU2IslandTravel.Pending) return;
            var inter = Manager.GetComponent<TDU2Interiors>();
            var veh = Manager.Current;
            var root = Manager.Streamer?.MapRoot;
            // während einer Rennschul-Prüfung (Prüfungsauto, private Strecke) Auto und Position nicht speichern
            bool schoolCar = veh != null && Racing.RacingSchoolMenu.IsSchoolCar(veh.Car);
            // Testfahrt beim Händler: Leihwagen und Position nicht speichern
            if (veh != null && veh.Car != null && root != null && (inter == null || !inter.Inside) && !schoolCar && !TDU2Interiors.TestDriveActive)
            {
                save.currentCar = veh.Car.FileName;
                var g = root.InverseTransformPoint(veh.transform.position);
                var d = root.InverseTransformVector(veh.transform.forward); d.y = 0f;
                save.px = g.x; save.py = g.y; save.pz = g.z;
                save.heading = d.sqrMagnitude > 1e-6f ? Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg : 0f;
                save.hasPosition = true;
                save.island = Manager.Streamer?.Index?.IslandName ?? save.island;
            }
            // Interieur merken (nicht als Gast und nicht im Casino – dort beginnt man ohnehin in der Halle)
            if (inter != null && inter.Inside && inter.CurrentSpot != null && inter.GuestHost == 0
                && !(inter.CurrentSpot.Code ?? "").StartsWith("ECAS", StringComparison.OrdinalIgnoreCase))
                save.interior = inter.CurrentSpot.Code + "|" + inter.CurrentBankStem + "|" + (InteriorHouse(inter.CurrentSpot) ?? "");
            else if (inter == null || !inter.Inside)
                save.interior = "";
            CollectOdometers(save);
        }

        Spot _houseSpot;
        string _houseName;

        /// <summary>DBHouse-Name des Interieur-Spots (Haus), sonst null – gecacht.</summary>
        string InteriorHouse(Spot s)
        {
            if (s == null) return null;
            if (!ReferenceEquals(s, _houseSpot)) { _houseSpot = s; _houseName = TDU2SaveGame.HouseOfSpot(Manager.Database, s); }
            return _houseName;
        }

        void CollectOdometers(SaveData save)
        {
            var ci = CultureInfo.InvariantCulture;
            var list = new List<OdoEntry>();
            var seen = new HashSet<int>();
            var names = new List<string>(save.ownedCars);
            if (!string.IsNullOrEmpty(save.currentCar)) names.Add(save.currentCar);
            foreach (var n in names)
            {
                var c = Manager.FindCar(n);
                if (c == null || !seen.Add(c.Id)) continue;
                if (double.TryParse(PlayerPrefs.GetString("TDU2.Odo." + c.Id, ""), NumberStyles.Float, ci, out double m) && m > 0)
                    list.Add(new OdoEntry { carId = c.Id, meters = m });
            }
            save.odometers = list;
        }

        /// <summary>Kilometerstände des Spielstands in die PlayerPrefs (dort liest/schreibt sie der Tacho).</summary>
        static void ApplyOdometers()
        {
            var save = TDU2Session.Save;
            if (save?.odometers == null) return;
            foreach (var o in save.odometers)
                PlayerPrefs.SetString("TDU2.Odo." + o.carId, o.meters.ToString("R", CultureInfo.InvariantCulture));
        }

        void OnApplicationQuit()
        {
            if (!TDU2Session.LoggedIn) return;
            try
            {
                CaptureState();
                TDU2Session.SaveAsync().Wait(3000);
                TDU2Session.Logout();
            }
            catch (Exception) { }
        }

        void OnDestroy()
        {
            if (Active && _st != St.Playing) Active = false;
        }

        static void QuitGame()
        {
            Active = false;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
