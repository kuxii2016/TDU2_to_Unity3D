using System;
using System.Collections.Generic;
using TDU2.Avatars;
using TDU2.Flash;
using TDU2.Formats;
using TDU2.Vehicles;
using UnityEngine;

namespace TDU2.Online
{
    /// <summary>
    /// Mitspieler beitreten und einladen (Spielerliste im Pausemenü, große Karte) und Mitfahren als Beifahrer ("Co-Driving").
    ///   * Beitreten (pcrd:pjoi, join:wait/pjoi, Karte goin/T): man landet mit dem eigenen Auto hinter dem Mitspieler; aus einem
    ///     Interieur heraus wird es vorher verlassen. Nicht möglich: andere Insel, Mitspieler nicht unterwegs, selbst Beifahrer.
    ///   * Einladen (Spielerkarte pcrd:pinv): Spiel-Einladung wie im Original – beim Eingeladenen erscheint general.bnk
    ///     /pop_gameInvite (open_mini, Enter = Details open_big, Enter = annehmen valid_invite -> "giok", Rücktaste = B -> "gica").
    ///     Annehmen bringt ihn automatisch zum Einladenden: steht der in einem Interieur, betritt der Gast dasselbe (Haus: auch
    ///     dieselbe Szene Wohnbereich/Garage, in der Garage stehen die Autos des Gastgebers mit dessen Lack/Beklebung), sonst
    ///     landet er mit seinem Auto hinter ihm. Der Einladende sieht die Antwort (open_answer: angenommen/abgelehnt).
    ///     Häuser sind privat: dort sieht man nur Gastgeber/Gäste (<see cref="InGroup"/>).
    ///   * Beifahrer (<see cref="InviteRide"/>): HUD-Fenster /HUD/contact (Label "invite", WhatContact "codriving");
    ///     Enter = annehmen (acpt), Rücktaste = ablehnen (rjct).
    ///   * Mitfahrt: der Eingeladene gibt sein Auto ab und sitzt im Auto des Einladenden – er sieht dessen Auto (Modell, Lack,
    ///     Beklebung über die UDP-Synchronisation) mit dem Fahrer-Avatar und sich selbst auf dem Beifahrersitz; der Fahrer sieht
    ///     den Beifahrer ebenfalls. Kamera: außen hinter dem Auto bzw. (Taste C) vom Beifahrersitz, Maus rechts = umsehen.
    ///     F = aussteigen (das eigene Auto erscheint neben dem Fahrer). Endet der Fahrer, steigt der Beifahrer automatisch aus.
    /// </summary>
    public sealed class TDU2Party : MonoBehaviour
    {
        public static TDU2Party Instance { get; private set; }
        /// <summary>Ich fahre als Beifahrer mit (Spielernummer des Fahrers, 0 = nein).</summary>
        public int RideWith { get; private set; }
        /// <summary>Mein Beifahrer (0 = keiner).</summary>
        public int Passenger { get; private set; }
        public static bool IsPassenger => Instance != null && Instance.RideWith != 0;
        /// <summary>Ich bin Gast im Haus von Spieler … (0 = nein).</summary>
        public int HomeHost { get; private set; }
        /// <summary>Meine Gäste im Haus.</summary>
        public readonly HashSet<int> HomeGuests = new HashSet<int>();
        /// <summary>Gehört der Spieler zu meiner Gruppe (Mitfahrt, Haus-Einladung)? Häuser zeigen nur diese Mitspieler.</summary>
        public bool InGroup(int id) => id != 0 && (RideWith == id || Passenger == id || HomeHost == id || HomeGuests.Contains(id));

        [Tooltip("Abstand hinter dem Mitspieler beim Beitreten (m)")] public float JoinDistance = 12f;
        [Tooltip("Einladung so lange anzeigen (Sekunden), danach abgelehnt")] public float InviteSeconds = 25f;

        TDU2VehicleManager _manager;
        TDU2VehicleManager Manager
        {
            get
            {
                if (_manager == null)
                {
#if UNITY_2023_1_OR_NEWER
                    _manager = FindFirstObjectByType<TDU2VehicleManager>();
#else
                    _manager = FindObjectOfType<TDU2VehicleManager>();
#endif
                }
                return _manager;
            }
        }

        public static TDU2Party Ensure(GameObject host)
        {
            if (Instance != null) return Instance;
            var p = host.GetComponent<TDU2Party>() ?? host.AddComponent<TDU2Party>();
            Instance = p;
            return p;
        }

        void Awake() { if (Instance == null) Instance = this; }
        void OnDestroy() { if (Instance == this) Instance = null; }

        static TDU2GameData GD => TDU2GameData.Instance;

        void Say(string text)
        {
            Debug.Log("[TDU2] " + text);
            GD?.AddChatLine("TDU2", text, 0);
        }

