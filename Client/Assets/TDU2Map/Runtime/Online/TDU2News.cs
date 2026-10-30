using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace TDU2.Online
{
    /// <summary>
    /// News vom Server (im Web-Panel angelegt: Überschrift, Text, Symbol, Insel) für den News-Kanal von TDU Media.
    /// Abgleich: jede "status"-Antwort bringt newsLatest (höchste Id auf dem Server, TDU2GameData ruft <see cref="OnStatus"/>);
    /// ist sie anders als die bekannte, wird mit op "news" (since = bekannte Id) nachgeholt. Zusätzlich alle 2 min zur Sicherheit.
    /// Gelesen-Stand je Profil in den PlayerPrefs ("TDU2.News.Seen.&lt;nick&gt;").
    /// </summary>
    public static class TDU2News
    {
        /// <summary>Meldungen, neueste zuerst.</summary>
        public static readonly List<NewsEntry> Items = new List<NewsEntry>();
        /// <summary>Höchste bekannte Id (wie der Server sie in newsLatest meldet).</summary>
        public static int Latest { get; private set; }
        /// <summary>Neue oder geänderte Meldungen angekommen (Hauptthread).</summary>
        public static event Action Changed;

        static string _nick;
        static Task<NetResponse> _task;
        static float _nextFallback;
        static int _wantLatest = -1;

        /// <summary>Ungelesene Meldungen des News-Kanals (Id größer als die zuletzt dort gesehene; ohne Patch-Notes).</summary>
        public static int UnreadCount
        {
            get
            {
                int n = 0;
                foreach (var e in Items) if (!IsUpdate(e) && IsUnread(e)) n++;
                return n;
            }
        }

        /// <summary>Patch-Note für den Updates-Kanal (kind "update").</summary>
        public static bool IsUpdate(NewsEntry e) => e != null && string.Equals(e.kind, "update", StringComparison.OrdinalIgnoreCase);

        static string SeenKey => "TDU2.News.Seen." + (_nick ?? "").ToLowerInvariant();
        static string SeenUpdKey => "TDU2.Updates.Seen." + (_nick ?? "").ToLowerInvariant();
        static int Seen { get { try { return PlayerPrefs.GetInt(SeenKey, 0); } catch { return 0; } } }
        static int SeenUpd { get { try { return PlayerPrefs.GetInt(SeenUpdKey, 0); } catch { return 0; } } }

        /// <summary>Noch nicht gesehen (News: im News-Kanal, Patch-Notes: im Updates-Kanal).</summary>
        public static bool IsUnread(NewsEntry e) => e != null && e.id > (IsUpdate(e) ? SeenUpd : Seen);

        /// <summary>News-Kanal geöffnet: alle News (ohne Patch-Notes) als gelesen merken.</summary>
        public static void MarkSeen() => Mark(false);

        /// <summary>Updates-Kanal verlassen: alle Patch-Notes als gelesen merken.</summary>
        public static void MarkUpdatesSeen() => Mark(true);

        static void Mark(bool updates)
        {
            int max = 0;
            foreach (var e in Items) if (IsUpdate(e) == updates) max = Math.Max(max, e.id);
            try
            {
                if (updates) PlayerPrefs.SetInt(SeenUpdKey, Math.Max(SeenUpd, max)); else PlayerPrefs.SetInt(SeenKey, Math.Max(Seen, max));
                PlayerPrefs.Save();
            }
            catch { }
        }

        /// <summary>Aus der Statusabfrage: Server meldet höchste News-Id.</summary>
        public static void OnStatus(int serverLatest)
        {
            EnsureRunner();
            CheckProfile();
            if (serverLatest != Latest) _wantLatest = serverLatest;
        }

        static void CheckProfile()
        {
            string nick = TDU2Session.Nick;
            if (string.Equals(nick, _nick, StringComparison.OrdinalIgnoreCase)) return;
            _nick = nick; Items.Clear(); Latest = 0; _wantLatest = -1;
            if (!string.IsNullOrEmpty(nick)) _wantLatest = int.MaxValue;   // neues Profil: alles holen
        }

        static void Tick()
        {
            if (!TDU2Session.LoggedIn || TDU2Session.Client == null) return;
            CheckProfile();
            if (_task == null)
            {
                if (_wantLatest < 0 && Time.unscaledTime >= _nextFallback) _wantLatest = int.MaxValue;
                if (_wantLatest < 0) return;
                _nextFallback = Time.unscaledTime + 120f;
                _wantLatest = -1;
                // Ist die bekannte Id größer als die des Servers (gelöscht/Server neu), alles neu holen
                _task = TDU2Session.Client.Send(new NetRequest { op = "news", session = TDU2Session.SessionId, since = Latest });
                return;
            }
            if (!_task.IsCompleted) return;
            var r = _task.Status == TaskStatus.RanToCompletion ? _task.Result : null;
            _task = null;
            if (r == null || !r.ok) { if (r != null && r.error != "not_logged_in" && r.error != "unknown_op") Debug.LogWarning("[TDU2] News: " + r.error); return; }
            Apply(r);
        }

        static void Apply(NetResponse r)
        {
            bool changed = false;
            int added = 0;
            if (r.newsLatest < Latest)
            {
                // Server zurückgesetzt (news.json neu): alles verwerfen und von vorn holen
                Items.Clear(); Latest = 0; _wantLatest = int.MaxValue;
                try { Changed?.Invoke(); } catch (Exception) { }
                return;
            }
            if (r.newsIds != null)
            {
                var ids = new HashSet<int>(r.newsIds);
                if (Items.RemoveAll(e => !ids.Contains(e.id)) > 0) changed = true;
            }
            if (r.news != null)
                foreach (var e in r.news)
                {
                    if (e == null || Items.Exists(x => x.id == e.id)) continue;
                    Items.Add(e); added++; changed = true;
                }
            Items.Sort((a, b) => b.id.CompareTo(a.id));
            Latest = r.newsLatest;
            if (!changed) return;
            Debug.Log($"[TDU2] News: {Items.Count} Meldungen ({added} neu, {UnreadCount} ungelesen, höchste Id {Latest})");
            try { Changed?.Invoke(); } catch (Exception ex) { Debug.LogWarning("[TDU2] News: " + ex.Message); }
        }

        // ------------------------------------------------------------------ Bilder (op "newsimg", einzeln, im Speicher gehalten)
        static readonly Dictionary<int, Texture2D> _images = new Dictionary<int, Texture2D>();
        static readonly HashSet<int> _imageFailed = new HashSet<int>();
        static readonly Queue<int> _imageQueue = new Queue<int>();
        static Task<NetResponse> _imageTask;
        static int _imageTaskId;

        /// <summary>Bild einer Meldung; null = (noch) keins – wird dann im Hintergrund geholt, danach <see cref="Changed"/>.</summary>
        public static Texture2D Image(NewsEntry e)
        {
            if (e == null || !e.hasImage) return null;
            if (_images.TryGetValue(e.id, out var t) && t != null) return t;
            if (!_imageFailed.Contains(e.id) && _imageTaskId != e.id && !_imageQueue.Contains(e.id)) { EnsureRunner(); _imageQueue.Enqueue(e.id); }
            return null;
        }

        static void TickImages()
        {
            if (_imageTask == null)
            {
                if (_imageQueue.Count == 0 || !TDU2Session.LoggedIn || TDU2Session.Client == null) return;
                _imageTaskId = _imageQueue.Dequeue();
                _imageTask = TDU2Session.Client.Send(new NetRequest { op = "newsimg", session = TDU2Session.SessionId, target = _imageTaskId });
                return;
            }
            if (!_imageTask.IsCompleted) return;
            var r = _imageTask.Status == TaskStatus.RanToCompletion ? _imageTask.Result : null;
            int id = _imageTaskId;
            _imageTask = null; _imageTaskId = 0;
            if (r == null || !r.ok || string.IsNullOrEmpty(r.newsImage)) { _imageFailed.Add(id); Debug.LogWarning($"[TDU2] News-Bild {id}: {r?.error ?? "keine Antwort"}"); return; }
            try
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "News " + id, wrapMode = TextureWrapMode.Clamp };
                if (!tex.LoadImage(Convert.FromBase64String(r.newsImage))) { UnityEngine.Object.Destroy(tex); _imageFailed.Add(id); return; }
                _images[id] = tex;
                Debug.Log($"[TDU2] News-Bild {id}: {tex.width}x{tex.height}");
                Changed?.Invoke();
            }
            catch (Exception ex) { _imageFailed.Add(id); Debug.LogWarning($"[TDU2] News-Bild {id}: {ex.Message}"); }
        }

        // ------------------------------------------------------------------ Hauptthread-Taktgeber
        sealed class Runner : MonoBehaviour { void Update() { Tick(); TickImages(); } }
        static Runner _runner;

        static void EnsureRunner()
        {
            if (_runner != null) return;
            var go = new GameObject("TDU2 News");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideInHierarchy;
            _runner = go.AddComponent<Runner>();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            Items.Clear(); Latest = 0; _nick = null; _task = null; _wantLatest = -1; _runner = null; _nextFallback = 0f;
            _images.Clear(); _imageFailed.Clear(); _imageQueue.Clear(); _imageTask = null; _imageTaskId = 0;
        }
    }
}
