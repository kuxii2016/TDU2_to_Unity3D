using System;
using System.Collections.Generic;
using System.Globalization;
using TDU2.Flash;
using TDU2.Online;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Sticker-Shop (Spot ESTICK_*, Interieur istick_b, Verkäuferin = FpSelectable Typ 28 "StickersShop_area") mit dem
    /// Original-Film frontend/&lt;res&gt;/shopscar.bnk (Labels st_open … st_close, WhatPage "stickers"):
    ///   _root.StickersMenu()  Hauptmenü menu_st: Lackieren / Bekleben / Entfernen / Abbrechen / Bestätigen &amp; bezahlen
    ///   FSCommands "stsh:*" (wie MngCarPainting im Spiel):
    ///     pain:colo:open|vali|canc|clos|type:next|type:prev|bion|biof|bich, pain:myco:init|vali|fill|eras|clos|clea
    ///     stic:list:open|prca|neca|vali|canc|clos, stic:move|rota|scal|alph|colo, stic:appl, stic:flip,
    ///     stic:symn|symf, stic:uscn|uscf, stic:hist:undo|redo|open|canc, stic:colo:myco:*, save:open|vali, clea, canc,
    ///     mcin/mcou (freie Kamera an/aus), menu:enbl|dsbl; außerdem shko (Menü verlassen), dbxo (Dialog), gofr (Shop verlassen).
    /// Im Shop ist nur das eigene Auto zu sehen (Mitspieler ausgeblendet, siehe TDU2Interiors). Ergebnis: <see cref="CarLivery"/>
    /// im Spielstand (<see cref="TDU2Liveries"/>), Mitspieler bekommen sie über die UDP-Synchronisation (TDU2Multiplayer).
    /// Steuerung: Maus = Sticker auf dem Auto platzieren, Klick = aufkleben, rechte Maustaste = Kamera drehen, Mausrad = Werkzeug
    /// (Drehen/Größe/Transparenz) bzw. Zoom, Pfeile/Enter/Rücktaste wie im Film (Move/A/B), X/Y = Tasten X/Y, F = spiegeln.
    /// </summary>
    public sealed class TDU2StickerShop : MonoBehaviour
    {
        public TDU2Interiors Interiors;
        [Tooltip("Grundgröße eines Stickers (halbe Breite in Metern bei 100 %)")]
        public float BaseHalfSize = 0.25f;

        static TDU2StickerShop _inst;
        public static bool IsOpen => _inst != null && _inst._open;
        static int _closedFrame = -10;
        /// <summary>Offen oder gerade geschlossen (die schließende Taste gehört noch dem Shop).</summary>
        public static bool Busy => IsOpen || Time.frameCount - _closedFrame <= 1;

        enum Mode { Menu, Paint, List, Place }

        VmfPlayer _ui;
        bool _open, _hooked;
        Mode _mode;
        CarPhysics _car;
        CarModel _model;
        Transform _carT;
        TDU2LiveryView _view;
        CarLivery _saved, _edit;
        Bounds _bounds;
        Collider _carBox;
        readonly List<MeshCollider> _cols = new List<MeshCollider>();
        readonly Dictionary<Mesh, (int[] tris, Vector3[] verts, Vector2[] uv)> _meshData = new Dictionary<Mesh, (int[], Vector3[], Vector2[])>();
        float _lastEsc = -10f;

        // Kamera
        float _yaw, _pitch = 12f, _dist = 6f;
        Vector3 _savedCamPos; Quaternion _savedCamRot; float _savedFov;

        // Lack
        bool _pBackupHas; Color32 _pBackup, _pBackup2; PaintFinish _fBackup; bool _bBackup;
        int _curColor = 1;          // 1 = Farbe 1, 2 = Farbe 2 (Bicolor)
        Texture2D _grad;
        Vector2 _pickPos = new Vector2(0.5f, 0.5f);

        // Sticker
        int _catIndex;              // Index in _cats
        readonly List<TDU2StickerLibrary.Board> _cats = new List<TDU2StickerLibrary.Board>();
        TDU2StickerLibrary.Board _board;
        int _stickerIndex;
        string _tool = "move";
        float _rot, _sx = 1f, _sy = 1f, _alpha = 1f;
        bool _uniform = true, _symmetry, _flip;
        Color32 _tint = new Color32(255, 255, 255, 255), _tintBackup;
        readonly List<int> _history = new List<int>();
        readonly List<List<StickerPlacement>> _redo = new List<List<StickerPlacement>>();
        bool _listPending;
        float _listSince = -1f;
        readonly HashSet<int> _newBoards = new HashSet<int>();

        void Awake() { _inst = this; }
        void OnDestroy() { if (_inst == this) _inst = null; if (_open) Close(false); }

        // ================================================================== Öffnen / Schließen
        /// <summary>Shop-Menü öffnen (Verkäuferin oder Auto angeklickt). Auto + Film kommen vom Interieur.</summary>
        public bool Open(VmfPlayer ui, CarPhysics car, CarModel model, Transform carT, Collider carBox)
        {
            if (_open) return true;
            if (ui == null || car == null || model == null || carT == null) return false;
            _ui = ui; _car = car; _model = model; _carT = carT; _carBox = carBox;
            if (!_hooked) { _ui.FSCommand += OnCommand; _hooked = true; }
            _ui.MouseEnabled = true;
            string bnk = Interiors != null && Interiors.Manager != null ? Interiors.Manager.BnkDir : null;
            TDU2StickerLibrary.Ensure(bnk);
            Racing.RaceTexts.Ensure(bnk, TDU2PauseMenu.SavedLanguage());   // Planchen-Namen (_name_hc)

            _view = TDU2LiveryView.For(model);
            _saved = TDU2Liveries.Get(car.FileName)?.Clone() ?? new CarLivery();
            _edit = _saved.Clone();
            _view.Show(_edit);
            _history.Clear(); _redo.Clear(); _newBoards.Clear();

            // Kollision für die Sticker-Platzierung: Lackflächen als MeshCollider (nur im Shop)
            if (_carBox != null) _carBox.enabled = false;
            BuildColliders();
            _bounds = new Bounds(_carT.position, Vector3.one);
            bool any = false;
            foreach (var r in _model.Visual.GetComponentsInChildren<Renderer>(false)) { if (!any) { _bounds = r.bounds; any = true; } else _bounds.Encapsulate(r.bounds); }

            // Kamera
            var cam = Camera.main;
            if (cam != null)
            {
                _savedCamPos = cam.transform.position; _savedCamRot = cam.transform.rotation; _savedFov = cam.fieldOfView;
                var d = _bounds.center - cam.transform.position; d.y = 0f;
                _yaw = d.sqrMagnitude > 1e-4f ? Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg : _carT.eulerAngles.y + 135f;
                _dist = Mathf.Clamp(_bounds.extents.magnitude * 2.2f, 4f, 9f);
                _pitch = 14f;
            }
            Interiors?.SetFirstPerson(false);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;

            _open = true;
            _mode = Mode.Menu;
            foreach (var n in new[] { "menu_st", "price", "invoice", "stickers", "paint", "help" }) _ui.Show("/" + n);
            SetMenuVars();
            UpdatePrices();
            _ui.SetValue("/:Input", true);
            _ui.CallClip("/", "StickersMenu");
            int p2 = 0; foreach (var m in model.PaintMaterials) if (CarModel.IsPaint2(m)) p2++;
            DebugManager.Debug("Sticker-Shop geöffnet: " + car.FileName + " (" + _edit.Stickers.Count + " Sticker, Lack-Materialien " + model.PaintMaterials.Count + ", davon Paint2: " + p2 + ")");
            return true;
        }

        void SetMenuVars()
        {
            bool factoryOn = _view != null && _view.HasFactory && !_edit.ClearFactory;
            _ui.SetValue("/:IsVehiclePaintable", !factoryOn);
            _ui.SetValue("/:IsVehicleStickable", true);
            _ui.SetValue("/:IsBike", false);
            _ui.SetValue("/:HasModifiedVehicle", Modified());
            _ui.SetValue("/:CanUndo", _history.Count > 0);
        }

        /// <summary>Shop-Menü schließen. keep = Änderungen behalten (gespeichert), sonst gespeicherten Stand zeigen.</summary>
        public void Close(bool keep)
        {
            if (!_open) return;
            _open = false;
            if (_view != null)
            {
                _view.Preview.Clear();
                _view.Show(keep ? TDU2Liveries.Get(_car.FileName) : _saved);
            }
            foreach (var c in _cols) if (c != null) UnityUtil.SafeDestroy(c);
            _cols.Clear(); _meshData.Clear(); _tints.Clear();
            if (_carBox != null) _carBox.enabled = true;
            var cam = Camera.main;
            if (cam != null) { cam.transform.SetPositionAndRotation(_savedCamPos, _savedCamRot); cam.fieldOfView = _savedFov; }
            Interiors?.SetFirstPerson(true);
            if (_ui != null)
            {
                foreach (var n in new[] { "menu_st", "price", "invoice", "stickers", "paint" }) _ui.Hide("/" + n);
                _ui.GotoLabel("/", "st_stop", false);
                _ui.SetValue("/:Input", true);
                _ui.SetValue("/:CurrentAction", "walk_mode");
            }
            _closedFrame = Time.frameCount;
        }

        // ================================================================== Befehle des Films
        void OnCommand(string cmd, string args)
        {
            if (!_open) return;
            string c = (cmd ?? "").Trim();
            string lc = c.ToLowerInvariant();
            if (TDU2FrontendSounds.Command(lc)) return;
            if (lc == "dbxo") { AnswerDialog(); return; }
            if (lc == "shko" || lc == "wmon" || lc == "liin") { Close(false); return; }   // Menü verlassen -> Laufmodus
            if (lc == "gofr") { Leave(); return; }
            if (!lc.StartsWith("stsh:")) return;
            string s = lc.Substring(5);
            Debug.Log("[TDU2] Sticker-Shop: " + s);
            switch (s)
            {
                case "mcin": case "mcou": case "menu:enbl": case "menu:dsbl": return;

                // ---------------- Lackieren
                case "pain:colo:open": BeginPaint(); return;
                case "pain:colo:vali": _edit.HasPaint = true; if (_edit.Finish == PaintFinish.Original) _edit.Finish = PaintFinish.Gloss; _mode = Mode.Menu; Changed(); return;
                case "pain:colo:canc":
                    _edit.HasPaint = _pBackupHas; _edit.Paint = _pBackup; _edit.Paint2 = _pBackup2; _edit.Finish = _fBackup; _edit.Bicolour = _bBackup;
                    _mode = Mode.Menu; _view.Show(_edit); return;
                case "pain:colo:clos": _mode = Mode.Menu; return;
                case "pain:colo:type:next": CycleFinish(1); return;
                case "pain:colo:type:prev": CycleFinish(-1); return;
                case "pain:colo:bion": _edit.Bicolour = true; _curColor = 1; _view.Show(_edit); return;
                case "pain:colo:biof": _edit.Bicolour = false; _curColor = 1; _view.Show(_edit); return;
                case "pain:colo:bich":
                    _curColor = _ui.GetVariable("/:CurrentColor") == "2" ? 2 : (_curColor == 1 ? 2 : 1);
                    _edit.Bicolour = true;
                    _pickPos = FindInGradient(CurrentPaint());
                    PlaceCursor("/paint/color_box/color_picker");
                    Swatches("/paint/color_box/color_picker");
                    _view.Show(_edit);
                    return;
                case "pain:myco:init": FillMyColors("/paint/my_colors_box"); return;
                case "pain:myco:clea": return;

                // ---------------- Sticker-Liste
                case "stic:list:open": _mode = Mode.List; _view.Preview.Clear(); _view.Refresh(); OpenList(); return;
                case "stic:list:prca": case "stic:list:neca": FillCategory(); return;
                case "stic:list:vali": PickSticker(); return;
                case "stic:list:canc": case "stic:list:clos": if (_mode == Mode.List) _mode = Mode.Menu; return;

                // ---------------- Platzieren
                case "stic:move":
                case "stic:rota":
                case "stic:scal":
                case "stic:alph":
                case "stic:colo":
                    _tool = s.Substring(5); if (_tool == "colo") BeginStickerColor(); UpdateInfos(); return;
                case "stic:appl": Apply(); return;
                case "stic:flip": _flip = !_flip; return;
                case "stic:symn": _symmetry = true; return;
                case "stic:symf": _symmetry = false; return;
                case "stic:uscn": _uniform = true; _sy = _sx; UpdateInfos(); return;
                case "stic:uscf": _uniform = false; return;
                case "stic:hist:undo": Undo(); return;
                case "stic:hist:redo": Redo(); return;
                case "stic:hist:open": case "stic:hist:canc": return;

                // ---------------- Bezahlen / Entfernen / Abbrechen
                case "save:open": UpdatePrices(); return;
                case "save:vali": Save(); return;
                case "clea":
                    _edit.Stickers.Clear(); _edit.ClearFactory = true; _history.Clear(); _redo.Clear();
                    Changed(); return;
                case "canc": _edit = _saved.Clone(); _view.Show(_edit); _history.Clear(); _redo.Clear(); _newBoards.Clear(); UpdatePrices(); return;
            }
            // "Meine Farben" (stPart = pain bzw. stic:colo): <part>:myco:vali|fill|eras|clos
            int k = s.IndexOf("myco:", StringComparison.Ordinal);
            if (k >= 0)
            {
                bool paint = s.StartsWith("pain");
                string box = paint ? "/paint/my_colors_box" : "/stickers/colors/my_colors_box";
                string op = s.Substring(k + 5);
                int slot = 0; int.TryParse(_ui.GetVariable(box + ":num_c") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out slot);
                if (op == "fill") { TDU2Liveries.SetFavorite(slot, ToRgb(paint ? CurrentPaint() : _tint)); FillMyColors(box); }
                else if (op == "eras") { TDU2Liveries.SetFavorite(slot, -1); FillMyColors(box); }
                else if (op == "vali")
                {
                    int rgb = TDU2Liveries.Favorite(slot);
                    if (rgb >= 0) { var col = FromRgb(rgb); if (paint) SetPaint(col); else SetTint(col); }
                }
                else if (op == "init") FillMyColors(box);
            }
            else if (s.StartsWith("stic:colo:")) { if (_mode == Mode.Place) _tool = "colo"; }
        }

        void AnswerDialog()
        {
            string dc = _ui.GetVariable("/:DgbxCase") ?? "";
            _ui.SetVariable("/:PlayerAnswer", "a");
            _ui.CallClip("/", "DialogBox");
        }

        void Leave()
        {
            var it = Interiors; var car = _car;
            Close(true);
            if (it != null) it.ExitInterior(it.LastCar ?? car);
        }

        // ================================================================== Lack
        void BeginPaint()
        {
            _mode = Mode.Paint;
            _pBackupHas = _edit.HasPaint; _pBackup = _edit.Paint; _pBackup2 = _edit.Paint2; _fBackup = _edit.Finish; _bBackup = _edit.Bicolour;
            if (!_edit.HasPaint)
            {
                _edit.Paint = (Color32)_view.OriginalPaint;
                _edit.Paint2 = (Color32)_view.OriginalPaint2;
                _edit.Bicolour = false;
            }
            if (_edit.Finish == PaintFinish.Original) _edit.Finish = PaintFinish.Gloss;
            _edit.HasPaint = true;   // Vorschau sofort (Abbrechen stellt den alten Stand wieder her)
            _curColor = 1;
            _ui.SetValue("/:CurrentColor", 1);
            _ui.SetValue("/:BicolourPaint", _edit.Bicolour);
            _ui.SetVariable("/:ColorType", FinishName(_edit.Finish));
            const string picker = "/paint/color_box/color_picker";
            _ui.SetClipTexture(picker + "/lure_grad", Gradient());
            _pickPos = FindInGradient(_edit.Paint);
            PlaceCursor(picker);
            Swatches(picker);
            _view.Show(_edit);
            UpdatePrices();
        }

        Color32 CurrentPaint() => _curColor == 2 ? _edit.Paint2 : _edit.Paint;

        void SetPaint(Color32 c)
        {
            c.a = 255;
            if (_curColor == 2) { _edit.Paint2 = c; _edit.Bicolour = true; } else _edit.Paint = c;
            _edit.HasPaint = true;
            Swatches("/paint/color_box/color_picker");
            _view.Show(_edit);
        }

        void CycleFinish(int dir)
        {
            int f = (int)_edit.Finish; if (f < 1 || f > 3) f = 1;
            f += dir; if (f > 3) f = 1; if (f < 1) f = 3;
            _edit.Finish = (PaintFinish)f;
            _ui.SetVariable("/:ColorType", FinishName(_edit.Finish));
            _view.Show(_edit);
            _ui.SetValue("/:Input", true);
        }

        static string FinishName(PaintFinish f)
        {
            bool de = TDU2PauseMenu.SavedLanguage() == "ge";
            switch (f)
            {
                case PaintFinish.Metallic: return de ? "Metallic" : "Metallic";
                case PaintFinish.Matt: return de ? "Matt" : "Matte";
                default: return de ? "Glänzend" : "Glossy";
            }
        }

        void Swatches(string picker)
        {
            bool paint = picker.StartsWith("/paint");
            Tint(picker + "/color1/cube/color", paint ? _edit.Paint : _tint);
            if (paint) Tint(picker + "/color2/cube/color", _edit.Paint2);
        }

        // Farbfelder (color1/2/cube/color, my_colors c0..c5): der Film legt die Clips bei jedem Labelwechsel neu an -> jedes Bild neu einfärben
        readonly Dictionary<string, Color32> _tints = new Dictionary<string, Color32>();

        void Tint(string path, Color32 c)
        {
            _tints[path] = c;
            ApplyTint(path, c);
        }

        void ApplyTint(string path, Color32 c)
        {
            var d = _ui.Find(path);
            if (d == null) return;
            d.Cx = new ColorTransform { Mul = new Vector4(0f, 0f, 0f, 1f), Add = new Vector4(c.r / 255f, c.g / 255f, c.b / 255f, 0f) };
            d.ScriptCx = true;
        }

        void FillMyColors(string box)
        {
            for (int i = 0; i < 6; i++)
            {
                int rgb = TDU2Liveries.Favorite(i);
                _ui.SetValue(box + "/c" + i + ":isColor", rgb >= 0);
                _ui.GotoLabel(box + "/c" + i + "/cube", rgb >= 0 ? "default" : "empty", false);
                if (rgb >= 0) Tint(box + "/c" + i + "/cube/color", FromRgb(rgb));
            }
            Tint(box + "/picked_color/color", box.StartsWith("/paint") ? CurrentPaint() : _tint);
        }

        static int ToRgb(Color32 c) => (c.r << 16) | (c.g << 8) | c.b;
        static Color32 FromRgb(int v) => new Color32((byte)(v >> 16), (byte)(v >> 8), (byte)v, 255);

        // ---- Farbverlauf (lure_grad): x = Farbton, y = oben Weiß -> Mitte reine Farbe -> unten Schwarz
        Texture2D Gradient()
        {
            if (_grad != null) return _grad;
            const int W = 256, H = 128;
            _grad = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "TDU2 Farbwähler", hideFlags = HideFlags.DontSave };
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    px[y * W + x] = GradColor(x / (W - 1f), y / (H - 1f));   // Zeile 0 = oben (Clip-Texturen wie .2db)
            _grad.SetPixels32(px);
            _grad.Apply(false, false);
            return _grad;
        }

        static Color32 GradColor(float x, float y)
        {
            // rechter Rand: Graustufen
            Color hue = x > 0.94f ? Color.gray : Color.HSVToRGB(Mathf.Clamp01(x / 0.94f), 1f, 1f);
            Color c = y < 0.5f ? Color.Lerp(Color.white, hue, y * 2f) : Color.Lerp(hue, Color.black, (y - 0.5f) * 2f);
            if (x > 0.94f) c = Color.Lerp(Color.white, Color.black, y);
            return c;
        }

        static Vector2 FindInGradient(Color32 c)
        {
            float best = float.MaxValue; var bp = new Vector2(0.5f, 0.5f);
            for (int y = 0; y <= 32; y++)
                for (int x = 0; x <= 64; x++)
                {
                    var p = new Vector2(x / 64f, y / 32f);
                    var g = GradColor(p.x, p.y);
                    float d = (g.r - c.r) * (g.r - c.r) + (g.g - c.g) * (g.g - c.g) + (g.b - c.b) * (g.b - c.b);
                    if (d < best) { best = d; bp = p; }
                }
            return bp;
        }

        void PlaceCursor(string picker)
        {
            var r = _ui.ScreenRect(picker + "/lure_grad");
            if (!r.HasValue) return;
            var gui = new Vector2(Mathf.Lerp(r.Value.xMin, r.Value.xMax, _pickPos.x), Mathf.Lerp(r.Value.yMin, r.Value.yMax, _pickPos.y));
            var local = _ui.GuiToLocal(picker, gui);
            if (local.HasValue) _ui.SetPosition(picker + (_curColor == 2 ? "/cursor_2" : "/cursor"), local.Value);
        }

        /// <summary>Maus im Farbverlauf: Farbe wählen (true, wenn die Maus darin liegt und gedrückt ist).</summary>
        bool PickWithMouse(string picker, Action<Color32> set)
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            if (!Input.GetMouseButton(0)) return false;
            var r = _ui.ScreenRect(picker + "/lure_grad");
            if (!r.HasValue) return false;
            var mp = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            if (!r.Value.Contains(mp)) return false;
            _pickPos = new Vector2(Mathf.InverseLerp(r.Value.xMin, r.Value.xMax, mp.x), Mathf.InverseLerp(r.Value.yMin, r.Value.yMax, mp.y));
            set(GradColor(_pickPos.x, _pickPos.y));
            PlaceCursor(picker);
            return true;
