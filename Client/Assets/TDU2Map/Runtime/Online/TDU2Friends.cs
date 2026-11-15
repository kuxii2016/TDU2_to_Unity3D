using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using TDU2.Flash;
using TDU2.Interior;
using TDU2.UI;
using TDU2.Vehicles;
using UnityEngine;

namespace TDU2.Online
{
    /// <summary>Beziehung zu einem Mitspieler – bestimmt u.a. die Farbe seines Punkts auf Minimap/Karte.</summary>
    public enum PlayerRelation { Normal = 0, Friend = 1, Club = 2 }

    /// <summary>
    /// Farben der Mitspieler wie im Spiel (runder Punkt auf der Minimap, wie die Gegner im Rennen):
    /// Weiß = normaler Spieler, Grün = Freund, Orange = Club-Mitglied. Zentral, damit Karte, Listen usw. dieselben Farben nutzen.
    /// </summary>
    public static class PlayerColors
    {
        public static Color Normal = new Color(1f, 1f, 1f, 1f);
        public static Color Friend = new Color(0.36f, 0.86f, 0.22f, 1f);
        public static Color Club = new Color(1f, 0.55f, 0.08f, 1f);
        /// <summary>Gegner im Rennen (KI) – wie normale Spieler weiß.</summary>
        public static Color Opponent = new Color(1f, 1f, 1f, 1f);

        public static Color Of(PlayerRelation r) => r == PlayerRelation.Friend ? Friend : r == PlayerRelation.Club ? Club : Normal;
    }

    /// <summary>
    /// Freundesliste wie im Spiel (Server: op "friend", gespeichert in data/friends.json):
    ///   * Anfrage senden: Spielerkarte "frre" bzw. "Nach Spieler suchen" (psch:vali – im Original genau dafür; Erfolg = Dialog
    ///     search_player_ok "Freundschaftsanfrage gesendet").
    ///   * Empfänger: general.bnk /pop_request (RequestType "friend", Text /:mpc_FriendRequest "RIBOS möchte dein Freund sein"):
    ///     open_mini -> Enter open_big -> Enter valid_request ("frok") bzw. Rücktaste cancel_request ("frca").
    ///   * Entfernen: Spielerkarte "delf" (nach dem Bestätigungsdialog remove_friend).
    /// Die Liste füllt <see cref="TDU2GameData.Friends"/> (Pausemenü "Freunde"); <see cref="RelationOf"/> färbt die Minimap-Punkte.
    /// </summary>
    [AddComponentMenu("TDU2/Freundesliste")]
    public sealed class TDU2Friends : MonoBehaviour
    {
        public static TDU2Friends Instance { get; private set; }

        [Tooltip("Freundesliste regelmäßig neu holen (Sekunden) – Online-Status der Freunde")]
        public float RefreshSeconds = 30f;
        [Tooltip("Anfrage-Fenster so lange zeigen (Sekunden); unbeantwortet bleibt die Anfrage gespeichert und kommt später wieder")]
        public float PopupSeconds = 25f;
        [Tooltip("Unbeantwortete Anfrage frühestens nach so vielen Sekunden erneut zeigen")]
        public float ReshowSeconds = 180f;

        readonly HashSet<string> _friends = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _pendingOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> _incoming = new List<string>();
        readonly Dictionary<string, float> _shownAt = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        public static TDU2Friends Ensure(GameObject host)
        {
            if (Instance != null) return Instance;
            var f = host.GetComponent<TDU2Friends>() ?? host.AddComponent<TDU2Friends>();
            Instance = f;
            return f;
        }

        void Awake() { if (Instance == null) Instance = this; }
        void OnDestroy() { if (Instance == this) Instance = null; }

        static TDU2GameData GD => TDU2GameData.Instance;
        static VmfPlayer General => GD != null ? GD.General : null;

