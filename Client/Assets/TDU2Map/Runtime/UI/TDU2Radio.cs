
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using TDU2.Map.Flash;
using TDU2.Map.IO;
using TDU2.Map.Online;
using TDU2.Map.Vehicles;
using UnityEngine;

public enum RadioStation
{
    off = 0,
    hariba,
    kalua,
    roadtrack,
}

public class TDU2Radio : MonoBehaviour
{
    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern int mciSendString(string command, StringBuilder returnValue, int returnLength, IntPtr callback);

    [DllImport("winmm.dll", CharSet = CharSet.Auto)]
    private static extern bool mciGetErrorString(int errorCode, StringBuilder errorText, int errorTextSize);
    public static TDU2Radio Instance { get; private set; }
    public static bool IsOpen => Instance != null && Instance.isOpen;
    public bool canPlayRadio = false;
    private VmfPlayer HudPlayer;
    private bool isOpen;
    [Header("Steuerung")]
    public KeyCode StationBack = KeyCode.Keypad4;
    public KeyCode StationNext = KeyCode.Keypad6;
    public KeyCode VolumeUp = KeyCode.Keypad8;
    public KeyCode VolumeDown = KeyCode.Keypad2;
    public KeyCode OpenRadio = KeyCode.Keypad5;

    public RadioStation CurrentStation = RadioStation.off;

    [Range(0, 10)]
    public int CurrentVolume = 1;

    public DateTime LastStationChange { get; private set; } = DateTime.MinValue;

    [Header("User Radio")]
    public string SharedAssetsFolderName = "SharedAssets";
    public string RadioFolderName = "Radio";
    public bool ShuffleTracks = false;

    [Header("Laufschrift")]
    [Min(1)]
    public int MarqueeLength = 15;

    [Min(0.05f)]
    public float MarqueeSpeed = 0.3f;

    public string MarqueeSeparator = "   -   ";

    [Header("Original-Radio (Hariba / RoadRock)")]
    [Tooltip("Original-Sender aus db_data.cpr abspielen (Assets aus Euro/Bnk/sound/radio). Kalua bleibt der MP3-Player.")]
    public bool PlayOriginalRadio = true;

    [Range(0f, 1f)]
    [Tooltip("Wahrscheinlichkeit, dass zwischen zwei Songs ein Sender-Jingle läuft (mind. alle 3 Songs).")]
    public float JingleChance = 0.3f;

    [Range(0f, 1f)]
    [Tooltip("Wahrscheinlichkeit für einen Kommentar des Moderators zwischen zwei Songs.")]
    public float CommentChance = 0.2f;

    [Range(0f, 1f)]
    [Tooltip("Wahrscheinlichkeit für einen Werbespot zwischen zwei Songs.")]
    public float AdChance = 0.1f;

    private const string MciAlias = "TDU2UserRadio";

    private string radioFolderPath;

    private readonly List<RadioTrack> tracks = new List<RadioTrack>();

    private int currentTrackIndex = -1;

    private bool tracksLoaded;
    private bool mciOpened;

    private float nextPlaybackCheck;
    private float nextMarqueeUpdate;

    // --- Original-Radio (Hariba / RoadRock) ---
    private string _bnkDir;
    private TDU2RadioData _radioData;
    private Task<TDU2RadioData> _radioDataTask;
    private AudioSource _radioSource;
    private AudioClip _radioClip;
    private Task<DecodedWav> _decodeTask;
    private RadioStation _decodeStation;
    private RadioMessage _currentMessage;
    private readonly List<RadioMessage> _stationQueue = new List<RadioMessage>();
    private int _queueIndex;
    private bool _originalPlaying;
    private bool _segmentPlaying;
    private bool _pendingRestart;
    private bool _playedAnySegment;
    private bool _waitRetry;
    private float _retryAt;
    private int _tracksSinceJingle = 99;
    private bool _lastWasMusic;

    // --- Synchrones Original-Radio (Server-Zeitbasis): alle Clients hören denselben Ausschnitt ---
    private TDU2RadioData.RadioPlaylist _syncPlaylist;
    private int _syncCycleStation, _syncCycleSeed;
    private RadioMessage _syncMessage;
    private RadioMessage _decodeMessage;
    private float _seekBase;                       // Offset im Segment beim Start der Dekodierung
    private double _seekBaseAt = double.NaN;       // Radio-Zeit (Serveruhr) zu diesem Zeitpunkt

    private struct DecodedWav
    {
        public float[] Samples;
        public int Channels;
        public int Rate;
    }

    private string currentTitle = "";
    private string currentArtist = "";

    private int titleScrollOffset;
    private int artistScrollOffset;

    private class RadioTrack
    {
        public string FilePath;
        public string Title;
        public string Artist;
    }

    private void Start()
    {
        CurrentVolume = PlayerPrefs.GetInt("TDU2.Opt.Volime", 1);
        CurrentStation = (RadioStation)PlayerPrefs.GetInt("TDU2.Opt.Station", 0);
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        string root = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        radioFolderPath = Path.Combine(root, SharedAssetsFolderName, RadioFolderName);

        try
        {
            Directory.CreateDirectory(radioFolderPath);
            LoadRadioTracks();
        }
        catch (Exception ex)
        {
        }

        if (_bnkDir == null) _bnkDir = FindBnkDir();
        EnsureRadioData();
    }

