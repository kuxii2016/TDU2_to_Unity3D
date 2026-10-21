using System.Net;
using System.Net.Sockets;
using System.Text;
using TDU2Server.Core;
using TDU2Server.Net.Protocol;

namespace TDU2Server.Net.Tcp;

/// <summary>TCP-Server: je Verbindung eine Schleife, eine JSON-Zeile (UTF-8, '\n') je Anfrage und je Antwort.</summary>
public sealed class TcpServer
{
    const int MaxLine = 1 << 20;   // 1 MB Schutz

    readonly ServerContext _ctx;
    readonly RequestRouter _router;
    TcpListener _listener;
    volatile bool _running = true;

    public TcpServer(ServerContext ctx)
    {
        _ctx = ctx;
        _router = new RequestRouter(ctx);
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _ctx.Config.tcpPort);
        _listener.Start();
    }

    public void Stop()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
    }

    public async Task AcceptLoopAsync()
    {
        while (_running)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(); }
            catch (Exception) when (!_running) { break; }
            catch (Exception ex) { Log.Info("Accept: " + ex.Message); continue; }
            _ = Task.Run(() => HandleClientAsync(c));
        }
    }

    async Task HandleClientAsync(TcpClient client)
    {
        var conn = new Connection { Client = client, Remote = client.Client.RemoteEndPoint?.ToString() ?? "?" };
        client.NoDelay = true;
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            while (_running)
            {
                string line = await reader.ReadLineAsync();
                if (line == null || line.Length > MaxLine) break;
                NetResponse resp;
                try
                {
                    resp = _router.Handle(conn, WireJson.ParseRequest(line));
                }
                catch (Exception ex)
                {
                    resp = new NetResponse { ok = false, error = "bad_request" };
                    Log.Info($"{conn.Remote}: Anfrage fehlerhaft: {ex.Message}");
                }
                await writer.WriteLineAsync(WireJson.Write(resp));
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log.Info($"{conn.Remote}: {ex.Message}"); }
        finally
        {
            var s = conn.Session;
            if (_ctx.Sessions.Remove(s)) Log.Info($"{s.Nick} getrennt ({_ctx.Sessions.Count} online)");
            try { client.Close(); } catch { }
        }
    }
}
