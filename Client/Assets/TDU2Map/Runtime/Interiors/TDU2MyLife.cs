using System;
using System.Collections.Generic;
using System.Globalization;
using TDU2.Map.Avatars;
using TDU2.Map.Online;
using TDU2.Map.Racing;
using UnityEngine;

namespace TDU2.Map.Vehicles
{
    /// <summary>
    /// MyTDULife (Tagebuch -> "My Life", home.vmf Clip /mylf) wie im Spiel:
    ///  * Laden: Film "load" schickt mylf:wait und wartet auf _root.myLifeVarLoaded; "open" schickt mylf:open und liest dann
    ///    _root-Variablen (WhatMood, MoodTxt, ed0..ed8, Ed*-Profilangaben, Fortschritt, Besitz) – hier aus Save.myLife.
    ///  * Bearbeiten (X): Kästen tauschen (Film schreibt _root.ed1..ed7, meldet nlis/cbou), Profilangaben per Liste
    ///    (scin -> ListId/VarToFocus, scne/scpr = nächster/voriger Eintrag, edva übernehmen, edca verwerfen), Geburtsdatum
    ///    (ListId day/month/year), Geschlecht (gend), Stimmung (mdva/mood), Stimmungstext über die Bildschirmtastatur /mylf/vk
    ///    (tfoc = Texteingabe in _root.TextToFocus, txck = Prüfung -> _root.mylfTextCorrect, txva = übernehmen).
    ///  * Foto (Y): /mylf/photo_taker – mylf:phot:open stellt Avatar und Kamera der Szene "M_MYLIFE" (Objekte "Avatar",
    ///    "c_mylife") auf, lock friert ein, vali speichert das Bild (JPG, Base64 in Save.myLife.photo) und meldet
    ///    _root.photoUploaded, canc bricht ab.
    ///  * Verlassen mit Änderungen: mylf:edit:upld -> speichern -> _root.MyLifeUploaded.
    /// Andere Spieler sehen Stimmung/Text über die Spielerliste und das Foto auf der Spielerkarte (op "profile").
    /// </summary>
    public sealed partial class TDU2Interiors
    {
        sealed class LifeList { public string Id, Var; public ulong[] Hc; }

        static ulong[] HcRange(ulong a, ulong b) { var r = new ulong[b - a + 1]; for (ulong i = a; i <= b; i++) r[i - a] = i; return r; }

        // Einträge der Auswahllisten (Texte aus db_<sprache>.cpr)
        static readonly LifeList[] LifeLists =
        {
            new LifeList { Id = "attitude", Var = "EdAttitude", Hc = HcRange(3490, 3496) },
            new LifeList { Id = "driving_camera", Var = "EdDrivingCamera", Hc = HcRange(3497, 3500) },
            new LifeList { Id = "driving_help", Var = "EdDrivingHelp", Hc = HcRange(3503, 3505) },
            new LifeList { Id = "gamer_type", Var = "EdGamerType", Hc = HcRange(3525, 3532) },
            new LifeList { Id = "game_genre", Var = "EdGameGenre", Hc = HcRange(3533, 3544) },
            new LifeList { Id = "gamer_kind", Var = "EdGamerKind", Hc = HcRange(3545, 3547) },
            new LifeList { Id = "like_tdu", Var = "EdLikeTDU", Hc = HcRange(3506, 3509) },
            new LifeList { Id = "fav_game_mode", Var = "EdFavGameMode", Hc = new ulong[] { 3510, 3511, 3512, 3513, 3514, 3516 } },
            new LifeList { Id = "fav_island", Var = "EdFavIsland", Hc = new ulong[] { 3519, 3520 } },
            new LifeList { Id = "fav_car_category", Var = "EdFavCarCategory", Hc = HcRange(3522, 3524) },
            new LifeList { Id = "play_frequency", Var = "EdPlayFrequency", Hc = HcRange(3486, 3489) },
        };

        static readonly string[] DefaultLifeBoxes = { "driver_profile", "gamer_profile", "racing", "cruising", "collection", "social", "vehicles" };
        const int BirthYearMin = 1940, BirthYearMax = 2012;

        static MyLifeData Life
        {
            get
            {
                var sv = TDU2Session.Save;
                if (sv == null) return null;
                if (sv.myLife == null) sv.myLife = new MyLifeData();
                if (sv.myLife.boxes == null) sv.myLife.boxes = new List<string>();
                if (sv.myLife.fields == null) sv.myLife.fields = new List<HouseEntry>();
                return sv.myLife;
            }
        }

        bool _lifeDirty;
        string _lifeSnap;           // Stand vor der laufenden Bearbeitung (edca stellt ihn wieder her)

        static int LifeField(MyLifeData l, string id)
        {
            if (l != null) foreach (var e in l.fields) if (e.k == id && int.TryParse(e.v, out int v)) return v;
            return -1;
        }

