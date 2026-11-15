using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TDU2Server.Core;

namespace TDU2Server.Web;

/// <summary>
/// Moderationspanel als Kestrel-Web-App im selben Prozess. Anmeldung mit einem Serverkonto,
/// das die Rolle helper/mod/admin/dev hat (Feld "permission" in der Konto-Datei).
/// </summary>
public sealed class WebPanel
{
    readonly ServerContext _ctx;
    readonly WebAuth _auth = new();
    WebApplication _app;

    public WebPanel(ServerContext ctx) => _ctx = ctx;

    public void Start()
    {
        var cfg = _ctx.Config;
        if (!cfg.webEnabled) { Log.Info("Web-Panel deaktiviert (config.webEnabled=false)"); return; }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.Logging.ClearProviders();
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.WebHost.UseUrls($"http://{cfg.webHost}:{cfg.webPort}");
        builder.Services.AddAntiforgery(o => o.SuppressXFrameOptionsHeader = true);

        var app = builder.Build();
        Map(app);
        _app = app;
        _ = Task.Run(async () =>
        {
            try { await app.RunAsync(); }
            catch (Exception ex) { Log.Info("Web-Panel Fehler: " + ex.Message); }
        });
        Log.Info($"Web-Panel auf http://{cfg.webHost}:{cfg.webPort} – Anmeldung mit Konto (Rolle helper/mod/admin)");
    }

    public void Stop() { try { _app?.DisposeAsync(); } catch { } }

    // ---- Routing --------------------------------------------------------------