        // ================================================================== Beitreten
        /// <summary>Kann man dem Spieler beitreten? <paramref name="error"/> = Grund (für JoinError / Chat).</summary>
        public bool CanJoin(int id, out string error)
        {
            error = null;
            var gd = GD;
            if (gd == null || !gd.Online) { error = "Nicht mit dem Server verbunden."; return false; }
            if (!gd.Players.TryGetValue(id, out var p) || !p.Online) { error = "Spieler ist nicht online."; return false; }
            if (RideWith != 0) { error = "Du fährst gerade als Beifahrer mit."; return false; }
            if (!p.SameIsland) { error = p.Nick + " ist auf der anderen Insel."; return false; }
            bool live = TDU2Multiplayer.Instance != null && TDU2Multiplayer.Instance.RemoteCar(id) != null;
            if (!live && (!p.InCar || !p.HasPos)) { error = p.Nick + " ist gerade nicht unterwegs."; return false; }
            var m = Manager;
            var inter = m != null ? m.GetComponent<TDU2Interiors>() : null;
            if (m == null || (m.Current == null && (inter == null || !inter.Inside))) { error = "Kein eigenes Auto."; return false; }
            return true;
        }

        /// <summary>Hinter den Spieler setzen (aus einem Interieur heraus mit dem zuletzt gefahrenen Auto).</summary>
        public bool Join(int id)
        {
            if (!CanJoin(id, out var err)) { Say(err); return false; }
            if (!TargetBehind(id, JoinDistance, out var pos, out var rot)) { Say("Position des Spielers unbekannt."); return false; }
            var m = Manager;
            var inter = m.GetComponent<TDU2Interiors>();
            if (inter != null && inter.Inside)
            {
                var car = inter.LastCar;
                car = inter.ExitInterior(car, false) ?? car ?? m.FindCar(m.StartCar);
                if (car != null) m.SpawnCar(car, pos, rot);
            }
            else if (m.Current != null) m.TeleportSafe(pos, rot);   // wartet, bis dort der Boden geladen ist
            else return false;
            var gd = GD;
            Say("Beigetreten: " + (gd != null && gd.Players.TryGetValue(id, out var p) ? p.Nick : "Spieler " + id));
            return true;
        }

        /// <summary>Punkt <paramref name="back"/> m hinter dem Auto des Spielers (Weltkoordinaten), Blick in seine Fahrtrichtung.</summary>
        bool TargetBehind(int id, float back, out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = Quaternion.identity;
            var m = Manager;
            var root = m != null && m.Streamer != null ? m.Streamer.MapRoot : null;
            Vector3 at, fwd;
            var go = TDU2Multiplayer.Instance != null ? TDU2Multiplayer.Instance.RemoteCar(id) : null;
            if (go != null) { at = go.transform.position; fwd = go.transform.forward; }
            else
            {
                var gd = GD;
                if (root == null || gd == null || !gd.Players.TryGetValue(id, out var p) || !p.HasPos) return false;
                at = root.TransformPoint(p.GamePos); fwd = root.TransformVector(p.GameForward);
            }
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
            fwd.Normalize();
            pos = at - fwd * back;
            pos.y = m.GroundHeight(pos) + 0.5f;
            rot = Quaternion.LookRotation(fwd, Vector3.up);
            return true;
        }

        // ================================================================== Einladen
        System.Threading.Tasks.Task<NetResponse> _inviteTask;
        int _inviteTarget;

        readonly Dictionary<int, string> _sentKinds = new Dictionary<int, string>();

        /// <summary>
        /// Spiel-Einladung (Spielerkarte "Einladen"): der Eingeladene kommt zu mir – ins selbe Interieur/Haus ("room|SPOT|Bank|Ort")
        /// bzw. hinter mein Auto ("game|Ort").
        /// </summary>
        public void Invite(int id)
        {
            var gd = GD;
            if (!TDU2Session.LoggedIn || TDU2Session.Client == null) { Say("Nicht mit dem Server verbunden."); return; }
            if (RideWith != 0) { Say("Du fährst gerade als Beifahrer mit."); return; }
            var m = Manager;
            var inter = m != null ? m.GetComponent<TDU2Interiors>() : null;
            string kind;
            if (inter != null && inter.Inside && inter.CurrentSpot != null)
            {
                if (inter.PresenceKey == 0) { Say("Hierher kann man niemanden einladen."); return; }
                kind = "room|" + inter.CurrentSpot.Code + "|" + inter.CurrentBankStem + "|" + Clean(inter.CurrentPlaceName);
            }
            else if (m != null && m.Current != null) kind = "game|";
            else { Say("Zum Einladen musst du unterwegs oder in einem Gebäude sein."); return; }
            _sentKinds[id] = kind;
            _inviteTarget = id;
            _inviteTask = TDU2Session.Client.Send(new NetRequest { op = "invite", session = TDU2Session.SessionId, target = id, kind = kind });
            string nick = gd != null && gd.Players.TryGetValue(id, out var p) ? p.Nick : "Spieler " + id;
            Say("Einladung an " + nick + " gesendet.");
        }

