using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using YtMobile.Json;
using Microsoft.Extensions.Caching.Memory;

namespace YtMobile.Auth;

/// <summary>Was das Frontend über den angemeldeten Kanal wissen muss.</summary>
public sealed record SessionUser(string ChannelId, string Name, string? Avatar);

/// <summary><paramref name="UserKey"/> ist eine kurze, stabile Kennung des Benutzers.
/// Sie geht in Cache-Schlüssel ein, damit private Antworten nicht zwischen Browsern
/// geteilt werden.</summary>
public sealed record AuthContext(string AccessToken, string UserKey, SessionUser User);

public sealed class NotSignedInException() : Exception("Nicht angemeldet.");

/// <summary>Zwischenstand des Anmeldevorgangs, unterwegs im kurzlebigen Flow-Cookie.</summary>
public sealed record OAuthFlow(
    [property: JsonPropertyName("v")] string Verifier,
    [property: JsonPropertyName("s")] string State,
    [property: JsonPropertyName("r")] string? Return);

internal sealed record SessionData(
    [property: JsonPropertyName("rt")] string Rt,
    [property: JsonPropertyName("c")] string ChannelId,
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("a")] string? Avatar);

/// <summary>
/// Die Anmeldung lebt ausschließlich im Browser: Das Refresh-Token reist verschlüsselt
/// im Cookie mit. Dadurch überlebt sie einen Neustart der App, ohne dass der Server eine
/// Sitzungstabelle führen müsste, und jeder Browser bleibt für sich.
/// </summary>
public sealed class SessionStore(
    IDataProtectionProvider provider,
    IMemoryCache cache,
    GoogleAuthService auth)
{
    private const string SessionCookie = "yt_session";
    private const string FlowCookie = "yt_oauth";
    private const string CallbackPath = "/api/auth/callback";

    private readonly IDataProtector _session = provider.CreateProtector("YtMobile.Session.v1");
    private readonly ITimeLimitedDataProtector _flow =
        provider.CreateProtector("YtMobile.OAuthFlow.v1").ToTimeLimitedDataProtector();

    // -- Anmeldevorgang ----------------------------------------------------

    /// <summary>Google prüft die Weiterleitung streng: HTTPS ist Pflicht, einzige Ausnahme
    /// sind localhost und 127.0.0.1. Eine LAN-IP wie 192.168.x.x lehnt Google ab, deshalb
    /// scheitert die Anmeldung dort schon hier — mit einer verständlichen Meldung statt
    /// einer Fehlerseite von Google.</summary>
    public static string RedirectUri(HttpContext ctx)
    {
        var host = ctx.Request.Host.Host;
        var allowed = ctx.Request.IsHttps
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host is "127.0.0.1" or "[::1]" or "::1";

        if (!allowed)
            throw new AuthException(
                $"Google erlaubt \"{host}\" nicht als Weiterleitungsziel. " +
                "Die Anmeldung ist nur über http://localhost:5099 möglich.");

        return $"{ctx.Request.Scheme}://{ctx.Request.Host}{CallbackPath}";
    }

    public OAuthFlow BeginFlow(HttpContext ctx, string? returnRoute)
    {
        var flow = new OAuthFlow(
            GoogleAuthService.CreateVerifier(),
            GoogleAuthService.CreateVerifier(),
            returnRoute);

        var payload = _flow.Protect(JsonSerializer.SerializeToUtf8Bytes(flow, AppJson.Default.OAuthFlow), TimeSpan.FromMinutes(10));
        ctx.Response.Cookies.Append(FlowCookie, Convert.ToBase64String(payload), Options(ctx, TimeSpan.FromMinutes(10)));

        return flow;
    }

    /// <summary>Liefert den Zwischenstand nur, wenn der zurückgereichte state zu dem im
    /// Cookie passt — das bindet die Rückkehr an genau den Browser, der angefangen hat.</summary>
    public OAuthFlow? EndFlow(HttpContext ctx, string? state)
    {
        var raw = ctx.Request.Cookies[FlowCookie];
        ctx.Response.Cookies.Delete(FlowCookie, Options(ctx, null));

        if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(state)) return null;

        try
        {
            var flow = JsonSerializer.Deserialize(_flow.Unprotect(Convert.FromBase64String(raw)), AppJson.Default.OAuthFlow);
            return flow is not null && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(flow.State), Encoding.UTF8.GetBytes(state))
                    ? flow
                    : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    // -- Sitzung -----------------------------------------------------------

    public void SignIn(HttpContext ctx, string refreshToken, SessionUser user)
    {
        var data = new SessionData(refreshToken, user.ChannelId, user.Name, user.Avatar);
        var payload = _session.Protect(JsonSerializer.SerializeToUtf8Bytes(data, AppJson.Default.SessionData));

        // 400 Tage ist die Obergrenze, die Chrome für Cookies durchlässt.
        ctx.Response.Cookies.Append(
            SessionCookie, Convert.ToBase64String(payload), Options(ctx, TimeSpan.FromDays(400)));
    }

    public SessionUser? Read(HttpContext ctx) =>
        ReadData(ctx) is { } d ? new SessionUser(d.ChannelId, d.Name, d.Avatar) : null;

    /// <summary>Meldet ab und widerruft das Token bei Google, damit die App in den
    /// Kontoeinstellungen nicht als weiterhin berechtigt stehen bleibt.</summary>
    public async Task SignOutAsync(HttpContext ctx, CancellationToken ct)
    {
        if (ReadData(ctx) is { } data)
        {
            cache.Remove(TokenCacheKey(UserKey(data.Rt)));
            await auth.RevokeAsync(data.Rt, ct);
        }
        ctx.Response.Cookies.Delete(SessionCookie, Options(ctx, null));
    }

    /// <summary>Zugriffstoken für den aktuellen Browser, bei Bedarf frisch geholt.
    /// <paramref name="force"/> umgeht den Zwischenspeicher, wenn Google ein eigentlich
    /// gültiges Token doch mit 401 abgelehnt hat.</summary>
    public async Task<AuthContext?> AuthAsync(HttpContext ctx, CancellationToken ct, bool force = false)
    {
        if (ReadData(ctx) is not { } data) return null;

        var userKey = UserKey(data.Rt);
        var user = new SessionUser(data.ChannelId, data.Name, data.Avatar);
        var key = TokenCacheKey(userKey);

        if (!force && cache.TryGetValue(key, out string? cached) && cached is not null)
            return new AuthContext(cached, userKey, user);

        try
        {
            // force heißt: Google hat ein eigentlich gültiges Token abgelehnt. Dann ist
            // auch ein gerade laufender Abruf nichts wert — dieser Aufruf holt selbst.
            var fresh = force
                ? await FreshTokenAsync(data.Rt, key, ct)
                : await SharedFreshTokenAsync(data.Rt, key).WaitAsync(ct);

            return new AuthContext(fresh, userKey, user);
        }
        catch (AuthException ex) when (ex.SessionInvalid)
        {
            ctx.Response.Cookies.Delete(SessionCookie, Options(ctx, null));
            return null;
        }
    }

    /// <summary>Was der Warmlauf braucht, und mehr nicht: das Refresh-Token und die
    /// Benutzerkennung, beides aus dem Cookie und ohne einen einzigen Netzaufruf.</summary>
    public (string RefreshToken, string UserKey)? Ticket(HttpContext ctx) =>
        ReadData(ctx) is { } d ? (d.Rt, UserKey(d.Rt)) : null;

    /// <summary>Zugriffstoken zu einem Refresh-Token, ohne <see cref="HttpContext"/> — für
    /// Aufgaben, die die Anfrage überleben. Eine ungültige Sitzung kann hier kein Cookie
    /// mehr löschen, deshalb meldet sie sich nur mit <c>null</c>; das nächste Mal, dass
    /// der Browser selbst fragt, räumt die Cookie-Variante auf.</summary>
    public async Task<string?> AccessTokenAsync(string refreshToken, CancellationToken ct)
    {
        var key = TokenCacheKey(UserKey(refreshToken));
        if (cache.TryGetValue(key, out string? cached) && cached is not null) return cached;

        try
        {
            return await SharedFreshTokenAsync(refreshToken, key).WaitAsync(ct);
        }
        catch (AuthException)
        {
            return null;
        }
    }

    /// <summary>Erneuert das Zugriffstoken genau einmal, auch wenn mehrere gleichzeitig
    /// danach fragen. Gemessen: Beim ersten Aufruf der Startseite holten der Warmlauf und
    /// die <c>/api/home</c>-Anfrage dasselbe Token nebeneinander — zwei Anfragen an Google
    /// für ein Ergebnis. Die Sperre ist statisch, weil <c>SessionStore</c> pro Anfrage
    /// neu entsteht; geteilt wird über den <c>IMemoryCache</c>, den sich alle teilen.</summary>
    private static readonly Lock TokenGate = new();

    private Task<string> SharedFreshTokenAsync(string refreshToken, string key)
    {
        var runKey = $"{key}|läuft";

        lock (TokenGate)
        {
            if (cache.TryGetValue(runKey, out Task<string>? running) && running is not null) return running;

            var task = Holen();
            cache.Set(runKey, task, TimeSpan.FromMinutes(1));
            return task;
        }

        // Ohne Abbruch-Token: Bricht der eine Aufrufer ab, sollen die anderen ihr Token
        // trotzdem bekommen. Wer nicht mehr warten will, tut das über WaitAsync.
        async Task<string> Holen()
        {
            try { return await FreshTokenAsync(refreshToken, key, CancellationToken.None); }
            finally { cache.Remove(runKey); }
        }
    }

    private async Task<string> FreshTokenAsync(string refreshToken, string key, CancellationToken ct)
    {
        var token = await auth.RefreshAsync(refreshToken, ct);
        // Eine Minute Sicherheitsabstand, damit kein Token mitten in einer Anfrage abläuft.
        var ttl = token.ExpiresAt - TimeSpan.FromMinutes(1) - DateTimeOffset.UtcNow;
        if (ttl > TimeSpan.Zero) cache.Set(key, token.AccessToken, ttl);
        return token.AccessToken;
    }

    /// <summary>Verwirft alle zwischengespeicherten privaten Antworten dieses Benutzers.
    /// Nötig nach jedem Schreibvorgang, sonst zeigt die App den alten Stand.</summary>
    public void Invalidate(string userKey, params string[] tags)
    {
        foreach (var tag in tags) cache.Remove($"{userKey}|{tag}");
    }

    // -- intern ------------------------------------------------------------

    private SessionData? ReadData(HttpContext ctx)
    {
        var raw = ctx.Request.Cookies[SessionCookie];
        if (string.IsNullOrEmpty(raw)) return null;

        try
        {
            return JsonSerializer.Deserialize(_session.Unprotect(Convert.FromBase64String(raw)), AppJson.Default.SessionData);
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            // Schlüssel gewechselt oder Cookie beschädigt — als abgemeldet behandeln.
            ctx.Response.Cookies.Delete(SessionCookie, Options(ctx, null));
            return null;
        }
    }

    private static string TokenCacheKey(string userKey) => $"at:{userKey}";

    /// <summary>Kurzer Fingerabdruck des Refresh-Tokens. Das Token selbst taucht damit
    /// weder in Cache-Schlüsseln noch in Logs auf.</summary>
    private static string UserKey(string refreshToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)))[..16].ToLowerInvariant();

    private static CookieOptions Options(HttpContext ctx, TimeSpan? life) => new()
    {
        HttpOnly = true,
        IsEssential = true,
        // Lax statt Strict: Die Rückkehr von Google ist eine seitenfremde Navigation,
        // bei Strict käme das Flow-Cookie dabei nicht mit.
        SameSite = SameSiteMode.Lax,
        Secure = ctx.Request.IsHttps,
        Path = "/",
        MaxAge = life,
    };
}
