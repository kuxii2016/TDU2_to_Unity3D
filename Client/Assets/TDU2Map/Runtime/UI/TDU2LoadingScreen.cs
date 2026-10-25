using System;
using System.Collections.Generic;
using System.IO;
using TDU2.IO;
using TDU2.Online;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Ladebildschirm wie im Spiel bei Szenenwechseln (Interieur betreten/verlassen, Raumwechsel Garage/Wohnbereich, Inselwechsel):
    ///   * Bild aus frontend/tips_&lt;Sprache&gt;.bnk (slideshow.xml: Tips_Progress, Tips_titles, car_category) – je Ladevorgang ein
    ///     zufälliges, es wird nicht weitergeblättert.
    ///   * Beim Einblenden wird gespeichert (TDU2Session.SaveAsync); das Speicher-Symbol (general.vmf saving_logo, TDU2GameData)
    ///     liegt über dem Bild (General-Film GuiDepth -30, Ladebild -25).
    ///   * Ausgeblendet wird erst, wenn die Szene fertig ist (Bedingung des Aufrufers, z.B. Interieur samt Autos bzw. Welt rund um die
    ///     Kamera gebaut, Auto geladen), das Speichern fertig ist und eine Mindestzeit vergangen ist – man sieht den Aufbau nicht.
    /// Bleibt über das Neuladen der Szene beim Inselwechsel erhalten (DontDestroyOnLoad).
    /// </summary>
    public sealed class TDU2LoadingScreen : MonoBehaviour
    {
        /// <summary>Höchstdauer, falls eine Bedingung nie erfüllt wird (Sekunden).</summary>
        public static float MaxSeconds = 60f;
        /// <summary>Ordner Euro/Bnk (für tips_&lt;Sprache&gt;.bnk); wird beim ersten Aufruf vom Fahrzeug-Manager übernommen.</summary>
        public static string BnkDir;

        static TDU2LoadingScreen _inst;
        Texture2D _tex;
        readonly List<Func<bool>> _ready = new List<Func<bool>>();
        float _shownAt, _minUntil, _alpha;
        bool _closing;

        /// <summary>Ladebildschirm ist (noch) zu sehen.</summary>
        public static bool Visible => _inst != null && !_inst._closing;
        /// <summary>Ein Originalbild ist geladen (sonst nur Schwarz).</summary>
        public static bool HasImage => _inst != null && _inst._tex != null;

        /// <summary>
        /// Ladebildschirm zeigen (läuft er schon, kommt die Bedingung dazu). <paramref name="ready"/> = Szene fertig;
        /// <paramref name="save"/> = Spielstand jetzt speichern (mit Speicher-Symbol).
        /// </summary>
        public static void Show(string reason, Func<bool> ready, float minSeconds = 1.2f, bool save = true, string bnkDir = null)
        {
            if (!string.IsNullOrEmpty(bnkDir)) BnkDir = bnkDir;
            if (_inst == null || _inst._closing)
            {
                if (_inst != null) Destroy(_inst.gameObject);
                var go = new GameObject("TDU2 Ladebildschirm");
                DontDestroyOnLoad(go);
                _inst = go.AddComponent<TDU2LoadingScreen>();
                _inst._shownAt = Time.unscaledTime;
                _inst._alpha = 1f;   // sofort deckend: der Abbau/Aufbau der Szene soll nicht zu sehen sein
                _inst._tex = LoadRandomImage();
            }
            var s = _inst;
            if (ready != null) s._ready.Add(ready);
            s._minUntil = Mathf.Max(s._minUntil, Time.unscaledTime + minSeconds);
            if (save && TDU2Session.LoggedIn && !TDU2Session.Saving)
            {
                try { TDU2Session.SaveAsync(); } catch (Exception ex) { Debug.LogWarning("[TDU2] Ladebildschirm: Speichern: " + ex.Message); }
            }
        }

        static TDU2MapStreamer _streamer;
        static TDU2VehicleManager _man;
        static float _nextFind;

        /// <summary>
        /// Welt rund um die Kamera fertig (Areas/Sektoren im Umkreis gebaut, kein Auto mehr im Laden) – Bedingung für das
        /// Ausblenden nach dem Verlassen eines Interieurs bzw. nach dem Inselwechsel.
        /// </summary>
        public static bool WorldReady(float radius = 350f)
        {
            if ((_streamer == null || _man == null) && Time.unscaledTime >= _nextFind)
            {
                _nextFind = Time.unscaledTime + 0.5f;
#if UNITY_2023_1_OR_NEWER
                if (_streamer == null) _streamer = FindFirstObjectByType<TDU2MapStreamer>();
#else
                if (_streamer == null) _streamer = FindObjectOfType<TDU2MapStreamer>();
#endif
                if (_man == null) _man = FindManager();
            }
            var cam = Camera.main;
            if (_streamer == null || cam == null) return false;
            if (_man != null && _man.IsLoadingCar) return false;
            return _streamer.ReadyAround(cam.transform.position, radius);
        }

        /// <summary>Sofort ausblenden (z.B. Abbruch).</summary>
        public static void Hide() { if (_inst != null) _inst._closing = true; }

        static TDU2VehicleManager FindManager()
        {
#if UNITY_2023_1_OR_NEWER
            return FindFirstObjectByType<TDU2VehicleManager>();
#else
            return FindObjectOfType<TDU2VehicleManager>();
#endif
        }

        /// <summary>Ein zufälliges Bild aus frontend/tips_&lt;Sprache&gt;.bnk (Fallback tips_us), Reihenfolge laut slideshow.xml.</summary>
        static Texture2D LoadRandomImage()
        {
            try
            {
                string dir = BnkDir;
                if (string.IsNullOrEmpty(dir)) { var m = FindManager(); if (m != null) dir = BnkDir = m.BnkDir; }
                if (string.IsNullOrEmpty(dir)) return null;
                string lang = TDU2PauseMenu.SavedLanguage();
                string path = null;
                foreach (var l in new[] { lang, "us", "ge" })
                {
                    if (string.IsNullOrEmpty(l)) continue;
                    var p = Path.Combine(dir, "frontend", "tips_" + l + ".bnk");
                    if (File.Exists(p)) { path = p; break; }
                }
                if (path == null) return null;
                var bank = BnkArchive.Open(path);
                var imgs = new List<BnkEntry>();
                foreach (var e in bank.Entries) if (e.Extension == ".jpg" || e.Extension == ".png") imgs.Add(e);
                if (imgs.Count == 0) return null;
                var pick = imgs[UnityEngine.Random.Range(0, imgs.Count)];
                var tex = new Texture2D(2, 2, TextureFormat.RGB24, false) { name = "Ladebild " + pick.Name, wrapMode = TextureWrapMode.Clamp };
                if (!ImageConversion.LoadImage(tex, bank.Read(pick), true)) { Destroy(tex); return null; }
                return tex;
            }
            catch (Exception ex) { Debug.LogWarning("[TDU2] Ladebildschirm: " + ex.Message); return null; }
        }

        bool Ready()
        {
            foreach (var r in _ready)
            {
                bool ok;
                try { ok = r(); } catch (Exception) { ok = true; }
                if (!ok) return false;
            }
            return true;
        }

        void Update()
        {
            float t = Time.unscaledTime;
            if (!_closing && t >= _minUntil && !TDU2Session.Saving && Ready()) _closing = true;
            if (!_closing && t - _shownAt > MaxSeconds) { _closing = true; Debug.LogWarning("[TDU2] Ladebildschirm: Zeitüberschreitung"); }
            _alpha = Mathf.MoveTowards(_alpha, _closing ? 0f : 1f, Time.unscaledDeltaTime / (_closing ? 0.35f : 0.12f));
            // Speicher-Symbol (General-Film) über dem Ladebild halten
            var g = TDU2GameData.Instance != null ? TDU2GameData.Instance.General : null;
            if (g != null) g.GuiDepth = _alpha > 0.01f ? -2001 : -30;
            if (_closing && _alpha <= 0f) Destroy(gameObject);
        }

        void OnGUI()
        {
            // ganz vorn (vor HUD/Tacho, Minimap, Menüs): darunter baut sich alles in Ruhe auf. Die Tiefe muss in JEDEM Ereignis
            // gesetzt werden (vorher nur beim Zeichnen -> Unity sortierte mit Tiefe 0, Tacho/HUD lagen davor)
            GUI.depth = -2000;
            if (Event.current.type != EventType.Repaint) return;
            var full = new Rect(0, 0, Screen.width, Screen.height);
            var c = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, _alpha);
            GUI.DrawTexture(full, Texture2D.whiteTexture);
            if (_tex != null)
            {
                GUI.color = new Color(1f, 1f, 1f, _alpha);
                GUI.DrawTexture(full, _tex, ScaleMode.ScaleToFit, false);
            }
            GUI.color = c;
        }

        void OnDestroy()
        {
            var g = TDU2GameData.Instance != null ? TDU2GameData.Instance.General : null;
            if (g != null) g.GuiDepth = -30;
            if (_tex != null) Destroy(_tex);
            if (_inst == this) _inst = null;
        }
    }
}
