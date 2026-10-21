using System.Text.Json;

namespace TDU2Server.Net.Protocol;

/// <summary>JSON-Einstellungen der Leitung (Felder statt Properties, Groß/Klein egal).</summary>
public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new() { IncludeFields = true, PropertyNameCaseInsensitive = true };

    public static NetRequest ParseRequest(string line) => JsonSerializer.Deserialize<NetRequest>(line, Options) ?? new NetRequest();

    public static string Write(NetResponse resp) => JsonSerializer.Serialize(resp, Options);
}