        static string Clean(string s) => (s ?? "").Replace("|", "/");

        /// <summary>Spieler als Beifahrer einladen (Server leitet weiter, Antwort kommt über "status").</summary>
        public void InviteRide(int id)
        {
            var gd = GD;
            if (!TDU2Session.LoggedIn || TDU2Session.Client == null) { Say("Nicht mit dem Server verbunden."); return; }
            if (RideWith != 0) { Say("Du fährst gerade als Beifahrer mit."); return; }
            if (Passenger != 0) { Say("Du hast schon einen Beifahrer."); return; }
            if (Manager == null || Manager.Current == null) { Say("Zum Einladen musst du im Auto sitzen."); return; }
            _inviteTarget = id;
            _sentKinds[id] = "codriving";
            _inviteTask = TDU2Session.Client.Send(new NetRequest { op = "invite", session = TDU2Session.SessionId, target = id, kind = "codriving" });
            string nick = gd != null && gd.Players.TryGetValue(id, out var p) ? p.Nick : "Spieler " + id;
            Say("Einladung an " + nick + " gesendet.");
        }

        void PollInvite()
        {
            if (_inviteTask == null || !_inviteTask.IsCompleted) return;
            var r = _inviteTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? _inviteTask.Result : null;
            _inviteTask = null;
            if (r != null && r.ok) return;
            string e = r?.error ?? "";
            Say("Einladung nicht möglich: " + (e == "other_island" ? "andere Insel" : e == "busy" ? "Spieler fährt schon mit" : e == "unknown_player" ? "Spieler nicht online" :
                e == "has_passenger" ? "du hast schon einen Beifahrer" : e == "is_passenger" ? "du fährst selbst mit" : e));
        }

        // ================================================================== Status vom Server
        readonly HashSet<int> _shownInvites = new HashSet<int>();
        readonly Queue<InviteEntry> _pendingInvites = new Queue<InviteEntry>();

        public void OnStatus(NetResponse r)
        {
            if (r == null) return;
            foreach (var inv in r.invites ?? new List<InviteEntry>())
                if (inv != null && inv.from != 0 && _shownInvites.Add(inv.from)) _pendingInvites.Enqueue(inv);
            // Einladungen, die der Server nicht mehr kennt, dürfen wiederkommen
            var still = new HashSet<int>();
            foreach (var inv in r.invites ?? new List<InviteEntry>()) if (inv != null) still.Add(inv.from);
            _shownInvites.RemoveWhere(f => !still.Contains(f) && (_popup == null || _popup.from != f));

            foreach (var n in r.notices ?? new List<string>())
            {
                var parts = (n ?? "").Split(new[] { ':' }, 3);
                if (parts.Length < 3) continue;
                int.TryParse(parts[1], out int pid);
                string nick = parts[2];
                switch (parts[0])
                {
                    case "accepted": Say(nick + " fährt bei dir mit."); HudResult(true, nick); break;
                    case "joined":
                        {
                            Say(nick + " hat die Einladung angenommen und kommt zu dir.");
                            if (_sentKinds.TryGetValue(pid, out var k) && IsHouseKind(k)) HomeGuests.Add(pid);
                            GameAnswer(true);
                            break;
                        }
                    case "declined":
                        Say(nick + " hat die Einladung abgelehnt.");
                        if (_sentKinds.TryGetValue(pid, out var dk) && dk != "codriving") GameAnswer(false); else HudResult(false, nick);
                        break;
                    case "rideend": Say("Mitfahrt mit " + nick + " beendet."); break;
                    case "news":   // Server-Konsole "say <Text>": Meldung an alle (Flash-Fenster pop_news des General-Films)
                        GD?.AddChatLine("Server", nick, 0);
                        if (TDU2UIManager.General != null) TDU2UIManager.SendNews(nick);
                        else Debug.Log("[TDU2] Server: " + nick);
                        break;
                }
            }
            // Mitfahrt (Server ist maßgeblich)
            if (r.rideWith != 0 && r.rideWith == _leftDriver && Time.unscaledTime - _leftAt < 6f) { }   // Antwort von vor dem Aussteigen
            else if (r.rideWith != RideWith)
            {
                if (r.rideWith != 0) BeginRide(r.rideWith);
                else if (RideWith != 0) EndRideLocal(false);
            }
            if (r.passenger != Passenger) { Passenger = r.passenger; UpdatePassengerAvatar(true); }
        }