        static void SetLifeField(MyLifeData l, string id, int v)
        {
            if (l == null) return;
            foreach (var e in l.fields) if (e.k == id) { e.v = v.ToString(CultureInfo.InvariantCulture); return; }
            l.fields.Add(new HouseEntry { k = id, v = v.ToString(CultureInfo.InvariantCulture) });
        }

        static string LifeText(LifeList l, int idx) => idx >= 0 && idx < l.Hc.Length ? RaceTexts.Get(l.Hc[idx], "?") : "-";
        static string MonthText(int m) => m >= 1 && m <= 12 ? RaceTexts.Get(3549 + (ulong)m, m.ToString("00")) : "--";

        // ------------------------------------------------------------------ Werte an den Film
        void PushMyLifeVars()
        {
            if (_ui == null) return;
            var l = Life;
            var sv = TDU2Session.Save;
            var de = CultureInfo.GetCultureInfo("de-DE");
            _ui.SetValue("/:LockMyLifeEdition", false);
            _ui.SetValue("/:AmericanDateFormat", false);
            _ui.SetValue("/:IsCasino", false);
            _ui.SetValue("/:HasCasino", false);
            _ui.SetValue("/:isCasinoStandalone", false);
            if (!string.IsNullOrEmpty(TDU2Session.Nick)) _ui.SetVariable("/:AvatarName", TDU2Session.Nick);
            _ui.SetValue("/:NumTitle", 1);
            // Besitz
            int cars = sv?.ownedCars?.Count ?? 0;
            _ui.SetVariable("/:Money", (sv != null ? sv.money : 0).ToString("N0", de) + " $");
            _ui.SetVariable("/:NbCars", cars.ToString());
            _ui.SetVariable("/:NbHouses", Math.Max(1, sv?.houses?.Count ?? 0).ToString());
            // Fortschritt (noch ohne Level-System: Anfangswerte)
            foreach (var g in new[] { "Racing", "Social", "Cruising", "Collection" })
            {
                _ui.SetValue("/:" + g + "Points", 0);
                _ui.SetValue("/:" + g + "LvlPoints", 0);
                _ui.SetValue("/:Is" + g + "GaugeFull", false);
            }
            _ui.SetValue("/:GlobalLevel", 1);
            _ui.SetValue("/:GlobalPoints", 0);
            _ui.SetValue("/:GlobalLvlPoints", 0);
            _ui.SetVariable("/:GlobalLevelCount", "1");
            _ui.SetValue("/:HasClub", false);
            _ui.SetValue("/:ClubLevel", 0);
            _ui.SetVariable("/:ClubName", " ");
            _ui.SetVariable("/:NbClubMembers", "--");
            _ui.SetVariable("/:NbClubCars", "--");
            // Kästen mit Zahlen
            double km = 0, bestKm = 0; int bestCar = 0;
            if (sv?.odometers != null) foreach (var o in sv.odometers) { km += o.meters / 1000.0; if (o.meters / 1000.0 > bestKm) { bestKm = o.meters / 1000.0; bestCar = o.carId; } }
            _ui.SetVariable("/:DrivenDistance", km.ToString("N0", de) + " km");
            _ui.SetVariable("/:DrivenDistanceFR", km.ToString("N0", de) + " km");
            _ui.SetVariable("/:MostUsedCarKm", bestKm.ToString("N0", de) + " km");
            _ui.SetVariable("/:NbVehOwned", cars.ToString());
            _ui.SetVariable("/:NbVehiclesBought", cars.ToString());
            _ui.SetVariable("/:NbClothesBought", (sv?.ownedClothes?.Count ?? 0).ToString());
            _ui.SetVariable("/:NbFurnituresBought", (sv?.ownedFurniture?.Count ?? 0).ToString());
            _ui.SetVariable("/:NbStickersBought", (sv?.stickerBoards?.Count ?? 0).ToString());
            _ui.SetVariable("/:EdCarSticked", (sv?.liveries?.Count ?? 0).ToString());
            _ui.SetVariable("/:NbHousesTotal", Math.Max(1, sv?.houses?.Count ?? 0).ToString());
            foreach (var z in new[] { "NbCarUpgraded", "NbEventsWon", "NbWrecksFound", "NbPhotosIDone", "NbPhotosHDone", "NbFriends", "NbFriendsInvited",
                                      "NbInstantWon", "NbInstantTotal", "NbMultiWon", "NbMultiTotal", "NbCoopPlayed", "NbChasePlayed", "NbSurgeriesBought" })
                _ui.SetVariable("/:" + z, "0");
            foreach (var z in new[] { "PlayTime", "GameCompletion", "UnlockedRoadsPercent", "LongestCruise", "BestJump", "BestDrift", "BestDodgeChain", "BestFrogSession" })
                _ui.SetVariable("/:" + z, "--");
            if (l == null) return;
            // Stimmung + Kästen
            string mood = string.IsNullOrEmpty(l.mood) ? "neutral" : l.mood;
            _ui.SetVariable("/:WhatMood", mood);
            _ui.SetVariable("/:MoodTxt", l.moodText ?? "");
            _ui.SetVariable("/:MoodTxtInput", l.moodText ?? "");
            _ui.SetVariable("/:ed0", "mood");
            for (int i = 1; i <= 7; i++)
                _ui.SetVariable("/:ed" + i, i - 1 < l.boxes.Count && !string.IsNullOrEmpty(l.boxes[i - 1]) ? l.boxes[i - 1] : DefaultLifeBoxes[i - 1]);
            _ui.SetVariable("/:ed8", "empty");
            _ui.SetValue("/:NbEdBoxes", 8);
            // Profilangaben
            foreach (var li in LifeLists) _ui.SetVariable("/:" + li.Var, LifeText(li, LifeField(l, li.Id)));
            PushBirth(l);
            string gender = !string.IsNullOrEmpty(l.gender) ? l.gender : (AvatarConfigs.Local != null && AvatarConfigs.Local.Sex == 1 ? "female" : "male");
            _ui.SetVariable("/:EdPlayerGender", gender);
            _ui.SetVariable("/:EdMail", "");
        }