    void Map(WebApplication app)
    {
        app.MapGet("/", (HttpContext http) => _auth.Current(http) is { } u
            ? Results.Redirect("/panel")
            : Results.Content(Pages.Login(""), "text/html"));

        app.MapPost("/login", async (HttpContext http) =>
        {
            var form = await http.Request.ReadFormAsync();
            string nick = (form["username"].ToString() ?? "").Trim();
            string pass = form["password"].ToString() ?? "";
            var a = _ctx.Accounts.Load(nick);
            if (a == null || !Data.Credentials.CheckPassword(a, pass))
            {
                _ctx.Audit.Add("mod", nick, "Web-Login fehlgeschlagen");
                return Results.Content(Pages.Login("Benutzername oder Passwort falsch."), "text/html", statusCode: 401);
            }
            if (!Data.Account.IsStaff(a.permission))
                return Results.Content(Pages.Login("Dieses Konto hat keine Panel-Rechte."), "text/html", statusCode: 403);
            var token = _auth.Start(a.nick, a.permission);
            WebAuth.SetCookie(http, token);
            _ctx.Audit.Add("mod", a.nick, "Web-Panel angemeldet");
            return Results.Redirect("/panel");
        });

        app.MapGet("/logout", (HttpContext http) =>
        {
            if (_auth.Current(http) is { } u) _ctx.Audit.Add("mod", u.Nick, "Web-Panel abgemeldet");
            _auth.End(WebAuth.TokenOf(http));
            WebAuth.ClearCookie(http);
            return Results.Redirect("/");
        });

        app.MapGet("/panel", (HttpContext http) => Require(http, u => Results.Content(Pages.Dashboard(u.Nick, u.Permission), "text/html")));
        app.MapGet("/chat", (HttpContext http) => Require(http, u => Results.Content(Pages.ChatPage(u.Nick, u.Permission), "text/html")));
        app.MapGet("/accounts", (HttpContext http) => Require(http, u => Results.Content(Pages.AccountsPage(u.Nick, u.Permission), "text/html")));
        app.MapGet("/players", (HttpContext http) => Require(http, u => Json(PlayerList())));
        app.MapGet("/players/info", (HttpContext http) => Require(http, u =>
        {
            int pid = int.TryParse(http.Request.Query["id"], out var p) ? p : 0;
            var info = PlayerInfo(pid);
            return info == null ? Results.Json(new { ok = false, error = "Spieler nicht online" }, statusCode: 404) : Json(info);
        }));
        app.MapGet("/chat/list", (HttpContext http) => Require(http, u => Json(_ctx.Chat.Recent(500).Select(c => new { time = c.At.ToString("HH:mm:ss"), nick = c.Nick, id = c.PlayerId, text = c.Text }))));
        app.MapGet("/events", (HttpContext http) => Require(http, u => Json(_ctx.Audit.Recent(300).Select(e => new { time = e.At.ToString("HH:mm:ss"), kind = e.Kind, nick = e.Nick, text = e.Text }))));
        app.MapGet("/islands", (HttpContext http) => Require(http, u => Json(new[] {
            new { code = 0, name = "Alle" }, new { code = Islands.Ibiza, name = "Ibiza" }, new { code = Islands.Hawaii, name = "Hawaii" } })));

        app.MapPost("/actions/message", (HttpContext http) => Require(http, u =>
        {
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            string text = f["text"].ToString();
            int island = int.TryParse(f["island"], out var i) ? i : 0;
            int pid = int.TryParse(f["playerId"], out var p) ? p : 0;
            int n = _ctx.Actions.Message(text, pid, island);
            _ctx.Audit.Add("mod", u.Nick, $"Meldung an {n} Spieler (Insel {Islands.Name(island)}): {text}");
            Log.Info($"[Mod] {u.Nick}: Meldung an {n} Spieler: {text}");
            return Json(new { ok = n > 0, sent = n });
        }));

        app.MapPost("/actions/dm", (HttpContext http) => Require(http, u =>
        {
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            int pid = int.TryParse(f["playerId"], out var p) ? p : 0;
            string text = f["text"].ToString();
            bool ok = _ctx.Actions.DirectMessage(pid, u.Nick, text);
            _ctx.Audit.Add("mod", u.Nick, $"Nachricht an #{pid}: {text}");
            Log.Info($"[Mod] {u.Nick}: Nachricht an #{pid}: {text}");
            return Json(new { ok });
        }));

        app.MapPost("/actions/kick", (HttpContext http) => Require(http, u =>
        {
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            int pid = int.TryParse(f["playerId"], out var p) ? p : 0;
            string reason = f["reason"].ToString();
            var target = _ctx.Actions.Find(pid);
            bool ok = _ctx.Actions.Kick(pid, reason);
            if (ok) _ctx.Audit.Add("kick", u.Nick, $"{target?.Nick} (#{pid}) gekickt: {reason}");
            return Json(new { ok });
        }));

        app.MapPost("/actions/ban", (HttpContext http) => Require(http, u =>
        {
            if (Data.Account.Rank(u.Permission) < 2) return Json(new { ok = false, error = "keine_rechte" });
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            int pid = int.TryParse(f["playerId"], out var p) ? p : 0;
            int hours = int.TryParse(f["hours"], out var h) ? h : 24;
            string reason = f["reason"].ToString();
            var target = _ctx.Actions.Find(pid);
            bool ok = _ctx.Actions.Ban(pid, reason, TimeSpan.FromHours(hours));
            if (ok) _ctx.Audit.Add("kick", u.Nick, $"{target?.Nick} (#{pid}) für {hours}h gesperrt: {reason}");
            return Json(new { ok });
        }));

        // ---- Lobbys ----
        app.MapGet("/lobbies", (HttpContext http) => Require(http, u => Results.Content(Pages.LobbiesPage(u.Nick, u.Permission), "text/html")));
        app.MapGet("/lobbies/list", (HttpContext http) => Require(http, u => Json(LobbyList())));

        app.MapPost("/lobbies/move", (HttpContext http) => Require(http, u =>
        {
            if (Data.Account.Rank(u.Permission) < 2) return Json(new { ok = false, error = "keine_rechte" });
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            int pid = int.TryParse(f["playerId"], out var p) ? p : 0;
            int lobby = int.TryParse(f["lobby"], out var l) ? l : -1;
            bool force = f["force"].ToString() == "1";
            bool notify = f["notify"].ToString() == "1";
            var s = _ctx.Actions.Find(pid);
            if (lobby < 0) return Json(new { ok = false, error = "Ziel fehlt" });
            int from = s?.Lobby ?? 0;
            if (!_ctx.Lobbies.Move(s, lobby, force, out string err, out int target)) return Json(new { ok = false, error = err });
            if (notify && target != from) s.Notice($"news:0:Du wurdest in Lobby {target} verschoben.");
            _ctx.Audit.Add("mod", u.Nick, $"{s.Nick} (#{pid}) von Lobby {from} nach Lobby {target} verschoben{(force ? " (trotz voll)" : "")}");
            Log.Info($"[Mod] {u.Nick}: {s.Nick} (#{pid}) Lobby {from} → {target}");
            return Json(new { ok = true, lobby = target });
        }));

        app.MapPost("/lobbies/delete", (HttpContext http) => Require(http, u =>
        {
            if (Data.Account.Rank(u.Permission) < 2) return Json(new { ok = false, error = "keine_rechte" });
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            int id = int.TryParse(f["id"], out var i) ? i : 0;
            bool ok = _ctx.Lobbies.Delete(id, _ctx.Actions.Find, out int moved);
            if (ok) { _ctx.Audit.Add("mod", u.Nick, $"Lobby {id} gelöscht, {moved} Spieler verteilt"); Log.Info($"[Mod] {u.Nick}: Lobby {id} gelöscht ({moved} Spieler verteilt)"); }
            return Json(new { ok, moved, error = ok ? "" : "Lobby existiert nicht" });
        }));

        app.MapPost("/lobbies/create", (HttpContext http) => Require(http, u =>
        {
            if (Data.Account.Rank(u.Permission) < 2) return Json(new { ok = false, error = "keine_rechte" });
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            int island = int.TryParse(f["island"], out var i) ? i : 0;
            if (island != Islands.Ibiza && island != Islands.Hawaii) return Json(new { ok = false, error = "Insel wählen" });
            var l = _ctx.Lobbies.CreateEmpty(island);
            _ctx.Audit.Add("mod", u.Nick, $"Lobby {l.Id} ({Islands.Name(island)}) angelegt");
            return Json(new { ok = true, id = l.Id });
        }));

        // ---- Konten ----
        app.MapGet("/accounts/list", (HttpContext http) => Require(http, u =>
        {
            var list = _ctx.Accounts.Names().OrderBy(n => n).Select(n =>
            {
                var a = _ctx.Accounts.Load(n);
                bool online = _ctx.Actions.Players().Any(s => string.Equals(s.Nick, a.nick, StringComparison.OrdinalIgnoreCase));
                return new
                {
                    nick = a.nick,
                    email = a.email,
                    permission = a.permission,
                    nationality = a.nationality,
                    money = a.save?.money ?? 0,
                    cars = a.save?.ownedCars?.Count ?? 0,
                    island = Islands.Name(Islands.Code(a.save?.island)),
                    created = a.created,
                    lastLogin = a.lastLogin,
                    banned = a.IsBanned,
                    bannedUntil = a.bannedUntil,
                    banReason = a.banReason,
                    online,
                };
            });
            return Json(list);
        }));

        app.MapGet("/accounts/info", (HttpContext http) => Require(http, u =>
        {
            string nick = http.Request.Query["nick"].ToString();
            var a = _ctx.Accounts.Load(nick);
            if (a == null) return Results.Json(new { ok = false, error = "not_found" }, statusCode: 404);
            return Results.Json(new
            {
                ok = true,
                nick = a.nick,
                email = a.email,
                permission = a.permission,
                nationality = a.nationality,
                created = a.created,
                lastLogin = a.lastLogin,
                banned = a.IsBanned,
                bannedUntil = a.bannedUntil,
                banReason = a.banReason,
                tokens = a.tokenHashes.Count,
                carCatalog = _ctx.Config.starterCars ?? new(),
                save = new
                {
                    island = a.save?.island,
                    money = a.save?.money ?? 0,
                    currentCar = a.save?.currentCar,
                    cars = a.save?.ownedCars ?? new(),
                    starterChosen = a.save?.starterChosen ?? false,
                    nbCars = a.save?.ownedCars?.Count ?? 0,
                    avatar = a.save?.avatar,
                    houses = a.save?.houses?.Count ?? 0,
                    odometers = a.save?.odometers?.Count ?? 0,
                    version = a.save?.version ?? 0,
                    interior = a.save?.interior,
                    savedAt = a.save?.savedAt,
                    px = a.save?.px ?? 0,
                    py = a.save?.py ?? 0,
                    pz = a.save?.pz ?? 0,
                    heading = a.save?.heading ?? 0,
                    hasPosition = a.save?.hasPosition ?? false,
                    liveries = a.save?.liveries?.Count ?? 0,
                    stickerBoards = a.save?.stickerBoards?.Count ?? 0,
                    favoriteColors = a.save?.favoriteColors?.Count ?? 0,
                    ownedClothes = a.save?.ownedClothes?.Count ?? 0,
                    ownedFurniture = a.save?.ownedFurniture?.Count ?? 0,
                    ownedMaterials = a.save?.ownedMaterials?.Count ?? 0,
                    bookmarks = a.save?.bookmarks?.Count ?? 0,
                    favoriteSpots = a.save?.favoriteSpots?.Count ?? 0,
                    carConfigs = a.save?.carConfigs?.Count ?? 0,
                    houseSpots = a.save?.houses?.Select(h => h.spot).ToList() ?? new(),
                    odometerList = a.save?.odometers?.Select(o => new { carId = o.carId, meters = o.meters }).ToList() ?? new(),
                    furnitureList = a.save?.ownedFurniture ?? new(),
                    materialList = a.save?.ownedMaterials ?? new(),
                    clothesList = a.save?.ownedClothes ?? new(),
                    stickerList = a.save?.stickerBoards ?? new(),
                    colorList = a.save?.favoriteColors ?? new(),
                    bookmarkList = a.save?.bookmarks ?? new(),
                    favoriteList = a.save?.favoriteSpots ?? new(),
                    carConfigList = a.save?.carConfigs?.Select(c => c.car).ToList() ?? new(),
                    liveryList = a.save?.liveries?.Select(l => l.car).ToList() ?? new(),
                    mood = a.save?.myLife?.mood,
                    moodText = a.save?.myLife?.moodText,
                    gender = a.save?.myLife?.gender,
                    birthDay = a.save?.myLife?.birthDay,
                    birthMonth = a.save?.myLife?.birthMonth,
                    birthYear = a.save?.myLife?.birthYear,
                    hasPhoto = !string.IsNullOrEmpty(a.save?.myLife?.photo),
                },
            });
        }));

        app.MapPost("/accounts/save", (HttpContext http) => Require(http, u =>
        {
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            string nick = f["nick"].ToString();
            var a = _ctx.Accounts.Load(nick);
            if (a == null) return Json(new { ok = false, error = "not_found" });
            var changes = new List<string>();

            if (Data.Account.Rank(u.Permission) >= 3)
            {
                string perm = f["permission"].ToString();
                if (!string.IsNullOrEmpty(perm) && perm != a.permission) { changes.Add($"Rolle {a.permission}→{perm}"); a.permission = perm; }
            }
            string email = f["email"].ToString();
            if (!string.IsNullOrEmpty(email) && email != a.email) { changes.Add("E-Mail"); a.email = email; }
            if (long.TryParse(f["money"], out var money) && a.save != null && money != a.save.money)
            {
                changes.Add($"Geld {a.save.money}→{money}");
                a.save.money = money;
            }
            string island = f["island"].ToString();
            if (!string.IsNullOrEmpty(island) && a.save != null && island != a.save.island)
            {
                changes.Add($"Insel {a.save.island}→{island}");
                a.save.island = island;
            }
            if (!string.IsNullOrEmpty(f["password"]))
            {
                string pw = f["password"].ToString();
                if (pw.Length >= 4) { a.salt = Data.Credentials.NewSalt(); a.passwordHash = Data.Credentials.HashPassword(pw, a.salt); a.tokenHashes.Clear(); changes.Add("Passwort"); }
            }
            if (string.Equals(f["unban"], "on", StringComparison.OrdinalIgnoreCase)) { a.bannedUntil = ""; a.banReason = ""; changes.Add("entbannt"); }

            _ctx.Accounts.Store(a);
            _ctx.Audit.Add("mod", u.Nick, $"Konto {a.nick} bearbeitet: {(changes.Count == 0 ? "keine Änderung" : string.Join(", ", changes))}");
            Log.Info($"[Mod] {u.Nick}: Konto {a.nick} bearbeitet ({string.Join(", ", changes)})");
            return Json(new { ok = true, changes });
        }));

        app.MapPost("/accounts/property", (HttpContext http) => Require(http, u =>
        {
            if (Data.Account.Rank(u.Permission) < 3) return Json(new { ok = false, error = "keine_rechte" });
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            string nick = f["nick"].ToString();
            string kind = f["kind"].ToString();
            string value = f["value"].ToString();
            var a = _ctx.Accounts.Load(nick);
            if (a?.save == null) return Json(new { ok = false, error = "not_found" });
            var s = a.save;
            bool removed = false;
            try
            {
                switch (kind)
                {
                    case "car":
                        removed = s.ownedCars.RemoveAll(c => string.Equals(c, value, StringComparison.OrdinalIgnoreCase)) > 0;
                        if (removed)
                        {
                            s.liveries.RemoveAll(l => string.Equals(l.car, value, StringComparison.OrdinalIgnoreCase));
                            s.carConfigs.RemoveAll(c => string.Equals(c.car, value, StringComparison.OrdinalIgnoreCase));
                            if (string.Equals(s.currentCar, value, StringComparison.OrdinalIgnoreCase)) s.currentCar = s.ownedCars.FirstOrDefault() ?? "";
                        }
                        break;
                    case "furniture": removed = s.ownedFurniture.Remove(int.Parse(value)); break;
                    case "material": removed = s.ownedMaterials.Remove(int.Parse(value)); break;
                    case "clothes": removed = s.ownedClothes.Remove(int.Parse(value)); break;
                    case "sticker": removed = s.stickerBoards.Remove(int.Parse(value)); break;
                    case "color": removed = s.favoriteColors.Remove(int.Parse(value)); break;
                    case "bookmark": removed = s.bookmarks.RemoveAll(b => b.id.ToString() == value) > 0; break;
                    case "favorite": removed = s.favoriteSpots.RemoveAll(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)) > 0; break;
                    case "carconfig": removed = s.carConfigs.RemoveAll(c => string.Equals(c.car, value, StringComparison.OrdinalIgnoreCase)) > 0; break;
                    case "house": removed = s.houses.RemoveAll(h => string.Equals(h.spot, value, StringComparison.OrdinalIgnoreCase)) > 0; break;
                    case "livery": removed = s.liveries.RemoveAll(l => string.Equals(l.car, value, StringComparison.OrdinalIgnoreCase)) > 0; break;
                }
            }
            catch (Exception ex) { return Json(new { ok = false, error = ex.Message }); }
            if (!removed) return Json(new { ok = false, error = "not_found" });
            _ctx.Accounts.Store(a);
            _ctx.Audit.Add("mod", u.Nick, $"Konto {a.nick}: {kind} '{value}' entfernt");
            Log.Info($"[Mod] {u.Nick}: {a.nick} – {kind} '{value}' entfernt");
            return Json(new { ok = true });
        }));

