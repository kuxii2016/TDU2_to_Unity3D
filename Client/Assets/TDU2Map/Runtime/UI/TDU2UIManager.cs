using System;
using System.Collections;
using System.Globalization;
using TDU2.Flash;
using TDU2.Online;
using TDU2.Racing;
using TDU2.Vehicles;
using UnityEngine;

/// <summary>
/// Zentrale Anlaufstelle für die Original-Oberflächen:
///   * Texte: GetLangString(hashcode) liefert den Text aus database/db_&lt;sprache&gt;.cpr in der Sprache, die der Spieler in den
///     Optionen eingestellt hat (TDU2PauseMenu.SavedLanguage(): ge, us, fr, it, sp, ru, ja, ko, ch, cz, pl). Wechselt die
///     Sprache, wird beim nächsten Aufruf automatisch die neue Datei geladen.
///   * GetFlashString("m_Resume_") liefert den Text einer Flash-Variable (DBLoc in db_data.cpr: _flash -> _hashcode).
///   * Filme: Main (main.vmf, Pause/Spielerkarte), Hud (hud.vmf), General (general.vmf, Popups/Einladungen),
///     Interior (aktueller Interieur-Film: shopscar.vmf, home.vmf, Casino …). Jeweils null, solange der Film nicht geladen ist.
/// Alles ist statisch und funktioniert auch ohne Instanz in der Szene.
/// </summary>
public class TDU2UIManager : MonoBehaviour
{
    public static TDU2UIManager Instance { get; private set; }