        void PushBirth(MyLifeData l)
        {
            _ui.SetVariable("/:EdBirthDay", l.birthDay > 0 ? l.birthDay.ToString("00") : "--");
            _ui.SetVariable("/:EdBirthMonth", MonthText(l.birthMonth));
            _ui.SetVariable("/:EdBirthYear", l.birthYear > 0 ? l.birthYear.ToString() : "----");
            _ui.SetVariable("/:PlayerAge", l.birthDay > 0 && l.birthMonth > 0 && l.birthYear > 0
                ? l.birthDay.ToString("00") + " " + MonthText(l.birthMonth) + " " + l.birthYear : "-");
        }

        Texture2D _myPhotoTex; int _myPhotoVer = -1;

        /// <summary>Eigenes Avatar-Foto in den Namenskasten (/mylf/pbox/name_box/avatar_photo/lure).</summary>
        void ApplyLifePhoto()
        {
            var l = Life;
            if (_ui == null || l == null) return;
            if (_myPhotoVer != l.photoVer || (_myPhotoTex == null && !string.IsNullOrEmpty(l.photo)))
            {
                if (_myPhotoTex != null) Destroy(_myPhotoTex);
                _myPhotoTex = TDU2GameData.DecodePhoto(l.photo);
                _myPhotoVer = l.photoVer;
            }
            _ui.SetClipTexture("/mylf/pbox/name_box/avatar_photo/lure", _myPhotoTex);
        }

        void SaveLifeIfDirty()
        {
            if (!_lifeDirty) return;
            _lifeDirty = false;
            ReadLifeBoxes();
            TDU2Session.SaveAsync();
            Debug.Log("[TDU2] MyTDULife gespeichert");
        }

        /// <summary>Kästen ed1..ed7 aus dem Film (der Film tauscht/verschiebt sie selbst).</summary>
        void ReadLifeBoxes()
        {
            var l = Life;
            if (_ui == null || l == null) return;
            var list = new List<string>();
            for (int i = 1; i <= 7; i++)
            {
                string b = _ui.GetVariable("/:ed" + i);
                list.Add(string.IsNullOrEmpty(b) ? "empty" : b);
            }
            l.boxes = list;
        }

        void MarkLifeEdited()
        {
            _lifeDirty = true;
            _ui.SetValue("/:HasEditedMyLife", true);
        }

