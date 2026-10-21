using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using TDU2.Map.Flash;
using TDU2.Map.Formats;
using TDU2.Map.Online;
using TDU2.Map.Racing;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Autohändler wie im Spiel (shopscar.vmf, WhatPage "car_dealer"):
    ///  * Auto anvisieren + E: _root.SetMiniMenu() (SelItemType "vehicle", Film geht nach "cd_buying") -> Mini-Menü
    ///    Kaufen / Einsteigen / Daten / Vergleichen / Testfahrt. Zurück in den Laufmodus: BackToWM -> "cd_stop".
    ///  * Kaufen: /buy "car_action" (Fenster window_buy) meldet buin + neop. Mit _root.fake = 0 schaltet das SPIEL die Seiten:
    ///    A auf Seite n schickt neop -> Spiel setzt num_option = n + 1 und spielt /buy "next" (-> options_stop, Fenster neu
    ///    befüllt); der Film blendet danach selbst über window_buy "transition_1to2"/"transition_2to3" um. Auf Seite 3 ->
    ///    Spiel spielt /buy "confirm" (TestMoney -> Dialog confirm_buy -> BuyVehicle -> buyc). B schickt prop -> Seite zurück
    ///    (auf Seite 1 schließt das Fenster selbst, /buy steht dann schon auf "close").
    ///    Seiten: 1 Felgen, 2 Lack, 3 Innenraum. Vorschaubild (window_buy/lure) = Kamera auf Rad / Auto / Cockpit.
    ///  * Einsteigen: enter_car "enter" -> rqca (Spiel lädt, setzt _root.CanEnterCar) -> cain/shin, Hilfeleiste "in_car"
    ///    (Rückblick, Hupe, Anlasser). Im Auto: Maus umsehen, Anlasser, Gas im Stand, Hupe, Rückblick, Zubehör (Verdeck,
    ///    Fenster, Blinker im HUD-Menü). B: ExitVehicle -> caou, mcou, BackToWM.
    ///  * Testfahrt: tstk -> Auto mit der gewählten Ausstattung draußen fahren, Restzeit im HUD (/HUD/time_cd), danach zurück
    ///    in den Händler (Original: 2 Minuten).
    ///  * Vergleichen (Mini-Menü "compare"): Film spielt car_datas "cp_in" und schickt comp -> das Auto wird Vergleichsauto
    ///    (_root.comp_* / Comp_car, _root.IsCompare); jedes danach angesehene Auto zeigt SetDatasCp (beide Werte). "reset_compare"
    ///    -> rsco hebt den Vergleich auf. Der Film schließt das Mini-Menü danach selbst (B -> Laufmodus).
    ///  * Garage (home.vmf, gleiche Abläufe): _root.SetMenu() mit SelItemType "vehicle" (AmIOwner: Losfahren, Einsteigen, Daten,
    ///    Vergleichen; Gäste: Einsteigen, Daten, Vergleichen). Losfahren -> Dialog drive_out -> Label garage_out -> mit dem Auto hinaus.
    ///  * Türen/Fenster: im Laufmodus Tür/Scheibe anvisieren + Rechtsklick (bzw. Türen-/Fenster-Taste); im Auto Tür/Scheibe
    ///    anvisieren + Linksklick, Lenkrad = Hupe; Hinweis über die Cursor-Leiste des Films. Mitspieler im selben Raum sehen es
    ///    (TDU2Multiplayer, Erweiterung 0xA2). Hilfeleiste "in_car": star (Anlasser), honk/hoof (Hupe), bcvi/ftvi (Rückblick).
    /// ESC/Rücktaste gehen über den zentralen Menüablauf (TDU2MenuFlow.cs: DealerBack / DealerForceClose).
    /// </summary>
    public sealed partial class TDU2Interiors
    {
        [Tooltip("Dauer der Testfahrt beim Autohändler (Sekunden Spielzeit)")]
        public float TestDriveSeconds = 120f;

        /// <summary>true während einer Testfahrt (Leihwagen wird nicht gespeichert).</summary>
        public static bool TestDriveActive { get; private set; }

        const string BuyWin = "/buy/window_buy";
        const string BuyLure = BuyWin + "/lure";
        static readonly string[] DealerClips = { "cursor", "title_bar", "car_datas", "price", "help" };   // /buy erst mit dem Kauffenster (buin)

        bool _dealerMenu;
        float _dealerMenuAt;
        DisplayCar _dealerCar;
        readonly List<RimInfo> _dRims = new List<RimInfo>();
        readonly List<CarColor> _dColors = new List<CarColor>();
        readonly List<InteriorInfo> _dInts = new List<InteriorInfo>();
        int _dRim, _dColor, _dInt;
        CarChoice _dOrig;
        bool _dBought;
        float _driveOutAt = -1f;
        CarPhysics _driveOutCar;
        readonly Dictionary<string, Color32> _dTints = new Dictionary<string, Color32>();

        Task<CarAssets> _previewTask;
        DisplayCar _previewFor;
        CarChoice _previewWant;
        bool _previewDirty;
        float _previewAt;

        float _testEnd;
        Spot _testSpot;
        CarPhysics _testCar, _testPrev;
        bool _testHud;
        int _testLastSec = -1;

        CarPhysics _compareCar;          // Vergleichsauto (Mini-Menü "Vergleichen"), bis rsco oder Verlassen
        bool _dChanged;                  // im Kauffenster Ausstattung geändert -> beim Abbrechen zurücksetzen
        DisplayCar _netCar;              // Auto, an dem man zuletzt Türen/Fenster bedient hat (Online: Mitspieler im Raum)
        bool _garageMenu;                // Menü der Garage (home.vmf) statt des Händlers (shopscar.vmf)
        string CarMenuStop => _garageMenu ? "garage_stop" : "cd_stop";

        /// <summary>
        /// Ablauf des Fahrzeugmenüs (Händler und Garage gleich). Die Phase wird JEDES Bild aus dem Zustand des Films bestimmt
        /// (welche Ebene ist offen?) – keine Zeitgeber, keine Annahmen über Label der Hauptzeitleiste:
        ///   Mini     = /cursor/mini_menu nicht auf "default" (öffnet, steht, schließt – danach ruft der Film ValidMenu)
        ///   Window   = Kauffenster /buy (Felgen/Lack/Innenraum)
        ///   Datas    = Fahrzeugdaten-Vollansicht /car_datas fd_* (B -> zurück ins Mini-Menü)
        ///   Car      = Einsteigen (/enter_car lädt bzw. man sitzt im Auto)
        ///   DriveOut = gekauft bzw. "Losfahren" bestätigt -> mit dem Auto hinaus
        ///   Idle     = nichts mehr offen. Bleibt das kurz so (und wartet keine Aktion mehr auf das Spiel, z.B. Testfahrt), ist
        ///              das Menü zu Ende -> Laufmodus. So kommt jeder Weg (B, Vergleichen, Fenster zu, Aussteigen …) sicher zurück.
        /// </summary>
        enum DPhase { Opening, Mini, Window, Datas, Car, DriveOut, Idle }
        DPhase _dPhase;
        float _dPhaseAt, _dIdleSince = -1f;

        void SetDealerPhase(DPhase p)
        {
            if (_dPhase == p) return;
            Debug.Log("[TDU2] " + (_garageMenu ? "Garage" : "Autohändler") + ": " + _dPhase + " -> " + p + (p == DPhase.Idle ? " (BtValid " + (_ui?.GetVariable("/:BtValid") ?? "") + ")" : ""));
            _dPhase = p; _dPhaseAt = Time.unscaledTime;
        }

        static bool OpenLabel(string l) => !string.IsNullOrEmpty(l) && l != "default";

        bool DealerCarOpen => _carIn != CarIn.None || (_ui != null && OpenLabel(_ui.Label("/enter_car")) && _ui.Label("/enter_car") != "close");
        bool DealerDatasOpen
        {
            get
            {
                string l = _ui != null ? _ui.Label("/car_datas") ?? "" : "";
                return l.StartsWith("fd_") || l == "data_back";
            }
        }

        DPhase ProbeDealerPhase()
        {
            if (_driveOutAt > 0f) return DPhase.DriveOut;
            if (DealerCarOpen) return DPhase.Car;
            if (DealerWindowOpen) return DPhase.Window;
            if (DealerDatasOpen) return DPhase.Datas;
            if (DealerMiniOpen) return DPhase.Mini;
            return _dPhase == DPhase.Opening && Time.unscaledTime - _dPhaseAt < 1.5f ? DPhase.Opening : DPhase.Idle;
        }

        /// <summary>Wartet die gewählte Aktion noch auf einen Befehl des Films (Testfahrt -> tstk am Ende von cd_close)?</summary>
        bool DealerActionPending()
        {
            string bt = _ui?.GetVariable("/:BtValid") ?? "";
            float t = Time.unscaledTime - _dIdleSince;
            if (bt == "test") return t < 3f;
            if (bt == "buy") return t < 1f;      // Kauffenster öffnet nach der Schließanimation des Mini-Menüs
            if (bt == "enter") return t < 1f;
            if (bt == "select") return t < 2f;   // Garage: Dialog "Losfahren"
            return false;
        }

        // ================================================================== Menü öffnen/schließen
        /// <summary>Mini-Menü des Films für ein Händlerauto öffnen; false, wenn der Film es nicht kann (dann Direktkauf wie bisher).</summary>
        bool OpenDealerMenu(DisplayCar dc)
        {
            if (_ui == null || dc == null || dc.Car == null || Manager?.Database == null) return false;
            bool garage = _kind == Kind.Garage;
            string fn = garage ? "SetMenu" : "SetMiniMenu";   // home.vmf: _root.SetMenu, shopscar.vmf: _root.SetMiniMenu
            if (_ui.FunctionOwners(fn).Count == 0) { Debug.LogWarning("[TDU2] " + (garage ? "Garage" : "Autohändler") + ": " + fn + " fehlt im Film"); return false; }
            _garageMenu = garage;
            _dealerMenu = true; _dealerMenuAt = Time.unscaledTime; _dealerCar = dc; _dBought = false; _dChanged = false;
            _dPhase = DPhase.Opening; _dPhaseAt = Time.unscaledTime; _dIdleSince = -1f;
            CasinoMenuOpen = true;   // sperrt das Pausemenü (ESC gehört dem Film)
            if (_fp != null) _fp.LookEnabled = false;
            LockCursor(false);
            try { FlashLocalization.Apply(_ui, FlashLocalization.Load(Manager.BnkDir, TDU2PauseMenu.SavedLanguage())); } catch (Exception) { }
            if (!garage) PrepareOptions(dc);
            else { _dRims.Clear(); _dColors.Clear(); _dInts.Clear(); _dOrig = dc.Choice; }
            _ui.MouseEnabled = true;
            _ui.SetValue("/:WhatPlatform", 1);
            _ui.SetVariable("/:MenuType", "pc");
            _ui.SetValue("/:IsPC_X360", true);
            _ui.SetValue("/:fake", 0);
            _ui.SetValue("/:Input", true);
            if (!garage) _ui.SetVariable("/:WhatPage", "car_dealer");
            _ui.SetVariable("/:SelItemType", "vehicle");
            bool bike = IsBike(dc.Car);
            _ui.SetVariable("/:state_Vehicle", bike ? "bike" : "car");
            _ui.SetValue("/:IsBike", bike);
            _ui.SetValue("/:IsPassenger", false);
            _ui.SetValue("/:CanEnterCar", false);
            _ui.SetValue("/:IsRewardCar", false);
            _ui.SetValue("/:DLC_Locked", false);
            _ui.SetValue("/:IsCompare", _compareCar != null);
            if (garage)
            {
                // Garage: eigene Autos (Losfahren), Gäste nur Einsteigen/Daten/Vergleichen; Stellplatz tauschen gibt es hier nicht
                _ui.SetValue("/:FromClub", false);
                _ui.SetValue("/:AmIOwner", GuestGarageCars == null);
                _ui.SetValue("/:EnableChangePlace", false);
                _ui.SetValue("/:IsBache", false);
                _ui.SetValue("/:LockBorrowVehicle", false);
                _ui.SetValue("/:CanBuyVehicle", false);
            }
            _ui.SetValue("/:IsClassicDealer", 0);
            _ui.SetValue("/:HaveBought", false);
            _ui.SetValue("/:state_Demo", 0);
            _ui.SetValue("/:state_demo", 0);
            _ui.SetValue("/:IsTutorial", false);
            _ui.SetVariable("/:AfterValid", "");
            _ui.SetVariable("/:BtValid", "");
            foreach (var n in DealerClips) _ui.SetVisible("/" + n, true);
            if (!garage) UpdateTotals();
            // Wie ItemType("valid") im Spiel für ein Fahrzeug: Fahrzeugdaten/Preis zu, Hilfeleiste zu, dann SetMiniMenu (Händler,
            // spielt auch Label cd_buying) bzw. SetMenu (Garage). Beide öffnen /cursor "mini_menu" – dessen Bild 50 setzt
            // _root.Clip = cursor, darüber schließt der Film das Menü später selbst (Vergleichen: Clip.B()).
            // Direkt statt über cursor.A(): das hing an _root.Input und dem Cursor-Label (Ursache der Hänger).
            _ui.SetValue("/:ItemInfo", false);
            _ui.GotoLabel("/car_datas", "default", false);
            if (!garage) _ui.GotoLabel("/price", "default", false);
            _ui.CallClip("/", "SetHelp", "close");
            _ui.SetValue("/:Input", true);
            if (!_ui.CallClip("/", fn)) { _dealerMenu = false; CasinoMenuOpen = false; LockCursor(true); return false; }
            _ui.SetVisible("/cursor/mini_menu", true);
            _ui.SetValue("/:Input", true);
            if (garage) Debug.Log("[TDU2] Garage: Menü für " + dc.Car.FileName + (GuestGarageCars != null ? " (Gast)" : ""));
            else Debug.Log("[TDU2] Autohändler: Menü für " + dc.Car.FileName + " (" + _dRims.Count + " Felgen, " + _dColors.Count + " Lackfarben, " + _dInts.Count + " Innenräume)");
            return true;
        }

        static bool IsBike(CarPhysics car)
        {
            int cat = car?.Raw != null ? car.Raw.I("_vehiclecategory") : 0;
            return cat >= 18 && cat <= 24;
        }

        /// <summary>Zurück in den Laufmodus (Film steht wieder auf cd_stop).</summary>
        void EndDealerMenu(bool restoreCar = true)
        {
            if (!_dealerMenu) return;
            EndDealerCar();
            EndLure();
            _dealerMenu = false; CasinoMenuOpen = false;
            _dTints.Clear();
            RacingSchoolMenu.NoteUiClosed();
            // nur wenn im Kauffenster etwas geändert wurde (sonst Auto nicht neu aufbauen – es "springt" sonst beim Schließen)
            if (restoreCar && _dChanged && !_dBought && _dealerCar != null && !_garageMenu)
            {
                // Ausstellungsauto wieder wie vorher (Ausstattung beim Blättern nur vorgeführt)
                _dColor = Mathf.Max(0, _dOrig.Color); _dRim = Mathf.Max(0, _dRims.FindIndex(r => r.Id == _dOrig.Rim)); _dInt = Mathf.Max(0, _dInts.FindIndex(i => i.Id == _dOrig.Interior));
                RequestPreview(_dOrig, 0f);
            }
            if (_ui != null)
            {
                _ui.GotoLabel("/cursor/mini_menu", "default", false);
                _ui.GotoLabel("/buy", "default", false);
                _ui.GotoLabel("/enter_car", "default", false);
                // Fahrzeugdaten/Preis zu (nach "Vergleichen" bliebe die Vergleichsanzeige sonst stehen) – beim nächsten Anvisieren
                // öffnet der Film sie wieder (mit IsCompare als Vergleich)
                string cdl = _ui.Label("/car_datas") ?? "";
                if (cdl != "" && cdl != "default") _ui.GotoLabel("/car_datas", "close", true);
                string prl = _ui.Label("/price") ?? "";
                if (prl != "" && prl != "default") _ui.GotoLabel("/price", "close", true);
                _ui.SetValue("/:ItemInfo", false);
                if (_ui.Label("/") != CarMenuStop) _ui.GotoLabel("/", CarMenuStop, false);
                foreach (var d in _ui.Root.List.Values) d.Visible = d.Name != null && UiVisible.Contains(d.Name);
                _ui.GotoLabel("/cursor", "default", false);   // Laufmodus: das nächste Anvisieren spielt wieder "on"
                _ui.SetValue("/:Input", true);
            }
            _uiAim = null; _dChanged = false; _dIdleSince = -1f;
            if (Inside) { LockCursor(true); if (_fp != null) _fp.LookEnabled = true; }
            Debug.Log("[TDU2] " + (_garageMenu ? "Garage" : "Autohändler") + ": Menü geschlossen" + (_compareCar != null ? " (Vergleich mit " + _compareCar.FileName + ")" : ""));
        }

        /// <summary>Beim Verlassen/Neuaufbau der Szene: Händlerzustand ohne Film zurücksetzen.</summary>
        void EndDealerModes()
        {
            EndDealerCar();
            EndLure();
            _dealerMenu = false;
            if (!_casinoMenu) CasinoMenuOpen = false;
            _dealerCar = null; _dTints.Clear();
            _driveOutAt = -1f; _driveOutCar = null;
            _compareCar = null; _dChanged = false; _netCar = null; _garageMenu = false;
            _previewDirty = false; _previewFor = null;
            if (_previewTask != null)
            {
                var t = _previewTask; _previewTask = null;
                t.ContinueWith(x => { if (!x.IsFaulted) MainThreadDestroy(x.Result); });
            }
        }

        static readonly List<CarAssets> _orphanAssets = new List<CarAssets>();
        static void MainThreadDestroy(CarAssets a) { if (a == null) return; lock (_orphanAssets) _orphanAssets.Add(a); }

        void FlushOrphanAssets()
        {
            lock (_orphanAssets)
            {
                foreach (var a in _orphanAssets) a.DestroyTextures();
                _orphanAssets.Clear();
            }
        }

        // ================================================================== Optionen (Felgen, Lack, Innenraum)
        void PrepareOptions(DisplayCar dc)
        {
            var db = Manager.Database; var car = dc.Car;
            int dup = car.Raw != null ? car.Raw.I("_duplicate", 0) : 0;
            _dRims.Clear(); _dColors.Clear();
            if (db.CarRims.TryGetValue(car.Id, out var rl) || (dup != 0 && db.CarRims.TryGetValue(dup, out rl)))
                foreach (int id in rl) if (db.Rims.TryGetValue(id, out var r) && !_dRims.Contains(r)) _dRims.Add(r);
            // Standardfelge zuerst, danach nach Preis
            _dRims.Sort((a, b) => a.Id == car.DefaultRims ? -1 : b.Id == car.DefaultRims ? 1 : a.Price.CompareTo(b.Price));
            if ((db.Colors.TryGetValue(car.Id, out var cols) && cols.Count > 0) || (dup != 0 && db.Colors.TryGetValue(dup, out cols)))
                _dColors.AddRange(cols);
            if (_dColors.Count > 24) _dColors.RemoveRange(24, _dColors.Count - 24);   // der Film hat 24 Farbfelder
            _dColor = dc.Assets?.Color != null ? Mathf.Max(0, _dColors.IndexOf(dc.Assets.Color)) : 0;
            _dRim = dc.Assets?.Rim != null ? Mathf.Max(0, _dRims.IndexOf(dc.Assets.Rim)) : 0;
            // Werkszustand des Ausstellungsautos (Lack 0, Standardfelge, erster Innenraum) – dahin beim Abbrechen zurück
            int ci = _dColor, ri = _dRim;
            _dColor = 0; _dRim = 0; RefreshInteriors(0);
            _dOrig = Choice();
            _dColor = ci; _dRim = ri;
            RefreshInteriors(dc.Assets?.Interior?.Id ?? 0);
        }

        /// <summary>Innenräume der gewählten Lackfarbe (DBCarsAndColors._interior_1..15, 1 = keiner).</summary>
        void RefreshInteriors(int keepId)
        {
            _dInts.Clear();
            var db = Manager.Database;
            if (_dColor >= 0 && _dColor < _dColors.Count)
                foreach (int id in _dColors[_dColor].Interiors)
                    if (id != 1 && id != 0 && db.Interiors.TryGetValue(id, out var ii) && !_dInts.Contains(ii)) _dInts.Add(ii);
            _dInt = Mathf.Max(0, _dInts.FindIndex(i => i.Id == keepId));
        }

        CarChoice Choice() => new CarChoice(_dColor,
            _dRim >= 0 && _dRim < _dRims.Count ? _dRims[_dRim].Id : 0,
            _dInt >= 0 && _dInt < _dInts.Count ? _dInts[_dInt].Id : 0);

        static bool Same(CarChoice a, CarChoice b) => a.Color == b.Color && a.Rim == b.Rim && a.Interior == b.Interior;

        int CarPrice => _dealerCar?.Car?.Raw != null ? Math.Max(0, _dealerCar.Car.Raw.I("_price")) : 0;
        int RimPrice => _dRim >= 0 && _dRim < _dRims.Count ? Math.Max(0, _dRims[_dRim].Price) : 0;
        int PaintPrice => _dColor >= 0 && _dColor < _dColors.Count ? Math.Max(0, _dColors[_dColor].Price) : 0;
        int InteriorPrice => _dInt >= 0 && _dInt < _dInts.Count ? Math.Max(0, _dInts[_dInt].Price) : 0;
        long Total => (long)CarPrice + RimPrice + PaintPrice + InteriorPrice;
        static long MyMoney => TDU2Session.Save != null ? TDU2Session.Save.money : 0;

        static string Money(long v) => "$ " + v.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ');

        string Loc(string var, string fallback)
        {
            var v = _ui.GetVariable(var);
            return string.IsNullOrEmpty(v) || v.StartsWith("_") ? fallback : v;
        }

        void UpdateTotals()
        {
            long total = Total;
            _ui.SetVariable("/:TotalPrice", Money(total));
            _ui.SetVariable("/:TotalPriceStr", Money(total));
            _ui.SetVariable("/:current_price", Money(total));
            _ui.SetVariable("/:money", Money(MyMoney));
            _ui.SetVariable("/:MyMoneyStr", Money(MyMoney));
            _ui.SetValue("/:EnoughMoney", !TDU2Session.LoggedIn || MyMoney >= total ? 1 : 3);
            for (int i = 1; i <= 3; i++)
                _ui.SetVariable(BuyWin + "/op_price_" + i + ":op_price", Money(i == 1 ? RimPrice : i == 2 ? PaintPrice : InteriorPrice));
        }

        /// <summary>Seite num_option (1 Felgen, 2 Lack, 3 Innenraum) in die Film-Variablen.</summary>
        void SetOptionVars(int option)
        {
            if (_ui == null) return;
            int nb, sel, price; string title, name; bool colors;
            switch (option)
            {
                case 1:
                    nb = Math.Max(1, _dRims.Count); sel = _dRim; colors = false;
                    title = Loc("/:m_Wheels_", "Felgen");
                    name = _dRims.Count > 0 ? _dRims[_dRim].DisplayName : "Standard";
                    price = RimPrice; break;
                case 2:
                    nb = Math.Max(1, _dColors.Count); sel = _dColor; colors = _dColors.Count > 0;
                    title = Loc("/:m_Paint_", "Lack");
                    name = _dColors.Count > 0 ? _dColors[_dColor].Name : "Standard";
                    price = PaintPrice; break;
                default:
                    nb = Math.Max(1, _dInts.Count); sel = _dInt; colors = _dInts.Count > 0;
                    title = Loc("/:m_Interior_", "Innenraum");
                    name = _dInts.Count > 0 ? _dInts[_dInt].Name : "Standard";
                    price = InteriorPrice; break;
            }
            sel = Mathf.Clamp(sel, 0, nb - 1);
            _ui.SetVariable("/:option_title", title);
            _ui.SetVariable("/:option_name", name);
            _ui.SetValue("/:has_colors", colors ? 1 : 0);
            _ui.SetValue("/:nb_items", nb);
            _ui.SetValue("/:num_item", sel + 1);
            _ui.SetValue("/:has_text", true);
            _ui.SetVariable("/:cur_op_price", Money(price));
            _ui.SetValue("/:isRimsChoice", _dRims.Count > 1);
            _ui.SetValue("/:isRimsFirst", sel == 0);
            _ui.SetValue("/:isRimsLast", sel >= nb - 1);
            _ui.SetValue("/:isPaintChoice", _dColors.Count > 1);
            _ui.SetValue("/:isInteriorChoice", _dInts.Count > 1);
            UpdateTotals();
            // Farbfelder: Lack (RAMPCLR1 der Lackfarbe) bzw. Innenraum (erste Innenfarbe)
            _dTints.Clear();
            if (colors)
                for (int i = 0; i < nb && i < 24; i++)
                    _dTints[BuyWin + "/colors/color" + (i + 1)] = option == 2 ? PaintRgb(_dColors[i]) : InteriorRgb(_dInts[i]);
            ApplyDealerTints();
        }

        Color32 PaintRgb(CarColor c)
        {
            var lib = _dealerCar?.Assets?.ColorLib;
            if (lib != null && c != null && !string.IsNullOrEmpty(c.ColorId)
                && lib.Materials.TryGetValue(MaterialDef.KeyFromName(c.ColorId), out var def) && def.TryGetParam("RAMPCLR1", out var v))
                return new Color(Mathf.Clamp01(v.x), Mathf.Clamp01(v.y), Mathf.Clamp01(v.z), 1f);
            return GuessColor(c?.Name);
        }

        Color32 InteriorRgb(InteriorInfo ii)
        {
            var lib = _dealerCar?.Assets?.ColorLib;
            if (lib != null && ii != null)
                foreach (var n in new[] { ii.Colors[0], ii.Material })
                {
                    if (string.IsNullOrEmpty(n) || !lib.Materials.TryGetValue(MaterialDef.KeyFromName(n), out var def)) continue;
                    if (def.TryGetParam("RAMPCLR1", out var v) || def.Params.TryGetValue(MaterialDef.KeyFromName("baseColor"), out v))
                        if (v.x + v.y + v.z > 0.001f) return new Color(Mathf.Clamp01(v.x), Mathf.Clamp01(v.y), Mathf.Clamp01(v.z), 1f);
                }
            return GuessColor(ii?.Name);
        }

        /// <summary>Notlösung ohne Materialfarbe: Farbname (englisch) grob zuordnen.</summary>
        static Color32 GuessColor(string name)
        {
            string n = (name ?? "").ToLowerInvariant();
            if (n.Contains("black") || n.Contains("noir")) return new Color32(20, 20, 22, 255);
            if (n.Contains("white") || n.Contains("blanc")) return new Color32(235, 235, 235, 255);
            if (n.Contains("red") || n.Contains("rouge")) return new Color32(170, 20, 20, 255);
            if (n.Contains("blue") || n.Contains("bleu")) return new Color32(30, 60, 160, 255);
            if (n.Contains("green") || n.Contains("vert")) return new Color32(30, 110, 50, 255);
            if (n.Contains("yellow") || n.Contains("gold") || n.Contains("jaune")) return new Color32(220, 180, 40, 255);
            if (n.Contains("orange") || n.Contains("saddle")) return new Color32(200, 100, 30, 255);
            if (n.Contains("silver") || n.Contains("grey") || n.Contains("gray") || n.Contains("gris")) return new Color32(150, 150, 155, 255);
            if (n.Contains("brown") || n.Contains("maroon") || n.Contains("beige") || n.Contains("tan")) return new Color32(120, 80, 50, 255);
            return new Color32(128, 128, 128, 255);
        }

        /// <summary>Der Film legt die Farbfelder bei Seitenwechseln neu an -> jedes Bild neu einfärben.</summary>
        void ApplyDealerTints()
        {
            foreach (var kv in _dTints)
            {
                var d = _ui.Find(kv.Key);
                if (d == null) continue;
                var c = kv.Value;
                d.Cx = new ColorTransform { Mul = new Vector4(0f, 0f, 0f, 1f), Add = new Vector4(c.r / 255f, c.g / 255f, c.b / 255f, 0f) };
                d.ScriptCx = true;
            }
        }

        int IntVar(string name)
        {
            var s = _ui.GetVariable(name);
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)Math.Round(d) : 0;
        }

        /// <summary>Blättern (Film hat /:num_item schon geändert): Auswahl übernehmen, Ausstellungsauto anpassen.</summary>
        void OnChangeOption()
        {
            int option = IntVar("/:num_option"), item = IntVar("/:num_item") - 1;
            switch (option)
            {
                case 1: _dRim = Mathf.Clamp(item, 0, Math.Max(0, _dRims.Count - 1)); break;
                case 2:
                    int keep = _dInt >= 0 && _dInt < _dInts.Count ? _dInts[_dInt].Id : 0;
                    _dColor = Mathf.Clamp(item, 0, Math.Max(0, _dColors.Count - 1));
                    RefreshInteriors(keep);
                    break;
                case 3: _dInt = Mathf.Clamp(item, 0, Math.Max(0, _dInts.Count - 1)); break;
            }
            SetOptionVars(option);
            _dChanged = true;
            RequestPreview(Choice(), 0.3f);
        }

        /// <summary>Tastatur: voriges/nächstes Element der Seite (wie die Pfeil-Buttons des Fensters).</summary>
        void StepOption(int dir)
        {
            int nb = IntVar("/:nb_items"), it = IntVar("/:num_item");
            int ni = Mathf.Clamp(it + dir, 1, Math.Max(1, nb));
            if (ni == it) return;
            _ui.SetValue("/:num_item", ni);
            OnChangeOption();
            _ui.SetFrame(BuyWin + "/colors/square", ni);
            TDU2FrontendSounds.Command("sndp:move");
        }

        /// <summary>
        /// Seite weiter (neop aus A) bzw. zurück (prop aus B) – das tut im Spiel der Programmcode (mit _root.fake = 0 schaltet
        /// der Film nicht selbst). Läuft synchron im FSCommand: danach blendet der Film anhand von num_option um.
        /// </summary>
        void DealerPage(int dir)
        {
            string bl = _ui.Label("/buy") ?? "";
            if (dir > 0 && bl == "car_action") { _ui.SetValue("/:num_option", 1); SetOptionVars(1); return; }   // Fenster öffnet
            if (dir < 0 && (bl == "close" || bl == "default")) return;                                          // B auf Seite 1: Fenster schließt
            int n = Mathf.Clamp(IntVar("/:num_option"), 1, 3);
            if (dir > 0 && n >= 3)
            {
                // letzte Seite bestätigt: Preisprüfung/Kaufdialog (confirm -> TestMoney)
                UpdateTotals();
                _ui.GotoLabel("/buy", "confirm", true);
                return;
            }
            int m = Mathf.Clamp(n + dir, 1, 3);
            if (m == n) return;
            _ui.SetValue("/:num_option", m);
            _ui.SetValue("/:hasChangeOption", true);
            SetOptionVars(m);
            _ui.GotoLabel("/buy", "next", true);   // -> options_stop: Fenster neu befüllen (Titel, Preise, Farbfelder, Pfeile)
            Debug.Log("[TDU2] Autohändler: Seite " + m + (m == 1 ? " (Felgen)" : m == 2 ? " (Lack)" : " (Innenraum)"));
        }

        // ================================================================== Befehle des Films
        bool OnDealerCommand(string c)
        {
            switch (c)
            {
                case "buin":                                   // Kauffenster öffnet
                    _ui.SetVisible("/buy", true);
                    _ui.SetValue("/:num_option", 1);
                    SetOptionVars(1);
                    return true;
                case "neop": DealerPage(+1); return true;       // A: Seite weiter (bzw. Fenster öffnet)
                case "prop": DealerPage(-1); return true;       // B: Seite zurück
                case "chop": OnChangeOption(); return true;     // Element gewechselt
                case "eval": UpdateTotals(); return true;        // TestMoney liest danach _root.EnoughMoney
                case "moup": return true;                        // Geld wird bei buyc abgebucht
                case "buyc": BuyDealerCar(); return true;
                case "buou": EndLure(); return true;             // Fenster abgebrochen -> BackToWM (cd_stop)
                case "tstk": StartTestDrive(); return true;
                case "dbxo":
                    if (_garageMenu) { AnswerGarageDialog(); return true; }
                    AnswerDealerDialog(); return true;
                case "rqca": BeginDealerCar(); return true;      // Einsteigen: Auto anfordern (Lader wartet auf CanEnterCar)
                case "caca":
                case "caou":                               // Einsteigen abgebrochen / ausgestiegen
                    EndDealerCar();
                    if (_ui != null && _ui.Label("/enter_car") != "default") _ui.GotoLabel("/enter_car", "default", false);
                    return true;
                case "wmon": case "wmof": return true;                  // Laufmodus an/aus – die Phase kommt aus dem Film-Zustand
                case "cain": case "shin": return true;           // Innenkamera / Hilfeleiste – schon gesetzt
                case "gofr":
                    // Ende von garage_quit (Losfahren) bzw. cd_close nach dem Kauf: jetzt mit dem Auto hinaus
                    if (_driveOutAt > 0f)
                    {
                        var car = _driveOutCar; _driveOutAt = -1f; _driveOutCar = null;
                        EndDealerMenu(false);
                        ExitInterior(car);
                    }
                    else if (!_dBought) EndDealerMenu();
                    return true;
                case "seca": return true;                        // Garage: Auto gewählt (das Losfahren folgt mit gofr)
                case "comp":                                   // Vergleichen: dieses Auto ist jetzt das Vergleichsauto
                    _compareCar = _dealerCar?.Car;
                    if (_compareCar != null) { FillCarData(_compareCar, true); _ui.SetValue("/:IsCompare", true); }
                    Debug.Log("[TDU2] Vergleich mit " + _compareCar?.FileName);
                    return true;
                case "rsco":                                   // Vergleich aufheben
                    _compareCar = null; _ui.SetValue("/:IsCompare", false);
                    return true;
                // Hilfeleiste im Auto (Mausklick auf die Knöpfe): Anlasser, Hupe, Rückblick
                case "star": if (_carIn == CarIn.Inside) ToggleDealerEngine(); return true;
                case "stao": return true;
                case "honk": _inHornClick = true; return true;
                case "hoof": _inHornClick = false; return true;
                case "bcvi": _inBackClick = true; return true;
                case "ftvi": _inBackClick = false; return true;
                case "shko":
                case "stci":
                case "sbdd":
                case "dain":
                case "camn":
                case "camf":
                case "mcin":
                case "mcou":
                case "shou":
                    return true;
            }
            return false;
        }

        void AnswerDealerDialog()
        {
            string kase = _ui.GetVariable("/:DgbxCase") ?? "";
            // Kein Geld: Preisanzeige rot ("no_money") und zurück zu den Optionen (em_3 "a" = back_options, em_0/em_2 "b")
            bool money = kase == "em_0" || kase == "em_2" || kase == "em_3";
            if (money)
            {
                string clip = null;
                foreach (var p in new[] { "/price/price/ic", "/price/price", "/price" }) if (_ui.HasLabel(p, "no_money")) { clip = p; break; }
                if (clip != null) _ui.GotoLabel(clip, "no_money", false);
                else ShowInfo("Nicht genug Geld: " + Money(Total) + " (du hast " + Money(MyMoney) + ")");
            }
            if (kase == "vehicle_full") { EndDealerCar(); _ui.SetValue("/:Input", true); return; }   // Einsteigen-Zeitüberschreitung
            string answer = kase == "em_0" || kase == "em_2" ? "b" : "a";
            Debug.Log("[TDU2] Autohändler: Dialog '" + kase + "' -> " + answer);
            _ui.SetVariable("/:PlayerAnswer", answer);
            if (!_ui.CallClip("/", "DialogBox")) _ui.SetValue("/:Input", true);
        }

        /// <summary>Garage (home.vmf): Dialog beantworten; "drive_out" (Losfahren) -> Film spielt garage_out, danach mit dem Auto hinaus.</summary>
        void AnswerGarageDialog()
        {
            string kase = _ui.GetVariable("/:DgbxCase") ?? "";
            if (kase == "vehicle_full") { EndDealerCar(); _ui.SetValue("/:Input", true); return; }
            // home.vmf: die Dialog-Auswertung steht in _root.DialogBox (drive_out + "a" -> Label garage_out)
            Debug.Log("[TDU2] Garage: Dialog '" + kase + "' -> Ja");
            _ui.SetVariable("/:PlayerAnswer", "a");
            if (!_ui.CallClip("/", "DialogBox"))
            {
                if (kase == "drive_out" && _ui.HasLabel("/", "garage_out")) _ui.GotoLabel("/", "garage_out", true);
                _ui.SetValue("/:Input", true);
            }
            if ((kase == "drive_out" || kase == "drive_out_list") && _dealerCar != null && GuestGarageCars == null)
            {
                _driveOutCar = _dealerCar.Car;
                _driveOutAt = Time.unscaledTime + 0.9f;
            }
        }

        void BuyDealerCar()
        {
            var dc = _dealerCar;
            if (dc == null || dc.Car == null || _dBought) return;
            long total = Total;
            if (TDU2Session.LoggedIn)
            {
                if (MyMoney < total) { ShowInfo("Nicht genug Geld."); return; }
                TDU2Session.Save.money = Math.Max(0, TDU2Session.Save.money - total);
            }
            _dBought = true;
            var choice = Choice();
            TDU2CarChoices.Set(dc.Car.FileName, choice);
            RememberOwned(dc.Car);
            if (TDU2Session.LoggedIn) TDU2Session.SaveAsync();
            Debug.Log("[TDU2] Autohändler: " + dc.Car.FileName + " gekauft für " + Money(total) + " (" + choice + ")");
            // wie im Spiel: Fenster schließt, danach fährt man mit dem neuen Auto hinaus
            EndLure();
            _driveOutCar = dc.Car;
            _driveOutAt = Time.unscaledTime + 1.2f;
        }

        // ================================================================== Jedes Bild (Menü offen)
        /// <summary>Kauffenster offen (Seiten Felgen/Lack/Innenraum)?</summary>
        bool DealerWindowOpen
        {
            get
            {
                if (_ui == null) return false;
                string bl = _ui.Label("/buy") ?? "";
                return bl == "car_action" || bl == "options_stop" || bl == "text" || bl == "no_text" || bl == "next"
                    || bl == "confirm" || bl == "back_options" || bl == "valid_car_dealer" || bl == "valid_bike_dealer";
            }
        }

        /// <summary>Mini-Menü offen (öffnet, steht oder schließt noch – erst am Ende der Schließanimation ruft der Film ValidMenu).</summary>
        bool DealerMiniOpen => _ui != null && OpenLabel(_ui.Label("/cursor/mini_menu"));

        void UpdateDealerMenu()
        {
            if (_ui == null) { _dealerMenu = false; CasinoMenuOpen = false; return; }
            ApplyDealerTints();
            var phase = ProbeDealerPhase();
            SetDealerPhase(phase);
            if (phase == DPhase.DriveOut)
            {
                if (Time.unscaledTime < _driveOutAt) return;
                var car = _driveOutCar; _driveOutAt = -1f; _driveOutCar = null;
                EndDealerMenu(false);
                ExitInterior(car);
                return;
            }
            if (phase == DPhase.Car) { if (_carIn != CarIn.None) UpdateDealerCar(); return; }   // im Ausstellungsauto (Einsteigen)
            if (_fp != null) _fp.LookEnabled = false;
            if (Cursor.lockState != CursorLockMode.None || !Cursor.visible) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            if (phase == DPhase.Idle)
            {
                // nichts mehr offen: kurz abwarten (Übergänge, z.B. Daten -> Mini-Menü) und ob die Aktion noch etwas startet
                if (_dIdleSince < 0f) _dIdleSince = Time.unscaledTime;
                if (Time.unscaledTime - _dIdleSince >= 0.3f && !DealerActionPending()) EndDealerMenu();
                return;
            }
            _dIdleSince = -1f;
            UpdateLure(phase == DPhase.Window ? Mathf.Clamp(IntVar("/:num_option"), 1, 3) : 0);
#if ENABLE_LEGACY_INPUT_MANAGER
            // ESC/Rücktaste: zentraler Menüablauf (TDU2MenuFlow.cs -> DealerBack); hier nur Auswahl/Bestätigen
            bool ok = Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.Space);
            bool up = Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W), down = Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S);
            bool left = Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A), right = Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D);
            if (ok || up || down || left || right)
                Debug.Log("[TDU2] Menü-Taste " + (ok ? "OK" : up ? "hoch" : down ? "runter" : left ? "links" : "rechts") + " in " + phase + " (Mini " + (_ui.Label("/cursor/mini_menu") ?? "-") + ", Kauf " + (_ui.Label("/buy") ?? "-") + ", Input " + _ui.GetVariable("/:Input") + ")");
            if (phase == DPhase.Window)
            {
                if (ok) { _ui.SetValue("/:Input", true); _ui.CallClip("/buy", "A"); }
                else if (left) StepOption(-1);
                else if (right) StepOption(1);
            }
            else if (phase == DPhase.Mini && _ui.Label("/cursor/mini_menu") == "stop")
            {
                // nur im stehenden Menü (beim Öffnen/Schließen würden Eingaben im Film verloren gehen oder doppelt zählen)
                if (ok || up || down) _ui.SetValue("/:Input", true);
                if (ok) _ui.CallClip("/cursor/mini_menu", "A");
                else if (up) _ui.CallClip("/cursor/mini_menu", "Move", "up");
                else if (down) _ui.CallClip("/cursor/mini_menu", "Move", "down");
            }
