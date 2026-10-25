using System;
using System.Collections;
using System.IO;
using TDU2.Online;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Inselwechsel (Flug am Flughafen, oder Spielstand auf der anderen Insel beim Anmelden):
    ///  - Spielstand bekommt die Zielinsel (beim Flug ohne Position -> Ankunft am Flughafen der Zielinsel), wird gespeichert.
    ///  - Szene wird mit der Zielinsel neu geladen (<see cref="TDU2MapStreamer.IslandOverride"/>), die Sitzung (TDU2Session,
    ///    statisch) bleibt bestehen; TDU2Login setzt ohne Anmeldemenü direkt fort (<see cref="Pending"/>).
    ///  - Während des Ladens der Original-Ladebildschirm (TDU2LoadingScreen: Tipp-Bild aus tips_&lt;Sprache&gt;.bnk), bis die Welt
    ///    an der Ankunftsstelle aufgebaut und das Auto geladen ist.
    /// </summary>
    public sealed class TDU2IslandTravel : MonoBehaviour
    {
        /// <summary>Wechsel läuft: nach dem Neuladen ohne Anmeldemenü weitermachen.</summary>
        public static bool Pending { get; private set; }
        /// <summary>Flug: Ankunft am Flughafen-Spot (EAIR*) der Zielinsel statt an der gespeicherten Position.</summary>
        public static bool Arriving { get; private set; }
        public static string Target { get; private set; }
        /// <summary>Spot-Präfix des Ankunftsorts beim Flug (EAIR = Flughafen, ELAND = Landeplatz Casino).</summary>
        public static string ArrivalPrefix { get; private set; } = "EAIR";

        static TDU2IslandTravel _inst;
        string _text;
        float _fade = 1f;
        bool _done;

        /// <summary>Inselordner (Bnk/islands/&lt;name&gt;) vorhanden?</summary>
        public static bool CanTravelTo(string island, string bnkDir)
        {
            if (string.IsNullOrEmpty(island) || string.IsNullOrEmpty(bnkDir)) return false;
            try { return Directory.Exists(Path.Combine(bnkDir, "islands", island.ToLowerInvariant())); } catch (Exception) { return false; }
        }

        public static string DisplayName(string island) =>
            string.IsNullOrEmpty(island) ? "?" : island.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? "Hawaii" : island.StartsWith("i", StringComparison.OrdinalIgnoreCase) ? "Ibiza"
            : island.StartsWith("cas", StringComparison.OrdinalIgnoreCase) ? "Casino" : island;

        /// <summary>Andere Insel zum Flug (Ibiza ↔ Hawaii).</summary>
        public static string OtherIsland(string current) =>
            !string.IsNullOrEmpty(current) && current.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? "ibiza" : "hawai";

        /// <summary>Wechsel starten. flight = true: Ankunft am Flughafen (Position im Spielstand wird verworfen).</summary>
        public static void Go(string island, bool flight, string carFile = null, string arrivalPrefix = "EAIR")
        {
            if (Pending || string.IsNullOrEmpty(island)) return;
            Target = island.ToLowerInvariant();
            ArrivalPrefix = string.IsNullOrEmpty(arrivalPrefix) ? "EAIR" : arrivalPrefix;
            Pending = true;
            Arriving = flight;
            TDU2MapStreamer.IslandOverride = Target;
            var save = TDU2Session.Save;
            if (save != null)
            {
                save.island = Target;
                if (flight) save.hasPosition = false;
                if (!string.IsNullOrEmpty(carFile)) save.currentCar = carFile;
                if (TDU2Session.LoggedIn) TDU2Session.SaveAsync();
            }
            TDU2DebugLog.Event("Inselwechsel nach " + Target + (flight ? " (Flug)" : " (Spielstand)") + ", Auto " + (carFile ?? save?.currentCar ?? "-"));
            Debug.Log("[TDU2] Inselwechsel nach " + DisplayName(Target) + (flight ? " (Flug)" : ""));
            var go = new GameObject("TDU2 IslandTravel");
            DontDestroyOnLoad(go);
            _inst = go.AddComponent<TDU2IslandTravel>();
            _inst._text = flight ? "Flug nach " + DisplayName(Target) + " …" : "Lade " + DisplayName(Target) + " …";
            // Ladebildschirm bis zur fertigen Welt auf der Zielinsel (gespeichert wird oben schon)
            TDU2LoadingScreen.Show("Inselwechsel nach " + DisplayName(Target), () => !Pending && TDU2LoadingScreen.WorldReady(), 2f, false);
            _inst.StartCoroutine(_inst.Reload());
        }

        /// <summary>Von TDU2Login nach dem Fortsetzen aufgerufen.</summary>
        public static void Done()
        {
            Pending = false; Arriving = false; ArrivalPrefix = "EAIR";
            if (_inst != null) _inst._done = true;
        }

        IEnumerator Reload()
        {
            float until = Time.unscaledTime + 1.2f;
            while (Time.unscaledTime < until) yield return null;   // Überblendung zeigen, Speichern anstoßen
            Time.timeScale = 1f;
            var scene = SceneManager.GetActiveScene();
#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(scene.path, new LoadSceneParameters(LoadSceneMode.Single));
#else
            if (scene.buildIndex >= 0) SceneManager.LoadScene(scene.buildIndex);
            else SceneManager.LoadScene(scene.name);
#endif
        }

        void Update()
        {
            if (_done)
            {
                _fade -= Time.unscaledDeltaTime * 0.8f;
                if (_fade <= 0f) { if (_inst == this) _inst = null; Destroy(gameObject); }
            }
        }

        GUIStyle _style;

        void OnGUI()
        {
            if (TDU2LoadingScreen.Visible) return;   // Original-Ladebildschirm deckt alles ab
            GUI.depth = -1000;
            float a = Mathf.Clamp01(_fade);
            GUI.color = new Color(0f, 0f, 0f, a);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            if (_style == null) _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
            _style.fontSize = Mathf.RoundToInt(Screen.height / 720f * 28f);
            _style.normal.textColor = new Color(1f, 0.85f, 0.6f, a);
            GUI.color = Color.white;
        }
    }
}
