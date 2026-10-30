using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using TDU2.Flash;
using TDU2.Online;
using TDU2.Vehicles;
using TDU2.World;
using UnityEngine;

/// <summary>
/// TDU Media (Fernseher im Haus, Auswahlpunkt Typ 57) mit dem Original-Film frontend/&lt;res&gt;/tdmedia.bnk:
///  - Öffnen: Wurzel-Label "media_in" (setBDD -> FSCommand sbdd = Texte), Hauptseite mit den Kacheln Wetter / News
///    (_root.fake = false; IsDLCChannel = Updates-Kanal, IsDLCHsys = Umfrage-Kanal, siehe unten).
///  - Wetter-Kanal ("tdmd:weat:open"): _root.IbizaWeather0..2 / HawaiiWeather0..2 = "d_"/"n_" + clear|cloudy|rainy|stormy
///    für jetzt, in 2 h und in 4 h Spielzeit (Texte m_Currently_/m_In2Hours_/m_In4Hours_) aus TDU2Weather.Forecast.
///  - News-Kanal ("tdmd:news:open"): native Liste (ASFlashList) mit news_title/news_date, Details _root.NewsTitle /
///    NewsDescription / CurrentNewsIndex / NbNews. Zuerst die Server-News (TDU2News: im Web-Panel angelegt, Überschrift/Text/
///    Symbol), danach Meldungen aus dem Spiel (Wetter, Spieler online, Profil, Geschichte).
///  - Umfrage-Kanal: der Film hat dafür nur Symbol/Titel ('survey'), keinen eigenen Clip. Die Kachel des Hilfe-DLC-Kanals
///    (IsDLCHsys) wird zur Umfrage-Kachel umgeschrieben (setSquare[i] = ['survey', Titel, false]); bei Auswahl öffnet sich die
///    Listen-Ansicht des News-Kanals (CurrentChannel = "news", cmdPrefix "tdmd:surv:") mit den Umfragen vom Server
///    (TDU2Surveys, im Web-Panel angelegt). Bestätigen auf einer offenen Umfrage = Antworten zeigen, Bestätigen auf einer
///    Antwort = abstimmen; B = zurück zur Umfrageliste.
///  - Updates-Kanal (Patch-Notes) = der Original-DLC-Kanal ("tdmd:cdlc:", Titel m_DLCChannel = "Updates") mit seinen Reitern
///    Packs / Vehicles / Avatar / Furnitures / Stickers: je Reiter legt der Film eine Liste "dlc_&lt;reiter&gt;" an, gefüllt mit
///    den Server-Meldungen der Art "update" und dieser Kategorie (Version statt Datum, Bild). Kaufen (A -> "dlci") ist aus;
///    Reiter mit ungelesenen Einträgen bekommen die "Neu"-Markierung (_root.IsNewsGM/IsNewsVehicles/…).
///  - News-Kanal nur für Meldungen aus dem Spiel/Server, getrennt nach den Original-Reitern: Spielfortschritt (Profil,
///    Geschichte), Community (Server-News aus dem Web-Panel), Club (Wetter, Spieler online); der vierte Reiter "DLC" ist
///    ausgeblendet (setTab gekürzt, lastTab = 2). Kopfzeile der Listen-Ansicht ("NEWS & DLC") je Kanal: NEWS / UPDATES / UMFRAGEN.
///  - B/ESC: Kanal zurück bzw. Film schließen (Ende der Schließ-Animation: FSCommand "tdmd:clos").
/// </summary>
public class TDU2TDUMedia : MonoBehaviour
{
    public bool UseOriginalFilm = true;
    public TDU2VehicleManager Manager;

    /// <summary>Fernseher offen: Laufmodus und Pausemenü ruhen (TDU2Interiors / TDU2PauseMenu).</summary>
    public static bool IsOpen { get; private set; }

    VmfPlayer _film;
    float _openedAt, _nextWeather, _forceCloseAt = -1f;
    VmfPlayer.FlashList _newsList;
    int _shownNews = -1;
    bool _german;

    sealed class News { public string Title, Text, Icon = "", Version = ""; public DateTime Date; public int ServerId; public NewsEntry Entry; }

    // Detailansicht (lists/details/content, Sprite 272): Bild "default" = schmaler Text + Bildrahmen img (Textur auf img/lure,
    // PS-Store-Logo ausgeblendet), "pictured" = Text über die ganze Breite (ohne Bild). Der Film schaltet beim Blättern selbst um –
    // hier jedes Bild auf den Stand der gewählten Meldung gebracht.
    const string DetailPath = "/lists/details/content";
    string _detailLabel = "pictured";
    Texture2D _detailTex;
    readonly List<News> _news = new List<News>();
    static readonly List<News> _log = new List<News>();   // Meldungen der Sitzung (Wetterwechsel …)
    static WeatherKind _lastKind = (WeatherKind)(-1);

    // Umfrage-Kanal
    bool _surveyMode;          // Listen-Ansicht zeigt Umfragen statt News
    int _surveyOpen = -1;      // Index der Umfrage, deren Antworten gerade zur Auswahl stehen (-1 = Umfrageliste)
    int _surveyBackSel = -1;   // Auswahl in der Umfrageliste beim Öffnen der Antworten
    float _surveyRefreshAt;
    string _surveyNote = "";   // Hinweis in der Detailansicht (Danke / Fehler)
    int _surveySquare = -1;
    bool _awaitVote;

    string SurveyTitle => _german ? "Umfragen" : "Polls";

    // Updates-Kanal (Patch-Notes)
    string _newsTab = "game_progress";   // Reiter des News-Kanals: game_progress, community, club
    bool _updatesMode;         // Liste im DLC-Kanal zeigt Patch-Notes
    string _updCat = "gmodes"; // Reiter/Kategorie: gmodes (Packs), vehicles, avatar, furnitures, stickers
    const string UpdatesTitle = "Updates";
    static readonly string[] UpdateCats = { "gmodes", "vehicles", "avatar", "furnitures", "stickers" };
    static readonly string[] UpdateCatVars = { "IsNewsGM", "IsNewsVehicles", "IsNewsAvatar", "IsNewsFurnitures", "IsNewsStickers" };

