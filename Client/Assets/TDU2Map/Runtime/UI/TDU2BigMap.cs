using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TDU2.Map.Flash;
using TDU2.Map.Formats;
using TDU2.Map.IO;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// Große Karte (Taste M) wie im Original (TestDrive2Dev.exe, Spielmodus GM_MAP):
    ///   * keine Bildkarte – eine Kamera schaut von oben auf die echte 3D-Insel (LOD2-Insel + geladene Areas/Sektoren).
    ///     Zoomstufen _root.WhatZoom 4 (Insel) .. 1 (nah); je Stufe Sichtbreite und Neigung (exe: "Inclination (island/far/
    ///     medium/close/very close zoom)", Breiten 0,5×Insel bzw. 768 m-Vielfache; Neigungen sind Tweak-Werte -> hier einstellbar).
    ///   * darüber frontend/&lt;res&gt;/map.bnk (map.vmf): Cursor (/curseur), Spieler (/player), Spot-Symbole als Kopien von
    ///     /pack/point ("/pack/point%03d", Label des Symbols in point/spot wie im Spiel), Tooltip (/tool_tip).
    ///   * Film-Protokoll: "map_open" abspielen, Bild 17 meldet "maok"; Zoom über setZoom("in"/"out") -> FSCommand zoin/zoou,
    ///     das Spiel setzt _root.WhatZoom; "close" spielt die Schließanimation.
    /// Bedienung: WASD/Pfeile oder linke Maustaste ziehen = verschieben, Mausrad/Bild↑↓/+/- = Zoom, Leertaste = zum Fahrzeug,
    /// M/ESC/Rücktaste = schließen.
    /// </summary>
    public sealed class TDU2BigMap : MonoBehaviour
    {
        public TDU2VehicleManager Manager;
        public static bool IsOpen { get; private set; }

        [Header("Kamera je Zoomstufe (WhatZoom 1 = nah … 4 = ganze Insel)")]
        [Tooltip("Sichtbreite in Metern; Stufe 4 = ganze Insel (wird aus der Inselgröße berechnet, wenn 0)")]
        public float[] ViewWidth = { 1536f, 6144f, 0f, 0f };
        [Tooltip("Neigung in Grad (0 = senkrecht von oben)")]
        public float[] Inclination = { 45f, 25f, 0f, 0f };
        [Tooltip("Anteil der Inselgröße für Stufe 3 (exe: 0,5)")]
        public float FarFactor = 0.5f;
        public float FieldOfView = 40f;
        [Tooltip("Geschwindigkeit der Zoom-/Kamerafahrt")]
        public float Smooth = 4f;
        [Tooltip("Verschieben mit Tasten: Bildschirmbreiten pro Sekunde")]
        public float PanSpeed = 0.25f;
        [Tooltip("Hintergrund außerhalb der Insel (Meer)")]
        public Color SeaColor = new Color32(0, 18, 47, 255); //new Color32(18, 54, 82, 255);
        [Tooltip("Größe der Spot-Symbole")]
        public float SpotScale = 0.75f;
        public int MaxSpots = 160;

        VmfPlayer _ui;
        string _bnkDir, _bank;
        Task<Dictionary<string, string>> _textTask;
        Dictionary<string, string> _texts;
        Camera _cam, _mainCam;
        bool _open, _closing; float _closeAt;
        int _zoom = 3;                         // WhatZoom
        Vector3 _target;                       // Kartenmitte (Weltkoordinaten, Höhe 0 = Meer)
        float _curWidth, _curIncl;
        float _savedTimeScale = 1f; bool _savedFog, _savedMinimap, _savedHud = true, _savedCursor; CursorLockMode _savedLock;
        int _pointChar; int _pointDepth0 = 1000;
        /// <summary>Spot auf der Karte: normaler Spot, Meisterschaft (cup = Index) oder – im Wettbewerbsmodus – deren Rennen (race).</summary>
        sealed class MapSpot { public Spot spot; public string label; public int cup = -1, race = -1; public int bookmark = -1; public bool gps; }
        readonly List<MapSpot> _spots = new List<MapSpot>();
        // Wettbewerbsmodus (map.vmf: IsContest, "contest"/"co_direct_enter", FSCommands coin/coou/giup)
        /// <summary>Gewählter Reiter der Filterleiste oben links (/presets, _root.numIcFilter): 0 Einzelspieler-Herausforderungen,
        /// 1 Multiplayer-Herausforderungen, 2 Orte für Avatar, 3 Orte für Fahrzeuge, 4 Spieler, 5 eigene Orte (Lesezeichen). Bleibt bis zum nächsten Öffnen.</summary>
        static int _filter;
        int _contestCup = -1;            // Meisterschaft, deren Rennen gezeigt werden (-1 = normale Karte)
        bool _contestDirect;             // vom Treffpunkt aus geöffnet: zurück = Karte schließen
        int _contestEnterCup = -1;       // nach dem Öffnen in den Wettbewerb wechseln (wartet auf das Ende von map_open)
        float _contestEnterAt;
        (int cup, int race) _pendingStart = (-1, -1);   // nach dem Schließen dieses Rennen starten
        static Racing.TDU2Championships Champs => Racing.TDU2Championships.Instance;
        readonly Dictionary<int, string> _slotLabel = new Dictionary<int, string>();
        int _hover = -1;
        bool _sysCursor;
        bool? _cibleOff; bool _cursorOverSpot;
        int _openFrame = -10;   // Taste, mit der geöffnet wurde (Enter am Treffpunkt, M), nicht gleich als Klick werten
        bool _lod;
        Vector3 _lastMouse, _downMouse; bool _dragging;
        int _selected = -1; Vector3? _cursorWorld;
        // Mini-Menü der freien Fläche (/mini_menu: Teleport, GPS-Ziel, Lesezeichen) und Rechtsklick = GPS-Ziel
        bool _miniMenu, _menuTargetPresent; Vector3? _menuWorld; Vector3 _downMouseR;
        bool _extended; float _fillAt = -1f;
        // Mitspieler (TDU2GameData): Blips "player" als weitere Kopien von /pack/point (Tiefen ab _pointDepth0 + MaxSpots)
        readonly Dictionary<int, int> _playerSlots = new Dictionary<int, int>();   // Spieler-Id -> Slot
        int _hoverPlayer = -1, _selectedPlayer = -1;
        [Tooltip("FSCommands der Karte in der Konsole protokollieren")]
        public bool LogCommands = true;

        [Header("Darstellung / Leistung")]
        [Tooltip("Straßennetz (wie Minimap/Original-Karte) über die 3D-Insel legen")]
        public bool ShowRoads = true;
        [Tooltip("Mindestbreite einer Straße auf dem Bildschirm (Pixel)")]
        public float RoadMinPixels = 1f;
        [Tooltip("3D-Welt nur neu zeichnen, wenn sich die Kartenkamera bewegt (sonst stehendes Bild) – spart sehr viel GPU-Last")]
        public bool RenderOnDemand = true;
        [Tooltip("Neu zeichnen spätestens nach so vielen Sekunden (nachgeladene Kacheln)")]
        public float RefreshSeconds = 1f;
        [Tooltip("Bildrate während der Karte begrenzen (0 = nicht begrenzen)")]
        public int MapFrameRate = 60;
        [Tooltip("Ab dieser Zoomstufe (WhatZoom) Gebäude/Objekte der nahen Sektoren nicht zeichnen (1 = immer, 5 = nie)")]
        public int HideSectorObjectsFromZoom = 2;
        [Tooltip("Ab dieser Zoomstufe das Kartengelände zeigen (alle Areas mit eigener Colormap, wie die Original-Karte) statt der " +
                 "Spielwelt, deren ferne Teile nur die grobe Insel-LOD sind (0 = nie)")]
        public int MapTerrainFromZoom = 1;
        const int MapLayer = 30;           // nur für die Kartenkamera (Graphics.DrawMesh), keine GameObjects auf dieser Ebene
        int _worldMask = ~0; bool _rtMapTerrain;

        RenderTexture _rt;
        Camera _clearCam;
        Vector3 _rtPos; Quaternion _rtRot; float _rtWidth; float _rtNext; bool _rtDirty;
        readonly Dictionary<int, (Mesh mesh, Vector3 origin)> _roadMeshes = new Dictionary<int, (Mesh, Vector3)>();
        readonly List<Renderer> _hiddenObjects = new List<Renderer>();
        readonly List<Light> _hiddenLights = new List<Light>();
        bool _objectsHidden; int _savedFps; bool _fpsSaved;

        //TODO: Bigmap -> Maxmimale Zoom Stufe play ui.PlayLabel("/credits", "default"); <- read txt file
        //Markers New Player player, recent (player mit !), friend, player_g (group),
        //club unowned, my_club owned 
        //forsale haus nicht im Besitz, home im besitz
        void Update()
        {
            if (Manager == null) Manager = GetComponent<TDU2VehicleManager>();
            EnsureLoaded();
#if ENABLE_LEGACY_INPUT_MANAGER
            if (!_open)
            {
                var pm = GetComponent<TDU2PauseMenu>();
                if (pm != null && pm.IsOpen) return;
                if (Manager != null && Manager.Suspended) return;   // im Interieur keine Karte
                if (TDU2Controls.Pressed("map") && _ui != null && Manager?.Streamer?.MapRoot != null) Open();
                return;
            }
            if (_contestEnterCup >= 0 && !_closing && (Time.unscaledTime >= _contestEnterAt || (_ui.Root != null && _ui.Root.Frame >= 60)))
            {
                // Treffpunkt: wie im Spiel direkt in den Wettbewerb der Meisterschaft (nach dem Aufbau der Karte)
                int c = _contestEnterCup; _contestEnterCup = -1;
                _ui.SetValue("/:ContestDirectAccess", true);
                _ui.SetValue("/:IsFirstTime", 0);
                _ui.GotoLabel("/", "co_direct_enter", true);
                EnterContest(c);
            }
            if (_closing)
            {
                if (Time.unscaledTime >= _closeAt) Finish();
                return;
            }
            if (TDU2Controls.Pressed("map")) { Close(); return; }
            // Zurück wie im Spiel an den Film (Tooltip/Mini-Menü schließen bzw. Karte: "gofr"); reagiert er nicht, direkt schließen
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace)) { if (!_ui.Input("B")) Close(); return; }
            HandleInput();