#endif
        }

        /// <summary>ESC/Rücktaste (vom zentralen Menüablauf): wie die B-Taste des Films auf der obersten offenen Ebene.</summary>
        void DealerBack()
        {
            if (_ui == null) { EndDealerMenu(); return; }
            _ui.SetValue("/:Input", true);
            switch (ProbeDealerPhase())
            {
                case DPhase.Car:
                    // enter_car.B(): lädt noch -> caca, sonst ExitVehicle -> caou, mcou, BackToWM
                    if (!_ui.CallClip("/enter_car", "B")) { EndDealerCar(); _ui.GotoLabel("/enter_car", "default", false); }
                    return;
                case DPhase.Window:
                    if (!_ui.CallClip("/buy", "B")) _ui.GotoLabel("/buy", "close", true);
                    return;
                case DPhase.Datas:
                    // car_datas.B (fd_stop): fd_close -> zurück ins Mini-Menü
                    if (!_ui.CallClip("/car_datas", "B")) _ui.GotoLabel("/car_datas", "fd_close", true);
                    return;
                case DPhase.Mini:
                    // wie die B-Taste: cursor.B -> wmon, camf, mini_menu close, BackFromMiniMenu
                    if (!_ui.CallClip("/cursor", "B"))
                    {
                        _ui.GotoLabel("/cursor/mini_menu", "close", true);
                        _ui.CallClip("/", "BackFromMiniMenu");
                    }
                    _ui.SetVariable("/:BtValid", "");
                    return;
                case DPhase.DriveOut:
                    return;
                default:
                    EndDealerMenu();
                    return;
            }
        }

        /// <summary>Händler sicher verlassen (Film reagiert nicht): alles zurücksetzen, Laufmodus.</summary>
        void DealerForceClose()
        {
            EndDealerCar();
            EndLure();
            if (_ui != null)
            {
                _ui.GotoLabel("/buy", "default", false);
                _ui.GotoLabel("/enter_car", "default", false);
                _ui.GotoLabel("/price", "default", false);
                _ui.GotoLabel("/car_datas", "default", false);
            }
            EndDealerMenu();
        }

        // ================================================================== Vorschaubild im Kauffenster (window_buy/lure)
        Camera _lureCam;
        RenderTexture _lureRt, _lureShow;
        int _lurePage;
        Vector3 _lurePos; Quaternion _lureRot; float _lureFov;
        bool _lureSnap;

        /// <summary>Kamera aufs Ausstellungsauto: Seite 1 Rad, 2 ganzes Auto, 3 Cockpit; 0 = aus.</summary>
        void UpdateLure(int page)
        {
            var dc = _dealerCar;
            if (page == 0 || dc == null || dc.Go == null || _ui == null) { if (_lureCam != null) _lureCam.enabled = false; return; }
            if (_lureCam == null)
            {
                var main = Camera.main;
                if (main == null) return;
                float aspect = 4f / 3f;
                var sr = _ui.ScreenRect(BuyLure);
                if (sr.HasValue && sr.Value.height > 8f) aspect = Mathf.Clamp(sr.Value.width / sr.Value.height, 0.5f, 3f);
                int w = 512, h = Mathf.Clamp(Mathf.RoundToInt(w / aspect), 128, 1024);
                _lureRt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "Händler Vorschau" };
                _lureShow = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32) { name = "Händler Vorschau (Anzeige)" };
                var go = new GameObject("TDU2 Händler Vorschaukamera");
                go.transform.SetParent(transform, false);
                _lureCam = go.AddComponent<Camera>();
                _lureCam.CopyFrom(main);
                _lureCam.targetTexture = _lureRt;
                _lureCam.aspect = (float)w / h;
                _lureCam.nearClipPlane = 0.05f;
                _lureCam.depth = main.depth - 2f;
                bool found = _ui.SetClipTexture(BuyLure, _lureShow, true);   // über den Fensterteilen des Films
                _lureSnap = true;
                _lureProbeAt = Time.unscaledTime + 0.8f;
                Debug.Log("[TDU2] Händler-Vorschau: Kamera " + w + "x" + h + ", Clip " + BuyLure + (found ? " gefunden" : " NICHT gefunden") +
                          ", Bildschirm " + (sr.HasValue ? sr.Value.ToString() : "-") + ", Hauptkamera " + main.name + " (Maske " + main.cullingMask + ", " + main.clearFlags + ")");
            }
            _lureCam.enabled = true;
            if (page != _lurePage) { _lurePage = page; }
            LurePose(dc, page, out var pos, out var rot, out float fov);
            if (_lureSnap) { _lurePos = pos; _lureRot = rot; _lureFov = fov; _lureSnap = false; }
            float k = 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime);
            _lurePos = Vector3.Lerp(_lurePos, pos, k); _lureRot = Quaternion.Slerp(_lureRot, rot, k); _lureFov = Mathf.Lerp(_lureFov, fov, k);
            _lureCam.transform.SetPositionAndRotation(_lurePos, _lureRot);
            _lureCam.fieldOfView = _lureFov;
            // kurzer Tiefenbereich: mit near 0,05 und der Sichtweite der Hauptkamera flimmerten/schwärzten sich die doppelt
            // liegenden Bodenflächen (Lightmap-Ebenen) im Vorschaubild (Z-Fighting)
            _lureCam.nearClipPlane = page == 3 ? 0.05f : 0.25f;
            _lureCam.farClipPlane = page == 3 ? 60f : 250f;
            _lureCam.useOcclusionCulling = false;
            _lureCam.allowHDR = false;
            // Flash erwartet Bilder von oben nach unten (wie die Spieltexturen)
            Graphics.Blit(_lureRt, _lureShow, new Vector2(1f, -1f), new Vector2(0f, 1f));
            if (_lureProbeAt > 0f && Time.unscaledTime >= _lureProbeAt) { _lureProbeAt = -1f; LogLurePixel(); }
        }

        float _lureProbeAt = -1f;

        /// <summary>Diagnose (einmal je Öffnen): Farbe in der Bildmitte der Vorschau und ob der Film-Clip das Bild hat.</summary>
        void LogLurePixel()
        {
            try
            {
                var prev = RenderTexture.active;
                RenderTexture.active = _lureShow;
                var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                t.ReadPixels(new Rect(_lureShow.width / 2, _lureShow.height / 2, 1, 1), 0, 0);
                var c = t.GetPixel(0, 0);
                RenderTexture.active = prev;
                Destroy(t);
                var d = _ui?.Find(BuyLure);
                Debug.Log("[TDU2] Händler-Vorschau: Mitte " + c + ", Kamera an " + (_lureCam != null && _lureCam.enabled) + " bei " + (_lureCam != null ? _lureCam.transform.position.ToString() : "-") +
                          ", Clip " + (d == null ? "fehlt" : (d.Visible ? "sichtbar" : "unsichtbar") + ", Textur " + (d.DynTex != null ? d.DynTex.name : "keine")));
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Händler-Vorschau: " + ex.Message); }
        }

        void LurePose(DisplayCar dc, int page, out Vector3 pos, out Quaternion rot, out float fov)
        {
            var car = dc.Go.transform;
            var bb = dc.Model != null ? dc.Model.BodyBounds : new Bounds(new Vector3(0f, 0.6f, 0f), new Vector3(1.9f, 1.3f, 4.5f));
            var center = car.TransformPoint(bb.center);
            var viewer = Camera.main != null ? Camera.main.transform.position : center + car.forward * 5f;
            if (page == 1)
            {
                // Rad auf der Seite des Spielers (vorne), seitlich leicht von vorn
                Transform best = null; float bd = float.MaxValue;
                foreach (var n in new[] { "D_RIM_FL", "D_RIM_FR", "DISC_FL", "DISC_FR", "D_TIRE_FL", "D_TIRE_FR" })
                {
                    var t = dc.Model?.Find(n);
                    if (t == null) continue;
                    float d = (t.position - viewer).sqrMagnitude;
                    if (d < bd) { bd = d; best = t; }
                }
                Vector3 wheel = best != null ? best.position : center + car.forward * bb.extents.z * 0.65f - car.up * bb.extents.y * 0.5f;
                float side = Mathf.Sign(Vector3.Dot(wheel - center, car.right)); if (side == 0f) side = 1f;
                pos = wheel + car.right * side * 1.25f + car.forward * 0.45f + Vector3.up * 0.15f;
                rot = Quaternion.LookRotation(wheel - pos, Vector3.up);
                fov = 38f;
                return;
            }
            if (page == 3)
            {
                // Cockpit: Fahrerkamera des Autos (camerasingame Modus 23), etwas zurück und zur Mitte für mehr Innenraum
                if (CockpitEye(dc, false, out var eye, out var look))
                {
                    pos = eye - (look - eye).normalized * 0.25f + (center - eye).normalized * 0.15f;
                    rot = Quaternion.LookRotation(look - pos, car.up);
                    fov = 70f;
                    return;
                }
            }
            // ganzes Auto, schräg von vorn auf der Seite des Spielers
            var flat = viewer - center; flat.y = 0f;
            if (flat.sqrMagnitude < 0.01f) flat = car.forward;
            var dir = Quaternion.AngleAxis(Vector3.Dot(flat, car.right) >= 0f ? -25f : 25f, Vector3.up) * flat.normalized;
            float dist = Mathf.Max(bb.size.x, bb.size.z) * 1.15f + 1f;
            pos = center + dir * dist + Vector3.up * (bb.size.y * 0.55f);
            rot = Quaternion.LookRotation(center - pos, Vector3.up);
            fov = 40f;
        }

        void EndLure()
        {
            if (_ui != null) _ui.SetClipTexture(BuyLure, null);
            if (_lureCam != null) Destroy(_lureCam.gameObject);
            if (_lureRt != null) { _lureRt.Release(); Destroy(_lureRt); }
            if (_lureShow != null) { _lureShow.Release(); Destroy(_lureShow); }
            _lureCam = null; _lureRt = null; _lureShow = null; _lurePage = 0;
        }

        // ================================================================== Einsteigen (enter_car)
        enum CarIn { None, Loading, Inside }
        CarIn _carIn;
        DisplayCar _inCar;
        Task _inSoundTask;
        TDU2VehicleAudio _inAudio;
        TDU2VehicleLights _inLights;
        TDU2Accessories _inAcc;
        float _inYaw, _inPitch, _inRpm, _inStartT;
        bool _inEngine, _inFpWas;
        bool _inHornClick, _inBackClick, _inHornAim;   // Hilfeleiste (honk/bcvi) bzw. Klick aufs Lenkrad
        CarPart _inAim = CarPart.None;
        Vector3 _inCamPos; Quaternion _inCamRot; float _inCamFov, _inCamNear;

        /// <summary>rqca: Fahrersitz vorbereiten (Sound im Hintergrund), dann darf der Lader weiter (CanEnterCar).</summary>
        void BeginDealerCar()
        {
            var dc = _dealerCar;
            var cam = Camera.main;
            if (dc == null || dc.Go == null || cam == null) { _ui.SetValue("/:CanEnterCar", true); return; }
            _carIn = CarIn.Loading; _inCar = dc;
            _inCamPos = cam.transform.position; _inCamRot = cam.transform.rotation; _inCamFov = cam.fieldOfView; _inCamNear = cam.nearClipPlane;
            _inFpWas = _fp != null && _fp.enabled;
            if (_fp != null) _fp.enabled = false;   // die Kamera gehört jetzt dem Fahrersitz
            _inYaw = 0f; _inPitch = 0f; _inEngine = false; _inRpm = 0f; _inStartT = -1f;
            _inHornClick = _inBackClick = _inHornAim = false; _inAim = CarPart.None;
            EndLure();
            if (dc.Assets != null && dc.Assets.Sound == null)
            {
                var a = dc.Assets; string bnk = Manager.BnkDir; var car = dc.Car;
                _inSoundTask = Task.Run(() => CarAssets.LoadSound(a, bnk, car));
            }
            // Lichter (Blinker) und Zubehör am Ausstellungsauto
            _inLights = dc.Go.GetComponent<TDU2VehicleLights>();
            if (_inLights == null && dc.Model != null)
            {
                _inLights = dc.Go.AddComponent<TDU2VehicleLights>();
                _inLights.External = true; _inLights.UnityLights = false;
                _inLights.Setup(null, dc.Model);
            }
            _inAcc = TDU2Accessories.For(dc.Model, _inLights);
            _netCar = dc;
            PlaceDealerCam(cam, 0f);
            _carIn = CarIn.Inside;
            _ui.SetVisible("/enter_car", true);   // Ladeanzeige / Innenansicht des Films (im Laufmodus ausgeblendet)
            _ui.SetValue("/:CanEnterCar", true);   // Lader (enter_car/cmon_loader) -> endLoading: Hilfeleiste "in_car", cain, shin
            LockCursor(true);
            Debug.Log("[TDU2] Eingestiegen in " + dc.Car.FileName + " (I Motor, W Gas, H Hupe, B Rückblick, V Zubehör, K Türen, N Fenster, Klick auf Tür/Scheibe/Lenkrad, ESC aussteigen)");
        }

        /// <summary>Augenpunkt und Blickziel des Fahrers (camerasingame Modus 23 bzw. Rückblick).</summary>
        bool CockpitEye(DisplayCar dc, bool back, out Vector3 eye, out Vector3 look)
        {
            var car = dc.Go.transform;
            var cams = Manager?.Cameras;
            int id = dc.Car.CameraId != 0 ? dc.Car.CameraId : dc.Car.Id;
            var def = cams?.Get(id, 23);
            if (def != null && back) { var b = cams.Get(id, def.BackMode); if (b != null) def = b; }
            if (def != null && def.IsFixed)
            {
                eye = car.TransformPoint(new Vector3(-def.FixedPos.x, def.FixedPos.y, def.FixedPos.z));
                look = car.TransformPoint(new Vector3(-def.FixedTarget.x, def.FixedTarget.y, def.FixedTarget.z));
                return true;
            }
            // Rückfall: Fahrersitz über den Sitzknoten bzw. vorne links in der Karosserie
            var seat = dc.Model?.Find("DVRSEAT_HR") ?? dc.Model?.Find("SEAT_HR_L");
            var bb = dc.Model != null ? dc.Model.BodyBounds : new Bounds(new Vector3(0f, 0.6f, 0f), new Vector3(1.9f, 1.3f, 4.5f));
            eye = seat != null ? seat.position + car.up * 0.7f : car.TransformPoint(bb.center + new Vector3(-bb.extents.x * 0.4f, bb.extents.y * 0.45f, 0f));
            look = eye + (back ? -car.forward : car.forward) * 3f;
            return seat != null;
        }

        void PlaceDealerCam(Camera cam, float dt)
        {
            var dc = _inCar;
            if (dc == null || dc.Go == null || cam == null) return;
            bool back = TDU2Controls.Held("lookback") || _inBackClick;
            CockpitEye(dc, back, out var eye, out var look);
            var baseRot = Quaternion.LookRotation(look - eye, dc.Go.transform.up);
            cam.transform.SetPositionAndRotation(eye, baseRot * Quaternion.Euler(_inPitch, _inYaw, 0f));
            cam.nearClipPlane = 0.03f;
            cam.fieldOfView = 62f;
        }

        void UpdateDealerCar()
        {
            var cam = Camera.main;
            var dc = _inCar;
            if (dc == null || dc.Go == null || cam == null) { EndDealerCar(); return; }
            float dt = Time.unscaledDeltaTime;
            // Sound fertig geladen: Motor-/Hupen-Klang am Auto (wie Mitspieler-Autos: Werte von außen)
            if (_inSoundTask != null && _inSoundTask.IsCompleted) _inSoundTask = null;
            if (_inAudio == null && _inSoundTask == null && dc.Assets?.Sound != null)
            {
                _inAudio = dc.Go.AddComponent<TDU2VehicleAudio>();
                _inAudio.External = true; _inAudio.InteriorView = true; _inAudio.MinDistance = 40f; _inAudio.MaxDistance = 200f;
                _inAudio.EngineOn = false;
                _inAudio.Setup(dc.Assets);
            }
            if (_inAcc != null) _inAcc.Audio = _inAudio;
            _inAcc?.Update(dt);
            bool menu = TDU2AccessoryMenu.Update(true);
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Cursor.lockState != CursorLockMode.Locked && Input.GetMouseButtonDown(0)) LockCursor(true);
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                _inYaw = Mathf.Clamp(_inYaw + Input.GetAxis("Mouse X") * 2.2f, -150f, 150f);
                _inPitch = Mathf.Clamp(_inPitch - Input.GetAxis("Mouse Y") * 2.2f, -55f, 65f);
            }
            if (Input.GetMouseButtonDown(2)) { _inYaw = 0f; _inPitch = 0f; }
            // Türen/Scheiben/Lenkrad in Blickmitte: Hinweis in der Cursor-Leiste, Linksklick schaltet (Lenkrad: Hupe, solange gedrückt)
            UpdateInCarAim(cam, menu);
            if (!menu)
            {
                if (TDU2Controls.Pressed("doors") && _inAcc != null) _inAcc.ToggleDoors();
                if (TDU2Controls.Pressed("windows") && _inAcc != null) _inAcc.ToggleWindows();
                CarPad(_inAcc);
                if (TDU2Controls.Pressed("starter") || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) ToggleDealerEngine();
                if (TDU2Controls.Pressed("accessories") && _inAcc != null)
                {
                    TDU2Minimap.HudOverlay = true;
                    TDU2AccessoryMenu.Open(_inAcc, IsBike(dc.Car));
                }
            }
            if (_inAudio != null) _inAudio.SetHorn(TDU2Controls.Held("horn") || _inHornClick || _inHornAim);
            bool gas = _inEngine && _inStartT < 0f && !menu && (TDU2Controls.Held("accel") || Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow));
