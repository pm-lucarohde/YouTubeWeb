using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace YtMobile.Auth;

public sealed class GoogleAuthOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

/// <summary><paramref name="sessionInvalid"/> is true when Google refused the refresh
/// token for good (revoked, expired, client changed) — the session must be dropped
/// rather than retried.</summary>
public sealed class AuthException(string message, bool sessionInvalid = false) : Exception(message)
{
    public bool SessionInvalid { get; } = sessionInvalid;
}

/// <summary>Nur der <paramref name="RefreshToken"/> wird dauerhaft aufbewahrt; er fehlt
/// in der Antwort auf ein Refresh, weil Google ihn dort nicht erneut mitschickt.</summary>
public sealed record TokenSet(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// OAuth 2.0 gegen Google, per Hand über die drei Endpunkte statt über die
/// Google.Apis-Bibliothek — passend zum übrigen Projekt, das die Data API ebenfalls
/// als rohes JSON anspricht.
/// </summary>
public sealed class GoogleAuthService(HttpClient http, IOptions<GoogleAuthOptions> options)
{
    /// <summary>Ein einziger Scope deckt Abos, Kommentare, Bewertungen und Playlists ab.
    /// Er gilt bei Google als "sensibel", verlangt aber keine Kombination mit weiteren.</summary>
    public const string Scope = "https://www.googleapis.com/auth/youtube.force-ssl";

    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";

    private readonly GoogleAuthOptions _opt = options.Value;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_opt.ClientId) && !string.IsNullOrWhiteSpace(_opt.ClientSecret);

    // -- PKCE --------------------------------------------------------------

    public static string CreateVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // -- Flow --------------------------------------------------------------

    public string AuthUrl(string redirectUri, string state, string challenge)
    {
        var query = new (string Key, string Value)[]
        {
            ("client_id", _opt.ClientId),
            ("redirect_uri", redirectUri),
            ("response_type", "code"),
            ("scope", Scope),
            // Ohne access_type=offline gibt es gar kein Refresh-Token, ohne prompt=consent
            // liefert Google es nur beim allerersten Mal — danach wäre die Anmeldung nach
            // einer Stunde vorbei.
            ("access_type", "offline"),
            ("prompt", "consent"),
            ("include_granted_scopes", "true"),
            ("state", state),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
        };

        var parts = query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}");
        return $"{AuthEndpoint}?{string.Join('&', parts)}";
    }

    public Task<TokenSet> ExchangeAsync(string code, string verifier, string redirectUri, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["redirect_uri"] = redirectUri,
        }, ct);

    public Task<TokenSet> RefreshAsync(string refreshToken, CancellationToken ct) =>
        TokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct);

    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token });
        // Ein fehlgeschlagener Widerruf darf das Abmelden nicht aufhalten — das Cookie
        // wird ohnehin gelöscht.
        try { using var _ = await http.PostAsync(RevokeEndpoint, form, ct); }
        catch (HttpRequestException) { }
    }

    private async Task<TokenSet> TokenAsync(Dictionary<string, string> fields, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new AuthException("Keine Google-OAuth-Zugangsdaten konfiguriert (Google:ClientId / Google:ClientSecret).");

        fields["client_id"] = _opt.ClientId;
        fields["client_secret"] = _opt.ClientSecret;

        using var res = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(fields), ct);
        var body = await res.Content.ReadAsByteArrayAsync(ct);

        if (!res.IsSuccessStatusCode) throw Describe(res.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var access = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrEmpty(access))
            throw new AuthException("Google hat kein Zugriffstoken geliefert.");

        var lifetime = root.TryGetProperty("expires_in", out var ex) && ex.TryGetInt32(out var secs)
            ? secs
            : 3600;

        return new TokenSet(
            access,
            root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            DateTimeOffset.UtcNow.AddSeconds(lifetime));
    }

    private static AuthException Describe(HttpStatusCode status, byte[] body)
    {
        string? code = null, description = null;
        try
        {
            var root = JsonDocument.Parse(body).RootElement;
            code = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
        }
        catch (JsonException) { }

        return code switch
        {
            // Widerrufen, abgelaufen (Testing-Status: nach 7 Tagen) oder Client gewechselt.
            "invalid_grant" => new AuthException("Die Anmeldung ist abgelaufen. Bitte neu anmelden.", sessionInvalid: true),
            "invalid_client" => new AuthException("Google hat die OAuth-Zugangsdaten abgelehnt. Client-ID und Secret prüfen."),
            "redirect_uri_mismatch" => new AuthException("Die Weiterleitungsadresse ist in der Google Cloud Console nicht eingetragen."),
            _ => new AuthException(description ?? code ?? $"Anmeldefehler {(int)status}."),
        };
    }
}