#endif
            UpdateCamera(false);
            RenderMap();
            UpdateOverlay();
            if (_fillAt > 0f && Time.unscaledTime >= _fillAt) { _fillAt = -1f; FillExtendedInfos(); }
        }

        // ------------------------------------------------------------------ Laden
        void EnsureLoaded()
        {
            string bnkDir = Manager != null ? Manager.BnkDir : null;
            if (string.IsNullOrEmpty(bnkDir)) return;
            if (_bnkDir != bnkDir)
            {
                _bnkDir = bnkDir;
                string lang = TDU2Options.LangCodes[Mathf.Clamp(PlayerPrefs.GetInt("TDU2.Opt.WhatLanguage", Array.IndexOf(TDU2Options.LangCodes, FlashLocalization.LangCode(Application.systemLanguage))), 0, TDU2Options.LangCodes.Length - 1)];
                _textTask = Task.Run(() => FlashLocalization.Load(bnkDir, lang));
                _bank = null;
                foreach (var res in new[] { "hires", "medres", "lowres" })
                {
                    _bank = CarDatabase.FindFile(Path.Combine(bnkDir, "frontend", res), "map.bnk");
                    if (_bank != null) break;
                }
                if (_bank == null) { DebugManager.Error("frontend/<res>/map.bnk nicht gefunden"); return; }
                try
                {
                    if (_ui == null) { _ui = gameObject.AddComponent<VmfPlayer>(); _ui.GuiDepth = -8; _ui.MouseEnabled = true; }
                    _ui.Load(_bank);
                    _ui.Visible = false;
                    _ui.FSCommand += OnCommand;
                }
                catch (Exception ex)
                {
                    DebugManager.Error("map.bnk nicht nutzbar: " + ex.Message);
                    if (_ui != null) { Destroy(_ui); _ui = null; }
                }
            }
            if (_texts == null && _textTask != null && _textTask.IsCompleted && _ui != null)
            {
                _texts = _textTask.Status == TaskStatus.RanToCompletion ? _textTask.Result : new Dictionary<string, string>();
                FlashLocalization.Apply(_ui, _texts);
            }
        }

        // ------------------------------------------------------------------ Öffnen / Schließen
        public void Open() { OpenMap(-1, false); }

        /// <summary>
        /// Karte im Wettbewerbsmodus einer Meisterschaft öffnen (Index wie TDU2Championships): zeigt nur deren Rennen,
        /// Auswahl startet das Rennen. direct = vom Treffpunkt aus (zurück schließt die Karte, ContestDirectAccess).
        /// </summary>
        public bool OpenContest(int cupIndex, bool direct)
        {
            if (_open || _ui == null || cupIndex < 0 || Champs == null) return false;
            OpenMap(cupIndex, direct);
            return _open;
        }

        void OpenMap(int contestCup, bool direct)
        {
            if (_open || _ui == null) return;
            var root = Manager?.Streamer?.MapRoot;
            if (root == null) return;
            _open = true; _closing = false; IsOpen = true;
            _contestCup = -1; _contestDirect = direct && contestCup >= 0; _contestEnterCup = -1; _pendingStart = (-1, -1);
            _openFrame = Time.frameCount;

            // Spiel anhalten wie das Pausemenü, HUD aus
            _savedTimeScale = Time.timeScale > 0f ? Time.timeScale : 1f;
            Time.timeScale = 0f;
            AudioListener.pause = true;
            _savedFog = RenderSettings.fog; RenderSettings.fog = false;
            var mm = Manager.Minimap;
            if (mm != null) { _savedMinimap = mm.ShowMinimap; mm.ShowMinimap = false; }
            var hud = Manager.GetComponent<TDU2VehicleHud>();
            if (hud != null) { _savedHud = hud.ShowHud; hud.ShowHud = false; }
            _savedLock = Cursor.lockState; _savedCursor = Cursor.visible;
            Cursor.lockState = CursorLockMode.None; Cursor.visible = false;   // Kartencursor des Films statt Mauszeiger
            _sysCursor = false;

            MapPerformance(true);

            // Kamera
            _mainCam = Camera.main;
            if (_cam == null)
            {
                var go = new GameObject("TDU2 Map Camera");
                go.transform.SetParent(transform, false);
                _cam = go.AddComponent<Camera>();
            }
            if (_mainCam != null) { _cam.CopyFrom(_mainCam); _mainCam.enabled = false; }
            _worldMask = _cam.cullingMask & ~(1 << MapLayer);
            Manager.Streamer.RequestMapTerrain();   // falls noch nicht im Hintergrund geladen
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = SeaColor;
            _cam.fieldOfView = FieldOfView;
            _cam.depth = (_mainCam != null ? _mainCam.depth : 0f) + 1f;
            if (RenderOnDemand)
            {
                // Kartenkamera zeichnet in ein Bild (nur bei Bewegung neu); eine leere Kamera hält den Bildschirm sauber
                _cam.enabled = false;
                EnsureTarget();
                if (_clearCam == null)
                {
                    var cg = new GameObject("TDU2 Map Clear Camera");
                    cg.transform.SetParent(transform, false);
                    _clearCam = cg.AddComponent<Camera>();
                    _clearCam.cullingMask = 0;
                    _clearCam.useOcclusionCulling = false;
                    _clearCam.allowHDR = false; _clearCam.allowMSAA = false;
                }
                _clearCam.clearFlags = CameraClearFlags.SolidColor;
                _clearCam.backgroundColor = SeaColor;
                _clearCam.depth = _cam.depth;
                _clearCam.enabled = true;
                _rtDirty = true;
            }
            else { _cam.targetTexture = null; _cam.enabled = true; }
            if (MapFrameRate > 0 && (Application.targetFrameRate <= 0 || Application.targetFrameRate > MapFrameRate))
            {
                _savedFps = Application.targetFrameRate; _fpsSaved = true;
                Application.targetFrameRate = MapFrameRate;
            }

            var veh = Manager.Current;
            _target = veh != null ? veh.transform.position : (_mainCam != null ? _mainCam.transform.position : root.position);
            _target.y = 0f;
            _zoom = 2;
            UpdateCamera(true);

            // Film-Zustand wie vom Spiel vor "map_open"
            _ui.SetValue("/:WhatPlatform", 1);
            _ui.SetVariable("/:MenuType", "pc");
            _ui.SetValue("/:IsPC_X360", true);
            _ui.SetValue("/:fake", false);
            _ui.SetValue("/:MapMode", 0);
            _ui.SetValue("/:WhatZoom", _zoom);
            _ui.SetValue("/:Input", true);
            _ui.SetValue("/:IsContest", false);
            _ui.SetValue("/:ContestDirectAccess", _contestDirect);
            _ui.SetValue("/:IsFirstTime", 0);
            _ui.SetValue("/:LockGiveUp", 0);
            _ui.SetValue("/:IsResultsDone", 1);
            _ui.SetVariable("/:WhatSpot", "noSpot");
            _ui.SetVariable("/:WhatSpotType", "none");
            _ui.SetValue("/:IsTutorial", 0);
            _ui.SetValue("/:IsTargetOK", contestCup < 0);   // A auf freier Fläche öffnet im Film das Mini-Menü (Teleport/GPS/Lesezeichen)
            _ui.SetValue("/:IsTeleportOK", true);
            _ui.SetValue("/:IsTargetPresent", false);
            _ui.SetValue("/:IsSpotFavorite", false);
            // Filterleiste (/presets öffnet sich im Film selbst): Reiter + Sperren wie vom Spiel gesetzt (ohne numIcFilter zeigte der Titel "..")
            _ui.SetValue("/:numIcFilter", _filter);
            foreach (var lk in new[] { "LockPresetSolo", "LockPresetMulti", "LockPresetAvSpots", "LockPresetVeSpots", "LockPresetPlayers", "LockPresetMySpots" })
                _ui.SetValue("/:" + lk, false);
            _miniMenu = false; _menuWorld = null;
            _gpsMarkerShown = false; _ui.SetVisible("/target", false);
            // Kartencursor, Geister-Cursor und GPS-Ziel liegen unter der Maus: deren Knöpfe nicht als "Maus über Knopf" werten
            // (sonst wechselte der Cursor jedes Bild zwischen Film- und Windows-Zeiger = Flackern, und Klicks liefen doppelt)
            _ui.MouseIgnore.Add("/curseur"); _ui.MouseIgnore.Add("/curseur_ghost"); _ui.MouseIgnore.Add("/target");
            _cibleOff = null; _cursorOverSpot = false;
            _ui.SetValue("/:CanAddMySpot", TDU2Bookmarks.CanAdd);
            _ui.SetVariable("/:lockGotoStatus", "none");
            _ui.SetVariable("/:money", Online.TDU2Session.LoggedIn ? Online.TDU2Session.Save.money.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " $" : "999 999 999 $");
            _ui.SetVariable("/:cur_upgrade", "level0");
            _ui.SetVariable("/:player_group", GroupLabel(veh?.Car?.Raw != null ? veh.Car.Raw.I("_vehiclecategory") : 0));
            if (veh != null && veh.Car != null)
            {
                _ui.SetVariable("/:player_car", ((veh.Car.Brand ?? "") + " " + veh.Car.ModelName).Trim());
                _ui.SetVariable("/:m_MapPlayerVehicle", ((veh.Car.Brand ?? "") + " " + veh.Car.ModelName).Trim());
            }
            _ui.Visible = true;
            _ui.GotoLabel("/", "map_open", true);
            // Laufschrift des Autonamens neu messen (Bild 0 von /infos/car liest _root.player_car per getTextLength)
            _ui.GotoLabel("/infos/car", "empty", false);
            _ui.GotoLabel("/infos/car", "set", false);
            _extended = false;
            BuildSpots();
            _lod = false; ApplyLod(true);
            if (contestCup >= 0) { _contestEnterCup = contestCup; _contestEnterAt = Time.unscaledTime + 1.3f; }
        }

        // ------------------------------------------------------------------ Wettbewerbsmodus (Meisterschaft)
        void EnterContest(int cup)
        {
            var ch = Champs;
            if (ch == null || cup < 0) return;
            var mc = ch.MapCupAt(cup);
            if (mc == null) return;
            _contestCup = cup;
            HideTooltip(); _extended = false;
            SetContestVars(cup);
            _ui.SetVariable("/:ContestName", mc.Name);
            _ui.SetVariable("/:ContestType", mc.ContestType);
            _ui.SetValue("/:IsContest", true);
            BuildSpots();
            // Kamera auf die Rennen der Meisterschaft
            var root = Manager.Streamer.MapRoot;
            var b = new Bounds(root.TransformPoint(mc.Hub), Vector3.zero);
            foreach (var r in ch.MapRaces(cup)) b.Encapsulate(root.TransformPoint(r.Start));
            _target = b.center; _target.y = 0f;
            float ext = Mathf.Max(b.size.x, b.size.z) * 1.35f + 400f;
            int z = 1; while (z < 4 && WidthFor(z) < ext) z++;
            SetZoom(z);
            TDU2DebugLog.Event("Karte: Wettbewerb " + mc.Name + " (" + mc.RaceCount + " Rennen, " + mc.RacesDone + " gefahren)" + (_contestDirect ? " vom Treffpunkt" : ""));
        }

        List<Racing.TDU2Championships.MapRace> _raceCache; int _raceCacheCup = -1, _raceCacheFrame = -1;
        /// <summary>Rennen eines Wettbewerbs-Spots (Status/Platz), sonst null.</summary>
        Racing.TDU2Championships.MapRace RaceOf(MapSpot ms)
        {
            if (ms == null || ms.cup < 0 || ms.race < 0 || Champs == null) return null;
            if (_raceCacheCup != ms.cup || _raceCacheFrame != Time.frameCount) { _raceCache = Champs.MapRaces(ms.cup); _raceCacheCup = ms.cup; _raceCacheFrame = Time.frameCount; }
            return ms.race < _raceCache.Count ? _raceCache[ms.race] : null;
        }

        void LeaveContest()
        {
            if (_contestCup < 0) return;
            _contestCup = -1;
            HideTooltip(); _extended = false;
            _ui.SetValue("/:IsContest", false);
            BuildSpots();
        }

        /// <summary>Variablen für Tooltip/Infos der Meisterschaft (infos.setContests, setRankings, Restriction).</summary>
        void SetContestVars(int cup)
        {
            var ch = Champs; if (ch == null) return;
            var mc = ch.MapCupAt(cup); if (mc == null) return;
            var races = ch.MapRaces(cup);
            _ui.SetValue("/:nb_chall", Mathf.Min(8, races.Count));
            for (int i = 0; i < 8; i++)
            {
                _ui.SetVariable("/:chall" + i + "_type", i < races.Count ? races[i].Label : "empty");
                _ui.SetValue("/:chall" + i + "_state", i < races.Count ? races[i].State : 0);
            }
            var st = ch.MapStandings(cup);
            int me = 0;
            for (int i = 0; i < 8; i++)
            {
                bool has = i < st.Count;
                _ui.SetVariable("/:rank" + i + "_pos", has ? (i + 1).ToString() : "");
                _ui.SetVariable("/:rank" + i + "_name", has ? st[i].name : "");
                _ui.SetVariable("/:rank" + i + "_pts", has ? st[i].points + " " + Txt("m_Pts_", "Pkt.") : "");
                if (has && st[i].player) me = i;
            }
            _ui.SetValue("/:PlayerRank", me);
            _ui.SetValue("/:IsResultsDone", mc.RacesDone > 0 ? 1 : 0);
            _ui.SetValue("/:SelectedContestGroup", 1);
            _ui.SetValue("/:num_contest", 1);
            _ui.SetVariable("/:GoldReward", mc.ChampReward > 0 ? Money(mc.ChampReward) : "");
            _ui.SetVariable("/:RestrictionType", "category");
            _ui.SetVariable("/:GroupRestriction", mc.Group ?? "");
            _ui.SetVariable("/:ChampRival", ch.RivalName(cup) ?? "");
            _ui.SetVariable("/:LevelCount", mc.RacesDone + "/" + mc.RaceCount);
        }

        static string Money(long v) => v.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " $";
        string Txt(string key, string fallback) => _texts != null && _texts.TryGetValue(key, out var t) && !string.IsNullOrEmpty(t) ? t.Trim() : fallback;

        // ------------------------------------------------------------------ Leistung: die Karte zeigt die ganze Insel von oben
        [Tooltip("Während der Karte: Schatten aus, niedrige LOD-Stufen, Gras/Vegetation aus (die Karte ruckelt sonst stark).")]
        public bool FastMapRendering = true;
        [Tooltip("LOD-Bias während der Karte (kleiner = gröbere Modelle).")]
        public float MapLodBias = 0.2f;
        ShadowQuality _savedShadows; float _savedLodBias; bool _perfOn;
        readonly List<Behaviour> _perfDisabled = new List<Behaviour>();

        void MapPerformance(bool on)
        {
            if (!FastMapRendering || on == _perfOn) return;
            _perfOn = on;
            if (on)
            {
                _savedShadows = QualitySettings.shadows; QualitySettings.shadows = ShadowQuality.Disable;
                _savedLodBias = QualitySettings.lodBias; QualitySettings.lodBias = Mathf.Min(_savedLodBias, MapLodBias);
                _perfDisabled.Clear();
#if UNITY_2023_1_OR_NEWER
                var grass = FindObjectsByType<TDU2.Map.World.GrassRenderer>(FindObjectsSortMode.None);
                var veg = FindObjectsByType<TDU2.Map.World.VegetationRenderer>(FindObjectsSortMode.None);
#else
                var grass = FindObjectsOfType<TDU2.Map.World.GrassRenderer>();
                var veg = FindObjectsOfType<TDU2.Map.World.VegetationRenderer>();
#endif
                foreach (var b in grass) if (b != null && b.enabled) { b.enabled = false; _perfDisabled.Add(b); }
                foreach (var b in veg) if (b != null && b.enabled) { b.enabled = false; _perfDisabled.Add(b); }
                // Punkt-/Spotlichter (Laternen, Scheinwerfer) kosten von oben über die ganze Insel viel und sieht man nicht
                _hiddenLights.Clear();
#if UNITY_2023_1_OR_NEWER
                var lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
#else
                var lights = FindObjectsOfType<Light>();
#endif
                foreach (var l in lights) if (l != null && l.enabled && l.type != LightType.Directional) { l.enabled = false; _hiddenLights.Add(l); }
            }
            else
            {
                QualitySettings.shadows = _savedShadows;
                QualitySettings.lodBias = _savedLodBias;
                foreach (var b in _perfDisabled) if (b != null) b.enabled = true;
                _perfDisabled.Clear();
                foreach (var l in _hiddenLights) if (l != null) l.enabled = true;
                _hiddenLights.Clear();
                HideSectorObjects(false);
            }
        }

        /// <summary>Gebäude/Objekte der nahen Sektoren (LOD0) aus-/einblenden; Gelände und Areas (LOD1) bleiben.</summary>
        void HideSectorObjects(bool hide)
        {
            if (hide == _objectsHidden) return;
            _objectsHidden = hide;
            if (!hide)
            {
                foreach (var r in _hiddenObjects) if (r != null) r.forceRenderingOff = false;
                _hiddenObjects.Clear();
                return;
            }
            var root = Manager?.Streamer?.MapRoot;
            if (root == null) return;
            _hiddenObjects.Clear();
            foreach (Transform t in root)
            {
                if (t == null || !t.name.EndsWith("(LOD0)")) continue;
                foreach (Transform c in t)
                {
                    if (c.name == "Terrain") continue;
                    foreach (var r in c.GetComponentsInChildren<Renderer>())
                        if (r != null && !r.forceRenderingOff) { r.forceRenderingOff = true; _hiddenObjects.Add(r); }
                }
            }
        }

        void EnsureTarget()
        {
            int w = Mathf.Max(16, Screen.width), h = Mathf.Max(16, Screen.height);
            if (_rt != null && _rt.width == w && _rt.height == h) { _cam.targetTexture = _rt; return; }
            if (_rt != null) { _cam.targetTexture = null; _rt.Release(); Destroy(_rt); }
            _rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32) { name = "TDU2 Map", hideFlags = HideFlags.DontSave, antiAliasing = 1 };
            _rt.Create();
            _cam.targetTexture = _rt;
            _rtDirty = true;
        }

        /// <summary>Straßennetz in der zur Sichtbreite passenden Breite (Stufen ×2, gecacht).</summary>
        (Mesh mesh, Vector3 origin) RoadMesh()
        {
            var mm = Manager?.Minimap;
            if (mm == null || !mm.HasRoads || _cam == null) return (null, default);
            float px = Mathf.Max(1f, _cam.pixelWidth);
            float metersPerPixel = _curWidth / px;
            float mul = Mathf.Max(1f, RoadMinPixels * metersPerPixel / 7f);   // 7 m = übliche Straßenbreite
            int lvl = Mathf.Clamp(Mathf.CeilToInt(Mathf.Log(mul, 2f) - 1e-3f), 0, 8);
            if (_roadMeshes.TryGetValue(lvl, out var e) && e.mesh != null) return e;
            var m = mm.BuildMapRoads(Mathf.Pow(2f, lvl), out var o);
            if (m == null) return (null, default);
            e = (m, o);
            _roadMeshes[lvl] = e;
            return e;
        }

        /// <summary>Maus über der Filterleiste bzw. Infoleiste des Films (Bildschirmkoordinaten wie Input.mousePosition)?</summary>
        bool OverFilmBar(Vector3 mouse)
        {
            if (_ui == null) return false;
            var g = new Vector2(mouse.x, Screen.height - mouse.y);
            // feste Lage im Film (16:9): Filterleiste oben links, Auto/Geld oben rechts, Tastenleiste unten
            float w = Screen.width, h = Screen.height;
            if ((g.x < w * 0.45f && g.y < h * 0.2f) || (g.x > w * 0.62f && g.y < h * 0.18f) || g.y > h * 0.87f) return true;
            return false;
        }

        /// <summary>Kartengelände statt Spielwelt? (Zoomstufe und alle Kacheln fertig)</summary>
        bool UseMapTerrain()
        {
            var st = Manager?.Streamer;
            return MapTerrainFromZoom > 0 && _zoom >= MapTerrainFromZoom && st != null && st.MapTerrainReady;
        }

        /// <summary>Kartenkacheln + Meer nur in die Kartenkamera zeichnen.</summary>
        void DrawMapTerrain()
        {
            var st = Manager?.Streamer; var root = st?.MapRoot;
            if (root == null) return;
            var m = root.localToWorldMatrix;
            foreach (var t in st.MapTiles)
                if (t.Mesh != null && t.Material != null)
                    Graphics.DrawMesh(t.Mesh, m * Matrix4x4.Translate(t.Origin), t.Material, MapLayer, _cam, 0, null, false, false);
            // kein Meer darüber: die Colormaps enthalten die Meeresfarben (Türkis/Tiefblau) selbst, wie die Original-Karte
        }

        void DrawRoads(int layer = 0)
        {
            if (!ShowRoads || _cam == null) return;
            var mm = Manager?.Minimap; var root = Manager?.Streamer?.MapRoot;
            if (mm == null || root == null) return;
            var mat = mm.RoadMaterial;
            if (mat == null) return;
            var (mesh, origin) = RoadMesh();
            if (mesh == null) return;
            Graphics.DrawMesh(mesh, Matrix4x4.Translate(root.position - origin), mat, layer, _cam, 0, null, false, false);
        }

        void RenderMap()
        {
            if (_cam == null) return;
            bool mapTerrain = UseMapTerrain();
            _cam.cullingMask = mapTerrain ? 1 << MapLayer : _worldMask;
            int layer = mapTerrain ? MapLayer : 0;
            HideSectorObjects(!mapTerrain && FastMapRendering && _zoom >= HideSectorObjectsFromZoom);
            if (!RenderOnDemand) { if (mapTerrain) DrawMapTerrain(); DrawRoads(layer); return; }
            EnsureTarget();
            if (mapTerrain != _rtMapTerrain) { _rtMapTerrain = mapTerrain; _rtDirty = true; }
            var t = _cam.transform;
            bool moved = (t.position - _rtPos).sqrMagnitude > 0.01f || Quaternion.Angle(t.rotation, _rtRot) > 0.01f || Mathf.Abs(_curWidth - _rtWidth) > 0.01f;
            if (!moved && !_rtDirty && Time.unscaledTime < _rtNext) return;
            _rtPos = t.position; _rtRot = t.rotation; _rtWidth = _curWidth; _rtDirty = false;
            _rtNext = Time.unscaledTime + Mathf.Max(0.1f, RefreshSeconds);
            if (mapTerrain) DrawMapTerrain();
            DrawRoads(layer);
            _cam.Render();
        }

        void OnGUI()
        {
            if (!_open || !RenderOnDemand || _rt == null) return;
            GUI.depth = 50;   // hinter der Kartenoberfläche (map.vmf, GuiDepth -8)
            if (Event.current.type == EventType.Repaint)
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _rt, ScaleMode.StretchToFill, false);
        }

        void OnDestroy()
        {
            foreach (var e in _roadMeshes.Values) if (e.mesh != null) Destroy(e.mesh);
            _roadMeshes.Clear();
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
            if (_fpsSaved) { Application.targetFrameRate = _savedFps; _fpsSaved = false; }
        }

        void BeginClosing()
        {
            if (_closing) return;
            _closing = true;
            HideTooltip();
            _closeAt = Time.unscaledTime + 0.45f;
        }

        public void Close()
        {
            if (!_open || _closing) return;
            _closing = true;
            if (_ui != null) { HideTooltip(); _ui.GotoLabel("/", "close", true); }
            _closeAt = Time.unscaledTime + 0.45f;
        }

        void Finish()
        {
            _open = false; _closing = false; IsOpen = false;
            if (_ui != null) _ui.Visible = false;   // bleibt am Ende der Schließanimation; nächstes Öffnen springt nach "map_open"
            if (_cam != null) _cam.enabled = false;
            if (_clearCam != null) _clearCam.enabled = false;
            if (_mainCam != null) _mainCam.enabled = true;
            if (_fpsSaved) { Application.targetFrameRate = _savedFps; _fpsSaved = false; }
            MapPerformance(false);
            Time.timeScale = _savedTimeScale;
            AudioListener.pause = false;
            RenderSettings.fog = _savedFog;
            if (Manager?.Minimap != null) Manager.Minimap.ShowMinimap = _savedMinimap;
            var hud = Manager != null ? Manager.GetComponent<TDU2VehicleHud>() : null;
            if (hud != null) hud.ShowHud = _savedHud;
            Cursor.lockState = _savedLock; Cursor.visible = _savedCursor;
            _contestCup = -1; _contestEnterCup = -1;
            if (_ui != null) _ui.SetValue("/:IsContest", false);
            var ps = _pendingStart; _pendingStart = (-1, -1);
            if (ps.cup >= 0 && Champs != null) Champs.StartFromMap(ps.cup, ps.race);
            // Teleport zu Händler/Haus/Club …: wie im Spiel gleich hinein (nach dem Schließen, Spiel läuft wieder)
            var pe = _pendingEnter; _pendingEnter = null;
            if (pe != null) StartCoroutine(EnterWhenReady(pe));
        }

        /// <summary>
        /// Teleport in ein Interieur: erst an die Einfahrt, warten bis die Welt dort steht (sonst lägen grobe LOD-Gelände-/Gebäude-
        /// teile und ihre Kollider mitten im Raum), dann hinein – alles hinter dem Ladebildschirm.
        /// </summary>
        System.Collections.IEnumerator EnterWhenReady(Spot s)
        {
            var it = Interiors; var st = Manager?.Streamer;
            TeleportOutside(s);
            if (it == null || st == null || it.Inside) yield break;
            bool done = false;
            TDU2LoadingScreen.Show("Teleport " + s.Code, () => done && (!it.Inside || it.SceneReady), 0.8f, false, Manager.BnkDir);
            float until = Time.unscaledTime + 20f;
            yield return null;
            while (Time.unscaledTime < until && !st.ReadyAround(st.MapRoot.TransformPoint(s.Position), 400f)) yield return null;
            var car = Manager.Current != null ? Manager.Current.Car : null;
            if (!it.Inside && it.EnterSpot(s, car)) Debug.Log("[TDU2] Karte: ins Interieur " + s.Code);
            done = true;
        }

        Spot _pendingEnter;
        TDU2Interiors _interiors;
        TDU2Interiors Interiors
        {
            get
            {
                if (_interiors == null && Manager != null) _interiors = Manager.GetComponentInChildren<TDU2Interiors>();
#if UNITY_2023_1_OR_NEWER
                if (_interiors == null) _interiors = FindFirstObjectByType<TDU2Interiors>();
#else
                if (_interiors == null) _interiors = FindObjectOfType<TDU2Interiors>();
#endif
                return _interiors;
            }
        }

        void OnDisable() { if (_open) { _closing = true; Finish(); } }

        void OnCommand(string cmd, string args)
        {
            string c = (cmd ?? "").Trim().ToLowerInvariant();
            if (TDU2FrontendSounds.Command(c)) return;   // sndp:* = Menü-Sound
            if (LogCommands) Debug.Log("[TDU2] Karten-Film -> " + c);
            if (c.StartsWith("bmap:fltr:icf"))
            {
                // Reiter der Filterleiste gewechselt (presets.sendCmdIc): nur noch Spots dieser Gruppe zeigen
                if (int.TryParse(c.Substring(13), out int f)) { _filter = Mathf.Clamp(f, 0, 5); _hover = -1; if (_selected >= 0) { _selected = -1; HideTooltip(); } }
                return;
            }
            switch (c)
            {
                case "zoin": SetZoom(_zoom - 1); break;   // setZoom("in")  -> Spiel verkleinert WhatZoom
                case "zoou": SetZoom(_zoom + 1); break;   // setZoom("out")
                case "gofr":                              // B im Navigator: zurück ins freie Fahren (Schließanimation läuft)
                    BeginClosing(); break;
                case "bmap:ttip:extd":                    // A auf einem Spot: Tooltip mit Infos/Menü ausklappen
                    _ui.CallClip("/tool_tip", "Extend", 1);
                    _ui.SetValue("/:Input", true);
                    _extended = true;
                    _fillAt = Time.unscaledTime + 0.15f;      // Liste nach dem Umschalten der Seiten füllen
                    break;
                case "bmap:ttip:redu":                    // Tooltip wieder eingeklappt
                    _extended = false;
                    break;
                case "bmap:ttip:mecl":                    // Menü des Tooltips zu
                    break;
                case "gotp":                              // Teleport an die Cursorposition (bzw. die des Mini-Menüs)
                    if (_menuWorld.HasValue) _cursorWorld = _menuWorld;
                    TeleportToCursor(); BeginClosing(); break;
                case "coin":                              // Wettbewerb der Meisterschaft betreten (A auf dem Meisterschafts-Spot)
                    {
                        int cup = _contestCup >= 0 ? _contestCup : (_selected >= 0 && _selected < _spots.Count ? _spots[_selected].cup : -1);
                        if (cup < 0 && _hover >= 0 && _hover < _spots.Count) cup = _spots[_hover].cup;
                        if (cup >= 0 && cup != _contestCup) EnterContest(cup);
                        break;
                    }
                case "coou":                              // Wettbewerb verlassen: vom Treffpunkt -> Karte zu, sonst normale Karte
                    if (_contestDirect) { _contestCup = -1; BeginClosing(); }
                    else LeaveContest();
                    break;
                case "giup":                              // Meisterschaft aufgeben
                    if (_contestCup >= 0 && Champs != null) { Champs.GiveUp(_contestCup); SetContestVars(_contestCup); BuildSpots(); }
                    break;
                case "mcen":                              // Menü des Tooltips bestätigt ("Teilnehmen" eines Meisterschaftsrennens)
                    if (_contestCup >= 0 && _selected >= 0 && _selected < _spots.Count && _spots[_selected].race >= 0)
                    {
                        _pendingStart = (_spots[_selected].cup, _spots[_selected].race);
                        TDU2DebugLog.Event("Karte: Meisterschaftsrennen " + (_spots[_selected].race + 1) + " gewählt");
                        HideTooltip(); _extended = false;
                        Close();
                    }
                    break;
                case "goin":                              // zum Spot fahren/teleportieren (bzw. zum Mitspieler)
                    if (_contestCup >= 0 && _selected >= 0 && _selected < _spots.Count && _spots[_selected].race >= 0)
                    {
                        // Rennen der Meisterschaft: nach dem Schließen an den Start und los
                        _pendingStart = (_spots[_selected].cup, _spots[_selected].race);
                        BeginClosing(); break;
                    }
                    if (_selectedPlayer >= 0) TeleportToPlayer(_selectedPlayer);
                    else TeleportToSpot(_selected >= 0 ? _spots[_selected].spot : null);
                    BeginClosing(); break;
                case "tpok":                              // Teleport möglich? (nur auf Straßen, wie im Spiel)
                    _ui.SetValue("/:IsTeleportOK", OnRoad(out _));
                    break;
                case "bmap:ttip:meop":                    // Mini-Menü offen (Eingabe gehört dem Film)
                    _miniMenu = true; break;
                case "inon":                              // Menü zu, Eingabe wieder an die Karte
                    _miniMenu = false; break;
                case "targ":                              // GPS-Ziel setzen bzw. entfernen
                    ToggleGpsTarget(MenuPoint(), _menuTargetPresent, false);   // Sound spielt der Film (sndp:valid/dropflag)
                    break;
                case "bmrk:enbl":                         // Lesezeichen an der Cursorposition
                    {
                        var g = MenuPoint();
                        if (g.HasValue && TDU2Bookmarks.CanAdd)
                        {
                            // Höhe vom Gelände (Cursor liegt auf Meereshöhe), damit das Symbol beim Kippen der Karte an der Stelle bleibt
                            var root = Manager.Streamer.MapRoot; var w = root.TransformPoint(g.Value);
                            if (Physics.Raycast(new Vector3(w.x, w.y + 3000f, w.z), Vector3.down, out var hit, 6000f, ~0, QueryTriggerInteraction.Ignore)) g = root.InverseTransformPoint(hit.point);
                            TDU2Bookmarks.Add(IslandName(), g.Value); BuildSpots();
                            _ui.CallClip("/presets/ttip", "Open");   // Meldung "Lesezeichen hinzugefügt" (schließt sich selbst)
                        }
                        _ui.SetValue("/:CanAddMySpot", TDU2Bookmarks.CanAdd);
                        break;
                    }
                case "bmrk:dsbl":                         // Lesezeichen entfernen
                    {
                        var ms = _selected >= 0 && _selected < _spots.Count ? _spots[_selected] : (_hover >= 0 && _hover < _spots.Count ? _spots[_hover] : null);
                        if (ms != null && ms.bookmark >= 0) { TDU2Bookmarks.Remove(ms.bookmark); _selected = -1; HideTooltip(); BuildSpots(); }
                        _ui.SetValue("/:CanAddMySpot", TDU2Bookmarks.CanAdd);
                        break;
                    }
                case "fiou":
                case "fiin":
                case "fity":
                case "stof":
                case "ston":
                case "mnok":   // Filterleiste: Animationsmeldungen
                    break;
                case "mysp:enbl":                         // Spot als Favorit
                case "mysp:dsbl":
                    {
                        var ms = _selected >= 0 && _selected < _spots.Count ? _spots[_selected] : null;
                        if (ms != null && ms.spot != null) TDU2Bookmarks.SetFavorite(ms.spot.Code, c == "mysp:enbl");
                        _ui.SetValue("/:IsSpotFavorite", ms != null && TDU2Bookmarks.IsFavorite(ms.spot?.Code));
                        break;
                    }
            }
        }

        void SetZoom(int z)
        {
            _zoom = Mathf.Clamp(z, 1, 4);
            if (_ui != null) _ui.SetValue("/:WhatZoom", _zoom);
            ApplyLod(false);
        }

        // ------------------------------------------------------------------ Eingabe
        void HandleInput()
        {
            if (Time.frameCount - _openFrame < 3) return;
#if ENABLE_LEGACY_INPUT_MANAGER
            float w = _curWidth;
            var north = North(); var east = East();
            Vector3 move = Vector3.zero;
            if (_extended || _miniMenu)
            {
                // ausgeklappter Tooltip / Mini-Menü: Pfeile gehören dem Film (Blättern der Liste, Menüauswahl) statt der Kamera
                if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) _ui.Input("Move", "left");
                if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) _ui.Input("Move", "right");
                if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) _ui.Input("Move", "up");
                if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) _ui.Input("Move", "down");
            }
            else
            {
                if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) move += north;
                if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) move -= north;
                if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) move += east;
                if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) move -= east;
            }
            float speed = PanSpeed * w * (Input.GetKey(KeyCode.LeftShift) ? 2.5f : 1f);
            _target += move * speed * Time.unscaledDeltaTime;

            // Ziehen mit der linken Maustaste (Weltpunkt unter der Maus bleibt unter der Maus)
            var mp = Input.mousePosition;
            if (Input.GetMouseButtonDown(0) && !((_extended || _miniMenu) && _ui.MouseOverButton)) { _dragging = true; _lastMouse = mp; }
            if (Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2)) { _dragging = true; _lastMouse = mp; }   // auch rechte/mittlere Taste
            if (!Input.GetMouseButton(0) && !Input.GetMouseButton(1) && !Input.GetMouseButton(2)) _dragging = false;
            if (_dragging && _cam != null)
            {
                var a = GroundPoint(_lastMouse); var b = GroundPoint(mp);
                if (a.HasValue && b.HasValue) _target += a.Value - b.Value;
                _lastMouse = mp;
            }

            // Zoom über den Film (Animationen von Cursor/Spots wie im Spiel), Mausrad/Bild/±
            float wheel = Input.mouseScrollDelta.y;
            bool zin = wheel > 0.1f || Input.GetKeyDown(KeyCode.PageUp) || Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.Plus) || Input.GetKeyDown(KeyCode.E);
            bool zout = wheel < -0.1f || Input.GetKeyDown(KeyCode.PageDown) || Input.GetKeyDown(KeyCode.KeypadMinus) || Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.Q);
            // an den Grenzen nicht an den Film geben (weiter rauszoomen startet dort den Abspann)
            if (zin && _zoom > 1) { if (!_ui.CallClip("/navigator", "setZoom", "in")) SetZoom(_zoom - 1); }
            if (zout && _zoom < 4) { if (!_ui.CallClip("/navigator", "setZoom", "out")) SetZoom(_zoom + 1); }

            if (Input.GetKeyDown(KeyCode.Space) && Manager?.Current != null) { _target = Manager.Current.transform.position; _target.y = 0f; }

            // A wie im Spiel: auf einem Spot -> Tooltip mit Infos; auf freier Fläche -> Mini-Menü (Teleport). Klick ohne Ziehen.
            // Klick ohne Ziehen; über Knöpfen des ausgeklappten Tooltips gehört der Klick dem Film
            // Filterleiste oben links (und die Info-Leisten) gehören dem Film: dort kein Klick auf die Karte (öffnete sonst das Mini-Menü)
            bool click = Input.GetMouseButtonUp(0) && (mp - _downMouse).sqrMagnitude < 36f && !((_extended || _miniMenu) && _ui.MouseOverButton) && !OverFilmBar(mp);
            if (Input.GetMouseButtonDown(0)) _downMouse = mp;
            if (Input.GetMouseButtonDown(1)) _downMouseR = mp;
            // Rechtsklick ohne Ziehen: GPS-Ziel direkt setzen/entfernen (auf einem Spot: am Spot)
            if (Input.GetMouseButtonUp(1) && (mp - _downMouseR).sqrMagnitude < 36f && !_extended && !_miniMenu)
            {
                var gw = GroundPoint(mp);
                if (_hover >= 0 && _hover < _spots.Count && !_spots[_hover].gps) ToggleGpsTarget(_spots[_hover].spot.Position);
                else if (gw.HasValue) ToggleGpsTarget(Manager.Streamer.MapRoot.InverseTransformPoint(gw.Value));
            }
            // Mini-Menü offen: Klicks gehören dem Film (Knöpfe btn0..3 -> Clic/A); ein Linksklick daneben macht nichts
            // (früher B = zu, das traf auch Klicks zwischen den Einträgen). Schließen: rechte Maustaste, Esc/Rücktaste.
            if (_miniMenu && click) click = false;
            if (_miniMenu && Input.GetMouseButtonUp(1) && (mp - _downMouseR).sqrMagnitude < 36f) _ui.Input("B");
            if (Input.GetMouseButtonUp(0) && !click)
                Debug.Log("[TDU2] Karte: Klick nicht für die Karte (gezogen " + ((mp - _downMouse).magnitude > 6f) + ", Filmleiste " + OverFilmBar(mp) + ", Menü " + (_extended || _miniMenu) + ")");
            if (click || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                Debug.Log("[TDU2] Karte: " + (click ? "Klick" : "Enter") + " – Spot " + (_hover >= 0 && _hover < _spots.Count ? _spots[_hover].label + " " + _spots[_hover].spot.Code : "-") + ", Mitspieler " + _hoverPlayer + ", Tooltip " + _extended + ", Mini-Menü " + _miniMenu);
                // Klick gehört der Karte: der Film soll das Loslassen nicht mehr sehen (sonst traf es das gerade geöffnete
                // Mini-Menü bzw. ein Tasten-Symbol darunter -> B -> Menü sofort wieder zu)
                if (click) _ui.ConsumeClick();
                if (!_miniMenu) _cursorWorld = GroundPoint(mp);
                if (_extended || _miniMenu) _ui.Input("A");    // Menü im Tooltip / Mini-Menü
                else if (_hoverPlayer >= 0) { _selectedPlayer = _hoverPlayer; _selected = -1; _ui.Input("A"); }   // Mitspieler: Infos
                else if (_hover >= 0)
                {
                    // Spot: Tooltip mit Infos (bmap:ttip:extd); Lesezeichen: Mini-Menü "Lesezeichen entfernen" (Film)
                    _selected = _hover; _selectedPlayer = -1;
                    var ms = _spots[_hover];
                    _menuWorld = Manager.Streamer.MapRoot.TransformPoint(ms.spot.Position);
                    _menuTargetPresent = TDU2Gps.Instance != null && TDU2Gps.Instance.IsTargetNear(ms.spot.Position, 40f);
                    _ui.SetValue("/:IsTargetPresent", _menuTargetPresent);
                    _ui.SetValue("/:IsSpotFavorite", TDU2Bookmarks.IsFavorite(ms.spot.Code));
                    _ui.SetValue("/:IsTeleportOK", true);
                    _ui.Input("A");
                }
                else
                {
                    // freie Fläche: Mini-Menü des Films (Teleport nur auf Straßen, GPS-Ziel, Lesezeichen)
                    _selected = -1; _selectedPlayer = -1;
                    _menuWorld = _cursorWorld;
                    _ui.SetVariable("/:WhatSpot", "noSpot");
                    _ui.SetVariable("/:WhatSpotType", "none");
                    _ui.SetValue("/:IsTeleportOK", OnRoad(out _));
                    _menuTargetPresent = GpsTargetUnderMouse(mp);
                    _ui.SetValue("/:IsTargetPresent", _menuTargetPresent);
                    _ui.SetValue("/:IsTargetOK", _contestCup < 0);
                    if (_contestCup >= 0) { if (OnRoad(out _)) { TeleportToCursor(); Close(); } else Sound("wrong"); }
                    else _ui.Input("A");
                }
            }
            // Filterleiste: LB/RB bzw. Bild-Tasten am PC -> Tab / Umschalt+Tab (wie die Pfeile im Film, braucht _root.Input)
            bool tabR = Input.GetKeyDown(KeyCode.JoystickButton5) || (Input.GetKeyDown(KeyCode.Tab) && !Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift));
            bool tabL = Input.GetKeyDown(KeyCode.JoystickButton4) || (Input.GetKeyDown(KeyCode.Tab) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)));
            if ((tabR || tabL) && _contestCup < 0 && !_extended && !_miniMenu) _ui.CallClip("/presets", "Move", tabR ? "right" : "left");
            if (Input.GetKeyDown(KeyCode.X)) _ui.Input("X");
            if (Input.GetKeyDown(KeyCode.Y)) _ui.Input("Y");
            // T = direkt an die Cursorposition teleportieren
            if (Input.GetKeyDown(KeyCode.T) && _hoverPlayer >= 0) { TeleportToPlayer(_hoverPlayer); Close(); return; }
            if (Input.GetKeyDown(KeyCode.T) && _contestCup >= 0 && _hover >= 0 && _spots[_hover].race >= 0) { _pendingStart = (_spots[_hover].cup, _spots[_hover].race); Close(); return; }
            if (Input.GetKeyDown(KeyCode.T)) { _cursorWorld = GroundPoint(mp); if (_hover >= 0) { TeleportToSpot(_spots[_hover].spot); Close(); } else if (OnRoad(out _)) { TeleportToCursor(); Close(); } else Sound("wrong"); }
            ClampTarget();