    /// <summary>Vom Map-Streamer/Minimap gesetzt: Euro/Bnk (enthält database, sound/radio ...).</summary>
    public void SetBnkDir(string bnkDir)
    {
        if (string.IsNullOrEmpty(bnkDir)) return;
        _bnkDir = bnkDir;
        EnsureRadioData();
        if (IsGameStation(CurrentStation) && !_originalPlaying) _pendingRestart = true;
    }

    private void EnsureRadioData()
    {
        if (!PlayOriginalRadio || _radioData != null || _radioDataTask != null) return;
        if (string.IsNullOrEmpty(_bnkDir)) _bnkDir = FindBnkDir();
        if (string.IsNullOrEmpty(_bnkDir)) return;
        string dir = _bnkDir;
        _radioDataTask = Task.Run(() => TDU2RadioData.Load(dir));
    }

    private static string FindBnkDir()
    {
        string proj = Directory.GetParent(Application.dataPath)?.FullName ?? Application.dataPath;
        string[] cands =
        {
            Path.Combine(proj, "Euro", "Bnk"),
            Path.Combine(Application.dataPath, "StreamingAssets", "Euro", "Bnk"),
            Path.Combine(proj, "Assets", "StreamingAssets", "Euro", "Bnk"),
            Path.Combine(proj, "Euro~", "Bnk"),
        };
        foreach (var c in cands)
            if (Directory.Exists(Path.Combine(c, "sound", "radio"))) return c;
        return null;
    }

    public void SetHudPlayer(VmfPlayer hud)
    {
        HudPlayer = hud;

        if (HudPlayer == null)
            return;
        HudPlayer.SetVisible("/HUD/radio", true);
        HudPlayer.ForceVisible("/HUD/radio", true);
        HudPlayer.ForceVisible("/HUD/radio/#2", false);

        UpdateHudText();
        ChangeStation(0);
    }

