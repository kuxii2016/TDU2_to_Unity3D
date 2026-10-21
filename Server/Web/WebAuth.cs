using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;

namespace TDU2Server.Web;

/// <summary>
/// Sitzungen des Web-Panels. Das Panel läuft nur im gleichen Prozess, deshalb ein einfacher
/// In-Memory-Token-Store mit Cookie. Rolle/Rang kommt aus dem Konto (Account.permission).
/// </summary>
public sealed class WebAuth
{
    public sealed class User
    {
        public string Nick = "";
        public string Permission = "player";
        public DateTime Expires;
    }

    const string Cookie = "tdu2panel";
    readonly Dictionary<string, User> _sessions = new();
    readonly object _lock = new();

    /// <summary>Neue Sitzung anlegen und ein Token (Cookie-Wert) zurückgeben.</summary>
    public string Start(string nick, string permission)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        lock (_lock) _sessions[token] = new User { Nick = nick, Permission = permission, Expires = DateTime.UtcNow.AddHours(12) };
        return token;
    }

    public void End(string token)
    {
        if (token == null) return;
        lock (_lock) _sessions.Remove(token);
    }

    /// <summary>Angemeldeten Benutzer aus dem Cookie lesen (oder null).</summary>
    public User Current(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(Cookie, out var token) || string.IsNullOrEmpty(token)) return null;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(token, out var u)) return null;
            if (u.Expires < DateTime.UtcNow) { _sessions.Remove(token); return null; }
            return u;
        }
    }

    public static void SetCookie(HttpContext http, string token) =>
        http.Response.Cookies.Append(Cookie, token, new CookieOptions
        {
            HttpOnly = true, IsEssential = true, SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddHours(12),
        });

    public static void ClearCookie(HttpContext http) => http.Response.Cookies.Delete(Cookie);
    public static string TokenOf(HttpContext http) =>
        http.Request.Cookies.TryGetValue(Cookie, out var t) ? t : null;
}
