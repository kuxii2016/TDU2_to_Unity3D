using TDU2Server.Core;
using TDU2Server.Data;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>
/// {"op":"login","nick":..,"password":..} -> anmelden mit Passwort (liefert neuen Merk-Token);
/// {"op":"login","nick":..,"token":..}    -> anmelden mit gemerktem Token (Profil auf dem PC).
/// </summary>
public sealed class LoginHandler : SessionStartHandler
{
    public LoginHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "login";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var a = Ctx.Accounts.Load((r.nick ?? "").Trim());
        if (a == null) return Fail("unknown_nick");
        if (a.IsBanned)
        {
            Log.Info($"Gesperrtes Konto {a.nick} versucht Anmeldung ({conn.Remote})");
            return Fail("banned");
        }

        string token;
        if (!string.IsNullOrEmpty(r.password))
        {
            if (!Credentials.CheckPassword(a, r.password))
            {
                Log.Info($"Falsches Passwort für {a.nick} ({conn.Remote})");
                return Fail("bad_password");
            }
            token = Credentials.NewToken();
            a.tokenHashes.Add(Credentials.HashToken(token));
            while (a.tokenHashes.Count > Account.MaxTokens) a.tokenHashes.RemoveAt(0);
        }
        else
        {
            if (string.IsNullOrEmpty(r.token) || !a.tokenHashes.Contains(Credentials.HashToken(r.token))) return Fail("bad_token");
            token = r.token;
        }

        if (ServerFull) return Fail("server_full");
        if (r.nationality > 0) a.nationality = r.nationality;
        return StartSession(conn, a, token);
    }
}