        // ================================================================== HUD-Fenster (hud.vmf /HUD/contact)
        InviteEntry _popup;
        float _popupUntil;
        VmfPlayer _hud;
        bool _hudHooked;

        VmfPlayer Hud
        {
            get
            {
                var h = Manager != null && Manager.Minimap != null ? Manager.Minimap.HudPlayer : null;
                if (h != _hud)
                {
                    if (_hud != null && _hudHooked) _hud.FSCommand -= OnHudCommand;
                    _hud = h; _hudHooked = false;
                    if (_hud != null) { _hud.FSCommand += OnHudCommand; _hudHooked = true; }
                }
                return _hud;
            }
        }

        bool CanShowPopup()
        {
            var m = Manager;
            if (m == null || m.Current == null || Hud == null) return false;
            var inter = m.GetComponent<TDU2Interiors>();
            if (inter != null && inter.Inside) return false;
            if (Time.timeScale <= 0f || TDU2BigMap.IsOpen || RideWith != 0) return false;
            return true;
        }

        void ShowInvite(InviteEntry inv)
        {
            var h = Hud;
            _popup = inv; _popupGame = false;
            _popupUntil = Time.unscaledTime + InviteSeconds;
            h.SetVariable("/:WhatContact", "codriving");
            h.SetValue("/:nb_players", 1);
            h.SetVariable("/HUD/contact/opponents/p1:name", inv.nick);
            h.GotoLabel("/HUD/contact", "invite", true);
            TDU2FrontendSounds.Command("sndp:recep");
            Say(inv.nick + " lädt dich als Beifahrer ein  (Enter = annehmen, Rücktaste = ablehnen)");
        }

        void HudResult(bool accepted, string nick)
        {
            var h = Hud;
            if (h == null) return;
            h.SetVariable("/:WhatContact", "codriving");
            h.SetValue("/:nb_players", 1);
            h.SetVariable("/HUD/contact/opponents/p1:name", nick);
            h.GotoLabel("/HUD/contact", accepted ? "accept" : "cancel", true);
        }

        void OnHudCommand(string cmd, string args)
        {
            string c = (cmd ?? "").Trim().ToLowerInvariant();
            if (_popup == null) return;
            switch (c)
            {
                case "acpt": Reply(true); break;
                case "rjct": case "cncl": Reply(false); break;
            }
        }

        void Reply(bool accept)
        {
            var inv = _popup;
            _popup = null;
            General?.MouseIgnore.Remove("/pop_gameInvite");
            if (inv == null || TDU2Session.Client == null) return;
            var t = TDU2Session.Client.Send(new NetRequest { op = "invreply", session = TDU2Session.SessionId, target = inv.from, accept = accept });
            _replyTask = t; _replyFrom = inv.from; _replyAccept = accept; _replyInv = inv;
            if (!accept) Say("Einladung von " + inv.nick + " abgelehnt.");
        }

        System.Threading.Tasks.Task<NetResponse> _replyTask;
        int _replyFrom; bool _replyAccept; InviteEntry _replyInv;

        void PollReply()
        {
            if (_replyTask == null || !_replyTask.IsCompleted) return;
            var r = _replyTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? _replyTask.Result : null;
            _replyTask = null;
            if (!_replyAccept) return;
            if (r != null && r.ok && !string.IsNullOrEmpty(r.inviteKind) && !r.inviteKind.StartsWith("codriving")) { Arrive(_replyFrom, _replyInv, r.inviteKind, r.save); return; }
            if (r != null && r.ok && r.rideWith != 0) { var h = Hud; if (h != null) h.GotoLabel("/HUD/contact", "accept", true); BeginRide(r.rideWith); }
            else Say("Mitfahren nicht möglich" + (r != null && !string.IsNullOrEmpty(r.error) ? " (" + r.error + ")" : "") + ".");
        }

        // ================================================================== Mitfahrt (Beifahrer)
        CarPhysics _ownCar;
        TDU2CarAvatar _seatAvatar;      // ich auf dem Beifahrersitz des Fahrer-Autos
        GameObject _seatCar;
        bool _cockpit;
        float _yaw, _pitch = 12f;
        Vector3 _camPos;
        bool _camInit;
        float _lostSince = -1f;
        int _leftDriver; float _leftAt = -100f;

        void BeginRide(int driver)
        {
            if (RideWith == driver) return;
            var m = Manager;
            if (m == null) return;
            var inter = m.GetComponent<TDU2Interiors>();
            if (inter != null && inter.Inside) { _ownCar = inter.ExitInterior(inter.LastCar, false) ?? inter.LastCar; }
            else _ownCar = m.Current != null ? m.Current.Car : _ownCar;
            m.RemoveCar();
            m.Suspended = true;
            RideWith = driver;
            _camInit = false; _yaw = 0f; _pitch = 12f; _lostSince = -1f;
            var gd = GD;
            Say("Du fährst bei " + (gd != null && gd.Players.TryGetValue(driver, out var p) ? p.Nick : "Spieler " + driver) + " mit  (F = aussteigen, C = Kamera)");
        }

