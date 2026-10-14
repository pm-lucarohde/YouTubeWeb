namespace YtMobile.Youtube;

/// <summary>Die vier Rümpfe, die diese App an die YouTube Data API *schickt*. Gelesen
/// wird alles über <c>JsonElement</c>, geschrieben wird nur hier — und weil Native AOT
/// ohne Reflexion serialisiert, muss jede dieser Formen benannt und im
/// <see cref="Json.AppJson"/>-Kontext eingetragen sein.
///
/// <para>Die Verschachtelung spiegelt die Ressourcen der API, nicht unseren Geschmack:
/// <c>snippet</c> heißt dort der Teil, den ein Aufrufer setzen darf. Die Namen landen
/// über die camelCase-Regel des Kontexts genau so im JSON, wie die API sie erwartet.</para></summary>
public sealed record ResourceIdBody(string Kind, string ChannelId);

public sealed record SubscribeSnippet(ResourceIdBody ResourceId);

/// <summary>Abonniert einen Kanal.</summary>
public sealed record SubscribeBody(SubscribeSnippet Snippet);

/// <summary>Der eigentliche Text — kommt in drei der vier Rümpfe vor.</summary>
public sealed record TextSnippet(string TextOriginal);

public sealed record TopLevelComment(TextSnippet Snippet);

public sealed record CommentThreadSnippet(string VideoId, TopLevelComment TopLevelComment);

/// <summary>Neuer Kommentar unter einem Video.</summary>
public sealed record CommentThreadBody(CommentThreadSnippet Snippet);

public sealed record ReplySnippet(string ParentId, string TextOriginal);

/// <summary>Antwort auf einen bestehenden Kommentar.</summary>
public sealed record ReplyBody(ReplySnippet Snippet);

/// <summary>Ändert den Text eines eigenen Kommentars.</summary>
public sealed record CommentEditBody(string Id, TextSnippet Snippet);