        app.MapPost("/accounts/car", (HttpContext http) => Require(http, u =>
        {
            if (Data.Account.Rank(u.Permission) < 3) return Json(new { ok = false, error = "keine_rechte" });
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            string nick = f["nick"].ToString();
            string action = (f["action"].ToString() ?? "add").Trim().ToLowerInvariant();
            string car = (f["car"].ToString() ?? "").Trim();
            var a = _ctx.Accounts.Load(nick);
            if (a?.save == null) return Json(new { ok = false, error = "not_found" });
            var s = a.save;

            if (action == "setcurrent")
            {
                if (!s.ownedCars.Any(c => string.Equals(c, car, StringComparison.OrdinalIgnoreCase)))
                    return Json(new { ok = false, error = "not_owned" });
                s.currentCar = s.ownedCars.First(c => string.Equals(c, car, StringComparison.OrdinalIgnoreCase));
                _ctx.Accounts.Store(a);
                _ctx.Audit.Add("mod", u.Nick, $"Konto {a.nick}: aktives Auto → {s.currentCar}");
                Log.Info($"[Mod] {u.Nick}: {a.nick} – aktives Auto → {s.currentCar}");
                return Json(new { ok = true, current = s.currentCar });
            }

            // add
            if (!System.Text.RegularExpressions.Regex.IsMatch(car, "^[A-Za-z0-9_\\-.]{1,64}$"))
                return Json(new { ok = false, error = "invalid_name" });
            if (s.ownedCars.Any(c => string.Equals(c, car, StringComparison.OrdinalIgnoreCase)))
                return Json(new { ok = false, error = "already_owned" });
            if (s.ownedCars.Count >= 500) return Json(new { ok = false, error = "too_many" });
            s.ownedCars.Add(car);
            if (string.IsNullOrEmpty(s.currentCar)) s.currentCar = car;
            _ctx.Accounts.Store(a);
            _ctx.Audit.Add("mod", u.Nick, $"Konto {a.nick}: Auto '{car}' hinzugefügt");
            Log.Info($"[Mod] {u.Nick}: {a.nick} – Auto '{car}' hinzugefügt");
            return Json(new { ok = true });
        }));

