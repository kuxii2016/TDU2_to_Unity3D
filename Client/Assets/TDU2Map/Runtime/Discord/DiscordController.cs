
using System;
using System.Text;
using UnityEngine;

/// <summary>
/// Discord Rich Presence for Test Drive Unlimited 2.
/// Handles initialization, queued updates, rate limiting and reconnection.
/// </summary>
public class DiscordController : MonoBehaviour
{
    public const string DefaultApplicationId = "1564941401722986496";

    public static DiscordController Instance;

    [Tooltip("Discord application id used for Rich Presence.")]
    public string applicationId = DefaultApplicationId;

    public string optionalSteamId;

    [Tooltip("Key of the large image uploaded to the Discord application.")]
    public string LargeImageKey = "images";

    [Tooltip("Optional key of a small image.")]
    public string SmallImageKey = "";

    public string SmallImageText = "";

    public long Starttime;

    public DiscordRpc.RichPresence presence = new DiscordRpc.RichPresence();

    DiscordRpc.EventHandlers handlers;

    bool _initialized;
    bool _failed;
    volatile bool _ready;

    float _nextCallback;
    float _nextSend;
    float _nextReconnect;

    string _lastKey;
    string _details;
    string _state;
    string _largeText;

    long _startTimestamp;
    bool _hasPending;

    const float MinSendInterval = 5f;
    const float CallbackInterval = 1f;
    const float ReconnectInterval = 5f;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoCreate()
    {
        if (Instance == null)
            Ensure();
    }

    public static DiscordController Ensure()
    {
        if (Instance != null)
            return Instance;

        GameObject go = new GameObject("Discord RPC");
        DontDestroyOnLoad(go);

        return go.AddComponent<DiscordController>();
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        DontDestroyOnLoad(gameObject);

        if (string.IsNullOrWhiteSpace(applicationId))
            applicationId = DefaultApplicationId;
    }

    void Start()
    {
        Starttime = NowUnix();

        if (!_hasPending)
        {
            Queue(
                "Test Drive Unlimited 2",
                "Main Menu",
                "Starting the game",
                Starttime
            );
        }

        EnsureInitialized();
    }

    void OnDestroy()
    {
        if (Instance != this)
            return;

        Shutdown();

        Instance = null;
    }

    void OnDisable()
    {
        if (Instance == this)
            Shutdown();
    }

    void Shutdown()
    {
        if (_initialized)
        {
            try
            {
                DiscordRpc.Shutdown();
            }
            catch (Exception ex)
            {
                DebugManager.Warning("[Discord] Shutdown: " + ex.Message);
            }
        }

        _initialized = false;
        _ready = false;
    }

    void Update()
    {
        // Retry initialization after a connection failure.
        if (!_initialized)
        {
            if (!_failed && Time.unscaledTime >= _nextReconnect)
            {
                _nextReconnect = Time.unscaledTime + ReconnectInterval;
                EnsureInitialized();
            }

            return;
        }

        // Process Discord callbacks.
        if (Time.unscaledTime >= _nextCallback)
        {
            _nextCallback = Time.unscaledTime + CallbackInterval;

            try
            {
                DiscordRpc.RunCallbacks();
            }
            catch (Exception ex)
            {
                DebugManager.Error("Callback error: " + ex.Message);
                Shutdown();
                _nextReconnect = Time.unscaledTime + ReconnectInterval;
                return;
            }
        }

        // Send queued presence only after Discord is ready.
        if (_ready && _hasPending && Time.unscaledTime >= _nextSend)
        {
            Flush(false);
        }
    }

