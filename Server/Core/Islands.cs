namespace TDU2Server.Core;

/// <summary>Inselcodes wie im UDP-Zustand (Byte +80): 1 Ibiza, 2 Hawaii, 0 unbekannt.</summary>
public static class Islands
{
    public const int Unknown = 0, Ibiza = 1, Hawaii = 2;

    /// <summary>Inselname aus dem Spielstand ("ibiza", "hawaii") -> Code.</summary>
    public static int Code(string island)
    {
        if (string.IsNullOrEmpty(island)) return Unknown;
        if (island.StartsWith("i", StringComparison.OrdinalIgnoreCase)) return Ibiza;
        if (island.StartsWith("h", StringComparison.OrdinalIgnoreCase)) return Hawaii;
        return Unknown;
    }

    public static string Name(int code) => code == Ibiza ? "Ibiza" : code == Hawaii ? "Hawaii" : code == Unknown ? "?" : code.ToString();

    /// <summary>Sehen sich zwei Spieler (gleiche Insel; unbekannt sieht alle)?</summary>
    public static bool Same(int a, int b) => a == Unknown || b == Unknown || a == b;
}