        // ------------------------------------------------------------------ Befehle des Films (mylf:*)
        bool OnMyLifeCommand(string c)
        {
            var l = Life;
            switch (c)
            {
                case "mylf:self": return true;
                case "mylf:wait":   // Laden: Werte setzen, dann darf "open" sie lesen
                    PushMyLifeVars();
                    _ui.SetValue("/:myLifeVarLoaded", true);
                    return true;
                case "mylf:open": PushMyLifeVars(); return true;
                case "mylf:bxok": ApplyLifePhoto(); return true;
                case "mylf:clos": EndLifeModes(); SaveLifeIfDirty(); return true;
                case "mylf:sgcx": return true;
                // Bearbeiten
                case "mylf:edit:edin": _lifeSnap = null; return true;
                case "mylf:edit:edou": SaveLifeIfDirty(); return true;
                case "mylf:edit:scin": if (_lifeSnap == null && l != null) _lifeSnap = JsonUtility.ToJson(l); return true;
                case "mylf:edit:scne": LifeScroll(+1); return true;
                case "mylf:edit:scpr": LifeScroll(-1); return true;
                case "mylf:edit:edva": _lifeSnap = null; MarkLifeEdited(); return true;
                case "mylf:edit:edca": RestoreLifeSnapshot(); return true;
                case "mylf:edit:gend":
                    if (l != null) { l.gender = _ui.GetVariable("/:EdPlayerGender") ?? ""; MarkLifeEdited(); }
                    return true;
                case "mylf:edit:mdva":
                case "mylf:edit:mood":
                    if (l != null) { l.mood = _ui.GetVariable("/:WhatMood") ?? "neutral"; MarkLifeEdited(); }
                    return true;
                case "mylf:edit:txin": return true;   // die Eingabe beginnt mit "tfoc" (Tastatur offen, TextToFocus gesetzt)
                case "mylf:edit:txca": _textVar = null; return true;
                case "mylf:edit:txck":   // Text prüfen (im Spiel Server-Filter): immer in Ordnung
                    _ui.SetValue("/:TextAbuse", false);
                    _ui.SetValue("/:TimeOut", false);
                    _ui.SetValue("/:mylfTextCorrect", true);
                    KickLifeLoader();
                    return true;
                case "mylf:edit:txva":
                    if (l != null)
                    {
                        string t = _ui.GetVariable("/:VK_Value");
                        if (string.IsNullOrEmpty(t)) t = _ui.GetVariable("/:MoodTxtInput") ?? "";
                        if (t.Length > 50) t = t.Substring(0, 50);
                        l.moodText = t;
                        _ui.SetVariable("/:MoodTxt", t);
                        _ui.SetVariable("/:MoodTxtInput", t);
                        MarkLifeEdited();
                    }
                    return true;
                case "mylf:edit:nlis":
                case "mylf:edit:cbou":
                case "mylf:edit:valid":
                    ReadLifeBoxes(); MarkLifeEdited(); return true;
                case "mylf:edit:numb": case "mylf:edit:nlist": case "mylf:edit:cbin": case "mylf:edit:cancel": return true;
                case "mylf:edit:upld":   // Verlassen nach Änderungen: speichern, dann darf der Film schließen
                    _lifeDirty = true; SaveLifeIfDirty();
                    _ui.SetValue("/:MyLifeUploaded", true);
                    KickLifeLoader();
                    return true;
                // Fotoalbum-Kästen (photo0..6) – ohne Fotoalbum nichts zu tun
                case "mylf:edit:phop":
                case "mylf:edit:phne":
                case "mylf:edit:phpr":
                case "mylf:edit:phva":
                case "mylf:edit:phca":
                case "mylf:edit:phot":
                    return true;
                // Avatar-Foto
                case "mylf:phot:open":
                    try { StartLifePhoto(); }
                    catch (Exception ex) { Debug.LogException(ex); EndLifePhoto(); }
                    return true;
                case "mylf:phot:lock": TakeLifePhoto(); return true;   // Auslöser (auch per Maus): Foto machen und Sucher schließen
                case "mylf:phot:vali": CaptureLifePhoto(); return true;
                case "mylf:phot:canc": EndLifePhoto(); return true;
                // Texteingabe (PC-Tastatur /mylf/vk bzw. Textfeld des Bearbeiten-Fensters)
                case "tfoc": BeginTextInput(); return true;
                case "vtke": return true;
            }
            return c.StartsWith("mylf:");   // mylf:scrl:* (Bildlaufleiste) u.a.
        }

        void LifeScroll(int dir)
        {
            var l = Life;
            if (l == null || _ui == null) return;
            if (_lifeSnap == null) _lifeSnap = JsonUtility.ToJson(l);
            string id = _ui.GetVariable("/:ListId") ?? "";
            switch (id)
            {
                case "day":
                    l.birthDay = l.birthDay <= 0 ? 1 : (l.birthDay - 1 + dir + 31) % 31 + 1;
                    PushBirth(l); return;
                case "month":
                    l.birthMonth = l.birthMonth <= 0 ? 1 : (l.birthMonth - 1 + dir + 12) % 12 + 1;
                    PushBirth(l); return;
                case "year":
                    int n = BirthYearMax - BirthYearMin + 1;
                    l.birthYear = l.birthYear <= 0 ? 1990 : (l.birthYear - BirthYearMin + dir + n) % n + BirthYearMin;
                    PushBirth(l); return;
            }
            foreach (var li in LifeLists)
            {
                if (li.Id != id) continue;
                int cur = LifeField(l, li.Id), cnt = li.Hc.Length;
                int next = cur < 0 ? (dir > 0 ? 0 : cnt - 1) : (cur + dir + cnt) % cnt;
                SetLifeField(l, li.Id, next);
                _ui.SetVariable("/:" + li.Var, LifeText(li, next));
                return;
            }
            Debug.Log("[TDU2] MyTDULife: unbekannte Liste '" + id + "'");
        }

