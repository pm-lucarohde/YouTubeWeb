# YouTube — a lightweight front end

A web app on **ASP.NET Core (.NET 10, Native AOT)**. The backend talks to the official
**YouTube Data API v3**; the front end is a custom full-width UI inspired by
[hytale.com](https://hytale.com/), from phone to desktop. Videos play in the official
`youtube-nocookie` IFrame player — embedding `m.youtube.com` is impossible because it
sends `x-frame-options: SAMEORIGIN`.

## Setup

1. **API key (free):** in the [Google Cloud Console](https://console.cloud.google.com/)
   enable **YouTube Data API v3**, create an **API key** and store it with
   `dotnet user-secrets set "YouTube:ApiKey" "…"` in `YtMobile/` (or `YOUTUBE_API_KEY`).
2. **Certificate (once):** `dotnet dev-certs https`. To trust it in Brave/Chrome, run `certutil`
   in a container against `~/.pki/nssdb` with trust **`P,,`** (not `C,,`: the cert is `CA:FALSE`).
3. **Run:** `./run.sh`, then open <https://localhost:5099>. The first run compiles a native
   binary with `podman` (image built once, then ~27 s per rebuild, only when C# changed).
   Without a key it shows setup instructions; HTTP fallback: `ASPNETCORE_URLS=http://0.0.0.0:5099`.
4. **Optional service:** `systemctl --user enable --now ~/Website/ytmobile.service` starts it
   at login and restarts it on crashes (`loginctl enable-linger` for boot without login).
   `run.sh` refuses to start while the service holds the port; `bauen.sh` restarts it.
5. **Optional YouTube account** (subscriptions, comments, ratings): in the same Cloud project
   open **Google Auth Platform**, user type **External**, add the scope
   `https://www.googleapis.com/auth/youtube.force-ssl`, keep the status on **Testing** and
   add yourself as a test user. Create a **Web application** client with these redirect URIs:
   `https://localhost:5099/api/auth/callback`, `http://localhost:5099/api/auth/callback`,
   `http://127.0.0.1:5099/api/auth/callback`. Store `Google:ClientId` and
   `Google:ClientSecret` via `dotnet user-secrets` and restart.

The session lives in an encrypted HttpOnly cookie (`yt_session`) holding only the refresh
token, so it survives restarts. HSTS is deliberately off — on `localhost` it is hard to undo.

## Configuration

`YtMobile/appsettings.json`, section `YouTube`: `ApiKey` (empty), `RegionCode` (`DE`),
`Language` (`de`), `CacheMinutes` (`10`), `ShortsCacheMinutes` (`120`),
`RelatedCacheMinutes` (`180`). Section `Google` (`ClientId`, `ClientSecret`) goes in user-secrets.

## Layout

```
run.sh / bauen.sh        Start the native binary / compile it in a Fedora 44 container
YtMobile/Program.cs      Minimal API endpoints, compression, static files
YtMobile/Youtube/        Data API client (raw JSON, no SDK), labels, DTOs, request bodies
YtMobile/Auth/           OAuth 2.0 with PKCE, cookie-based session store
YtMobile/Json/           Source-generated serialization (AOT has no reflection)
YtMobile/wwwroot/        index.html, css/app.css, js/app.js (hash router, no framework)
```

**Public API:** `/api/config`, `/api/categories`, `/api/feed`, `/api/shorts`, `/api/search?q=`,
`/api/channel/{id}`, `/api/video/{id}`, `/api/related/{id}`, `/api/comments/{id}[/replies]`.
**Signed in only** (else `401`): `/api/auth/*`, `/api/home`, `/api/likes`, `/api/rating/{id}`,
`/api/subscriptions[/feed]`, and `POST`/`PATCH`/`DELETE` on comments and subscriptions.

## Highlights

- **Personalized home:** mixes channels from your last 200 likes, regional trends and
  subscription uploads at **7 : 2 : 1**, newest 14 days first, max three per channel.
  Category chips filter that same mix at no quota cost.
- **Shorts detection:** `search.list` for `#shorts`, drop anything over 3 minutes, then a
  `HEAD` on `youtube.com/shorts/<id>` (200 = Short). Run in parallel, 11.8× faster.
- **Lightweight:** no framework or build step, ~5 KB JS and ~4 KB CSS (brotli), lazy
  thumbnails, `content-visibility: auto`, server-side label formatting.
- **Player extras:** hover previews via a single reused player, keys `0`–`9` seek to tenths
  via `postMessage`, and Pointer Lock hides the cursor in fullscreen.
- **Native AOT:** ~6× faster startup (43–58 ms vs. ~300 ms) and half the memory (44 vs.
  92 MiB), at the cost of 15–20 % lower sustained throughput — the right trade for one user.

## Limitations

- **Sign-in only on the PC:** Google needs HTTPS or `localhost` redirects, and the dev cert
  covers only `localhost`. On a phone the app works signed out and is not installable.
- **Sign-in expires after 7 days** in Testing status; production needs a verified domain.
- **No watch history, Watch Later or real recommendations** — Google retired those APIs.
- **"Related videos" are approximated** (`relatedToVideoId` is gone): keyword search in the
  video's category, up to three widening steps, falling back to "More from …".
- Non-embeddable videos link to YouTube; Shorts play in the normal player.

## Quota

10,000 units/day. Reads cost 1 unit, writes 50, **`search.list` 100** — and search has its
own daily counter (`429 rateLimitExceeded`) that usually runs out first. Search, the Shorts
row and related videos use it, hence the long caches. A full home refresh costs ~110 units,
the subscription feed about one unit per channel. YouTube's name and logo are Google trademarks.