        /// <summary>Aussteigen: eigenes Auto neben dem Fahrer.</summary>
        public void LeaveRide() => EndRideLocal(true);

        void EndRideLocal(bool tellServer)
        {
            if (RideWith == 0) return;
            int driver = RideWith;
            RideWith = 0;
            _leftDriver = driver; _leftAt = Time.unscaledTime;
            if (tellServer && TDU2Session.Client != null) TDU2Session.Client.Send(new NetRequest { op = "leaveride", session = TDU2Session.SessionId });
            DestroySeatAvatar();
            var m = Manager;
            if (m == null) return;
            m.Suspended = false;
            var car = _ownCar ?? m.FindCar(m.StartCar) ?? (m.Available.Count > 0 ? m.Available[0] : null);
            Vector3 pos; Quaternion rot;
            var go = TDU2Multiplayer.Instance != null ? TDU2Multiplayer.Instance.RemoteCar(driver) : null;
            if (go != null)
            {
                var right = go.transform.right; right.y = 0f; right.Normalize();
                pos = go.transform.position + right * 4f - go.transform.forward * 2f;
                pos.y = m.GroundHeight(pos) + 0.5f;
                var f = go.transform.forward; f.y = 0f;
                rot = Quaternion.LookRotation(f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward, Vector3.up);
            }
            else
            {
                var cam = Camera.main;
                pos = cam != null ? cam.transform.position : Vector3.zero;
                pos.y = m.GroundHeight(pos) + 0.5f;
                var f = cam != null ? cam.transform.forward : Vector3.forward; f.y = 0f;
                rot = Quaternion.LookRotation(f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward, Vector3.up);
            }
            if (car != null) m.SpawnCar(car, pos, rot);
        }

        void DestroySeatAvatar()
        {
            if (_seatAvatar != null) Destroy(_seatAvatar);
            _seatAvatar = null; _seatCar = null;
        }

        void UpdateRide()
        {
            if (RideWith == 0) return;
            var mp = TDU2Multiplayer.Instance;
            var go = mp != null ? mp.RemoteCar(RideWith) : null;
            var model = mp != null ? mp.RemoteModel(RideWith) : null;
            // Fahrer weg (offline, im Interieur, kein Auto): nach einer Weile aussteigen
            if (go == null)
            {
                if (_lostSince < 0f) _lostSince = Time.unscaledTime;
                if (Time.unscaledTime - _lostSince > 12f) { Say("Fahrer nicht mehr unterwegs – Mitfahrt beendet."); EndRideLocal(true); }
                return;
            }
            _lostSince = -1f;
            // ich selbst auf dem Beifahrersitz (neu nach Autowechsel des Fahrers)
            if (_seatCar != go || _seatAvatar == null)
            {
                DestroySeatAvatar();
                if (model != null && AvatarLibrary.Ready)
                {
                    int d = RideWith;
                    _seatAvatar = TDU2CarAvatar.Attach(go, model, AvatarConfigs.Local, true, () => mp.RemoteSteer(d));
                    if (_seatAvatar != null) _seatAvatar.ConfigCode = () => TDU2Session.Save?.avatar;
                    _seatCar = go;
                }
            }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (!TDU2GameData.ChatTyping && Time.timeScale > 0f)
            {
                if (Input.GetKeyDown(KeyCode.F)) { EndRideLocal(true); return; }
                if (Input.GetKeyDown(KeyCode.C)) { _cockpit = !_cockpit; _yaw = 0f; _pitch = _cockpit ? 0f : 12f; }
                if (Input.GetMouseButton(1))
                {
                    _yaw += Input.GetAxis("Mouse X") * 3f;
                    _pitch = Mathf.Clamp(_pitch - Input.GetAxis("Mouse Y") * 3f, _cockpit ? -40f : -5f, _cockpit ? 40f : 60f);
                }
                else if (!_cockpit) _yaw = Mathf.MoveTowards(_yaw, 0f, Time.unscaledDeltaTime * 60f);
            }
#endif
        }