        app.MapPost("/accounts/delete", (HttpContext http) => Require(http, u =>
        {
            if (Data.Account.Rank(u.Permission) < 3) return Json(new { ok = false, error = "keine_rechte" });
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            string nick = f["nick"].ToString();
            var online = _ctx.Actions.Players().FirstOrDefault(s => string.Equals(s.Nick, nick, StringComparison.OrdinalIgnoreCase));
            if (online != null) _ctx.Actions.Kick(online.PlayerId, "Konto gelöscht");
            bool ok = _ctx.Accounts.Delete(nick);
            if (ok) { _ctx.Audit.Add("mod", u.Nick, $"Konto {nick} gelöscht"); Log.Info($"[Mod] {u.Nick}: Konto {nick} gelöscht"); }
            return Json(new { ok });
        }));

        // ---- News (TDU Media, News-Kanal des Clients) ----
        app.MapGet("/news", (HttpContext http) => Require(http, u => Results.Content(Pages.NewsPage(u.Nick, u.Permission), "text/html")));
        app.MapGet("/news/list", (HttpContext http) => Require(http, u => Json(_ctx.News.All().Select(n => new
        {
            id = n.id,
            header = n.header,
            message = n.message,
            icon = n.icon,
            island = n.island,
            islandName = n.island == 0 ? "Alle" : Islands.Name(n.island),
            author = n.author,
            created = n.created,
            updated = n.updated,
            hasImage = n.hasImage,
            kind = n.kind ?? "",
            version = n.version ?? "",
            category = n.category ?? "",
        }))));
        app.MapGet("/news/image", (HttpContext http) => Require(http, u =>
        {
            int id = int.TryParse(http.Request.Query["id"], out var i) ? i : 0;
            string b64 = _ctx.News.ImageBase64(id);
            if (b64 == null) return Results.NotFound();
            var bytes = Convert.FromBase64String(b64);
            return Results.File(bytes, bytes.Length > 0 && bytes[0] == 0x89 ? "image/png" : "image/jpeg");
        }));
        app.MapPost("/news/save", (HttpContext http) => Require(http, u =>
        {
            if (Data.Account.Rank(u.Permission) < 2) return Json(new { ok = false, error = "keine_rechte" });
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            string header = f["header"].ToString(), message = f["message"].ToString(), icon = f["icon"].ToString();
            string kind = f["kind"].ToString(), version = f["version"].ToString(), category = f["category"].ToString();
            int island = int.TryParse(f["island"], out var isl) ? isl : 0;
            int id = int.TryParse(f["id"], out var i) ? i : 0;
            if (string.IsNullOrWhiteSpace(header)) return Json(new { ok = false, error = "Überschrift fehlt" });
            if (string.IsNullOrWhiteSpace(message)) return Json(new { ok = false, error = "Text fehlt" });
            byte[] image = null;
            string imgB64 = f["image"].ToString();
            if (!string.IsNullOrWhiteSpace(imgB64))
            {
                image = Data.NewsStore.CheckImage(imgB64, out string imgErr);
                if (image == null) return Json(new { ok = false, error = imgErr });
            }
            bool removeImage = f["removeImage"].ToString() == "1";
            var n = id > 0 ? _ctx.News.Update(id, header, message, icon, island, image, removeImage, kind, version, category)
                           : _ctx.News.Add(header, message, icon, island, u.Nick, image, kind, version, category);
            if (n == null) return Json(new { ok = false, error = "not_found" });
            _ctx.Audit.Add("mod", u.Nick, $"News {(id > 0 ? "bearbeitet" : "angelegt")} #{n.id}: {n.header}");
            Log.Info($"[Mod] {u.Nick}: News {(id > 0 ? "bearbeitet" : "angelegt")} #{n.id} ({Islands.Name(n.island)}): {n.header}");
            return Json(new { ok = true, id = n.id });
        }));
        app.MapPost("/news/delete", (HttpContext http) => Require(http, u =>
        {
            if (Data.Account.Rank(u.Permission) < 2) return Json(new { ok = false, error = "keine_rechte" });
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            int id = int.TryParse(f["id"], out var i) ? i : 0;
            bool ok = _ctx.News.Delete(id);
            if (ok) { _ctx.Audit.Add("mod", u.Nick, $"News #{id} gelöscht"); Log.Info($"[Mod] {u.Nick}: News #{id} gelöscht"); }
            return Json(new { ok });
        }));

