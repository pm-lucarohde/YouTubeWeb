using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.ResponseCompression;
using YtMobile.Auth;
using YtMobile.Json;
using YtMobile.Youtube;

// CreateSlimBuilder statt CreateBuilder: der schlanke Aufbau ohne die Teile, die diese
// App nie anfasst. Zwei davon braucht sie allerdings doch, und beide fehlen darin —
// deshalb stehen sie gleich darunter ausdrücklich.
var builder = WebApplication.CreateSlimBuilder(args);

// Der schlanke Kestrel bringt nur UseKestrelCore mit, und darin ist kein HTTPS. Ohne
// diese Zeile startet die App auf der https-Adresse aus run.sh gar nicht erst.
builder.WebHost.UseKestrelHttpsConfiguration();

// User-Secrets sind im schlanken Aufbau ebenfalls nicht dabei. Dort liegen der API-Key
// und die OAuth-Zugangsdaten; ohne sie zeigt die App nur ihre Setup-Anleitung.
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddUserSecrets<Program>(optional: true);

// Ohne Reflexion zur Laufzeit muss jede JSON-Form vorab beschrieben sein. Der Kontext
// wird vorn in die Kette gehängt, damit er vor allen anderen Auflösern gefragt wird.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJson.Default));

builder.Services.Configure<YouTubeOptions>(builder.Configuration.GetSection("YouTube"));

// Convenience: accept a plain YOUTUBE_API_KEY next to the canonical YouTube__ApiKey.
if (Environment.GetEnvironmentVariable("YOUTUBE_API_KEY") is { Length: > 0 } envKey)
    builder.Services.PostConfigure<YouTubeOptions>(o => o.ApiKey = envKey);

builder.Services.Configure<GoogleAuthOptions>(builder.Configuration.GetSection("Google"));

builder.Services.AddMemoryCache();

// Verschlüsselt das Sitzungs-Cookie. Die Schlüssel liegen unter ~/.aspnet/DataProtection-Keys
// und überleben damit einen Neustart der App — niemand wird durch ein Deployment abgemeldet.
builder.Services.AddDataProtection();
builder.Services.AddScoped<SessionStore>();
builder.Services.AddHttpClient<GoogleAuthService>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient<YouTubeService>(c =>
{
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.Add("Accept", "application/json");
})
// Die Shorts-Erkennung unterscheidet 200 von 303 auf youtube.com/shorts/<id>.
// Mit automatischem Folgen der Weiterleitung wäre beides 200. Die Data API
// selbst antwortet immer direkt, ihr fehlt dadurch nichts.
.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

// Brotli/gzip for the JSON feeds and the static assets — matters on slow mobile links.
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.Providers.Add<BrotliCompressionProvider>();
    o.Providers.Add<GzipCompressionProvider>();
    o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        ["application/json", "image/svg+xml", "application/manifest+json"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);

var app = builder.Build();

app.UseResponseCompression();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var headers = ctx.Context.Response.Headers;
        headers.CacheControl = app.Environment.IsDevelopment()
            ? "no-cache"
            : "public,max-age=604800,immutable";
    }
});

// Die Shell wird nie gecacht und trägt ein Token aus den Änderungszeiten von CSS/JS
// in deren URLs ein. Geänderte Dateien werden dadurch zu neuen URLs — ein einmal
// aggressiv gecachtes Asset kann den Browser so nicht mehr auf einem alten Stand
// festhalten. UseDefaultFiles() ist bewusst raus, sonst würde die statische
// index.html "/" bedienen, bevor dieser Handler drankommt.
var webRoot = app.Environment.WebRootPath;

string AssetToken()
{
    long stamp = 0;
    foreach (var rel in (string[])["css/app.css", "js/app.js", "yt-logo.svg"])
    {
        var file = Path.Combine(webRoot, rel);
        if (File.Exists(file)) stamp = stamp * 31 + File.GetLastWriteTimeUtc(file).Ticks;
    }
    return ((ulong)stamp).ToString("x");
}