    void EnsureInitialized()
    {
        if (_initialized || _failed)
            return;

        if (string.IsNullOrWhiteSpace(applicationId))
            applicationId = DefaultApplicationId;

        try
        {
            handlers = new DiscordRpc.EventHandlers
            {
                readyCallback = OnReady,
                disconnectedCallback = OnDisconnected,
                errorCallback = OnError,
                joinCallback = OnJoin,
                spectateCallback = OnSpectate,
                requestCallback = OnRequest
            };


            DiscordRpc.Initialize(
                applicationId,
                ref handlers,
                true,
                optionalSteamId ?? ""
            );

            _initialized = true;
            _ready = false;

            _nextCallback = 0f;
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    void Fail(Exception ex)
    {
        _failed = false;
        _initialized = false;
        _ready = false;

        _nextReconnect = Time.unscaledTime + ReconnectInterval;

        DebugManager.Error(
            "Rich Presence initialization failed: " +
            ex.GetType().Name + " - " + ex.Message
        );
    }

    // Native Discord callbacks.
    // Do not call Unity APIs from these callbacks.

    void OnReady(ref DiscordRpc.DiscordUser user)
    {
        _ready = true;
    }

    void OnDisconnected(int code, string message)
    {
        _ready = false;

        Debug.LogWarning(
            "[Discord] Disconnected: " + code + " - " + message
        );
    }

    void OnError(int code, string message)
    {
        Debug.LogError(
            "[Discord] Error: " + code + " - " + message
        );
    }

    void OnJoin(string secret)
    {
        Debug.Log("[Discord] Join request: " + secret);
    }

    void OnSpectate(string secret)
    {
        Debug.Log("[Discord] Spectate request: " + secret);
    }

    void OnRequest(ref DiscordRpc.DiscordUser user)
    {
        Debug.Log("[Discord] Activity request from: " + user.username);
    }

    /// <summary>
    /// Queue a presence update.
    /// line1 = large image tooltip
    /// line2 = details
    /// line3 = state
    /// </summary>
    public static void UpdatePresence(
        string line1,
        string line2,
        string line3)
    {
        UpdatePresence(line1, line2, line3, 0);
    }

    /// <summary>
    /// Queue a presence update.
    /// startTimestamp = 0 keeps the original game start timestamp.
    /// </summary>
    public static void UpdatePresence(
        string line1,
        string line2,
        string line3,
        long startTimestamp)
    {
        DiscordController c = Instance != null
            ? Instance
            : Ensure();

        if (c == null)
            return;

        c.Queue(line1, line2, line3, startTimestamp);
    }

    void Queue(
        string line1,
        string line2,
        string line3,
        long startTimestamp)
    {
        string newLargeText = Fit(line1);
        string newDetails = Fit(line2);
        string newState = Fit(line3);

        long newTimestamp = startTimestamp > 0
            ? startTimestamp
            : (Starttime > 0 ? Starttime : NowUnix());

        string newKey =
            newLargeText + "|" +
            newDetails + "|" +
            newState + "|" +
            newTimestamp;

        string currentKey =
            _largeText + "|" +
            _details + "|" +
            _state + "|" +
            _startTimestamp;

        if (newKey == currentKey)
            return;

        _largeText = newLargeText;
        _details = newDetails;
        _state = newState;
        _startTimestamp = newTimestamp;

        _hasPending = true;

        if (_ready && Time.unscaledTime >= _nextSend)
            Flush(false);
    }

    void Flush(bool force)
    {
        if (!_hasPending)
            return;

        EnsureInitialized();

        if (!_initialized || !_ready || _failed)
            return;

        string key =
            _largeText + "|" +
            _details + "|" +
            _state + "|" +
            _startTimestamp;

        if (!force)
        {
            if (key == _lastKey)
            {
                _hasPending = false;
                return;
            }

            if (Time.unscaledTime < _nextSend)
                return;
        }

        presence.largeImageKey =
            string.IsNullOrEmpty(LargeImageKey)
                ? null
                : LargeImageKey;

        presence.largeImageText = _largeText;
        presence.details = _details;
        presence.state = _state;
        presence.startTimestamp = _startTimestamp;

        presence.smallImageKey =
            string.IsNullOrEmpty(SmallImageKey)
                ? null
                : SmallImageKey;

        presence.smallImageText =
            string.IsNullOrEmpty(SmallImageText)
                ? null
                : SmallImageText;

        try
        {
            DiscordRpc.UpdatePresence(presence);

            _lastKey = key;
            _hasPending = false;

            _nextSend = Time.unscaledTime + MinSendInterval;

        }
        catch (Exception ex)
        {
            DebugManager.Error("UpdatePresence failed: " + ex.Message);

            _hasPending = true;

            Shutdown();

            _nextReconnect = Time.unscaledTime + ReconnectInterval;
        }
    }

    /// <summary>
    /// Trim text to Discord's byte limit without splitting UTF-8 characters.
    /// </summary>
    static string Fit(string s, int maxBytes = 120)
    {
        if (string.IsNullOrEmpty(s))
            return "";

        s = s.Replace('\r', ' ')
             .Replace('\n', ' ')
             .Trim();

        if (Encoding.UTF8.GetByteCount(s) <= maxBytes)
            return s;

        int n = s.Length;

        while (n > 0 &&
               Encoding.UTF8.GetByteCount(s.Substring(0, n)) > maxBytes - 3)
        {
            n--;
        }

        // Avoid cutting a UTF-16 surrogate pair.
        if (n > 0 && n < s.Length &&
            char.IsHighSurrogate(s[n - 1]) &&
            char.IsLowSurrogate(s[n]))
        {
            n--;
        }

        return s.Substring(0, n).TrimEnd() + "…";
    }

    static long NowUnix()
    {
        return (long)(
            DateTime.UtcNow -
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        ).TotalSeconds;
    }
}