        // ---- Umfragen (TDU Media, Umfrage-Kanal des Clients) ----
        app.MapGet("/surveys", (HttpContext http) => Require(http, u => Results.Content(Pages.SurveysPage(u.Nick, u.Permission), "text/html")));
        app.MapGet("/surveys/list", (HttpContext http) => Require(http, u =>
        {
            var list = _ctx.Surveys.All(out var raw);
            return Json(list.Select(s => new
            {
                id = s.id,
                question = s.question,
                text = s.text,
                answers = s.answers,
                counts = s.counts,
                total = s.total,
                open = s.open,
                closed = raw.TryGetValue(s.id, out var it) && !it.open,
                created = s.created,
                closes = s.closes,
                island = it?.island ?? 0,
                islandName = (it?.island ?? 0) == 0 ? "Alle" : Islands.Name(it.island),
                author = it?.author ?? "",
            }));
        }));
        app.MapPost("/surveys/save", (HttpContext http) => Require(http, u =>
        {
            if (Data.Account.Rank(u.Permission) < 2) return Json(new { ok = false, error = "keine_rechte" });
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            string question = f["question"].ToString(), text = f["text"].ToString(), closes = f["closes"].ToString();
            var answers = f["answers"].ToString().Replace("\r", "").Split('\n');
            int island = int.TryParse(f["island"], out var isl) ? isl : 0;
            var s = _ctx.Surveys.Add(question, text, answers, island, closes, u.Nick, out string err);
            if (s == null) return Json(new { ok = false, error = err });
            _ctx.Audit.Add("mod", u.Nick, $"Umfrage angelegt #{s.id}: {s.question}");
            Log.Info($"[Mod] {u.Nick}: Umfrage angelegt #{s.id} ({(s.island == 0 ? "alle Inseln" : Islands.Name(s.island))}): {s.question}");
            return Json(new { ok = true, id = s.id });
        }));
        app.MapPost("/surveys/state", (HttpContext http) => Require(http, u =>
        {
            if (Data.Account.Rank(u.Permission) < 2) return Json(new { ok = false, error = "keine_rechte" });
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            int id = int.TryParse(f["id"], out var i) ? i : 0;
            string action = f["action"].ToString();
            bool ok = action switch
            {
                "close" => _ctx.Surveys.SetOpen(id, false),
                "open" => _ctx.Surveys.SetOpen(id, true),
                "reset" => _ctx.Surveys.ResetVotes(id),
                "delete" => _ctx.Surveys.Delete(id),
                _ => false,
            };
            string what = action switch { "close" => "geschlossen", "open" => "geöffnet", "reset" => "Stimmen zurückgesetzt", "delete" => "gelöscht", _ => action };
            if (ok) { _ctx.Audit.Add("mod", u.Nick, $"Umfrage #{id} {what}"); Log.Info($"[Mod] {u.Nick}: Umfrage #{id} {what}"); }
            return Json(new { ok });
        }));