#endif
        }

        Vector3 North() { var r = Manager.Streamer.MapRoot; var v = r.TransformVector(Vector3.back); v.y = 0f; return v.normalized; }   // Spiel -Z = oben auf der Karte
        Vector3 East() { var r = Manager.Streamer.MapRoot; var v = r.TransformVector(Vector3.right); v.y = 0f; return v.normalized; }   // TransformVector: Spiegelung (MirrorX) beachten

        Vector3? GroundPoint(Vector3 screen)
        {
            var ray = _cam.ScreenPointToRay(screen);
            if (Mathf.Abs(ray.direction.y) < 1e-4f) return null;
            float t = -ray.origin.y / ray.direction.y;
            return t > 0f ? ray.origin + ray.direction * t : (Vector3?)null;
        }

        void ClampTarget()
        {
            var idx = Manager?.Streamer?.Index; var root = Manager?.Streamer?.MapRoot;
            if (idx == null || root == null) return;
            var g = root.InverseTransformPoint(_target);
            g.x = Mathf.Clamp(g.x, 0f, Mathf.Max(1, idx.AreasX) * 6144f);
            g.z = Mathf.Clamp(g.z, 0f, Mathf.Max(1, idx.AreasZ) * 6144f);
            var w = root.TransformPoint(g); _target.x = w.x; _target.z = w.z;
        }

        // ------------------------------------------------------------------ Kamera
        float IslandWidth()
        {
            var idx = Manager?.Streamer?.Index;
            float a = idx != null ? Mathf.Max(idx.AreasX, idx.AreasZ) : 8;
            return a * 6144f;
        }

        float WidthFor(int z)
        {
            int i = Mathf.Clamp(z, 1, 4) - 1;
            float w = ViewWidth != null && i < ViewWidth.Length ? ViewWidth[i] : 0f;
            if (w > 0f) return w;
            return z >= 4 ? IslandWidth() : IslandWidth() * FarFactor;
        }

        float InclFor(int z)
        {
            int i = Mathf.Clamp(z, 1, 4) - 1;
            return Inclination != null && i < Inclination.Length ? Inclination[i] : 0f;
        }

        void UpdateCamera(bool snap)
        {
            if (_cam == null || Manager?.Streamer?.MapRoot == null) return;
            float tw = WidthFor(_zoom), ti = InclFor(_zoom);
            if (snap || _curWidth <= 0f) { _curWidth = tw; _curIncl = ti; }
            else
            {
                float k = 1f - Mathf.Exp(-Smooth * Time.unscaledDeltaTime);
                _curWidth = Mathf.Exp(Mathf.Lerp(Mathf.Log(_curWidth), Mathf.Log(tw), k));
                _curIncl = Mathf.Lerp(_curIncl, ti, k);
            }
            // wie im Spiel: Abstand so, dass die Sichtbreite genau ins Bild passt (Breite*0,5 / tan(hFov/2))
            float vfov = _cam.fieldOfView * Mathf.Deg2Rad;
            float hfov = 2f * Mathf.Atan(Mathf.Tan(vfov * 0.5f) * Mathf.Max(0.1f, _cam.aspect));
            float dist = _curWidth * 0.5f / Mathf.Tan(hfov * 0.5f);
            float incl = _curIncl * Mathf.Deg2Rad;
            var north = North();
            var pos = _target + Vector3.up * (dist * Mathf.Cos(incl)) - north * (dist * Mathf.Sin(incl));
            _cam.transform.position = pos;
            _cam.transform.rotation = Quaternion.LookRotation((_target - pos).normalized, north);
            _cam.nearClipPlane = Mathf.Max(1f, dist * 0.02f);
            _cam.farClipPlane = dist * 3f + 20000f;
        }

        // ------------------------------------------------------------------ Oberfläche
        Vector2? ToGui(Vector3 world)
        {
            var s = _cam.WorldToScreenPoint(world);
            if (s.z <= 0f) return null;
            return new Vector2(s.x, Screen.height - s.y);
        }

        void BuildSpots()
        {
            _spots.Clear();
            var ch = Champs;
            var spotss = Manager?.Streamer?.Spots;
            foreach (var item in spotss.Spots)
            {
                Debug.Log("Spot-Debug: " + item.Code + " : " + item.Key);
            }


            if (_contestCup >= 0 && ch != null)
            {
                // Wettbewerbsmodus: nur die Rennen der Meisterschaft
                foreach (var r in ch.MapRaces(_contestCup))
                    _spots.Add(new MapSpot
                    {
                        spot = new Spot { Code = "#CUPRACE_" + r.Name, Position = r.Start, Position2 = r.Start - r.Forward * 14f, Index = -1 },
                        label = r.Label,
                        cup = _contestCup,
                        race = r.Index,
                    });
            }
            else
            {
                var cupByCode = new Dictionary<string, int>();
                if (ch != null) foreach (var c in ch.MapCups()) cupByCode[c.Code] = c.Index;
                var spots = Manager?.Streamer?.Spots;
                if (spots != null)
                    foreach (var s in spots.Spots)
                    {
                        string label = TDU2Minimap.SpotLabel(s.Code);
                        if (label == null) continue;
                        _spots.Add(new MapSpot { spot = s, label = label, cup = cupByCode.TryGetValue(s.Code, out int ci) ? ci : -1 });
                    }
                // Lesezeichen (nur auf der großen Karte, gespeichert je Spieler und Insel)
                string isl = IslandName();
                foreach (var b in TDU2Bookmarks.All)
                    if (string.Equals(b.Island ?? "", isl, StringComparison.OrdinalIgnoreCase))
                        _spots.Add(new MapSpot { spot = new Spot { Code = "#BOOKMARK_" + b.Id, Position = b.Pos, Position2 = b.Pos, Index = -1 }, label = "bookmark", bookmark = b.Id });
                // GPS-Ziel: eigener Clip des Films (/target, siehe UpdateGpsMarker), kein Spot-Symbol
            }
            int extra = 0; foreach (var e in _spots) if (e.cup >= 0) extra++;
            Debug.Log("[TDU2] Karte: " + _spots.Count + " Spots mit Symbol" + (_contestCup >= 0 ? " (Wettbewerb)" : ", davon " + extra + " Meisterschaften"));
            var tmpl = _ui.Find("/pack/point");
            if (tmpl != null) { _pointChar = tmpl.CharId; tmpl.Visible = false; }
            // alte Kopien entfernen
            foreach (var kv in _slotLabel) _ui.Detach("/pack", _pointDepth0 + kv.Key);
            _slotLabel.Clear();
            foreach (var kv in _playerSlots) _ui.Detach("/pack", _pointDepth0 + MaxSpots + 1 + kv.Value);
            _playerSlots.Clear();
            _hover = -1; _hoverPlayer = -1; _selectedPlayer = -1;
        }

        /// <summary>Spot im gewählten Reiter der Filterleiste? (Wettbewerbsmodus: alle Rennen; GPS-Ziel immer)</summary>
        bool FilterPass(MapSpot ms)
        {
            if (_contestCup >= 0 || ms.gps) return true;
            if (ms.bookmark >= 0) return _filter == 5;
            int cat = SpotCategory(ms);
            switch (_filter)
            {
                case 0: return cat == 0;
                case 2: return cat == 2;
                case 3: return cat == 3;
                case 5: return TDU2Bookmarks.IsFavorite(ms.spot?.Code);   // eigene Orte: Lesezeichen + Favoriten
                default: return false;   // 1 Multiplayer-Herausforderungen (keine Spots), 4 Spieler (nur Spielersymbole)
            }
        }

        /// <summary>0 Herausforderung (Rennen, Meisterschaften, Schule, Foto, gestohlene Autos …), 2 Ort für den Avatar, 3 Ort für Fahrzeuge.</summary>
        static int SpotCategory(MapSpot ms)
        {
            if (ms.cup >= 0) return 0;
            switch (ms.label)
            {
                case "home":
                case "club":
                case "estates":
                case "clothes":
                case "hair":
                case "surgery":
                case "casino":
                case "gamblingHall":
                case "airport":
                case "watches":
                case "forsale":
                    return 2;
                case "carDealer":
                case "bikeDealer":
                case "tuning":
                case "carWash":
                case "carPaint":
                case "carRental":
                case "auctionHall":
                    return 3;
                default: return 0;
            }
        }

        void ApplyLod(bool force)
        {
            bool lod = _zoom >= 3;   // weit weg: kleine Punkte (point/spot "lod")
            if (!force && lod == _lod) return;
            _lod = lod;
            foreach (var kv in _slotLabel)
                _ui.CallClip("/pack/point" + kv.Key.ToString("000"), "LOD", lod ? 1 : 0);
        }

        void UpdateOverlay()
        {
            if (_ui == null || _cam == null || _closing) return;
            var root = Manager.Streamer.MapRoot;

            // Spieler
            var veh = Manager.Current;
            if (veh != null)
            {
                var g = ToGui(veh.transform.position);
                var lp = g.HasValue ? _ui.GuiToLocal("/", g.Value) : null;
                if (lp.HasValue)
                {
                    _ui.SetVisible("/player", true);
                    _ui.SetPosition("/player", lp.Value);
                    // Symbol ist ein Auto in Seitenansicht (player/ic "car"/"bike") – wird im Spiel nicht gedreht
                }
                else _ui.SetVisible("/player", false);
            }

            // Cursor an der Maus
#if ENABLE_LEGACY_INPUT_MANAGER
            var mouse = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
#else
            var mouse = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
#endif
            var cl = _ui.GuiToLocal("/", mouse);
            if (cl.HasValue) _ui.SetPosition("/curseur", cl.Value);
            // Über Tooltip/Menüs liegt der Kartencursor des Films darunter -> dort den Mauszeiger von Windows zeigen (immer obenauf)
            bool sysCursor = _extended || _miniMenu || _ui.MouseOverButton;
            if (sysCursor != _sysCursor)
            {
                _sysCursor = sysCursor;
                Cursor.visible = sysCursor;
                _ui.SetVisible("/curseur", !sysCursor);
            }
            // Sperr-Symbol am Cursor: /curseur/cible "off" = rotes Verbotszeichen (abseits von Straßen und Spots), "on" = leer.
            // Nur bei Änderung umschalten (sonst startet das Bild jedes Mal neu).
            if (Time.frameCount % 4 == 0)
            {
#if ENABLE_LEGACY_INPUT_MANAGER
                _cursorWorld = GroundPoint(Input.mousePosition);
#endif
                if (!_miniMenu)
                {
                    bool offroad = _hover < 0 && _hoverPlayer < 0 && !OnRoad(out _);
                    if (_cibleOff != offroad) { _cibleOff = offroad; _ui.GotoLabel("/curseur/cible", offroad ? "off" : "on", false); }
                }
                _ui.SetValue("/:IsRoadLocked", false);
            }

            UpdateGpsMarker();

            // Spots: sichtbare, nächste zum Kartenmittelpunkt zuerst
            if (_pointChar == 0) return;
            var list = new List<(float d, int idx, Vector2 gui)>();
            var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            for (int i = 0; i < _spots.Count; i++)
            {
                var g = ToGui(root.TransformPoint(_spots[i].spot.Position));
                if (!g.HasValue || g.Value.x < -40 || g.Value.y < -40 || g.Value.x > Screen.width + 40 || g.Value.y > Screen.height + 40) continue;
                if (!FilterPass(_spots[i])) continue;
                // Lesezeichen immer zuerst (sonst fielen sie bei weit herausgezoomter Karte hinter MaxSpots weg)
                list.Add((_spots[i].bookmark >= 0 ? -1f : (g.Value - center).sqrMagnitude, i, g.Value));
            }
            list.Sort((a, b) => a.d.CompareTo(b.d));
            int n = Mathf.Min(list.Count, MaxSpots);
            int hover = -1; float best = 22f * 22f * (Screen.height / 720f) * (Screen.height / 720f);
            for (int slot = 0; slot < MaxSpots; slot++)
            {
                string name = "point" + slot.ToString("000");
                if (slot >= n)
                {
                    if (_slotLabel.ContainsKey(slot)) { _ui.Detach("/pack", _pointDepth0 + slot); _slotLabel.Remove(slot); }
                    continue;
                }
                var (_, idx, gui) = list[slot];
                var d = _ui.AttachSprite("/pack", _pointChar, name, _pointDepth0 + slot);
                if (d == null) continue;
                string label = _spots[idx].label;
                var gpsI = TDU2Gps.Instance;
                bool tgt = gpsI != null && gpsI.HasTarget && (_spots[idx].gps || gpsI.IsTargetNear(_spots[idx].spot.Position, 40f));
                string key = label + "|" + idx + (tgt ? "|T" : "");
                if (!_slotLabel.TryGetValue(slot, out var cur) || cur != key)
                {
                    _ui.GotoLabel("/pack/" + name + "/spot", label, false);
                    _ui.CallClip("/pack/" + name, "LOD", _lod ? 1 : 0);
                    // Status wie im Spiel: ohne das zeigt das Symbol alle Medaillen-Fähnchen; gefahrene Rennen: Haken + Medaille
                    _ui.CallClip("/pack/" + name, "resetIconStatus");
                    if (TDU2Minimap.ExtraSpotNumbers.TryGetValue(_spots[idx].spot.Code, out var cnum)) _ui.SetVariable("/pack/" + name + ":num_contest", cnum);
                    var rs = RaceOf(_spots[idx]);
                    if (rs != null && rs.State == 2)
                    {
                        _ui.CallClip("/pack/" + name, "setDoneStatus", true);
                        _ui.CallClip("/pack/" + name, "setRankStatus", rs.Place >= 1 && rs.Place <= 3 ? rs.Place : 0);
                    }
                    if (label == "carDealer") ApplyDealerLogo("/pack/" + name + "/spot", _spots[idx].spot.Code);
                    else _ui.SetClipTexture("/pack/" + name + "/spot/ic/logo/lure", null);
                    // GPS-Ziel: Fähnchen (spot/target "open") und Pfeil wie im Spiel, sonst aus
                    if (tgt) _ui.CallClip("/pack/" + name, "setTargetStatus", true);
                    _ui.SetVisible("/pack/" + name + "/fleche", tgt);
                    _slotLabel[slot] = key;
                }
                var lp = _ui.GuiToLocal("/pack", gui);
                if (lp.HasValue) d.Position = lp.Value;
                d.Matrix.A = SpotScale; d.Matrix.D = SpotScale; d.Matrix.B = 0f; d.Matrix.C = 0f; d.ScriptMatrix = true;
                d.Visible = true;
                float dm = (gui - mouse).sqrMagnitude;
                if (dm < best && !_spots[idx].gps) { best = dm; hover = idx; }   // GPS-Ziel selbst: Mini-Menü der freien Fläche
            }
            int hoverPlayer = UpdatePlayerBlips(mouse, ref best);
            if (hoverPlayer >= 0) hover = -1;
            if ((hover != _hover || hoverPlayer != _hoverPlayer) && !_extended && !_miniMenu)
            {
                _hover = hover; _hoverPlayer = hoverPlayer;
                // Cursor-Ring wie im Spiel: über einem Spot zusammenziehen (spotOver), sonst zurück (spotOut)
                bool over = hover >= 0 || hoverPlayer >= 0;
                if (over != _cursorOverSpot) { _cursorOverSpot = over; _ui.CallClip("/curseur", over ? "spotOver" : "spotOut"); }
                var gd = Online.TDU2GameData.Instance;
                if (hoverPlayer >= 0 && gd != null && gd.Players.TryGetValue(hoverPlayer, out var pl)) { ShowPlayerTooltip(pl); return; }
                // Spiel: _root.WhatSpot = Spot unter dem Cursor ("noSpot" sonst), WhatSpotType "spot" bzw. "contest" (Meisterschaft/
                // deren Rennen – A darauf öffnet im Film den Wettbewerb bzw. das Auswahlmenü)
                bool contest = hover >= 0 && (_spots[hover].cup >= 0);
                _ui.SetVariable("/:WhatSpot", hover >= 0 ? _spots[hover].label : "noSpot");
                _ui.SetVariable("/:WhatSpotType", hover >= 0 ? (contest ? "contest" : "spot") : "none");
                if (hover >= 0) ShowTooltip(_spots[hover]); else HideTooltip();
            }
        }

        /// <summary>Blips der Mitspieler (Label "player" im Spot-Symbol); liefert den Spieler unter dem Mauszeiger.</summary>
        int UpdatePlayerBlips(Vector2 mouse, ref float best)
        {
            var gd = Online.TDU2GameData.Instance;
            var root = Manager.Streamer.MapRoot;
            int hover = -1;
            var seen = new HashSet<int>();
            if (gd != null && _pointChar != 0)
                foreach (var p in gd.Players.Values)
                {
                    if (!p.Online || !p.HasPos) continue;
                    var g = ToGui(root.TransformPoint(p.GamePos));
                    if (!g.HasValue) continue;
                    if (!_playerSlots.TryGetValue(p.Id, out int slot))
                    {
                        slot = 0; while (_playerSlots.ContainsValue(slot)) slot++;
                        if (slot >= 64) continue;
                        _playerSlots[p.Id] = slot;
                    }
                    seen.Add(p.Id);
                    string name = "plyr" + slot.ToString("00");
                    bool fresh = _ui.Find("/pack/" + name) == null;
                    var d = _ui.AttachSprite("/pack", _pointChar, name, _pointDepth0 + MaxSpots + 1 + slot);
                    if (d == null) continue;
                    if (fresh)
                    {
                        _ui.GotoLabel("/pack/" + name + "/spot", "player", false);
                        _ui.CallClip("/pack/" + name, "LOD", _lod ? 1 : 0);
                        _ui.SetVisible("/pack/" + name + "/fleche", false);
                    }
                    var lp = _ui.GuiToLocal("/pack", g.Value);
                    if (lp.HasValue) d.Position = lp.Value;
                    d.Matrix.A = SpotScale; d.Matrix.D = SpotScale; d.Matrix.B = 0f; d.Matrix.C = 0f; d.ScriptMatrix = true;
                    d.Visible = true;
                    float dm = (g.Value - mouse).sqrMagnitude;
                    if (dm < best) { best = dm; hover = p.Id; }
                }
            var gone = new List<int>();
            foreach (var kv in _playerSlots) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var id in gone) { _ui.Detach("/pack", _pointDepth0 + MaxSpots + 1 + _playerSlots[id]); _playerSlots.Remove(id); }
            return hover;
        }

        void ShowPlayerTooltip(Online.PlayerInfo p)
        {
            _ui.SetVariable("/:WhatSpot", "player");
            _ui.SetVariable("/:WhatSpotType", "spot");
            _ui.SetVariable("/:SpotName", p.Nick);
            _ui.GotoLabel("/tool_tip/ic", "player", false);
            if (!_ttipOpen) { _ui.CallClip("/tool_tip", "Open"); _ttipOpen = true; }
        }

        /// <summary>Ausgeklappter Tooltip eines Spielers (infos.setPlayerInfos wartet auf _root.MapPlayerInfosLoaded).</summary>
        void FillPlayerInfos()
        {
            var gd = Online.TDU2GameData.Instance;
            if (gd == null || !gd.Players.TryGetValue(_selectedPlayer, out var p)) return;
            var c = p.Car;
            _ui.SetVariable("/:m_MapPlayerVehicle", p.CarName);
            _ui.SetVariable("/:MapPlayerVehicleGroup", GroupLabel(c?.Raw != null ? c.Raw.I("_vehiclecategory") : 0));
            _ui.SetValue("/:MapPlayerVehicleUpg", 0);
            _ui.SetValue("/:MapPlayerNationality", p.Nationality > 0 ? p.Nationality : 1);
            foreach (var n in new[] { "MapPlayerCurGlobalLevel", "MapPlayerGlobalLevel", "MapPlayerRacingCurLevel", "MapPlayerRacingLevel", "MapPlayerSocialCurlevel",
                                      "MapPlayerSocialLevel", "MapPlayerCruisingCurLevel", "MapPlayerCruisingLevel", "MapPlayerCollectionLevel", "MapPlayerCollectionCurLevel" })
                _ui.SetValue("/:" + n, 0);
            _ui.SetValue("/:MapPlayerNbCars", p.NbCars);
            _ui.SetValue("/:MapPlayerNbHouses", 0);
            _ui.SetVariable("/:MapPlayerMoney", p.Money.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " $");
            _ui.SetValue("/:MapPlayerInfosLoaded", true);
        }

        void TeleportToPlayer(int id)
        {
            // Beitreten wie über die Spielerliste (verlässt auch ein Interieur; Fehler im Chat)
            var gd = Online.TDU2GameData.Instance;
            var party = Online.TDU2Party.Instance ?? (gd != null ? Online.TDU2Party.Ensure(gd.gameObject) : null);
            if (party != null) { party.Join(id); return; }
            if (gd == null || !gd.Players.TryGetValue(id, out var p) || !p.HasPos) return;
            var root = Manager.Streamer.MapRoot;
            var fwd = root.TransformVector(p.GameForward); fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            TeleportTo(root.TransformPoint(p.GamePos) - fwd * 12f, fwd);   // hinter den Spieler
        }

        void ApplyDealerLogo(string spotPath, string code)
        {
            var mm = Manager?.Minimap;
            var tex = mm != null ? mm.DealerLogoTexture(code) : null;
            if (tex != null)
            {
                _ui.GotoLabel(spotPath + "/ic", "brands", false);
                _ui.SetClipTexture(spotPath + "/ic/logo/lure", tex);
            }
        }

        /// <summary>Marken im Bestand eines Händlers (häufigste zuerst), z.B. "Ferrari" oder "Audi / Volkswagen".</summary>
        string DealerBrands(string code)
        {
            var db = Manager?.Database; var shop = db?.ShopFor(code);
            if (shop == null) return null;
            var count = new Dictionary<string, int>();
            foreach (int id in shop.CarIds)
                if (id != 0 && db.ById.TryGetValue(id, out var c) && !string.IsNullOrEmpty(c.Brand)) { count.TryGetValue(c.Brand, out int n); count[c.Brand] = n + 1; }
            var list = new List<KeyValuePair<string, int>>(count);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var names = new List<string>();
            for (int i = 0; i < list.Count && i < 3; i++) names.Add(list[i].Key);
            return string.Join(" / ", names);
        }

        static string GroupLabel(int cat)
        {
            if (cat >= 1 && cat <= 7) return "A" + cat;
            if (cat >= 9 && cat <= 13) return "B" + (cat - 8);
            if (cat >= 14 && cat <= 17) return "C" + (cat - 13);
            if (cat >= 18 && cat <= 19) return "mA" + (cat - 17);
            if (cat >= 20 && cat <= 22) return "mB" + (cat - 19);
            if (cat >= 23 && cat <= 24) return "mC" + (cat - 22);
            return "empty";
        }

        // ------------------------------------------------------------------ Teleport (gotp / goin)
        /// <summary>Straße unter dem Cursor (bis ~2,5 % der Sichtbreite, mind. 25 m) – Spielkoordinaten mit Höhe.</summary>
        bool OnRoad(out Vector3 world)
        {
            world = default;
            var mm = Manager?.Minimap; var root = Manager?.Streamer?.MapRoot;
            if (mm == null || root == null || !_cursorWorld.HasValue) return false;
            var g = root.InverseTransformPoint(_cursorWorld.Value);
            if (!mm.NearestRoad(g, Mathf.Max(25f, _curWidth * 0.025f), out var rp)) return false;
            world = root.TransformPoint(rp);
            return true;
        }

        void TeleportToCursor()
        {
            if (!OnRoad(out var w)) return;
            // Fahrtrichtung entlang der Straße: Nachbarpunkt suchen
            Vector3? dir = null;
            var mm = Manager.Minimap; var root = Manager.Streamer.MapRoot;
            var g = root.InverseTransformPoint(w);
            if (mm.NearestRoad(g + new Vector3(12f, 0f, 0f), 10f, out var n1) && (n1 - g).sqrMagnitude > 1f) dir = root.TransformVector(n1 - g);
            TeleportTo(w, dir);
        }

        void Sound(string s) => TDU2FrontendSounds.Play(s);   // Menü-Sounds (fe_snd.bnk)

        void TeleportToSpot(Spot s)
        {
            if (s == null) { TeleportToCursor(); return; }
            // Spot mit Interieur (Händler, Haus, Club, Laden …): nach dem Schließen der Karte direkt hinein
            if (s.Index >= 0 && !string.IsNullOrEmpty(s.Code) && !s.Code.StartsWith("#") && Interiors != null && !Interiors.Inside)
            { _pendingEnter = s; return; }
            TeleportOutside(s);
        }

        void TeleportOutside(Spot s)
        {
            var root = Manager.Streamer.MapRoot;
            // Einfahrt (Position2), sonst Spot-Position; Blick zum Spot
            bool p2 = s.Position2.sqrMagnitude > 1f && !float.IsNaN(s.Position2.x);
            var at = root.TransformPoint(p2 ? s.Position2 : s.Position);
            var look = root.TransformPoint(s.Position) - at; look.y = 0f;
            TeleportTo(at, look.sqrMagnitude > 1f ? look : (Vector3?)null);
        }

        void TeleportTo(Vector3 world, Vector3? dir)
        {
            var veh = Manager?.Current;
            if (veh == null) return;
            var f = dir ?? Vector3.ProjectOnPlane(veh.transform.forward, Vector3.up);
            f.y = 0f;
            // Zielhöhe aus den Spieldaten (Straßenpunkt/Spot/Mitspieler): das Auto wartet dort, bis der Boden geladen ist
            Manager.TeleportSafe(world, Quaternion.LookRotation(f.sqrMagnitude > 1e-3f ? f.normalized : Vector3.forward, Vector3.up));
            _target = world; _target.y = 0f;
        }

        // ------------------------------------------------------------------ Bilder (FrontEnd/AllRes/MapScreens/Spots, RealtorScr)
        // Name wie im Spiel: <code>_<AreaX><AreaZ><SectorX><SectorZ>[i] (i = Ibiza), z.B. ecd_ital_3210.bnk / ecd_ital_1207i.bnk;
        // Häuser (EPH) in RealtorScr.
        readonly Dictionary<string, Texture2D> _pics = new Dictionary<string, Texture2D>();
        string[] _spotPicFiles;

        /// <summary>
        /// Bild der Spot-Bank: MapScreens/Spots/&lt;code&gt;_&lt;ziffern&gt;[i].bnk. Die Ziffern (Area/Sektor) und das "i" passen nicht
        /// immer zu unserer Berechnung -> exakter Name, sonst mit/ohne "i", sonst die Datei des Codes mit den ähnlichsten Ziffern.
        /// Zusätzliche Spots (Meisterschaften) über TDU2Minimap.ExtraSpotPictures (z.B. "contest/cl-4-1").
        /// </summary>
        string SpotPictureFile(Spot s, string allres, string name)
        {
            if (s.Code.StartsWith("#CUPRACE_") && _contestCup >= 0 && Champs != null)
            {
                var mc = Champs.MapCupAt(_contestCup);
                if (mc != null && !string.IsNullOrEmpty(mc.Picture))
                    return CarDatabase.FindFile(Path.Combine(allres, "MapScreens", Path.GetDirectoryName(mc.Picture) ?? ""), Path.GetFileName(mc.Picture) + ".bnk");
            }
            if (TDU2Minimap.ExtraSpotPictures.TryGetValue(s.Code, out var extra))
            {
                string sub = Path.GetDirectoryName(extra) ?? "";
                return CarDatabase.FindFile(Path.Combine(allres, "MapScreens", sub), Path.GetFileName(extra) + ".bnk");
            }
            string dir = Path.Combine(allres, "MapScreens", "Spots");
            string f = CarDatabase.FindFile(dir, name + ".bnk");
            if (f != null) return f;
            string other = name.EndsWith("i") ? name.Substring(0, name.Length - 1) : name + "i";
            f = CarDatabase.FindFile(dir, other + ".bnk");
            if (f != null) return f;
            try
            {
                if (_spotPicFiles == null) _spotPicFiles = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.bnk") : new string[0];
                string prefix = s.Code.ToLowerInvariant() + "_";
                string want = s.AreaX.ToString() + s.AreaZ + s.SectorX + s.SectorZ;
                string best = null; int bestScore = -1;
                foreach (var file in _spotPicFiles)
                {
                    string fn = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    if (!fn.StartsWith(prefix)) continue;
                    string digits = fn.Substring(prefix.Length).TrimEnd('i');
                    int score = digits == want ? 100 : 0;
                    for (int k = 0; k < Math.Min(digits.Length, want.Length); k++) if (digits[k] == want[k]) score += 10 - k;
                    if (score > bestScore) { bestScore = score; best = file; }
                }
                return best;
            }
            catch (Exception) { return null; }
        }

        Texture2D SpotPicture(Spot s)
        {
            if (s == null || string.IsNullOrEmpty(_bnkDir)) return null;
            bool ibiza = (Manager?.Streamer?.Index?.IslandName ?? "").StartsWith("i", StringComparison.OrdinalIgnoreCase);
            string name = s.Code.ToLowerInvariant() + "_" + s.AreaX + s.AreaZ + s.SectorX + s.SectorZ + (ibiza ? "i" : "");
            if (_pics.TryGetValue(s.Code + "|" + name, out var t)) return t;
            t = null;
            try
            {
                string allres = Path.Combine(_bnkDir, "frontend", "AllRes");
                bool house = s.Code.StartsWith("EPH", StringComparison.OrdinalIgnoreCase);
                string f = house ? CarDatabase.FindFile(Path.Combine(allres, "RealtorScr"), name + ".bnk") : null;
                if (f == null) f = SpotPictureFile(s, allres, name);
                if (f != null)
                {
                    var bank = BnkArchive.Open(f);
                    foreach (var e in bank.All(".2db"))
                    {
                        t = TextureDef.Parse(bank.Read(e), Path.GetFileNameWithoutExtension(e.Name)).CreateTexture(TextureWrapMode.Clamp, TextureWrapMode.Clamp, false, true);
                        break;
                    }
                }
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Karte: Bild " + name + ": " + ex.Message); }
            _pics[s.Code + "|" + name] = t;
            if (t == null) Debug.Log("[TDU2] Karte: kein Bild für Spot " + s.Code + " (" + name + ")");
            return t;
        }

        // ------------------------------------------------------------------ Ausgeklappter Tooltip: Fahrzeugliste des Händlers
        void FillExtendedInfos()
        {
            if (_selectedPlayer >= 0) { FillPlayerInfos(); return; }
            if (_selected < 0 || _selected >= _spots.Count) return;
            var ms = _spots[_selected];
            var s = ms.spot; var label = ms.label;
            _ui.SetClipTexture("/tool_tip/infos/picture/lure_map", SpotPicture(s));
            if (ms.cup >= 0) { SetContestVars(ms.cup); return; }
            if (label != "carDealer" && label != "bikeDealer" && label != "home") return;
            var db = Manager?.Database; var shop = label == "home" ? null : db?.ShopFor(s.Code);
            var cars = label == "home" ? HouseCars(s.Code) : new List<CarPhysics>();
            if (shop != null) foreach (int id in shop.CarIds) if (id != 0 && db.ById.TryGetValue(id, out var c)) cars.Add(c);
            const string txt = "/tool_tip/infos/txt";
            _ui.SetVariable(txt + ":list_title", (_texts != null && _texts.TryGetValue("m_Vehicles_", out var v) ? v : "Fahrzeuge"));
            _ui.SetVariable("/:nb_cars", cars.Count.ToString());
            // Blättern macht der Film selbst (txt.Move: nb_pages, num_page 0.., last_page, num_page_folio 1..) – 4 Zeilen je Seite
            int pages = Mathf.Clamp((Mathf.Min(cars.Count, 12) + 3) / 4, 1, 3);
            _ui.SetVariable(txt + ":nb_pages", pages.ToString());
            _ui.SetVariable(txt + ":last_page", (pages - 1).ToString());
            _ui.SetVariable(txt + ":num_page", "0");
            _ui.SetVariable(txt + ":prev_page", "0");
            _ui.SetVariable(txt + ":num_page_folio", "1");
            _ui.SetVariable(txt + "/folio:nb_pages", pages.ToString());
            _ui.SetVariable(txt + "/folio:num_page_folio", "1");
            for (int i = 0; i < 12; i++)
            {
                string li = txt + "/pages/li" + i;
                if (i >= cars.Count) { _ui.GotoLabel(li, "default", false); continue; }
                var c = cars[i];
                _ui.GotoLabel(li, "cars", false);
                _ui.GotoLabel(li + "/disp", "default", false);
                _ui.SetVariable(li + "/disp:txt", ((c.Brand ?? "") + " " + c.ModelName).Trim());
                _ui.GotoLabel(li + "/disp/gp", GroupLabel(c.Raw != null ? c.Raw.I("_vehiclecategory") : 0), false);
                _ui.GotoLabel(li + "/disp/chck", "off", false);
                _ui.GotoLabel(li + "/disp/car", "set", false);
            }
        }

        /// <summary>Name des Spot-Typs aus der Spieldatenbank (DBLoc, wie die Kartenfilter: m_CarDealer, m_RacingSchool …).</summary>
        static readonly Dictionary<string, string> SpotLocKeys = new Dictionary<string, string>
        {
            { "carDealer", "m_CarDealer" }, { "bikeDealer", "m_BikeDealer" }, { "carPaint", "m_CarPaint" }, { "carRental", "m_CarRental" },
            { "carWash", "m_CarWash_" }, { "estates", "m_Estates" }, { "home", "m_Home" }, { "hair", "m_HairDresser" },
            { "clothes", "m_Clothes_" }, { "rschool", "m_RacingSchool" }, { "tuning", "m_AfterMarket" }, { "casino", "m_Casino_title" },
            { "gamblingHall", "m_gamblin_hall_short" }, { "cup", "m_Cup_" },
        };

        string LocalizedSpotName(string label, string code)
        {
            if (label != null && _texts != null && SpotLocKeys.TryGetValue(label, out var key) && _texts.TryGetValue(key, out var txt) && !string.IsNullOrEmpty(txt))
                return txt.Trim();
            return SpotsFile.CategoryFromCode(code).Replace("?", "").Trim();
        }

        // ------------------------------------------------------------------ GPS-Ziel / Lesezeichen
        bool _gpsMarkerShown; Vector3 _gpsMarkerFor;

        /// <summary>
        /// GPS-Ziel auf der Karte: Clip /target des Films (map.vmf, Tiefe 10, Labels default/open/loop – blendet sich bei
        /// "open" ein und läuft dann in "loop"); im Wettbewerbs-Vorschau blendet der Film ihn selbst aus.
        /// </summary>
        void UpdateGpsMarker()
        {
            var gps = TDU2Gps.Instance;
            var root = Manager?.Streamer?.MapRoot;
            bool show = gps != null && gps.HasTarget && root != null && _contestCup < 0;
            Vector2? gui = show ? ToGui(root.TransformPoint(gps.TargetGame)) : null;
            var lp = gui.HasValue ? _ui.GuiToLocal("/", gui.Value) : null;
            show &= lp.HasValue;
            if (show)
            {
                if (!_gpsMarkerShown || _gpsMarkerFor != gps.TargetGame) { _ui.GotoLabel("/target", "open", true); _gpsMarkerFor = gps.TargetGame; }
                _ui.SetPosition("/target", lp.Value);
            }
            if (show != _gpsMarkerShown || !show) _ui.SetVisible("/target", show);
            _gpsMarkerShown = show;
        }
        string IslandName() => Manager?.Streamer?.Index?.IslandName ?? "";

        /// <summary>Punkt des Mini-Menüs bzw. gewählten Spots in Spielkoordinaten.</summary>
        Vector3? MenuPoint()
        {
            var root = Manager?.Streamer?.MapRoot;
            if (root == null) return null;
            if (_selected >= 0 && _selected < _spots.Count) return _spots[_selected].spot.Position;
            var w = _menuWorld ?? _cursorWorld;
            return w.HasValue ? root.InverseTransformPoint(w.Value) : (Vector3?)null;
        }

        /// <summary>GPS-Ziel unter dem Mauszeiger (Bildschirmabstand wie die Spot-Auswahl)?</summary>
        bool GpsTargetUnderMouse(Vector3 mouseScreen)
        {
            var gps = TDU2Gps.Instance;
            if (gps == null || !gps.HasTarget || Manager?.Streamer?.MapRoot == null) return false;
            var g = ToGui(Manager.Streamer.MapRoot.TransformPoint(gps.TargetGame));
            if (!g.HasValue) return false;
            var m = new Vector2(mouseScreen.x, Screen.height - mouseScreen.y);
            float r = 22f * (Screen.height / 720f);
            return (g.Value - m).sqrMagnitude < r * r;
        }

        /// <summary>GPS-Ziel an diesem Punkt (Spielkoordinaten) setzen; liegt dort schon das Ziel: entfernen.</summary>
        void ToggleGpsTarget(Vector3? game, bool? present = null, bool sound = true)
        {
            var gps = TDU2Gps.Instance;
            if (gps == null || !game.HasValue) { if (sound) Sound("wrong"); return; }
#if ENABLE_LEGACY_INPUT_MANAGER
            bool under = _selected < 0 && GpsTargetUnderMouse(Input.mousePosition);
#else
            bool under = false;
#endif
            bool remove = present ?? (gps.HasTarget && (gps.IsTargetNear(game.Value, 40f) || under));
            if (remove && gps.HasTarget) gps.ClearTarget();
            else gps.SetTarget(game.Value);
            _ui.SetValue("/:IsTargetPresent", gps.HasTarget);
            if (sound) Sound(gps.HasTarget ? "dropflag" : "valid");
            BuildSpots();
        }

        bool _ttipOpen;
        void ShowTooltip(MapSpot ms)
        {
            var s = ms.spot; var label = ms.label;
            // Titel /:SpotName, Symbol /tool_tip/ic (gleiches Spot-Sprite wie auf der Karte)
            string name = TDU2Minimap.ExtraSpotNames.TryGetValue(s.Code, out var extraName) ? extraName : LocalizedSpotName(label, s.Code);
            if (ms.bookmark >= 0) name = Txt("m_Bookmark", "Lesezeichen") + " " + ms.bookmark;
            _ui.SetValue("/:IsSpotFavorite", TDU2Bookmarks.IsFavorite(s.Code));
            if (label == "carDealer") { var b = DealerBrands(s.Code); if (!string.IsNullOrEmpty(b)) name = b; }
            var ch = Champs;
            if (ms.cup >= 0 && ch != null)
            {
                var mc = ch.MapCupAt(ms.cup);
                if (ms.race >= 0)
                {
                    // Rennen im Wettbewerbsmodus: Name, Runden/Länge, Preisgeld
                    var races = ch.MapRaces(ms.cup);
                    var r = ms.race < races.Count ? races[ms.race] : null;
                    if (r != null)
                    {
                        name = (ms.race + 1) + ". " + r.Title;
                        _ui.SetVariable("/:NbLaps", r.Laps.ToString());
                        _ui.SetVariable("/:Distance", r.LengthM > 0f ? (r.LengthM / 1000f).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("de-DE")) + " km" : "");
                        _ui.SetVariable("/:GMDescription", r.ModeName + (r.State == 2 ? " – " + r.Points + " " + Txt("m_Pts_", "Pkt.") : ""));
                        _ui.SetVariable("/:GoldReward", mc != null && mc.RaceReward > 0 ? Money(mc.RaceReward) : "");
                    }
                }
                else if (mc != null)
                {
                    name = mc.Name;
                    SetContestVars(ms.cup);
                    _ui.SetVariable("/:ContestName", mc.Name);
                    _ui.SetVariable("/:ContestType", mc.ContestType);
                }
            }
            _ui.SetVariable("/:SpotName", name);
            _ui.SetVariable("/:WhatSpot", label);
            _ui.GotoLabel("/tool_tip/ic", label, false);
            if (label == "carDealer") ApplyDealerLogo("/tool_tip/ic", s.Code);
            else _ui.SetClipTexture("/tool_tip/ic/logo/lure", null);
            if (TDU2Minimap.ExtraSpotNumbers.TryGetValue(s.Code, out var tnum)) _ui.SetVariable("/tool_tip:num_contest", tnum);
            // Haken/Medaille auch im Tooltip-Symbol
            var rsT = RaceOf(ms);
            if (rsT != null && rsT.State == 2) _ui.GotoLabel("/tool_tip/ic/done", "on", false); else _ui.GotoFrameNumber("/tool_tip/ic/done", 0);
            int medal = rsT != null && rsT.State == 2 ? rsT.Place : 0;
            _ui.GotoLabel("/tool_tip/ic/rank", medal == 1 ? "gold" : medal == 2 ? "silver" : medal == 3 ? "bronze" : "default", false);
            SetSpotVars(ms);
            _ui.SetClipTexture("/tool_tip/infos/picture/lure_map", ms.bookmark >= 0 || ms.gps ? null : SpotPicture(s));   // auch null (kein altes Bild stehen lassen)
            if (!_ttipOpen) { _ui.CallClip("/tool_tip", "Open"); _ttipOpen = true; }
        }

        /// <summary>Zahlen im Tooltip normaler Spots (Händler: Fahrzeuge, Garagen …); nicht gesetzte bleiben leer (Platzhalter aus).</summary>
        void SetSpotVars(MapSpot ms)
        {
            if (ms.cup >= 0) return;
            var db = Manager?.Database;
            if (ms.label == "carDealer" || ms.label == "bikeDealer" || ms.label == "carRental")
            {
                var shop = db?.ShopFor(ms.spot.Code);
                int n = 0; if (shop != null) foreach (int id in shop.CarIds) if (id != 0) n++;
                _ui.SetVariable("/:nb_cars", n > 0 ? n.ToString() : "");
                _ui.SetVariable("/:NbCarsForRent", n > 0 ? n.ToString() : "");
                int owned = 0;
                var save = Online.TDU2Session.Save;
                if (shop != null && save != null && save.ownedCars != null && db != null)
                    foreach (int id in shop.CarIds)
                        if (id != 0 && db.ById.TryGetValue(id, out var c))
                            foreach (var oc in save.ownedCars) if (string.Equals(oc, c.FileName, StringComparison.OrdinalIgnoreCase)) { owned++; break; }
                _ui.SetVariable("/:nb_cars_bought", owned.ToString());
            }
            else if (ms.label == "home")
            {
                // Haus: Garagenplätze, Stufe und die dort abgestellten Autos (infos.CarsList(false) liest /:nb_cars, /:carN_car_name, carN_gp, carN_state)
                var house = db?.HouseFor(ms.spot.Code);
                var cars = HouseCars(ms.spot.Code);
                int places = house != null && house.CarSlots > 0 ? house.CarSlots : cars.Count;
                _ui.SetVariable("/:nb_cars", cars.Count.ToString());
                _ui.SetVariable("/:nb_garage_places", places > 0 ? places.ToString() : "");
                if (house != null && house.Level > 0) _ui.SetValue("/:HouseLevel", house.Level);
                for (int i = 0; i < 12; i++)
                {
                    var c = i < cars.Count ? cars[i] : null;
                    _ui.SetVariable("/:car" + i + "_car_name", c != null ? ((c.Brand ?? "") + " " + c.ModelName).Trim() : "");
                    _ui.SetVariable("/:car" + i + "_gp", c != null ? GroupLabel(c.Raw != null ? c.Raw.I("_vehiclecategory") : 0) : "");
                    _ui.SetValue("/:car" + i + "_state", false);
                }
            }
        }

        /// <summary>Autos in der Garage eines Hauses – wie beim Betreten der Garage (TDU2Interiors): das gefahrene Auto zuerst,
        /// dann die eigenen Autos aus dem Spielstand, höchstens so viele wie das Haus Stellplätze hat.</summary>
        List<CarPhysics> HouseCars(string code)
        {
            var r = new List<CarPhysics>();
            if (Manager == null) return r;
            var cur = Manager.Current?.Car;
            var owned = Online.TDU2Session.OwnedCars;
            if (cur != null) foreach (var n in owned) if (string.Equals(n, cur.FileName, StringComparison.OrdinalIgnoreCase)) { r.Add(cur); break; }
            foreach (var n in owned) { var c = Manager.FindCar(n); if (c != null && !r.Contains(c)) r.Add(c); }
            var house = Manager.Database?.HouseFor(code);
            if (house != null && house.CarSlots > 0 && r.Count > house.CarSlots) r.RemoveRange(house.CarSlots, r.Count - house.CarSlots);
            return r;
        }

        void HideTooltip()
        {
            if (_ttipOpen && _ui != null) _ui.CallClip("/tool_tip", "Close");
            _ttipOpen = false;
        }
    }
}
