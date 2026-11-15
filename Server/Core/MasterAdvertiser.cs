using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TDU2Server.Core;

/// <summary>
/// Meldet diesen Server beim Paradise-MasterServer an (Serverliste im Launcher),
/// hält den Eintrag per Heartbeat aktuell (Spielerzahl, Mod-Nachricht) und meldet
/// ihn beim Beenden sauber wieder ab.
///
/// Ablauf:
///   Start      → POST /api/register  (komplette ServerInfo, liefert Token)
///   alle 10 s  → POST /api/heartbeat (Token + Spielerzahl)
///   Änderung   → POST /api/register  (Name/Host/Port/Version/Locked neu setzen)
///   404        → Token ungültig (Master neu gestartet) → sofort neu anmelden
///   Beenden    → POST /api/unregister
///
/// Die JSON-Felder entsprechen Paradise.Core.Net (MasterApi/ServerInfo), damit
/// MasterServer und Launcher ohne Anpassung zusammenpassen. Das Server-Projekt
/// bleibt dabei unabhängig von Paradise.Core (eigene, kleine DTOs).
/// </summary>
public sealed class MasterAdvertiser
{
    // Routen wie in Paradise.Core.Net.MasterApi (relativ, damit auch ein Master unter einem Basispfad funktioniert).
    const string RegisterPath = "api/register";
    const string HeartbeatPath = "api/heartbeat";
    const string UnregisterPath = "api/unregister";

    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    readonly ServerContext _ctx;
    HttpClient _http;
    CancellationTokenSource _cts;
    Task _loop;

    string _id = "";
    string _token = "";
    string _baseUrl = "";
    string _registeredFields = "";
    string _state = "aus";

    public MasterAdvertiser(ServerContext ctx) => _ctx = ctx;

    /// <summary>Kurzer Zustand für Konsole/Web-Panel.</summary>
    public string Status => _state;

    /// <summary>Läuft die Anmeldung gerade (Token vorhanden)?</summary>
    public bool IsRegistered => _token.Length > 0;

    /// <summary>Startet die Anmelde-/Heartbeat-Schleife. Läuft auch bei masterEnabled=false weiter,
    /// damit ein Umschalten über Konsole/Web-Panel ohne Neustart wirkt.</summary>
    public void Start()
    {
        if (_loop != null) return;

        EnsureId();
        if (!_ctx.Config.masterEnabled)
            Log.Info("Master-Anmeldung deaktiviert (masterEnabled=false).");

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Meldet den Server ab und beendet die Schleife (für einen sauberen Shutdown).</summary>
    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts == null) return;
        _cts = null;

        try { cts.Cancel(); } catch { }
        if (_loop != null)
        {
            try { await _loop; } catch { }
            _loop = null;
        }

