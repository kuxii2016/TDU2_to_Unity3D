using TDU2Server.Core;
using TDU2Server.Data;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;
using TDU2Server.Sessions;

namespace TDU2Server.Net.Handlers;

/// <summary>
/// Freundesliste (wie im Spiel: Anfrage -> Antwort beim Empfänger -> beidseitig Freunde):
///   {"op":"friend","session":..,"kind":"list"}                  -> friends, friendRequests (an mich), friendPending (von mir)
///   {"op":"friend","session":..,"kind":"request","nick":name}   -> friendResult; Empfänger bekommt "friendreq:&lt;name&gt;"
///   {"op":"friend","session":..,"kind":"accept"|"decline","nick":name} -> Absender bekommt "friendok:" bzw. "frienddecl:"
///   {"op":"friend","session":..,"kind":"remove","nick":name}    -> Freundschaft beenden / eigene Anfrage zurückziehen ("friendrm:")
/// Jede Antwort enthält die aktuelle Liste. Anfragen an Spieler, die gerade nicht online sind, warten bis zu ihrer Antwort.
/// </summary>
public sealed class FriendHandler : RequestHandler
{
    public FriendHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "friend";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        if (me == null) return Fail("not_logged_in");
        string kind = (r.kind ?? "list").ToLowerInvariant();
        if (kind == "list") return Fill(me, new NetResponse { ok = true, online = Online });

        // Name wie im Konto gespeichert (Groß-/Kleinschreibung)
        var acc = Ctx.Accounts.Load(r.nick ?? "");
        if (acc == null) return Fail("unknown_player");
        string other = acc.nick;
        var resp = new NetResponse { ok = true, online = Online };
        switch (kind)
        {
            case "request":
                {
                    var res = Ctx.Friends.Request(me.Nick, other);
                    switch (res)
                    {
                        case FriendStore.RequestResult.Self: return Fail("self");
                        case FriendStore.RequestResult.Full: return Fail("full");
                        case FriendStore.RequestResult.AlreadyFriends: resp.friendResult = "already_friends"; break;
                        case FriendStore.RequestResult.AlreadySent: resp.friendResult = "already_sent"; break;
                        case FriendStore.RequestResult.BecameFriends:
                            resp.friendResult = "became_friends";
                            NoticeTo(other, "friendok:" + me.Nick);
                            Log.Info($"{me.Nick} und {other} sind jetzt Freunde");
                            break;
                        default:
                            resp.friendResult = "sent";
                            NoticeTo(other, "friendreq:" + me.Nick);
                            Log.Info($"{me.Nick} möchte mit {other} befreundet sein");
                            break;
                    }
                    break;
                }
            case "accept":
            case "decline":
                {
                    bool accept = kind == "accept";
                    if (!Ctx.Friends.Answer(me.Nick, other, accept)) return Fill(me, Fail("no_request"));
                    NoticeTo(other, (accept ? "friendok:" : "frienddecl:") + me.Nick);
                    Log.Info($"{me.Nick} {(accept ? "nimmt" : "lehnt")} die Freundschaftsanfrage von {other} {(accept ? "an" : "ab")}");
                    break;
                }
            case "remove":
                if (Ctx.Friends.Remove(me.Nick, other))
                {
                    NoticeTo(other, "friendrm:" + me.Nick);
                    Log.Info($"{me.Nick} entfernt {other} aus der Freundesliste");
                }
                break;
            default:
                return Fail("unknown_kind");
        }
        return Fill(me, resp);
    }

    void NoticeTo(string nick, string notice)
    {
        foreach (var s in Ctx.Sessions.All)
            if (string.Equals(s.Nick, nick, StringComparison.OrdinalIgnoreCase)) s.Notice(notice);
    }

    NetResponse Fill(Session me, NetResponse resp)
    {
        var rec = Ctx.Friends.Get(me.Nick);
        var online = new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Ctx.Sessions.All) online[s.Nick] = s;
        foreach (var n in rec.friends) resp.friends.Add(Entry(n, online));
        foreach (var n in rec.incoming) resp.friendRequests.Add(Entry(n, online));
        resp.friendPending.AddRange(rec.outgoing);
        return resp;
    }

    FriendEntry Entry(string nick, Dictionary<string, Session> online)
    {
        var e = new FriendEntry { nick = nick };
        var rp = Ctx.Recent.Find(nick);
        if (rp != null)
        {
            e.lastSeen = rp.lastSeen; e.carId = rp.carId; e.color = rp.color; e.money = rp.money; e.nbCars = rp.nbCars; e.nationality = rp.nationality;
        }
        if (online.TryGetValue(nick, out var s))
        {
            e.online = true; e.id = s.PlayerId; e.nationality = s.Nationality; e.money = s.Money; e.nbCars = s.NbCars;
            if (s.CarId != 0) { e.carId = s.CarId; e.color = s.Color; }
            e.photoVer = s.PhotoVer; e.mood = s.Mood ?? "";
        }
        return e;
    }
}
