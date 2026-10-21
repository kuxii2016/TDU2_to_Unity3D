using System.Net.Sockets;
using TDU2Server.Sessions;

namespace TDU2Server.Net.Tcp;

/// <summary>Eine TCP-Verbindung eines Clients (höchstens eine Sitzung).</summary>
public sealed class Connection
{
    public TcpClient Client;
    public string Remote = "";
    public Session Session;
}