        void LateUpdate()
        {
            if (RideWith == 0) return;
            var mp = TDU2Multiplayer.Instance;
            var go = mp != null ? mp.RemoteCar(RideWith) : null;
            var cam = Camera.main;
            if (go == null || cam == null) return;
            var t = go.transform;
            Vector3 pos; Quaternion rot;
            var model = mp.RemoteModel(RideWith);
            var seat = _cockpit && model != null ? (model.Find("PSGSEAT_HR") ?? model.Find("PSGSEAT_LR")) : null;
            if (seat != null)
            {
                pos = seat.position + t.up * 0.68f + t.forward * 0.05f;
                rot = Quaternion.LookRotation(Quaternion.AngleAxis(_yaw, t.up) * Quaternion.AngleAxis(_pitch, t.right) * t.forward, t.up);
                cam.transform.SetPositionAndRotation(pos, rot);
                _camPos = pos; _camInit = true;
                return;
            }
            float len = model != null ? Mathf.Max(2f, model.BodyBounds.extents.z) : 2.3f;
            var flat = t.forward; flat.y = 0f;
            if (flat.sqrMagnitude < 1e-4f) flat = Vector3.forward;
            flat.Normalize();
            var dir = Quaternion.Euler(_pitch, _yaw, 0f);
            var back = Quaternion.LookRotation(flat, Vector3.up) * dir * Vector3.back;
            var look = t.position + Vector3.up * 1.1f;
            pos = look + back * (len * 2.2f + 2.5f) + Vector3.up * 0.6f;
            float k = 1f - Mathf.Exp(-Time.unscaledDeltaTime * 8f);
            if (!_camInit || (pos - _camPos).sqrMagnitude > 40f * 40f) { _camPos = pos; _camInit = true; }
            else _camPos = Vector3.Lerp(_camPos, pos, k);
            cam.transform.SetPositionAndRotation(_camPos, Quaternion.LookRotation(look - _camPos, Vector3.up));
        }

        // ================================================================== Fahrer: Beifahrer im eigenen Auto
        TDU2CarAvatar _guest;
        GameObject _guestCar;

        void UpdatePassengerAvatar(bool force)
        {
            var m = Manager;
            var car = m != null && m.Current != null ? m.Current.gameObject : null;
            if (Passenger == 0 || car == null || m.Model == null)
            {
                if (_guest != null) Destroy(_guest);
                _guest = null; _guestCar = null;
                return;
            }
            if (!force && _guest != null && _guestCar == car) return;
            if (_guest != null) Destroy(_guest);
            _guest = null; _guestCar = null;
            if (!AvatarLibrary.Ready) return;
            int pid = Passenger;
            var veh = m.Current;
            _guest = TDU2CarAvatar.Attach(car, m.Model, AvatarConfigs.ForPlayer(pid), true, () => veh != null ? veh.Steer : 0f);
            if (_guest != null) { _guest.ConfigCode = () => AvatarConfigs.RemoteCode(pid); _guestCar = car; }
        }

        // ================================================================== Spiel-Einladung (general.bnk /pop_gameInvite)
        bool _popupGame, _gameBig, _generalHooked;
        VmfPlayer _generalHookedOn;
        string _reqTemplate;
        float _answerCloseAt = -1f;

        static VmfPlayer General => TDU2GameData.Instance != null ? TDU2GameData.Instance.General : null;

        static bool IsHouseKind(string kind)
        {
            var p = (kind ?? "").Split('|');
            return p.Length > 1 && p[0] == "room" && p[1].StartsWith("EPH", StringComparison.OrdinalIgnoreCase) && !p[1].StartsWith("EPHOT", StringComparison.OrdinalIgnoreCase);
        }

        void HookGeneral()
        {
            var g = General;
            if (g == _generalHookedOn && _generalHooked) return;
            if (_generalHookedOn != null && _generalHooked) _generalHookedOn.FSCommand -= OnGeneralCommand;
            _generalHookedOn = g; _generalHooked = false;
            if (g != null) { g.FSCommand += OnGeneralCommand; _generalHooked = true; }
        }

        bool CanShowGamePopup()
        {
            if (General == null || Time.timeScale <= 0f || TDU2BigMap.IsOpen || RideWith != 0) return false;
            if (TDU2ClothesShop.Busy || TDU2Hairdresser.Busy || TDU2StickerShop.Busy) return false;
            return true;
        }

        void ShowGameInvite(InviteEntry inv)
        {
            var g = General;
            HookGeneral();
            _popup = inv; _popupGame = true; _gameBig = false; _bigAt = -1f;
            _popupUntil = Time.unscaledTime + InviteSeconds;
            // Das kleine Fenster hat einen Maus-Button, der sofort annimmt (valid_invite) – ein beliebiger Klick (Menü, Kamera)
            // nahm so die Einladung an. Kleine Ansicht nur per Tastatur; Maus erst in der großen Ansicht.
            g.MouseIgnore.Add("/pop_gameInvite");
            var parts = (inv.kind ?? "").Split('|');
            string place = parts[0] == "room" ? (parts.Length > 3 ? parts[3] : "") : (parts.Length > 1 ? parts[1] : "");
            g.Show("/pop_gameInvite");
            g.SetValue("/:InviteForbidden", 0);
            g.SetValue("/:InviteAccepted", 0);
            g.SetVariable("/:InvitePlace", place);
            g.SetValue("/:WhatDistrict", 1);
            g.SetVariable("/:AvatarName", inv.nick);
            if (_reqTemplate == null) _reqTemplate = g.GetVariable("/:m_InviteRequest") ?? "";
            g.SetVariable("/:m_InviteRequest", FillNick(_reqTemplate, inv.nick));
            g.GotoLabel("/pop_gameInvite", "open_mini", true);
            Say(inv.nick + " lädt dich ein" + (string.IsNullOrEmpty(place) ? "" : " (" + place + ")") + "  (Enter = ansehen/annehmen, Rücktaste = ablehnen)");
        }