        void RestoreLifeSnapshot()
        {
            var sv = TDU2Session.Save;
            if (_lifeSnap != null && sv != null)
            {
                var old = JsonUtility.FromJson<MyLifeData>(_lifeSnap);
                if (old != null)
                {
                    sv.myLife = old;
                    var l = Life;
                    foreach (var li in LifeLists) _ui.SetVariable("/:" + li.Var, LifeText(li, LifeField(l, li.Id)));
                    PushBirth(l);
                }
            }
            _lifeSnap = null;
        }

        // ------------------------------------------------------------------ Texteingabe (tfoc)
        string _textVar; int _textMax = 50; float _textAt;

        void BeginTextInput()
        {
            string v = _ui.GetVariable("/:TextToFocus");
            if (string.IsNullOrEmpty(v)) return;
            if (!v.StartsWith("/:") && !v.StartsWith("_root.")) v = "/:" + v.TrimStart('/', ':');
            if (v.StartsWith("_root.")) v = "/:" + v.Substring(6);
            _textVar = v;
            _textMax = ReadLifeInt("/:VK_MaxLength", ReadLifeInt("/:MaxLenghtInput", 50));
            if (_textMax <= 0 || _textMax > 200) _textMax = 50;
            _textAt = Time.unscaledTime;
        }

        int ReadLifeInt(string v, int def)
        {
            string s = _ui.GetVariable(v);
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (int)d : def;
        }

        bool TextInputVisible()
        {
            // Tastatur /mylf/vk: Bild 0 = zu; die Öffnen-Animation (Bild 1..33) hat kein eigenes Label
            var vk = _ui.Find("/mylf/vk")?.Sprite;
            if (vk != null && _ui.IsShown(vk) && _ui.Frame("/mylf/vk") > 1 && _ui.Label("/mylf/vk") != "close") return true;
            string pl = _ui.Label("/mylf/edbox/edit_popup") ?? "";
            return pl.StartsWith("open_txt");
        }

        /// <summary>Tastatur in das fokussierte Textfeld; true = Eingabe verbraucht.</summary>
        bool UpdateTextInput()
        {
            if (_textVar == null) return false;
            if (Time.unscaledTime - _textAt > 0.5f && !TextInputVisible()) { _textVar = null; return false; }
#if ENABLE_LEGACY_INPUT_MANAGER
            string cur = _ui.GetVariable(_textVar) ?? "";
            bool changed = false;
            foreach (char ch in Input.inputString)
            {
                if (ch == '\b') { if (cur.Length > 0) { cur = cur.Substring(0, cur.Length - 1); changed = true; } }
                else if (ch == '\n' || ch == '\r') { }
                else if (!char.IsControl(ch) && cur.Length < _textMax) { cur += ch; changed = true; }
            }
            if (changed) _ui.SetVariable(_textVar, cur);
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) { _textVar = null; _ui.Input("A"); }
            // ESC: zentraler Menüablauf (TDU2MenuFlow.cs) bricht die Eingabe ab
#endif
            return true;
        }

        // ------------------------------------------------------------------ Avatar-Foto
        GameObject _lifePhotoGo;
        TDU2Avatar _lifeAvatar;
        Camera _lifeCam;
        RenderTexture _lifeRt;
        AnmClip _lifeClip;
        float _lifePoseT, _lifePhotoAt;
        bool _lifePhoto, _lifeFrozen, _fpWasOn;
        Vector3 _lifeCamPos; Quaternion _lifeCamRot; float _lifeCamFov;

