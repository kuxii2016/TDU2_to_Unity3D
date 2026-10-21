namespace TDU2Server.Core;

/// <summary>Konsolenausgabe mit Uhrzeit.</summary>
public static class Log
{
    static readonly object _lock = new();

    public static void Info(string message)
    {
        lock (_lock) Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}