#else
            return false;
#endif
        }

        void MovePick(string picker, Vector2 delta, Action<Color32> set)
        {
            _pickPos = new Vector2(Mathf.Clamp01(_pickPos.x + delta.x), Mathf.Clamp01(_pickPos.y + delta.y));
            set(GradColor(_pickPos.x, _pickPos.y));
            PlaceCursor(picker);
        }

        // ================================================================== Sticker-Liste
        void OpenList()
        {
            _cats.Clear();
            foreach (var b in TDU2StickerLibrary.Boards) if (b.Type == 1 || b.Type == 4 || b.Type == 5) _cats.Add(b);
            if (_cats.Count == 0 && !TDU2StickerLibrary.Ready) _listPending = true;
            if (_board != null) { int i = _cats.IndexOf(_board); if (i >= 0) _catIndex = i; }
            _ui.SetValue("/:CurrentStickersCat", Mathf.Clamp(_catIndex, 0, Math.Max(0, _cats.Count - 1)));
            FillCategory();
        }

        void FillCategory()
        {
            int nb = _cats.Count;
            int cur = 0; int.TryParse(_ui.GetVariable("/:CurrentStickersCat") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out cur);
            if (nb > 0) cur = ((cur % nb) + nb) % nb;
            _catIndex = cur;
            _ui.SetValue("/:NbStickersCat", Math.Max(1, nb));
            _ui.SetValue("/:CurrentStickersCat", cur);
            var b = nb > 0 ? _cats[cur] : null;
            if (b == null)
            {
                _ui.SetValue("/:NbStickersCurPage", 0);
                _ui.SetVariable("/:StickersCatStr", TDU2StickerLibrary.Ready ? "-" : "…");
                _ui.SetValue("/:CategoryLoaded", TDU2StickerLibrary.Ready);
                return;
            }
            bool owned = b.Price <= 0 || TDU2Liveries.HasBoard(b.Id) || _newBoards.Contains(b.Id);
            bool locked = (b.Type == 4 || b.Type == 5) && !TDU2Liveries.HasBoard(b.Id);
            _ui.SetVariable("/:StickersCatStr", Racing.RaceTexts.Get(b.NameHc, b.BankFile));
            _ui.SetVariable("/:PriceCat", owned ? "" : Money(b.Price));
            _ui.SetValue("/:IsCategoryBought", owned);
            _ui.SetValue("/:StickLocked", locked);
            _ui.SetValue("/:DLC_Locked", false);
            _ui.SetValue("/:CanColourSticker", b.Mono);
            bool ready = TDU2StickerLibrary.Request(b);
            // Planche fehlerhaft oder lädt zu lange: Liste trotzdem freigeben (sonst läuft die Ladeanimation endlos)
            bool give = ready || b.Failed || (_listSince > 0f && Time.unscaledTime - _listSince > 6f);
            if (!ready && !give && _listSince < 0f) _listSince = Time.unscaledTime;
            int n = ready ? b.Count : (b.Failed ? 0 : 24);
            _ui.SetValue("/:NbStickersCurPage", Math.Min(24, n));
            _ui.SetValue("/:CategoryLoaded", give);
            _listPending = !ready && !b.Failed;
            if (give) _listSince = ready || b.Failed ? -1f : _listSince;
            DebugManager.Debug("Sticker-Liste: Planche " + (cur + 1) + "/" + nb + " " + b.BankFile + (ready ? " (" + b.Count + " Sticker)" : b.Failed ? " (Fehler)" : " (lädt …)"));
            for (int i = 0; i < 24; i++)
                _ui.SetClipTexture("/stickers/list/container/st" + i + "/lure", ready && i < b.Count ? b.Textures[i] : null);
        }

        void PickSticker()
        {
            if (_catIndex < 0 || _catIndex >= _cats.Count) return;
            var b = _cats[_catIndex];
            int idx = 0; int.TryParse(_ui.GetVariable("/:SelectedSticker") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out idx);
            if (!b.Loaded || idx < 0 || idx >= b.Count) return;
            _board = b; _stickerIndex = idx;
            _mode = Mode.Place;
            _tool = "move";
            _rot = 0f; _sx = _sy = 1f; _alpha = 1f; _flip = false;
            _uniform = true; _ui.SetValue("/:IsUniformScale", true);
            _symmetry = false; _ui.SetValue("/:UseSymmetry", false);
            if (!b.Mono) _tint = new Color32(255, 255, 255, 255);
            _ui.SetValue("/:CanColourSticker", b.Mono);
            if (b.Price > 0 && !TDU2Liveries.HasBoard(b.Id)) _newBoards.Add(b.Id);
            UpdateInfos();
            UpdatePrices();
        }

        void BeginStickerColor()
        {
            const string picker = "/stickers/colors/color_box/color_picker";
            _tintBackup = _tint;
            _ui.SetClipTexture(picker + "/lure_grad", Gradient());
            _curColor = 1;
            _pickPos = FindInGradient(_tint);
            PlaceCursor(picker);
            Swatches(picker);
            FillMyColors("/stickers/colors/my_colors_box");
        }

        void SetTint(Color32 c) { c.a = 255; _tint = c; Swatches("/stickers/colors/color_box/color_picker"); }

        void UpdateInfos()
        {
            var ci = CultureInfo.GetCultureInfo("de-DE");
            _ui.SetVariable("/:StickerRotateAngleStr", (((_rot % 360f) + 360f) % 360f).ToString("0.0", ci) + "°");
            _ui.SetVariable("/:StickerXScaleStr", (_sx * 100f).ToString("0", ci) + " %");
            _ui.SetVariable("/:StickerYScaleStr", (_sy * 100f).ToString("0", ci) + " %");
            _ui.SetVariable("/:StickerAlphaStr", (_alpha * 100f).ToString("0", ci) + " %");
        }

        // ================================================================== Platzieren (Strahl auf die Lackflächen, UV-Satz 1)
        void BuildColliders()
        {
            var paint = new HashSet<Material>(_model.PaintMaterials);
            foreach (var mr in _model.Visual.GetComponentsInChildren<MeshRenderer>(false))
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                bool has = false;
                foreach (var m in mr.sharedMaterials) if (m != null && paint.Contains(m)) { has = true; break; }
                if (!has) continue;
                try
                {
                    var mc = mr.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                    mr.gameObject.layer = 2;
                    _cols.Add(mc);
                }
                catch (Exception ex) { DebugManager.Error("Sticker-Shop Kollision: " + ex.Message); }
            }
        }

        (int[] tris, Vector3[] verts, Vector2[] uv) MeshData(Mesh m)
        {
            if (_meshData.TryGetValue(m, out var d)) return d;
            var uv = new List<Vector2>();
            m.GetUVs(CarModel.StickerUvSet >= 0 ? CarModel.StickerUvSet : 1, uv);
            d = (m.triangles, m.vertices, uv.ToArray());
            _meshData[m] = d;
            return d;
        }

        bool PaintTriangle(MeshCollider mc, int tri)
        {
            var mesh = mc.sharedMesh; var mr = mc.GetComponent<MeshRenderer>();
            if (mesh == null || mr == null) return false;
            var mats = mr.sharedMaterials;
            int idx = tri * 3;
            for (int s = 0; s < mesh.subMeshCount && s < mats.Length; s++)
            {
                var sm = mesh.GetSubMesh(s);
                if (idx >= sm.indexStart && idx < sm.indexStart + sm.indexCount) return mats[s] != null && _model.PaintMaterials.Contains(mats[s]);
            }
            return false;
        }

        bool CastCar(Ray ray, out RaycastHit hit, out MeshCollider col)
        {
            col = null;
            var hits = UnityEngine.Physics.RaycastAll(ray, 40f, 1 << 2, QueryTriggerInteraction.Ignore);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                var mc = h.collider as MeshCollider;
                if (mc == null || !_cols.Contains(mc)) continue;
                hit = h; col = mc;
                return PaintTriangle(mc, h.triangleIndex);   // erste Fläche zählt (Glas/Felge davor -> keine Platzierung)
            }
            hit = default;
            return false;
        }

        /// <summary>Weltvektor in der Tangentialebene des Treffers -> UV-Vektor (über dP/du, dP/dv des Dreiecks).</summary>
        bool Jacobian(MeshCollider mc, int tri, out Vector3 dPdu, out Vector3 dPdv)
        {
            dPdu = dPdv = Vector3.zero;
            var d = MeshData(mc.sharedMesh);
            if (d.uv == null || d.uv.Length == 0 || tri * 3 + 2 >= d.tris.Length) return false;
            int i0 = d.tris[tri * 3], i1 = d.tris[tri * 3 + 1], i2 = d.tris[tri * 3 + 2];
            if (i0 >= d.uv.Length || i1 >= d.uv.Length || i2 >= d.uv.Length) return false;
            var t = mc.transform;
            Vector3 p0 = t.TransformPoint(d.verts[i0]), p1 = t.TransformPoint(d.verts[i1]), p2 = t.TransformPoint(d.verts[i2]);
            Vector2 u0 = d.uv[i0], u1 = d.uv[i1], u2 = d.uv[i2];
            Vector3 e1 = p1 - p0, e2 = p2 - p0;
            float du1 = u1.x - u0.x, dv1 = u1.y - u0.y, du2 = u2.x - u0.x, dv2 = u2.y - u0.y;
            float det = du1 * dv2 - du2 * dv1;
            if (Mathf.Abs(det) < 1e-12f) return false;
            dPdu = (e1 * dv2 - e2 * dv1) / det;
            dPdv = (e2 * du1 - e1 * du2) / det;
            return true;
        }

        static Vector2 ToUv(Vector3 w, Vector3 T, Vector3 B)
        {
            float a = Vector3.Dot(T, T), b = Vector3.Dot(T, B), c = Vector3.Dot(B, B);
            float det = a * c - b * b;
            if (Mathf.Abs(det) < 1e-12f) return Vector2.zero;
            float rt = Vector3.Dot(T, w), rb = Vector3.Dot(B, w);
            return new Vector2((c * rt - b * rb) / det, (a * rb - b * rt) / det);
        }

        Vector3 MirrorPoint(Vector3 p) { var l = _carT.InverseTransformPoint(p); l.x = -l.x; return _carT.TransformPoint(l); }
        Vector3 MirrorDir(Vector3 v) { var l = _carT.InverseTransformDirection(v); l.x = -l.x; return _carT.TransformDirection(l); }

        StickerPlacement MakePlacement(RaycastHit hit, MeshCollider mc, Vector3 right, Vector3 up)
        {
            if (!Jacobian(mc, hit.triangleIndex, out var T, out var B)) return null;
            var uv = hit.textureCoord2;
            if (CarModel.StickerUvSet == 0) uv = hit.textureCoord;
            float hw = BaseHalfSize * _sx, hh = BaseHalfSize * _sy;
            var a = ToUv(right * hw * (_flip ? -1f : 1f), T, B);
            var b = ToUv(up * hh, T, B);
            if (a.sqrMagnitude > 1f || b.sqrMagnitude > 1f) return null;   // entartet (Naht)
            return new StickerPlacement
            {
                Board = (byte)_board.Id,
                Index = (byte)_stickerIndex,
                U = uv.x,
                V = uv.y,
                A = a,
                B = b,
                Tint = new Color32(_tint.r, _tint.g, _tint.b, (byte)Mathf.RoundToInt(_alpha * 255f)),
                Flags = (byte)(_flip ? 1 : 0),
            };
        }

        /// <summary>Vorschau-Sticker unter der Maus (+ Spiegelbild bei Symmetrie).</summary>
        List<StickerPlacement> PlacementsAtMouse()
        {
            var res = new List<StickerPlacement>();
            var cam = Camera.main;
            if (cam == null || _board == null) return res;
#if ENABLE_LEGACY_INPUT_MANAGER
            if (_ui.MouseOverButton) return res;
            var ray = cam.ScreenPointToRay(Input.mousePosition);
#else
            var ray = new Ray(cam.transform.position, cam.transform.forward);
#endif
            if (!CastCar(ray, out var hit, out var mc)) return res;
            var n = hit.normal;
            var right = Vector3.ProjectOnPlane(cam.transform.right, n);
            if (right.sqrMagnitude < 1e-6f) right = Vector3.ProjectOnPlane(cam.transform.up, n);
            right.Normalize();
            var up = Vector3.Cross(right, n).normalized;
            var q = Quaternion.AngleAxis(_rot, n);
            right = q * right; up = q * up;
            var p = MakePlacement(hit, mc, right, up);
            if (p == null) return res;
            res.Add(p);
            if (_symmetry)
            {
                var mRay = new Ray(MirrorPoint(ray.origin), MirrorDir(ray.direction));
                if (CastCar(mRay, out var h2, out var mc2) && (h2.point - MirrorPoint(hit.point)).sqrMagnitude < 0.15f * 0.15f)
                {
                    var p2 = MakePlacement(h2, mc2, MirrorDir(right), MirrorDir(up));
                    if (p2 != null && (new Vector2(p2.U - p.U, p2.V - p.V)).sqrMagnitude > 1e-5f) res.Add(p2);
                }
            }
            return res;
        }

        void Apply()
        {
            if (_mode != Mode.Place || _board == null) return;
            var list = PlacementsAtMouse();
            if (list.Count == 0) return;
            if (_edit.Stickers.Count + list.Count > CarLivery.MaxStickers) { DebugManager.Warning("Sticker-Shop: maximal " + CarLivery.MaxStickers + " Sticker"); return; }
            _edit.Stickers.AddRange(list);
            _history.Add(list.Count);
            _redo.Clear();
            Changed();
        }

        void Undo()
        {
            if (_history.Count == 0) return;
            int n = _history[_history.Count - 1]; _history.RemoveAt(_history.Count - 1);
            n = Math.Min(n, _edit.Stickers.Count);
            var removed = _edit.Stickers.GetRange(_edit.Stickers.Count - n, n);
            _edit.Stickers.RemoveRange(_edit.Stickers.Count - n, n);
            _redo.Add(removed);
            Changed();
        }

        void Redo()
        {
            if (_redo.Count == 0) return;
            var l = _redo[_redo.Count - 1]; _redo.RemoveAt(_redo.Count - 1);
            _edit.Stickers.AddRange(l);
            _history.Add(l.Count);
            Changed();
        }

        // ================================================================== Preise / Speichern
        bool PaintChanged() => _edit.HasPaint != _saved.HasPaint || (_edit.HasPaint && (!Same(_edit.Paint, _saved.Paint) || !Same(_edit.Paint2, _saved.Paint2) || _edit.Finish != _saved.Finish || _edit.Bicolour != _saved.Bicolour));
        static bool Same(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b;
        bool StickersChanged()
        {
            if (_edit.Stickers.Count != _saved.Stickers.Count || _edit.ClearFactory != _saved.ClearFactory) return true;
            for (int i = 0; i < _edit.Stickers.Count; i++) if (!ReferenceEquals(_edit.Stickers[i], _saved.Stickers[i]) && _edit.Stickers[i].U != _saved.Stickers[i].U) return true;
            return false;
        }
        bool Modified() => PaintChanged() || StickersChanged();

        void Changed()
        {
            _view.Show(_edit);
            _ui.SetValue("/:HasModifiedVehicle", Modified());
            _ui.SetValue("/:CanUndo", _history.Count > 0);
            bool factoryOn = _view.HasFactory && !_edit.ClearFactory;
            _ui.SetValue("/:IsVehiclePaintable", !factoryOn);
            UpdatePrices();
        }

        long Total(out long labour, out long paint, out long stickers)
        {
            bool p = PaintChanged(), s = StickersChanged();
            labour = p || s ? TDU2StickerLibrary.LabourPrice : 0;
            paint = p ? TDU2StickerLibrary.PaintPrice : 0;
            stickers = 0;
            var used = new HashSet<int>();
            foreach (var st in _edit.Stickers) used.Add(st.Board);
            foreach (var id in used)
            {
                if (TDU2Liveries.HasBoard(id)) continue;
                var b = TDU2StickerLibrary.ById(id);
                if (b != null && b.Price > 0) stickers += b.Price;
            }
            if (_edit.ClearFactory && !_saved.ClearFactory) stickers += TDU2StickerLibrary.ErasePrice;
            return labour + paint + stickers;
        }

        void UpdatePrices()
        {
            long total = Total(out long labour, out long paint, out long stickers);
            long money = TDU2Session.Save != null ? TDU2Session.Save.money : 0;
            bool enough = TDU2Session.Save == null || money >= total;
            _ui.SetVariable("/:LabourCostStr", Money(labour));
            _ui.SetVariable("/:PaintCostStr", Money(paint));
            _ui.SetVariable("/:StickersCostStr", Money(stickers));
            _ui.SetVariable("/:TotalPriceStr", Money(total));
            _ui.SetVariable("/:current_price", Money(total));
            _ui.SetVariable("/:MyMoneyStr", TDU2Session.Save != null ? Money(money) : " ");
            _ui.SetVariable("/:ClubMoney", " ");
            _ui.SetValue("/:EnoughMoney", enough);
        }

        static string Money(long v) => "$ " + v.ToString("N0", CultureInfo.GetCultureInfo("de-DE"));

        void Save()
        {
            long total = Total(out _, out _, out _);
            var sv = TDU2Session.Save;
            if (sv != null) sv.money = Math.Max(0, sv.money - total);
            var boards = new List<int>();
            foreach (var st in _edit.Stickers) if (!TDU2Liveries.HasBoard(st.Board)) boards.Add(st.Board);
            if (boards.Count > 0) TDU2Liveries.AddBoards(boards);
            TDU2Liveries.Set(_car.FileName, _edit);   // speichert (Server + lokal) und meldet die Änderung (Mitspieler)
            _saved = _edit.Clone();
        }

        // ================================================================== Eingabe + Kamera
        void Update()
        {
            if (!_open || _ui == null) return;
            if (_listPending && _mode == Mode.List && TDU2StickerLibrary.Ready)
            {
                var cb = _cats.Count > 0 ? _cats[Mathf.Clamp(_catIndex, 0, _cats.Count - 1)] : null;
                if (_cats.Count == 0) OpenList();
                else if (cb == null || cb.Loaded || cb.Failed || (_listSince > 0f && Time.unscaledTime - _listSince > 6f)) FillCategory();
            }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (TDU2GameData.ChatTyping) return;
            // ---- Film-Tasten
            bool myColors = (_mode == Mode.Paint && IsOpenLabel("/paint/my_colors_box")) || (_mode == Mode.Place && _tool == "colo" && IsOpenLabel("/stickers/colors/my_colors_box"));
            bool arrowsToFilm = _mode == Mode.Menu || _mode == Mode.List || myColors || _mode == Mode.Place;
            if (arrowsToFilm)
            {
                if (Input.GetKeyDown(KeyCode.UpArrow)) _ui.Input("Move", "up");
                if (Input.GetKeyDown(KeyCode.DownArrow)) _ui.Input("Move", "down");
                if (Input.GetKeyDown(KeyCode.LeftArrow)) _ui.Input("Move", "left");
                if (Input.GetKeyDown(KeyCode.RightArrow)) _ui.Input("Move", "right");
            }
            if (_mode == Mode.List)
            {
                if (Input.GetKeyDown(KeyCode.Q) || Input.GetKeyDown(KeyCode.PageUp)) _ui.CallClip("/stickers/list", "changeCategory", "left");
                if (Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.PageDown)) _ui.CallClip("/stickers/list", "changeCategory", "right");
            }
            if (_mode == Mode.Paint && !myColors)
            {
                if (Input.GetKeyDown(KeyCode.Q) || Input.GetKeyDown(KeyCode.PageUp)) _ui.CallClip("/paint", "changeType", "left");
                if (Input.GetKeyDown(KeyCode.E) || Input.GetKeyDown(KeyCode.PageDown)) _ui.CallClip("/paint", "changeType", "right");
                var dv = Vector2.zero;
                if (Input.GetKey(KeyCode.LeftArrow)) dv.x -= 1f; if (Input.GetKey(KeyCode.RightArrow)) dv.x += 1f;
                if (Input.GetKey(KeyCode.UpArrow)) dv.y -= 1f; if (Input.GetKey(KeyCode.DownArrow)) dv.y += 1f;
                if (dv != Vector2.zero) MovePick("/paint/color_box/color_picker", dv * Time.unscaledDeltaTime * 0.35f, SetPaint);
                PickWithMouse("/paint/color_box/color_picker", SetPaint);
            }
            if (_mode == Mode.Place && _tool == "colo" && !myColors) PickWithMouse("/stickers/colors/color_box/color_picker", SetTint);
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space)) _ui.Input("A");
            if (Input.GetKeyDown(KeyCode.X)) _ui.Input("X");
            if (Input.GetKeyDown(KeyCode.Y) || Input.GetKeyDown(KeyCode.Z))
            {
                _ui.Input("Y");
            }
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace))
            {
                // doppeltes ESC: Menü sicher verlassen (falls der Film hängt)
                if (Time.unscaledTime - _lastEsc < 1.2f) { Close(false); return; }
                _lastEsc = Time.unscaledTime;
                if (!_ui.Input("B")) Close(false);
            }

            // ---- Platzieren
            if (_mode == Mode.Place && _board != null)
            {
                if (Input.GetKeyDown(KeyCode.F)) _flip = !_flip;
                float wheel = Input.GetAxis("Mouse ScrollWheel");
                bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
                if (Mathf.Abs(wheel) > 1e-4f && !ctrl && _tool != "move" && _tool != "colo")
                {
                    float st = Mathf.Sign(wheel);
                    if (_tool == "rota") _rot += st * 5f;
                    else if (_tool == "scal") { _sx = Mathf.Clamp(_sx * (1f + st * 0.06f), 0.1f, 6f); if (_uniform) _sy = _sx; }
                    else if (_tool == "alph") _alpha = Mathf.Clamp(_alpha + st * 0.05f, 0.1f, 1f);
                    UpdateInfos();
                    wheel = 0f;
                }
                if (Input.GetKey(KeyCode.Q)) { _rot -= 90f * Time.unscaledDeltaTime; UpdateInfos(); }
                if (Input.GetKey(KeyCode.E)) { _rot += 90f * Time.unscaledDeltaTime; UpdateInfos(); }
                if (Input.GetKey(KeyCode.KeypadPlus) || Input.GetKey(KeyCode.Equals)) { _sx = Mathf.Min(6f, _sx * (1f + Time.unscaledDeltaTime)); if (_uniform) _sy = _sx; UpdateInfos(); }
                if (Input.GetKey(KeyCode.KeypadMinus) || Input.GetKey(KeyCode.Minus)) { _sx = Mathf.Max(0.1f, _sx * (1f - Time.unscaledDeltaTime)); if (_uniform) _sy = _sx; UpdateInfos(); }
                if (!_uniform && (Input.GetKey(KeyCode.PageUp) || Input.GetKey(KeyCode.PageDown))) { _sy = Mathf.Clamp(_sy * (1f + (Input.GetKey(KeyCode.PageUp) ? 1f : -1f) * Time.unscaledDeltaTime), 0.1f, 6f); UpdateInfos(); }
                if (Mathf.Abs(wheel) > 1e-4f) _dist = Mathf.Clamp(_dist * (1f - wheel * 1.5f), 2.5f, 14f);

                _view.Preview.Clear();
                if (_tool != "colo" || !_ui.MouseOverButton) _view.Preview.AddRange(PlacementsAtMouse());
                _view.Refresh();
                if (Input.GetMouseButtonDown(0) && !_ui.MouseOverButton && _tool != "colo" && _view.Preview.Count > 0) _ui.Input("A");   // Werkzeugleiste: A = aufkleben (stsh:stic:appl)
            }
            else
            {
                if (_view.Preview.Count > 0) { _view.Preview.Clear(); _view.Refresh(); }
                float wheel = Input.GetAxis("Mouse ScrollWheel");
                if (Mathf.Abs(wheel) > 1e-4f && !_ui.MouseOverButton) _dist = Mathf.Clamp(_dist * (1f - wheel * 1.5f), 2.5f, 14f);
            }
            if (Input.GetMouseButton(1))
            {
                _yaw += Input.GetAxis("Mouse X") * 3f;
                _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * 3f, -5f, 70f);
            }
#endif
        }

        bool IsOpenLabel(string path)
        {
            var l = _ui.Label(path);
            return l == "open" || l == "stop";
        }

        void LateUpdate()
        {
            if (!_open) return;
            foreach (var kv in _tints) ApplyTint(kv.Key, kv.Value);
            var cam = Camera.main;
            if (cam == null) return;
            var target = _bounds.center;
            var rot = Quaternion.Euler(_pitch, _yaw, 0f);
            var pos = target - rot * Vector3.forward * _dist;
            // nicht durch Wände/Boden des Shops
            if (UnityEngine.Physics.Raycast(target, (pos - target).normalized, out var hit, _dist, ~(1 << 2), QueryTriggerInteraction.Ignore))
                pos = hit.point + (target - pos).normalized * 0.3f;
            cam.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(target - pos, Vector3.up));
            cam.fieldOfView = 50f;
        }
    }
}