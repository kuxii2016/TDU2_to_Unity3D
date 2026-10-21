using TDU2Server.Core;
using TDU2Server.Data;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>{"op":"register","nick":..,"password":..,"email":..} -> neues Konto, gleich angemeldet.</summary>
public sealed class RegisterHandler : SessionStartHandler
{
    public RegisterHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "register";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        string nick = (r.nick ?? "").Trim();
        if (!AccountStore.ValidNick(nick)) return Fail("invalid_nick");
        if (string.IsNullOrEmpty(r.password) || r.password.Length < 4) return Fail("weak_password");
        if (ServerFull) return Fail("server_full");

        var cfg = Ctx.Config;
        string salt = Credentials.NewSalt();
        var a = new Account
        {
            nick = nick, email = (r.email ?? "").Trim(), salt = salt, passwordHash = Credentials.HashPassword(r.password, salt),
            created = DateTime.UtcNow.ToString("o"),
            save = new SaveData { island = cfg.startIsland, money = cfg.startMoney, starterChosen = false },
        };
        string token = Credentials.NewToken();
        a.tokenHashes.Add(Credentials.HashToken(token));
        if (!Ctx.Accounts.Create(a)) return Fail("nick_taken");
        Log.Info($"Neues Konto: {nick} ({conn.Remote})");
        a.nationality = r.nationality;
        return StartSession(conn, a, token);
    }
}