    private void LoadRadioTracks()
    {
        tracks.Clear();

        string[] files = Directory.GetFiles(radioFolderPath, "*", SearchOption.TopDirectoryOnly);

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        foreach (string path in files)
        {
            if (!string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ReadMetadata(path, out string title, out string artist);

            tracks.Add(new RadioTrack
            {
                FilePath = path,
                Title = title,
                Artist = artist
            });
        }
        tracksLoaded = true;
    }

    /// <summary>
    /// Radio stumm schalten (z.B. in Interieurs/Rennschule: dort läuft im Spiel der Spot-Sound, kein Autoradio).
    /// Sender läuft weiter (synchron), nur hörbar ist er nicht.
    /// </summary>
    public bool Muted
    {
        get => _muted;
        set { if (_muted == value) return; _muted = value; ApplyMute(); }
    }
    private bool _muted;
    private float _nextMuteCheck;

    private void ApplyMute()
    {
        if (mciOpened) MciCommand("setaudio " + MciAlias + " volume to " + (_muted ? 0 : CurrentVolume * 100));
        if (_radioSource != null) _radioSource.mute = _muted;
    }

    private void Update()
    {
        // neue Titel/Quellen übernehmen den Stumm-Zustand
        if (_muted && Time.unscaledTime >= _nextMuteCheck) { _nextMuteCheck = Time.unscaledTime + 0.25f; ApplyMute(); }
        if (Input.GetKeyUp(OpenRadio))
        {
            isOpen = !isOpen;
            LastStationChange = DateTime.Now;
            ChangeRadio();
        }

        // Diese Tasten wechseln ausschlie�lich Radiosender.
        if (Input.GetKeyUp(StationBack))
        {
            ChangeStation(-1);
        }
        else if (Input.GetKeyUp(StationNext))
        {
            ChangeStation(1);
        }
        else if (Input.GetKeyUp(VolumeUp))
        {
            ChangeVolume(1);
        }
        else if (Input.GetKeyUp(VolumeDown))
        {
            ChangeVolume(-1);
        }

        // Laufschrift unabh�ngig von der Spiel-Zeitskalierung.
        UpdateMarquee();

        // Original-Sender (Hariba / RoadRock) aus den Spiel-Bänken.
        PollRadioData();
        PollOriginalRadio();

        // Automatischen Titelwechsel bei Kaluna �berwachen.
        if (CurrentStation == RadioStation.kalua && tracksLoaded && mciOpened && Time.unscaledTime >= nextPlaybackCheck)
        {
            nextPlaybackCheck = Time.unscaledTime + 0.5f;

            string mode = MciQuery("status " + MciAlias + " mode");

            if (mode == "stopped")
            {
                PlayNextTrack();
            }
            else if (string.IsNullOrEmpty(mode))
            {
                StopTrack();
            }
        }
    }

    private void ChangeVolume(int delta)
    {
        if (!isOpen)
        {
            isOpen = true;
            LastStationChange = DateTime.Now;
            ChangeRadio();
        }

        CurrentVolume = Mathf.Clamp(CurrentVolume + delta, 0, 11);

        if (mciOpened)
        {
            MciCommand("setaudio " + MciAlias + " volume to " + (CurrentVolume * 100));
        }

        if (_radioSource != null)
        {
            _radioSource.volume = Mathf.Clamp01(CurrentVolume / 10f);
        }

        if (HudPlayer != null)
        {
            HudPlayer.SetFrame("/HUD/radio/volume", CurrentVolume);
        }
        PlayerPrefs.SetInt("TDU2.Opt.Volime", (int)CurrentVolume);
    }

    private void ChangeStation(int delta)
    {
        int count = Enum.GetValues(typeof(RadioStation)).Length;

        // delta 0 = aktuellen Sender nur neu starten (kein Wechsel).
        if (delta != 0)
        {
            int index = ((int)CurrentStation + (delta > 0 ? 1 : -1) + count) % count;
            CurrentStation = (RadioStation)index;
        }

        isOpen = true;
        LastStationChange = DateTime.Now;

        if (HudPlayer != null)
        {
            // gotoAndStop: das Logo darf nicht in die Frames der anderen Sender weiterlaufen (falsches Icon / Flimmern).
            HudPlayer.GotoLabel("/HUD/radio/radio_logo", CurrentStation.ToString(), false);
        }

        StopTrack();
        StopOriginalAudio();

        if (CurrentStation == RadioStation.kalua && tracksLoaded)
        {
            PlayNextTrack();
        }
        else if (IsGameStation(CurrentStation))
        {
            _pendingRestart = true;
            if (_radioData != null)
            {
                _pendingRestart = false;
                StartOriginalStation();
            }
        }
        else
        {
            currentTitle = "";
            currentArtist = "";
            titleScrollOffset = 0;
            artistScrollOffset = 0;
            UpdateHudText();
        }
        ChangeRadio();
        PlayerPrefs.SetInt("TDU2.Opt.Station", (int)CurrentStation);
    }

    private void PlayNextTrack()
    {
        if (!tracksLoaded ||
            tracks.Count == 0 ||
            CurrentStation != RadioStation.kalua)
        {
            return;
        }

        int nextIndex;

        if (ShuffleTracks && tracks.Count > 1)
        {
            nextIndex = UnityEngine.Random.Range(
                0,
                tracks.Count
            );

            if (nextIndex == currentTrackIndex)
            {
                nextIndex = (nextIndex + 1) % tracks.Count;
            }
        }
        else
        {
            nextIndex = (currentTrackIndex + 1) % tracks.Count;
        }

        PlayTrack(nextIndex);
    }

    private void PlayTrack(int index)
    {
        if (index < 0 ||
            index >= tracks.Count ||
            CurrentStation != RadioStation.kalua)
        {
            return;
        }

        StopTrack();

        RadioTrack track = tracks[index];

        string safePath = Path.GetFullPath(track.FilePath)
            .Replace("\"", "");

        int result = MciCommand(
            "open \"" + safePath +
            "\" type mpegvideo alias " + MciAlias
        );

        if (result != 0)
        {
            Debug.LogError(
                "[TDU2 Radio] MP3 konnte nicht ge�ffnet werden: " +
                track.FilePath
            );

            // Index vormerken, damit ein defekter Track
            // beim n�chsten Versuch �bersprungen werden kann.
            currentTrackIndex = index;
            return;
        }

        mciOpened = true;

        MciCommand(
            "setaudio " + MciAlias +
            " volume to " + (CurrentVolume * 100)
        );

        result = MciCommand("play " + MciAlias);

        if (result != 0)
        {
            Debug.LogError(
                "[TDU2 Radio] Wiedergabe fehlgeschlagen: " +
                track.Title
            );

            currentTrackIndex = index;
            StopTrack();
            return;
        }

        currentTrackIndex = index;
        nextPlaybackCheck = Time.unscaledTime + 0.5f;

        currentTitle = track.Title;
        currentArtist = track.Artist;

        titleScrollOffset = 0;
        artistScrollOffset = 0;
        nextMarqueeUpdate = Time.unscaledTime + MarqueeSpeed;

        Debug.Log(
            "[TDU2 Radio] Jetzt l�uft: " +
            track.Artist + " - " + track.Title
        );

        UpdateHudText();
    }

    private void StopTrack()
    {
        if (!mciOpened)
            return;

        MciCommand("stop " + MciAlias);
        MciCommand("close " + MciAlias);

        mciOpened = false;
    }

    private int MciCommand(string command)
    {
        int result = mciSendString(
            command,
            null,
            0,
            IntPtr.Zero
        );

        if (result != 0)
        {
            StringBuilder error = new StringBuilder(256);

            mciGetErrorString(
                result,
                error,
                error.Capacity
            );

            Debug.LogWarning(
                "[TDU2 Radio] MCI-Fehler: " +
                command + " -> " + error
            );
        }

        return result;
    }

    private string MciQuery(string command)
    {
        StringBuilder result = new StringBuilder(256);

        int error = mciSendString(
            command,
            result,
            result.Capacity,
            IntPtr.Zero
        );

        return error == 0
            ? result.ToString().Trim()
            : string.Empty;
    }

    // Aktualisiert Titel und Interpret getrennt.
    private void UpdateMarquee()
    {
        if (CurrentStation == RadioStation.off ||
            HudPlayer == null)
        {
            return;
        }

        if (Time.unscaledTime < nextMarqueeUpdate)
            return;

        nextMarqueeUpdate =
            Time.unscaledTime + Mathf.Max(0.05f, MarqueeSpeed);

        int visibleLength = Mathf.Max(1, MarqueeLength);

        if (currentTitle.Length > visibleLength)
        {
            titleScrollOffset++;
        }
        else
        {
            titleScrollOffset = 0;
        }

        if (currentArtist.Length > visibleLength)
        {
            artistScrollOffset++;
        }
        else
        {
            artistScrollOffset = 0;
        }

        UpdateHudText();
    }

    private string GetMarqueeText(string text, int offset)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        int length = Mathf.Max(1, MarqueeLength);

        // Kurze Texte werden nicht gescrollt.
        if (text.Length <= length)
            return text;

        string separator = MarqueeSeparator ?? "   -   ";

        // Wiederholung mit Abstand, damit der �bergang
        // vom Ende zum Anfang fl�ssig bleibt.
        string loopText = text + separator;
        StringBuilder output = new StringBuilder(length);

        for (int i = 0; i < length; i++)
        {
            int charIndex = (offset + i) % loopText.Length;
            output.Append(loopText[charIndex]);
        }

        return output.ToString();
    }

