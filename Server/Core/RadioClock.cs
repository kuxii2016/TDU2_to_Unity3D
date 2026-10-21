using System.Security.Cryptography;

namespace TDU2Server.Core;

/// <summary>
/// Gemeinsame Radio-Zeitbasis: beim Serverstart festgelegt. Jeder Client berechnet aus Epoche + Startwert dieselbe Playlist
/// und denselben Wiedergabezeitpunkt (nur Originalsender Hariba/RoadRock).
/// </summary>
public sealed class RadioClock
{
    /// <summary>Serverstart (UTC, Unix-Millisekunden) – Nullpunkt der Radio-Zeitleiste.</summary>
    public long Epoch { get; } = NowMs();
    /// <summary>Gemeinsamer Zufallsstartwert für die deterministische Playlist.</summary>
    public int Seed { get; } = RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);

    /// <summary>Aktuelle Serverzeit (UTC, Unix-Millisekunden).</summary>
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
