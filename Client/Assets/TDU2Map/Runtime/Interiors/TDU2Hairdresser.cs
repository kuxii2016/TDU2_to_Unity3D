using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TDU2.Avatars;
using TDU2.Flash;
using TDU2.Online;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Friseur (Spots EHD_*, Interieur ihd_b bzw. ihair_ca, FpSelectable "Hairdresser_area" Typ 29) mit dem Original-Film
    /// frontend/&lt;res&gt;/shopsavatar.bnk ab Label "hairdresser": openMenuHairDresser() -> hairdresser_edit, menu_sq mit
    /// Frisur (manhaircut/womanhaircut), Bart (pilosity), Augenbrauen, Abbrechen (hd_cancel), Bestätigen &amp; bezahlen (hd_valid).
    /// Untermenü hair_menu: Auswahlboxen mit FSCommands "hadr:edit:&lt;maha|woha|pilo&gt;:&lt;type|cuts|colo&gt;:scne|scpr",
    /// Anzeige über _root.HairType/HairCut/HairColor (bzw. Pilosity*), A = ":vali", B = ":canc". Bezahlen: "evmo" (EnoughMoney),
    /// Dialog hadr_valid -> "hadr:edit:vali", "moup", "hadr:clos"; Abbruch -> "hadr:edit:canc", "hadr:clos".
    /// Frisuren aus DBClothe (Teil 3, _haircutstyle 1 kurz, 2 lang, 4 Afro, 9 zusammengebunden), Bärte (Teil 9: 8 Kinnbart,
    /// 7 Schnurrbart, 6 Vollbart, M_NO_BEARD rasiert), 15 Farben. Avatar sitzt im Friseurstuhl (M_HEAD: Avatar, hd_sitstand_loop),
    /// Kamera C_HEAD, die Friseurin an ihrem M_HEAD-Platz.
    /// </summary>
    public sealed class TDU2Hairdresser : MonoBehaviour
    {
        public TDU2Interiors Interiors;
        static TDU2Hairdresser _inst;
        public static bool IsOpen => _inst != null && _inst._open;
        static int _closedFrame = -10;
        public static bool Busy => IsOpen || Time.frameCount - _closedFrame <= 1;

        const int StyleShort = 1, StyleLong = 2, StyleAfro = 4, StyleAttached = 9;
        const int BeardFull = 6, BeardMoustache = 7, BeardGoatee = 8;

        VmfPlayer _ui;
        TDU2InteriorNpcs _npcs;
        TDU2InteriorNpcs.Npc _npc;
        bool _open;
        float _startAt = -1f, _lastEsc = -10f;
        AvatarConfig _orig, _edit, _menuBackup;
        string _menu;   // "maha", "woha", "pilo"
        string _previewCode;

        // Auswahl
        int[] _types;                                   // Stile der Kategorie
        readonly Dictionary<int, List<ClotheInfo>> _byStyle = new Dictionary<int, List<ClotheInfo>>();
        int _typeIdx, _cutIdx, _color;

        Transform _holder;
        TDU2Avatar _avatar, _pending;
        AnmClip _clip;
        float _poseT;
        Vector3 _savedCamPos; Quaternion _savedCamRot; float _savedFov, _savedNear;
        Vector3 _camPos; Quaternion _camRot; float _camFov; bool _camInit;

        void Awake() { _inst = this; }
        void OnDestroy() { if (_inst == this) _inst = null; if (_open) Close(); if (_ui != null) Destroy(_ui.gameObject); }

        public bool Open(string frontendBank, TDU2InteriorNpcs npcs)
        {
            if (_open) return true;
            if (string.IsNullOrEmpty(frontendBank) || !File.Exists(frontendBank) || !AvatarLibrary.Ready) return false;
            _npcs = npcs;
            string bnk = Interiors != null && Interiors.Manager != null ? Interiors.Manager.BnkDir : null;
            Racing.RaceTexts.Ensure(bnk, TDU2PauseMenu.SavedLanguage());
            if (_ui == null)
            {
                try
                {
                    var go = new GameObject("TDU2 Hairdresser UI");
                    go.transform.SetParent(transform, false);
                    _ui = go.AddComponent<VmfPlayer>();
                    _ui.GuiDepth = -2;
                    _ui.Load(frontendBank);
                    _ui.FSCommand += OnCommand;
                    FlashLocalization.Apply(_ui, FlashLocalization.Load(bnk, TDU2PauseMenu.SavedLanguage()));
                }
                catch (Exception ex) { DebugManager.Error("Friseur: " + ex.Message); if (_ui != null) Destroy(_ui.gameObject); _ui = null; return false; }
            }
            _orig = AvatarConfigs.Local.Clone() ?? AvatarConfig.Default();
            _edit = _orig.Clone();
            _previewCode = null;
            _npc = npcs != null ? npcs.First("Hair") ?? npcs.First(null) : null;
            if (_npc != null) { var a = npcs.FindActor(_npc.Hc, "HEAD"); if (a != null) npcs.SetPose(_npc.ConfigName, a); }

            var cam = Camera.main;
            if (cam != null) { _savedCamPos = cam.transform.position; _savedCamRot = cam.transform.rotation; _savedFov = cam.fieldOfView; _savedNear = cam.nearClipPlane; cam.nearClipPlane = 0.03f; }
            Interiors?.SetFirstPerson(false);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
            PlaceAvatar();
            ShowPreview(true);
            _camInit = false;

            _open = true;
            _ui.gameObject.SetActive(true);
            _ui.MouseEnabled = true;
            _ui.SetValue("/:fake", false);
            _ui.SetValue("/:IsTutorial", 0);
            _ui.SetValue("/:WhatPlatform", 1);
            _ui.SetVariable("/:ClubMoney", " ");
            UpdateMoney();
            _ui.GotoLabel("/", "hairdresser", true);
            _startAt = Time.unscaledTime + 0.6f;
            return true;
        }

        public void Close()
        {
            if (!_open) return;
            _open = false; _startAt = -1f;
            if (_avatar != null) Destroy(_avatar.gameObject);
            if (_pending != null) Destroy(_pending.gameObject);
            _avatar = _pending = null;
            if (_holder != null) Destroy(_holder.gameObject);
            _holder = null;
            if (_npc != null && _npcs != null) _npcs.ResetPose(_npc);
            var cam = Camera.main;
            if (cam != null) { cam.transform.SetPositionAndRotation(_savedCamPos, _savedCamRot); cam.fieldOfView = _savedFov; if (_savedNear > 0f) cam.nearClipPlane = _savedNear; }
            if (_ui != null) _ui.gameObject.SetActive(false);
            Interiors?.SetFirstPerson(true);
            _closedFrame = Time.frameCount;
        }

        void StartMenu()
        {
            bool woman = _orig.Sex == 1;
            _ui.SetVariable("/:SelItemType", "hairdresser");
            _ui.SetValue("/:isWoman", woman);
            _ui.SetValue("/:LockHair", false);
            _ui.SetValue("/:LockPilosity", woman);
            _ui.SetValue("/:LockEyebrows", true);   // Augenbrauen-Varianten nicht nachgebaut
            _ui.SetValue("/:LockHairCancel", false);
            _ui.SetValue("/:LockHairValid", false);
            _ui.SetValue("/:Input", true);
            UpdatePrice();
            if (!_ui.CallClip("/", "openMenuHairDresser")) { _ui.GotoLabel("/", "hairdresser_edit", false); _ui.CallClip("/", "setSquareMenu"); }
        }

        // ================================================================== Auswahl
        static ClotheInfo Worn(AvatarConfig c, ClothePart p)
        {
            foreach (int h in c.Items) { var ci = AvatarLibrary.Clothe(h); if (ci != null && ci.Part == p) return ci; }
            return null;
        }

        void BuildLists(string menu)
        {
            _byStyle.Clear();
            bool beard = menu == "pilo";
            bool woman = _edit.Sex == 1;
            _types = beard ? new[] { BeardGoatee, BeardMoustache, BeardFull, 0 }
                           : woman ? new[] { StyleShort, StyleAttached, StyleAfro } : new[] { StyleShort, StyleLong, StyleAfro };
            foreach (int t in _types) _byStyle[t] = new List<ClotheInfo>();
            var part = beard ? ClothePart.Beard : ClothePart.Hair;
            foreach (var c in AvatarLibrary.AllClothes)
            {
                if (c.Part != part || !c.Valid || c.Brand == 73) continue;   // 73 = Figuren (PNJ)
                if (woman ? !c.ForWoman : !c.ForMan) continue;
                if (c.File.EndsWith("_HELMET", StringComparison.OrdinalIgnoreCase)) continue;
                int st = c.HairCutStyle;
                if (beard && c.IsNothing) st = 0;
                else if (c.IsNothing || !AvatarLibrary.HasBank(c)) continue;
                if (!_byStyle.ContainsKey(st)) { if (!beard && st != 0) _byStyle[StyleShort].Add(c); continue; }
                _byStyle[st].Add(c);
            }
            foreach (var l in _byStyle.Values) l.Sort((a, b) => a.SortIndex != b.SortIndex ? a.SortIndex.CompareTo(b.SortIndex) : string.CompareOrdinal(a.File, b.File));
            // Auswahl auf das Getragene
            var w = Worn(_edit, part);
            _typeIdx = 0; _cutIdx = 0; _color = w != null ? _edit.ColorOf(w.Hash) : 0;
            for (int i = 0; i < _types.Length; i++)
            {
                int k = w != null ? _byStyle[_types[i]].IndexOf(w) : -1;
                if (k >= 0) { _typeIdx = i; _cutIdx = k; break; }
            }
        }

        List<ClotheInfo> CurList => _types != null && _byStyle.TryGetValue(_types[_typeIdx], out var l) ? l : new List<ClotheInfo>();
        ClotheInfo CurCut { get { var l = CurList; return l.Count > 0 ? l[Mathf.Clamp(_cutIdx, 0, l.Count - 1)] : null; } }

        static string StyleName(int st, bool beard)
        {
            if (beard) return st == BeardGoatee ? "goatee" : st == BeardMoustache ? "moustache" : st == BeardFull ? "beard" : "shave";
            return st == StyleLong ? "long" : st == StyleAfro ? "afro" : st == StyleAttached ? "w_attached" : "short";
        }

        string CutName(ClotheInfo c)
        {
            if (c == null) return "–";
            string n = c.NameHc != 0 ? Racing.RaceTexts.Get((ulong)(uint)c.NameHc, null) : null;
            if (string.IsNullOrEmpty(n)) { var p = c.File.Split('_'); n = string.Join(" ", p, Math.Min(3, p.Length - 1), p.Length - Math.Min(3, p.Length - 1)); }
            return n;
        }

        void ApplySelection()
        {
            bool beard = _menu == "pilo";
            var part = beard ? ClothePart.Beard : ClothePart.Hair;
            var c = CurCut;
            for (int i = _edit.Items.Count - 1; i >= 0; i--)
                if (AvatarLibrary.Clothe(_edit.Items[i])?.Part == part) { _edit.Items.RemoveAt(i); if (i < _edit.Colors.Count) _edit.Colors.RemoveAt(i); }
            if (c != null && !c.IsNothing) _edit.Set(c, Mathf.Clamp(_color, 0, Mathf.Max(0, c.NbColors - 1)));
            UpdateTexts();
            UpdatePrice();
            ShowPreview(false);
        }

        void UpdateTexts()
        {
            if (_types == null) return;
            bool beard = _menu == "pilo";
            var c = CurCut;
            string type = StyleName(_types[_typeIdx], beard);
            string cut = CutName(c) + "  (" + (Mathf.Clamp(_cutIdx, 0, Math.Max(0, CurList.Count - 1)) + 1) + "/" + Math.Max(1, CurList.Count) + ")";
            int nc = c != null ? Math.Max(1, c.NbColors) : 1;
            string col = (Mathf.Clamp(_color, 0, nc - 1) + 1) + "/" + nc;
            if (beard) { _ui.SetVariable("/:PilosityType", type); _ui.SetVariable("/:PilosityCut", cut); _ui.SetVariable("/:PilosityColor", col); }
            else { _ui.SetVariable("/:HairType", type); _ui.SetVariable("/:HairCut", cut); _ui.SetVariable("/:HairColor", col); }
        }

        int Price()
        {
            int p = 0;
            foreach (var part in new[] { ClothePart.Hair, ClothePart.Beard })
            {
                var a = Worn(_orig, part); var b = Worn(_edit, part);
                if (a == b && (b == null || _orig.ColorOf(b.Hash) == _edit.ColorOf(b.Hash))) continue;
                if (b != null && b.Price < 999999) p += b.Price;
                else if (b == null) p += 100;
                else p += 300;
            }
            return p;
        }

        void UpdatePrice()
        {
            int p = Price();
            var sv = TDU2Session.Save;
            string s = Money(p);
            _ui.SetVariable("/:CurrentPrice", s);
            _ui.SetVariable("CurrentPrice", s);
            _ui.SetVariable("/:HairPrice", s);
            _ui.SetValue("/:EnoughMoney", sv == null || sv.money >= p ? 1 : 0);
        }

        void UpdateMoney()
        {
            var sv = TDU2Session.Save;
            _ui.SetVariable("/:MyMoneyStr", sv != null ? Money(sv.money) : " ");
        }

        static string Money(long v) => "$ " + v.ToString("N0", CultureInfo.GetCultureInfo("de-DE"));

        // ================================================================== Befehle
        void OnCommand(string cmd, string args)
        {
            if (!_open) return;
            string c = (cmd ?? "").Trim().ToLowerInvariant();
            if (TDU2FrontendSounds.Command(c)) return;
            if (c == "dbxo") { AnswerDialog(); return; }
            if (c == "evmo") { UpdatePrice(); return; }
            if (c == "moup") { UpdateMoney(); return; }
            if (c == "hadr:clos" || c == "gofr" || c == "wmon") { Close(); return; }
            if (c == "hadr:edit:vali") { Commit(); return; }
            if (c == "hadr:edit:canc") { _edit = _orig.Clone(); ShowPreview(false); return; }
            if (c == "hadr:open" || c == "hadr:edit:open" || c == "hadr:edit:chpa") return;
            if (!c.StartsWith("hadr:edit:")) { Debug.Log("[TDU2] Friseur: " + c); return; }
            var p = c.Substring(10).Split(':');   // maha:type:scne
            if (p.Length < 2) return;
            string menu = p[0];
            if (menu == "eybr") return;
            if (p[1] == "open") { _menu = menu; _menuBackup = _edit.Clone(); BuildLists(menu); UpdateTexts(); return; }
            if (_menu != menu || _types == null) { _menu = menu; _menuBackup = _edit.Clone(); BuildLists(menu); }
            if (p[1] == "vali") { _menuBackup = null; UpdatePrice(); return; }
            if (p[1] == "canc") { if (_menuBackup != null) _edit = _menuBackup; _menuBackup = null; UpdateTexts(); UpdatePrice(); ShowPreview(false); return; }
            int d = p.Length > 2 && p[2] == "scpr" ? -1 : 1;
            switch (p[1])
            {
                case "type":
                    _typeIdx = (_typeIdx + d + _types.Length) % _types.Length; _cutIdx = 0;
                    break;
                case "cuts":
                    { int n = Math.Max(1, CurList.Count); _cutIdx = (_cutIdx + d + n) % n; break; }
                case "colo":
                    { var cc = CurCut; int n = cc != null ? Math.Max(1, cc.NbColors) : 1; _color = (_color + d + n) % n; break; }
                default: return;
            }
            ApplySelection();
        }

        void Commit()
        {
            int price = Price();
            var sv = TDU2Session.Save;
            if (sv != null) sv.money = Math.Max(0, sv.money - price);
            _orig = _edit.Clone();
            AvatarConfigs.SetLocal(_orig);
            UpdateMoney();
            DebugManager.Info("Friseur: neue Frisur gespeichert, bezahlt $" + price);
        }

        void AnswerDialog()
        {
            string dc = _ui.GetVariable("/:DgbxCase") ?? "";
            _ui.SetVariable("/:PlayerAnswer", "a");
            var owners = _ui.FunctionOwners("DialogBox");
            string target = owners.Contains("/") ? "/" : owners.Count > 0 ? owners[owners.Count - 1] : "/";
            _ui.CallClip(target, "DialogBox");
        }

        // ================================================================== Avatar im Stuhl + Kamera
        CineScene.Obj Find(string name, string scene)
        {
            return _npcs != null ? _npcs.FindObject(name, scene) : null;
        }

        void PlaceAvatar()
        {
            var go = new GameObject("TDU2 Friseur-Avatar");
            _holder = go.transform;
            var scene = _npcs != null ? _npcs.Scene : null;
            var a = Find("Avatar", "HEAD");
            if (scene != null && a != null)
            {
                _holder.SetParent(scene, false);
                _holder.localPosition = a.Position; _holder.localRotation = a.Rotation;
            }
            else if (scene != null)
            {
                _holder.SetParent(scene, false);
                var cam = Camera.main;
                _holder.position = cam != null ? cam.transform.position + cam.transform.forward * 2f - Vector3.up * 1.6f : scene.position;
            }
            string clip = a != null && !string.IsNullOrEmpty(a.Clip) ? a.Clip : "hd_sitstand_loop-m";
            bool woman = _orig.Sex == 1;
            if (woman && clip.EndsWith("-m")) clip = clip.Substring(0, clip.Length - 2) + "-w";
            _clip = (_npcs != null ? _npcs.Clip(clip) : null) ?? AvatarLibrary.Clip(woman ? "co_stand-w" : "co_stand-m");
            _poseT = 0f;
        }

        void ShowPreview(bool force)
        {
            if (_holder == null) return;
            string code = _edit.Encode();
            if (!force && code == _previewCode) return;
            _previewCode = code;
            if (_pending != null) Destroy(_pending.gameObject);
            _pending = TDU2Avatar.Create(_holder, _edit.Clone(), "TDU2 Friseur-Anprobe");
            _pending.Built += av =>
            {
                if (av != _pending) return;
                if (_avatar != null) Destroy(_avatar.gameObject);
                _avatar = av; _pending = null;
                Pose(av);
            };
        }

        void Pose(TDU2Avatar a)
        {
            if (a == null || !a.Ready) return;
            a.BeginPose();
            if (_clip != null) a.ApplyClip(_clip, Mathf.Repeat(_poseT, Mathf.Max(0.05f, _clip.Length)), 1f, true);
            a.CommitPose();
        }

        void Update()
        {
            if (!_open || _ui == null) return;
            if (_startAt > 0f && Time.unscaledTime >= _startAt) { _startAt = -1f; StartMenu(); }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (TDU2GameData.ChatTyping) return;
            if (Input.GetKeyDown(KeyCode.UpArrow)) _ui.Input("Move", "up");
            if (Input.GetKeyDown(KeyCode.DownArrow)) _ui.Input("Move", "down");
            if (Input.GetKeyDown(KeyCode.LeftArrow)) _ui.Input("Move", "left");
            if (Input.GetKeyDown(KeyCode.RightArrow)) _ui.Input("Move", "right");
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space)) _ui.Input("A");
            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace))
            {
                if (Time.unscaledTime - _lastEsc < 1.2f) { _edit = _orig.Clone(); Close(); return; }
                _lastEsc = Time.unscaledTime;
                if (!_ui.Input("B")) Close();
            }
