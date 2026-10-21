// TDU2Server – Account-/Spielstand-Server für TDU2MapUnity (.NET 8, Konsole)
//
//   dotnet run [config.json]        startet mit config.json (wird beim ersten Start angelegt)
//
// Aufbau:
//   Core/       Server (Start, Konsole, Konfiguration, Log, Radio-Zeitbasis)
//   Data/       Konten und Spielstände (Datei-Speicher, Prüfung, Passwörter)
//   Sessions/   angemeldete Spieler (Sitzung, Spielernummer, Mitfahrt)
//   Net/Protocol  JSON-Nachrichten (Feldnamen = JSON-Namen, identisch im Unity-Client TDU2Net.cs)
//   Net/Tcp       TCP-Verbindungen (eine JSON-Zeile je Anfrage/Antwort) und Verteiler
//   Net/Handlers  je Anfrage ("op") eine Klasse
//   Net/Udp       Spielersynchronisation, Chat, Verkehr (je Pakettyp eine Klasse)
// Protokollbeschreibung: README.md

using System.Text;
using TDU2Server.Core;

namespace TDU2Server;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string configFile = args.Length > 0 ? args[0] : "config.json";
        var server = new GameServer(configFile);
        await server.RunAsync();
    }
}
