using YtMobile.Auth;

namespace YtMobile.Json;

/// <summary>Antwortformen, die nicht aus der YouTube-Schicht kommen, sondern von der API
/// selbst. Sie waren bis zur AOT-Umstellung anonyme Objekte — die kann der
/// Quellcodegenerator nicht sehen, und ohne Reflexion zur Laufzeit fällt die
/// Serialisierung dann aus. Benannt sind sie zugleich die ehrlichere Beschreibung
/// dessen, was die Oberfläche tatsächlich zu lesen bekommt.</summary>
public sealed record ErrorDto(string Error);

/// <summary>Quittung ohne Inhalt, etwa nach dem Abmelden.</summary>
public sealed record OkDto(bool Ok);

/// <summary>Bestätigt einen gelöschten Kommentar mit seiner Kennung.</summary>
public sealed record DeletedDto(string Deleted);

/// <summary>Was die Oberfläche vor allem anderen wissen muss. Diese eine Form bedient
/// beide Wege: das in die Startseite eingebettete <c>__CONFIG__</c> und
/// <c>/api/config</c>. Vorher stand sie zweimal im Code und musste von Hand
/// gleichgehalten werden.</summary>
public sealed record ConfigDto(bool Configured, bool LoginAvailable, SessionUser? User);