    private void UpdateHudText()
    {
        if (HudPlayer == null)
            return;

        string stationName = StationDisplayName(CurrentStation);

        string artist = GetMarqueeText(
            currentArtist,
            artistScrollOffset
        );

        string title = GetMarqueeText(
            currentTitle,
            titleScrollOffset
        );

        HudPlayer.SetVariable("/:RadioTitle", stationName);
        HudPlayer.SetVariable("/:RadioSinger", artist);
        HudPlayer.SetVariable("/:RadioTrack", title);
    }

    // ------------------------------------------------------------------
    //  Original-Radio (Hariba / RoadRock): Assets aus Euro/Bnk/sound/radio
    // ------------------------------------------------------------------

    private static bool IsGameStation(RadioStation st)
    {
        return st == RadioStation.hariba || st == RadioStation.roadtrack;
    }

    private RadioStationInfo StationInfo(RadioStation st)
    {
        return _radioData != null ? _radioData.StationByLabel(st.ToString()) : null;
    }

    private string StationDisplayName(RadioStation st)
    {
        var info = StationInfo(st);
        if (info != null && !string.IsNullOrEmpty(info.Name)) return info.Name;
        return st.ToString();
    }

    private void PollRadioData()
    {
        if (_radioDataTask != null && _radioDataTask.IsCompleted)
        {
            var t = _radioDataTask;
            _radioDataTask = null;

            if (t.IsFaulted)
            {
                Debug.LogError("[TDU2 Radio] Radio-Daten nicht lesbar: " + t.Exception?.GetBaseException().Message);
            }
            else
            {
                _radioData = t.Result;
                if (!string.IsNullOrEmpty(_radioData.Error))
                    Debug.LogWarning("[TDU2 Radio] " + _radioData.Error);
                else
                    Debug.Log("[TDU2 Radio] " + _radioData.Stations.Count + " Sender, " + _radioData.Messages.Count + " Nachrichten, " + _radioData.BankPaths.Count + " Bänke.");
            }

            if (IsGameStation(CurrentStation) && _pendingRestart)
            {
                _pendingRestart = false;
                StartOriginalStation();
            }
        }
    }

    /// <summary>Online? Dann läuft der Originalsender auf der gemeinsamen Server-Zeitleiste (alle Clients gleich).</summary>
    private bool UseSync => PlayOriginalRadio && TDU2Session.LoggedIn && TDU2Session.HasRadioClock && _radioData != null && IsGameStation(CurrentStation);

