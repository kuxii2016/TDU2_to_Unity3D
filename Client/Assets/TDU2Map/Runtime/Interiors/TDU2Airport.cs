using System;
using System.IO;
using TDU2.Flash;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Flughafen (Interieur iair_a/iair_b, Hostess = FpSelectable Typ 32) mit dem Original-Film frontend/&lt;res&gt;/airport.bnk:
    ///   _root.destination = Zielinsel (Labels "ibiza"/"hawai" im Clip destination), _root.Price, _root.TicketIsSold,
    ///   Label "ai_open" öffnet die Flugauswahl (Clip airport), A() -> DBXO "m_TakeFlight" (Fall buy_ticket) -> FSCommand dbxo,
    ///   Antwort "a" + _root.DialogBox() -> BuyTicket() -> CloseAirport() -> Bild 65: FSCommand "flok" = Flug bestätigt.
    ///   B() -> CloseAirport() -> BackToWM() -> FSCommand "wmon" (zurück in den Laufmodus).
    /// Nach "flok": <see cref="TDU2IslandTravel.Go"/> mit dem Auto, mit dem man gekommen ist.
    /// </summary>
    public sealed class TDU2Airport : MonoBehaviour
    {
        public TDU2VehicleManager Manager;
        public TDU2Interiors Interiors;
        [Tooltip("Flugpreis ($), 0 = kostenlos (im Spiel ist der Flug frei). [vermutet]")]
        public long Price = 0;

        static TDU2Airport _inst;
        public static bool IsOpen => _inst != null && _inst._open;

        VmfPlayer _film;
        string _bank;
        bool _open, _flying;
        float _savedTs = 1f;
        CursorLockMode _savedLock; bool _savedCursor;
        string _target;
        string[] _choices;              // Casino: Ibiza oder Hawaii wählbar (links/rechts)
        string _arrival = "EAIR";       // Spot-Präfix des Ankunftsorts
        float _lastEsc = -10f;
        float _flyAt = -1f, _closeAt = -1f;

        void Awake() { _inst = this; }
        void OnDestroy() { if (_inst == this) _inst = null; if (_open) Close(); }

        bool LoadFilm()
        {
            if (_film != null) return true;
            if (Manager == null || string.IsNullOrEmpty(Manager.BnkDir)) return false;
            foreach (var res in new[] { "hires", "medres", "lowres" })
            {
                _bank = CarDatabase.FindFile(Path.Combine(Manager.BnkDir, "frontend", res), "airport.bnk");
                if (_bank != null) break;
            }
            if (_bank == null) { DebugManager.Error("airport.bnk not found"); return false; }
            try
            {
                var go = new GameObject("TDU2 Airport UI");
                go.transform.SetParent(transform, false);
                _film = go.AddComponent<VmfPlayer>();
                _film.GuiDepth = -2;
                _film.MouseEnabled = true;
                _film.Load(_bank);
                _film.Visible = false;
                _film.FSCommand += OnCommand;
                FlashLocalization.Apply(_film, FlashLocalization.Load(Manager.BnkDir, TDU2PauseMenu.SavedLanguage()));
                return true;
            }
            catch (Exception ex)
            {
                DebugManager.Error("airport.bnk: " + ex.Message);
                if (_film != null) { Destroy(_film.gameObject); _film = null; }
                return false;
            }
        }

        public void Open() => Open(null, null);

        /// <summary>
        /// Flugauswahl öffnen. choices = mögliche Ziele (Casino-Ausgang: "hawai", "ibiza" – mit links/rechts wählbar, wie im Spiel
        /// über den Flughafen-Film), arrivalPrefix = Ankunfts-Spot (ELAND = Landeplatz, ohne -> Flughafen EAIR).
        /// </summary>
        public void Open(string[] choices, string arrivalPrefix)
        {
            if (_open || _flying) return;
            _flyAt = -1f; _closeAt = -1f;
            string cur = Manager != null && Manager.Streamer != null && Manager.Streamer.Index != null ? Manager.Streamer.Index.IslandName : "ibiza";
            _choices = choices != null && choices.Length > 0 ? choices : null;
            _arrival = string.IsNullOrEmpty(arrivalPrefix) ? "EAIR" : arrivalPrefix;
            _target = _choices != null ? _choices[0] : TDU2IslandTravel.OtherIsland(cur);
            if (!TDU2IslandTravel.CanTravelTo(_target, Manager.BnkDir))
            {
                DebugManager.Error("Airport: Island " + _target + " missing");
                return;
            }
            _open = true;
            _savedTs = Time.timeScale > 0f ? Time.timeScale : 1f;
            Time.timeScale = 0f;
            _savedLock = Cursor.lockState; _savedCursor = Cursor.visible;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            if (!LoadFilm())
            {
                // ohne Film: gleich fliegen
                Fly();
                return;
            }
            _film.SetVariable("/:destination", _target);
            ShowDestination();
            _film.SetVariable("/:Price", Price > 0 ? Price.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " $" : "");
            _film.SetValue("/:TicketIsSold", true);   // kein Kauf-Ablauf (Geld-Animation) – Flug ist frei
            _film.SetValue("/:Input", true);
            _film.Visible = true;
            _film.GotoLabel("/", "ai_open", true);
            Debug.Log("[TDU2] Flughafen geöffnet: Flug nach " + TDU2IslandTravel.DisplayName(_target));
        }

        public void Close()
        {
            if (!_open) return;
            _open = false;
            Time.timeScale = _savedTs;
            Cursor.lockState = _savedLock; Cursor.visible = _savedCursor;
            if (_film != null) { _film.Visible = false; _film.GotoLabel("/", "default", false); }
            Racing.RacingSchoolMenu.NoteUiClosed();
        }

        void OnCommand(string cmd, string args)
        {
            string c = (cmd ?? "").Trim().ToLowerInvariant();
            if (TDU2FrontendSounds.Command(c)) return;
            switch (c)
            {
                case "dbxo":
                    {
                        // Bestätigung "Flug antreten?" (buy_ticket) / "Flughafen verlassen?" (quit) – wie die anderen Menüs mit "ja"
                        string dc = _film.GetVariable("/:DgbxCase") ?? "";
                        _film.SetVariable("/:PlayerAnswer", "a");
                        _film.CallClip("/", "DialogBox");
                        TDU2DebugLog.Event("Flughafen: Dialog '" + dc + "' mit ja beantwortet");
                        // Rückfall: die Animationskette (BuyTicket -> CloseAirport -> close -> flok) kommt im Nachbau nicht immer an
                        if (string.Equals(dc, "quit", StringComparison.OrdinalIgnoreCase)) _closeAt = Time.unscaledTime + 1.5f;
                        else _flyAt = Time.unscaledTime + 1.5f;
                        break;
                    }
                case "flok": Fly(); break;
                case "wmon":
                case "gofr": Close(); break;
            }
        }

        void Fly()
        {
            if (_flying) return;
            _flying = true;
            if (Price > 0 && Online.TDU2Session.Save != null) Online.TDU2Session.Save.money = Math.Max(0, Online.TDU2Session.Save.money - Price);
            var car = Interiors != null ? Interiors.LastCar : null;
            if (car == null && Manager.Current != null) car = Manager.Current.Car;
            Close();
            TDU2IslandTravel.Go(_target, true, car != null && !Racing.RacingSchoolMenu.IsSchoolCar(car) ? car.FileName : null, _arrival);
        }

        void CycleChoice(int dir)
        {
            if (_choices == null || _choices.Length < 2) return;
            int i = Array.IndexOf(_choices, _target);
            i = ((i < 0 ? 0 : i) + dir + _choices.Length) % _choices.Length;
            _target = _choices[i];
            _film.SetVariable("/:destination", _target);
            ShowDestination();
            TDU2FrontendSounds.Command("sndp:move");
            DebugManager.Info("Fly to: " + TDU2IslandTravel.DisplayName(_target));
        }

        /// <summary>Alle Clips "destination" (Bild der Zielinsel, Labels ibiza/hawai) auf das Ziel stellen.</summary>
        void ShowDestination()
        {
            if (_film == null || _film.Root == null) return;
            void Walk(VmfPlayer.Instance inst, string path, int depth)
            {
                if (inst == null || depth > 10) return;
                foreach (var d in inst.List.Values)
                {
                    if (d == null || d.Sprite == null || string.IsNullOrEmpty(d.Name)) continue;
                    string p = path + "/" + d.Name;
                    if (d.Name == "destination") _film.GotoLabel(p, _target, false);
                    else Walk(d.Sprite, p, depth + 1);
                }
            }
            Walk(_film.Root, "", 0);
        }

        void Update()
        {
            if (_flyAt > 0f && Time.unscaledTime >= _flyAt) { _flyAt = -1f; TDU2DebugLog.Event("Flughafen: kein flok vom Film – Flug selbst starten"); Fly(); return; }
            if (_closeAt > 0f && Time.unscaledTime >= _closeAt) { _closeAt = -1f; Close(); return; }
            if (!_open || _film == null) return;
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) _film.Input("Move", "up");
            if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) _film.Input("Move", "down");
            if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) { _film.Input("Move", "left"); CycleChoice(-1); }
            if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) { _film.Input("Move", "right"); CycleChoice(1); }
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space)) _film.Input("A");
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace))
            {
                // doppeltes ESC schließt immer (falls der Film hängt)
                if (Time.unscaledTime - _lastEsc < 1.5f) { Close(); return; }
                _lastEsc = Time.unscaledTime;
                if (!_film.Input("B")) Close();
            }
            if (Input.GetKeyDown(KeyCode.F10)) Close();
#endif
            // Rückfall: Film im Laufmodus-Label angekommen, ohne wmon zu senden
            string lab = _film.Label("/") ?? "";
            if (string.Equals(lab, "airport_wm", StringComparison.OrdinalIgnoreCase) || string.Equals(lab, "back_fr", StringComparison.OrdinalIgnoreCase)) Close();
        }
    }
}