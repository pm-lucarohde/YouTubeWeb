using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using YtMobile.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace YtMobile.Youtube;

public sealed class YouTubeOptions
{
    public string ApiKey { get; set; } = "";
    public string RegionCode { get; set; } = "DE";
    public string Language { get; set; } = "de";
    /// <summary>How long API responses are cached. Saves quota and makes repeat views instant.</summary>
    public int CacheMinutes { get; set; } = 10;
    /// <summary>Eigene, längere Cache-Dauer für die Shorts-Zeile: Sie basiert auf
    /// search.list und kostet damit 100 statt 1 Kontingenteinheit pro Abruf.</summary>
    public int ShortsCacheMinutes { get; set; } = 120;
    /// <summary>Eigene, längere Cache-Dauer für die Spalte „Ähnliche Videos": Sie beruht
    /// wie die Shorts-Zeile auf search.list und kostet 100 Einheiten je Video.</summary>
    public int RelatedCacheMinutes { get; set; } = 180;
    /// <summary>Eigene, längere Cache-Dauer für den Abo-Feed. Er kostet eine
    /// Kontingenteinheit je abonniertem Kanal — bei 604 Abos also 604 je Neuaufbau. Alle
    /// zehn Minuten wäre das Tageskontingent nach knapp drei Stunden weg.</summary>
    public int SubscriptionCacheMinutes { get; set; } = 15;
    /// <summary>Eigene, längere Cache-Dauer für die Suche. Sie beruht wie Shorts-Zeile und
    /// „Ähnliche Videos" auf search.list und zählt damit gegen das getrennte, deutlich
    /// engere Tageslimit für Suchanfragen. Zehn Minuten sind dafür zu kurz: Dieselbe
    /// Suche kurz darauf noch einmal einzutippen kostete sonst erneut eine Anfrage,
    /// obwohl sich an den Treffern in drei Stunden praktisch nichts ändert.</summary>
    public int SearchCacheMinutes { get; set; } = 180;
}

/// <summary><paramref name="reason"/> carries the raw API reason code (e.g.
/// "commentsDisabled") so callers can react to specific failures.</summary>
public sealed class YouTubeException(string message, string? reason = null) : Exception(message)
{
    public string? Reason { get; } = reason;
}

