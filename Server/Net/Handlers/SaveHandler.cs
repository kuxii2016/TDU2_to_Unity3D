using TDU2Server.Core;
using TDU2Server.Data;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>{"op":"save","session":..,"save":{..}} -> Spielstand prüfen und speichern, Spielerliste aktualisieren.</summary>
public sealed class SaveHandler : RequestHandler
{
    public SaveHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "save";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var s = SessionOf(conn, r);
        if (s == null) return Fail("not_logged_in");
        if (r.save == null) return Fail("no_save");
        var a = Ctx.Accounts.Load(s.Nick);
        if (a == null) return Fail("unknown_nick");

        var sv = SaveSanitizer.Clean(r.save, a.save);
        a.save = sv;
        Ctx.Accounts.Store(a);

        s.Money = sv.money;
        s.NbCars = sv.ownedCars.Count;
        s.Avatar = sv.avatar;
        s.Mood = sv.myLife.mood;
        s.MoodTxt = sv.myLife.moodText;
        s.PhotoVer = sv.myLife.photoVer;
        int isl = Islands.Code(sv.island);
        if (isl != Islands.Unknown && isl != s.Island)
        {
            Log.Info($"{s.Nick}: Insel {sv.island}");
            s.Island = isl;
        }
        return new NetResponse { ok = true, nick = a.nick, online = Online };
    }
}
