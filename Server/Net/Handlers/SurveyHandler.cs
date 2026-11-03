using TDU2Server.Core;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>
/// {"op":"surveys","session":..} -> surveys = Umfragen für die Insel des Spielers (offene zuerst) mit Summen je Antwort und
/// eigener Stimme (myVote, -1 = keine), surveyVersion = Änderungszähler. Im Spiel: TDU Media, Umfrage-Kanal.
/// </summary>
public sealed class SurveyHandler : RequestHandler
{
    public SurveyHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "surveys";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        if (me == null) return Fail("not_logged_in");
        return new NetResponse { ok = true, online = Online, surveys = Ctx.Surveys.ForPlayer(me.Nick, me.Island), surveyVersion = Ctx.Surveys.Version };
    }
}

/// <summary>
/// {"op":"vote","session":..,"target":umfrage,"choice":antwort} -> abstimmen (eine Stimme je Konto, solange offen änderbar);
/// Antwort wie "surveys" (aktuelle Summen). Fehler: not_found, closed, bad_choice.
/// </summary>
public sealed class VoteHandler : RequestHandler
{
    public VoteHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "vote";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        if (me == null) return Fail("not_logged_in");
        string err = Ctx.Surveys.Vote(r.target, me.Nick, r.choice);
        if (err.Length > 0) return Fail(err);
        Log.Info($"[Umfrage] {me.Nick} stimmt bei #{r.target} für Antwort {r.choice + 1}");
        return new NetResponse { ok = true, online = Online, surveys = Ctx.Surveys.ForPlayer(me.Nick, me.Island), surveyVersion = Ctx.Surveys.Version };
    }
}