        // ---- Config-Editor ----
        app.MapGet("/config", (HttpContext http) => Require(http, u => Results.Content(Pages.ConfigPage(u.Nick, u.Permission), "text/html")));
        app.MapGet("/config/data", (HttpContext http) => Require(http, u => Json(ConfigView())));
        app.MapPost("/config/save", (HttpContext http) => Require(http, u =>
        {
            if (Data.Account.Rank(u.Permission) < 3) return Json(new { ok = false, error = "keine_rechte" });
            var f = http.Request.ReadFormAsync().GetAwaiter().GetResult();
            var cfg = _ctx.Config;
            try
            {
                if (int.TryParse(f["tcpPort"], out var tcp)) cfg.tcpPort = tcp;
                if (int.TryParse(f["udpPort"], out var udp)) cfg.udpPort = udp;
                if (int.TryParse(f["maxPlayers"], out var mp)) cfg.maxPlayers = mp;
                if (long.TryParse(f["startMoney"], out var sm)) cfg.startMoney = sm;
                if (!string.IsNullOrWhiteSpace(f["motd"])) cfg.motd = f["motd"].ToString();
                if (!string.IsNullOrWhiteSpace(f["startSpot"])) cfg.startSpot = f["startSpot"].ToString();
                if (!string.IsNullOrWhiteSpace(f["startIsland"])) cfg.startIsland = f["startIsland"].ToString();
                var cars = f["starterCars"].ToString();
                if (!string.IsNullOrWhiteSpace(cars))
                    cfg.starterCars = cars.Split(new[] { ',', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                // MasterServer (Serverliste): Änderungen greifen sofort, die Anmeldung läuft im Hintergrund weiter.
                cfg.masterEnabled = !string.IsNullOrEmpty(f["masterEnabled"].ToString());
                if (!string.IsNullOrWhiteSpace(f["masterUrl"])) cfg.masterUrl = f["masterUrl"].ToString().Trim();
                cfg.masterName = f["masterName"].ToString().Trim();
                cfg.masterHost = f["masterHost"].ToString().Trim();
                if (int.TryParse(f["masterPort"], out var mport)) cfg.masterPort = mport;
                cfg.masterModMessage = f["masterModMessage"].ToString().Trim();
                if (!string.IsNullOrWhiteSpace(f["masterVersion"])) cfg.masterVersion = f["masterVersion"].ToString().Trim();
                cfg.masterLocked = !string.IsNullOrEmpty(f["masterLocked"].ToString());
                ServerConfig.Save(_ctx.ConfigFile, cfg);
                _ctx.Audit.Add("mod", u.Nick, "config.json geändert");
                Log.Info($"[Mod] {u.Nick}: config.json geändert");
                return Json(new { ok = true });
            }
            catch (Exception ex) { return Json(new { ok = false, error = ex.Message }); }
        }));
    }

    // ---- Helpers --------------------------------------------------------------

    object ConfigView()
    {
        var cfg = _ctx.Config;
        return new
        {
            tcpPort = cfg.tcpPort,
            udpPort = cfg.udpPort,
            dataDir = cfg.dataDir,
            startSpot = cfg.startSpot,
            startIsland = cfg.startIsland,
            startMoney = cfg.startMoney,
            maxPlayers = cfg.maxPlayers,
            motd = cfg.motd,
            starterCars = string.Join(", ", cfg.starterCars),
            webHost = cfg.webHost,
            webPort = cfg.webPort,
            masterEnabled = cfg.masterEnabled,
            masterUrl = cfg.masterUrl,
            masterId = cfg.masterId,
            masterName = cfg.masterName,
            masterHost = cfg.masterHost,
            masterPort = cfg.masterPort,
            masterModMessage = cfg.masterModMessage,
            masterVersion = cfg.masterVersion,
            masterLocked = cfg.masterLocked,
            masterHeartbeatMs = cfg.masterHeartbeatMs,
            masterRetryMs = cfg.masterRetryMs,
            masterStatus = _ctx.Master?.Status ?? "aus",
        };
    }

    List<object> PlayerList()
    {
        var now = DateTime.UtcNow;
        return _ctx.Actions.Players().Select(s => (object)new
        {
            id = s.PlayerId,
            nick = s.Nick,
            island = Islands.Name(s.Island),
            islandCode = s.Island,
            carId = s.CarId,
            inCar = s.InCar && (now - s.LastUdp).TotalSeconds < 10,
            money = s.Money,
            nbCars = s.NbCars,
            nationality = s.Nationality,
            px = Math.Round(s.Px, 1),
            pz = Math.Round(s.Pz, 1),
            rideWith = s.RideWith,
            passenger = s.Passenger,
            udp = s.UdpEndPoint?.ToString() ?? "",
            lastUdp = s.LastUdp == DateTime.MinValue ? "" : (now - s.LastUdp).TotalSeconds.ToString("0") + "s",
            since = s.Since.ToString("HH:mm:ss"),
            minutes = (int)(DateTime.Now - s.Since).TotalMinutes,
            remote = s.Conn?.Remote ?? "",
        }).ToList();
    }

    /// <summary>Alle wichtigen Daten eines Online-Spielers (Verbindung, P2P, Lobby, Zustand, Konto).</summary>
    object PlayerInfo(int pid)
    {
        var s = _ctx.Actions.Find(pid);
        if (s == null) return null;
        var now = DateTime.UtcNow;
        string Ago(DateTime t) => t == DateTime.MinValue ? "nie" : (now - t).TotalSeconds < 120 ? $"vor {(now - t).TotalSeconds:0} s" : $"vor {(now - t).TotalMinutes:0} Min.";
        var cfg = _ctx.Config;
        var all = _ctx.Actions.Players();
        var lobby = _ctx.Lobbies.Get(s.Lobby);
        // Partner, denen der Server die Endpunkte dieses Spielers für P2P vermittelt (gleiche Lobby, beide P2P-fähig, UDP bekannt)
        var peers = all.Where(o => o != s && TDU2Server.Sessions.LobbyManager.SameLobby(s, o)).Select(o => new
        {
            id = o.PlayerId,
            nick = o.Nick,
            p2p = s.P2P && o.P2P && s.UdpEndPoint != null && o.UdpEndPoint != null,
            sameLan = s.UdpEndPoint != null && o.UdpEndPoint != null && Equals(s.UdpEndPoint.Address, o.UdpEndPoint.Address),
        }).ToList();
        var a = _ctx.Accounts.Load(s.Nick);
        string invites; lock (s.Lock) invites = string.Join(", ", s.Invites.Select(i => $"{i.Nick} ({i.Kind})"));
        int notices; lock (s.Lock) notices = s.Notices.Count;
        return new
        {
            ok = true,
            id = s.PlayerId,
            nick = s.Nick,
            since = s.Since.ToString("dd.MM. HH:mm:ss"),
            minutes = (int)(DateTime.Now - s.Since).TotalMinutes,
            // Verbindung
            tcp = s.Conn?.Remote ?? "",
            ip = (s.Conn?.Remote ?? "").Split(':')[0],
            udp = s.UdpEndPoint?.ToString() ?? "",
            udpLast = Ago(s.LastUdp),
            udpActive = s.UdpEndPoint != null && s.UdpActive(now, 10),
            udpIpDiffers = s.UdpEndPoint != null && !string.IsNullOrEmpty(s.Conn?.Remote) && s.Conn.Remote.Split(':')[0] != s.UdpEndPoint.Address.ToString(),
            statusLast = Ago(s.LastStatus),
            statusIntervalMs = cfg.statusIntervalMs,
            localEp = s.LocalEp ?? "",
            // P2P
            p2pServer = cfg.p2pEnabled,
            p2p = s.P2P,
            relayCount = s.RelayCount,
            peers,
            // Lobby
            lobby = s.Lobby,
            lobbyCount = lobby?.Count ?? 0,
            lobbyMax = cfg.maxPlayersPerLobby,
            island = Islands.Name(s.Island),
            // Zustand
            carId = s.CarId,
            color = s.Color,
            inCar = s.InCar && s.UdpActive(now, 10),
            px = Math.Round(s.Px, 1),
            pz = Math.Round(s.Pz, 1),
            money = s.Money,
            nbCars = s.NbCars,
            nationality = s.Nationality,
            rideWith = s.RideWith,
            passenger = s.Passenger,
            invites,
            notices,
            mood = s.Mood,
            moodText = s.MoodTxt,
            hasAvatar = !string.IsNullOrEmpty(s.Avatar),
            photoVer = s.PhotoVer,
            // Konto
            permission = a?.permission ?? "",
            email = a?.email ?? "",
            created = a?.created ?? "",
            lastLogin = a?.lastLogin ?? "",
            banned = a?.IsBanned ?? false,
        };
    }

    object LobbyList()
    {
        var now = DateTime.UtcNow;
        var players = _ctx.Actions.Players();
        object Member(TDU2Server.Sessions.Session s) => new
        {
            id = s.PlayerId,
            nick = s.Nick,
            island = Islands.Name(s.Island),
            islandCode = s.Island,
            carId = s.CarId,
            inCar = s.InCar && (now - s.LastUdp).TotalSeconds < 10,
            rideWith = s.RideWith,
            passenger = s.Passenger,
            p2p = s.P2P,
            udp = s.UdpEndPoint != null && s.UdpActive(now, 10),
            minutes = (int)(DateTime.Now - s.Since).TotalMinutes,
        };
        var lobbies = _ctx.Lobbies.All().Select(l =>
        {
            List<int> ids;
            lock (l.Members) ids = l.Members.ToList();
            var members = players.Where(p => ids.Contains(p.PlayerId)).OrderBy(p => p.Nick, StringComparer.OrdinalIgnoreCase).Select(Member).ToList();
            return new
            {
                id = l.Id,
                island = Islands.Name(l.Island),
                islandCode = l.Island,
                count = l.Count,
                created = l.Created.ToLocalTime().ToString("HH:mm:ss"),
                minutes = (int)(now - l.Created).TotalMinutes,
                members,
                missing = ids.Count - members.Count,   // Nummern ohne Sitzung (sollte 0 sein)
            };
        }).ToList();
        var without = players.Where(p => p.Lobby == 0).Select(Member).ToList();
        return new { max = _ctx.Config.maxPlayersPerLobby, online = players.Count, lobbies, without };
    }

    IResult Require(HttpContext http, Func<WebAuth.User, IResult> onOk)
    {
        var u = _auth.Current(http);
        if (u == null) return Results.Redirect("/");
        return onOk(u);
    }

    static IResult Json(object o) => Results.Json(o);
}