        /// <summary>Spielername in den Text "_RIBOS invites you to join him" (Lokalisierung) einsetzen.</summary>
        static string FillNick(string template, string nick)
        {
            if (string.IsNullOrEmpty(template)) return nick + " lädt dich ein";
            foreach (var tok in new[] { "RIBOS", "Ribos", "%s", "%1$s", "%1", "[NAME]", "<NAME>", "{0}" })
                if (template.Contains(tok)) return template.Replace(tok, nick);
            return nick + ": " + template;
        }

        void UpdateGamePopup()
        {
            var g = General;
            if (g == null) { Reply(false); return; }
            if (Time.unscaledTime > _popupUntil)
            {
                g.GotoLabel("/pop_gameInvite", _gameBig ? "close_big" : "close_mini", true);
                Reply(false);
                return;
            }
#if ENABLE_LEGACY_INPUT_MANAGER
            if (TDU2GameData.ChatTyping || Time.timeScale <= 0f) return;
            if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                if (!_gameBig) { OpenBig(); if (!g.CallClip("/pop_gameInvite", "open_big")) g.GotoLabel("/pop_gameInvite", "open_big", true); }
                else if (Time.unscaledTime - _bigAt >= 0.5f)   // erst ansehen, dann (zweites Enter) annehmen
                {
                    if (!g.CallClip("/pop_gameInvite", "valid_invite")) Reply(true);
                    _acceptAt = Time.unscaledTime + 1.5f;   // falls "giok" nicht kommt
                }
            }
            else if (Input.GetKeyDown(KeyCode.Backspace))
            {
                if (!g.CallClip("/pop_gameInvite", "B")) { g.GotoLabel("/pop_gameInvite", _gameBig ? "close_big" : "close_mini", true); Reply(false); }
                _declineAt = Time.unscaledTime + 1f;
            }
#endif
        }

        void OnGeneralCommand(string cmd, string args)
        {
            string c = (cmd ?? "").Trim().ToLowerInvariant();
            if (_popup == null || !_popupGame) return;
            switch (c)
            {
                case "gein": case "opbg": if (!_gameBig) OpenBig(); break;
                case "giok":
                    // nur aus der großen Ansicht annehmen (kein versehentlicher Klick/Tastendruck in der kleinen)
                    if (_gameBig && Time.unscaledTime - _bigAt >= 0.5f) Reply(true);
                    else { var g = General; g?.Show("/pop_gameInvite"); g?.GotoLabel("/pop_gameInvite", _gameBig ? "open_big" : "open_mini", true); }
                    break;
                case "gica": Reply(false); break;
            }
        }

        float _bigAt = -1f;

        void OpenBig()
        {
            _gameBig = true; _bigAt = Time.unscaledTime;
            General?.MouseIgnore.Remove("/pop_gameInvite");
        }

        /// <summary>Antwort beim Einladenden: open_answer (IsInvitAccepted 1 = angenommen, sonst abgelehnt).</summary>
        void GameAnswer(bool accepted)
        {
            var g = General;
            if (g == null) return;
            g.Show("/pop_gameInvite");
            g.SetValue("/:IsInvitAccepted", accepted ? 1 : 0);
            g.GotoLabel("/pop_gameInvite", "open_answer", true);
            _answerCloseAt = Time.unscaledTime + 3.5f;
        }