        void StartLifePhoto()
        {
            if (_lifePhoto) { FreezeLifePhoto(false); return; }   // neues Foto nach "Nein"
            var cam = Camera.main;
            if (cam == null) return;
            _lifePhoto = true; _lifeFrozen = false; _lifePhotoAt = Time.unscaledTime;
            _lifeCamPos = cam.transform.position; _lifeCamRot = cam.transform.rotation; _lifeCamFov = cam.fieldOfView;
            _fpWasOn = _fp != null && _fp.enabled;
            if (_fp != null) _fp.enabled = false;   // die Kamera gehört jetzt dem Foto

            var npcs = Npcs;
            var scene = npcs != null ? npcs.Scene : null;
            var act = npcs != null ? npcs.FindObject("Avatar", "MYLIFE") : null;
            var cobj = npcs != null ? npcs.FindObject("c_mylife", "MYLIFE") : null;
            _lifePhotoGo = new GameObject("TDU2 MyTDULife Foto");
            var holder = _lifePhotoGo.transform;
            if (scene != null && act != null && act.HasPosition)
            {
                holder.SetParent(scene, false);
                holder.localPosition = act.Position; holder.localRotation = act.Rotation;
            }
            else
            {
                // Ersatz: 2,3 m vor der Kamera auf dem Boden, Blick zur Kamera
                Vector3 fwd = cam.transform.forward; fwd.y = 0f; if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward; fwd.Normalize();
                Vector3 p = _lifeCamPos + fwd * 2.3f;
                float floor = _lifeCamPos.y - 1.65f;
                if (UnityEngine.Physics.Raycast(p + Vector3.up * 0.5f, Vector3.down, out var hit, 4f, ~(1 << 2), QueryTriggerInteraction.Ignore)) floor = hit.point.y;
                p.y = floor;
                if (scene != null)
                {
                    holder.SetParent(scene, false);
                    holder.position = p;
                    var f = scene.InverseTransformVector(-fwd); f.y = 0f;
                    holder.localRotation = f.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(f.normalized, Vector3.up) : Quaternion.identity;
                }
                else { holder.position = p; holder.rotation = Quaternion.LookRotation(-fwd, Vector3.up); holder.localScale = new Vector3(-1f, 1f, 1f); }
            }

            // Kamera der Szene (c_mylife), sonst halbnah von vorn
            Vector3 cpos; Quaternion crot; float fov;
            if (scene != null && cobj != null && cobj.HasPosition)
            {
                CineScene.ToWorld(scene, cobj, out cpos, out crot);
                fov = Mathf.Clamp(cobj.Fov, 15f, 70f);
            }
            else
            {
                Vector3 look = holder.position + Vector3.up * 1.45f;
                Vector3 front = _lifeCamPos - holder.position; front.y = 0f;
                if (front.sqrMagnitude < 1e-4f) front = Vector3.forward;
                cpos = look + front.normalized * 1.6f + Vector3.up * 0.05f;
                crot = Quaternion.LookRotation(look - cpos, Vector3.up);
                fov = 35f;
            }
            cam.transform.SetPositionAndRotation(cpos, crot);
            cam.fieldOfView = fov;

            // Fotokamera (quadratisch) -> Textur für den Sucher (/mylf/photo_taker/lure) und das gespeicherte Bild
            _lifeRt = new RenderTexture(512, 512, 24, RenderTextureFormat.ARGB32) { name = "MyTDULife Sucher" };
            var cgo = new GameObject("TDU2 MyTDULife Kamera");
            cgo.transform.SetParent(holder.parent, true);
            cgo.transform.SetPositionAndRotation(cpos, crot);
            _lifeCam = cgo.AddComponent<Camera>();
            _lifeCam.CopyFrom(cam);
            _lifeCam.targetTexture = _lifeRt;
            _lifeCam.aspect = 1f;
            _lifeCam.fieldOfView = fov;
            _lifeCam.depth = cam.depth - 1f;
            _lifeShowRt = new RenderTexture(512, 512, 0, RenderTextureFormat.ARGB32) { name = "MyTDULife Sucher (Anzeige)" };
            _ui.SetClipTexture("/mylf/photo_taker/lure", _lifeShowRt);
            // Tagebuch (Buch/Menü links), Titelleiste und Geld ausblenden: nur der Sucher
            foreach (var n in LifePhotoHide) _ui.SetVisible("/" + n, false);
            // Umsicht um den Avatar (Maus ziehen, Pfeile/WASD, Mausrad)
            _orbitPivot = holder.position + Vector3.up * 1.35f;
            var off = cpos - _orbitPivot;
            _orbitDist = Mathf.Clamp(off.magnitude, 0.8f, 6f);
            _orbitYaw = Mathf.Atan2(off.x, off.z) * Mathf.Rad2Deg;
            _orbitPitch = Mathf.Asin(Mathf.Clamp(off.y / Mathf.Max(0.01f, off.magnitude), -1f, 1f)) * Mathf.Rad2Deg;
            _orbitFov = fov;

            // Avatar mit der Pose der Szene
            bool woman = AvatarConfigs.Local != null && AvatarConfigs.Local.Sex == 1;
            string clip = act?.Clip;
            if (!string.IsNullOrEmpty(clip))
            {
                if (woman && clip.EndsWith("-m")) clip = clip.Substring(0, clip.Length - 2) + "-w";
                else if (!woman && clip.EndsWith("-w")) clip = clip.Substring(0, clip.Length - 2) + "-m";
            }
            _lifeClip = (!string.IsNullOrEmpty(clip) ? (npcs != null ? npcs.Clip(clip) : AvatarLibrary.Clip(clip)) : null)
                        ?? AvatarLibrary.Clip(woman ? "co_stand-w" : "co_stand-m");
            _lifePoseT = 0f;
            if (AvatarLibrary.Ready)
            {
                _lifeAvatar = TDU2Avatar.Create(holder, AvatarConfigs.Local, "TDU2 MyTDULife Avatar");
                _lifeAvatar.InCar = false;
                _lifeAvatar.Built += PoseLifeAvatar;
            }
            Debug.Log("[TDU2] MyTDULife Foto: Szene " + (act != null ? "M_MYLIFE" : "Ersatz") + ", Pose " + (_lifeClip != null ? clip : "keine"));
        }