    private void PollOriginalRadio()
    {
        // Dekodierung eines Segments abgeschlossen?
        if (_decodeTask != null && _decodeTask.IsCompleted)
        {
            var t = _decodeTask;
            _decodeTask = null;
            var wav = t.IsFaulted ? new DecodedWav() : t.Result;

            // Bei synchroner Wiedergabe nur übernehmen, wenn das Segment noch das gewünschte ist.
            bool current = _originalPlaying && _segmentPlaying == false && _decodeStation == CurrentStation && IsGameStation(CurrentStation)
                           && (!UseSync || ReferenceEquals(_decodeMessage, _syncMessage));

            if (current && wav.Samples != null && wav.Samples.Length > 0)
            {
                int frames = wav.Samples.Length / Mathf.Max(1, wav.Channels);
                if (_radioClip != null) { Destroy(_radioClip); _radioClip = null; }
                _radioClip = AudioClip.Create("TDU2RadioClip", frames, wav.Channels, wav.Rate, false);
                _radioClip.SetData(wav.Samples, 0);
                EnsureSource();
                _radioSource.clip = _radioClip;
                _radioSource.volume = Mathf.Clamp01(CurrentVolume / 10f);
                _radioSource.loop = false;
                _radioSource.Play();
                _segmentPlaying = true;

                // Auf die gemeinsame Sendezeit ziehen (Dekodierung hat etwas gedauert).
                if (!double.IsNaN(_seekBaseAt))
                {
                    float off = _seekBase + (float)(TDU2Session.RadioElapsedSeconds - _seekBaseAt);
                    SeekAudio(off);
                    _seekBaseAt = double.NaN;
                }
            }
            else if (current)
            {
                // Bank fehlt/defekt -> Segment erneut versuchen.
                _waitRetry = true;
                _retryAt = Time.unscaledTime + 0.3f;
            }
            return;
        }

        if (!_originalPlaying) return;

        if (_waitRetry)
        {
            if (Time.unscaledTime >= _retryAt && _decodeTask == null)
            {
                _waitRetry = false;
                if (!UseSync) AdvanceSegment();
            }
            return;
        }

        // Synchron: Segment folgt aus der Server-Zeit. Sonst: Segment zu Ende -> weiterschalten.
        if (UseSync)
        {
            UpdateSyncedRadio();
            return;
        }

        if (_segmentPlaying && _radioSource != null && !_radioSource.isPlaying)
        {
            _segmentPlaying = false;
            AdvanceSegment();
        }
    }

    private void SeekAudio(float seconds)
    {
        if (_radioSource == null || _radioSource.clip == null) return;
        float len = _radioSource.clip.length;
        _radioSource.time = Mathf.Clamp(seconds, 0f, Mathf.Max(0f, len - 0.05f));
    }

    /// <summary>Aus der Server-Zeit den gerade laufenden Titel samt Position bestimmen und abspielen.</summary>
    private void UpdateSyncedRadio()
    {
        var info = StationInfo(CurrentStation);
        if (info == null) return;

        var playlist = GetPlaylist(info.Hashcode);
        if (playlist == null || playlist.Total <= 0.5f) return;

        double elapsed = TDU2Session.RadioElapsedSeconds;
        double pos = elapsed - Math.Floor(elapsed / playlist.Total) * playlist.Total;
        var seg = playlist.At(pos, out float offset);
        if (seg == null) return;
        var msg = FitConditions(seg, info.Hashcode, Math.Floor(elapsed / playlist.Total));

        if (!ReferenceEquals(msg, _syncMessage))
        {
            _syncMessage = msg;
            _segmentPlaying = false;
            _seekBase = offset;
            _seekBaseAt = elapsed;
            PlayMessage(msg);
            return;
        }

        if (_segmentPlaying && _radioSource != null && _radioSource.clip != null && _radioSource.isPlaying)
        {
            // Nur bei deutlicher Abweichung nachziehen (kleine Drift ist unhörbar).
            if (Mathf.Abs(_radioSource.time - offset) > 2f) SeekAudio(offset);
        }
        else if (_decodeTask == null && !_waitRetry)
        {
            _seekBase = offset;
            _seekBaseAt = elapsed;
            PlayMessage(msg);
        }
    }

    readonly Dictionary<long, RadioMessage> _fitCache = new Dictionary<long, RadioMessage>();

    /// <summary>
    /// Synchrones Radio: steht im Ablauf eine Wetter- oder Tageszeit-Moderation, die nicht zum aktuellen Wetter/zur Uhrzeit
    /// passt (TDU2Weather – auf allen Clients gleich), wird sie durch eine passende ersetzt. Auswahl deterministisch
    /// (Zyklus + Segment), bevorzugt eine, die in die Lücke passt -> alle Spieler hören dieselbe Meldung.
    /// </summary>
    private RadioMessage FitConditions(TDU2RadioData.RadioSegment seg, int stationHash, double cycle)
    {
        var m = seg.Message;
        if (m == null || TDU2.Map.World.TDU2Weather.Instance == null) return m;
        bool isWeather = m.Type == RadioMessageType.WeatherRain || m.Type == RadioMessageType.WeatherStorm || m.Type == RadioMessageType.WeatherSun;
        bool isTime = m.Type == RadioMessageType.TimeMidday || m.Type == RadioMessageType.TimeNightfall || m.Type == RadioMessageType.TimeSunrise;
        if (!isWeather && !isTime) return m;
        RadioMessageType? want = isWeather ? TDU2.Map.World.TDU2Weather.RadioWeather() : TDU2.Map.World.TDU2Weather.RadioTime();
        if (want.HasValue && want.Value == m.Type) return m;
        long key = ((long)cycle << 24) ^ (long)(seg.Start * 10f) ^ ((long)(want.HasValue ? (int)want.Value : 99) << 56);
        if (_fitCache.TryGetValue(key, out var hit)) return hit;
        if (_fitCache.Count > 256) _fitCache.Clear();
        var cand = want.HasValue ? _radioData.ForStation(stationHash, want.Value) : new List<RadioMessage>();
        if (cand.Count == 0) cand = _radioData.ForStation(stationHash, RadioMessageType.Short, RadioMessageType.BeforeMusic);
        RadioMessage best = m;
        if (cand.Count > 0)
        {
            // passende Länge bevorzugen, bei Gleichstand deterministisch nach Hash
            uint h = (uint)(key * 2654435761L) ^ 0x5bd1e995u;
            float bestScore = float.MaxValue;
            for (int i = 0; i < cand.Count; i++)
            {
                float d = _radioData.DurationOf(cand[i]);
                float score = (d > seg.Duration + 0.5f ? 1000f : 0f) + Mathf.Abs(seg.Duration - d) + ((h + (uint)i * 2246822519u) % 997u) * 0.001f;
                if (score < bestScore) { bestScore = score; best = cand[i]; }
            }
        }
        _fitCache[key] = best;
        return best;
    }

