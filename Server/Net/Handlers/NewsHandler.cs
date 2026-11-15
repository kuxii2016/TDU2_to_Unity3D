using TDU2Server.Core;
using TDU2Server.Net.Protocol;
using TDU2Server.Net.Tcp;

namespace TDU2Server.Net.Handlers;

/// <summary>
/// {"op":"news","session":..,"since":id} -> News mit Id &gt; since (neueste zuerst, max. 50; nur alle Inseln bzw. die Insel des
/// Spielers), dazu newsLatest (höchste Id) und newsIds (alle vorhandenen Ids, damit der Client gelöschte entfernt).
/// Ob es Neues gibt, steht auch in jeder "status"-Antwort (newsLatest) – der Client fragt dann hier nach.
/// Bilder (hasImage) holt der Client einzeln mit {"op":"newsimg","session":..,"target":id} (<see cref="NewsImageHandler"/>).
/// </summary>
public sealed class NewsHandler : RequestHandler
{
    public NewsHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "news";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        var me = SessionOf(conn, r);
        if (me == null) return Fail("not_logged_in");
        return new NetResponse
        {
            ok = true,
            online = Online,
            news = Ctx.News.Since(Math.Max(0, r.since), me.Island),
            newsLatest = Ctx.News.Latest,
            newsIds = Ctx.News.Ids(),
        };
    }
}

/// <summary>{"op":"newsimg","session":..,"target":id} -> newsImage = Bild der Meldung (Base64, leer = keins), lifeOf = id.</summary>
public sealed class NewsImageHandler : RequestHandler
{
    public NewsImageHandler(ServerContext ctx) : base(ctx) { }

    public override string Op => "newsimg";

    public override NetResponse Handle(Connection conn, NetRequest r)
    {
        if (SessionOf(conn, r) == null) return Fail("not_logged_in");
        string img = Ctx.News.ImageBase64(r.target);
        if (img == null) return Fail("no_image");
        return new NetResponse { ok = true, online = Online, newsImage = img, lifeOf = r.target };
    }
}