        // ------------------------------------------------------------------ Abfragen
        public bool IsFriend(string nick) => !string.IsNullOrEmpty(nick) && _friends.Contains(nick);
        public bool RequestSentTo(string nick) => !string.IsNullOrEmpty(nick) && _pendingOut.Contains(nick);
        public int IncomingCount => _incoming.Count;

        /// <summary>Beziehung zu einem Spieler (Club vor Freund – wie im Spiel).</summary>
        public static PlayerRelation RelationOf(string nick)
        {
            if (string.IsNullOrEmpty(nick)) return PlayerRelation.Normal;
            var gd = GD;
            if (gd != null)
                foreach (var c in gd.Club) if (c != null && string.Equals(c.Nick, nick, StringComparison.OrdinalIgnoreCase)) return PlayerRelation.Club;
            return Instance != null && Instance.IsFriend(nick) ? PlayerRelation.Friend : PlayerRelation.Normal;
        }

        public static Color ColorOf(string nick) => PlayerColors.Of(RelationOf(nick));

        // ------------------------------------------------------------------ Server
        Task<NetResponse> _listReq;
        float _nextRefresh;
        bool _wasLoggedIn;
        readonly List<(Task<NetResponse> task, string kind, string nick, Action<NetResponse> done)> _ops = new List<(Task<NetResponse>, string, string, Action<NetResponse>)>();

        /// <summary>Liste neu holen (sofort, wenn keine Abfrage läuft).</summary>
        public void Refresh()
        {
            if (!TDU2Session.LoggedIn || TDU2Session.Client == null || _listReq != null) { _nextRefresh = 0f; return; }
            _listReq = TDU2Session.Client.Send(new NetRequest { op = "friend", session = TDU2Session.SessionId, kind = "list" });
            _nextRefresh = Time.unscaledTime + RefreshSeconds;
        }

        /// <summary>Freundschaftsanfrage senden. done bekommt die Antwort (ok, friendResult bzw. error) oder null (keine Verbindung).</summary>
        public void Request(string nick, Action<NetResponse> done) => Send("request", nick, done);
        public void Remove(string nick, Action<NetResponse> done) => Send("remove", nick, done);
        public void Answer(string nick, bool accept) => Send(accept ? "accept" : "decline", nick, null);

        void Send(string kind, string nick, Action<NetResponse> done)
        {
            if (!TDU2Session.LoggedIn || TDU2Session.Client == null || string.IsNullOrEmpty(nick)) { done?.Invoke(null); return; }
            var t = TDU2Session.Client.Send(new NetRequest { op = "friend", session = TDU2Session.SessionId, kind = kind, nick = nick });
            _ops.Add((t, kind, nick, done));
        }

        /// <summary>Status vom Server: Meldungen friendreq/friendok/frienddecl/friendrm -> Liste neu holen, Hinweis im Chat.</summary>
        public void OnStatus(NetResponse r)
        {
            if (r?.notices == null) return;
            bool refresh = false;
            foreach (var n in r.notices)
            {
                if (string.IsNullOrEmpty(n) || !n.StartsWith("friend", StringComparison.Ordinal)) continue;
                int i = n.IndexOf(':');
                if (i < 0) continue;
                string what = n.Substring(0, i), nick = n.Substring(i + 1);
                refresh = true;
                switch (what)
                {
                    case "friendok": AddNews(NewsAccepted, nick); break;                 // meine Anfrage wurde angenommen
                    case "frienddecl": Log(nick + " hat die Freundschaftsanfrage abgelehnt"); break;   // im Spiel ohne Meldung
                    case "friendrm": Log(nick + " hat die Freundschaft beendet"); break;              // im Spiel ohne Meldung
                    case "friendreq": _shownAt.Remove(nick); break;   // neue Anfrage -> sofort zeigen (Meldung beim Eintreffen in der Liste)
                }
            }
            if (refresh) Refresh();
        }

        void Update()
        {
            bool li = TDU2Session.LoggedIn;
            if (li && !_wasLoggedIn) Refresh();   // nach dem Anmelden: Liste und offene Anfragen holen
            if (!li && _wasLoggedIn) Clear();
            _wasLoggedIn = li;
            if (li && _listReq == null && Time.unscaledTime >= _nextRefresh) Refresh();
            PollList();
            PollOps();
            UpdatePopup();
        }