    /// <summary>Deterministische Playlist des Senders (aus Server-Startwert) – auf allen Clients identisch.</summary>
    private TDU2RadioData.RadioPlaylist GetPlaylist(int stationHash)
    {
        int seed = TDU2Session.RadioSeed;
        if (_syncPlaylist != null && _syncCycleStation == stationHash && _syncCycleSeed == seed) return _syncPlaylist;
        _syncPlaylist = _radioData.BuildPlaylist(stationHash, seed, JingleChance, CommentChance, AdChance);
        _syncCycleStation = stationHash;
        _syncCycleSeed = seed;
        return _syncPlaylist;
    }

    private void StartOriginalStation()
    {
        if (_radioData == null)
        {
            _pendingRestart = true;
            return;
        }

        _originalPlaying = true;
        _playedAnySegment = false;
        _lastWasMusic = false;
        _tracksSinceJingle = 99;
        _stationQueue.Clear();
        _queueIndex = 0;
        _segmentPlaying = false;
        _waitRetry = false;
        _syncMessage = null;
        _seekBaseAt = double.NaN;
        if (!UseSync) AdvanceSegment();   // synchron übernimmt UpdateSyncedRadio
    }

    private void StopOriginalAudio()
    {
        _originalPlaying = false;
        _segmentPlaying = false;
        _waitRetry = false;
        _pendingRestart = false;
        _syncMessage = null;
        _seekBaseAt = double.NaN;

        if (_radioSource != null && _radioSource.isPlaying)
            _radioSource.Stop();
        if (_radioClip != null)
        {
            Destroy(_radioClip);
            _radioClip = null;
        }
        _currentMessage = null;
    }

    private void AdvanceSegment()
    {
        if (!_originalPlaying || _radioData == null) return;

        var info = StationInfo(CurrentStation);
        if (info == null) return;

        RadioMessage m = null;

        // Musik ist der Standard. Zwischenstücke (Jingle/Kommentar/Werbung) laufen nur
        // NACH einem Song und höchstens eines am Stück – so ist der Titel fast immer sichtbar.
        if (_playedAnySegment && _lastWasMusic)
        {
            bool forceJingle = _tracksSinceJingle >= 3;
            float roll = UnityEngine.Random.value;

            if (TDU2.Map.World.TDU2Weather.NewsPending)
                m = PickComment(info.Hashcode);   // Wetterwechsel: Wetternachricht als nächstes Zwischenstück
            if (m != null) { }
            else if (forceJingle || roll < JingleChance)
                m = PickRandom(info.Hashcode, RadioMessageType.Jingle);
            else if (roll < JingleChance + CommentChance)
                m = PickComment(info.Hashcode);
            else if (roll < JingleChance + CommentChance + AdChance)
                m = PickRandom(info.Hashcode, RadioMessageType.Advertising);
        }

        if (m == null)
        {
            m = NextMusic(info.Hashcode);
            if (m != null)
            {
                _tracksSinceJingle++;
                _lastWasMusic = true;
            }
        }
        else
        {
            _lastWasMusic = false;
            if (m.Type == RadioMessageType.Jingle) _tracksSinceJingle = 0;
        }

        if (m == null) return;
        _playedAnySegment = true;
        PlayMessage(m);
    }

    private RadioMessage NextMusic(int stationHashcode)
    {
        if (_stationQueue.Count == 0)
        {
            _stationQueue.AddRange(_radioData.ForStation(stationHashcode, RadioMessageType.Music));
            Shuffle(_stationQueue);
            _queueIndex = 0;
        }
        if (_stationQueue.Count == 0) return null;

        var m = _stationQueue[_queueIndex];
        _queueIndex++;
        if (_queueIndex >= _stationQueue.Count)
        {
            _queueIndex = 0;
            Shuffle(_stationQueue);
        }
        return m;
    }

    private RadioMessage PickRandom(int stationHashcode, RadioMessageType type)
    {
        var list = _radioData.ForStation(stationHashcode, type);
        if (list.Count == 0) return null;
        return list[UnityEngine.Random.Range(0, list.Count)];
    }