#else
            bool gas = false;
#endif
            // HUD-Overlay erst aus, wenn das Zubehörmenü seine Schließen-Animation beendet hat
            if (!TDU2AccessoryMenu.IsOpen && TDU2Minimap.HudOverlay && !TestDriveActive && (TDU2UIManager.Hud == null || (TDU2UIManager.Hud.Label("/HUD/ig_menu") ?? "default") == "default"))
                TDU2Minimap.HudOverlay = false;
            UpdateDealerEngine(dt, gas);
            PlaceDealerCam(cam, dt);
        }

        void ToggleDealerEngine()
        {
            if (_inEngine) { _inEngine = false; _inStartT = -1f; if (_inAudio != null) _inAudio.EngineOn = false; return; }
            _inEngine = true; _inStartT = 0f;
            if (_inAudio != null) { _inAudio.EngineOn = true; _inAudio.Play("EngineStart"); }
        }

        /// <summary>Drehzahl im Stand: Anlassen (Zündzeit), Leerlauf, Gas hoch bis kurz vor den Begrenzer, langsam zurück.</summary>
        void UpdateDealerEngine(float dt, bool gas)
        {
            var c = _inCar?.Car;
            if (c == null) return;
            float idle = Mathf.Max(600f, c.RpmMin > 0f ? c.RpmMin : 850f);
            float red = Mathf.Max(idle + 1500f, c.RedLine > 0f ? c.RedLine : 7000f);
            float torque = 0f;
            if (!_inEngine) _inRpm = Mathf.MoveTowards(_inRpm, 0f, idle * 2f * dt);
            else if (_inStartT >= 0f)
            {
                // Anlassen: Zündzeit lang hochdrehen bis knapp über Leerlauf
                _inStartT += dt;
                float ign = Mathf.Max(0.3f, c.TimeIgnition);
                _inRpm = Mathf.Lerp(idle * 0.3f, idle * 1.25f, Mathf.Clamp01(_inStartT / ign));
                if (_inStartT >= ign) _inStartT = -1f;
            }
            else if (gas)
            {
                _inRpm = Mathf.MoveTowards(_inRpm, red * 0.97f, (red - idle) * 0.9f * dt);
                if (_inRpm >= red * 0.965f) _inRpm -= (red - idle) * 0.08f;   // Begrenzer
                torque = c.TorqueNm;
            }
            else
            {
                _inRpm = Mathf.MoveTowards(_inRpm, idle, (red - idle) * 0.55f * dt);
                torque = -c.TorqueNm * 0.1f;
            }
            if (_inAudio != null) { _inAudio.ExtRpm = _inRpm; _inAudio.ExtKmh = 0f; _inAudio.ExtTorque = torque; }
        }

        /// <summary>Aussteigen (caou/caca bzw. Menü geschlossen): Kamera zurück an den Fußgänger, Klang/Zubehör aus.</summary>
        void EndDealerCar()
        {
            if (_carIn == CarIn.None) return;
            _carIn = CarIn.None;
            if (TDU2AccessoryMenu.IsOpen) TDU2AccessoryMenu.Close(false);
            if (!TestDriveActive) TDU2Minimap.HudOverlay = false;
            if (_inAcc != null) { _inAcc.CloseAll(); if (_inAcc.RoofOpen) _inAcc.ToggleRoof(); _inAcc.Audio = null; }
            if (_inAim != CarPart.None && _ui != null) _ui.GotoLabel("/cursor/low_bar", "close", true);
            _inAim = CarPart.None; _inHornClick = _inBackClick = _inHornAim = false;
            if (_inAudio != null)
            {
                _inAudio.SetHorn(false);
                // Klangquellen gehören zum Ausstellungsauto: mit entfernen (sonst sammeln sie sich bei jedem Einsteigen an)
                foreach (var src in _inAudio.GetComponents<AudioSource>()) Destroy(src);
                Destroy(_inAudio);
            }
            _inAudio = null; _inAcc = null; _inSoundTask = null;
            if (_inLights != null) { _inLights.IndicatorLeft = _inLights.IndicatorRight = _inLights.Hazard = false; }
            var cam = Camera.main;
            if (cam != null) { cam.transform.SetPositionAndRotation(_inCamPos, _inCamRot); cam.fieldOfView = _inCamFov; cam.nearClipPlane = _inCamNear; }
            if (_fp != null && _inFpWas) { _fp.enabled = true; _fp.LookEnabled = false; }
            _inCar = null;
            Debug.Log("[TDU2] Autohändler: ausgestiegen");
        }

        /// <summary>
        /// Im Auto: Tür/Scheibe/Lenkrad in Blickmitte anvisieren. Hinweis über die Cursor-Leiste des Films (t_wm_destination),
        /// Linksklick schaltet Tür bzw. Scheibe, Lenkrad = Hupe solange gedrückt.
        /// </summary>
        void UpdateInCarAim(Camera cam, bool menu)
        {
            // Im Original gibt es im Auto kein Anvisieren von Tür/Scheibe/Lenkrad (kein "Fenster öffnen"-Hinweis): Türen und Fenster
            // nur über das Fahrzeugmenü (Num 4/6/8/2), Hupe/Anlasser/Rückblick über die Hilfeleiste
            var part = CarPart.None;
            if (_ui != null && part != _inAim)
            {
                if (part != CarPart.None)
                {
                    _ui.SetVisible("/cursor", true);
                    _ui.SetVariable("/:t_wm_destination", _inAcc.Hint(part));
                    _ui.GotoLabel("/cursor", "on", true);
                    _ui.GotoLabel("/cursor/low_bar", "open", true);
                    _ui.GotoLabel("/cursor/low_bar/ic", "default", false);
                }
                else _ui.GotoLabel("/cursor/low_bar", "close", true);
            }
            _inAim = part;
#if ENABLE_LEGACY_INPUT_MANAGER
            _inHornAim = part == CarPart.Steering && Input.GetMouseButton(0);
            if (part != CarPart.None && part != CarPart.Steering && Input.GetMouseButtonDown(0) && _inAcc.Toggle(part))
            {
                _ui?.SetVariable("/:t_wm_destination", _inAcc.Hint(part));
                TDU2FrontendSounds.Command("sndp:valid");
            }
#endif
        }

        /// <summary>Online: Ausstellungsauto, an dem man gerade Türen/Fenster bedient (Mitspieler im selben Raum sehen es).</summary>
        public bool LocalCarState(out int carId, out int bits, out int flags)
        {
            carId = 0; bits = 0; flags = 0;
            var dc = _netCar;
            if (dc == null || dc.Car == null || dc.Model == null || !_cars.Contains(dc)) return false;
            var acc = TDU2Accessories.Existing(dc.Model);
            carId = dc.Car.Id;
            bits = acc != null ? acc.Bits : 0;
            bool sitting = _carIn == CarIn.Inside && _inCar == dc;
            if (sitting) flags |= 1;
            if (sitting && _inEngine) flags |= 2;
            if (sitting && (TDU2Controls.Held("horn") || _inHornClick || _inHornAim)) flags |= 4;
            return true;
        }

        /// <summary>Online: Mitspieler im selben Raum bedient Türen/Fenster an einem Ausstellungsauto (DB-Id).</summary>
        public bool ApplyRemoteCarState(int carId, int bits, int flags)
        {
            if (!Inside || carId == 0) return false;
            foreach (var dc in _cars)
            {
                if (dc.Car == null || dc.Car.Id != carId || dc.Model == null) continue;
                TDU2Accessories.For(dc.Model, null).ApplyBits(bits);
                return true;
            }
            return false;
        }

        /// <summary>Tiefster Punkt der Räder (Welt-Y) – zum Ausrichten eines neu aufgebauten Ausstellungsautos.</summary>
        static bool WheelBottom(DisplayCar dc, out float y)
        {
            y = float.MaxValue;
            if (dc?.Model == null) return false;
            foreach (var n in new[] { "DISC_FL", "DISC_FR", "DISC_RL", "DISC_RR" })
            {
                var t = dc.Model.Find(n);
                if (t == null) continue;
                foreach (var r in t.GetComponentsInChildren<Renderer>(true)) if (r.enabled) y = Mathf.Min(y, r.bounds.min.y);
            }
            return y < float.MaxValue;
        }

        // ================================================================== Ausstellungsauto mit anderer Ausstattung
        void RequestPreview(CarChoice want, float delay)
        {
            _previewWant = want; _previewFor = _dealerCar;
            _previewDirty = true; _previewAt = Time.unscaledTime + delay;
        }

        /// <summary>Jedes Bild (auch nach dem Schließen: Auto zurücksetzen).</summary>
        void PollDealerPreview()
        {
            FlushOrphanAssets();
            if (_previewTask != null && _previewTask.IsCompleted)
            {
                var t = _previewTask; _previewTask = null;
                var target = _previewFor;
                if (t.IsFaulted) Debug.LogWarning("[TDU2] Autohändler-Vorschau: " + t.Exception?.GetBaseException().Message);
                else if (target == null || !_cars.Contains(target) || _holder == null || _carIn != CarIn.None) t.Result?.DestroyTextures();
                else
                {
                    try { ReplaceDisplayCar(target, t.Result, _loadingChoice); }
                    catch (Exception ex) { Debug.LogWarning("[TDU2] Autohändler-Vorschau: " + ex.Message); t.Result?.DestroyTextures(); }
                }
            }
            if (!_previewDirty || _previewTask != null || Time.unscaledTime < _previewAt || _carIn != CarIn.None) return;
            _previewDirty = false;
            var dc = _previewFor;
            if (dc == null || dc.Car == null || !_cars.Contains(dc) || Same(dc.Choice, _previewWant)) return;
            string bnk = Manager.BnkDir; var db = Manager.Database; var car = dc.Car; var w = _previewWant;
            _loadingChoice = w;
            _previewTask = Task.Run(() => CarAssets.Load(bnk, db, car, w.Color, false, w.Rim, w.Interior));
        }

        CarChoice _loadingChoice;

        void ReplaceDisplayCar(DisplayCar old, CarAssets a, CarChoice choice)
        {
            int idx = _cars.IndexOf(old);
            if (idx < 0) { a.DestroyTextures(); return; }
            // altes Auto vorher aus dem Weg: sonst landet der Boden-Strahl des neuen auf seinem Dach (Auto "springt" hoch)
            if (old.Col != null) old.Col.enabled = false;
            // gleiche Lage wie das alte Auto (Stellplatz + Bodenhöhe); Drehteller-Autos drehen weiter, wo sie waren
            BuildDisplayCar(a, old.Car, old.Slot);
            var nd = _cars[_cars.Count - 1];
            if (old.Go != null && nd.Go != null)
            {
                nd.Go.transform.rotation = old.Go.transform.rotation;
                var p = nd.Go.transform.position; var op = old.Go.transform.position;
                // Höhe: Räder des neuen Autos auf dieselbe Bodenhöhe wie die des alten (andere Felgen = anderer Raddurchmesser);
                // ohne Räder wie bisher die Höhe aus dem Aufbau
                nd.Go.transform.position = new Vector3(op.x, p.y, op.z);
                if (WheelBottom(old, out float ob) && WheelBottom(nd, out float nb) && Mathf.Abs(ob - nb) < 0.6f)
                    nd.Go.transform.position += Vector3.up * (ob - nb);
            }
            nd.Choice = choice;
            _cars.RemoveAt(_cars.Count - 1);
            _cars[idx] = nd;
            TDU2Accessories.Forget(old.Model);
            old.Model?.Destroy();
            old.Assets?.DestroyTextures();
            if (old.Go != null) UnityUtil.SafeDestroy(old.Go);
            if (_dealerCar == old) _dealerCar = nd;
            if (_netCar == old) _netCar = nd;
            if (_previewFor == old) _previewFor = nd;
            if (ReferenceEquals(_uiAim, old)) _uiAim = nd;
        }

        // ================================================================== Testfahrt
        void StartTestDrive()
        {
            var dc = _dealerCar;
            if (dc == null || dc.Car == null || _spot == null) return;
            var car = dc.Car; var spot = _spot; var prev = _lastCar; var choice = Choice();
            EndDealerMenu(false);
            TDU2CarChoices.SetTemporary(car.FileName, choice);
            TestDriveActive = true;
            _testCar = car; _testSpot = spot; _testPrev = prev;
            _testEnd = Time.time + Mathf.Max(10f, TestDriveSeconds);
            _testHud = false; _testLastSec = -1;
            Debug.Log("[TDU2] Testfahrt: " + car.FileName + " (" + choice + "), " + TestDriveSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
            ExitInterior(car);
            _lastCar = prev;   // ExitInterior merkt sich sonst den Leihwagen
        }

        /// <summary>Jedes Bild: Restzeit im HUD, danach zurück zum Händler.</summary>
        void UpdateTestDrive()
        {
            if (!TestDriveActive) return;
            float left = _testEnd - Time.time;
            var h = TDU2UIManager.Hud;
            if (h != null)
            {
                if (!_testHud) { _testHud = true; h.SetVisible("/HUD/time_cd", true); h.GotoLabel("/HUD/time_cd", "open", true); }
                float s = Mathf.Max(0f, left);
                int ms = (int)(s * 1000f);
                h.SetVariable("/:MinCd", (ms / 60000).ToString(CultureInfo.InvariantCulture));
                h.SetVariable("/:SecCd", ((ms / 1000) % 60).ToString("00", CultureInfo.InvariantCulture));
                h.SetVariable("/:CentCd", ((ms / 10) % 100).ToString("00", CultureInfo.InvariantCulture));
                int sec = Mathf.CeilToInt(s);
                if (sec <= 5 && sec >= 1 && sec != _testLastSec)
                {
                    if (_testLastSec < 0) h.GotoLabel("/HUD/time_out", "on", true);
                    _testLastSec = sec;
                    h.SetVariable("/:time_out", sec.ToString(CultureInfo.InvariantCulture));
                }
            }
            if (left > 0f || Inside || Manager.IsLoadingCar) return;
            var spot = _testSpot; var prev = _testPrev;
            StopTestDrive();
            Debug.Log("[TDU2] Testfahrt beendet – zurück zum Händler " + spot?.Code);
            if (spot != null && !EnterSpot(spot, prev) && prev != null && Manager.Current != null)
                Manager.SpawnCar(prev, Manager.Current.transform.position, Manager.Current.transform.rotation);
        }

        /// <summary>Testfahrt beenden (HUD aus, Leihwagen-Ausstattung verwerfen).</summary>
        void StopTestDrive()
        {
            if (!TestDriveActive) return;
            TestDriveActive = false;
            if (_testCar != null) TDU2CarChoices.ClearTemporary(_testCar.FileName);
            var h = TDU2UIManager.Hud;
            if (h != null && _testHud)
            {
                h.GotoLabel("/HUD/time_cd", "default", false);
                h.GotoLabel("/HUD/time_out", "off", false);
            }
            _testHud = false; _testCar = null; _testSpot = null;
        }

        /// <summary>Während der Testfahrt ein anderes Interieur betreten: Testfahrt endet, beim Verlassen kommt das eigene Auto.</summary>
        void AbortTestDriveOnEnter()
        {
            if (!TestDriveActive) return;
            var prev = _testPrev;
            StopTestDrive();
            Manager.RemoveCar();
            _lastCar = prev;
            Debug.Log("[TDU2] Testfahrt abgebrochen (Interieur betreten)");
        }
    }
}
