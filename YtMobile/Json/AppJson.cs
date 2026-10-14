using System.Text.Json;
using System.Text.Json.Serialization;
using YtMobile.Auth;
using YtMobile.Youtube;

namespace YtMobile.Json;

/// <summary>Der vorab erzeugte Serialisierungsplan für alles, was diese App über JSON
/// schreibt oder liest. Native AOT lässt die Reflexion von System.Text.Json weg — was
/// hier nicht steht, fällt zur Laufzeit aus, und zwar erst beim Aufruf des Endpunkts.
/// Neue Antwortform heißt deshalb: neue <c>JsonSerializable</c>-Zeile.
///
/// <para><see cref="JsonSerializerDefaults.Web"/> entspricht genau dem, was ASP.NET für
/// Minimal APIs ohnehin einstellt — camelCase, Groß-/Kleinschreibung beim Lesen egal.
/// Damit schreibt der Kontext dieselben Namen wie vorher der Reflexionsweg, die
/// Oberfläche merkt von der Umstellung nichts.</para>
///
/// <para>Der Zeichenkodierer bleibt bewusst der voreingestellte: Er maskiert
/// <c>&lt;</c>, <c>&gt;</c> und <c>&amp;</c>. Nur deshalb kann ein Kanalname aus dem
/// eingebetteten <c>&lt;script&gt;</c> der Startseite nicht ausbrechen.</para></summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
// -- Antworten der API
[JsonSerializable(typeof(ConfigDto))]
[JsonSerializable(typeof(ErrorDto))]
[JsonSerializable(typeof(OkDto))]
[JsonSerializable(typeof(DeletedDto))]
// -- YouTube-Schicht
[JsonSerializable(typeof(FeedDto))]
[JsonSerializable(typeof(VideoDetailDto))]
[JsonSerializable(typeof(CommentsDto))]
[JsonSerializable(typeof(CommentDto))]
[JsonSerializable(typeof(RatingDto))]
[JsonSerializable(typeof(SubStateDto))]
[JsonSerializable(typeof(ChannelRefDto))]
[JsonSerializable(typeof(IReadOnlyList<CategoryDto>))]
[JsonSerializable(typeof(IReadOnlyList<ChannelRefDto>))]
[JsonSerializable(typeof(IReadOnlyList<VideoDto>))]
// -- was die Oberfläche schickt
[JsonSerializable(typeof(CommentBody))]
[JsonSerializable(typeof(RatingBody))]
// -- Sitzung: verschlüsselt im Cookie, nie über die Leitung sichtbar
[JsonSerializable(typeof(OAuthFlow))]
[JsonSerializable(typeof(SessionData))]
// -- Anfragerümpfe an die YouTube Data API
[JsonSerializable(typeof(SubscribeBody))]
[JsonSerializable(typeof(CommentThreadBody))]
[JsonSerializable(typeof(ReplyBody))]
[JsonSerializable(typeof(CommentEditBody))]
internal sealed partial class AppJson : JsonSerializerContext;