/// <summary>Die Startseite bringt die Konfiguration schon mit, statt dass der Browser
/// sie in einer eigenen Runde nachfragt — das spart die Runde und den Token-Abruf bei
/// Google auf dem kritischen Pfad. Ist jemand angemeldet, läuft außerdem der Aufbau der
/// Startseite schon los, während der Browser die Seite noch parst.</summary>
IResult Shell(HttpContext ctx, YouTubeService yt, GoogleAuthService auth, SessionStore sessions,
    IServiceScopeFactory scopes, ILoggerFactory logs)
{
    ctx.Response.Headers.CacheControl = "no-store";

    // Dieselbe Form wie die Antwort von /api/config — der Browser liest beides mit
    // demselben Code, und seit es ein benannter Typ ist, kann das nicht mehr
    // auseinanderlaufen. Zum Encoder siehe AppJson: Er maskiert <, > und &, ein
    // Kanalname kann das eingebettete <script> also nicht verlassen.
    var config = JsonSerializer.Serialize(
        new ConfigDto(yt.IsConfigured, auth.IsConfigured, sessions.Read(ctx)),
        AppJson.Default.ConfigDto);

    var html = File.ReadAllText(Path.Combine(webRoot, "index.html"))
        .Replace("__V__", AssetToken())
        .Replace("__CONFIG__", config);

    if (yt.IsConfigured && sessions.Ticket(ctx) is { } ticket)
        WarmHome(scopes, logs.CreateLogger("Warmlauf"), ticket.RefreshToken, ticket.UserKey);

    return Results.Content(html, "text/html", System.Text.Encoding.UTF8);
}

/// <summary>Der Warmlauf überlebt die Anfrage, die ihn angestoßen hat. Er darf deshalb
/// weder den <see cref="HttpContext"/> noch einen Dienst aus dessen Bereich anfassen —
/// mit reist nur das Refresh-Token als Zeichenkette, und die Dienste kommen aus einem
/// eigenen Bereich.</summary>
static void WarmHome(IServiceScopeFactory scopes, ILogger log, string refreshToken, string userKey) =>
    _ = Task.Run(async () =>
    {
        using var scope = scopes.CreateScope();
        try
        {
            var sessions = scope.ServiceProvider.GetRequiredService<SessionStore>();
            var yt = scope.ServiceProvider.GetRequiredService<YouTubeService>();

            if (await sessions.AccessTokenAsync(refreshToken, CancellationToken.None) is { } token)
                await yt.WarmHomeAsync(token, userKey, CancellationToken.None);
        }
        catch (Exception e)
        {
            // Der Warmlauf ist reine Vorarbeit — scheitert er, merkt der Nutzer nur, dass
            // die Startseite wieder so lange braucht wie vorher.
            log.LogDebug(e, "Warmlauf der Startseite fehlgeschlagen");
        }
    });

app.MapGet("/", Shell);

var api = app.MapGroup("/api");

api.MapGet("/config", (HttpContext ctx, YouTubeService yt, GoogleAuthService auth, SessionStore sessions) =>
    TypedResults.Ok(new ConfigDto(yt.IsConfigured, auth.IsConfigured, sessions.Read(ctx))));

api.MapGet("/categories", async (YouTubeService yt, CancellationToken ct) =>
    await Guarded(() => yt.CategoriesAsync(ct)));

api.MapGet("/feed", async (YouTubeService yt, string? category, string? page, CancellationToken ct) =>
    await Guarded(() => yt.TrendingAsync(category, page, ct)));

api.MapGet("/shorts", async (YouTubeService yt, CancellationToken ct) =>
    await Guarded(() => yt.ShortsAsync(ct)));