        static readonly string[] LifePhotoHide = { "diary", "title_bar", "club_money", "mylf_book", "help" };
        Vector3 _orbitPivot; float _orbitYaw, _orbitPitch, _orbitDist, _orbitFov;
        RenderTexture _lifeShowRt;   // Sucherbild, gekippt wie die Spieltexturen

        void ApplyLifeOrbit()
        {
            var rot = Quaternion.Euler(-_orbitPitch, _orbitYaw, 0f);
            var pos = _orbitPivot + rot * new Vector3(0f, 0f, 1f) * _orbitDist;
            var look = Quaternion.LookRotation(_orbitPivot - pos, Vector3.up);
            var cam = Camera.main;
            if (cam != null) { cam.transform.SetPositionAndRotation(pos, look); cam.fieldOfView = _orbitFov; }
            if (_lifeCam != null) { _lifeCam.transform.SetPositionAndRotation(pos, look); _lifeCam.fieldOfView = _orbitFov; }
        }

        /// <summary>Auslöser: Blitz, Bild speichern, Sucher schließen (zurück zu MyTDULife).</summary>
        void TakeLifePhoto()
        {
            if (!_lifePhoto) return;
            _ui.GotoLabel("/mylf/photoFlash", "flash", true);
            CaptureLifePhoto();
            CloseLifePhotoTaker();
        }

        /// <summary>Sucher schließen wie im Film (photo_taker "close": MyTDULife wieder öffnen).</summary>
        void CloseLifePhotoTaker()
        {
            EndLifePhoto();
            _ui.GotoLabel("/mylf/photo_taker", "close", true);
            _ui.SetValue("/:LockMyLifeEdition", false);
            _ui.SetValue("/:Input", true);
        }

        void PoseLifeAvatar(TDU2Avatar a)
        {
            if (a == null || !a.Ready) return;
            a.transform.localPosition = Vector3.zero;
            a.transform.localRotation = Quaternion.identity;
            a.BeginPose();
            if (_lifeClip != null) a.ApplyClip(_lifeClip, Mathf.Repeat(_lifePoseT, _lifeClip.Length > 0.01f ? _lifeClip.Length : 1f), 1f, true);
            a.CommitPose();
        }

        void FreezeLifePhoto(bool frozen)
        {
            _lifeFrozen = frozen;
            if (_lifeCam != null) _lifeCam.enabled = !frozen;
        }

        void CaptureLifePhoto()
        {
            var l = Life;
            if (_lifeRt != null && l != null)
            {
                if (_lifeCam != null && !_lifeFrozen) _lifeCam.Render();
                var small = RenderTexture.GetTemporary(256, 256, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(_lifeRt, small);
                var prev = RenderTexture.active;
                RenderTexture.active = small;
                var tex = new Texture2D(256, 256, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 256, 256), 0, 0);
                // Zeilen umdrehen: die Flash-Darstellung (SetClipTexture) erwartet Bilder von oben nach unten wie die Spieltexturen
                var px = tex.GetPixels32(); var fl = new Color32[px.Length];
                for (int y = 0; y < 256; y++) Array.Copy(px, y * 256, fl, (255 - y) * 256, 256);
                tex.SetPixels32(fl);
                tex.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(small);
                byte[] jpg = tex.EncodeToJPG(85);
                Destroy(tex);
                l.photo = Convert.ToBase64String(jpg);
                l.photoVer++;
                _lifeDirty = true;
                SaveLifeIfDirty();
                ApplyLifePhoto();
                Debug.Log("[TDU2] MyTDULife Foto gespeichert (" + jpg.Length / 1024 + " KB)");
            }
            FreezeLifePhoto(true);
            _ui.SetValue("/:photoUploaded", true);
        }