    void Start()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this.gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(this.gameObject);
        Preload();
    }

    public IEnumerator CloseNewsTimed()
    {
        yield return new WaitForSecondsRealtime(4f);
        CloseNews();
    }
    public IEnumerator CloseTipTimed()
    {
        yield return new WaitForSecondsRealtime(4f);
        CloseTip();
    }
    // ------------------------------------------------------------------ Texte (db_<sprache>.cpr)

    /// <summary>Sprachkürzel aus den Spieler-Optionen (ge, us, fr …).</summary>
    public static string Language => TDU2PauseMenu.SavedLanguage();

    /// <summary>Bnk-Ordner des Spiels (…\Euro\Bnk), null solange er nicht bekannt ist.</summary>
    public static string BnkDir
    {
        get
        {
            var m = VehicleManager;
            if (m != null && !string.IsNullOrEmpty(m.BnkDir)) return m.BnkDir;
            return TDU2.Avatars.AvatarLibrary.BnkDir;
        }
    }

    /// <summary>Text zum HashCode in der eingestellten Sprache; fallback, wenn es ihn nicht gibt (oder die Datenbank fehlt).</summary>
    public static string GetLangString(ulong hashcode, string fallback = "")
    {
        if (hashcode == 0) return fallback;
        string dir = BnkDir;
        if (string.IsNullOrEmpty(dir)) return fallback;
        RaceTexts.Ensure(dir, Language);   // lädt nur beim ersten Aufruf bzw. nach einem Sprachwechsel
        return RaceTexts.Get(hashcode, fallback);
    }

    public static string GetLangString(long hashcode, string fallback = "") => hashcode <= 0 ? fallback : GetLangString((ulong)hashcode, fallback);
    public static string GetLangString(int hashcode, string fallback = "") => hashcode <= 0 ? fallback : GetLangString((ulong)hashcode, fallback);

    /// <summary>HashCode als Text ("1625"), z.B. direkt aus einem Flash-Argument oder einer Datenbankspalte.</summary>
    public static string GetLangString(string hashcode, string fallback = "")
    {
        return ulong.TryParse((hashcode ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hc) ? GetLangString(hc, fallback) : fallback;
    }

    /// <summary>Gibt es zu dem HashCode einen Text?</summary>
    public static bool TryGetLangString(ulong hashcode, out string text)
    {
        const string none = "\u0000";
        text = GetLangString(hashcode, none);
        if (text == none) { text = null; return false; }
        return true;
    }

    /// <summary>Text einer Flash-Variable wie im Spiel (z.B. "m_Resume_", "m_my_house") in der eingestellten Sprache.</summary>
    public static string GetFlashString(string flashVar, string fallback = "")
    {
        string dir = BnkDir;
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(flashVar)) return fallback;
        var texts = FlashLocalization.Load(dir, Language);
        return texts != null && texts.TryGetValue(flashVar, out var t) ? t : fallback;
    }

    /// <summary>
    /// Text in eine Film-Variable schreiben, z.B. SetLangText(TDU2UIManager.Main, "/:m_Title", 1625). pin = true schützt den
    /// Wert davor, dass der Film ihn wieder überschreibt (VmfPlayer.PinText).
    /// </summary>
    public static bool SetLangText(VmfPlayer player, string variable, ulong hashcode, bool pin = false, string fallback = "")
    {
        if (player == null || string.IsNullOrEmpty(variable)) return false;
        string text = GetLangString(hashcode, fallback);
        if (pin) player.PinText(variable, text); else player.SetVariable(variable, text);
        return true;
    }

    /// <summary>Texte im Hintergrund vorladen (der erste GetLangString-Aufruf blockiert sonst kurz).</summary>
    public static void Preload()
    {
        string dir = BnkDir, lang = Language;
        if (string.IsNullOrEmpty(dir)) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try { RaceTexts.Ensure(dir, lang); } catch (Exception ex) { Debug.LogWarning("[TDU2] Texte vorladen: " + ex.Message); }
        });
    }

    // ------------------------------------------------------------------ Filme

    /// <summary>main.vmf (Pausemenü, Optionen, Spielerkarte).</summary>
    public static VmfPlayer Main { get { var p = PauseMenu; return p != null ? p.Ui : null; } }

    /// <summary>hud.vmf (Minimap, Rennen, Kontakte …).</summary>
    public static VmfPlayer Hud { get { var m = Minimap; return m != null ? m.HudPlayer : null; } }

    /// <summary>general.vmf (Popups, Spiel-Einladungen).</summary>
    public static VmfPlayer General => TDU2GameData.Instance != null ? TDU2GameData.Instance.General : null;

    /// <summary>Aktueller Interieur-Film (shopscar.vmf, home.vmf, Casino …); null außerhalb von Interieurs.</summary>
    public static VmfPlayer Interior { get { var i = Interiors; return i != null ? i.Ui : null; } }

    /// <summary>Die Komponenten selbst, falls mehr gebraucht wird als der Film.</summary>
    public static TDU2PauseMenu PauseMenu => Cache<TDU2PauseMenu>.Get();
    public static TDU2Minimap Minimap => Cache<TDU2Minimap>.Get();
    public static TDU2Interiors Interiors => Cache<TDU2Interiors>.Get();
    public static TDU2VehicleManager VehicleManager => Cache<TDU2VehicleManager>.Get();

    /// <summary>Komponente zwischenspeichern; fehlt sie, höchstens alle 0,5 s neu suchen.</summary>
    static class Cache<T> where T : UnityEngine.Object
    {
        static T _value;
        static float _next = -1f;

        public static T Get()
        {
            if (_value != null) return _value;
            if (Time.unscaledTime < _next) return null;
            _value = UnityEngine.Object.FindFirstObjectByType<T>();
            if (_value == null) _next = Time.unscaledTime + 0.5f;
            return _value;
        }
    }

    /// <summary>
    /// Print News in the game. The news will be displayed for 4 seconds and then closed automatically.
    /// </summary>
    /// <param name="NewsText"></param>
    public static void SendNews(string NewsText)
    {
        if (General != null)
        {
            var ui = General;
            ui.PlayLabel("/pop_news", "open");
            ui.SetText("/:NewsTxt", NewsText);
            ui.ForceVisible("/pop_news", true);
            Instance.StartCoroutine("CloseNewsTimed");
        }
    }

    public static void SendTip(string tipText)
    {
        if (General != null)
        {
            var ui = General;
            ui.ForceVisible("/pop_tutorial", true);
            ui.PlayLabel("/pop_tutorial", "open_tuto");
            ui.SetText("/:m_Tutorial", tipText);
            ui.SetText("/:TipTxt", tipText);
            Instance.StartCoroutine("CloseTipTimed");
        }
    }
    private void CloseTip()
    {
        if (General != null)
        {
            var ui = General;
            ui.PlayLabel("/pop_tutorial", "close_tuto");
        }
    }
    private void CloseNews()
    {
        if (General != null)
        {
            var ui = General;
            ui.PlayLabel("/pop_news", "close");
        }
    }
}