        /// <summary>Angenommen: zum Einladenden (Interieur/Haus bzw. hinter sein Auto).</summary>
        void Arrive(int from, InviteEntry inv, string kind, SaveData hostSave)
        {
            var gd = GD;
            string nick = inv != null ? inv.nick : (gd != null && gd.Players.TryGetValue(from, out var pl) ? pl.Nick : "Spieler " + from);
            var parts = (kind ?? "").Split('|');
            var m = Manager;
            var inter = m != null ? m.GetComponent<TDU2Interiors>() : null;
            if (parts[0] == "room" && parts.Length >= 3 && inter != null)
            {
                Spot spot = null;
                var spots = m.Streamer != null && m.Streamer.Spots != null ? m.Streamer.Spots.Spots : null;
                if (spots != null) foreach (var sp in spots) if (string.Equals(sp.Code, parts[1], StringComparison.OrdinalIgnoreCase)) { spot = sp; break; }
                if (spot == null) { Say("Ort der Einladung nicht gefunden (" + parts[1] + ")."); return; }
                var last = m.Current != null ? m.Current.Car : (inter.Inside ? inter.LastCar : null);
                if (!inter.EnterSpotRoom(spot, parts[2], last)) { Say("Ort der Einladung kann nicht betreten werden."); return; }
                if (IsHouseKind(kind))
                {
                    HomeHost = from;
                    inter.GuestHost = from;
                    if (hostSave != null)
                    {
                        var cars = new List<string>();
                        if (!string.IsNullOrEmpty(hostSave.currentCar)) cars.Add(hostSave.currentCar);
                        foreach (var c in hostSave.ownedCars ?? new List<string>()) if (!string.IsNullOrEmpty(c) && !cars.Contains(c)) cars.Add(c);
                        var liv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var l in hostSave.liveries ?? new List<LiveryEntry>()) if (l != null && !string.IsNullOrEmpty(l.car)) liv[l.car] = l.data;
                        inter.GuestGarageCars = cars;
                        inter.GuestLiveries = liv;
                        inter.GuestHouses = hostSave.houses;   // My House: Einrichtung des Gastgebers
                    }
                }
                Say("Du bist jetzt bei " + nick + ".");
                return;
            }
            if (!Join(from)) Say("Zu " + nick + " konnte nicht teleportiert werden.");
        }

        /// <summary>Haus-Gruppe auflösen, sobald Gast bzw. Gastgeber das Haus verlassen hat.</summary>
        void UpdateGroups()
        {
            var m = Manager;
            var inter = m != null ? m.GetComponent<TDU2Interiors>() : null;
            bool inHouse = inter != null && inter.Inside && inter.CurrentSpot != null && IsHouseKind("room|" + inter.CurrentSpot.Code);
            if (HomeHost != 0 && (!inHouse || inter.GuestHost != HomeHost)) HomeHost = 0;
            if (HomeGuests.Count > 0 && !inHouse) HomeGuests.Clear();
            var gd = GD;
            if (gd != null && HomeGuests.Count > 0) HomeGuests.RemoveWhere(id => !gd.Players.TryGetValue(id, out var p) || !p.Online);
        }

        // ================================================================== Ablauf
        void Update()
        {
            PollInvite();
            PollReply();
            // Einladung anzeigen, sobald das HUD frei ist
            if (_popup == null && _pendingInvites.Count > 0)
            {
                var next = _pendingInvites.Peek();
                bool ride = string.IsNullOrEmpty(next.kind) || next.kind.StartsWith("codriving");
                if (ride ? CanShowPopup() : CanShowGamePopup())
                {
                    _pendingInvites.Dequeue();
                    if (ride) ShowInvite(next); else ShowGameInvite(next);
                }
            }
            UpdateGroups();
            if (_answerCloseAt > 0f && Time.unscaledTime >= _answerCloseAt) { _answerCloseAt = -1f; General?.GotoLabel("/pop_gameInvite", "close_answer", true); }
            if (_popup != null && _popupGame) UpdateGamePopup();
            else if (_popup != null)
            {
                if (Time.unscaledTime > _popupUntil) { var h = Hud; if (h != null) h.CallClip("/HUD/contact", "cancel_contact"); if (_popup != null) Reply(false); }
#if ENABLE_LEGACY_INPUT_MANAGER
                else if (!TDU2GameData.ChatTyping && Time.timeScale > 0f)
                {
                    var h = Hud;
                    if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                    {
                        if (h == null || !h.CallClip("/HUD/contact", "accept")) Reply(true);
                        // Film meldet "acpt" erst nach der Animation – spätestens nach 1,5 s selbst antworten
                        _acceptAt = Time.unscaledTime + 1.5f;
                    }
                    else if (Input.GetKeyDown(KeyCode.Backspace))
                    {
                        if (h == null || !h.CallClip("/HUD/contact", "cancel_contact")) Reply(false);
                        _declineAt = Time.unscaledTime + 1f;
                    }
                }
#endif
            }
            if (_acceptAt > 0f && Time.unscaledTime >= _acceptAt) { _acceptAt = -1f; if (_popup != null) { if (_popupGame) General?.GotoLabel("/pop_gameInvite", "close_big", true); Reply(true); } }
            if (_declineAt > 0f && Time.unscaledTime >= _declineAt) { _declineAt = -1f; if (_popup != null) Reply(false); }
            UpdateRide();
            if (Passenger != 0 || _guest != null) UpdatePassengerAvatar(false);
        }

        float _acceptAt = -1f, _declineAt = -1f;
    }
}