        /// <summary>Foto-Modus: Pose weiterspielen, Ende erkennen (Sucher geschlossen); true = Eingabe verbraucht.</summary>
        bool UpdateLifePhoto()
        {
            if (!_lifePhoto) return false;
            if (!_lifeFrozen) { _lifePoseT += Time.unscaledDeltaTime; PoseLifeAvatar(_lifeAvatar); }
            if (!_lifeFrozen && _lifeRt != null && _lifeShowRt != null) Graphics.Blit(_lifeRt, _lifeShowRt, new Vector2(1f, -1f), new Vector2(0f, 1f));
            string pl = _ui.Label("/mylf/photo_taker") ?? "default";
            if (Time.unscaledTime - _lifePhotoAt > 1f && (pl == "default" || pl == "close")) { EndLifePhoto(); return false; }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (Cursor.lockState != CursorLockMode.None || !Cursor.visible) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            if (Time.unscaledTime - _lifePhotoAt < 0.3f) return true;   // Taste, mit der geöffnet wurde
            // Auslösen / Abbrechen direkt (der Film-Dialog "Dieses Foto aufnehmen?" hing ohne Rückmeldung)
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space)) { TakeLifePhoto(); return true; }
            // ESC/Rücktaste: zentraler Menüablauf (TDU2MenuFlow.cs) schließt den Sucher
            float dt = Time.unscaledDeltaTime;
            // Kamera um den Avatar: Maus ziehen (links/rechts gedrückt), Pfeile/WASD drehen, Mausrad/Bild-Tasten zoomen, Q/E Avatar drehen
            if (Input.GetMouseButton(0) || Input.GetMouseButton(1))
            {
                _orbitYaw += Input.GetAxis("Mouse X") * 4f;
                _orbitPitch = Mathf.Clamp(_orbitPitch - Input.GetAxis("Mouse Y") * 3f, -30f, 60f);
            }
            float h = (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A) ? 1f : 0f);
            float v = (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S) ? 1f : 0f);
            _orbitYaw += h * 70f * dt;
            _orbitPitch = Mathf.Clamp(_orbitPitch + v * 40f * dt, -30f, 60f);
            float zoom = -Input.mouseScrollDelta.y * 0.25f + ((Input.GetKey(KeyCode.PageDown) || Input.GetKey(KeyCode.KeypadMinus) ? 1f : 0f) - (Input.GetKey(KeyCode.PageUp) || Input.GetKey(KeyCode.KeypadPlus) ? 1f : 0f)) * 1.5f * dt;
            _orbitDist = Mathf.Clamp(_orbitDist + zoom, 0.6f, 5f);
            if (_lifePhotoGo != null)
            {
                float turn = (Input.GetKey(KeyCode.E) ? 1f : 0f) - (Input.GetKey(KeyCode.Q) ? 1f : 0f);
                if (turn != 0f) _lifePhotoGo.transform.Rotate(0f, turn * 90f * dt, 0f, Space.Self);
            }
            ApplyLifeOrbit();
#endif
            return true;
        }

        void EndLifePhoto()
        {
            if (!_lifePhoto) return;
            _lifePhoto = false;
            if (_ui != null)
            {
                _ui.SetClipTexture("/mylf/photo_taker/lure", null);
                foreach (var n in LifePhotoHide) if (n != "help") _ui.SetVisible("/" + n, true);
            }
            if (_lifePhotoGo != null) Destroy(_lifePhotoGo);
            if (_lifeCam != null) Destroy(_lifeCam.gameObject);
            if (_lifeRt != null) { _lifeRt.Release(); Destroy(_lifeRt); }
            if (_lifeShowRt != null) { _lifeShowRt.Release(); Destroy(_lifeShowRt); _lifeShowRt = null; }
            _lifePhotoGo = null; _lifeAvatar = null; _lifeCam = null; _lifeRt = null;
            var cam = Camera.main;
            if (cam != null) { cam.transform.SetPositionAndRotation(_lifeCamPos, _lifeCamRot); cam.fieldOfView = _lifeCamFov; }
            if (_fp != null && _fpWasOn) { _fp.enabled = true; _fp.LookEnabled = false; }
        }

        /// <summary>Tagebuch: Foto- und Texteingabe-Modus vor allem anderen; true = Eingabe verbraucht.</summary>
        // Ladeanzeige /mylf/cmon_loader (wartet per onEnterFrame auf _root.mylfTextCorrect bzw. _root.MyLifeUploaded): schließt
        // sie nicht selbst (z.B. weil der Wert schon vor initLoading gesetzt war), hier nachhelfen – sonst hängt "Annehmen"/Verlassen
        float _loaderKick1 = -1f, _loaderKick2 = -1f;

        void KickLifeLoader() { _loaderKick1 = Time.unscaledTime + 0.8f; _loaderKick2 = Time.unscaledTime + 2.2f; }

        void UpdateLifeLoader()
        {
            const string L = "/mylf/cmon_loader";
            if (_loaderKick1 > 0f && Time.unscaledTime >= _loaderKick1)
            {
                _loaderKick1 = -1f;
                if (_ui.GetVariable(L + ":isLoading") == "true") { Debug.Log("[TDU2] MyTDULife: Ladeanzeige geschlossen"); _ui.CallClip(L, "closeLoading"); }
            }
            if (_loaderKick2 > 0f && Time.unscaledTime >= _loaderKick2)
            {
                _loaderKick2 = -1f;
                if (_ui.GetVariable(L + ":isLoading") == "true") { Debug.Log("[TDU2] MyTDULife: Ladeanzeige beendet"); _ui.CallClip(L, "endLoading"); _ui.SetValue("/:Input", true); }
            }
        }

        bool UpdateMyLife()
        {
            if (_ui == null) return false;
            UpdateLifeLoader();
            TDU2GameData.ExternalTextInput = _textVar != null;
            if (UpdateLifePhoto()) return true;
            return UpdateTextInput();
        }

        void EndLifeModes()
        {
            _textVar = null;
            TDU2GameData.ExternalTextInput = false;
            EndLifePhoto();
        }
    }
}
