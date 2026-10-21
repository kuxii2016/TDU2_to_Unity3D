using System.Text;
using TDU2.Map.Online;
using TDU2.Map.Racing;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Sauberes Verlassen aller Interieur-Menüs (Casino, Händler, Tagebuch, MyTDULife, My House) über EINE Stelle:
    ///  * Die oberste offene Menü-Ebene wird jedes Bild aus dem Zustand bestimmt (nicht gemerkt -> kann nicht "veralten").
    ///  * ESC/Rücktaste gehört genau dieser Ebene: zuerst der normale Weg des Films (B-Taste, mit _root.Input = true, weil
    ///    der Film B sonst ignoriert – die häufigste Ursache für Hänger).
    ///  * Reagiert der Film darauf gar nicht (0,7 s keine Änderung), schließt das Spiel die Ebene selbst und stellt den
    ///    Laufmodus bzw. die darunterliegende Ebene her. Ein zweites ESC, solange das erste noch läuft, schließt sofort.
    ///  * ESC gehalten (0,8 s): Notausgang – alle Menüs zu, zurück im Laufmodus.
    ///  * Ohne offenes Menü: verwaiste Sperren (Pausemenü gesperrt, Texteingabe aktiv, HUD-Overlay) werden aufgeräumt.
    /// Die einzelnen Menüs behandeln ESC nicht mehr selbst (früher: je Menü eigene Watchdogs/"doppeltes ESC" -> Hänger je
    /// nach Kombination).
    /// </summary>
    public sealed partial class TDU2Interiors
    {
        enum MenuLayer { None, Casino, DealerCar, DealerWindow, Dealer, LifePhoto, LifeText, MyLife, Diary, HouseEditor, House }

        [Header("Menüs verlassen")]
        [Tooltip("ESC so lange halten (s): alle Menüs schließen (Notausgang)")]
        public float EscHoldCloseAll = 0.8f;
        [Tooltip("Zeigt der Film nach ESC so lange (s) keine Reaktion, schließt das Spiel das Menü selbst")]
        public float BackNoReactionSeconds = 0.7f;
        [Tooltip("Spätestens nach so vielen Sekunden muss das Menü nach ESC weiter sein (Animationen, Lader)")]
        public float BackMaxSeconds = 3f;

        MenuLayer _backLayer;
        float _backAt, _backChangeAt;
        bool _backChanged;
        string _backSig;
        float _escDownAt = -1f; bool _escHoldDone;
        float _noMenuSince = -1f, _nextHeal;

        /// <summary>Oberste offene Menü-Ebene (Diagnose, F1-Menü).</summary>
        public string OpenMenuLayer => TopMenuLayer().ToString();

        MenuLayer TopMenuLayer()
        {
            if (_casinoMenu) return MenuLayer.Casino;
            if (_dealerMenu) return _carIn != CarIn.None ? MenuLayer.DealerCar : DealerWindowOpen ? MenuLayer.DealerWindow : MenuLayer.Dealer;
            if (_diaryOpen)
            {
                if (_lifePhoto) return MenuLayer.LifePhoto;
                if (_textVar != null) return MenuLayer.LifeText;
                if (LifeShown()) return MenuLayer.MyLife;
                return MenuLayer.Diary;
            }
            if (_myHouse == HouseMode.Furniture || _myHouse == HouseMode.Material) return MenuLayer.HouseEditor;
            if (_myHouse == HouseMode.Walk) return MenuLayer.House;
            return MenuLayer.None;
        }

        bool LifeShown()
        {
            var d = _ui?.Find("/mylf");
            if (d == null || !d.Visible) return false;
            string l = _ui.Label("/mylf");
            return !string.IsNullOrEmpty(l) && l != "default" && l != "close";
        }

        static readonly string[] SigClips = { "/", "/diary", "/diary/menu", "/mylf", "/mylf/edbox/edit_popup", "/mylf/photo_taker", "/mylf/vk",
                                              "/myhs_editor", "/buy", "/cursor/mini_menu", "/enter_car" };

        /// <summary>Kurzer Fingerabdruck des Film-Zustands: ändert er sich nach ESC, hat der Film reagiert.</summary>
        string MenuSignature()
        {
            var sb = new StringBuilder(128);
            if (_ui != null)
            {
                foreach (var p in SigClips) sb.Append(_ui.Label(p)).Append('|');
                sb.Append(_ui.GetVariable("/:Input"));
            }
            sb.Append('|').Append(TopMenuLayer());
            return sb.ToString();
        }

        /// <summary>Jedes Bild im Interieur, vor den einzelnen Menüs.</summary>
        void UpdateMenuFlow()
        {
            float now = Time.unscaledTime;
            var top = TopMenuLayer();
            if (top == MenuLayer.None) { _backLayer = MenuLayer.None; HealIdle(now); return; }
            _noMenuSince = -1f;
            if (TDU2AccessoryMenu.IsOpen) return;   // Zubehörmenü (im Auto) schließt mit ESC selbst
#if ENABLE_LEGACY_INPUT_MANAGER
            // Notausgang: ESC halten
            if (Input.GetKey(KeyCode.Escape))
            {
                if (_escDownAt < 0f) _escDownAt = now;
                else if (!_escHoldDone && now - _escDownAt >= EscHoldCloseAll) { _escHoldDone = true; CloseAllMenus("ESC gehalten"); return; }
            }
            else { _escDownAt = -1f; _escHoldDone = false; }

            bool esc = Input.GetKeyDown(KeyCode.Escape);
            bool bsp = Input.GetKeyDown(KeyCode.Backspace) && top != MenuLayer.LifeText;   // Rücktaste löscht im Textfeld
            if (esc || bsp)
            {
                RacingSchoolMenu.NoteUiClosed();   // dieses ESC öffnet nicht zusätzlich das Pausemenü
                if (_backLayer == top && now - _backAt < BackMaxSeconds) { ForceCloseLayer(top, "ESC erneut"); return; }
                _backLayer = top; _backAt = now; _backChangeAt = now; _backChanged = false;
                _backSig = MenuSignature();
                MenuBack(top);
                return;
            }
#endif
            // Läuft ein Zurück? Ebene gewechselt = geschafft; Film reagiert und kommt zur Ruhe = geschafft;
            // keine Reaktion bzw. zu lange = Spiel schließt selbst
            if (_backLayer == MenuLayer.None) return;
            if (top != _backLayer) { _backLayer = MenuLayer.None; return; }
            var sig = MenuSignature();
            if (sig != _backSig) { _backSig = sig; _backChangeAt = now; _backChanged = true; }
            if (_backChanged && now - _backChangeAt >= 0.6f) { _backLayer = MenuLayer.None; return; }
            if (!_backChanged && now - _backAt >= BackNoReactionSeconds) ForceCloseLayer(top, "Film reagiert nicht");
            else if (now - _backAt >= BackMaxSeconds) ForceCloseLayer(top, "Zeitüberschreitung");
        }

        /// <summary>Normaler Weg zurück (wie die B-Taste im Spiel).</summary>
        void MenuBack(MenuLayer l)
        {
            Debug.Log("[TDU2] Menü zurück: " + l);
            if (_ui != null) _ui.SetValue("/:Input", true);
            switch (l)
            {
                case MenuLayer.Casino:
                    if (_ui == null || !_ui.Input("B")) CloseCasinoMenu();
                    break;
                case MenuLayer.DealerCar:
                case MenuLayer.DealerWindow:
                case MenuLayer.Dealer:
                    DealerBack();
                    break;
                case MenuLayer.LifePhoto:
                    CloseLifePhotoTaker();
                    break;
                case MenuLayer.LifeText:
                    _textVar = null;
                    if (_ui != null) _ui.Input("B");
                    break;
                case MenuLayer.MyLife:
                case MenuLayer.Diary:
                    if (_ui == null || !_ui.Input("B")) CloseDiary();
                    break;
                case MenuLayer.HouseEditor:
                    if (_ui == null || !_ui.Input("B")) ForceHouseWalk();
                    break;
                case MenuLayer.House:
                    QuitMyHouse();
                    break;
            }
        }

        /// <summary>Ebene ohne Mitwirkung des Films schließen und einen sauberen Zustand herstellen.</summary>
        void ForceCloseLayer(MenuLayer l, string why)
        {
            Debug.Log("[TDU2] Menü " + l + " vom Spiel geschlossen (" + why + ")");
            _backLayer = MenuLayer.None;
            RacingSchoolMenu.NoteUiClosed();
            switch (l)
            {
                case MenuLayer.Casino:
                    CloseCasinoMenu();
                    break;
                case MenuLayer.DealerCar:
                case MenuLayer.DealerWindow:
                case MenuLayer.Dealer:
                    DealerForceClose();
                    break;
                case MenuLayer.LifePhoto:
                    EndLifePhoto();
                    if (_ui != null)
                    {
                        _ui.GotoLabel("/mylf/photo_taker", "default", false);
                        _ui.SetValue("/:LockMyLifeEdition", false);
                        _ui.SetValue("/:Input", true);
                    }
                    break;
                case MenuLayer.LifeText:
                    _textVar = null;
                    TDU2GameData.ExternalTextInput = false;
                    if (_ui != null) { _ui.GotoLabel("/mylf/vk", "default", false); _ui.SetValue("/:Input", true); }
                    break;
                case MenuLayer.MyLife:
                case MenuLayer.Diary:
                    CloseDiary();   // speichert MyTDULife, beendet Foto/Texteingabe, Laufmodus
                    break;
                case MenuLayer.HouseEditor:
                    ForceHouseWalk();
                    break;
                case MenuLayer.House:
                    ForceEndMyHouse();
                    break;
            }
        }

        /// <summary>Notausgang: alle Ebenen von oben nach unten schließen.</summary>
        void CloseAllMenus(string why)
        {
            for (int i = 0; i < 8; i++)
            {
                var top = TopMenuLayer();
                if (top == MenuLayer.None) break;
                ForceCloseLayer(top, why);
            }
            if (TDU2AccessoryMenu.IsOpen) TDU2AccessoryMenu.Close(false);
            RestoreWalkState();
        }

        /// <summary>My House ohne Film-Rückmeldung verlassen (Editor verwerfen, Wohnbereich wiederherstellen).</summary>
        void ForceEndMyHouse()
        {
            if (_myHouse == HouseMode.None) return;
            RevertEdit();
            EndMyHouse();
            if (_ui != null)
            {
                _ui.GotoLabel("/myhs_editor", "default", false);
                _ui.SetValue("/:Input", true);
            }
            _houseRestoreAt = Time.unscaledTime;   // nächstes Bild: Laufmodus-Oberfläche (UpdateMyHouse)
        }

        /// <summary>Laufmodus: Film auf den Ruhe-Label, nur Fadenkreuz/Titel sichtbar, Eingabe frei, Maus gefangen.</summary>
        void RestoreWalkState()
        {
            CasinoMenuOpen = false;
            TDU2GameData.ExternalTextInput = false;
            if (!TestDriveActive) TDU2Minimap.HudOverlay = false;
            if (_ui != null && TopMenuLayer() == MenuLayer.None && _kind != Kind.StickerShop)
            {
                string stop = _spotBanks != null && _spotBanks.IsCasino ? "casino_hall" : _kind == Kind.Dealer ? "cd_stop" : _kind == Kind.Garage ? "garage_stop" : "living_stop";
                if (_ui.HasLabel("/", stop) && _ui.Label("/") != stop) _ui.GotoLabel("/", stop, false);
                foreach (var d in _ui.Root.List.Values) d.Visible = d.Name != null && UiVisible.Contains(d.Name);
                _ui.GotoLabel("/cursor", "valid", true);
                _ui.SetValue("/:Input", true);
            }
            _uiAim = null;
            if (Inside) { LockCursor(true); if (_fp != null) { _fp.enabled = true; _fp.LookEnabled = true; } }
        }

        /// <summary>Kein Menü offen: verwaiste Sperren aufräumen (einmal je Sekunde, nach kurzer Ruhezeit).</summary>
        void HealIdle(float now)
        {
            if (_noMenuSince < 0f) _noMenuSince = now;
            if (now < _nextHeal || now - _noMenuSince < 1f) return;
            _nextHeal = now + 1f;
            if (_houseRestoreAt > 0f || TDU2StickerShop.Busy || TDU2ClothesShop.Busy || TDU2Hairdresser.Busy || _menuOpen) return;
            if (CasinoMenuOpen) { CasinoMenuOpen = false; Debug.Log("[TDU2] Menüsperre ohne offenes Menü aufgehoben"); }
            if (TDU2GameData.ExternalTextInput) TDU2GameData.ExternalTextInput = false;
            if (!TestDriveActive && TDU2Minimap.HudOverlay) TDU2Minimap.HudOverlay = false;
            if (_carIn != CarIn.None) EndDealerCar();
            if (_lifePhoto) EndLifePhoto();
            if (_ui != null && _kind != Kind.StickerShop && _ui.GetVariable("/:Input") == "false") _ui.SetValue("/:Input", true);
        }
    }
}