#endif
        }

        void LateUpdate()
        {
            if (!_open) return;
            _poseT += Time.deltaTime;
            if (_avatar != null) Pose(_avatar);
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 pos; Quaternion rot; float fov = 22f;
            var c = Find("C_HEAD", null);
            if (c != null && _npcs != null && _npcs.Scene != null) { CineScene.ToWorld(_npcs.Scene, c, out pos, out rot); fov = Mathf.Clamp(c.Fov, 10f, 90f); }
            else
            {
                Vector3 look = _holder != null ? _holder.position + Vector3.up * 1.2f : cam.transform.position;
                if (_avatar != null && _avatar.Ready && _avatar.Bones != null) { int h = _avatar.Bone("HEAD"); if (h >= 0) look = _avatar.Bones[h].position; }
                pos = look + (_savedCamPos - look).normalized * 1.1f;
                rot = Quaternion.LookRotation(look - pos, Vector3.up);
                fov = 35f;
            }
            if (!_camInit) { _camPos = cam.transform.position; _camRot = cam.transform.rotation; _camFov = cam.fieldOfView; _camInit = true; }
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 4f);
            _camPos = Vector3.Lerp(_camPos, pos, k); _camRot = Quaternion.Slerp(_camRot, rot, k); _camFov = Mathf.Lerp(_camFov, fov, k);
            cam.transform.SetPositionAndRotation(_camPos, _camRot);
            cam.fieldOfView = _camFov;
        }
    }
}