    /// <summary>
    /// Moderation passend zu Wetter und Tageszeit (TDU2Weather, für alle Spieler gleich): Regen-/Gewitter-/Sonnenmeldung nur
    /// beim passenden Wetter, Sonnenaufgang/Mittag/Abend nur zur passenden Uhrzeit. Nach einem Wetterwechsel kommt zuerst
    /// die Wettermeldung.
    /// </summary>
    private RadioMessage PickComment(int stationHashcode)
    {
        var weather = TDU2.Map.World.TDU2Weather.Instance;
        if (weather != null)
        {
            RadioMessageType? wt = TDU2.Map.World.TDU2Weather.RadioWeather(), tt = TDU2.Map.World.TDU2Weather.RadioTime();
            if (TDU2.Map.World.TDU2Weather.NewsPending)
            {
                TDU2.Map.World.TDU2Weather.NewsPending = false;
                if (wt.HasValue)
                {
                    var news = _radioData.ForStation(stationHashcode, wt.Value);
                    if (news.Count > 0) return news[UnityEngine.Random.Range(0, news.Count)];
                }
            }
            var types = new List<RadioMessageType> { RadioMessageType.BeforeAd, RadioMessageType.BeforeMusic, RadioMessageType.Short };
            if (wt.HasValue) types.Add(wt.Value);
            if (tt.HasValue) types.Add(tt.Value);
            var fit = _radioData.ForStation(stationHashcode, types.ToArray());
            if (fit.Count > 0) return fit[UnityEngine.Random.Range(0, fit.Count)];
        }
        var list = _radioData.ForStation(stationHashcode,
            RadioMessageType.BeforeAd, RadioMessageType.BeforeMusic, RadioMessageType.Short,
            RadioMessageType.TimeMidday, RadioMessageType.TimeNightfall, RadioMessageType.TimeSunrise,
            RadioMessageType.WeatherRain, RadioMessageType.WeatherStorm, RadioMessageType.WeatherSun);
        if (list.Count == 0) return null;
        return list[UnityEngine.Random.Range(0, list.Count)];
    }

    private void PlayMessage(RadioMessage m)
    {
        _currentMessage = m;
        _decodeMessage = m;
        _segmentPlaying = false;

        string path = _radioData.BankPathFor(m);
        if (path != null)
        {
            _decodeStation = CurrentStation;
            _decodeTask = Task.Run(() => DecodeBankWav(path));
        }
        else
        {
            _waitRetry = true;
            _retryAt = Time.unscaledTime + 0.3f;
        }

        currentArtist = m.HasMetadata ? (m.Artist ?? "") : "";
        currentTitle = m.HasMetadata ? (m.Track ?? "") : "";
        titleScrollOffset = 0;
        artistScrollOffset = 0;
        nextMarqueeUpdate = Time.unscaledTime + MarqueeSpeed;
        UpdateHudText();
    }

    private static DecodedWav DecodeBankWav(string path)
    {
        try
        {
            var bank = BnkArchive.Open(path);

            // Bevorzugt echte .wav-Einträge (der Namensbaum kann bei Dateinamen mit
            // Punkt die Endung falsch zuordnen, deshalb anschließend roher Versuch).
            foreach (var e in bank.All(".wav"))
                if (TryDecodeEntry(bank, e, out var wav)) return wav;

            foreach (var e in bank.Entries)
                if (TryDecodeEntry(bank, e, out var wav)) return wav;
        }
        catch (Exception)
        {
        }
        return new DecodedWav();
    }

    private static bool TryDecodeEntry(BnkArchive bank, BnkEntry e, out DecodedWav wav)
    {
        wav = new DecodedWav();
        try
        {
            if (WavDecoder.TryDecode(bank.Read(e), out var samples, out int channels, out int rate) &&
                samples != null && samples.Length > 0 && channels > 0 && rate > 0)
            {
                wav = new DecodedWav { Samples = samples, Channels = channels, Rate = rate };
                return true;
            }
        }
        catch (Exception)
        {
        }
        return false;
    }

    private void EnsureSource()
    {
        if (_radioSource == null)
        {
            _radioSource = gameObject.AddComponent<AudioSource>();
            _radioSource.playOnAwake = false;
            _radioSource.spatialBlend = 0f;
            _radioSource.dopplerLevel = 0f;
        }
    }