    void OnEnable() { TDU2News.Changed += OnServerNews; TDU2Surveys.Changed += OnSurveys; }
    void OnDisable() { TDU2News.Changed -= OnServerNews; TDU2Surveys.Changed -= OnSurveys; }

    /// <summary>Neue Server-News, während der Fernseher läuft: Liste neu aufbauen.</summary>
    void OnServerNews()
    {
        if (!IsOpen || _film == null || _surveyMode) return;
        BuildNews();
        if (_newsList != null) { ShowNews(_newsList.Selected); if (!_updatesMode) TDU2News.MarkSeen(); }
    }

    public void Open()
    {
        LoadFilm();
        if (_film == null) return;
        _german = (TDU2PauseMenu.SavedLanguage() ?? "").StartsWith("ge", StringComparison.OrdinalIgnoreCase);
        IsOpen = true; _openedAt = Time.unscaledTime; _forceCloseAt = -1f; _clockShown = -1;
        try { FlashLocalization.Apply(_film, FlashLocalization.Load(Manager.BnkDir, TDU2PauseMenu.SavedLanguage())); } catch (Exception) { }
        _film.SetValue("/:WhatPlatform", 1);
        _film.SetVariable("/:MenuType", "pc");
        _film.SetValue("/:IsPC_X360", true);
        _film.SetValue("/:fake", false);
        _film.SetValue("/:IsMyHouse", true);
        _film.SetValue("/:IsDLCChannel", true);           // DLC-Kanal = Updates (Patch-Notes)
        _film.SetValue("/:DLC_Bought", false);
        _updatesMode = false; _updCat = "gmodes";
        SetUpdateTexts();
        SetUpdateTabFlags();
        _film.SetValue("/:IsDLCHsys", true);              // Kachel wird zur Umfrage-Kachel (siehe UpdateVirtualSquares)
        _film.SetVariable("/:m_HelpDLCChannel", SurveyTitle);
        _surveyMode = false; _surveyOpen = -1; _surveySquare = -1; _awaitVote = false;
        TDU2Surveys.Refresh();
        _film.SetValue("/:Input", true);
        BuildNews();
        SetWeather();
        _film.Visible = true;
        _film.GotoLabel("/", "media_in", true);
        Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        try { TDU2FrontendMusic.Play(Manager.BnkDir, "TDMEDIA"); } catch (Exception) { }
        TDU2.TDU2Diag.Log("HAUS", $"TDU Media geöffnet: {_news.Count} News, Wetter {Frame(0f)} / {Frame(2f)} / {Frame(4f)}");
    }

    void Update()
    {
        PollWeatherNews();
        if (!IsOpen || _film == null) return;
        if (Time.unscaledTime >= _nextWeather) { _nextWeather = Time.unscaledTime + 5f; SetWeather(); }
        TDU2Surveys.Tick();
        UpdateVirtualSquares();
        UpdateSurveyChannel();
        if (_updatesMode && string.Equals(_film.GetVariable("/:CurrentChannel"), "main", StringComparison.OrdinalIgnoreCase)) LeaveUpdates();
        UpdateListChrome();
        UpdateNewsDetails();
        UpdateClock();
        if (_forceCloseAt > 0f && Time.unscaledTime >= _forceCloseAt)
        {
            // B hat den Film zugeklappt, aber das abschließende "tdmd:clos" kam nicht: selbst schließen
            _forceCloseAt = -1f;
            string l = _film.Label("/") ?? "";
            if (l == "close" || l == "default") Close();
        }
        if (Time.unscaledTime - _openedAt < 0.2f) return;   // Taste, mit der geöffnet wurde
#if ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) _film.Input("Move", "up");
        if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) _film.Input("Move", "down");
        if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) _film.Input("Move", "left");
        if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) _film.Input("Move", "right");
        if ((Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.E))
            && !(_updatesMode && string.Equals(_film.GetVariable("/:CurrentChannel"), "dlc", StringComparison.OrdinalIgnoreCase)))
            _film.Input("A");   // im Updates-Kanal (DLC-Liste) hätte A nur den Kauf ausgelöst
        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace))
        {
            if (_surveyMode && _surveyOpen >= 0) { SurveyBack(); return; }   // Antworten -> Umfrageliste
            if (!_film.Input("B")) Close();          // B des Films: Kanal zurück bzw. Fernseher aus
            else _forceCloseAt = Time.unscaledTime + 2.5f;
        }
        _film.ForceVisible("/dlc_popup", false);