        void Clear()
        {
            _friends.Clear(); _pendingOut.Clear(); _incoming.Clear(); _shownAt.Clear();
            GD?.Friends.Clear();
            ClosePopup(false);
        }

        void PollList()
        {
            if (_listReq == null || !_listReq.IsCompleted) return;
            var r = _listReq.Status == TaskStatus.RanToCompletion ? _listReq.Result : null;
            _listReq = null;
            if (r != null && r.ok) Apply(r);
        }

        void PollOps()
        {
            for (int i = _ops.Count - 1; i >= 0; i--)
            {
                var op = _ops[i];
                if (!op.task.IsCompleted) continue;
                _ops.RemoveAt(i);
                var r = op.task.Status == TaskStatus.RanToCompletion ? op.task.Result : null;
                if (r != null && (r.friends != null || r.friendRequests != null) && (r.ok || r.error == "no_request")) Apply(r);
                if (r != null && r.ok)
                {
                    if (op.kind == "request")
                    {
                        if (r.friendResult == "became_friends") AddNews(NewsNewFriend, op.nick);
                        else if (r.friendResult == "sent") AddNews(NewsSent, op.nick);
                        else Log(op.nick + ": " + r.friendResult);
                    }
                    else if (op.kind == "accept") AddNews(NewsNewFriend, op.nick);
                    else Log(op.kind + " " + op.nick + " ok");
                }
                else Log(op.kind + " " + op.nick + ": " + ErrorText(r?.error));
                try { op.done?.Invoke(r); } catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        static string ErrorText(string e)
        {
            switch (e)
            {
                case "unknown_player": return "Spieler nicht gefunden.";
                case "self": return "Du kannst dich nicht selbst hinzufügen.";
                case "full": return "Die Freundesliste ist voll.";
                case null: case "": return "keine Verbindung zum Server.";
                default: return e;
            }
        }

        void Apply(NetResponse r)
        {
            var gd = GD;
            _friends.Clear();
            _pendingOut.Clear();
            if (r.friendPending != null) foreach (var n in r.friendPending) if (!string.IsNullOrEmpty(n)) _pendingOut.Add(n);
            var db = FindDb();
            var old = new Dictionary<string, PlayerInfo>(StringComparer.OrdinalIgnoreCase);
            if (gd != null) foreach (var p in gd.Friends) if (p != null && !string.IsNullOrEmpty(p.Nick)) old[p.Nick] = p;
            var list = new List<PlayerInfo>();
            foreach (var e in r.friends ?? new List<FriendEntry>())
            {
                if (e == null || string.IsNullOrEmpty(e.nick)) continue;
                _friends.Add(e.nick);
                if (!old.TryGetValue(e.nick, out var p)) p = new PlayerInfo { Nick = e.nick };
                // online: denselben Eintrag wie in der Spielerliste benutzen (Position, Foto, Auto)
                if (e.online && gd != null)
                    foreach (var o in gd.Players.Values) if (string.Equals(o.Nick, e.nick, StringComparison.OrdinalIgnoreCase)) { p = o; break; }
                p.Online = e.online;
                if (e.id != 0) p.Id = e.id; else if (!e.online) p.Id = 0;
                if (e.nationality > 0) p.Nationality = e.nationality;
                if (e.carId != 0) { p.CarId = e.carId; p.Color = e.color; }
                if (e.money != 0) p.Money = e.money;
                if (e.nbCars != 0) p.NbCars = e.nbCars;
                if (e.online) { p.PhotoVer = e.photoVer; if (!string.IsNullOrEmpty(e.mood)) p.Mood = e.mood; }
                if (!string.IsNullOrEmpty(e.lastSeen) && DateTime.TryParse(e.lastSeen, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var seen))
                    p.LastSeen = seen.Kind == DateTimeKind.Utc ? seen.ToLocalTime() : seen;
                if (p.Car == null && db != null && db.ById.TryGetValue(p.CarId, out var c)) p.Car = c;
                list.Add(p);
            }
            // online zuerst, dann nach Name
            list.Sort((a, b) => a.Online != b.Online ? (a.Online ? -1 : 1) : string.Compare(a.Nick, b.Nick, StringComparison.OrdinalIgnoreCase));
            if (gd != null) { gd.Friends.Clear(); gd.Friends.AddRange(list); }

            var now = new List<string>();
            foreach (var e in r.friendRequests ?? new List<FriendEntry>()) if (e != null && !string.IsNullOrEmpty(e.nick)) now.Add(e.nick);
            foreach (var n in now) if (!_incoming.Exists(x => string.Equals(x, n, StringComparison.OrdinalIgnoreCase))) AddNews(NewsIncoming, n);
            _incoming.Clear(); _incoming.AddRange(now);
            if (_popupNick != null && !_incoming.Exists(x => string.Equals(x, _popupNick, StringComparison.OrdinalIgnoreCase))) ClosePopup(false);
            gd?.NotifyChanged();
        }

        static CarDatabase FindDb()
        {
#if UNITY_2023_1_OR_NEWER
            var m = FindFirstObjectByType<TDU2VehicleManager>();
#else
            var m = FindObjectOfType<TDU2VehicleManager>();
#endif
            return m != null ? m.Database : null;
        }

        static void Log(string text) => Debug.Log("Freunde: " + text);

        // ------------------------------------------------------------------ Meldungen für TDU Media (Reiter "Freunde & Klub")
        // Texte aus der Spieldatenbank (db_<sprache>.cpr), wie im Spiel; für Ablehnen/Entfernen gibt es dort keine Meldung.
        public const ulong TxtFriendRequest = 1663;     // "Freundesanfrage"
        public const ulong TxtWantsFriend = 1464;       // "§PLAYER§ möchte Ihr Freund werden."
        public const ulong TxtRequestSent = 7516;       // "Freundschaftsanfrage an §PLAYERNAME§ gesendet."
        public const ulong TxtNewFriendName = 7519;     // "Neuer Freund: §FRIENDNAME§"
        public const ulong TxtAccepted = 7520;          // "§FRIENDNAME§ hat deine Freundschaftsanfrage angenommen."
        public const ulong TxtNewFriend = 3399;         // "Neuer Freund"

        const int NewsIncoming = 0, NewsSent = 1, NewsAccepted = 2, NewsNewFriend = 3;

        [Serializable] public sealed class FriendNews { public int kind; public string nick = ""; public string date = ""; public bool read; }
        [Serializable] sealed class FriendNewsList { public List<FriendNews> list = new List<FriendNews>(); }

        /// <summary>Meldung als Text (Titel, Inhalt) in der eingestellten Sprache.</summary>
        public static void NewsText(FriendNews n, out string title, out string text)
        {
            string nick = n?.nick ?? "";
            string T(ulong hc, string fb) => TDU2UIManager.GetLangString(hc, fb)
                .Replace("§PLAYER§", nick).Replace("§PLAYERNAME§", nick).Replace("§FRIENDNAME§", nick);
            switch (n?.kind ?? 0)
            {
                case NewsSent: title = T(TxtFriendRequest, "Friend request"); text = T(TxtRequestSent, "Friend request sent to §PLAYERNAME§."); break;
                case NewsAccepted: title = T(TxtNewFriendName, "New friend: §FRIENDNAME§"); text = T(TxtAccepted, "§FRIENDNAME§ has accepted your friend request."); break;
                case NewsNewFriend: title = T(TxtNewFriend, "New friend"); text = T(TxtNewFriendName, "New friend: §FRIENDNAME§"); break;
                default: title = T(TxtFriendRequest, "Friend request"); text = T(TxtWantsFriend, "§PLAYER§ wants to be your friend"); break;
            }
        }

        public static DateTime NewsDate(FriendNews n) =>
            n != null && DateTime.TryParse(n.date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime() : DateTime.Now;

        const int MaxNews = 30;
        FriendNewsList _news;
        string _newsNick;
        string NewsKey => "TDU2.FriendNews." + (TDU2Session.Nick ?? "").ToLowerInvariant();

        FriendNewsList NewsList
        {
            get
            {
                string nick = TDU2Session.Nick ?? "";
                if (_news != null && _newsNick == nick) return _news;
                _newsNick = nick; _news = null;
                try { var s = PlayerPrefs.GetString(NewsKey, ""); if (!string.IsNullOrEmpty(s)) _news = JsonUtility.FromJson<FriendNewsList>(s); }
                catch (Exception) { }
                return _news ??= new FriendNewsList();
            }
        }

        /// <summary>Meldungen, neueste zuerst.</summary>
        public IReadOnlyList<FriendNews> News => NewsList.list;
        public bool HasUnreadNews { get { foreach (var n in NewsList.list) if (!n.read) return true; return false; } }

        public void MarkNewsRead()
        {
            bool ch = false;
            foreach (var n in NewsList.list) if (!n.read) { n.read = true; ch = true; }
            if (ch) SaveNews();
        }

        void AddNews(int kind, string nick)
        {
            if (string.IsNullOrEmpty(nick) || string.IsNullOrEmpty(TDU2Session.Nick)) return;
            var l = NewsList.list;
            // dieselbe Meldung nicht doppelt (z.B. Anfrage, die beim nächsten Anmelden noch offen ist)
            if (kind == NewsIncoming && l.Exists(x => x.kind == NewsIncoming && string.Equals(x.nick, nick, StringComparison.OrdinalIgnoreCase))) return;
            l.Insert(0, new FriendNews { kind = kind, nick = nick, date = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) });
            if (l.Count > MaxNews) l.RemoveRange(MaxNews, l.Count - MaxNews);
            NewsText(l[0], out var t, out var x);
            Log(t + " – " + x);
            SaveNews();
            NewsChanged?.Invoke();
        }

        /// <summary>Neue Meldung (TDU Media aktualisiert eine offene Liste).</summary>
        public static event Action NewsChanged;

        void SaveNews()
        {
            try { PlayerPrefs.SetString(NewsKey, JsonUtility.ToJson(NewsList)); PlayerPrefs.Save(); } catch (Exception) { }
        }

        // ------------------------------------------------------------------ Anfrage-Fenster (general.bnk /pop_request)
        string _popupNick;
        bool _popupBig;
        float _popupUntil, _bigAt;
        VmfPlayer _hooked;
        string _reqTemplate;

        bool CanShowPopup()
        {
            var g = General;
            if (g == null || Time.timeScale <= 0f || TDU2BigMap.IsOpen || TDU2GameData.ChatTyping) return false;
            if (TDU2Party.Instance != null && TDU2Party.Instance.PopupOpen) return false;
            if (TDU2ClothesShop.Busy || TDU2Hairdresser.Busy || TDU2StickerShop.Busy) return false;
            if (Cinematics.TDU2Cutscenes.IsPlaying) return false;
            return g.Find("/pop_request") != null;
        }

        void UpdatePopup()
        {
            if (_popupNick == null)
            {
                if (_incoming.Count == 0 || !CanShowPopup()) return;
                foreach (var n in _incoming)
                {
                    if (_shownAt.TryGetValue(n, out float at) && Time.unscaledTime - at < ReshowSeconds) continue;
                    ShowPopup(n);
                    break;
                }
                return;
            }
            var g = General;
            if (g == null) { ClosePopup(false); return; }
            if (Time.unscaledTime > _popupUntil) { ClosePopup(true); return; }   // unbeantwortet: bleibt gespeichert
#if ENABLE_LEGACY_INPUT_MANAGER
            if (TDU2GameData.ChatTyping || Time.timeScale <= 0f) return;
            if (TDU2Controls.ConfirmPressed())
            {
                if (!_popupBig) { OpenBig(); if (!g.CallClip("/pop_request", "open_big")) g.GotoLabel("/pop_request", "req_friend", true); }
                else if (Time.unscaledTime - _bigAt >= 0.5f)   // erst ansehen, dann (zweites Enter) annehmen
                {
                    if (!g.CallClip("/pop_request", "valid_request")) Reply(true);
                }
            }
            else if (Input.GetKeyDown(KeyCode.Backspace) || TDU2Nav.PadBack())
            {
                if (!g.CallClip("/pop_request", "cancel_request")) Reply(false);
            }
#endif
        }

        void ShowPopup(string nick)
        {
            var g = General;
            Hook(g);
            _popupNick = nick; _popupBig = false; _bigAt = -1f;
            _popupUntil = Time.unscaledTime + PopupSeconds;
            _shownAt[nick] = Time.unscaledTime;
            // Kleines Fenster nur per Tastatur/Pad (ein beliebiger Mausklick sollte nichts annehmen), Maus erst in der großen Ansicht
            g.MouseIgnore.Add("/pop_request");
            g.Show("/pop_request");
            g.SetVariable("/:RequestType", "friend");
            g.SetValue("/:RequestForbidden", 0);
            g.SetVariable("/:AvatarName", nick);
            if (_reqTemplate == null) _reqTemplate = g.GetVariable("/:mpc_FriendRequest") ?? "";
            g.SetVariable("/:mpc_FriendRequest", FillNick(_reqTemplate, nick));
            g.GotoLabel("/pop_request", "open_mini", true);
            Log(nick + " möchte befreundet sein (Fenster offen)");
        }

        void OpenBig()
        {
            _popupBig = true; _bigAt = Time.unscaledTime;
            General?.MouseIgnore.Remove("/pop_request");
        }

        /// <summary>Spielername in "_RIBOS wants to be your friend." (Lokalisierung) einsetzen.</summary>
        static string FillNick(string template, string nick)
        {
            if (string.IsNullOrEmpty(template)) return nick + " möchte dein Freund sein.";
            foreach (var tok in new[] { "RIBOS", "Ribos", "%s", "%1$s", "%1", "[NAME]", "<NAME>", "{0}" })
                if (template.Contains(tok)) return template.Replace(tok, nick);
            return nick + ": " + template;
        }

        void Hook(VmfPlayer g)
        {
            if (g == _hooked) return;
            if (_hooked != null) _hooked.FSCommand -= OnGeneralCommand;
            _hooked = g;
            if (g != null) g.FSCommand += OnGeneralCommand;
        }

        void OnGeneralCommand(string cmd, string args)
        {
            if (_popupNick == null) return;
            switch ((cmd ?? "").Trim().ToLowerInvariant())
            {
                case "gein": case "opbg": if (!_popupBig) OpenBig(); break;
                case "frok":
                    if (_popupBig && Time.unscaledTime - _bigAt >= 0.5f) Reply(true);
                    else { var g = General; g?.Show("/pop_request"); g?.GotoLabel("/pop_request", _popupBig ? "req_friend" : "open_mini", true); }
                    break;
                case "frca": Reply(false); break;
            }
        }

        void Reply(bool accept)
        {
            string nick = _popupNick;
            _popupNick = null;
            General?.MouseIgnore.Remove("/pop_request");
            if (nick == null) return;
            _incoming.RemoveAll(x => string.Equals(x, nick, StringComparison.OrdinalIgnoreCase));
            Answer(nick, accept);
            if (!accept) Log("Anfrage von " + nick + " abgelehnt");
        }

        void ClosePopup(bool animate)
        {
            if (_popupNick == null) return;
            var g = General;
            if (g != null)
            {
                g.MouseIgnore.Remove("/pop_request");
                if (animate) g.GotoLabel("/pop_request", _popupBig ? "close_friend" : "close_mini", true);
            }
            _popupNick = null;
        }
    }
}