api.MapGet("/search", async (YouTubeService yt, string q, string? page, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(q)
        ? TypedResults.BadRequest(new ErrorDto("Suchbegriff fehlt."))
        : await Guarded(() => yt.SearchAsync(q.Trim(), page, ct)));

// Notnagel, wenn das Tageslimit für Suchanfragen aufgebraucht ist: Die Abo-Liste liegt im
// Cache und braucht kein search.list. Bewusst ein eigener Endpunkt — der normale Suchpfad
// bleibt unverändert und wird nur im Fehlerfall überhaupt umgangen.
api.MapGet("/search/channels", async (HttpContext ctx, YouTubeService yt, SessionStore s, string q, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(q)
        ? TypedResults.BadRequest(new ErrorDto("Suchbegriff fehlt."))
        : await Authed(ctx, s, a => yt.SubscribedChannelsAsync(a.AccessToken, a.UserKey, q.Trim(), ct)));

// ?shorts=1 nur aus der Seitenspalte neben einem laufenden Short; die Kanalseite selbst
// trennt Shorts immer ab.
api.MapGet("/channel/{id}", async (YouTubeService yt, string id, string? page, string? shorts, CancellationToken ct) =>
    await Guarded(() => yt.ChannelVideosAsync(id, page, ct, shorts == "1")));

api.MapGet("/comments/{id}", async (HttpContext ctx, YouTubeService yt, SessionStore sessions,
        string id, string? page, CancellationToken ct) =>
    await Guarded(() => yt.CommentsAsync(id, page, sessions.Read(ctx)?.ChannelId, ct)));

api.MapGet("/comments/{id}/replies", async (HttpContext ctx, YouTubeService yt, SessionStore sessions,
        string id, string? parent, string? page, CancellationToken ct) =>
    string.IsNullOrWhiteSpace(parent)
        ? TypedResults.BadRequest(new ErrorDto("Kommentar fehlt."))
        : await Guarded(() => yt.RepliesAsync(parent, page, sessions.Read(ctx)?.ChannelId, ct)));

api.MapGet("/related/{id}", async (YouTubeService yt, string id, CancellationToken ct) =>
    await Guarded(() => yt.RelatedAsync(id, ct)));

api.MapGet("/video/{id}", async (YouTubeService yt, string id, CancellationToken ct) =>
    await Guarded(async () => await yt.VideoAsync(id, ct) is { } v
        ? v
        : throw new YouTubeException("Video nicht gefunden oder nicht verfügbar.")));

// -- Anmeldung ---------------------------------------------------------------

api.MapGet("/auth/login", (HttpContext ctx, SessionStore sessions, GoogleAuthService auth, string? @return) =>
{
    if (!auth.IsConfigured)
        return TypedResults.Json(new ErrorDto("Keine Google-OAuth-Zugangsdaten hinterlegt (Google:ClientId / Google:ClientSecret)."), AppJson.Default.ErrorDto, statusCode: 503);

    try
    {
        var redirect = SessionStore.RedirectUri(ctx);
        var flow = sessions.BeginFlow(ctx, @return);
        return Results.Redirect(auth.AuthUrl(redirect, flow.State, GoogleAuthService.Challenge(flow.Verifier)));
    }
    catch (AuthException ex)
    {
        return TypedResults.Json(new ErrorDto(ex.Message), AppJson.Default.ErrorDto, statusCode: 400);
    }
});

api.MapGet("/auth/callback", async (HttpContext ctx, SessionStore sessions, GoogleAuthService auth,
        YouTubeService yt, string? code, string? state, string? error, CancellationToken ct) =>
{
    // Das Flow-Cookie wird in jedem Fall verbraucht, auch wenn der Nutzer abgebrochen hat.
    var flow = sessions.EndFlow(ctx, state);

    if (!string.IsNullOrEmpty(error))
        return Results.Redirect(Failed("Die Anmeldung wurde abgebrochen."));

    if (flow is null || string.IsNullOrEmpty(code))
        return Results.Redirect(Failed("Die Anmeldung ist abgelaufen. Bitte noch einmal versuchen."));

    try
    {
        var token = await auth.ExchangeAsync(code, flow.Verifier, SessionStore.RedirectUri(ctx), ct);

        // Ohne Refresh-Token wäre die Anmeldung nach einer Stunde wieder weg.
        if (token.RefreshToken is not { Length: > 0 } refresh)
            return Results.Redirect(Failed("Google hat kein dauerhaftes Token geliefert. Zugriff im Google-Konto entziehen und neu anmelden."));

        var me = await yt.MeAsync(token.AccessToken, ct);
        sessions.SignIn(ctx, refresh, new SessionUser(me?.Id ?? "", me?.Title ?? "Mein Konto", me?.Thumb));

        return Results.Redirect(Landing(flow.Return));
    }
    catch (AuthException ex)
    {
        return Results.Redirect(Failed(ex.Message));
    }
    catch (YouTubeException ex)
    {
        return Results.Redirect(Failed(ex.Message));
    }
});

api.MapPost("/auth/logout", async (HttpContext ctx, SessionStore sessions, CancellationToken ct) =>
{
    await sessions.SignOutAsync(ctx, ct);
    return TypedResults.Ok(new OkDto(true));
});

// -- Persönliche Daten -------------------------------------------------------

api.MapGet("/home", (HttpContext ctx, YouTubeService yt, SessionStore s, string? category, string? page, CancellationToken ct) =>
    Authed(ctx, s, a => yt.HomeFeedAsync(a.AccessToken, a.UserKey, category, page, ct)));

api.MapGet("/likes", (HttpContext ctx, YouTubeService yt, SessionStore s, string? page, CancellationToken ct) =>
    Authed(ctx, s, a => yt.LikesFeedAsync(a.AccessToken, a.UserKey, page, ct)));

api.MapGet("/subscriptions", (HttpContext ctx, YouTubeService yt, SessionStore s, CancellationToken ct) =>
    Authed(ctx, s, a => yt.SubscriptionsAsync(a.AccessToken, a.UserKey, ct)));

api.MapGet("/subscriptions/feed", (HttpContext ctx, YouTubeService yt, SessionStore s, string? page, CancellationToken ct) =>
    Authed(ctx, s, a => yt.SubscriptionFeedAsync(a.AccessToken, a.UserKey, page, ct)));

api.MapGet("/subscriptions/state/{cid}", (HttpContext ctx, YouTubeService yt, SessionStore s, string cid, CancellationToken ct) =>
    Authed(ctx, s, a => yt.SubscriptionStateAsync(a.AccessToken, cid, ct)));

api.MapPost("/subscriptions/{cid}", (HttpContext ctx, YouTubeService yt, SessionStore s, string cid, CancellationToken ct) =>
    Authed(ctx, s, async a =>
    {
        var state = await yt.SubscribeAsync(a.AccessToken, cid, ct);
        s.Invalidate(a.UserKey, "subs", "subfeed", "subitems", "home", YouTubeService.FreshTag);
        return state;
    }));

api.MapDelete("/subscriptions/{cid}", (HttpContext ctx, YouTubeService yt, SessionStore s, string cid, CancellationToken ct) =>
    Authed(ctx, s, async a =>
    {
        var state = await yt.UnsubscribeAsync(a.AccessToken, cid, ct);
        s.Invalidate(a.UserKey, "subs", "subfeed", "subitems", "home", YouTubeService.FreshTag);
        return state;
    }));

api.MapGet("/rating/{id}", (HttpContext ctx, YouTubeService yt, SessionStore s, string id, CancellationToken ct) =>
    Authed(ctx, s, a => yt.RatingAsync(a.AccessToken, id, ct)));

api.MapPost("/rating/{id}", (HttpContext ctx, YouTubeService yt, SessionStore s, string id, RatingBody body, CancellationToken ct) =>
    Authed(ctx, s, async a =>
    {
        var rating = await yt.RateAsync(a.AccessToken, id, body?.Rating ?? "none", ct);
        // Die Startseite steht auf den Likes — nach einer Bewertung ist sie veraltet.
        s.Invalidate(a.UserKey, "likes", "home", YouTubeService.FreshTag);
        return rating;
    }));

api.MapPost("/comments/{id}", async (HttpContext ctx, YouTubeService yt, SessionStore s, string id, CommentBody body, CancellationToken ct) =>
    Trimmed(body?.Text) is { } text
        ? await Authed(ctx, s, a => yt.AddCommentAsync(a.AccessToken, id, text, a.User.ChannelId, ct))
        : TypedResults.BadRequest(new ErrorDto("Der Kommentar ist leer.")));

// POST legt auf einem Video an, {id} ist dort eine Video-Id. PATCH und DELETE greifen
// dagegen einen bestehenden Kommentar an — dort ist {id} dessen Kommentar-Id.
api.MapPatch("/comments/{id}", async (HttpContext ctx, YouTubeService yt, SessionStore s, string id, CommentBody body, CancellationToken ct) =>
    Trimmed(body?.Text) is { } text
        ? await Authed(ctx, s, a => yt.EditCommentAsync(a.AccessToken, id, text, a.User.ChannelId, ct))
        : TypedResults.BadRequest(new ErrorDto("Der Kommentar ist leer.")));

api.MapDelete("/comments/{id}", async (HttpContext ctx, YouTubeService yt, SessionStore s, string id, CancellationToken ct) =>
    await Authed(ctx, s, async a =>
    {
        await yt.DeleteCommentAsync(a.AccessToken, id, ct);
        return new DeletedDto(id);
    }));

api.MapPost("/comments/{id}/reply", async (HttpContext ctx, YouTubeService yt, SessionStore s, string id, CommentBody body, CancellationToken ct) =>
    (Trimmed(body?.Text), body?.ParentId) switch
    {
        (null, _) => TypedResults.BadRequest(new ErrorDto("Die Antwort ist leer.")),
        (_, null or "") => TypedResults.BadRequest(new ErrorDto("Kommentar fehlt.")),
        var (text, parent) => await Authed(ctx, s, a => yt.AddReplyAsync(a.AccessToken, parent!, text!, a.User.ChannelId, ct)),
    });

// Client-side routing: unknown non-API paths fall through to the shell.
app.MapFallback(Shell);

app.Run();

/// <summary>Turns API/network failures into a JSON error the frontend can render.</summary>
static async Task<IResult> Guarded<T>(Func<Task<T>> work)
{
    try
    {
        return TypedResults.Ok(await work());
    }
    catch (YouTubeException ex)
    {
        return TypedResults.Json(new ErrorDto(ex.Message), AppJson.Default.ErrorDto, statusCode: 502);
    }
    catch (HttpRequestException)
    {
        return TypedResults.Json(new ErrorDto("YouTube ist gerade nicht erreichbar."), AppJson.Default.ErrorDto, statusCode: 504);
    }
    catch (NotSignedInException)
    {
        return TypedResults.Json(new ErrorDto("Nicht angemeldet."), AppJson.Default.ErrorDto, statusCode: 401);
    }
    catch (AuthException ex)
    {
        return TypedResults.Json(new ErrorDto(ex.Message), AppJson.Default.ErrorDto, statusCode: 401);
    }
    catch (TaskCanceledException)
    {
        return TypedResults.Json(new ErrorDto("Zeitüberschreitung bei der Anfrage an YouTube."), AppJson.Default.ErrorDto, statusCode: 504);
    }
}

/// <summary>Wie <see cref="Guarded{T}"/>, verlangt aber eine Anmeldung. Ein 401 von Google
/// bedeutet, dass das Zugriffstoken serverseitig ungültig wurde — dann wird es genau einmal
/// erneuert und die Anfrage wiederholt.</summary>
static async Task<IResult> Authed<T>(HttpContext ctx, SessionStore sessions, Func<AuthContext, Task<T>> work) =>
    await Guarded(async () =>
    {
        var auth = await sessions.AuthAsync(ctx, ctx.RequestAborted)
            ?? throw new NotSignedInException();

        try
        {
            return await work(auth);
        }
        catch (YouTubeException ex) when (ex.Reason is "unauthorized")
        {
            var renewed = await sessions.AuthAsync(ctx, ctx.RequestAborted, force: true)
                ?? throw new NotSignedInException();

            return await work(renewed);
        }
    });

static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

/// <summary>Nach der Anmeldung zurück auf die Route, von der aus sie gestartet wurde —
/// aber nur, wenn sie wie eine App-Route aussieht, damit die Weiterleitung nicht
/// zweckentfremdet werden kann.</summary>
static string Landing(string? route) =>
    route is { Length: > 1 } r && r.StartsWith("#/", StringComparison.Ordinal) && !r.Contains("//", StringComparison.Ordinal)
        ? $"/{r}"
        : "/#/";

static string Failed(string message) => $"/?authError={Uri.EscapeDataString(message)}";

internal sealed record CommentBody(string? Text, string? ParentId);

internal sealed record RatingBody(string? Rating);

