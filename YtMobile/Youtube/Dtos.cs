using System.Text.Json.Serialization;

namespace YtMobile.Youtube;

/// <summary>Compact video shape sent to the browser. Labels are pre-formatted
/// server-side so the client script stays small on weak devices.</summary>
public sealed record VideoDto(
    string Id,
    string Title,
    string ChannelId,
    string ChannelTitle,
    string Thumb,
    string? DurationLabel,
    string? ViewsLabel,
    string PublishedLabel,
    bool Embeddable)
{
    /// <summary>Rohe Länge in Sekunden. Nur für die Shorts-Erkennung serverseitig
    /// gebraucht und deshalb nicht Teil der JSON-Antwort.</summary>
    [JsonIgnore]
    public int DurationSeconds { get; init; }

    /// <summary>YouTube-Kategorie des Videos. Dient serverseitig dem Kategoriefilter der
    /// Startseite und gehört wie die Rohlänge nicht in die JSON-Antwort.</summary>
    [JsonIgnore]
    public string? CategoryId { get; init; }
}

/// <summary><paramref name="Shorts"/> trägt die aus dem Raster aussortierten Shorts der
/// ersten Seite, damit die Oberfläche sie in ihrer eigenen Zeile zeigen kann.</summary>
public sealed record FeedDto(
    IReadOnlyList<VideoDto> Items,
    string? NextPageToken,
    IReadOnlyList<VideoDto>? Shorts = null);

public sealed record CategoryDto(string Id, string Title);

/// <summary><paramref name="IsShort"/> entscheidet, ob in der Seitenspalte Shorts
/// erlaubt sind: Wer einen Short schaut, will danach wieder Shorts sehen.</summary>
public sealed record VideoDetailDto(
    VideoDto Video,
    string Description,
    string? LikesLabel,
    bool IsShort);

/// <summary><paramref name="IsOwn"/> markiert Kommentare des angemeldeten Kanals —
/// nur diese dürfen bearbeitet und gelöscht werden.</summary>
public sealed record CommentDto(
    string Id,
    string Author,
    string? AuthorChannelId,
    string? AuthorThumb,
    string Text,
    string PublishedLabel,
    string? LikesLabel,
    int ReplyCount,
    bool IsOwn);

/// <summary><paramref name="Disabled"/> is true when the uploader turned comments off,
/// which the API reports as a 403 rather than an empty list.</summary>
public sealed record CommentsDto(
    IReadOnlyList<CommentDto> Items,
    string? NextPageToken,
    bool Disabled);

/// <summary>Ein abonnierter Kanal in der Abo-Liste.</summary>
public sealed record ChannelRefDto(string Id, string Title, string? Thumb);

/// <summary><paramref name="SubscriptionId"/> wird zum Abbestellen gebraucht und ist
/// nur gesetzt, wenn der Kanal abonniert ist.</summary>
public sealed record SubStateDto(bool Subscribed, string? SubscriptionId);

/// <summary>Bewertung des angemeldeten Kanals: "like", "dislike" oder "none".</summary>
public sealed record RatingDto(string Rating);