        if (_token.Length > 0 && _http != null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var resp = await _http.PostAsJsonAsync(UnregisterPath,
                    new HeartbeatRequest { Id = _id, Token = _token }, Json, timeout.Token);
                Log.Info(resp.IsSuccessStatusCode
                    ? "Beim Master abgemeldet."
                    : $"Abmelden beim Master: HTTP {(int)resp.StatusCode}.");
            }
            catch (Exception ex) { Log.Info("Abmelden beim Master fehlgeschlagen: " + ex.Message); }
        }

        _token = "";
        SetState("beendet");
        _http?.Dispose();
        _http = null;
        cts.Dispose();
    }

    // ---- Schleife -------------------------------------------------------------

    async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            bool failed = false;

            if (!_ctx.Config.masterEnabled)
            {
                SetState("aus");
                _token = "";           // beim Wiedereinschalten frisch anmelden
                _registeredFields = "";
                await Delay(TimeSpan.FromSeconds(5), ct);
                continue;
            }

            try
            {
                ApplyBaseUrl();

                if (_token.Length == 0)
                {
                    var reg = await RegisterAsync(ct);
                    if (!reg.Ok)
                    {
                        SetState("abgelehnt: " + (reg.Error ?? "unbekannt"));
                        Log.Info("Master-Anmeldung abgelehnt: " + (reg.Error ?? "unbekannt"));
                        failed = true;
                    }
                    else
                    {
                        _token = reg.Token;
                        _registeredFields = RegistrationFields();
                        SetState("angemeldet");
                        Log.Info($"Beim Master angemeldet: {Name} → {_baseUrl} (Port {Port}, ID {_id})");
                    }
                }
                else
                {
                    var hb = await HeartbeatAsync(ct);
                    if (hb == HeartbeatResult.NotFound)
                    {
                        // Der Master hat uns vergessen (Neustart, Timeout) → sofort komplett neu anmelden.
                        Log.Info("Master kennt den Server nicht mehr – melde neu an.");
                        _token = "";
                        SetState("neu anmelden");
                        continue;
                    }
                    if (hb == HeartbeatResult.Failed)
                        throw new HttpRequestException("Heartbeat abgelehnt");

                    // Felder, die nur /api/register ändern kann (Name, Host, Port, Version, Locked).
                    if (RegistrationFields() != _registeredFields)
                    {
                        var reg = await RegisterAsync(ct);
                        if (reg.Ok)
                        {
                            _token = reg.Token;
                            _registeredFields = RegistrationFields();
                            SetState("angemeldet");
                            Log.Info("Master-Eintrag aktualisiert.");
                        }
                        else
                        {
                            SetState("abgelehnt: " + (reg.Error ?? "unbekannt"));
                            Log.Info("Master-Update abgelehnt: " + (reg.Error ?? "unbekannt"));
                            failed = true;
                        }
                    }
                    else SetState("angemeldet");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failed = true;
                // Nur Zustandswechsel loggen, sonst spammt ein länger ausgefallener Master die Konsole voll.
                if (SetState("Fehler: " + ex.Message))
                    Log.Info("Master nicht erreichbar: " + ex.Message);
            }

            await Delay(failed ? RetryDelay : HeartbeatDelay, ct);
        }
    }

    // ---- HTTP -----------------------------------------------------------------

    async Task<RegisterResponse> RegisterAsync(CancellationToken ct)
    {
        var info = new ServerInfo
        {
            Id = _id,
            Name = Name,
            ModMessage = ModMessage,
            Host = Host,
            Port = Port,
            Players = _ctx.Sessions.Count,
            MaxPlayers = _ctx.Config.maxPlayers,
            Version = _ctx.Config.masterVersion ?? "",
            Locked = _ctx.Config.masterLocked,
        };

        using var resp = await _http.PostAsJsonAsync(RegisterPath, info, Json, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        RegisterResponse result = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try { result = JsonSerializer.Deserialize<RegisterResponse>(body, Json); } catch { }
        }
        result ??= new RegisterResponse { Ok = false, Error = $"HTTP {(int)resp.StatusCode}" };
        if (!resp.IsSuccessStatusCode) result.Ok = false;
        return result;
    }

    async Task<HeartbeatResult> HeartbeatAsync(CancellationToken ct)
    {
        var hb = new HeartbeatRequest
        {
            Id = _id,
            Token = _token,
            Players = _ctx.Sessions.Count,
            MaxPlayers = _ctx.Config.maxPlayers,
            ModMessage = ModMessage,
        };

        using var resp = await _http.PostAsJsonAsync(HeartbeatPath, hb, Json, ct);
        if (resp.IsSuccessStatusCode) return HeartbeatResult.Ok;
        if (resp.StatusCode == HttpStatusCode.NotFound) return HeartbeatResult.NotFound;
        return HeartbeatResult.Failed;
    }

    // ---- Konfiguration --------------------------------------------------------

    string Name => string.IsNullOrWhiteSpace(_ctx.Config.masterName)
        ? "TDU2-Server"
        : _ctx.Config.masterName.Trim();

    string Host => (_ctx.Config.masterHost ?? "").Trim();

    int Port => _ctx.Config.masterPort > 0 ? _ctx.Config.masterPort : _ctx.Config.tcpPort;

    string ModMessage => string.IsNullOrWhiteSpace(_ctx.Config.masterModMessage)
        ? _ctx.Config.motd ?? ""
        : _ctx.Config.masterModMessage;

    TimeSpan HeartbeatDelay => TimeSpan.FromMilliseconds(Math.Clamp(_ctx.Config.masterHeartbeatMs, 2000, 300_000));

    TimeSpan RetryDelay => TimeSpan.FromMilliseconds(Math.Clamp(_ctx.Config.masterRetryMs, 2000, 300_000));

    /// <summary>Felder, deren Änderung eine vollständige Neuanmeldung (Update) nötig macht.</summary>
    string RegistrationFields() => string.Join('\u001f', Name, Host, Port.ToString(), _ctx.Config.masterVersion ?? "", _ctx.Config.masterLocked);

    /// <summary>Setzt die Master-Adresse; bei Änderung wird der Token verworfen (neuer Master = neue Anmeldung).</summary>
    void ApplyBaseUrl()
    {
        string url = (_ctx.Config.masterUrl ?? "").Trim();
        if (url.Length > 0 && !url.Contains("://", StringComparison.Ordinal)) url = "http://" + url;
        url = url.TrimEnd('/');

        if (!Uri.TryCreate(url + "/", UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException($"masterUrl ungültig: '{_ctx.Config.masterUrl}'");

        if (url == _baseUrl && _http != null) return;

        _baseUrl = url;
        _token = "";
        _registeredFields = "";
        _http?.Dispose();
        _http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(8) };
    }

    /// <summary>Stabile ID sicherstellen und beim ersten Mal in config.json festhalten.</summary>
    void EnsureId()
    {
        var cfg = _ctx.Config;
        if (!string.IsNullOrWhiteSpace(cfg.masterId)) { _id = cfg.masterId.Trim(); return; }

        _id = Guid.NewGuid().ToString("N");
        cfg.masterId = _id;
        try
        {
            ServerConfig.Save(_ctx.ConfigFile, cfg);
            Log.Info("masterId erzeugt und in config.json gespeichert.");
        }
        catch (Exception ex) { Log.Info("masterId konnte nicht gespeichert werden: " + ex.Message); }
    }

    bool SetState(string state)
    {
        if (_state == state) return false;
        _state = state;
        return true;
    }

    static async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { }
    }

    enum HeartbeatResult { Ok, NotFound, Failed }

    // ---- JSON-Vertrag (identisch zu Paradise.Core.Net) ------------------------

    sealed class ServerInfo
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("modMessage")] public string ModMessage { get; set; } = "";
        [JsonPropertyName("host")] public string Host { get; set; } = "";
        [JsonPropertyName("port")] public int Port { get; set; }
        [JsonPropertyName("players")] public int Players { get; set; }
        [JsonPropertyName("maxPlayers")] public int MaxPlayers { get; set; }
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("locked")] public bool Locked { get; set; }
    }

    sealed class RegisterResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("token")] public string Token { get; set; } = "";
        [JsonPropertyName("error")] public string Error { get; set; }
    }

    sealed class HeartbeatRequest
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("token")] public string Token { get; set; } = "";
        [JsonPropertyName("players")] public int Players { get; set; }
        [JsonPropertyName("maxPlayers")] public int MaxPlayers { get; set; }
        [JsonPropertyName("modMessage")] public string ModMessage { get; set; }
    }
}