    private static void Shuffle(List<RadioMessage> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            var tmp = list[i];
            list[i] = list[j];
            list[j] = tmp;
        }
    }

    private static void ReadMetadata(string path, out string title, out string artist)
    {
        title = Path.GetFileNameWithoutExtension(path);
        artist = "Unbekannter Interpret";

        try
        {
            using (FileStream stream = File.OpenRead(path))
            using (BinaryReader reader = new BinaryReader(stream))
            {
                if (stream.Length >= 10)
                {
                    byte[] header = reader.ReadBytes(10);

                    if (header[0] == 'I' &&
                        header[1] == 'D' &&
                        header[2] == '3')
                    {
                        int version = header[3];
                        int tagSize = ReadSyncSafeInt(header, 6);

                        long tagEnd = Math.Min(
                            stream.Length,
                            10L + tagSize
                        );

                        while (stream.Position + 10 <= tagEnd)
                        {
                            byte[] frame = reader.ReadBytes(10);

                            string id = Encoding.ASCII.GetString(
                                frame,
                                0,
                                4
                            );

                            if (id == "\0\0\0\0")
                                break;

                            int size = version == 4
                                ? ReadSyncSafeInt(frame, 4)
                                : ReadBigEndianInt(frame, 4);

                            if (size <= 0 ||
                                stream.Position + size > tagEnd)
                            {
                                break;
                            }

                            byte[] data = reader.ReadBytes(size);
                            string value = DecodeTextFrame(data);

                            if (id == "TIT2" &&
                                !string.IsNullOrWhiteSpace(value))
                            {
                                title = value;
                            }
                            else if (id == "TPE1" &&
                                     !string.IsNullOrWhiteSpace(value))
                            {
                                artist = value;
                            }
                        }
                    }
                }

                // ID3v1 als Fallback.
                if (artist == "Unbekannter Interpret" ||
                    string.IsNullOrWhiteSpace(title))
                {
                    if (stream.Length >= 128)
                    {
                        stream.Position = stream.Length - 128;

                        byte[] tag = reader.ReadBytes(128);

                        if (Encoding.ASCII.GetString(tag, 0, 3) == "TAG")
                        {
                            string v1Title = ReadLatin1(tag, 3, 30);
                            string v1Artist = ReadLatin1(tag, 33, 30);

                            if (!string.IsNullOrWhiteSpace(v1Title))
                                title = v1Title;

                            if (!string.IsNullOrWhiteSpace(v1Artist))
                                artist = v1Artist;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning(
                "[TDU2 Radio] ID3-Lesefehler: " + ex.Message
            );
        }
    }

    private static int ReadSyncSafeInt(byte[] data, int offset)
    {
        return (data[offset] & 0x7F) << 21 |
               (data[offset + 1] & 0x7F) << 14 |
               (data[offset + 2] & 0x7F) << 7 |
               (data[offset + 3] & 0x7F);
    }

    private static int ReadBigEndianInt(byte[] data, int offset)
    {
        return data[offset] << 24 |
               data[offset + 1] << 16 |
               data[offset + 2] << 8 |
               data[offset + 3];
    }

    private static string DecodeTextFrame(byte[] data)
    {
        if (data == null || data.Length <= 1)
            return string.Empty;

        int encoding = data[0];
        int length = data.Length - 1;

        byte[] textBytes = new byte[length];

        Buffer.BlockCopy(data, 1, textBytes, 0, length);

        string value;

        switch (encoding)
        {
            case 0:
                value = DecodeLatin1(textBytes);
                break;

            case 1:
                value = DecodeUnicode(textBytes, false);
                break;

            case 2:
                value = DecodeUnicode(textBytes, true);
                break;

            case 3:
                value = Encoding.UTF8.GetString(textBytes);
                break;

            default:
                return string.Empty;
        }

        return value.Trim('\0', ' ', '\r', '\n', '\t');
    }

    private static string DecodeLatin1(byte[] data)
    {
        char[] chars = new char[data.Length];

        for (int i = 0; i < data.Length; i++)
            chars[i] = (char)data[i];

        return new string(chars);
    }

    private static string DecodeUnicode(byte[] data, bool bigEndian)
    {
        int offset = 0;

        if (data.Length >= 2)
        {
            if (data[0] == 0xFF && data[1] == 0xFE)
            {
                bigEndian = false;
                offset = 2;
            }
            else if (data[0] == 0xFE && data[1] == 0xFF)
            {
                bigEndian = true;
                offset = 2;
            }
        }

        int length = data.Length - offset;
        length -= length % 2;

        if (length <= 0)
            return string.Empty;

        return (bigEndian
                ? Encoding.BigEndianUnicode
                : Encoding.Unicode)
            .GetString(data, offset, length);
    }

    private static string ReadLatin1(byte[] data, int offset, int length)
    {
        char[] chars = new char[length];

        for (int i = 0; i < length; i++)
            chars[i] = (char)data[offset + i];

        return new string(chars).Trim(
            '\0', ' ', '\r', '\n', '\t'
        );
    }

    public void UpdateRadio(string station, string song, string title)
    {
        if (HudPlayer == null)
            return;

        HudPlayer.SetVariable("/:RadioTitle", station);
        HudPlayer.SetVariable("/:RadioSinger", title);
        HudPlayer.SetVariable("/:RadioTrack", song);
    }

    public void ChangeRadio()
    {
        if (HudPlayer == null)
            return;

        HudPlayer.GotoLabel("/HUD/radio", isOpen ? "open" : "close", true);
    }

    private void LateUpdate()
    {
        if (HudPlayer == null)
            return;

        if (isOpen && (DateTime.Now - LastStationChange).TotalSeconds > 5)
        {
            isOpen = false;
            ChangeRadio();
        }
    }

    private void OnDestroy()
    {
        if (Instance != this)
            return;

        StopTrack();
        StopOriginalAudio();
        if (_radioClip != null) { Destroy(_radioClip); _radioClip = null; }
        Instance = null;
    }
}