#endif
    }

    int _clockShown = -1;

    static string LocalTime(string island)
    {
        float h = TDU2Weather.HourOn(island);
        int hh = Mathf.FloorToInt(h) % 24, mm = Mathf.FloorToInt((h - Mathf.Floor(h)) * 60f) % 60;
        return hh.ToString("00", CultureInfo.InvariantCulture) + ":" + mm.ToString("00", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Uhr oben im Film (/clock = tdmd_cmon_hour): Ortszeit der Insel aus TDU2Weather (Spielzeit, für alle gleich).
    /// Der Film liest _root.HDecade/HUnit/MDecade/MUnit in setHour() – wie die Uhr im Pausemenü.
    /// </summary>
    void UpdateClock()
    {
        if (TDU2Weather.Instance == null) return;
        float h = TDU2Weather.Hour;
        int hh = Mathf.FloorToInt(h) % 24, mm = Mathf.FloorToInt((h - Mathf.Floor(h)) * 60f) % 60;
        int code = hh * 100 + mm;
        if (code == _clockShown) return;
        _clockShown = code;
        _film.SetValue("/:HDecade", hh / 10); _film.SetValue("/:HUnit", hh % 10);
        _film.SetValue("/:MDecade", mm / 10); _film.SetValue("/:MUnit", mm % 10);
        _film.SetValue("/:SDecade", 0); _film.SetValue("/:SUnit", 0);
        _film.CallClip("/clock", "setHour");
    }

    void Close()
    {
        if (!IsOpen) return;
        IsOpen = false; _forceCloseAt = -1f;
        if (_film != null) { _film.Visible = false; _film.GotoLabel("/", "default", false); }
        _newsList = null; _shownNews = -1;
        _surveyMode = false; _surveyOpen = -1; _awaitVote = false;
        if (_updatesSeenPending) { _updatesSeenPending = false; TDU2News.MarkUpdatesSeen(); }
        _updatesMode = false;
        try { TDU2FrontendMusic.Stop(); } catch (Exception) { }
        Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false;
        TDU2.TDU2Diag.Log("HAUS", "TDU Media geschlossen");
    }

    void LoadFilm()
    {
        if (!UseOriginalFilm || _film != null || Manager == null || string.IsNullOrEmpty(Manager.BnkDir)) return;
        string bank = null;
        foreach (var res in new[] { "hires", "medres", "lowres" })
        {
            bank = CarDatabase.FindFile(Path.Combine(Manager.BnkDir, "frontend", res), "tdmedia.bnk");
            if (bank != null) break;
        }
        if (bank == null) { Debug.LogWarning("[TDU2] tdmedia.bnk nicht gefunden"); return; }
        try
        {
            var go = new GameObject("TDU2 tdmedia UI");
            go.transform.SetParent(transform, false);
            _film = go.AddComponent<VmfPlayer>();
            _film.GuiDepth = -2;
            _film.MouseEnabled = true;
            _film.Load(bank);
            _film.Visible = false;
            _film.FSCommand += OnFilmCommand;
            _film.ListCreated += OnListCreated;
            _film.ListValidated += l => { if (l == _newsList) ShowCurrent(l.Selected); };
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[TDU2] tdmedia.bnk: " + ex.Message);
            if (_film != null) { Destroy(_film.gameObject); _film = null; }
        }
    }

    void OnFilmCommand(string cmd, string args)
    {
        string c = (cmd ?? "").Trim();
        if (!string.IsNullOrEmpty(args)) c += ":" + args.Trim();
        string lc = c.ToLowerInvariant();
        if (TDU2FrontendSounds.Command(lc)) return;
        TDU2.TDU2Diag.Log("HAUS", "TDU Media FSCommand " + c);
        if (lc == "sbdd")
        {
            try { FlashLocalization.Apply(_film, FlashLocalization.Load(Manager.BnkDir, TDU2PauseMenu.SavedLanguage())); } catch (Exception) { }
            _film.SetVariable("/:m_HelpDLCChannel", SurveyTitle);
            SetUpdateTexts();
            return;
        }
        if (lc == "tdmd:clos") { TDU2FrontendSounds.Play("mdstop"); Close(); return; }   // Film zu (Ende der Schließ-Animation), Fernseher aus
        // Kanal wechseln (sounds.ini "mdswitch" = Med_shift_01; die übrigen md*-Töne schickt der Film selbst als sndp:md…)
        if (lc.StartsWith("tdmd:") && lc.EndsWith(":open")) TDU2FrontendSounds.Play("mdswitch");
        if (lc.StartsWith("tdmd:weat:")) { SetWeather(); return; }          // Wetter-Kanal auf/zu
        if (lc.StartsWith("tdmd:virt:")) return;                                // eigene Kachel gewählt (Umschalten im Update)
        if (lc == "dlci") return;                                               // Kauf-Anfrage des DLC-Kanals: gibt es nicht
        if (lc.StartsWith("tdmd:cdlc:"))
        {
            if (lc.EndsWith(":open")) { _updatesMode = true; SetUpdateTabFlags(); return; }
            if (lc.EndsWith(":clos")) { LeaveUpdates(); return; }
            int us = _newsList != null ? _newsList.Selected : 0;
            if (lc.Contains("del") && _updatesMode && us >= 0 && us < _news.Count)
            {
                _news.RemoveAt(us);
                if (_newsList != null) { FillNews(_newsList); _film.ListRefresh(_newsList); }
            }
            if (_updatesMode) ShowNews(us);
            return;
        }
        if (lc.StartsWith("tdmd:surv:"))
        {
            if (lc.EndsWith(":clos")) { LeaveSurvey(); return; }
            if (lc.EndsWith(":open")) return;                                   // Kanal gewählt, Umschalten in UpdateSurveyChannel
            if (_surveyMode && lc.EndsWith(":slct")) SurveySelect(_newsList != null ? _newsList.Selected : 0);
            return;
        }
        if (lc.StartsWith("tdmd:news:"))
        {
            if (lc.EndsWith(":open")) { SetNewsTabFlags(); BuildNews(); TDU2News.MarkSeen(); return; }
            if (lc.EndsWith(":clos")) { BuildNews(); return; }
            // Auswahl (cmd_select) bzw. Löschen (cmd_del) einer Meldung
            int sel = _newsList != null ? _newsList.Selected : 0;
            if (lc.Contains("del") && sel >= 0 && sel < _news.Count)
            {
                _news.RemoveAt(sel);
                if (_newsList != null) { FillNews(_newsList); _film.ListRefresh(_newsList); }
            }
            ShowNews(sel);
        }
    }

    // ------------------------------------------------------------------ Wetter
    static string Frame(float hoursAhead, string island = null)
    {
        TDU2Weather.Forecast(hoursAhead, island, out var kind, out bool night);
        string t;
        switch (kind)
        {
            case WeatherKind.Rainy: t = "rainy"; break;
            case WeatherKind.Tempest: t = "stormy"; break;
            case WeatherKind.Cloudy:
            case WeatherKind.BeforeRain: t = "cloudy"; break;
            default: t = "clear"; break;
        }
        return (night ? "n_" : "d_") + t;
    }

    void SetWeather()
    {
        if (_film == null) return;
        for (int i = 0; i < 3; i++)
        {
            // jetzt, in 2 h, in 4 h (Spielzeit) – je Insel eigenes Wetter und Ortszeit (Tag/Nacht, Hawaii -11 h)
            _film.SetVariable("/:IbizaWeather" + i, Frame(i * 2f, "ibiza"));
            _film.SetVariable("/:HawaiiWeather" + i, Frame(i * 2f, "hawai"));
        }
    }

    string KindText(WeatherKind k)
    {
        switch (k)
        {
            case WeatherKind.Rainy: return _german ? "Regen" : "rain";
            case WeatherKind.Tempest: return _german ? "Gewitter" : "thunderstorm";
            case WeatherKind.Cloudy: return _german ? "bewölkt" : "cloudy";
            case WeatherKind.BeforeRain: return _german ? "zunehmend bewölkt" : "clouds building up";
            case WeatherKind.AfterRain: return _german ? "abtrocknend, Sonne" : "drying up, sunny";
            default: return _german ? "sonnig" : "sunny";
        }
    }

    /// <summary>Wetterwechsel als Meldung merken (läuft, sobald der Fernseher einmal da war).</summary>
    void PollWeatherNews()
    {
        var k = TDU2Weather.Kind;
        if ((int)_lastKind < 0) { _lastKind = k; return; }
        if (k == _lastKind || TDU2Weather.StoryLock) return;
        _lastKind = k;
        _log.Insert(0, new News
        {
            Date = DateTime.Now,
            Title = _german ? "Wetterumschwung" : "Weather change",
            Text = (_german ? "Das Wetter ändert sich: " : "The weather is changing: ") + KindText(k) + ".",
        });
        if (_log.Count > 20) _log.RemoveAt(_log.Count - 1);
    }

    // ------------------------------------------------------------------ News
    void BuildNews()
    {
        _news.Clear();
        var now = DateTime.Now;
        // Server-News (Web-Panel), neueste zuerst
        foreach (var e in TDU2News.Items)
        {
            if (e == null) continue;
            bool update = string.Equals(e.kind, "update", StringComparison.OrdinalIgnoreCase);
            if (update != _updatesMode) continue;   // Patch-Notes nur im Updates-Kanal, News nur im News-Kanal
            if (update && UpdateCat(e) != _updCat) continue;   // Reiter des Updates-Kanals
            if (!update && _newsTab != "community") continue;   // Server-News im Reiter Community
            DateTime d = now;
            if (!string.IsNullOrEmpty(e.created) && DateTime.TryParse(e.created, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt)) d = dt.ToLocalTime();
            _news.Add(new News { Title = e.header ?? "", Text = e.message ?? "", Icon = e.icon ?? "", Version = e.version ?? "", Date = d, ServerId = e.id, Entry = e });
        }
        if (_updatesMode)
        {
            if (_news.Count == 0)
                _news.Add(new News
                {
                    Date = now, Title = _german ? "Keine Einträge" : "No entries",
                    Text = !TDU2Session.LoggedIn ? (_german ? "Die Update-Liste kommt vom Server – bitte anmelden." : "The update list comes from the server – please log in.")
                         : (_german ? "Noch keine Patch-Notes veröffentlicht." : "No patch notes published yet."),
                });
            FinishNews();
            return;
        }
        if (_newsTab == "club") AddWorldNews(now);
        if (_newsTab == "game_progress") AddPlayerNews(now);
        if (_news.Count == 0)
            _news.Add(new News
            {
                Date = now, Title = _german ? "Keine Meldungen" : "No news",
                Text = _newsTab == "community" && !TDU2Session.LoggedIn
                    ? (_german ? "Server-News gibt es nur online – bitte anmelden." : "Server news are only available online – please log in.")
                    : (_german ? "In dieser Kategorie gibt es gerade nichts Neues." : "Nothing new in this category right now."),
            });
        FinishNews();
    }

    /// <summary>Reiter Club: Wetter (beide Inseln), Wetterwechsel der Sitzung, Spieler online.</summary>
    void AddWorldNews(DateTime now)
    {
        TDU2Weather.Forecast(0f, out var k0, out _);
        TDU2Weather.Forecast(2f, out var k2, out _);
        TDU2Weather.Forecast(4f, out var k4, out _);
        string other = TDU2Weather.IslandKey(Manager?.Streamer?.Island) == "hawai" ? "ibiza" : "hawai";
        TDU2Weather.Forecast(0f, other, out var ko, out _);
        _news.Add(new News
        {
            Date = now,
            Title = _german ? "Wetterbericht" : "Weather report",
            Text = _german
                ? $"Aktuell {KindText(k0)}, in 2 Stunden {KindText(k2)}, in 4 Stunden {KindText(k4)} (Spielzeit). Details im Wetter-Kanal."
                : $"Currently {KindText(k0)}, in 2 hours {KindText(k2)}, in 4 hours {KindText(k4)} (game time). See the weather channel.",
        });
        _news.Add(new News
        {
            Date = now,
            Title = _german ? (other == "hawai" ? "Wetter auf Hawaii" : "Wetter auf Ibiza") : (other == "hawai" ? "Weather on Hawaii" : "Weather on Ibiza"),
            Text = (_german ? "Gerade " : "Currently ") + KindText(ko) + (_german ? $" – Ortszeit {LocalTime(other)}." : $" – local time {LocalTime(other)}."),
        });
        _news.AddRange(_log);
        if (TDU2Session.LoggedIn)
            _news.Add(new News
            {
                Date = now,
                Title = "Online",
                Text = _german ? $"Gerade sind {TDU2Session.Online} Spieler auf dem Server unterwegs." : $"{TDU2Session.Online} players are currently on the server.",
            });
    }

    /// <summary>Reiter Spielfortschritt: Profil und nächster Schritt der Geschichte.</summary>
    void AddPlayerNews(DateTime now)
    {
        var save = TDU2Session.Save;
        if (save != null)
            _news.Add(new News
            {
                Date = now,
                Title = _german ? "Dein Profil" : "Your profile",
                Text = _german
                    ? $"{TDU2Session.Nick}: {save.ownedCars?.Count ?? 0} Autos in der Garage, Kontostand {save.money:N0} $."
                    : $"{TDU2Session.Nick}: {save.ownedCars?.Count ?? 0} cars in the garage, balance ${save.money:N0}.",
            });
        int step = TDU2.Story.TDU2StoryProgress.Get(TDU2Session.Nick);
        if (step >= 0 && step < TDU2.Story.TDU2StoryProgress.Implemented)
            _news.Add(new News
            {
                Date = now,
                Title = _german ? "Deine Geschichte" : "Your story",
                Text = (_german ? "Nächster Schritt: " : "Next step: ") + TDU2.Story.TDU2StoryProgress.StepName(step),
            });
    }

    void FinishNews()
    {
        if (_film != null)
        {
            _film.SetVariable("/:NbNews", _news.Count.ToString(CultureInfo.InvariantCulture));
            if (_newsList != null) { FillNews(_newsList); _film.ListRefresh(_newsList); }
        }
    }

    void OnListCreated(VmfPlayer.FlashList l)
    {
        string path = VmfPlayer.PathOf(l.Owner) ?? "";
        TDU2.TDU2Diag.Log("HAUS", $"TDU Media Liste {l.Name} ({path}) Zeilen {l.ViewSize}");
        // Updates-Kanal (Original-DLC-Kanal): je Reiter eine Liste "dlc_<reiter>"
        string lname = l.Name ?? "";
        if (lname.StartsWith("dlc_", StringComparison.OrdinalIgnoreCase) && !_surveyMode)
        {
            _updatesMode = true;
            _updCat = lname.Substring(4).ToLowerInvariant();
            if (Array.IndexOf(UpdateCats, _updCat) < 0) _updCat = "gmodes";
            _newsList = l; _shownNews = -1;
            BuildNews();
            _film.ListRefresh(l);
            _film.ListLoaded(l);
            ShowNews(0);
            MarkUpdatesSeen();
            return;
        }
        // News-Kanal: native Liste im Clip news_list; DLC-Listen bleiben leer (keine DLC-Kanäle)
        if (!_surveyMode && lname.StartsWith("news_", StringComparison.OrdinalIgnoreCase))
        {
            _newsTab = lname.Substring(5).ToLowerInvariant();
            if (_newsTab != "community" && _newsTab != "club") _newsTab = "game_progress";
            _updatesMode = false;
            _newsList = l; _shownNews = -1;
            BuildNews();
            _film.ListRefresh(l);
            _film.ListLoaded(l);
            ShowNews(0);
            return;
        }
        bool news = path.IndexOf("news", StringComparison.OrdinalIgnoreCase) >= 0 || (l.Name ?? "").IndexOf("news", StringComparison.OrdinalIgnoreCase) >= 0
                    || string.Equals(_film.GetVariable("/:CurrentChannel"), "news", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(_film.GetVariable("/:NextChannel"), "news", StringComparison.OrdinalIgnoreCase);
        if (news) { _newsList = l; FillCurrent(l); }
        else l.Rows.Clear();
        _film.ListRefresh(l);
        _film.ListLoaded(l);
        if (news) ShowCurrent(0);
    }

    void FillNews(VmfPlayer.FlashList l)
    {
        l.ColumnVars = new[] { "news_title", "news_date" };
        l.Rows.Clear();
        foreach (var n in _news)
            l.Rows.Add(new[] { n.Title, !string.IsNullOrEmpty(n.Version) ? "v" + n.Version.TrimStart('v', 'V') : n.Date.ToString(_german ? "dd.MM.yyyy" : "MM/dd/yyyy", CultureInfo.InvariantCulture) });
        if (l.Selected >= l.Rows.Count) { l.Selected = Math.Max(0, l.Rows.Count - 1); l.Top = 0; }
        _film.SetVariable("/:NbNews", _news.Count.ToString(CultureInfo.InvariantCulture));
    }

    void UpdateNewsDetails()
    {
        if (_newsList == null) return;
        if (_newsList.Selected != _shownNews) ShowCurrent(_newsList.Selected);
        // Bild/Textlage halten (der Film setzt die Detailansicht beim Blättern selbst um)
        string l = _film.Label(DetailPath);
        if (l != null && l != _detailLabel) ApplyDetailLayout();
    }

    void ApplyDetailLayout()
    {
        _film.GotoLabel(DetailPath, _detailLabel, false);
        if (_detailLabel == "default")
        {
            _film.SetVisible(DetailPath + "/ps_store", false);
            _film.SetClipTexture(DetailPath + "/img/lure", _detailTex);
        }
    }

    void ShowNews(int i)
    {
        if (_film == null) return;
        _shownNews = i;
        var n = i >= 0 && i < _news.Count ? _news[i] : null;
        // Bild der Server-News (kommt es erst noch, zeigt OnServerNews die Meldung danach neu an)
        _detailTex = n?.Entry != null ? TDU2News.Image(n.Entry) : null;
        _detailLabel = _detailTex != null ? "default" : "pictured";
        if (_newsList != null) ApplyDetailLayout();
        _film.SetVariable("/:NewsTitle", n?.Title ?? "");
        _film.SetVariable("/:NewsDescription", n?.Text ?? "");
        _film.SetVariable("/:CurrentNewsIndex", (Math.Max(0, i) + 1).ToString(CultureInfo.InvariantCulture));
        _film.SetVariable("/:NbNews", _news.Count.ToString(CultureInfo.InvariantCulture));
    }

    void FillCurrent(VmfPlayer.FlashList l) { if (_surveyMode) FillSurveys(l); else FillNews(l); }
    void ShowCurrent(int i) { if (_surveyMode) ShowSurvey(i); else ShowNews(i); }

    // ------------------------------------------------------------------ Umfragen
    /// <summary>
    /// Umfrage-Kachel: die Hilfe-DLC-Kachel der Hauptseite (main_page.setSquare[i] = ['hsys_dlc', m_HelpDLCChannel, false]) wird
    /// zu ['survey', "Umfragen", false] (Symbol im Film vorhanden). ValidMainMenu hat für 'survey' keinen eigenen Clip – den Kanal
    /// baut UpdateSurveyChannel aus der Listen-Ansicht des News-Kanals. Jedes Bild geprüft, weil der Film setSquare bei jeder
    /// Rückkehr zur Hauptseite neu anlegt (initSquares).
    /// </summary>
    void UpdateVirtualSquares()
    {
        if (_surveySquare < 0 || _film.GetScriptValue("/main_page", "setSquare." + _surveySquare + ".0") != "survey")
        {
            int found = -1;
            for (int i = 0; i < 8; i++)
            {
                string id = _film.GetScriptValue("/main_page", "setSquare." + i + ".0");
                if (id == null) break;
                if (id != "hsys_dlc" && id != "survey") continue;
                _film.SetScriptValue("/main_page", "setSquare." + i + ".0", "survey");
                _film.SetScriptValue("/main_page", "setSquare." + i + ".1", SurveyTitle);
                found = i;
                break;
            }
            if (found != _surveySquare && found >= 0) TDU2.TDU2Diag.Log("HAUS", "TDU Media: Kachel " + found + " -> Umfragen");
            if (found < 0 && _surveySquare >= 0) TDU2.TDU2Diag.Log("HAUS", "TDU Media: Umfrage-Kachel nicht gefunden, setSquare = " + (_film.GetScriptValue("/main_page", "setSquare") ?? "undefined"));
            _surveySquare = found;
        }
        KeepSquare(_surveySquare, "survey", SurveyTitle);
        // Auf der Hauptseite: neutrales Präfix für die Umfrage-Kachel (die Film-Kanäle setzen ihr eigenes bei der Auswahl)
        if (!_surveyMode && string.Equals(_film.GetVariable("/:CurrentChannel"), "main", StringComparison.OrdinalIgnoreCase))
            _film.SetVariable("/:cmdPrefix", "tdmd:virt:");
    }

    void KeepSquare(int index, string icon, string title)
    {
        if (index < 0) return;
        string sq = "/main_page/sq" + index;
        string lab = _film.Label(sq + "/ic");
        if (lab != null && lab != icon) _film.GotoLabel(sq + "/ic", icon, false);
        if (_film.GetVariable(sq + ":txt") != title) _film.SetVariable(sq + ":txt", title);
    }

    // ------------------------------------------------------------------ Listen-Ansicht: Kopfzeile, Reiter, Hilfe
    const string HeaderPath = "/lists/#244";   // statischer Text "NEWS & DLC" der Listen-Ansicht
    int _tabsShown = -1;

    /// <summary>
    /// Je Kanal: Kopfzeile, Anzahl der Reiter (News 3 ohne "DLC", Umfragen 1, Updates alle 5) und Hilfeleiste
    /// (Updates: nur Zurück statt "DLC kaufen"; Umfragen: Auswählen/Zurück). Jedes Bild, weil der Film Reiter/Hilfe beim
    /// Kanal- und Reiterwechsel neu setzt.
    /// </summary>
    void UpdateListChrome()
    {
        string ch = _film.GetVariable("/:CurrentChannel") ?? "";
        bool news = string.Equals(ch, "news", StringComparison.OrdinalIgnoreCase), dlc = string.Equals(ch, "dlc", StringComparison.OrdinalIgnoreCase);
        if (!news && !dlc) { if (_tabsShown != -1) { ShowTabs(5); _tabsShown = -1; } return; }
        string header = _surveyMode ? (_german ? "UMFRAGEN" : "POLLS") : dlc ? "UPDATES" : "NEWS";
        _film.SetObjectText(HeaderPath, header);
        int tabs = _surveyMode ? 1 : news ? 3 : 5;
        if (tabs < 5)
        {
            string len = _film.GetScriptValue("/lists", "setTab.length");
            if (len != null && int.TryParse(len, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > tabs)
                _film.SetScriptValue("/lists", "setTab.length", tabs);
            if (_film.GetScriptValue("/lists", "lastTab") != (tabs - 1).ToString(CultureInfo.InvariantCulture))
            {
                _film.SetScriptValue("/lists", "lastTab", tabs - 1);
                _film.SetScriptValue("/lists", "nbTabs", tabs);
            }
        }
        if (_tabsShown != tabs) { ShowTabs(tabs); _tabsShown = tabs; }
        if (_surveyMode && _film.GetVariable("/lists/tabs/tab0:txt") != SurveyTitle) _film.SetVariable("/lists/tabs/tab0:txt", SurveyTitle);
        string help = _surveyMode ? "hsys_dlc" : dlc ? "back" : null;
        if (help != null)
        {
            string hl = _film.Label("/help/content");
            if (hl != null && hl != help) _film.GotoLabel("/help/content", help, false);
        }
    }

    void ShowTabs(int count)
    {
        for (int i = 0; i < 5; i++) _film.ForceVisible("/lists/tabs/tab" + i, i < count ? (bool?)null : false);
    }

    /// <summary>"Neu"-Markierung der News-Reiter (Community = ungelesene Server-News), vor dem Aufbau der Reiter.</summary>
    void SetNewsTabFlags()
    {
        bool unread = false;
        foreach (var e in TDU2News.Items) if (e != null && !TDU2News.IsUpdate(e) && TDU2News.IsUnread(e)) { unread = true; break; }
        _film.SetValue("/:IsNewsGameProgress", false);
        _film.SetValue("/:IsNewsCommunity", unread);
        _film.SetValue("/:IsNewsClub", false);
        _film.SetValue("/:IsNewsDLC", false);
    }

    // ------------------------------------------------------------------ Updates (Original-DLC-Kanal)
    /// <summary>Kategorie (= Reiter) einer Patch-Note; leer/unbekannt = Packs.</summary>
    static string UpdateCat(NewsEntry e)
    {
        string c = (e?.category ?? "").Trim().ToLowerInvariant();
        return Array.IndexOf(UpdateCats, c) >= 0 ? c : "gmodes";
    }

    /// <summary>Titel des Kanals und Hilfetext (statt "DLC kaufen").</summary>
    void SetUpdateTexts()
    {
        if (_film == null) return;
        _film.SetVariable("/:m_DLCChannel", UpdatesTitle);
        _film.SetVariable("/:m_BuyDLCHelp_", _german ? "Lesen" : "Read");
        _film.SetVariable("/:m_NoOffer_", _german ? "Keine Einträge in dieser Kategorie." : "No entries in this category.");
    }

    /// <summary>"Neu"-Markierung der Reiter: ungelesene Patch-Notes je Kategorie (vor dem Aufbau der Reiter gesetzt).</summary>
    void SetUpdateTabFlags()
    {
        if (_film == null) return;
        for (int c = 0; c < UpdateCats.Length; c++)
        {
            bool unread = false;
            foreach (var e in TDU2News.Items)
                if (e != null && string.Equals(e.kind, "update", StringComparison.OrdinalIgnoreCase) && UpdateCat(e) == UpdateCats[c] && TDU2News.IsUnread(e)) { unread = true; break; }
            _film.SetValue("/:" + UpdateCatVars[c], unread);
        }
    }

    bool _updatesSeenPending;
    /// <summary>Updates gelesen merken – erst beim Verlassen, damit die Markierungen während des Besuchs sichtbar bleiben.</summary>
    void MarkUpdatesSeen() { _updatesSeenPending = true; }

    void LeaveUpdates()
    {
        if (!_updatesMode) return;
        _updatesMode = false; _shownNews = -1;
        if (_updatesSeenPending) { _updatesSeenPending = false; TDU2News.MarkUpdatesSeen(); }
        SetUpdateTabFlags();
        BuildNews();
    }

    /// <summary>Umfrage-Kachel gewählt (CurrentChannel = 'survey'): Listen-Ansicht des News-Kanals mit den Umfragen öffnen.</summary>
    void UpdateSurveyChannel()
    {
        string ch = _film.GetVariable("/:CurrentChannel") ?? "";
        if (!_surveyMode && string.Equals(ch, "survey", StringComparison.OrdinalIgnoreCase))
        {
            _surveyMode = true; _surveyOpen = -1; _surveyNote = ""; _shownNews = -1; _awaitVote = false;
            _film.SetVariable("/:cmdPrefix", "tdmd:surv:");
            _film.SetVariable("/:CurrentChannel", "news");     // Liste/Detail/Tasten wie im News-Kanal
            TDU2Surveys.Refresh(); _surveyRefreshAt = Time.unscaledTime + 20f;
            _film.GotoLabel("/lists", "open", true);
            if (_newsList != null) { FillSurveys(_newsList); _film.ListRefresh(_newsList); ShowSurvey(0); }
            TDU2.TDU2Diag.Log("HAUS", $"TDU Media: Umfrage-Kanal ({TDU2Surveys.Items.Count} Umfragen)");
            return;
        }
        if (_surveyMode && string.Equals(ch, "main", StringComparison.OrdinalIgnoreCase)) { LeaveSurvey(); return; }
        if (_surveyMode && _surveyOpen < 0 && Time.unscaledTime >= _surveyRefreshAt)
        {
            _surveyRefreshAt = Time.unscaledTime + 20f;
            TDU2Surveys.Refresh();
        }
    }

    void LeaveSurvey()
    {
        if (!_surveyMode) return;
        _surveyMode = false; _surveyOpen = -1; _shownNews = -1; _awaitVote = false;
        if (_newsList != null) { FillNews(_newsList); _film.ListRefresh(_newsList); }
    }

    void OnSurveys()
    {
        if (!IsOpen || _film == null || !_surveyMode || _newsList == null) return;
        if (TDU2Surveys.Busy) return;
        if (_awaitVote)
        {
            // Antwort auf die Stimme: zurück zur Umfrageliste, die Umfrage bleibt gewählt
            _awaitVote = false;
            if (TDU2Surveys.LastError.Length > 0) _surveyNote = SurveyError(TDU2Surveys.LastError);
            _surveyOpen = -1;
            FillSurveys(_newsList);
            _newsList.Selected = Mathf.Clamp(_surveyBackSel, 0, Math.Max(0, _newsList.Rows.Count - 1));
            _film.ListRefresh(_newsList);
            ShowSurvey(_newsList.Selected);
            return;
        }
        if (_surveyOpen >= 0) return;   // Antwortauswahl nicht unter den Füßen umbauen
        int sel = _newsList.Selected;
        FillSurveys(_newsList);
        _newsList.Selected = Mathf.Clamp(sel, 0, Math.Max(0, _newsList.Rows.Count - 1));
        _film.ListRefresh(_newsList);
        ShowSurvey(_newsList.Selected);
    }

    string SurveyError(string code)
    {
        switch (code)
        {
            case "closed": return _german ? "Diese Umfrage ist bereits beendet." : "This poll has already ended.";
            case "not_found": return _german ? "Diese Umfrage gibt es nicht mehr." : "This poll no longer exists.";
            case "not_logged_in": return _german ? "Nur online möglich – bitte anmelden." : "Only available online – please log in.";
            case "unknown_op": return _german ? "Der Server kennt noch keine Umfragen (Server aktualisieren)." : "The server does not support polls yet.";
            default: return (_german ? "Abstimmung fehlgeschlagen: " : "Voting failed: ") + code;
        }
    }

    SurveyEntry SurveyAt(int i) => i >= 0 && i < TDU2Surveys.Items.Count ? TDU2Surveys.Items[i] : null;

    void FillSurveys(VmfPlayer.FlashList l)
    {
        l.ColumnVars = new[] { "news_title", "news_date" };
        l.Rows.Clear();
        var open = SurveyAt(_surveyOpen);
        if (open != null)
        {
            for (int a = 0; a < open.answers.Count; a++)
                l.Rows.Add(new[] { (a == open.myVote ? "» " : "") + open.answers[a], Percent(open, a) });
        }
        else
        {
            foreach (var s in TDU2Surveys.Items)
                l.Rows.Add(new[] { s.question, !s.open ? (_german ? "beendet" : "ended") : s.myVote >= 0 ? (_german ? "abgestimmt" : "voted") : (_german ? "offen" : "open") });
            if (l.Rows.Count == 0)
                l.Rows.Add(new[] { !TDU2Session.LoggedIn ? (_german ? "Nur online verfügbar" : "Online only")
                                   : !TDU2Surveys.Loaded ? (_german ? "Lade Umfragen…" : "Loading polls…") : (_german ? "Keine Umfragen" : "No polls"), "" });
        }
        if (l.Selected >= l.Rows.Count) { l.Selected = Math.Max(0, l.Rows.Count - 1); l.Top = 0; }
        _film.SetVariable("/:NbNews", l.Rows.Count.ToString(CultureInfo.InvariantCulture));
    }

    static string Percent(SurveyEntry s, int a)
    {
        int n = a < s.counts.Count ? s.counts[a] : 0;
        return (s.total > 0 ? Mathf.RoundToInt(n * 100f / s.total) : 0).ToString(CultureInfo.InvariantCulture) + " %";
    }

    string Results(SurveyEntry s)
    {
        var sb = new System.Text.StringBuilder();
        for (int a = 0; a < s.answers.Count; a++)
        {
            int n = a < s.counts.Count ? s.counts[a] : 0;
            sb.Append(a == s.myVote ? "» " : "   ").Append(s.answers[a]).Append(": ").Append(Percent(s, a)).Append(" (").Append(n).Append(')').Append('\n');
        }
        sb.Append(_german ? $"{s.total} Stimmen" : $"{s.total} votes");
        return sb.ToString();
    }

    void ShowSurvey(int i)
    {
        if (_film == null) return;
        _shownNews = i;
        _detailTex = null; _detailLabel = "pictured";
        if (_newsList != null) ApplyDetailLayout();
        string title, text;
        var open = SurveyAt(_surveyOpen);
        if (open != null)
        {
            title = open.question;
            string ans = i >= 0 && i < open.answers.Count ? open.answers[i] : "";
            text = (string.IsNullOrEmpty(open.text) ? "" : open.text + "\n\n")
                 + (_german ? $"Deine Wahl: {ans}\nBestätigen = abstimmen, Zurück = abbrechen." : $"Your choice: {ans}\nConfirm = vote, Back = cancel.")
                 + (open.myVote >= 0 ? (_german ? "\n(Du hast schon abgestimmt – eine neue Wahl ersetzt die alte.)" : "\n(You already voted – a new choice replaces the old one.)") : "");
        }
        else
        {
            var s = SurveyAt(i);
            if (s == null)
            {
                title = SurveyTitle;
                text = !TDU2Session.LoggedIn ? (_german ? "Umfragen gibt es nur online. Melde dich am Server an." : "Polls are only available online. Log in to the server.")
                     : (_german ? "Gerade läuft keine Umfrage. Schau später wieder vorbei!" : "There are no polls right now. Check back later!");
            }
            else
            {
                title = s.question;
                string state = !s.open ? (_german ? "Diese Umfrage ist beendet." : "This poll has ended.")
                    : s.myVote >= 0 ? (_german ? "Du hast abgestimmt. Bestätigen = Stimme ändern." : "You have voted. Confirm = change your vote.")
                    : (_german ? "Bestätigen = abstimmen." : "Confirm = vote.");
                string ends = "";
                if (s.open && !string.IsNullOrEmpty(s.closes) && DateTime.TryParse(s.closes, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var c))
                    ends = (_german ? "Endet am " : "Ends ") + c.ToLocalTime().ToString(_german ? "dd.MM.yyyy HH:mm" : "MM/dd/yyyy HH:mm", CultureInfo.InvariantCulture) + "\n";
                text = (string.IsNullOrEmpty(s.text) ? "" : s.text + "\n\n") + Results(s) + "\n\n" + ends + state;
            }
        }
        if (_surveyNote.Length > 0) text = _surveyNote + "\n\n" + text;
        _film.SetVariable("/:NewsTitle", title ?? "");
        _film.SetVariable("/:NewsDescription", text ?? "");
        _film.SetVariable("/:CurrentNewsIndex", (Math.Max(0, i) + 1).ToString(CultureInfo.InvariantCulture));
        _film.SetVariable("/:NbNews", (_newsList != null ? _newsList.Rows.Count : 0).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Bestätigen in der Umfrage-Ansicht: Umfrage -> Antworten zeigen; Antwort -> abstimmen.</summary>
    void SurveySelect(int i)
    {
        if (_newsList == null) return;
        _surveyNote = "";
        var open = SurveyAt(_surveyOpen);
        if (open != null)
        {
            if (i < 0 || i >= open.answers.Count || _awaitVote) return;
            TDU2Surveys.Vote(open.id, i);
            _awaitVote = true;
            _surveyNote = _german ? $"Danke! Deine Stimme für „{open.answers[i]}“ wurde gezählt." : $"Thanks! Your vote for \"{open.answers[i]}\" was counted.";
            TDU2.TDU2Diag.Log("HAUS", $"Umfrage #{open.id}: Stimme für Antwort {i + 1}");
            return;
        }
        var s = SurveyAt(i);
        if (s == null || !s.open) { TDU2FrontendSounds.Play("mdwrong"); return; }
        _surveyBackSel = i;
        _surveyOpen = i;
        FillSurveys(_newsList);
        _newsList.Selected = Mathf.Clamp(s.myVote, 0, Math.Max(0, _newsList.Rows.Count - 1)); _newsList.Top = 0;
        _film.ListRefresh(_newsList);
        ShowSurvey(_newsList.Selected);
    }

    /// <summary>B in der Antwortauswahl: zurück zur Umfrageliste (ohne den Kanal zu verlassen).</summary>
    void SurveyBack()
    {
        _surveyOpen = -1; _awaitVote = false; _surveyNote = "";
        TDU2FrontendSounds.Play("mdback");
        if (_newsList == null) return;
        FillSurveys(_newsList);
        _newsList.Selected = Mathf.Clamp(_surveyBackSel, 0, Math.Max(0, _newsList.Rows.Count - 1));
        _film.ListRefresh(_newsList);
        ShowSurvey(_newsList.Selected);
    }

    void OnDestroy()
    {
        if (IsOpen) { IsOpen = false; Cursor.lockState = CursorLockMode.Locked; Cursor.visible = false; }
    }
}
