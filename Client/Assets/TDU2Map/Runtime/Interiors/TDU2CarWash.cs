using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using TDU2.Avatars;
using TDU2.Flash;
using TDU2.Formats;
using TDU2.Online;
using TDU2.Story;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Waschanlage (Spot ECW_*, Spielmodus CAR_WASH) wie im Spiel (Ablauf aus TestDrive2.exe: Menü 0xBFC280, "cwok" 0xBFC160,
    /// Kino-Ende 0xBFCE90, Babes starten/stoppen 0xBFC780/0xBFBF80):
    ///   C_INTRO_E_1 -> Menü + C_LOOP (Schleife) -> A/"cwok": bezahlen, sofort C_WASHING (Ferrari: C_OUTRO_FERRARI)
    ///   -> Ende C_WASHING: C_INCAR (Blick aus dem Auto, ohne Kino-Kamera) + Babes C_BABE_01..03 am Auto
    ///   -> Ende C_INCAR: Babes stoppen, C_OUTRO -> Ende C_OUTRO/C_OUTRO_FERRARI: zurück in die Welt.
    ///   B: Menü zu ("gofr") -> C_OUTRO. Musik kommt aus den .cin-Fx (C_WASHING/C_INCAR/C_OUTRO); Figuren blendet die Szene selbst ein/aus.
    /// Waschanlage (Spot ECW_*, Spielmodus CAR_WASH) wie im Spiel. Alles stammt aus der Exterior-Bank des Spots
    /// (islands/&lt;insel&gt;/exterior/ecw_a.bnk, Quelle spots/carwash/cw_a): entities.xmb mit den Kinos
    ///   C_INTRO_E_1 / C_INTRO_E_2  Einfahrt (Kamera, PLAYER_CAR, Avatar steigt aus) – zufällig eins von beiden
    ///   C_LOOP                     Kameraschleife hinter dem Menü, dazu C_INCAR (Avatar im Auto, ohne Kamera)
    ///   C_WASHING                  Waschen durch die drei Babes (Schwamm, Schlauch, Radio)
    ///   C_OUTRO                    Ende (Babes räumen auf, Auto fährt raus)
    ///   C_BABE_01..03              Babes im Hintergrund (ohne Kamera, Schleife) während Einfahrt und Menü
    /// Menü = Original-Film frontend/&lt;res&gt;/shopscar.bnk ab Label "car_wash": FSCommand "wain" (Start), Preis /:wash_price,
    /// /:CanWash, Geld /:MyMoneyStr; A -> "cwok" (bezahlt, Geld-Animation), B -> "car_wash_out" -> "gofr".
    /// Preis = WashingPrice (Spielwerte-Tabelle: 1500 $). Nach dem Waschen ist der Schmutz weg (<see cref="TDU2CarDirt"/>),
    /// während C_WASHING nimmt er am Vorführauto sichtbar ab. Danach steht das Auto an der Ausfahrt des Spots (Position2).
    /// Szene: <see cref="TDU2StoryScene"/> mit der Exterior-Bank (die gestreamte Anlage wird so lange ausgeblendet).
    /// </summary>
    [DefaultExecutionOrder(1100)]   // nach TDU2StoryScene (1000): Innenkamera während C_INCAR
    public sealed class TDU2CarWash : MonoBehaviour
    {
        public TDU2VehicleManager Manager;
        [Tooltip("Preis einer Wäsche in $ (Spielwerte: WashingPrice = 1500)")]
        public long Price = 1500;
        [Tooltip("Einfahrt/Ende mit Enter/Esc überspringen")]
        public bool AllowSkip = true;

        enum Phase { None, Loading, Intro, Menu, Washing, InCar, Outro }

        static TDU2CarWash _active;
        /// <summary>Waschanlage läuft (Pausemenü/Karte/Spot-Eingang gesperrt).</summary>
        public static bool IsActive => _active != null && _active._phase != Phase.None;

        Phase _phase;
        Spot _spot;
        CarPhysics _car;
        float _dirt, _dirtStart, _scratch;
        bool _washed, _paid;
        TDU2StoryScene _scene;
        GameObject _carGo; CarModel _model;
        Task<CarAssets> _carTask;
        float _loadT, _menuAt;
        string _intro;
        VmfPlayer _film;
        bool _menuOpen;
        float _gofrAt = -1f;
        CursorLockMode _savedLock; bool _savedCursor;

        static readonly string[] Babes = { "C_BABE_01", "C_BABE_02", "C_BABE_03" };

        /// <summary>Exterior-Bank des Spots (absoluter Pfad), null = keine.</summary>
        public static string ExteriorBank(TDU2VehicleManager mgr, Spot s)
        {
            var dir = mgr?.Streamer?.Index?.ExteriorDir;
            if (dir == null || s == null || string.IsNullOrEmpty(s.Code)) return null;
            var f = Path.Combine(dir, s.Code.ToLowerInvariant() + ".bnk");
            return File.Exists(f) ? f : null;
        }

        /// <summary>Waschanlage an Spot s starten (aus TDU2Interiors an der Einfahrt mit Enter).</summary>
        public bool Begin(Spot s)
        {
            if (IsActive || s == null || Manager == null) return false;
            string bank = ExteriorBank(Manager, s);
            if (bank == null) { DebugManager.Warning("[TDU2] Waschanlage: Bank fehlt für " + s.Code); return false; }
            var cur = Manager.Current;
            _car = cur != null ? cur.Car : null;
            if (_car == null) { DebugManager.Warning("[TDU2] Waschanlage: nur mit Auto"); return false; }
            var dirt = cur.GetComponentInChildren<TDU2CarDirt>();
            _dirt = _dirtStart = dirt != null ? dirt.Level : TDU2CarDirt.GetSaved(_car);
            _scratch = dirt != null ? dirt.Scratches : 0f;
            _spot = s; _washed = false; _paid = false; _loadT = 0f; _gofrAt = -1f;
            _active = this;
            _phase = Phase.Loading;
            Manager.RemoveCar();
            Manager.Suspended = true;
            TDU2GameMode.Push(Gamemode.GAME_MODE_CAR_WASH);
            // Bank als absoluter Pfad (Path.Combine(BnkDir, abs) = abs); genau dieser Spot (mehrere Anlagen teilen sich ECW_A)
            _scene = TDU2StoryScene.Create(Manager, new TDU2StoryScene.Setup
            {
                Bank = bank, SpotRef = s, Spot = s.Code, HideSpotInterior = true, LoadUi = false, IntroUi = false,
            });
            int color = 0, rim = 0, interior = 0;
            if (TDU2CarChoices.TryGet(_car.FileName, out var ch)) { color = ch.Color; rim = ch.Rim; interior = ch.Interior; }
            string bnk = Manager.BnkDir; var db = Manager.Database; var car = _car;
            _carTask = Task.Run(() => CarAssets.Load(bnk, db, car, color, false, rim, interior));
            TDU2DebugLog.Event($"Waschanlage {s.Code}: Auto {_car.FileName}#{_car.Id}, Schmutz {_dirt:0.00}");
            return true;
        }

        void Update()
        {
            if (_phase == Phase.None) return;
            if (_scene == null || _scene.Failed) { DebugManager.Warning("[TDU2] Waschanlage: Szene fehlgeschlagen" + (_scene != null ? " – " + _scene.Error : "")); Finish(); return; }
            PollCar();
            float dt = Time.deltaTime;
            switch (_phase)
            {
                case Phase.Loading: TickLoading(dt); break;
                case Phase.Intro:
                    if (AllowSkip && TDU2Story.SkipPressed()) _scene.SkipMain();
                    foreach (var n in _scene.TakeEndEvents()) if (n == _intro) StartLoop();
                    break;
                case Phase.Menu: TickMenu(); break;
                case Phase.Washing:
                case Phase.InCar:
                    {
                        if (_menuOpen && _gofrAt > 0f && Time.unscaledTime >= _gofrAt) { _gofrAt = -1f; CloseMenu(); }
                        // Schmutz nimmt über C_WASHING + C_INCAR ab (am Ende von C_INCAR ganz weg)
                        _washT += dt;
                        _dirt = Mathf.Lerp(_dirtStart, 0f, Mathf.Clamp01(_washT / Mathf.Max(1f, _washLen)));
                        ApplyDirt();
                        foreach (var n in _scene.TakeEndEvents())
                        {
                            if (n == "C_WASHING") StartInCar();
                            else if (n == "C_INCAR") StartOutro();
                        }
                        break;
                    }
                case Phase.Outro:
                    if (AllowSkip && TDU2Story.SkipPressed()) _scene.SkipMain();
                    if (_menuOpen && _gofrAt > 0f && Time.unscaledTime >= _gofrAt) { _gofrAt = -1f; CloseMenu(); }
                    foreach (var n in _scene.TakeEndEvents()) if (n == "C_OUTRO" || n == "C_OUTRO_FERRARI") { Finish(); return; }
                    break;
            }
        }

        float _washT, _washLen = 30f;

        void PollCar()
        {
            if (_carTask == null || !_carTask.IsCompleted) return;
            var t = _carTask; _carTask = null;
            if (t.Status != TaskStatus.RanToCompletion || t.Result == null) { DebugManager.Error("[TDU2] Waschanlage: Auto " + _car?.FileName + ": " + t.Exception?.GetBaseException().Message); return; }
            try
            {
                _carGo = new GameObject("CarWash " + _car.ModelName);
                _model = CarModel.Build(t.Result, _carGo.transform);
                TDU2WheelSpin.Attach(_carGo, _model, t.Result);
                // eigene Beklebung (Sticker-Shop) wie am gefahrenen Auto
                try { TDU2StickerLibrary.Ensure(Manager.BnkDir); TDU2LiveryView.ApplyOwn(_model, _car.FileName); }
                catch (Exception ex) { DebugManager.Warning("[TDU2] Waschanlage: Beklebung: " + ex.Message); }
                ApplyDirt();
            }
            catch (Exception ex) { DebugManager.Error("[TDU2] Waschanlage: Auto " + _car.FileName + ": " + ex.Message); }
        }

        /// <summary>Schmutz am Vorführauto (dirt_scratch über die Lack-Materialien, wie TDU2CarDirt).</summary>
        void ApplyDirt()
        {
            if (_model == null) return;
            foreach (var m in _model.PaintMaterials)
            {
                if (m == null || !m.HasProperty("_DirtLevel")) continue;
                m.SetFloat("_DirtLevel", _dirt);
                if (m.HasProperty("_ScratchLevel")) m.SetFloat("_ScratchLevel", _washed || _phase == Phase.Washing ? 0f : _scratch);
            }
        }

        void TickLoading(float dt)
        {
            _loadT += dt;
            _scene.SetFade(1f, Color.black);
            if (!_scene.Loaded) return;
            if (_carGo == null && _carTask != null && _loadT < 20f) return;
            if (!_sceneReady)
            {
                _sceneReady = true;
                FixSceneryRoot();
                string cfg = TDU2StoryProgress.IntroAvatar(TDU2Session.Nick);
                if (string.IsNullOrEmpty(cfg)) cfg = AvatarConfigs.Local != null && AvatarConfigs.Local.Sex == 1 ? "AVT_Female_Intro" : "AVT_Male_Intro";
                _scene.SetPlayerAvatar(cfg, AvatarConfigs.Local);
                if (_carGo != null) _scene.SetCar(_carGo.transform, null, "PLAYER_CAR");
            }
            if (!Manager.Streamer.ReadyAround(_scene.Holder.position, 200f) && _loadT < 20f) return;
            _scene.SetFade(0f, Color.black);
            // Spiel (0xAB24C0): C_INTRO_E_1, C_INTRO_E_2 nur bei einem besonderen Fahrzeug-Merkmal (unbekannt) -> E_1
            _intro = _scene.Find("C_INTRO_E_1") != null ? "C_INTRO_E_1" : "C_INTRO_E_2";
            if (!_scene.PlayMain(_intro)) { StartLoop(); return; }
            _phase = Phase.Intro;
        }

        bool _sceneReady;

        /// <summary>
        /// Wurzelknoten "ECW_A" der Anlage liegt in der Szene versetzt (wie im Exterior: TDU2MapStreamer.specialObjects setzt ihn
        /// dort auf den Ursprung) – hier genauso, sonst steht die Anlage neben der gestreamten Welt.
        /// </summary>
        void FixSceneryRoot()
        {
            if (_scene?.Holder == null) return;
            foreach (var t in _scene.Holder.GetComponentsInChildren<Transform>(true))
                if (string.Equals(t.name, "ECW_A", StringComparison.OrdinalIgnoreCase) && t.localPosition != Vector3.zero)
                {
                    TDU2Diag.Log("WASCH", "Wurzel ECW_A von " + TDU2Diag.F(t.localPosition) + " auf den Ursprung");
                    t.localPosition = Vector3.zero;
                }
        }

        // ------------------------------------------------------------------ Menü (shopscar.vmf "car_wash")
        void StartLoop()
        {
            _phase = Phase.Menu;
            _scene.PlayMain("C_LOOP");
            _menuAt = Time.unscaledTime;
            _menuOpen = false;
        }

        void TickMenu()
        {
            _scene.TakeEndEvents();
            if (!_menuOpen && Time.unscaledTime >= _menuAt) OpenMenu();
            if (_gofrAt > 0f && Time.unscaledTime >= _gofrAt) { _gofrAt = -1f; MenuDone(); return; }   // Rückfall, falls "gofr" ausbleibt
            if (!_menuOpen || _film == null) return;
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.E)) _film.Input("A");
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace)) { if (!_film.Input("B")) MenuDone(); else _gofrAt = Time.unscaledTime + 2.5f; }
#endif
        }

        static string MoneyStr(long v) => "$ " + v.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');

        bool LoadFilm()
        {
            if (_film != null) return true;
            string bank = null;
            foreach (var res in new[] { "hires", "medres", "lowres" })
            {
                bank = CarDatabase.FindFile(Path.Combine(Manager.BnkDir, "frontend", res), "shopscar.bnk");
                if (bank != null) break;
            }
            if (bank == null) { DebugManager.Warning("[TDU2] Waschanlage: shopscar.bnk fehlt"); return false; }
            try
            {
                var go = new GameObject("TDU2 CarWash UI");
                go.transform.SetParent(transform, false);
                _film = go.AddComponent<VmfPlayer>();
                _film.GuiDepth = -2;
                _film.MouseEnabled = true;
                _film.Load(bank);
                _film.Visible = false;
                _film.FSCommand += OnFilmCommand;
                return true;
            }
            catch (Exception ex)
            {
                DebugManager.Warning("[TDU2] Waschanlage: shopscar.bnk: " + ex.Message);
                if (_film != null) { Destroy(_film.gameObject); _film = null; }
                return false;
            }
        }

        void OpenMenu()
        {
            _menuOpen = true;
            if (!LoadFilm()) { Wash(); return; }   // ohne Film: gleich waschen
            ApplyTexts();
            SetMenuValues();
            _film.SetValue("/:fake", false);
            _film.SetValue("/:WhatPlatform", 1);
            _film.SetVariable("/:MenuType", "pc");
            _film.SetValue("/:Input", true);
            _film.Visible = true;
            _film.GotoLabel("/", "car_wash", true);
            _savedLock = Cursor.lockState; _savedCursor = Cursor.visible;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            TDU2Diag.Log("WASCH", $"Menü: Preis {Price} $, Geld {(TDU2Session.Save != null ? TDU2Session.Save.money : -1)}, Schmutz {_dirt:0.00}");
        }

        void ApplyTexts()
        {
            try { FlashLocalization.Apply(_film, FlashLocalization.Load(Manager.BnkDir, TDU2PauseMenu.SavedLanguage())); } catch (Exception) { }
        }

        bool CanPay => TDU2Session.Save == null || TDU2Session.Save.money >= Price;

        void SetMenuValues()
        {
            _film.SetVariable("/:wash_price", MoneyStr(Price));
            _film.SetValue("/:CanWash", CanPay);
            _film.SetVariable("/:MyMoneyStr", TDU2Session.Save != null ? MoneyStr(TDU2Session.Save.money) : " ");
            _film.SetVariable("/:ClubMoney", " ");
        }

        void CloseMenu()
        {
            if (!_menuOpen) return;
            _menuOpen = false;
            if (_film != null) { _film.Visible = false; _film.GotoLabel("/", "default", false); }
            Cursor.lockState = _savedLock; Cursor.visible = _savedCursor;
        }

        void OnFilmCommand(string cmd, string args)
        {
            string c = (cmd ?? "").Trim().ToLowerInvariant();
            if (TDU2FrontendSounds.Command(c)) return;
            TDU2Diag.Log("WASCH", "FSCommand " + c + (string.IsNullOrEmpty(args) ? "" : ":" + args));
            switch (c)
            {
                case "sbdd": ApplyTexts(); SetMenuValues(); break;
                case "wain": SetMenuValues(); break;                 // Menü aufgebaut
                case "cwok": Pay(); _gofrAt = Time.unscaledTime + 4f; Wash(); break;   // bezahlt: sofort waschen (Spiel 0xBFC160)
                case "gofr": _gofrAt = -1f; if (_phase == Phase.Menu) MenuDone(); else CloseMenu(); break;   // Film zu
                case "dbxo":
                    // "cant_wash_car" (zu wenig Geld): Hinweis bestätigen
                    _film.SetVariable("/:PlayerAnswer", "a");
                    _film.CallClip("/", "DialogBox");
                    break;
            }
        }

        void Pay()
        {
            if (_paid) return;
            _paid = true;
            var save = TDU2Session.Save;
            if (save != null)
            {
                save.money = Math.Max(0, save.money - Price);
                if (_film != null) _film.SetVariable("/:MyMoneyStr", MoneyStr(save.money));
                if (TDU2Session.LoggedIn) TDU2Session.SaveAsync();
            }
            TDU2DebugLog.Event($"Waschanlage: bezahlt {Price} $");
        }

        void MenuDone()
        {
            if (_phase != Phase.Menu) return;
            CloseMenu();
            if (_paid) Wash(); else StartOutro();
        }

        void Wash()
        {
            if (_phase != Phase.Menu) return;
            _washT = 0f; _dirtStart = _dirt;
            _scene.StopBackground();
            // Ferrari: keine Wäsche durch die Babes, nur C_OUTRO_FERRARI (Spiel: Fahrzeug-Merkmal, hier Marke Ferrari)
            if (IsFerrari && _scene.Find("C_OUTRO_FERRARI") != null)
            {
                _washed = true; _dirt = 0f; ApplyDirt();
                _phase = Phase.Outro;
                _scene.PlayMain("C_OUTRO_FERRARI");
                return;
            }
            _phase = Phase.Washing;
            var dw = _scene.Find("C_WASHING"); var di = _scene.Find("C_INCAR");
            _washLen = (dw?.Cin != null ? dw.Cin.Duration : 15f) + (di?.Cin != null ? di.Cin.Duration : 10f);
            if (!_scene.PlayMain("C_WASHING")) StartInCar();
        }

        bool IsFerrari => _car != null && ((_car.Brand ?? "").IndexOf("ferrari", StringComparison.OrdinalIgnoreCase) >= 0
                                          || (_car.FileName ?? "").StartsWith("ferrari", StringComparison.OrdinalIgnoreCase));

        /// <summary>Nach C_WASHING: Blick aus dem Auto (C_INCAR ohne Kino-Kamera), Babes waschen ringsum (C_BABE_01..03).</summary>
        void StartInCar()
        {
            _phase = Phase.InCar;
            foreach (var b in Babes) _scene.PlayBackground(b, true);
            if (!_scene.PlayMain("C_INCAR")) StartOutro();
        }

        // Blick aus dem Auto während C_INCAR (das Spiel schaltet dort auf die Innenkamera)
        void LateUpdate()
        {
            if (_phase != Phase.InCar || _carGo == null) return;
            var cam = Camera.main;
            if (cam == null) return;
            var car = _carGo.transform;
            Vector3 fwd = car.forward, up = car.up;
            Vector3 eye;
            var steer = _model != null ? _model.SteeringWheel : null;
            if (steer != null) eye = steer.position + up * 0.32f - fwd * 0.5f;
            else eye = car.position + up * 1.15f - fwd * 0.25f;
            cam.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(fwd - up * 0.08f, up));
            cam.fieldOfView = 70f;
        }

        void StartOutro()
        {
            if (_phase == Phase.Washing || _phase == Phase.InCar) { _washed = true; _dirt = 0f; ApplyDirt(); }
            _phase = Phase.Outro;
            _scene.StopBackground();
            if (!_scene.PlayMain("C_OUTRO")) Finish();
        }

        // ------------------------------------------------------------------ Musik (Fx-Wurzeln der Bank: M_WASHING_INTRO, M_WASHING, M_WASHING_OUTRO)
        System.Collections.Generic.List<AudioSource> _music;

        /// <summary>Musik-Wurzel direkt abspielen (derzeit ungenutzt: die Musik steckt als Fx-Ereignis in den .cin – C_WASHING: M_WASHING_INTRO,
        /// C_INCAR: M_WASHING, C_OUTRO: M_WASHING_OUTRO; Einfahrt und Menü sind im Spiel ohne Musik).</summary>
        void PlayMusic(string root, bool loop)
        {
            StopMusic();
            var au = _scene != null ? _scene.Audio : null;
            if (au == null) return;
            ulong hc = au.FindRootByName(root);
            if (hc == 0) { TDU2Diag.Log("WASCH", "Musik " + root + " nicht gefunden"); return; }
            _music = au.PlayRoot(hc, 0f, loop, null, TDU2.Cinematics.TDU2CinematicAudio.Flag2D);
            TDU2Diag.Log("WASCH", $"Musik {root}: {(_music != null ? _music.Count : 0)} Spur(en){(loop ? ", Schleife" : "")}");
        }

        void StopMusic()
        {
            if (_music != null) TDU2.Cinematics.TDU2CinematicAudio.Stop(_music);
            _music = null;
        }

        // ------------------------------------------------------------------ Ende
        void Finish(bool spawn = true)
        {
            if (_phase == Phase.None) return;
            _phase = Phase.None;
            StopMusic();
            CloseMenu();
            if (_washed) TDU2CarDirt.SetSaved(_car, 0f, true);
            if (_carGo != null) Destroy(_carGo);
            _model?.Destroy(); _model = null; _carGo = null; _carTask = null;
            if (_scene != null) _scene.Dispose();   // Unity-Vergleich: beim Beenden kann die Szene schon zerstört sein
            _scene = null;
            _sceneReady = false;
            if (spawn) TDU2GameMode.Pop();   // beim Beenden des Spiels nicht mehr (Instanz ggf. schon weg)
            if (Manager != null) Manager.Suspended = false;
            var s = _spot; _spot = null;
            if (_active == this) _active = null;
            if (!spawn || s == null || _car == null || Manager == null || Manager.Streamer == null || Manager.Streamer.MapRoot == null) return;
            // Auto an der Ausfahrt des Spots (wie beim Verlassen eines Interieurs)
            var root = Manager.Streamer.MapRoot;
            var gp = s.Position2.sqrMagnitude > 1f ? s.Position2 : s.Position;
            var p = root.TransformPoint(gp);
            var fwd = root.TransformDirection(new Vector3(Mathf.Sin(s.Heading2), 0f, Mathf.Cos(s.Heading2)));
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            p.y = Manager.GroundHeight(p) + 0.4f;
            var cam = Camera.main;
            if (cam != null) cam.transform.SetPositionAndRotation(p - fwd * 7f + Vector3.up * 2.5f, Quaternion.LookRotation(fwd, Vector3.up));
            Manager.SpawnCar(_car, p, Quaternion.LookRotation(fwd, Vector3.up));
            TDU2DebugLog.Event($"Waschanlage {s.Code} verlassen: {(_washed ? "gewaschen" : "abgebrochen")}");
        }

        void OnDestroy()
        {
            if (_phase != Phase.None) Finish(false);   // Spiel/Szene wird beendet: nur aufräumen
            if (_active == this) _active = null;
        }
    }
}