/// <summary>
/// Thin wrapper over the YouTube Data API v3. Uses raw JSON instead of the
/// Google.Apis client so the app stays small and starts fast.
/// </summary>
public sealed class YouTubeService(
    HttpClient http, IMemoryCache cache, IOptions<YouTubeOptions> options, ILogger<YouTubeService> log)
{
    private const string Base = "https://www.googleapis.com/youtube/v3/";
    private readonly YouTubeOptions _opt = options.Value;

    /// <summary>Misst einen Abschnitt und schreibt ihn ins Protokoll. Die Startseite baut
    /// sich aus mehreren Stufen zusammen; ohne diese Marken lässt sich nicht sagen, welche
    /// davon die Wartezeit verursacht.</summary>
    private async Task<T> TimedAsync<T>(string label, Func<Task<T>> work)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await work();
        }
        finally
        {
            log.LogInformation("⏱  {Label}: {Ms} ms", label, (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opt.ApiKey);

    public async Task<FeedDto> TrendingAsync(string? categoryId, string? pageToken, CancellationToken ct)
    {
        string ChartUrl(string? token) => Url("videos",
            ("part", "snippet,contentDetails,statistics,status"),
            ("chart", "mostPopular"),
            ("regionCode", _opt.RegionCode),
            ("hl", _opt.Language),
            ("maxResults", "24"),
            ("videoCategoryId", categoryId),
            ("pageToken", token));

        // Auch die Trend-Charts führen Shorts — „Film & Animation" besteht zeitweise fast
        // nur daraus. Sie gehören in ihre eigene Zeile, nicht ins Raster. Bleibt danach
        // kaum ein Video übrig, wird weitergeblättert: eine Chart-Seite kostet 1 Einheit.
        var videos = new List<VideoDto>();
        var shorts = new List<VideoDto>();
        var next = pageToken;

        for (var page = 0; page < TrendingPageDepth; page++)
        {
            using var doc = await GetAsync(ChartUrl(next), ct);
            var raw = ReadVideoList(doc.RootElement);
            next = raw.NextPageToken;

            // Die Kanal-Feeds der Chart-Videos verraten kostenlos, welche davon Shorts
            // sind — das erspart der Prüfung unten fast jede einzelne Anfrage.
            await WarmShortsAsync(raw.Items, ct);

            var (found, aside) = await SplitShortsAsync(raw.Items, ct);
            videos.AddRange(found);
            shorts.AddRange(aside);

            // Nur weiterblättern, wenn eine Seite gar nichts fürs Raster hergab — sonst
            // holt der Browser die nächste Seite selbst, und die Antwort bleibt schnell.
            if (videos.Count > 0 || next is null) break;
        }

        return new FeedDto(videos, next, shorts);
    }


    public async Task<FeedDto> SearchAsync(string query, string? pageToken, CancellationToken ct)
    {
        var searchUrl = Url("search",
            ("part", "snippet"),
            ("q", query),
            ("type", "video"),
            ("regionCode", _opt.RegionCode),
            ("relevanceLanguage", _opt.Language),
            ("maxResults", "24"),
            ("pageToken", pageToken));

        using var searchDoc = await GetAsync(searchUrl, ct, TimeSpan.FromMinutes(_opt.SearchCacheMinutes));
        var root = searchDoc.RootElement;

        var ids = root.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").TryGetProperty("videoId", out var v) ? v.GetString() : null)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToArray();

        var next = root.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null;
        if (ids.Length == 0) return new FeedDto([], next);

        // search.list carries no duration or view count, so hydrate via videos.list.
        var items = await HydrateAsync(ids!, ct);

        // Wie überall sonst: Shorts gehören in ihre eigene Zeile, nicht zwischen die
        // liegenden Karten. Anders als bei den Trends wird hier aber nicht
        // weitergeblättert, falls eine Seite fast nur Shorts hergibt — eine Suchseite
        // kostet 100 Einheiten und zählt zusätzlich gegen das Tageslimit für Suchen.
        await WarmShortsAsync(items, ct);
        var (videos, shorts) = await SplitShortsAsync(items, ct);

        return new FeedDto(videos, next, shorts);
    }

    /// <summary>
    /// Neueste Videos eines Kanals — über dessen Uploads-Playlist (Kanal-ID mit „UU"
    /// statt „UC") statt über search.list: **1 Kontingenteinheit statt 100**, und die
    /// Antwort fasst 50 statt 24 Einträge. Shorts reisen getrennt mit, wie bei Startseite
    /// und Abo-Feed, damit sie nicht im Raster landen. Bringt eine Seite ausschließlich
    /// Shorts — bei manchen Kanälen der Normalfall —, wird weitergeblättert, statt ein
    /// leeres Raster zu zeigen; bei einer Einheit je Seite ist das bezahlbar.
    /// </summary>
    public async Task<FeedDto> ChannelVideosAsync(string channelId, string? pageToken, CancellationToken ct, bool withShorts = false)
    {
        if (!channelId.StartsWith("UC", StringComparison.Ordinal))
            return new FeedDto([], null);

        var videos = new List<VideoDto>();
        var shorts = new List<VideoDto>();
        var next = pageToken;

        for (var page = 0; page < ChannelPageDepth; page++)
        {
            string[] ids;
            try
            {
                using var doc = await GetAsync(UploadsUrl(channelId, next), ct);
                var root = doc.RootElement;

                ids = root.GetProperty("items").EnumerateArray()
                    .Select(i => i.TryGetProperty("contentDetails", out var cd)
                        && cd.TryGetProperty("videoId", out var v) ? v.GetString() : null)
                    .Where(id => !string.IsNullOrEmpty(id))
                    .ToArray()!;

                next = root.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null;
            }
            catch (YouTubeException ex) when (ex.Reason is "playlistNotFound" or "notFound")
            {
                // Ein Kanal ohne öffentliche Uploads hat gar keine solche Playlist.
                return new FeedDto([], null);
            }

            if (ids.Length > 0)
            {
                var hydrated = await HydrateAsync(ids, ct);

                // Ein einziger Feed klärt die neuesten 15 Uploads dieses Kanals.
                await WarmShortsAsync([channelId], ct);

                if (withShorts)
                {
                    // Neben einem laufenden Short bleibt alles in der ursprünglichen
                    // Reihenfolge stehen, Shorts eingeschlossen.
                    videos.AddRange(hydrated);
                }
                else
                {
                    var (found, aside) = await SplitShortsAsync(hydrated, ct);
                    videos.AddRange(found);
                    shorts.AddRange(aside);
                }
            }

            if (videos.Count > 0 || next is null) break;
        }

        // Die Shorts jeder Seite reisen mit: Die Zeilen im Browser füllen sich daraus
        // nach, statt dass die erste Antwort auf alles warten müsste.
        return new FeedDto(videos, next, shorts);
    }

    /// <summary>25 statt 50 je Seite: Der Kanal-Feed klärt die neuesten 15 Videos
    /// kostenlos, alles darüber hinaus braucht eine einzelne HEAD-Prüfung. Eine kleinere
    /// Seite verschiebt diese Kosten dorthin, wo der Nutzer tatsächlich weiterblättert.</summary>
    private string UploadsUrl(string channelId, string? pageToken) =>
        Url("playlistItems",
            ("part", "contentDetails"),
            ("playlistId", $"UU{channelId[2..]}"),
            ("maxResults", "25"),
            ("pageToken", pageToken));

    /// <summary>
    /// Ähnliche Videos für die Seitenspalte der Wiedergabe. Google hat
    /// <c>relatedToVideoId</c> aus search.list entfernt — der Parameter beantwortet jeden
    /// Aufruf mit 400 —, eine echte Empfehlungsliste gibt es also nicht mehr. Die
    /// Annäherung: mit den Stichwörtern des Videos, ersatzweise seinem Titel, innerhalb
    /// seiner eigenen Kategorie suchen. Das Video selbst fällt raus, und höchstens
    /// <see cref="SameChannelLimit"/> Treffer dürfen vom selben Kanal stammen — sonst
    /// wäre die Spalte doch wieder nur die Kanalliste.
    /// </summary>
    public async Task<FeedDto> RelatedAsync(string videoId, CancellationToken ct)
    {
        var key = $"related:{videoId}:{_opt.RegionCode}:{_opt.Language}";
        if (cache.TryGetValue(key, out FeedDto? hit) && hit is not null) return hit;

        var ttl = TimeSpan.FromMinutes(_opt.RelatedCacheMinutes);

        string channelId, categoryId, language;
        List<string> queries;
        bool seedIsShort;
        using (var seedDoc = await GetAsync(Url("videos", ("part", "snippet,contentDetails"), ("id", videoId)), ct, ttl))
        {
            var items = seedDoc.RootElement.GetProperty("items");
            if (items.GetArrayLength() == 0) return new FeedDto([], null);

            // Wer einen Short schaut, will danach wieder Shorts sehen — dann bleiben sie
            // in der Spalte. Sonst fliegen sie raus.
            seedIsShort = ReadVideo(items[0]) is { } self
                && await IsShortAsync(self, ct);

            var seed = items[0].GetProperty("snippet");

            // Stichwörter beschreiben das Thema treffender als der Titel, viele Kanäle
            // vergeben aber gar keine mehr — dann bleibt nur der Titel.
            var tags = seed.TryGetProperty("tags", out var t)
                ? t.EnumerateArray()
                    .Select(x => x.GetString() ?? "")
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray()
                : [];

            var title = seed.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
            queries = tags.Length > 0 ? TagQueries(tags) : TitleQueries(title);

            if (queries.Count == 0) return new FeedDto([], null);

            channelId = seed.TryGetProperty("channelId", out var cid) ? cid.GetString() ?? "" : "";
            categoryId = seed.TryGetProperty("categoryId", out var cat) ? cat.GetString() ?? "" : "";

            // relevanceLanguage muss zum Video passen, nicht zur Oberfläche: „de" auf ein
            // englisches Video gelegt spült Übersetzungen und Reaktionen nach oben.
            language = (seed.TryGetProperty("defaultAudioLanguage", out var dal) ? dal.GetString() : null)
                ?? (seed.TryGetProperty("defaultLanguage", out var dl) ? dl.GetString() : null)
                ?? _opt.Language;
            language = language.Split('-')[0];
        }

        var attempts = Ladder(queries, categoryId);

        IReadOnlyList<VideoDto> best = [];
        foreach (var (q, cat) in attempts)
        {
            var found = await RelatedPageAsync(q, cat, language, videoId, channelId, seedIsShort, ct, ttl);
            if (found.Count > best.Count) best = found;
            if (best.Count >= RelatedMinimum) break;
        }

        // Manche Titel sind Wortschöpfungen des Kanals („Dürrecheck"), auf die keine Suche
        // etwas findet. Statt einer Spalte mit einem Treffer dann die neuesten Videos des
        // Kanals dazu — die Kanaldeckelung gilt dafür bewusst nicht. Kostet 1 Einheit.
        if (best.Count < RelatedMinimum && channelId.Length > 0)
        {
            try
            {
                var own = await ChannelVideosAsync(channelId, null, ct);
                var seen = best.Select(v => v.Id).Append(videoId).ToHashSet(StringComparer.Ordinal);

                best = [.. best, .. own.Items.Where(v => seen.Add(v.Id)).Take(RelatedSize - best.Count)];
            }
            catch (YouTubeException)
            {
                // Kein Kanalarchiv erreichbar: Dann bleibt es bei den wenigen Treffern.
            }
        }

        var feed = new FeedDto(best, null);
        cache.Set(key, feed, ttl);
        return feed;
    }

    /// <summary>Eine Suchstufe: suchen, anreichern, Shorts und Kanaldominanz filtern.</summary>
    private async Task<IReadOnlyList<VideoDto>> RelatedPageAsync(
        string query, string categoryId, string language, string videoId, string channelId,
        bool seedIsShort, CancellationToken ct, TimeSpan ttl)
    {
        var ids = await RelatedIdsAsync(query, categoryId, language, videoId, ct, ttl);
        if (ids.Length == 0) return [];

        var hydrated = await HydrateAsync(ids, ct);
        await WarmShortsAsync(hydrated, ct);

        // Neben einem gewöhnlichen Video gehören Shorts nicht in eine Spalte aus
        // liegenden Karten; neben einem Short sind sie genau das Erwartete.
        IReadOnlyList<VideoDto> videos = seedIsShort
            ? hydrated
            : (await SplitShortsAsync(hydrated, ct)).Videos;

        var fromChannel = 0;
        var chosen = new List<VideoDto>(RelatedSize);
        foreach (var v in videos)
        {
            if (v.ChannelId == channelId && ++fromChannel > SameChannelLimit) continue;

            chosen.Add(v);
            if (chosen.Count == RelatedSize) break;
        }

        return chosen;
    }

    /// <summary>
    /// Die Suchstufen von eng nach breit, samt Kategorie. Die letzte lässt zusätzlich die
    /// Kategorie fallen. Gesucht wird nur so weit, bis eine Stufe genug hergibt — im
    /// Normalfall bleibt es bei der ersten.
    /// </summary>
    private static List<(string Query, string Category)> Ladder(List<string> queries, string categoryId)
    {
        var ladder = queries
            .Take(RelatedMaxSearches)
            .Select(q => (Query: q, Category: categoryId))
            .ToList();

        // Die breiteste Stufe darf auch die Kategorie hinter sich lassen: Manche sind so
        // eng besetzt, dass in ihnen selbst ein einzelnes Stichwort nichts findet.
        if (categoryId.Length > 0)
        {
            var last = ladder.Count - 1;
            if (ladder.Count < RelatedMaxSearches) ladder.Add((ladder[last].Query, ""));
            else ladder[last] = (ladder[last].Query, "");
        }

        return ladder;
    }

    /// <summary>
    /// Baut die Stichwort-Abfragen, von eng nach breit. Die erste nimmt Stichwörter auf,
    /// bis <see cref="RelatedQueryWords"/> Wörter zusammen sind — mehr machen die Abfrage
    /// nicht treffender, sondern enger: Hängt an drei kurzen Begriffen noch der
    /// Serienname des Kanals, findet die Suche am Ende nur noch das Video selbst. Die
    /// folgenden Stufen lassen die hinteren Stichwörter weg, bis nur noch das erste
    /// übrig ist — halbierend statt einzeln, weil eine Stufe je weggelassenem Stichwort
    /// kaum breiter wäre, aber jedes Mal eine volle Suche kostet.
    /// </summary>
    private static List<string> TagQueries(IReadOnlyList<string> tags)
    {
        var capped = new List<string>();
        var words = 0;

        foreach (var tag in tags)
        {
            var length = tag.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (capped.Count > 0 && words + length > RelatedQueryWords) break;

            capped.Add(tag);
            words += length;
        }

        int[] steps = [capped.Count, Math.Max(1, capped.Count / 2), 1];

        return [.. steps.Distinct().Take(RelatedTagSteps).Select(n => string.Join(' ', capped.Take(n)))];
    }

    /// <summary>
    /// Dasselbe für Videos ohne Stichwörter — und das sind viele. Der Titel wird dabei
    /// nicht wortweise gekürzt, das ergäbe Abfragen wie „Die europäische": Stattdessen
    /// zählt zuerst der Teil vor dem ersten Trennzeichen, denn danach steht meist der
    /// Serien- oder Kanalzusatz („Dürrecheck — Hätte auch noch schlimmer sein können").
    /// Erst wenn der zu wenig hergibt, kommt der ganze Titel dran.
    /// </summary>
    private static List<string> TitleQueries(string title)
    {
        static string Cap(string text)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(' ', words.Take(RelatedQueryWords));
        }

        if (string.IsNullOrWhiteSpace(title)) return [];

        var lead = title.Split([" - ", " – ", " — ", " | ", ": "], StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        List<string> queries = [Cap(lead.Length > 0 ? lead : title)];

        var whole = Cap(title);
        if (!string.Equals(whole, queries[0], StringComparison.Ordinal)) queries.Add(whole);

        return queries;
    }

    private async Task<string[]> RelatedIdsAsync(
        string query, string categoryId, string language, string exclude, CancellationToken ct, TimeSpan ttl)
    {
        var url = Url("search",
            ("part", "snippet"),
            ("q", query),
            ("type", "video"),
            ("videoCategoryId", categoryId.Length > 0 ? categoryId : null),
            ("regionCode", _opt.RegionCode),
            ("relevanceLanguage", language),
            ("maxResults", "40"));

        using var doc = await GetAsync(url, ct, ttl);

        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("id").TryGetProperty("videoId", out var v) ? v.GetString() : null)
            .Where(id => !string.IsNullOrEmpty(id) && id != exclude)
            .ToArray()!;
    }

    /// <summary>
    /// Populäre Shorts der Region. Die API kennt keinen Shorts-Filter, deshalb drei Stufen:
    /// über #shorts und videoDuration=short suchen, per Länge (max. 3 Minuten) grob sieben
    /// und den Rest gegen youtube.com/shorts/&lt;id&gt; verifizieren. Das Ergebnis wird
    /// komplett gecacht, weil die Suche allein 100 Kontingenteinheiten kostet.
    /// </summary>
    public async Task<FeedDto> ShortsAsync(CancellationToken ct)
    {
        var key = $"shorts:{_opt.RegionCode}:{_opt.Language}:{_opt.ApiKey.GetHashCode()}";
        if (cache.TryGetValue(key, out FeedDto? hit) && hit is not null) return hit;

        // Auf den Tagesbeginn gerundet — mit einem laufenden Zeitstempel wäre die
        // URL bei jeder Anfrage anders und der Payload-Cache liefe ins Leere.
        var since = DateTime.UtcNow.Date.AddDays(-7).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        var searchUrl = Url("search",
            ("part", "snippet"),
            ("q", "#shorts"),
            ("type", "video"),
            ("videoDuration", "short"),
            // Kein order=viewCount: das spült globale Viral-Clips nach oben und
            // ignoriert Region und Sprache praktisch komplett.
            ("publishedAfter", since),
            ("regionCode", _opt.RegionCode),
            ("relevanceLanguage", _opt.Language),
            ("maxResults", "50"));

        string[] ids;
        using (var searchDoc = await GetAsync(searchUrl, ct, TimeSpan.FromMinutes(_opt.ShortsCacheMinutes)))
        {
            ids = searchDoc.RootElement.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("id").TryGetProperty("videoId", out var v) ? v.GetString() : null)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToArray()!;
        }

        var feed = new FeedDto([], null);
        if (ids.Length > 0)
        {
            // Alles über 3 Minuten ist ein normales Video, das nur #shorts im Titel trägt.
            var candidates = (await HydrateAsync(ids, ct))
                .Where(v => v.DurationSeconds is > 0 and <= 180)
                .Take(32)
                .ToArray();

            // Die Kanal-Feeds klären die meisten Kandidaten vorab und kostenlos; übrig
            // bleiben nur Videos, die älter sind als die letzten 15 Uploads ihres Kanals.
            await WarmShortsAsync(candidates, ct);

            var verified = await VerifyShortsAsync(candidates, ct);

            // Sollte YouTube die Weiterleitung einmal ändern, lieber die ungeprüfte
            // Liste zeigen als eine leere Zeile.
            IReadOnlyList<VideoDto> chosen = verified.Count > 0 ? verified : candidates;

            feed = new FeedDto(
                // 28 reicht für vier volle Zeilen à sieben Karten.
                chosen.Take(28).Select(v => v with { Thumb = VerticalThumb(v.Id) }).ToArray(),
                null);
        }

        cache.Set(key, feed, TimeSpan.FromMinutes(_opt.ShortsCacheMinutes));
        return feed;
    }

    /// <summary>frame0.jpg ist das einzige hochkante Standbild in vernünftiger Größe
    /// (~270x480, ~25 KB). oardefault.jpg wäre schärfer, kostet aber 100-280 KB.</summary>
    private static string VerticalThumb(string id) => $"https://i.ytimg.com/vi/{id}/frame0.jpg";

    /// <summary>youtube.com/shorts/&lt;id&gt; antwortet nur bei echten Shorts mit 200,
    /// sonst mit 303 auf /watch. Kostet kein API-Kontingent.</summary>
    /// <summary>
    /// Ein HEAD je Kandidat. Gemessen antwortet ein normales Video in rund 70 ms (303),
    /// ein echter Short erst nach rund 1,7 s (200) — die Kosten wachsen also mit der Zahl
    /// der Shorts, nicht mit der der Kandidaten.
    ///
    /// Die naheliegende Abkürzung über <c>i.ytimg.com/vi/&lt;id&gt;/oardefault.jpg</c> —
    /// das Bild gibt es nur für Videos, die nicht 16:9 sind — wurde gemessen und wieder
    /// verworfen: Für einen Short antwortet es zwar in 50 ms, für alles andere aber erst
    /// nach 900 ms, und der zweite Durchgang für die verbliebenen Unklaren kommt obendrauf.
    /// Über dieselben 24 Videos: 2,4 s einstufig gegen 3,5 s zweistufig; über Videos ohne
    /// Shorts 0,1 s gegen 1,6 s. Auch beide Prüfungen gleichzeitig laufen zu lassen und
    /// die erste eindeutige Antwort zu nehmen, war nicht schneller.
    /// </summary>
    private async Task<List<VideoDto>> VerifyShortsAsync(IReadOnlyList<VideoDto> candidates, CancellationToken ct)
    {
        if (candidates.Count == 0) return [];

        // 64 statt 24 gleichzeitig: gemessen 47 Prüfungen in 2,7 statt 5,0 Sekunden,
        // ohne dass YouTube eine davon abweist.
        using var gate = new SemaphoreSlim(ProbeConcurrency);

        var checks = candidates.Select(async v =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, $"https://www.youtube.com/shorts/{v.Id}");
                // Ohne Consent-Cookie landet jede Anfrage aus der EU auf consent.youtube.com.
                req.Headers.TryAddWithoutValidation("Cookie", "CONSENT=YES+cb; SOCS=CAI");
                req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");

                using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                return res.StatusCode == System.Net.HttpStatusCode.OK ? v : null;
            }
            catch (HttpRequestException)
            {
                // Ein Aussetzer zählt als „kein Short" — lieber im Raster als verschwunden.
                return null;
            }
            finally
            {
                gate.Release();
            }
        });

        return (await Task.WhenAll(checks)).OfType<VideoDto>().ToList();
    }

    public async Task<VideoDetailDto?> VideoAsync(string id, CancellationToken ct)
    {
        var url = Url("videos",
            ("part", "snippet,contentDetails,statistics,status"),
            ("id", id),
            ("hl", _opt.Language));

        using var doc = await GetAsync(url, ct);
        var items = doc.RootElement.GetProperty("items");
        if (items.GetArrayLength() == 0) return null;

        var item = items[0];
        var video = ReadVideo(item);
        if (video is null) return null;

        var snippet = item.GetProperty("snippet");
        var description = snippet.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var likes = ReadCount(item, "likeCount");

        await WarmShortsAsync([video], ct);
        var (_, shorts) = await SplitShortsAsync([video], ct);

        return new VideoDetailDto(video, description, Format.Likes(likes), shorts.Count > 0);
    }

    public async Task<CommentsDto> CommentsAsync(string videoId, string? pageToken, string? viewer, CancellationToken ct)
    {
        var url = Url("commentThreads",
            ("part", "snippet"),
            ("videoId", videoId),
            ("order", "relevance"),
            ("textFormat", "plainText"),
            ("maxResults", "20"),
            ("pageToken", pageToken));

        JsonDocument doc;
        try
        {
            doc = await GetAsync(url, ct);
        }
        catch (YouTubeException ex) when (ex.Reason is "commentsDisabled")
        {
            // Not an error worth surfacing — the video simply has comments turned off.
            return new CommentsDto([], null, Disabled: true);
        }

        using (doc)
        {
            var root = doc.RootElement;

            var items = root.GetProperty("items").EnumerateArray()
                .Select(t => ReadThread(t, viewer))
                .OfType<CommentDto>()
                .ToArray();

            var next = root.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null;
            return new CommentsDto(items, next, Disabled: false);
        }
    }

    /// <summary>Antworten auf einen Kommentar. Sie sind öffentlich lesbar und laufen
    /// deshalb über den API-Key statt über die Anmeldung — eine Kontingenteinheit.</summary>
    public async Task<CommentsDto> RepliesAsync(string parentId, string? pageToken, string? viewer, CancellationToken ct)
    {
        var url = Url("comments",
            ("part", "snippet"),
            ("parentId", parentId),
            ("textFormat", "plainText"),
            ("maxResults", "50"),
            ("pageToken", pageToken));

        using var doc = await GetAsync(url, ct);
        var root = doc.RootElement;

        var items = root.GetProperty("items").EnumerateArray()
            .Select(c => ReadComment(c, 0, viewer))
            .OfType<CommentDto>()
            .ToArray();

        var next = root.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null;
        return new CommentsDto(items, next, Disabled: false);
    }

    private static CommentDto? ReadThread(JsonElement thread, string? viewer)
    {
        if (!thread.TryGetProperty("snippet", out var threadSnippet)) return null;
        if (!threadSnippet.TryGetProperty("topLevelComment", out var top)) return null;

        var replies = threadSnippet.TryGetProperty("totalReplyCount", out var rc) ? rc.GetInt32() : 0;
        return ReadComment(top, replies, viewer);
    }

    /// <summary>Bildet eine einzelne Comment-Ressource ab. Dieselbe Form steckt in
    /// commentThreads, in comments.list und in den Antworten der Insert-Aufrufe.</summary>
    private static CommentDto? ReadComment(JsonElement comment, int replyCount, string? viewer)
    {
        if (!comment.TryGetProperty("snippet", out var s)) return null;

        var published = s.TryGetProperty("publishedAt", out var pa)
            && pa.TryGetDateTimeOffset(out var when)
                ? when
                : DateTimeOffset.UtcNow;

        var likes = s.TryGetProperty("likeCount", out var lc) && lc.TryGetUInt64(out var l) && l > 0
            ? Format.Compact(l)
            : null;

        var authorId = s.TryGetProperty("authorChannelId", out var ac)
            && ac.TryGetProperty("value", out var av)
                ? av.GetString()
                : null;

        // textOriginal liefert Google nur dem Verfasser selbst; für alle anderen ist
        // textDisplay dank textFormat=plainText bereits reiner Text.
        var text = s.TryGetProperty("textOriginal", out var to) && to.GetString() is { Length: > 0 } original
            ? original
            : s.TryGetProperty("textDisplay", out var td) ? td.GetString() ?? "" : "";

        return new CommentDto(
            Id: comment.TryGetProperty("id", out var cid) ? cid.GetString() ?? "" : "",
            Author: s.TryGetProperty("authorDisplayName", out var a) ? a.GetString() ?? "" : "",
            AuthorChannelId: authorId,
            AuthorThumb: s.TryGetProperty("authorProfileImageUrl", out var t) ? t.GetString() : null,
            Text: text,
            PublishedLabel: Format.Age(published),
            LikesLabel: likes,
            ReplyCount: replyCount,
            IsOwn: viewer is not null && authorId == viewer);
    }

    public async Task<IReadOnlyList<CategoryDto>> CategoriesAsync(CancellationToken ct)
    {
        var url = Url("videoCategories",
            ("part", "snippet"),
            ("regionCode", _opt.RegionCode),
            ("hl", _opt.Language));

        using var doc = await GetAsync(url, ct);

        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("snippet").TryGetProperty("assignable", out var a) && a.GetBoolean())
            .Select(i => new CategoryDto(
                i.GetProperty("id").GetString()!,
                i.GetProperty("snippet").GetProperty("title").GetString()!))
            .ToArray();
    }

    // -- Angemeldete Aufrufe -----------------------------------------------

    /// <summary>Kanal des angemeldeten Benutzers. Wird einmal direkt nach der Anmeldung
    /// geholt, damit Name und Bild ins Sitzungs-Cookie wandern können.</summary>
    public async Task<ChannelRefDto?> MeAsync(string token, CancellationToken ct)
    {
        using var doc = await GetAuthAsync(AuthUrl("channels", ("part", "snippet"), ("mine", "true")), token, ct);

        var items = doc.RootElement.GetProperty("items");
        if (items.GetArrayLength() == 0) return null;

        var item = items[0];
        var snippet = item.GetProperty("snippet");

        return new ChannelRefDto(
            item.GetProperty("id").GetString() ?? "",
            snippet.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            ThumbUrl(snippet));
    }

    /// <summary>Abonnierte Kanäle, deren Name die Anfrage enthält — der Notnagel der
    /// Suche. Die Abo-Liste liegt ohnehin im Cache, das kostet also nichts und
    /// funktioniert auch dann noch, wenn das getrennte Tageslimit für Suchanfragen
    /// aufgebraucht ist. Beginnt ein Name mit der Anfrage, steht er vorn: Wer „IGN"
    /// tippt, meint eher den Kanal IGN als „Design Insider".</summary>
    public async Task<IReadOnlyList<ChannelRefDto>> SubscribedChannelsAsync(
        string token, string userKey, string query, CancellationToken ct)
    {
        const StringComparison Wie = StringComparison.InvariantCultureIgnoreCase;

        var subs = await SubscriptionsAsync(token, userKey, ct);

        return
        [
            .. subs.Where(c => c.Title.Contains(query, Wie))
                   .OrderByDescending(c => c.Title.StartsWith(query, Wie))
                   .ThenBy(c => c.Title.Length)
                   .Take(12)
        ];
    }

    public async Task<IReadOnlyList<ChannelRefDto>> SubscriptionsAsync(string token, string userKey, CancellationToken ct)
    {
        var key = $"{userKey}|subs";
        if (cache.TryGetValue(key, out ChannelRefDto[]? cached) && cached is not null) return cached;

        var all = new List<ChannelRefDto>();
        string? page = null;

        do
        {
            var url = AuthUrl("subscriptions",
                ("part", "snippet"),
                ("mine", "true"),
                ("maxResults", "50"),
                ("order", "alphabetical"),
                ("pageToken", page));

            using var doc = await GetAuthAsync(url, token, ct);
            var root = doc.RootElement;

            foreach (var item in root.GetProperty("items").EnumerateArray())
            {
                if (!item.TryGetProperty("snippet", out var s)) continue;

                var id = s.TryGetProperty("resourceId", out var r) && r.TryGetProperty("channelId", out var c)
                    ? c.GetString()
                    : null;
                if (string.IsNullOrEmpty(id)) continue;

                all.Add(new ChannelRefDto(
                    id,
                    s.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    ThumbUrl(s)));
            }

            page = root.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null;
        }
        while (!string.IsNullOrEmpty(page) && all.Count < 1000);

        var result = all.ToArray();
        cache.Set(key, result, TimeSpan.FromMinutes(_opt.CacheMinutes));
        return result;
    }

    /// <summary>Abo-Feed. Google hat activities.list?home abgeschafft, deshalb mischt die
    /// App die Uploads der abonnierten Kanäle selbst zusammen.</summary>
    public async Task<FeedDto> SubscriptionFeedAsync(string token, string userKey, string? pageToken, CancellationToken ct)
    {
        var key = $"{userKey}|subitems";
        if (!cache.TryGetValue(key, out FeedPage? page) || page is null)
        {
            var ids = (await FeedIdsAsync(token, userKey, ct)).Select(d => d.Id).Take(FeedDepth).ToArray();
            page = await BuildPageAsync(ids, ct);
            cache.Set(key, page, TimeSpan.FromMinutes(_opt.SubscriptionCacheMinutes));
        }

        return await SliceAsync(page, pageToken, ct);
    }

    /// <summary><paramref name="limit"/> beschränkt die Zahl der abgefragten Kanäle. Die
    /// Startseite nutzt das: Abos steuern dort 10 % bei, für die sich 604 Kanal-Feeds
    /// nicht lohnen. Der Abo-Tab fragt ohne Grenze und bleibt vollständig.</summary>
    private async Task<IReadOnlyList<Dated>> FeedIdsAsync(
        string token, string userKey, CancellationToken ct, int? limit = null)
    {
        var key = limit is { } n ? $"{userKey}|subfeed:{n}" : $"{userKey}|subfeed";
        if (cache.TryGetValue(key, out Dated[]? cached) && cached is not null) return cached;

        var channels = await SubscriptionsAsync(token, userKey, ct);
        if (limit is { } take && channels.Count > take) channels = [.. channels.Take(take)];
        var found = new System.Collections.Concurrent.ConcurrentBag<Dated>();

        // Ein Aufruf pro Kanal, aber je nur eine Kontingenteinheit. Der naheliegende Weg
        // über search.list?channelId= würde 100 Einheiten pro Kanal kosten.
        using var gate = new SemaphoreSlim(UploadConcurrency);
        await Task.WhenAll(channels.Select(async c =>
        {
            await gate.WaitAsync(ct);
            try
            {
                foreach (var upload in await UploadsAsync(c.Id, ct)) found.Add(upload);
            }
            catch (YouTubeException)
            {
                // Ein einzelner Kanal ohne sichtbare Uploads darf den ganzen Feed nicht kippen.
            }
            finally
            {
                gate.Release();
            }
        }));

        var ids = found.OrderByDescending(v => v.At).DistinctBy(v => v.Id).ToArray();
        cache.Set(key, ids, TimeSpan.FromMinutes(_opt.SubscriptionCacheMinutes));
        return ids;
    }

    /// <summary>
    /// Die neuesten Uploads eines Kanals. Erste Wahl ist der Atom-Feed unter
    /// <c>youtube.com/feeds/videos.xml</c>: Er kostet **kein Kontingent** — bei 604 Abos
    /// also 0 statt 604 Einheiten je Aktualisierung — und verrät nebenbei, welche der
    /// Videos Shorts sind. Nur wenn er nichts hergibt, wird auf die Uploads-Playlist
    /// zurückgefallen, die eine Einheit je Kanal kostet.
    /// </summary>
    private async Task<IReadOnlyList<Dated>> UploadsAsync(string channelId, CancellationToken ct)
    {
        var rss = await RssUploadsAsync(channelId, ct);
        return rss.Count > 0 ? rss : await PlaylistUploadsAsync(channelId, ct);
    }

    /// <summary>
    /// Liest den Atom-Feed eines Kanals: die letzten 15 Uploads mit Datum, dazu das
    /// Shorts-Urteil. Das steckt im <c>link rel="alternate"</c> — bei einem Short zeigt
    /// er auf <c>/shorts/…</c>, sonst auf <c>/watch?v=…</c>. Gegen die einzelne
    /// HEAD-Prüfung gemessen: 48 von 48 Videos identisch beurteilt.
    /// </summary>
    private async Task<IReadOnlyList<Dated>> RssUploadsAsync(string channelId, CancellationToken ct)
    {
        if (!channelId.StartsWith("UC", StringComparison.Ordinal)) return [];

        var url = $"https://www.youtube.com/feeds/videos.xml?channel_id={Uri.EscapeDataString(channelId)}";
        if (!cache.TryGetValue(url, out string? xml) || xml is null)
        {
            try
            {
                using var res = await http.GetAsync(url, ct);
                if (!res.IsSuccessStatusCode) return [];

                xml = await res.Content.ReadAsStringAsync(ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                return [];
            }

            cache.Set(url, xml, TimeSpan.FromMinutes(_opt.SubscriptionCacheMinutes));
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (XmlException)
        {
            return [];
        }

        XNamespace atom = "http://www.w3.org/2005/Atom";
        XNamespace yt = "http://www.youtube.com/xml/schemas/2015";

        var result = new List<Dated>();
        foreach (var entry in doc.Descendants(atom + "entry"))
        {
            if (entry.Element(yt + "videoId")?.Value is not { Length: > 0 } id) continue;

            var at = DateTimeOffset.TryParse(entry.Element(atom + "published")?.Value, out var when)
                ? when
                : DateTimeOffset.MinValue;

            var href = entry.Elements(atom + "link")
                .FirstOrDefault(l => (string?)l.Attribute("rel") == "alternate")?
                .Attribute("href")?.Value;

            // Ohne link-Element bleibt das Urteil offen statt falsch.
            bool? isShort = href is null ? null : href.Contains("/shorts/", StringComparison.Ordinal);

            result.Add(new Dated(id, at, channelId, isShort));
        }

        Remember(result);
        return result;
    }

    /// <summary>Einzelnes Video: erst den Kanal-Feed fragen, dann erst die HEAD-Prüfung.</summary>
    private async Task<bool> IsShortAsync(VideoDto video, CancellationToken ct)
    {
        await WarmShortsAsync([video], ct);
        return (await SplitShortsAsync([video], ct)).Shorts.Count > 0;
    }

    /// <summary>Legt die aus dem RSS bekannten Shorts-Urteile in denselben Cache, den die
    /// HEAD-Prüfung benutzt. <see cref="SplitShortsAsync"/> findet sie dort vor und stellt
    /// für diese Videos gar keine Anfrage mehr.</summary>
    private void Remember(IEnumerable<Dated> uploads)
    {
        foreach (var upload in uploads)
        {
            if (upload.IsShort is { } verdict)
                cache.Set(ShortKey(upload.Id), verdict, TimeSpan.FromHours(24));
        }
    }

    /// <summary>Holt für eine Menge Videos die Kanal-Feeds und legt die Shorts-Urteile ab.
    /// Für Trend-Videos lohnt sich das: 24 Feeds kosten 0,2 Sekunden und kein Kontingent,
    /// während 24 einzelne HEAD-Prüfungen mehrere Sekunden brauchen.</summary>
    /// <summary>Wärmt nur für die Videos auf, die überhaupt Shorts sein könnten und noch
    /// kein Urteil haben. Ein Kanal, von dem hier nur ein Zwanzigminüter steht, braucht
    /// seinen Feed nicht.</summary>
    private Task WarmShortsAsync(IEnumerable<VideoDto> videos, CancellationToken ct) =>
        WarmShortsAsync(
            videos.Where(v => CouldBeShort(v) && !IsJudged(v.Id)).Select(v => v.ChannelId), ct);

    private async Task WarmShortsAsync(IEnumerable<string> channelIds, CancellationToken ct)
    {
        var channels = channelIds.Where(c => !string.IsNullOrEmpty(c)).Distinct(StringComparer.Ordinal).ToArray();
        if (channels.Length == 0) return;

        using var gate = new SemaphoreSlim(UploadConcurrency);

        await Task.WhenAll(channels.Select(async id =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await RssUploadsAsync(id, ct);
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    private async Task<IReadOnlyList<Dated>> PlaylistUploadsAsync(string channelId, CancellationToken ct)
    {
        // Die Uploads-Playlist eines Kanals ist dessen ID mit "UU" statt "UC" — das spart
        // den Umweg über channels.list und damit einen Aufruf pro Kanal.
        if (!channelId.StartsWith("UC", StringComparison.Ordinal)) return [];

        var url = Url("playlistItems",
            ("part", "contentDetails"),
            ("playlistId", $"UU{channelId[2..]}"),
            ("maxResults", "5"));

        using var doc = await GetAsync(url, ct);

        var result = new List<Dated>();
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            if (!item.TryGetProperty("contentDetails", out var cd)) continue;
            if (!cd.TryGetProperty("videoId", out var v) || v.GetString() is not { Length: > 0 } id) continue;

            var at = cd.TryGetProperty("videoPublishedAt", out var p) && p.TryGetDateTimeOffset(out var when)
                ? when
                : DateTimeOffset.MinValue;

            result.Add(new Dated(id, at, channelId));
        }

        return result;
    }

    public async Task<SubStateDto> SubscriptionStateAsync(string token, string channelId, CancellationToken ct)
    {
        var url = AuthUrl("subscriptions", ("part", "id"), ("mine", "true"), ("forChannelId", channelId));

        using var doc = await GetAuthAsync(url, token, ct);
        var items = doc.RootElement.GetProperty("items");

        return items.GetArrayLength() > 0
            ? new SubStateDto(true, items[0].GetProperty("id").GetString())
            : new SubStateDto(false, null);
    }

    public async Task<SubStateDto> SubscribeAsync(string token, string channelId, CancellationToken ct)
    {
        var body = new SubscribeBody(new SubscribeSnippet(
            new ResourceIdBody("youtube#channel", channelId)));

        using var doc = await SendAuthAsync(HttpMethod.Post, AuthUrl("subscriptions", ("part", "snippet")),
            token, body, AppJson.Default.SubscribeBody, ct);
        return new SubStateDto(true, doc?.RootElement.GetProperty("id").GetString());
    }

    public async Task<SubStateDto> UnsubscribeAsync(string token, string channelId, CancellationToken ct)
    {
        // subscriptions.delete kennt nur die ID des Abos, nicht die des Kanals.
        var state = await SubscriptionStateAsync(token, channelId, ct);
        if (state.SubscriptionId is { } id)
            (await SendAuthAsync(HttpMethod.Delete, AuthUrl("subscriptions", ("id", id)), token, null, ct))?.Dispose();

        return new SubStateDto(false, null);
    }

    public async Task<RatingDto> RatingAsync(string token, string videoId, CancellationToken ct)
    {
        using var doc = await GetAuthAsync(AuthUrl("videos/getRating", ("id", videoId)), token, ct);
        var items = doc.RootElement.GetProperty("items");

        return new RatingDto(items.GetArrayLength() > 0 && items[0].TryGetProperty("rating", out var r)
            ? r.GetString() ?? "none"
            : "none");
    }

    public async Task<RatingDto> RateAsync(string token, string videoId, string rating, CancellationToken ct)
    {
        if (rating is not ("like" or "dislike" or "none"))
            throw new YouTubeException("Ungültige Bewertung.");

        var url = AuthUrl("videos/rate", ("id", videoId), ("rating", rating));
        (await SendAuthAsync(HttpMethod.Post, url, token, null, ct))?.Dispose();

        return new RatingDto(rating);
    }

    public async Task<CommentDto> AddCommentAsync(string token, string videoId, string text, string? viewer, CancellationToken ct)
    {
        var body = new CommentThreadBody(new CommentThreadSnippet(
            videoId, new TopLevelComment(new TextSnippet(text))));

        using var doc = await SendAuthAsync(HttpMethod.Post, AuthUrl("commentThreads", ("part", "snippet")),
            token, body, AppJson.Default.CommentThreadBody, ct);

        return doc is not null && ReadThread(doc.RootElement, viewer) is { } comment
            ? comment
            : throw new YouTubeException("Der Kommentar wurde gesendet, kam aber nicht lesbar zurück.");
    }

    public async Task<CommentDto> AddReplyAsync(string token, string parentId, string text, string? viewer, CancellationToken ct)
    {
        var body = new ReplyBody(new ReplySnippet(parentId, text));

        using var doc = await SendAuthAsync(HttpMethod.Post, AuthUrl("comments", ("part", "snippet")),
            token, body, AppJson.Default.ReplyBody, ct);

        return doc is not null && ReadComment(doc.RootElement, 0, viewer) is { } comment
            ? comment
            : throw new YouTubeException("Die Antwort wurde gesendet, kam aber nicht lesbar zurück.");
    }

    /// <summary>Ändert den Text eines eigenen Kommentars. Antworten und Kommentare der
    /// obersten Ebene sind dieselbe Comment-Ressource, der Aufruf gilt also für beide.
    /// Google lehnt fremde Kommentare selbst ab — geprüft wird trotzdem vorher.</summary>
    public async Task<CommentDto> EditCommentAsync(string token, string commentId, string text, string? viewer, CancellationToken ct)
    {
        var body = new CommentEditBody(commentId, new TextSnippet(text));

        using var doc = await SendAuthAsync(HttpMethod.Put, AuthUrl("comments", ("part", "snippet")),
            token, body, AppJson.Default.CommentEditBody, ct);

        return doc is not null && ReadComment(doc.RootElement, 0, viewer) is { } comment
            ? comment
            : throw new YouTubeException("Die Änderung wurde gespeichert, kam aber nicht lesbar zurück.");
    }

    /// <summary>Löscht einen eigenen Kommentar. Trifft es einen der obersten Ebene, nimmt
    /// Google den ganzen Strang samt Antworten mit.</summary>
    public async Task DeleteCommentAsync(string token, string commentId, CancellationToken ct) =>
        (await SendAuthAsync(HttpMethod.Delete, AuthUrl("comments", ("id", commentId)), token, null, ct))?.Dispose();

    /// <summary>Zuletzt mit „Gefällt mir" bewertete Videos. `myRating=like` liefert
    /// vollständige Video-Ressourcen — der Kanal fällt damit ohne Zusatzaufruf ab.</summary>
    public async Task<IReadOnlyList<VideoDto>> LikedVideosAsync(string token, string userKey, int max, CancellationToken ct)
    {
        var key = $"{userKey}|likes";
        if (cache.TryGetValue(key, out VideoDto[]? cached) && cached is not null) return cached;

        var all = new List<VideoDto>();
        string? page = null;

        do
        {
            var url = AuthUrl("videos",
                ("part", "snippet,contentDetails,statistics,status"),
                ("myRating", "like"),
                ("hl", _opt.Language),
                ("maxResults", "50"),
                ("pageToken", page));

            using var doc = await GetAuthAsync(url, token, ct);
            var batch = ReadVideoList(doc.RootElement);

            all.AddRange(batch.Items);
            page = batch.NextPageToken;
        }
        while (!string.IsNullOrEmpty(page) && all.Count < max);

        var result = all.Take(max).ToArray();
        cache.Set(key, result, TimeSpan.FromMinutes(_opt.CacheMinutes));
        return result;
    }

    public async Task<FeedDto> LikesFeedAsync(string token, string userKey, string? pageToken, CancellationToken ct)
    {
        var liked = await LikedVideosAsync(token, userKey, LikeHistory, ct);
        var offset = int.TryParse(pageToken, out var o) && o > 0 ? o : 0;

        var items = liked.Skip(offset).Take(HomePageSize).ToArray();
        var next = offset + HomePageSize < liked.Count ? (offset + HomePageSize).ToString() : null;

        return new FeedDto(items, next);
    }

    /// <summary>Personalisierte Startseite. Echte Empfehlungen gibt die API nicht her
    /// (`activities.list?home` antwortet mit `homeParameterDeprecated`), also wird aus den
    /// Signalen gemischt, die sie hergibt: Kanäle der eigenen Likes, Trends, Abos.</summary>
    /// <summary>
    /// Die persönliche Startseite. Ein <paramref name="categoryId"/> filtert dieselbe
    /// Mischung, statt auf die rohen Kategorie-Trends umzuschalten: Reihenfolge,
    /// Kanaldeckel und Shorts-Trennung bleiben so erhalten. Der Filter kostet nichts —
    /// die 200 vorbereiteten Videos liegen bereits im Speicher und tragen ihre Kategorie
    /// mit. Bleiben dabei zu wenige übrig, füllen die Trends dieser Kategorie auf.
    /// </summary>
    public async Task<FeedDto> HomeFeedAsync(string token, string userKey, string? categoryId, string? pageToken, CancellationToken ct)
    {
        var key = HomeKey(userKey);

        // Die erste Seite wartet nicht auf die Abo-Kanäle: Sie besteht ohnehin nur aus
        // Likes und Trends. Der Abo-Abruf läuft derweil und wird beim nächsten Aufruf
        // eingesammelt, der dann die vollständige Mischung baut und diese hier ersetzt.
        var top = ParseOffset(pageToken) == 0;
        var head = top && string.IsNullOrEmpty(categoryId);

        cache.TryGetValue(key, out FeedPage? page);

        // Eine im Hintergrund fertig gewordene Auffrischung wartet unter einem zweiten
        // Schlüssel und wird nur ganz oben übernommen: Der Blätterzeiger ist eine
        // Position in genau dieser Liste, mitten im Blättern zeigte dieselbe Zahl nach
        // dem Austausch auf eine andere Stelle.
        if (top && cache.TryGetValue(FreshKey(userKey), out FeedPage? refreshed) && refreshed is not null)
        {
            cache.Remove(FreshKey(userKey));
            cache.Set(key, page = refreshed, HardTtl);
            log.LogInformation("⏱  Startseite: aufgefrischte Mischung übernommen");
        }

        if (page is null || (page.Partial && !head))
        {
            page = await HomeBuildAsync(token, userKey, head);
        }
        else if (top && !page.Partial && DateTimeOffset.UtcNow - page.BuiltAt > SoftTtl)
        {
            // Alt, aber brauchbar: unverändert ausliefern und daneben erneuern.
            StartHomeRefresh(token, userKey);
        }

        if (string.IsNullOrEmpty(categoryId))
            return await TimedAsync("Erste Seite schneiden (gesamt)", () => SliceAsync(page, pageToken, ct));

        // Bewusst ohne eigenen Cache-Eintrag: Das Filtern ist ein LINQ-Durchlauf über
        // schon geprüfte Videos, und die Trends dazu liegen im URL-Cache. Ein zweiter
        // Eintrag je Kategorie würde beim Bewerten nur veralten, weil die Invalidierung
        // feste Schlüssel löscht.
        return await SliceAsync(await FilterAsync(page, categoryId, pageToken, ct), pageToken, ct);
    }

    /// <summary>Schneidet die persönliche Mischung auf eine Kategorie zu und hängt die
    /// Trends dieser Kategorie an, falls zu wenig übrig bleibt.</summary>
    /// <summary>Startet den Abo-Abruf für die Startseite und merkt sich die laufende
    /// Aufgabe, damit der Kopf-Aufruf sie anstoßen und der nächste sie einsammeln kann,
    /// ohne sie ein zweites Mal loszutreten.</summary>
    private Task<IReadOnlyList<Dated>> HomeSubsTaskAsync(string token, string userKey)
    {
        // Bewusst ohne das Abbruch-Token der Anfrage: Die Aufgabe soll den Kopf-Aufruf
        // überleben, der sie gestartet hat.
        return SharedTaskAsync(
            $"{userKey}|subtask",
            () => SafeAsync(() => FeedIdsAsync(token, userKey, CancellationToken.None, HomeSubChannels)),
            TimeSpan.FromMinutes(_opt.SubscriptionCacheMinutes));
    }

    /// <summary>Schützt das Merken laufender Aufgaben. Zwischen <c>TryGetValue</c> und
    /// <c>Set</c> passt sonst ein zweiter Aufrufer — gemessen: Warmlauf und die Anfrage
    /// des Browsers drei Millisekunden auseinander, und die Startseite wurde zweimal
    /// gebaut. Statisch, weil <c>AddHttpClient&lt;YouTubeService&gt;</c> den Dienst
    /// transient registriert: Ein Feld je Instanz träfe nie denselben Konkurrenten.</summary>
    private static readonly Lock BuildGate = new();

    /// <summary>Holt die laufende Aufgabe oder startet sie — genau einmal.</summary>
    private Task<T> SharedTaskAsync<T>(string runKey, Func<Task<T>> start, TimeSpan ttl)
    {
        lock (BuildGate)
        {
            if (cache.TryGetValue(runKey, out Task<T>? running) && running is not null) return running;

            // Der Start läuft synchron bis zum ersten await; die Sperre ist also nur
            // Mikrosekunden gehalten und niemals über einen Netzaufruf hinweg.
            var task = start();
            cache.Set(runKey, task, ttl);
            return task;
        }
    }

    private static string HomeKey(string userKey) => $"{userKey}|home";

    /// <summary>Unter diesem Schlüssel wartet eine fertige Auffrischung, bis sie ganz oben
    /// übernommen werden darf.</summary>
    internal static string FreshTag => "home:neu";
    private static string FreshKey(string userKey) => $"{userKey}|{FreshTag}";

    /// <summary>Weiche Frist: Danach gilt die Mischung als alt und wird im Hintergrund
    /// erneuert — ausgeliefert wird sie weiter. Erst nach der harten Frist muss wieder
    /// jemand auf einen Neuaufbau warten.</summary>
    private TimeSpan SoftTtl => TimeSpan.FromMinutes(_opt.CacheMinutes);
    private TimeSpan HardTtl => TimeSpan.FromMinutes(_opt.CacheMinutes * 6);

    /// <summary>Baut den Kopf der Startseite vor, wenn er noch nicht dasteht. Angestoßen
    /// vom Ausliefern der HTML-Seite, also bevor der Browser überhaupt danach fragt.</summary>
    public async Task WarmHomeAsync(string token, string userKey, CancellationToken ct)
    {
        if (cache.TryGetValue(HomeKey(userKey), out FeedPage? _))
        {
            log.LogInformation("⏱  Warmlauf: Startseite steht schon");
            return;
        }

        // Sagt im Protokoll, wer den Aufbau angestoßen hat: Läuft der Browser dem
        // Warmlauf davon, steht diese Zeile nach dessen /api/home-Anfrage.
        log.LogInformation("⏱  Warmlauf: Kopf angestoßen");
        await HomeBuildAsync(token, userKey, head: true);
    }

    /// <summary>Der Aufbau als geteilte Aufgabe: Der Warmlauf und die Anfrage des Browsers
    /// kurz darauf brauchen dieselbe Mischung und sollen sie nicht zweimal bauen. Nach
    /// demselben Muster wie <see cref="HomeSubsTaskAsync"/>.</summary>
    private Task<FeedPage> HomeBuildAsync(string token, string userKey, bool head)
    {
        var runKey = $"{userKey}|homebuild:{(head ? "kopf" : "voll")}";
        return SharedTaskAsync(runKey, () => BuildHomeAsync(token, userKey, head, runKey),
            TimeSpan.FromMinutes(2));
    }

    private async Task<FeedPage> BuildHomeAsync(string token, string userKey, bool head, string runKey)
    {
        try
        {
            log.LogInformation("⏱  --- Startseite: {Phase} ---", head ? "Kopf" : "vollständige Mischung");

            // Bewusst ohne das Abbruch-Token einer einzelnen Anfrage: An dieser Aufgabe
            // hängen womöglich mehrere, und der Warmlauf hat überhaupt keine.
            var subsTask = head ? null : HomeSubsTaskAsync(token, userKey);
            var ids = await TimedAsync("Mischung zusammenstellen (gesamt)",
                () => HomeIdsAsync(token, userKey, subsTask, CancellationToken.None));

            // Der Kopf reicht bis zur zweiten Zeile — alles Weitere kommt mit der
            // vollständigen Mischung, sonst würde hier schon wieder alles vorbereitet.
            var page = await BuildPageAsync(
                ids.Take(head ? HeadDepth : FeedDepth).ToArray(), CancellationToken.None);
            page.Partial = head;
            cache.Set(HomeKey(userKey), page, HardTtl);

            // Kopf ausgeliefert — die Abo-Kanäle jetzt im Hintergrund holen, damit der
            // nächste Aufruf sie fertig vorfindet.
            if (head) _ = HomeSubsTaskAsync(token, userKey);
            return page;
        }
        finally
        {
            cache.Remove(runKey);
        }
    }

    /// <summary>Erneuert die Mischung neben der laufenden Antwort und legt sie unter dem
    /// zweiten Schlüssel ab. Wer gerade blättert, merkt davon nichts.</summary>
    private void StartHomeRefresh(string token, string userKey)
    {
        var runKey = $"{userKey}|homerefresh";

        // Dieselbe Sperre wie beim Aufbau: Zwei Aufrufe von ganz oben, die gleichzeitig
        // die weiche Frist reißen, dürfen nicht zwei Auffrischungen loslassen.
        lock (BuildGate)
        {
            if (cache.TryGetValue(runKey, out bool _)) return;
            cache.Set(runKey, true, TimeSpan.FromMinutes(5));
        }

        _ = Task.Run(async () =>
        {
            try
            {
                log.LogInformation("⏱  --- Startseite: Auffrischung im Hintergrund ---");

                var subsTask = HomeSubsTaskAsync(token, userKey);
                var ids = await TimedAsync("Auffrischung zusammenstellen (gesamt)",
                    () => HomeIdsAsync(token, userKey, subsTask, CancellationToken.None));

                var page = await BuildPageAsync(ids.Take(FeedDepth).ToArray(), CancellationToken.None);
                cache.Set(FreshKey(userKey), page, HardTtl);
            }
            catch (Exception e)
            {
                // Eine misslungene Auffrischung darf niemanden stören — die alte Mischung
                // steht ja weiter.
                log.LogDebug(e, "Auffrischung der Startseite fehlgeschlagen");
            }
            finally
            {
                cache.Remove(runKey);
            }
        });
    }

    private async Task<FeedPage> FilterAsync(FeedPage page, string categoryId, string? pageToken, CancellationToken ct)
    {
        static List<VideoDto> Of(IEnumerable<VideoDto> items, string categoryId) =>
            [.. items.Where(v => v.CategoryId == categoryId)];

        // Auch hier nur so tief prüfen, wie die verlangte Seite reicht — gezählt wird
        // aber, was zur Kategorie passt.
        var offset = ParseOffset(pageToken);
        await EnsureAsync(page, offset + HomePageSize + 1, ct, v => v.CategoryId == categoryId);

        var videos = Of(page.Videos, categoryId);
        var shorts = Of(page.Shorts, categoryId);

        // Eine Kategorie, in der man selten etwas liked, bliebe sonst fast leer.
        if (videos.Count < HomePageSize)
        {
            try
            {
                var trends = await TrendingAsync(categoryId, null, ct);
                // Gegen die ganze Strecke abgleichen, nicht nur gegen das schon Geprüfte —
                // sonst käme ein Trend-Video doppelt, sobald weiter geblättert wird.
                var seen = page.All.Concat(page.Videos).Concat(page.Shorts)
                    .Select(v => v.Id)
                    .ToHashSet(StringComparer.Ordinal);

                videos.AddRange(trends.Items.Where(v => seen.Add(v.Id)));
                shorts.AddRange((trends.Shorts ?? []).Where(v => seen.Add(v.Id)));
            }
            catch (YouTubeException)
            {
                // Keine Trends für diese Kategorie: Dann eben nur die eigenen Treffer.
            }
        }

        // Fertig geprüft — die abgeleitete Strecke hat nichts mehr nachzuholen.
        return new FeedPage { All = [], Videos = videos, Shorts = shorts };
    }

    // -- internals ---------------------------------------------------------

    // -- Startseite --------------------------------------------------------

    /// <summary>Video-ID mit Veröffentlichungszeit und Kanal. Reicht zum Mischen, ohne für
    /// jede Quelle die vollen Videodaten holen zu müssen.</summary>
    /// <summary><paramref name="IsShort"/> stammt aus dem RSS-Feed des Kanals und spart
    /// die einzelne HEAD-Prüfung; <c>null</c> heißt „unbekannt".</summary>
    private readonly record struct Dated(string Id, DateTimeOffset At, string ChannelId, bool? IsShort = null);

    /// <summary>Länge der Spalte „Ähnliche Videos" und wie viele Treffer darin
    /// höchstens vom Kanal des laufenden Videos stammen dürfen.</summary>
    /// <summary>Wie viele Uploads-Seiten höchstens durchgesehen werden, wenn eine Seite
    /// nur Shorts enthält.</summary>
    /// <summary>Wie viele Shorts-Prüfungen gleichzeitig laufen dürfen.</summary>
    private const int ProbeConcurrency = 32;
    /// <summary>Wie viele Kanal-Uploads gleichzeitig geholt werden. Gemessen: 32
    /// gleichzeitige Aufrufe kosten die API kaum mehr als ein einzelner, und ein Abo-Feed
    /// kann sehr breit sein — beim Testkonto sind es 604 Kanäle und damit 604 Aufrufe.</summary>
    private const int UploadConcurrency = 32;
    private const int ChannelPageDepth = 3;
    /// <summary>Wie viele Chart-Seiten höchstens geholt werden, falls eine davon nach dem
    /// Aussortieren der Shorts gar kein Video übriglässt.</summary>
    private const int TrendingPageDepth = 3;
    private const int RelatedSize = 20;
    private const int SameChannelLimit = 6;
    /// <summary>Höchstlänge der Suchabfrage in Wörtern, die Trefferzahl, ab der eine Stufe
    /// als brauchbar gilt, und wie viele Stichwort-Stufen höchstens versucht werden.</summary>
    private const int RelatedQueryWords = 6;
    private const int RelatedMinimum = 5;
    private const int RelatedTagSteps = 3;
    /// <summary>Obergrenze der Suchen je Video. Sie bindet härter als die
    /// Kontingenteinheiten: Google zählt Suchanfragen pro Tag getrennt mit.</summary>
    private const int RelatedMaxSearches = 3;
    private const int LikeHistory = 200;
    private const int LikedChannelLimit = 40;
    private const int HomePageSize = 24;
    /// <summary>Die ersten zwei Zeilen der Startseite. So viel wird ohne die Abos gebaut
    /// und ausgeliefert, während deren Kanal-Feeds noch laufen.</summary>
    private const int HeadSize = 10;

    /// <summary>Wie viele IDs der Kopf vorbereitet — das Doppelte der zwei Zeilen, weil
    /// ein Teil davon Shorts sind und in die Zeile wandert.
    ///
    /// Genau so weit bleibt der Abo-Topf draußen, und das ist keine Kosmetik: Der
    /// Blätterzeiger ist eine Position in der Liste, und die zweite Seite liest ihn in
    /// der vollständigen Mischung, die den Kopf abgelöst hat. Beide Listen müssen sich
    /// deshalb auf den ersten <see cref="HeadDepth"/> Plätzen decken. Stiegen die Abos
    /// schon nach <see cref="HeadSize"/> ein, rückten die letzten Videos des Kopfes in
    /// der vollständigen Mischung nach hinten — und standen hinter dem Zeiger ein
    /// zweites Mal da. Genau das war der Doppelgänger am Seitenübergang.</summary>
    private const int HeadDepth = HeadSize * 2;
    /// <summary>Wie viele Abo-Kanäle in die Startseiten-Mischung einfließen. Der Abo-Tab
    /// fragt weiterhin alle ab; für die 10 %, die Abos zur Startseite beitragen, wären
    /// 604 Kanal-Feeds reine Wartezeit.</summary>
    private const int HomeSubChannels = 240;
    private const int PerChannelPerPage = 3;

    /// <summary>Videos ab hier gelten als frisch und werden bevorzugt einsortiert.</summary>
    private static readonly TimeSpan FreshWindow = TimeSpan.FromDays(14);

    /// <summary>Mischungsverhältnis Likes-Kanäle : Trends : Abos.</summary>
    private static readonly int[] Ratio = [7, 2, 1];

    /// <summary>Stellt die Mischung zusammen. <paramref name="subsTask"/> darf noch laufen —
    /// für die ersten zwei Zeilen wird er gar nicht gebraucht, siehe <see cref="HeadSize"/>.
    /// Ist er <c>null</c>, entsteht nur der Kopf aus Likes und Trends.</summary>
    private async Task<IReadOnlyList<string>> HomeIdsAsync(
        string token, string userKey, Task<IReadOnlyList<Dated>>? subsTask, CancellationToken ct)
    {
        // Die Trends hängen nicht an den Likes — sie laufen von Anfang an mit.
        var trendingTask = TimedAsync("Trends", () => SafeAsync(() => TrendingIdsAsync(ct)));

        var liked = await TimedAsync("Like-Verlauf", () => LikedVideosAsync(token, userKey, LikeHistory, ct));

        // Wie oft in einem Kanal geliked wurde, ist sein Gewicht.
        var weights = liked
            .Where(v => !string.IsNullOrEmpty(v.ChannelId))
            .GroupBy(v => v.ChannelId)
            .ToDictionary(g => g.Key, g => g.Count());

        var favourites = weights
            .OrderByDescending(p => p.Value)
            .Take(LikedChannelLimit)
            .ToArray();

        var fromLikes = new System.Collections.Concurrent.ConcurrentBag<Dated>();
        using (var gate = new SemaphoreSlim(UploadConcurrency))
        {
            await TimedAsync($"Uploads von {favourites.Length} Lieblingskanälen", async () =>
            {
            await Task.WhenAll(favourites.Select(async pair =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    // Lieblingskanäle steuern mehr bei, aber gedeckelt.
                    var take = 1 + Math.Min(4, pair.Value);
                    foreach (var upload in (await UploadsAsync(pair.Key, ct)).Take(take))
                        fromLikes.Add(upload);
                }
                catch (YouTubeException)
                {
                    // Ein einzelner Kanal ohne sichtbare Uploads darf die Startseite nicht kippen.
                }
                finally
                {
                    gate.Release();
                }
            }));
            return 0;
            });
        }

        var trending = await trendingTask;
        var subs = subsTask is null
            ? []
            : await TimedAsync("Abo-Feed", () => subsTask);

        List<List<Dated>> Pools(bool fresh)
        {
            var cutoff = DateTimeOffset.UtcNow - FreshWindow;
            bool Match(Dated d) => fresh ? d.At >= cutoff : d.At < cutoff;

            return new List<List<Dated>>
            {
                fromLikes.Where(Match).OrderByDescending(d => d.At).ToList(),
                trending.Where(Match).OrderByDescending(d => d.At).ToList(),
                subs.Where(Match).OrderByDescending(d => d.At).ToList(),
            };
        }

        // Gelikte Videos kennt man schon; sie tauchen auf der Startseite nicht wieder auf.
        var seen = new HashSet<string>(liked.Select(v => v.Id), StringComparer.Ordinal);

        // Erst alles aus den letzten 14 Tagen, dann der Rest — nach derselben Regel.
        var ids = Interleave(Pools(fresh: true), seen, 0);
        ids.AddRange(Interleave(Pools(fresh: false), seen, ids.Count));

        return ids;
    }

    /// <summary>Eine fertig aufbereitete Feed-Seite: Raster-Videos und die davon
    /// getrennten Shorts.</summary>
    /// <summary>Eine vorbereitete Feed-Strecke. <c>All</c> steht in Mischreihenfolge und
    /// ist angereichert, aber noch ungeprüft; <c>Videos</c> und <c>Shorts</c> wachsen
    /// nach, während geblättert wird. Die Shorts-Prüfung ist der teuerste Teil einer
    /// Antwort — deshalb wird sie nur so weit getrieben, wie die verlangte Seite
    /// reicht.</summary>
    private sealed class FeedPage
    {
        public required VideoDto[] All { get; init; }
        /// <summary>Wann diese Mischung entstanden ist. Danach richtet sich, ob sie im
        /// Hintergrund erneuert wird.</summary>
        public DateTimeOffset BuiltAt { get; } = DateTimeOffset.UtcNow;
        public List<VideoDto> Videos { get; init; } = [];
        public List<VideoDto> Shorts { get; init; } = [];
        public int Judged { get; set; }
        /// <summary>Nur der Kopf aus Likes und Trends; die Abos fehlen noch.</summary>
        public bool Partial { get; set; }
        /// <summary>Zwei gleichzeitige Anfragen teilen sich einen Cache-Eintrag.</summary>
        public SemaphoreSlim Gate { get; } = new(1);
    }

    /// <summary>Wie viele Videos je Anlauf geprüft werden. Ein Anlauf kostet ungefähr so
    /// viel wie seine langsamste Anfrage, unabhängig davon, wie viele darin stecken —
    /// gemessen 21 Prüfungen in 2,7 s und 12 in 4,0 s. Zwei Anläufe kosten also doppelt.
    /// Der Block ist deshalb groß genug, dass die erste Seite in aller Regel mit einem
    /// auskommt, und bleibt trotzdem weit unter den 200 vorbereiteten Videos.</summary>
    private const int JudgeChunk = 96;

    /// <summary>Prüft so weit in die Strecke hinein, bis <paramref name="needed"/> passende
    /// Videos beisammen sind. Was dabei an Shorts anfällt, sammelt die Strecke selbst;
    /// welche davon der Browser schon hat, entscheidet der Blätterzeiger.</summary>
    private async Task EnsureAsync(
        FeedPage page, int needed, CancellationToken ct, Func<VideoDto, bool>? match = null)
    {
        await page.Gate.WaitAsync(ct);
        try
        {
            while (page.Judged < page.All.Length
                && (match is null ? page.Videos.Count : page.Videos.Count(match)) < needed)
            {
                var chunk = page.All.Skip(page.Judged).Take(JudgeChunk).ToArray();
                page.Judged += chunk.Length;

                var (videos, shorts) = await TimedAsync(
                    $"Shorts-Prüfung, Block ab {page.Judged - chunk.Length} ({chunk.Length} Videos)",
                    () => SplitShortsAsync(chunk, ct));
                page.Videos.AddRange(videos);
                page.Shorts.AddRange(shorts);
            }
        }
        finally
        {
            page.Gate.Release();
        }
    }

    /// <summary>Wie tief ein persönlicher Feed im Voraus aufbereitet wird. Einmal
    /// anreichern statt je Seite — Blättern kostet danach gar nichts mehr.</summary>
    private const int FeedDepth = 200;

    /// <summary>Reichert die Strecke an. Geprüft wird erst beim Schneiden.</summary>
    private async Task<FeedPage> BuildPageAsync(IReadOnlyList<string> ids, CancellationToken ct) =>
        new() { All = [.. await TimedAsync($"Anreichern von {ids.Count} Videos", () => HydrateManyAsync(ids, ct))] };

    /// <summary>Der Blätterzeiger trägt zwei Zahlen: wie viele Raster-Videos und wie
    /// viele Shorts der Browser schon hat. Ohne die zweite stünden die Shorts der
    /// Kopf-Seite ein zweites Mal in der Zeile, sobald die vollständige Mischung sie
    /// ersetzt und dabei von vorn prüft.</summary>
    private static (int Videos, int Shorts) ParseToken(string? pageToken)
    {
        static int Number(string s) => int.TryParse(s, out var n) && n > 0 ? n : 0;

        if (string.IsNullOrEmpty(pageToken)) return (0, 0);
        var parts = pageToken.Split(':');
        return (Number(parts[0]), parts.Length > 1 ? Number(parts[1]) : 0);
    }

    private static int ParseOffset(string? pageToken) => ParseToken(pageToken).Videos;

    private async Task<FeedDto> SliceAsync(FeedPage page, string? pageToken, CancellationToken ct)
    {
        var (offset, seenShorts) = ParseToken(pageToken);

        // Eines mehr als die Seite braucht, damit feststeht, ob noch eine folgt.
        await EnsureAsync(page, offset + HomePageSize + 1, ct);

        var items = page.Videos.Skip(offset).Take(HomePageSize).ToArray();

        // Beim Kopf steht der Folgezeiger immer: Dahinter wartet die vollständige
        // Mischung, auch wenn diese Seite schon alles zeigt, was der Kopf hergibt.
        var nextOffset = page.Partial ? items.Length : offset + HomePageSize;
        var next = page.Partial || nextOffset < page.Videos.Count
            ? $"{nextOffset}:{page.Shorts.Count}"
            : null;

        // Nur, was der Browser noch nicht hat — er hängt es an den Vorrat seiner Zeile an.
        return new FeedDto(items, next, [.. page.Shorts.Skip(seenShorts)]);
    }

    /// <summary>videos.list nimmt höchstens 50 IDs je Aufruf entgegen. Die Blöcke sind
    /// voneinander unabhängig und laufen deshalb gleichzeitig; zusammengesetzt wird
    /// anschließend wieder in Blockreihenfolge, denn daran hängt die Mischreihenfolge
    /// der Startseite.</summary>
    private async Task<List<VideoDto>> HydrateManyAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        var blocks = Enumerable.Range(0, (ids.Count + 49) / 50)
            .Select(i => HydrateAsync([.. ids.Skip(i * 50).Take(50)], ct));

        var result = new List<VideoDto>(ids.Count);
        foreach (var block in await Task.WhenAll(blocks)) result.AddRange(block);

        return result;
    }

    /// <summary>Trennt Shorts vom Rest, damit sie nicht wahllos im Raster landen. Die API
    /// kennt keinen Shorts-Filter, deshalb dieselbe Erkennung wie in
    /// <see cref="ShortsAsync"/>: über die Länge grob sieben, den Rest gegen
    /// youtube.com/shorts/&lt;id&gt; verifizieren. Das Urteil je Video hält einen Tag,
    /// sonst liefe bei jeder Aktualisierung dieselbe Prüfung erneut.</summary>
    /// <summary>Vorsieb: Alles über drei Minuten ist sicher kein Short und muss weder
    /// geprüft noch aufgewärmt werden.</summary>
    private static bool CouldBeShort(VideoDto v) => v.DurationSeconds is > 0 and <= 180;

    private async Task<(List<VideoDto> Videos, List<VideoDto> Shorts)> SplitShortsAsync(
        IReadOnlyList<VideoDto> items, CancellationToken ct)
    {
        var unknown = items
            .Where(v => CouldBeShort(v) && !IsJudged(v.Id))
            .DistinctBy(v => v.Id)
            .ToArray();

        if (unknown.Length > 0)
        {
            var verified = (await VerifyShortsAsync(unknown, ct))
                .Select(v => v.Id)
                .ToHashSet(StringComparer.Ordinal);

            foreach (var v in unknown)
                cache.Set(ShortKey(v.Id), verified.Contains(v.Id), TimeSpan.FromHours(24));
        }

        var videos = new List<VideoDto>();
        var shorts = new List<VideoDto>();

        foreach (var v in items)
        {
            var isShort = CouldBeShort(v)
                && cache.TryGetValue(ShortKey(v.Id), out bool flag)
                && flag;

            // Ein Short wird hochkant gezeigt; mqdefault ist 320x180 und würde in der
            // Zeile auf mehr als das Doppelte hochskaliert.
            (isShort ? shorts : videos).Add(isShort ? v with { Thumb = VerticalThumb(v.Id) } : v);
        }

        return (videos, shorts);
    }

    private static string ShortKey(string videoId) => $"isshort:{videoId}";

    /// <summary>Liegt für dieses Video schon ein Shorts-Urteil im Speicher?</summary>
    private bool IsJudged(string videoId) => cache.TryGetValue(ShortKey(videoId), out bool _);

    /// <summary>Nimmt reihum aus den Töpfen, im Verhältnis <see cref="Ratio"/>, und lässt
    /// je Seite höchstens <see cref="PerChannelPerPage"/> Videos desselben Kanals durch.
    /// <paramref name="startIndex"/> hält die Seitengrenzen über mehrere Aufrufe hinweg
    /// an derselben Stelle.</summary>
    /// <summary>Die ersten <see cref="HeadDepth"/> Plätze — der Kopf — bleiben den Likes
    /// und Trends vorbehalten. Der Abo-Topf steigt erst danach ein; so steht der Anfang der
    /// Startseite schon, während die Abos noch geladen werden, und die vollständige
    /// Mischung beginnt Platz für Platz genauso wie der Kopf, den sie ablöst.</summary>
    private static List<string> Interleave(List<List<Dated>> pools, HashSet<string> seen, int startIndex)
    {
        const int SubsPool = 2;

        var used = pools.Select(p => new bool[p.Count]).ToArray();
        var perChannel = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<string>();
        var block = -1;

        bool TryTake(int source)
        {
            // Abos steigen erst hinter dem Kopf ein.
            if (source == SubsPool && startIndex + result.Count < HeadDepth) return false;

            var current = (startIndex + result.Count) / HomePageSize;
            if (current != block)
            {
                block = current;
                perChannel.Clear();
            }

            var pool = pools[source];
            for (var i = 0; i < pool.Count; i++)
            {
                if (used[source][i]) continue;

                var candidate = pool[i];
                if (seen.Contains(candidate.Id))
                {
                    used[source][i] = true;
                    continue;
                }

                var channel = candidate.ChannelId;
                if (channel.Length > 0 && perChannel.GetValueOrDefault(channel) >= PerChannelPerPage)
                    continue;   // Kanal hat auf dieser Seite genug — später nochmal versuchen.

                used[source][i] = true;
                seen.Add(candidate.Id);
                if (channel.Length > 0) perChannel[channel] = perChannel.GetValueOrDefault(channel) + 1;
                result.Add(candidate.Id);
                return true;
            }

            return false;
        }

        while (true)
        {
            var progress = false;
            for (var source = 0; source < pools.Count; source++)
                for (var n = 0; n < Ratio[source]; n++)
                    if (TryTake(source)) progress = true;

            if (progress) continue;

            // Nichts mehr durchgekommen: Liegt es nur am Kanaldeckel, wird er für diesen
            // Block aufgehoben, statt die Seite vorzeitig abzubrechen.
            if (perChannel.Count == 0) break;
            perChannel.Clear();
        }

        return result;
    }

    private async Task<IReadOnlyList<Dated>> TrendingIdsAsync(CancellationToken ct)
    {
        var url = Url("videos",
            ("part", "snippet"),
            ("chart", "mostPopular"),
            ("regionCode", _opt.RegionCode),
            ("hl", _opt.Language),
            ("maxResults", "50"));

        using var doc = await GetAsync(url, ct);

        var result = new List<Dated>();
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } videoId)
                result.Add(new Dated(videoId, PublishedAt(item), ChannelOf(item)));
        }

        // Die Charts sagen nichts über Shorts. Die Kanal-Feeds dazu schon — und sie kosten
        // nichts, im Gegensatz zu einer HEAD-Prüfung je Video.
        await WarmShortsAsync(result.Select(d => d.ChannelId), ct);

        return result;
    }

    private static DateTimeOffset PublishedAt(JsonElement item) =>
        item.TryGetProperty("snippet", out var s)
            && s.TryGetProperty("publishedAt", out var p)
            && p.TryGetDateTimeOffset(out var when)
                ? when
                : DateTimeOffset.MinValue;

    private static string ChannelOf(JsonElement item) =>
        item.TryGetProperty("snippet", out var s) && s.TryGetProperty("channelId", out var c)
            ? c.GetString() ?? ""
            : "";

    /// <summary>Eine Quelle der Startseite darf ausfallen, ohne die anderen mitzunehmen.</summary>
    private static async Task<IReadOnlyList<T>> SafeAsync<T>(Func<Task<IReadOnlyList<T>>> work)
    {
        try
        {
            return await work();
        }
        catch (Exception e) when (e is YouTubeException or HttpRequestException or TaskCanceledException)
        {
            return [];
        }
    }


    private async Task<IReadOnlyList<VideoDto>> HydrateAsync(string[] ids, CancellationToken ct)
    {
        var url = Url("videos",
            ("part", "snippet,contentDetails,statistics,status"),
            ("id", string.Join(',', ids)),
            ("hl", _opt.Language));

        using var doc = await GetAsync(url, ct);
        var byId = ReadVideoList(doc.RootElement).Items.ToDictionary(v => v.Id);

        // videos.list does not preserve the relevance order of search.list.
        return ids.Select(id => byId.GetValueOrDefault(id)).OfType<VideoDto>().ToArray();
    }

    private static FeedDto ReadVideoList(JsonElement root)
    {
        var items = root.GetProperty("items").EnumerateArray()
            .Select(ReadVideo)
            .OfType<VideoDto>()
            .ToArray();

        var next = root.TryGetProperty("nextPageToken", out var n) ? n.GetString() : null;
        return new FeedDto(items, next);
    }

    private static VideoDto? ReadVideo(JsonElement item)
    {
        var id = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;
        if (string.IsNullOrEmpty(id)) return null;
        if (!item.TryGetProperty("snippet", out var s)) return null;

        var rawDuration = item.TryGetProperty("contentDetails", out var cd)
            && cd.TryGetProperty("duration", out var dur)
                ? dur.GetString()
                : null;
        var duration = Format.Duration(rawDuration);

        // Non-embeddable videos must open on youtube.com instead of the inline player.
        var embeddable = !item.TryGetProperty("status", out var st)
            || !st.TryGetProperty("embeddable", out var emb)
            || emb.GetBoolean();

        var published = s.TryGetProperty("publishedAt", out var pa)
            && pa.TryGetDateTimeOffset(out var when)
                ? when
                : DateTimeOffset.UtcNow;

        return new VideoDto(
            Id: id,
            Title: s.GetProperty("title").GetString() ?? "",
            ChannelId: s.TryGetProperty("channelId", out var cid) ? cid.GetString() ?? "" : "",
            ChannelTitle: s.TryGetProperty("channelTitle", out var ct) ? ct.GetString() ?? "" : "",
            // mqdefault is 320x180 and unlike hqdefault has no letterboxing — small and 16:9.
            Thumb: $"https://i.ytimg.com/vi/{id}/mqdefault.jpg",
            DurationLabel: duration,
            ViewsLabel: Format.Views(ReadCount(item, "viewCount")),
            PublishedLabel: Format.Age(published),
            Embeddable: embeddable)
        {
            DurationSeconds = Format.DurationSeconds(rawDuration),
            CategoryId = s.TryGetProperty("categoryId", out var cat) ? cat.GetString() : null
        };
    }

    private static ulong? ReadCount(JsonElement item, string name)
    {
        // Creators can hide view/like counts, in which case the field is absent.
        if (!item.TryGetProperty("statistics", out var stats)) return null;
        if (!stats.TryGetProperty(name, out var el)) return null;
        return ulong.TryParse(el.GetString(), out var v) ? v : null;
    }

    private string Url(string path, params (string Key, string? Value)[] query)
    {
        var parts = query
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}")
            .Append($"key={Uri.EscapeDataString(_opt.ApiKey)}");

        return $"{Base}{path}?{string.Join('&', parts)}";
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct, TimeSpan? ttl = null)
    {
        if (!IsConfigured)
            throw new YouTubeException("Kein YouTube-API-Key konfiguriert.");

        // Cache the raw payload; the key includes the API key so rotating it invalidates.
        if (cache.TryGetValue(url, out byte[]? cached) && cached is not null)
            return JsonDocument.Parse(cached);

        using var res = await http.GetAsync(url, ct);
        var body = await res.Content.ReadAsByteArrayAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            var (message, reason) = DescribeError(res.StatusCode, body);
            throw new YouTubeException(message, reason);
        }

        cache.Set(url, body, ttl ?? TimeSpan.FromMinutes(_opt.CacheMinutes));
        return JsonDocument.Parse(body);
    }

    /// <summary>Bildnachweis aus einem Snippet: das kleinste Format reicht für Avatare.</summary>
    private static string? ThumbUrl(JsonElement snippet)
    {
        if (!snippet.TryGetProperty("thumbnails", out var thumbs)) return null;

        foreach (var size in (string[])["default", "medium", "high"])
            if (thumbs.TryGetProperty(size, out var t) && t.TryGetProperty("url", out var u))
                return u.GetString();

        return null;
    }

    /// <summary>Wie <see cref="Url"/>, nur ohne API-Key: Mit einem Bearer-Token ist er
    /// überflüssig.</summary>
    private static string AuthUrl(string path, params (string Key, string? Value)[] query)
    {
        var parts = query
            .Where(p => !string.IsNullOrEmpty(p.Value))
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!)}");

        return $"{Base}{path}?{string.Join('&', parts)}";
    }

    /// <summary>Angemeldete Antworten dürfen nie in den Zwischenspeicher von
    /// <see cref="GetAsync"/> geraten: Der schlüsselt allein nach URL und würde die
    /// privaten Daten des einen Browsers an den nächsten ausliefern. Was sich zu cachen
    /// lohnt, legen die Aufrufer selbst unter einem Schlüssel mit Benutzerkennung ab.</summary>
    private async Task<JsonDocument> GetAuthAsync(string url, string token, CancellationToken ct) =>
        await SendAuthAsync(HttpMethod.Get, url, token, null, ct)
            ?? throw new YouTubeException("Leere Antwort von YouTube.");

    /// <summary>Mit Rumpf. Der Serialisierungsplan reist als <see cref="JsonTypeInfo{T}"/>
    /// mit, statt dass der Typ zur Laufzeit erraten wird — nur so trägt es unter Native
    /// AOT, wo es keine Reflexion mehr gibt.</summary>
    private Task<JsonDocument?> SendAuthAsync<TBody>(
        HttpMethod method, string url, string token, TBody body, JsonTypeInfo<TBody> plan,
        CancellationToken ct) =>
        SendAuthAsync(method, url, token, JsonSerializer.Serialize(body, plan), ct);

    private async Task<JsonDocument?> SendAuthAsync(
        HttpMethod method, string url, string token, string? bodyJson, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (bodyJson is not null)
            req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");

        using var res = await http.SendAsync(req, ct);
        var payload = await res.Content.ReadAsByteArrayAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            var (message, reason) = DescribeError(res.StatusCode, payload);
            // 401 heißt: Das Zugriffstoken taugt nicht mehr. Der Aufrufer erneuert es
            // genau einmal und wiederholt die Anfrage.
            throw new YouTubeException(
                message, res.StatusCode == HttpStatusCode.Unauthorized ? "unauthorized" : reason);
        }

        // subscriptions.delete und videos.rate antworten mit 204 ohne Rumpf.
        return payload.Length > 0 ? JsonDocument.Parse(payload) : null;
    }

    private static (string Message, string? Reason) DescribeError(System.Net.HttpStatusCode status, byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var err = doc.RootElement.GetProperty("error");
            var reason = err.TryGetProperty("errors", out var errs) && errs.GetArrayLength() > 0
                && errs[0].TryGetProperty("reason", out var r)
                    ? r.GetString()
                    : null;

            var message = reason switch
            {
                "quotaExceeded" => "Das Tageskontingent des API-Keys ist aufgebraucht. Morgen wieder oder neues Projekt anlegen.",
                // Google zählt Suchanfragen getrennt von den Kontingenteinheiten und deckelt
                // sie deutlich früher. Die Rohmeldung nennt die Projektnummer — die hat im
                // Browser nichts verloren.
                "rateLimitExceeded" => "Das Tageslimit für Suchanfragen ist aufgebraucht. Suche, Shorts-Zeile und ähnliche Videos gehen erst morgen wieder.",
                "keyInvalid" or "badRequest" => "Der API-Key wurde abgelehnt. Prüfe Key und ob die YouTube Data API v3 im Google-Cloud-Projekt aktiviert ist.",
                "commentsDisabled" => "Für dieses Video sind Kommentare deaktiviert.",
                "ipRefererBlocked" => "Der API-Key ist für diese Anfrage gesperrt (Key-Restrictions im Google-Cloud-Projekt prüfen).",
                "insufficientPermissions" => "Der Anmeldung fehlen die nötigen Rechte. Bitte neu anmelden.",
                "forbidden" => "YouTube hat die Aktion abgelehnt.",
                _ => err.TryGetProperty("message", out var m) ? m.GetString() ?? $"API-Fehler {(int)status}" : $"API-Fehler {(int)status}"
            };

            return (message, reason);
        }
        catch (JsonException)
        {
            return ($"API-Fehler {(int)status}", null);
        }
    }
}
