using System;
using UnityEngine;
using UnityEngine.SocialPlatforms;

[System.Serializable]
public class DiscordJoinEvent : UnityEngine.Events.UnityEvent<string> { }

[System.Serializable]
public class DiscordSpectateEvent : UnityEngine.Events.UnityEvent<string> { }

[System.Serializable]
public class DiscordJoinRequestEvent : UnityEngine.Events.UnityEvent<DiscordRpc.DiscordUser> { }

public class DiscordController : MonoBehaviour
{
    public static DiscordController Instance;
    public long Starttime;
    public DiscordRpc.RichPresence presence = new DiscordRpc.RichPresence();
    public string applicationId;
    public string optionalSteamId;
    public DiscordRpc.DiscordUser joinRequest;
    public UnityEngine.Events.UnityEvent onConnect;
    public UnityEngine.Events.UnityEvent onDisconnect;
    public UnityEngine.Events.UnityEvent hasResponded;
    public DiscordJoinEvent onJoin;
    public DiscordJoinEvent onSpectate;
    public DiscordJoinRequestEvent onJoinRequest;
    DiscordRpc.EventHandlers handlers;

    void Start()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this.gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(this.gameObject);

        handlers = new DiscordRpc.EventHandlers();
        handlers.readyCallback += ReadyCallback;
        handlers.disconnectedCallback += DisconnectedCallback;
        handlers.errorCallback += ErrorCallback;
        handlers.joinCallback += JoinCallback;
        handlers.spectateCallback += SpectateCallback;
        handlers.requestCallback += RequestCallback;
        DiscordRpc.Initialize(applicationId, ref handlers, true, optionalSteamId);

        initPrecence();
    }

    void OnEnable()
    {
    }

    private void initPrecence()
    {
        Starttime = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        //Icon Paar
        presence.largeImageKey = "images";
        presence.largeImageText = "Main Menue";
        presence.startTimestamp = Starttime;
        presence.details = "Game Launched";
        presence.state = "Main-Menue";
        DiscordRpc.UpdatePresence(presence);
    }

    public static void UpdatePresence(string Line1, string Line2, string Line3)
    {
        Instance.presence.largeImageKey = "images";
        Instance.presence.startTimestamp = Instance.Starttime;

        //Tooltip
        Instance.presence.largeImageText = Line1;

        //main RPC Firstline, SecondLine
        Instance.presence.details = Line2;
        Instance.presence.state = Line3;

        DiscordRpc.UpdatePresence(Instance.presence);
    }

    void OnDisable()
    {
        DiscordRpc.Shutdown();
    }

    void Update()
    {
        DiscordRpc.RunCallbacks();
    }

    public void RequestRespondYes()
    {
        DiscordRpc.Respond(joinRequest.userId, DiscordRpc.Reply.Yes);
        hasResponded.Invoke();
    }

    public void RequestRespondNo()
    {
        DiscordRpc.Respond(joinRequest.userId, DiscordRpc.Reply.No);
        hasResponded.Invoke();
    }

    public void ReadyCallback(ref DiscordRpc.DiscordUser connectedUser)
    {
        onConnect.Invoke();
    }

    public void DisconnectedCallback(int errorCode, string message)
    {
        onDisconnect.Invoke();
    }

    public void ErrorCallback(int errorCode, string message)
    {
        UnityEngine.Debug.Log(string.Format("Discord: error {0}: {1}", errorCode, message));
    }

    public void JoinCallback(string secret)
    {
        onJoin.Invoke(secret);
    }

    public void SpectateCallback(string secret)
    {
        onSpectate.Invoke(secret);
    }

    public void RequestCallback(ref DiscordRpc.DiscordUser request)
    {
        joinRequest = request;
        onJoinRequest.Invoke(request);
    }

}
