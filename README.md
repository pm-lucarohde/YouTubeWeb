# YouTube — leichtgewichtige Oberfläche

Web-App auf **ASP.NET Core (.NET 10)**. Das Backend spricht die offizielle
**YouTube Data API v3**, das Frontend ist eine eigene Oberfläche im Stil von
[hytale.com](https://hytale.com/), die von Handy bis Desktop die volle
Bildschirmbreite nutzt. Videos laufen im offiziellen `youtube-nocookie`-IFrame-Player.

## Warum nicht einfach m.youtube.com einbetten?

Geht nicht. YouTube sendet auf `m.youtube.com` den Header `x-frame-options: SAMEORIGIN`,
jeder Browser blockiert das Framing deshalb hart. Der offizielle Embed-Player ist der
einzige von YouTube dafür freigegebene Weg — den nutzt diese App.

## Setup

### 1. API-Key besorgen (kostenlos)

1. Projekt anlegen in der [Google Cloud Console](https://console.cloud.google.com/)
2. **YouTube Data API v3** aktivieren
3. Unter *Anmeldedaten* einen **API-Key** erstellen

Kontingent: 10.000 Einheiten/Tag. Ein Feed-Aufruf kostet 1, eine Suche 100.
Antworten werden serverseitig 10 Minuten gecacht.

### 2. Key hinterlegen

```bash
cd YtMobile
dotnet user-secrets set "YouTube:ApiKey" "DEIN_KEY"
```

Alternativ per Umgebungsvariable:

```bash
export YOUTUBE_API_KEY="DEIN_KEY"
```

### 3. Starten

```bash
./run.sh
```

Danach auf <https://localhost:5099>.

Der erste Aufruf übersetzt den Server vorab in eine native Binärdatei, dafür braucht es
`podman`. Das Bauabbild entsteht einmalig (ein paar Minuten), jeder weitere Bau dauert
rund 27 s — und nur dann, wenn sich am C#-Code etwas geändert hat. Warum überhaupt, steht
unter [„Vorab übersetzt statt zur Laufzeit"](#vorab-übersetzt-statt-zur-laufzeit).

Außerdem braucht es beim ersten Mal ein Entwicklerzertifikat:

```bash
dotnet dev-certs https
```

Ohne Key startet die App trotzdem und zeigt eine Setup-Anleitung statt des Feeds.

Klemmt das Zertifikat, führt ein Umweg zurück auf unverschlüsselt:

```bash
ASPNETCORE_URLS=http://0.0.0.0:5099 ./run.sh
```

#### Warum HTTPS, und was das Zertifikat zu vertrauen kostet

Am Rechner allein wäre HTTP folgenlos — `localhost` gilt Browsern ohnehin als sicherer
Kontext. Über die zweite Adresse, die `run.sh` anbietet, nicht: `https://192.168.…`
ist einer, `http://192.168.…` nicht. Daran hängen die Kachel auf dem Startbildschirm
(das `site.webmanifest` ist genau dafür da) und die Anmeldung, denn Google lässt als
Weiterleitungsziel nur HTTPS zu — einzige Ausnahme ist Loopback. Gemessen kommt dazu,
dass die Anmeldekekse erst über HTTPS als `secure` markiert werden:

```
über http    yt_oauth=…; max-age=600; path=/; samesite=lax; httponly
über https   yt_oauth=…; max-age=600; path=/; secure; samesite=lax; httponly
```

Der Umstieg selbst war folgenlos, nachgemessen über beide Adressen: keine gemischten
Inhalte (alles Fremde ist längst `https` — Fonts, `i.ytimg.com`, `youtube-nocookie.com`),
gleich viele geladene Quellen, keine Skriptfehler, der Player meldet seine Dauer
unverändert zurück, und `SessionStore.RedirectUri` folgt dem Schema von allein. TLS
handelt HTTP/2 aus; der Aufschlag ist ein Handschlag von rund 3 ms, danach kostet keine
Anfrage mehr als vorher (sechs Anfragen auf einer Verbindung: 1,6 ms über HTTP,
4,6 ms über HTTPS).

**`UseHsts` ist bewusst nicht gesetzt.** HSTS auf `localhost` oder einer IP sperrt den
Browser dauerhaft von `http://` aus — die einzige hier wirklich schwer umkehrbare
Nebenwirkung.

**Vertrauen einrichten.** `dotnet dev-certs https --trust` richtet auf diesem System nur
das OpenSSL-Vertrauen ein; für Brave und Chrome fehlt `certutil` aus `nss-tools`, und auf
einem ostree-System hieße das Einschichten und Neustart. Der Umweg kommt ohne beides
aus — `certutil` läuft im Container gegen die eigene Zertifikatsdatenbank:

```bash
CERT=$(ls ~/.aspnet/dev-certs/trust/aspnetcore-localhost-*.pem | head -1)
D=$(mktemp -d); cp "$CERT" $D/dev.pem
podman run --rm -v "$HOME/.pki/nssdb:/nssdb:z" -v "$D:/cert:ro,z" \
  registry.fedoraproject.org/fedora:41 bash -c '
    dnf -y install --setopt=install_weak_deps=False nss-tools >/dev/null
    certutil -d sql:/nssdb -A -t "P,," -n "aspnet-dev-localhost" -i /cert/dev.pem'
rm -rf $D
```

Die Vertrauensstufe muss **`P,,`** sein, nicht `C,,`: Das Entwicklerzertifikat trägt
`CA:FALSE`, taugt also nicht als Aussteller. Mit `C,,` blieb es beim Datenschutzfehler,
mit `P,,` — „vertrauenswürdiger Gegenpart" — lädt die Seite ohne Warnung.

Für Kommandozeilenwerkzeuge gilt derselbe Speicher nicht; `curl` und Co. brauchen

```bash
export SSL_CERT_DIR="$HOME/.aspnet/dev-certs/trust:/etc/pki/tls/certs"
```

**Das Handy bleibt außen vor.** Das Entwicklerzertifikat kennt nur `localhost`,
`127.0.0.1` und ein paar Container-Namen — keine LAN-Adresse. Vom Handy kommt deshalb
eine Namenswarnung, und mit Zertifikatsfehler bietet der Browser das Installieren als
Kachel nicht an. Wer das will, braucht ein Zertifikat, dem das Handy vertraut: eine
eigene kleine CA mit der LAN-Adresse in den Namen, einmalig aufs Handy gebracht — oder
einen Dienst wie Tailscale, der einen echten, öffentlich vertrauten Namen mitbringt.

### 4. Optional: als Dienst beim Anmelden starten

Damit die Seite nicht an einem offenen Terminal hängt, gibt es `ytmobile.service` — eine
systemd-Unit für den Benutzer-Manager. Einmalig einrichten:

```bash
systemctl --user enable --now ~/Website/ytmobile.service
```

Der absolute Pfad ist Absicht: `enable` legt die Verknüpfung in `~/.config/systemd/user/`
selbst an, die Unit bleibt dabei hier im Projekt und damit im Versionsstand. Danach
startet die Seite bei jeder Anmeldung von selbst und wird bei einem Absturz binnen zwei
Sekunden neu gestartet. Für den Alltag:

```bash
systemctl --user status ytmobile
systemctl --user restart ytmobile
systemctl --user stop ytmobile
journalctl --user -u ytmobile -e     # Protokoll, wie es sonst im Terminal stünde
```

`run.sh` bleibt für den Vordergrund — es bricht ab, wenn der Dienst den Port schon hat,
statt Kestrel in eine Ausnahme laufen zu lassen. `bauen.sh` zieht einen laufenden Dienst
nach dem Übersetzen von selbst nach; ohne das liefe weiter die alte Binärdatei.

**Warum kein Container.** Nahe lag er, gebaut wird ja auch in einem. Zur Laufzeit hängt
die App aber an drei Dingen im Home: dem Entwicklerzertifikat, den User-Secrets und dem
Data-Protection-Schlüsselring, der beschreibbar sein muss. Alle drei müssten einzeln
hineingereicht werden — der Prozess wäre getrennt, seine Daten nicht. Dazu käme ein
Laufzeitabbild, das dieselbe Fedora-Fassung sein müsste wie der Wirt, gegen die ohnehin
gebaut wird. Zwölf Zeilen Unit leisten hier dasselbe, ohne diesen Anhang.

Nur beim Anmelden, nicht beim Booten: Der Benutzer-Manager startet mit der ersten
Sitzung. Wer die Seite auch ohne angemeldeten Rechner erreichen will, schaltet einmalig
`loginctl enable-linger` dazu.

### 5. Optional: YouTube-Konto verbinden

Ohne diesen Schritt läuft alles Öffentliche weiter — Trends, Suche, Wiedergabe und
Kommentare lesen. Erst mit ihm kommen Abos, Abo-Feed, Kommentieren, Antworten,
Abonnieren und Bewerten dazu.

Im selben Google-Cloud-Projekt wie der API-Key:

Die Konsole führt das unter **Google Auth Platform** (früher „OAuth consent screen").

1. **Erste Schritte** durchlaufen: App-Name, Support-E-Mail, Nutzertyp **Extern**,
   Kontakt-E-Mail.
2. **Datenzugriff** → „Bereiche manuell hinzufügen":
   `https://www.googleapis.com/auth/youtube.force-ssl` — der eine Bereich deckt Abos,
   Kommentare und Bewertungen zusammen ab. Er gilt als *sensibel*; das angebotene
   Überprüfungsverfahren ist für den Eigengebrauch nicht nötig.
3. **Zielgruppe** → Status auf **Test** lassen und sich unter **Testnutzer** selbst
   eintragen.

   *Warum nicht „In Produktion"?* Weil Google dafür bei einem sensiblen Bereich
   Startseite, Datenschutzerklärung und Nutzungsbedingungen auf einer eigenen, in der
   Search Console nachgewiesenen Domain verlangt — die eine reine `localhost`-App nicht
   hat. Der Knopf „App veröffentlichen" bleibt sonst ausgegraut. Preis des Testnutzer-Wegs:
   **Google lässt die Anmeldung nach 7 Tagen verfallen**, dann einmal neu anmelden. Die App
   erkennt das abgelaufene Token, verwirft die Sitzung und zeigt wieder den Anmelde-Knopf.
4. **Clients** ([direkt](https://console.cloud.google.com/auth/clients)) →
   **Client erstellen** → Typ **Webanwendung**, als Weiterleitungen eintragen:

   ```
   https://localhost:5099/api/auth/callback
   http://localhost:5099/api/auth/callback
   http://127.0.0.1:5099/api/auth/callback
   ```

   Die erste Zeile ist die, die `run.sh` heute benutzt; die beiden anderen decken den
   Rückweg auf unverschlüsselt ab. Der Server baut das Ziel aus Schema und Host der
   Anfrage (`SessionStore.RedirectUri`), es muss also genau eine davon passen — sonst
   antwortet Google mit `redirect_uri_mismatch`.

5. Zugangsdaten hinterlegen und die App neu starten:

   ```bash
   cd YtMobile
   dotnet user-secrets set "Google:ClientId" "…"
   dotnet user-secrets set "Google:ClientSecret" "…"
   ```

Die Anmeldung lebt in einem verschlüsselten Cookie (`yt_session`, HttpOnly), nicht in
einer Sitzungstabelle auf dem Server. Sie übersteht damit einen Neustart der App, und
jeder Browser bleibt für sich. Im Cookie steckt nur das Refresh-Token; die kurzlebigen
Zugriffstoken liegen im Arbeitsspeicher.

## Konfiguration

`YtMobile/appsettings.json`, Abschnitt `YouTube`:

| Schlüssel      | Standard | Bedeutung                              |
| -------------- | -------- | -------------------------------------- |
| `ApiKey`       | *(leer)* | Google-API-Key                         |
| `RegionCode`   | `DE`     | Region für Trends und Kategorien       |
| `Language`     | `de`     | Sprache für Titel und Kategorienamen   |
| `CacheMinutes` | `10`     | Cache-Dauer der API-Antworten          |
| `ShortsCacheMinutes` | `120` | Eigene, längere Dauer für die Shorts-Zeile |
| `RelatedCacheMinutes` | `180` | Eigene, längere Dauer für „Ähnliche Videos" |

Abschnitt `Google` — gehört in `dotnet user-secrets`, nicht in `appsettings.json`:

| Schlüssel      | Bedeutung                             |
| -------------- | ------------------------------------- |
| `ClientId`     | OAuth-Client-ID (Webanwendung)        |
| `ClientSecret` | OAuth-Client-Secret                   |

## Aufbau

```
run.sh                    Startet die native Binärdatei, baut bei Bedarf vorher neu
bauen.sh                  Übersetzt nativ, im Container (siehe „Vorab übersetzt")
YtMobile/
  Program.cs              Minimal-API-Endpunkte, Kompression, statische Dateien
  Youtube/
    YouTubeService.cs     Client für die Data API v3 (rohes JSON, kein SDK)
    Format.cs             Deutsche Labels für Dauer, Aufrufe, Alter
    Dtos.cs               Kompakte Shapes für den Browser
    RequestBodies.cs      Die vier Rümpfe, die an YouTube geschickt werden
  Auth/
    GoogleAuth.cs         OAuth 2.0 mit PKCE gegen Google
    SessionStore.cs       Sitzung im verschlüsselten Cookie, ohne Server-Tabelle
  Json/
    AppJson.cs            Erzeugter Serialisierungsplan (Native AOT kennt keine Reflexion)
    Payloads.cs           Antwortformen der API selbst, nicht der YouTube-Schicht
  wwwroot/
    index.html            Shell mit App-Bar und Tab-Leiste
    css/app.css           Design-System
    js/app.js             Hash-Router und Views
nativ/                    Bauergebnis, nicht im Versionsstand
```

### API

| Endpunkt              | Zweck                                    |
| --------------------- | ---------------------------------------- |
| `GET /api/config`     | Ist ein Key hinterlegt?                  |
| `GET /api/categories` | Kategorien der Region                    |
| `GET /api/feed`       | Trends, optional `?category=&page=`      |
| `GET /api/shorts`     | Shorts der Region für die Shorts-Zeilen  |
| `GET /api/search?q=`  | Videosuche, optional `&page=`            |
| `GET /api/channel/{id}` | Neueste Videos eines Kanals, optional `?page=&shorts=1` |
| `GET /api/video/{id}` | Details inkl. Beschreibung               |
| `GET /api/related/{id}` | Ähnliche Videos für die Seitenspalte   |
| `GET /api/comments/{id}` | Top-Kommentare, optional `?page=`     |
| `GET /api/comments/{id}/replies?parent=` | Antworten auf einen Kommentar |

Nur mit Anmeldung; ohne sie antworten alle mit `401`:

| Endpunkt                        | Zweck                                  |
| ------------------------------- | -------------------------------------- |
| `GET /api/auth/login?return=`   | Weiterleitung zu Google                |
| `GET /api/auth/callback`        | Rückkehr von Google, setzt das Cookie  |
| `POST /api/auth/logout`         | Abmelden und Token bei Google widerrufen |
| `GET /api/home`                 | Personalisierte Startseite, optional `?category=&page=` |
| `GET /api/likes`                | Zuletzt mit „Gefällt mir" bewertet     |
| `GET /api/subscriptions`        | Abonnierte Kanäle                      |
| `GET /api/subscriptions/feed`   | Abo-Feed, optional `?page=`            |
| `GET /api/subscriptions/state/{channelId}` | Abonniert oder nicht        |
| `POST`/`DELETE /api/subscriptions/{channelId}` | Abonnieren, abbestellen |
| `GET`/`POST /api/rating/{id}`   | Bewertung lesen und setzen             |
| `POST /api/comments/{id}`       | Kommentar schreiben (`{id}` = Video)   |
| `POST /api/comments/{id}/reply` | Auf einen Kommentar antworten          |
| `PATCH /api/comments/{id}`      | Eigenen Kommentar ändern (`{id}` = Kommentar) |
| `DELETE /api/comments/{id}`     | Eigenen Kommentar löschen (`{id}` = Kommentar) |

`POST` legt auf einem **Video** an, `PATCH` und `DELETE` greifen einen bestehenden
**Kommentar** an — daher bedeutet `{id}` je nach Verb etwas anderes. Antworten und
Kommentare der obersten Ebene sind für die API dieselbe Ressource, beide Aufrufe gelten
also für beides; ein gelöschter Kommentar der obersten Ebene nimmt seine Antworten mit.
Ändern und Löschen kosten je **50 Einheiten**, so viel wie das Schreiben.

### Personalisierte Startseite

Angemeldet und ohne gewählte Kategorie mischt die Startseite drei Quellen im Verhältnis
**7 : 2 : 1**:

1. **Kanäle der letzten 200 Likes.** `videos.list?myRating=like` liefert die bewerteten
   Videos als vollständige Ressourcen — der Kanal fällt ohne Zusatzaufruf ab. Wie oft in
   einem Kanal geliked wurde, ist sein Gewicht; die 40 meistgelikten steuern je
   `1 + min(4, Gewicht)` ihrer neuesten Uploads bei.
2. **Regions-Trends.**
3. **Abo-Uploads**, die für den Abo-Tab ohnehin im Zwischenspeicher liegen.

Gemischt wird in zwei Durchgängen: erst alles aus den **letzten 14 Tagen**, dann der Rest
nach derselben Regel. Bereits gelikte Videos fallen raus, und je Seite kommt kein Kanal
mehr als dreimal vor. Fällt eine Quelle aus, füllen die anderen auf; ohne Likes bleiben
Abos und Trends, ohne Anmeldung die reinen Trends.

**Ein Kategorie-Chip filtert dieselbe Mischung**, statt auf die rohen Trends dieser
Kategorie umzuschalten. Reihenfolge, Kanaldeckel und Shorts-Trennung bleiben damit
erhalten — sonst sähe die Seite mit Chip völlig anders aus als ohne. Der Filter kostet
kein Kontingent: Die 200 vorbereiteten Videos liegen bereits im Speicher und tragen ihre
Kategorie mit (`snippet.categoryId`, serverseitig, nicht Teil der JSON-Antwort). Bleiben
weniger als eine volle Seite übrig — in Kategorien, in denen man selten etwas liked —,
füllen die Trends dieser Kategorie auf. Abgemeldet bleibt es bei den reinen Trends.

Die Chart-Antworten werden dabei ebenfalls von Shorts befreit, und das ist kein
Randfall: „Film & Animation" bestand im Test aus **69 Shorts und 3 Videos**,
„Entertainment" aus 71 und 1.

### Shorts-Zeilen laden nach

Die Shorts-Erkennung ist der langsamste Teil einer Feed-Antwort. Gemessen:

| | |
| --- | --- |
| HEAD auf `youtube.com/shorts/<id>`, **kein** Short (303) | ~70 ms |
| dasselbe für einen **echten** Short (200) | ~1700 ms |
| 20 Prüfungen nacheinander / gleichzeitig | 33,9 s / 2,9 s — **Faktor 11,8** |
| 47 Prüfungen mit 24 / 64 gleichzeitigen Anfragen | 4,95 s / 2,72 s |

### Was die Umstellung gebracht hat

Gegengemessen mit einem Schalter, der dieselbe Binärdatei wie vor der RSS-Umstellung
laufen lässt (keine Kanal-Feeds, 50er Upload-Seiten, 40er Prüfblöcke): jeder Endpunkt
kalt, abwechselnd alt/neu, damit Netzschwankungen beide gleich treffen. Mediane aus drei
Runden:

| Endpunkt | vorher | nachher | Gewinn |
| --- | --- | --- | --- |
| Trends (alle) | 0,36 s / 7 HEAD | 0,41 s / **0** | ±0 |
| Kategorie „Film & Animation" | 3,90 s / 23 HEAD | 2,44 s / **4** | **+1,5 s** |
| Kategorie „Musik" | 0,21 s / 8 HEAD | 0,32 s / **1** | ±0 |
| Shorts-Zeile der Region | 5,17 s / 32 HEAD | 5,11 s / 32 | ±0 |
| Suche | 3,92 s / 17 HEAD | 3,03 s / **10** | +0,9 s |
| Kanalseite (Shorts-Kanal) | 6,29 s / 44 HEAD | 3,44 s / **9** | **+2,9 s** |
| **Summe** | **19,84 s** | **14,75 s** | **+5,1 s** |

Die Shorts-Zeile der Region ist der eine Pfad ohne Gewinn: Ihre Kandidaten kommen aus
der Suche und stehen selten unter den neuesten 15 ihres Kanals, das Aufwärmen greift dort
also nicht. Sie ist dafür zwei Stunden gecacht.

Aufgewärmt wird nur, was überhaupt ein Short sein kann: Videos über drei Minuten sind
schon durch ihre Dauer entschieden, ihr Kanal-Feed wird gar nicht erst geholt. Auf der
Kategorieseite sank die Zahl der RSS-Abrufe damit von 258 auf 23.

Die Kosten wachsen also mit der Zahl der **Shorts**, nicht der Kandidaten, und
Nebenläufigkeit hilft massiv (`ProbeConcurrency`, Standard 32). Die Messwerte streuen
allerdings stark — dieselbe Kategorie kalt kostete zwischen 2,5 und 8,5 Sekunden.

Verworfen wurde eine Abkürzung über `i.ytimg.com/vi/<id>/oardefault.jpg` (das Bild gibt es
nur für Videos, die nicht 16:9 sind). Es erkennt Shorts in 50 ms, braucht für alles andere
aber 900 ms, und die Unklaren müssten trotzdem noch durch die richtige Prüfung. Über
dieselben 24 Videos: **2,4 s einstufig gegen 3,5 s zweistufig**, über Videos ohne Shorts
0,1 s gegen 1,6 s. Beide Prüfungen gleichzeitig zu starten und die erste eindeutige
Antwort zu nehmen, war ebenfalls nicht schneller.

Deshalb wartet keine Seite mehr darauf:

- **Eine Chart-Seite je Antwort.** Tiefer geblättert wird nur, wenn eine Seite nach dem
  Aussortieren *gar kein* Video übriglässt. Den Rest holt der Browser beim Scrollen nach.
- **Shorts reisen mit jeder Seite mit**, nicht nur mit der ersten. Vorher gingen die
  Shorts der Folgeseiten verloren.
- **Die Zeilen sind Platzhalter.** Jede entsteht sofort versteckt und belegt einen festen
  Abschnitt des gemeinsamen Vorrats (Zeile 1 nimmt Shorts 1–7, Zeile 2 die 8–14 …).
  Wächst der Vorrat, wird die ganze Liste **von oben nach unten** neu gezeichnet — die
  sichtbaren Zeilen stehen damit zuerst. Eine Zeile ohne Nachschub entfernt sich erst,
  wenn der Feed meldet, dass keine Seite mehr kommt.
- Bringt eine Seite gar keine eigenen Shorts — die Trend-Charts ohne Kategorie führen
  zeitweise keine —, springt die Zeile auf `/api/shorts` um, die Shorts der Region.

- **Die persönlichen Feeds prüfen nur, was sie zeigen.** Startseite, Abo-Feed und der
  Kategoriefilter bereiten weiterhin 200 Videos vor, geprüft wird davon aber blockweise
  nur so tief, wie die verlangte Seite reicht (`FeedPage.Judged`, `EnsureAsync`). Die
  erste Seite kostet damit rund 24 statt 200 Prüfungen. Beim Blättern wächst der geprüfte
  Teil mit, und die dabei neu gefundenen Shorts reisen mit der jeweiligen Seite mit.
- **Was nebeneinander laufen kann, läuft nebeneinander.** In `HomeIdsAsync` starten Trends
  und Abo-Feed zusammen mit dem Like-Verlauf, statt danach; die Uploads-Aufrufe laufen zu
  24 statt zu 8 gleichzeitig, und die vier 50er-Blöcke der Anreicherung ebenfalls
  gleichzeitig. Gemessen kosten 32 parallele API-Aufrufe kaum mehr als ein einzelner
  (239 ms gegen 176 ms).

Die Shorts-Urteile selbst gelten 24 Stunden und app-weit.

**Shorts bleiben aus dem Raster heraus.** Die API kennt keinen Shorts-Filter, deshalb
dieselbe Erkennung wie in der Shorts-Zeile: alles bis drei Minuten ist ein Kandidat und
wird gegen `youtube.com/shorts/<id>` verifiziert (200 statt 303). Was dabei als Short
erkannt wird, wandert in die Shorts-Zeile der jeweiligen Seite statt zwischen die
normalen Karten. Das Urteil je Video bleibt einen Tag im Speicher, damit die Prüfung
nicht bei jeder Aktualisierung erneut läuft. Startseite und Abo-Feed speisen ihre
Shorts-Zeile aus den eigenen Kanälen; die abgemeldete Startseite nimmt weiter die
Regions-Shorts aus `/api/shorts`.

Beide persönlichen Feeds werden **einmal bis zu 200 Videos tief** aufbereitet und liegen
dann fertig im Speicher — Blättern kostet danach keinen einzigen Aufruf mehr.

### Kaltstart der Startseite, vorher und nachher

Beide Male am angemeldeten Testkonto (604 Abos) mit den `⏱`-Marken des Servers gemessen:

| Stufe | vorher | nachher |
| --- | --- | --- |
| Trends | 0,09 s | 0,35 s |
| Like-Verlauf | 1,59 s | 1,40 s |
| Uploads von 40 Lieblingskanälen | 0,33 s | 0,13 s |
| **Abo-Feed** | **6,20 s**, 604 Einheiten | **3,29 s**, **0 Einheiten** |
| Anreichern | 0,18 s (200 Videos) | 0,08 s (20) · 0,21 s (200) |
| **Shorts-Prüfung** | **6,68 s** (2 Blöcke) | **0,001 s** (20) · **0 s** (96) |
| **bis zur ersten Zeile** | **~13 s** | **~1,6 s** |

Die Shorts-Prüfung ist damit nicht schneller geworden, sondern **weg**: Über die ganze
Startseite lief keine einzige HEAD-Anfrage mehr, alle Urteile kamen aus den 323 Kanal-Feeds
mit. Der Abo-Feed kostet weiter gut drei Sekunden, steht aber nicht mehr im Weg — er läuft
in der zweiten Stufe, während die ersten Reihen schon dastehen.

### Warmstart: die Arbeit vor die Anfrage ziehen

Was danach übrig blieb, ist keine fehlende Nebenläufigkeit, sondern echte
Datenabhängigkeit: Die vier Seiten des Like-Verlaufs hängen über `pageToken` aneinander,
und welche Kanäle abgefragt werden, steht erst fest, wenn die Likes da sind. Das lässt
sich nicht nebeneinanderlegen, nur **vorziehen**. (Auf den Browser verteilen geht nicht:
`youtube.com/feeds/videos.xml` schickt keinen `Access-Control-Allow-Origin`-Header, die
Kanal-Feeds sind für JavaScript unerreichbar.)

**Die HTML-Seite bringt die Konfiguration mit.** `Shell` in `Program.cs` füllt einen
`__CONFIG__`-Platzhalter aus dem Sitzungs-Cookie — ein reiner Cookie-Zugriff, kein
Netzaufruf. Damit fällt der `GET /api/config` samt seinem Token-Abruf bei Google (~230 ms)
vom kritischen Pfad. `/api/config` bleibt für den Fall nach An- und Abmeldung.

**Und sie stößt den Aufbau an.** Ist jemand angemeldet, startet `Shell` den Aufbau des
Kopfes, während der Browser die Seite noch parst. Die Aufgabe überlebt die Anfrage und
fasst deshalb weder den `HttpContext` noch einen Dienst aus dessen Bereich an: Mit reist
nur das Refresh-Token als Zeichenkette, die Dienste kommen über `IServiceScopeFactory` aus
einem eigenen Bereich. **Eine Sitzungstabelle entsteht dabei nicht** — die Anmeldung lebt
weiter ausschließlich im Browser.

Damit der Warmlauf und die Anfrage des Browsers kurz darauf nicht dieselbe Mischung
zweimal bauen, liegt die **laufende Aufgabe** im Cache, nicht nur ihr Ergebnis — dasselbe
Muster, das `HomeSubsTaskAsync` schon für den Abo-Abruf benutzt.

#### Einmal heißt einmal: die Sperre muss statisch sein

Der erste Anlauf merkte sich die laufende Aufgabe mit einem `TryGetValue`, gefolgt von
einem `Set`. Zwischen beide passt ein zweiter Aufrufer, und genau das trat sofort ein:

```
16:34:05.887  Request starting  GET /api/home
16:34:06.033  ⏱  --- Startseite: Kopf ---     ← Warmlauf
16:34:06.036  ⏱  --- Startseite: Kopf ---     ← /api/home, 3 ms später
16:34:07.205  ⏱  Trends: 1170 ms
16:34:07.259  ⏱  Trends: 1223 ms              ← alles zweimal
```

Drei Millisekunden reichten. Erschwerend kommt hinzu, dass `AddHttpClient<YouTubeService>`
den Dienst **transient** registriert und `SessionStore` **scoped** ist: Ein Sperrobjekt als
Feld träfe nie denselben Konkurrenten, weil jede Anfrage ihre eigene Instanz bekommt.
Geteilt wird nur der `IMemoryCache`. Beide Dienste haben deshalb jetzt eine **statische**
`Lock`, und alle drei Stellen mit diesem Muster gehen darüber — `HomeBuildAsync`,
`HomeSubsTaskAsync` und der Auslöser in `StartHomeRefresh`:

```csharp
private static readonly Lock BuildGate = new();

private Task<T> SharedTaskAsync<T>(string runKey, Func<Task<T>> start, TimeSpan ttl)
{
    lock (BuildGate)
    {
        if (cache.TryGetValue(runKey, out Task<T>? running) && running is not null) return running;
        var task = start();          // läuft synchron bis zum ersten await
        cache.Set(runKey, task, ttl);
        return task;
    }
}
```

Die Sperre wird nie über einen Netzaufruf gehalten: `start()` läuft nur bis zum ersten
`await`, danach ist sie wieder frei.

Dasselbe Rennen lief eine Ebene tiefer beim Zugriffstoken — Warmlauf und `/api/home`
forderten es nebeneinander bei Google an, zwei Anfragen für ein Ergebnis. `SessionStore`
hat dafür jetzt `SharedFreshTokenAsync`. Der 401-Wiederholungspfad (`force: true`) geht
bewusst daran vorbei: Hat Google ein Token gerade abgelehnt, taugt auch ein parallel
laufender Abruf nichts.

#### Was der Warmstart wirklich bringt

Gemessen am kalten Server, angemeldetes Testkonto:

```
16:44:22.991  Request starting  GET /
16:44:23.005  Request finished  GET /                 13.9 ms
16:44:23.007  Token bei Google anfordern        ← 2 ms nach dem Ausliefern
16:44:23.034  Request starting  GET /api/home    ← 27 ms später, hängt sich an
16:44:23.208  ⏱  Warmlauf: Kopf angestoßen
16:44:25.042  Request finished  GET /api/home       2007 ms
```

Zwischen `GET /` und `/api/home` liegen auf dem Rechner nur rund 30 bis 50 ms — der
Browser hat das Skript im Cache und fragt sofort nach. So viel Vorsprung holt der Warmlauf
also heraus, mehr nicht; auf dem Handy, wo das Parsen länger dauert, wird der Abstand
größer. Der große Posten ist nicht der Vorlauf, sondern der weggefallene
`GET /api/config`: eine ganze Runde plus Token-Abruf, vorher in Reihe davor.

Beim zweiten Aufruf, wenn alles steht, kostet die Startseite nichts mehr:

```
16:38:43.489  ⏱  Warmlauf: Startseite steht schon
16:38:43.489  Request finished  GET /                  0.89 ms
16:38:43.514  Request finished  GET /api/home          1.08 ms
```

### Alter Stand sofort, Auffrischung dahinter

Der `FeedPage`-Eintrag hat jetzt zwei Fristen: nach der **weichen** (`CacheMinutes`) gilt
die Mischung als alt, nach der **harten** (das Sechsfache) verschwindet sie. Dazwischen
wird sie unverändert ausgeliefert und daneben erneuert. Damit trifft die volle Bauzeit
niemanden mehr beim Ablaufen.

**Ausgetauscht wird nur ganz oben.** Der Blätterzeiger ist eine Position in genau dieser
Liste (`"24:3"` → `page.Videos.Skip(24)`). Würde sie mitten im Blättern ersetzt, zeigte
dieselbe Zahl auf eine andere Stelle:

```
alte Liste:   [A B C D E F G H …]        Zeiger 3  ->  D E F G …
neue Liste:   [X Y A B C D E F G H …]    Zeiger 3  ->  B C D E …
```

Die fertige Auffrischung wartet deshalb unter `{userKey}|home:neu` und wird erst bei einer
Anfrage mit Zeiger 0 übernommen:

| Anfrage | Verhalten |
| --- | --- |
| Zeiger 0, Auffrischung liegt bereit | übernehmen, ausliefern, zweiten Schlüssel leeren |
| Zeiger 0, Mischung ist zu alt | alte Mischung ausliefern, Auffrischung anstoßen |
| Zeiger > 0 | immer die laufende Liste — nie übernehmen, nie neu aufbauen |

Wer gerade blättert, blättert seine Liste zu Ende; frisch wird es beim nächsten Aufruf von
oben.

Live nachgemessen, angemeldetes Testkonto. Der erste Aufruf traf die Lage, auf die es
ankommt — eine wartende Auffrischung übernehmen **und** gleich die nächste anstoßen:

```
18:33:56.018  GET /api/home                                  ← Zeiger 0
18:33:56.173  ⏱  Startseite: aufgefrischte Mischung übernommen
18:33:56.173  ⏱  --- Startseite: Auffrischung im Hintergrund ---

18:33:56.680  page=24:31   …   18:33:58.694  page=120:54     ← durchblättern

18:34:00.834  ⏱  Anreichern von 200 Videos: 211 ms           ← neue Mischung liegt bereit

18:34:01 … 18:34:28   dieselben fünf Marken, 28 Runden weiter
```

Ab 18:34:00.834 lag die fertige Mischung abholbereit; die folgenden rund 140 Anfragen mit
Zeiger > 0 haben sie **kein einziges Mal** übernommen. Über die sechs echten
Blätterschritte kam **keine Video-Id doppelt**. Die `top`-Bedingung hält also nicht nur
auf dem Papier.

**Vorsicht bei der Prüfmethode.** Zwei Anläufe gingen daneben, beide lehrreich:

1. Mit fester Pause durchblättern reicht nicht — die Liste war nach 2,5 s zu Ende, der
   Neuaufbau braucht 5 s. Der kritische Moment (fertige Mischung liegt bereit, jemand
   blättert weiter) kam gar nicht vor. Es braucht eine zweite Phase, die dieselben Marken
   nachfasst, bis die Auffrischung sicher durch ist.
2. „Dieselbe Marke muss dieselbe Antwort liefern" ist **kein** gültiges Kriterium. Die
   Shorts-Liste wächst beim Blättern, weil `EnsureAsync` nur so tief urteilt, wie die
   verlangte Seite reicht, und `SliceAsync` `page.Shorts.Skip(seenShorts)` zurückgibt:

   ```
   Seite 0  Marke (oben)   24 Raster + 31 Shorts
   Seite 2  Marke 48:31    24 Raster + 23 Shorts
   Seite 5  Marke 120:54   24 Raster +  2 Shorts
   ```

   Dieselbe Marke später abgerufen liefert alles, was seither dazugekommen ist. Der
   Client vergrößert damit über `makeShelf.extend(next.shorts)` seinen Vorrat, und weil
   jede Marke ihren Shorts-Zähler mitträgt, kommt innerhalb einer Blätterstrecke trotzdem
   kein Short zweimal. Prüfen lässt sich also nur **auf Dubletten**, nicht auf
   Antwortgleichheit.

## Vorab übersetzt statt zur Laufzeit

Der Server läuft als **Native AOT**: Statt IL, das die Laufzeit beim Start erst
übersetzt, liegt fertiger Maschinencode in einer einzelnen Datei von 19,7 MB, die ohne
installierte .NET-Laufzeit startet. `./run.sh` ruft sie auf und baut vorher neu, wenn
eine Quelldatei jünger ist als die Binärdatei.

### Gebaut wird im Container

Native AOT bindet die Binärdatei mit `clang` gegen statische Systembibliotheken. Dieses
System ist ostree-basiert — `clang` und die `-devel`-Pakete nachzuziehen hieße
Einschichten und Neustart. `bauen.sh` erledigt es deshalb in einem Fedora-44-Container,
derselben Fassung wie der Wirt, und reicht das SDK aus `~/.dotnet` samt
NuGet-Zwischenspeicher hinein, statt beides im Abbild zu doppeln:

```bash
./bauen.sh      # ~27 s; beim allerersten Mal entsteht davor noch das Abbild
```

Eine Stolperstelle, falls das Abbild einmal neu entsteht: **`libicu` gehört hinein**,
obwohl es nichts übersetzt. Sonst bricht schon das SDK selbst mit „Couldn't find a valid
ICU package" ab, bevor überhaupt etwas passiert — die App setzt
`InvariantGlobalization=false`, weil die deutschen Labels an echten Sortier- und
Formatregeln hängen. Und weil AOT gegen die Systembibliotheken des Bauwirts bindet,
läuft die Binärdatei nur auf gleich alten oder neueren Systemen.

### Was der Umbau am Code kostete

AOT streicht die Reflexion. Alles, was `System.Text.Json` bisher zur Laufzeit über den
tatsächlichen Typ herausfand, muss vorher feststehen:

- **20 anonyme Objekte** auf dem Serialisierungsweg wurden benannte Records
  (`ErrorDto`, `OkDto`, `DeletedDto`, `ConfigDto` in `Json/Payloads.cs`). Der
  Quellcodegenerator sieht anonyme Typen nicht. Nebenbei fiel eine Doppelung weg:
  Shell und `/api/config` bauten dieselbe Form zweimal und mussten von Hand
  gleichgehalten werden — jetzt ist es ein `ConfigDto`.
- **`Results.` → `TypedResults.`**: `Results.Ok(object?)` serialisiert nach *Laufzeittyp*,
  und genau das kann der Analysator nicht beweisen. `TypedResults.Ok<T>` behält den
  statischen Typ.
- **`Json/AppJson.cs`**, der erzeugte Serialisierungsplan. Was dort nicht steht, fällt
  zur Laufzeit aus — und zwar erst beim Aufruf des Endpunkts. Neue Antwortform heißt
  also: neue `JsonSerializable`-Zeile.
- **`SendAuthAsync` nahm `object? body`** und serialisierte darauf. Die vier Rümpfe, die
  diese App überhaupt an YouTube schickt, stehen jetzt benannt in
  `Youtube/RequestBodies.cs`, und die Methode nimmt eine `JsonTypeInfo<TBody>` dazu.
- **`CreateSlimBuilder` lässt zwei Dinge weg**, die hier gebraucht werden: HTTPS
  (`UseKestrelHttpsConfiguration`) und die user-secrets. Ohne das erste startet die App
  auf der `https://`-Adresse gar nicht, ohne das zweite fehlt der API-Key.

Der eigentliche Schalter ist dabei nicht `PublishAot`, sondern **`IsAotCompatible`**: Er
schaltet die Trim- und AOT-Analysatoren schon beim gewöhnlichen `dotnet build` ein. Jede
künftige Zeile, die AOT bricht, meldet sich damit sofort statt erst beim Veröffentlichen
— oder, schlimmer, beim Aufruf.

Das offene Risiko war **Data Protection**: Die Bibliothek verschlüsselt das
Sitzungs-Cookie und instanziiert ihre Entschlüsseler über Typnamen aus einer XML-Datei,
also genau das Muster, das AOT nicht mag. Es hält: Die native Binärdatei liest denselben
Schlüsselring und stellt Cookies mit demselben `CfDJ8…`-Präfix aus, bestehende
Anmeldungen bleiben gültig.

### Was es bringt — und was es kostet

Gemessen auf `http://127.0.0.1`, damit kein Netzanteil mitzählt: Startzeit vom Aufruf
bis zur ersten *beantworteten* Anfrage (nicht bis zur Logzeile — die sagt nichts darüber,
ob schon jemand bedient wird), RSS aus `/proc/<pid>/status` im Leerlauf, Durchsatz über
eine offene Verbindung in Blöcken von 20 000 Anfragen an `/api/config`, nach 5000 zum
Aufwärmen.

|                                     | JIT (`dotnet YtMobile.dll`) | Native AOT       |
| ----------------------------------- | --------------------------- | ---------------- |
| Start bis zur ersten Antwort        | 295–328 ms                  | **43–58 ms**     |
| RSS im Leerlauf                     | 91–92 MiB                   | **43–44 MiB**    |
| Durchsatz, erster Block             | 6960 /s                     | **7180 /s**      |
| Durchsatz, eingependelt             | **8400–8650 /s**            | 6880–7340 /s     |

Also rund **sechsmal schnellerer Start** und **halber Speicher** — und dafür etwa
**15–20 % weniger Durchsatz im Dauerbetrieb**. Das ist keine Überraschung, sondern der
bekannte Handel: Ohne JIT fehlt die dynamische Profiloptimierung. Schön zu sehen ist es
an den Blöcken. AOT liefert vom ersten an dieselbe Rate, der JIT beginnt darunter und
zieht ab dem zweiten Block vorbei:

```
AOT   7183  7337  7229  6890  7007  6883
JIT   6964  8630  8644  8638  8415  8418
```

Eine erste, kürzere Messung hatte AOT mit 43 % *Vorsprung* ausgewiesen. Das war
vollständig ein Aufwärm-Artefakt: 3000 Anfragen reichen nicht, damit der JIT seine heißen
Methoden hochstuft. Steht hier, weil es die naheliegende Falle dieser Art Messung ist.

Für diese App ist der Handel trotzdem richtig herum. Sie bedient einen einzigen Nutzer
und wartet die meiste Zeit auf YouTube; 7000 Anfragen je Sekunde sind eine Zahl, die im
Betrieb nie in die Nähe kommt. Spürbar ist dagegen beides andere: der Start bei jedem
`./run.sh` und die 47 MiB, die nicht belegt werden.

Bezahlt wird mit der Bauzeit — 27 s im Container statt gut 2 s für `dotnet build`.

## Auf Leichtgewicht ausgelegt

- Kein Frontend-Framework, kein Build-Step — brotli-komprimiert ~5 KB JS, ~4 KB CSS
- `content-visibility: auto` auf Karten: Off-Screen-Inhalte werden nicht gelayoutet
- Thumbnails als `mqdefault` (320×180, ~6 KB), `loading="lazy"` + `decoding="async"`;
  nur die Spalte neben dem Player nimmt `hq720` (siehe „Zwei Größen für dasselbe Bild")
- Brotli/Gzip für JSON und Assets
- Serverseitiges Formatieren aller Labels hält das Skript klein
- `prefers-reduced-motion` wird respektiert

### Zwei Größen für dasselbe Bild

Dieselbe Karte steht an zwei sehr verschiedenen Stellen. Gemessen bei 1600 px
Fensterbreite:

| Ort | Datei | geliefert | angezeigt | |
| --- | --- | --- | --- | --- |
| Raster der Startseite | `mqdefault.jpg` | 320×180 | 283×158 | scharf |
| Shorts-Zeile | `frame0.jpg` | 268×480 | 202×360 | scharf |
| Spalte neben dem Player | `mqdefault.jpg` | 320×180 | 446×250 | **1,39-fach hochgerechnet** |

Nur die Spalte lädt deshalb `hq720.jpg` (1280×720): 68 KB gegen 6 KB — für zwanzig
Karten neben dem Video vertretbar, für zweihundert im Raster nicht.

`srcset` mit `sizes="auto"` wäre der elegantere Weg, hielt der Messung aber nicht stand:
Für dieselbe 283 px breite Rasterkarte wählte der Browser einmal `mqdefault` und einmal
`hq720`, je nachdem, ob die gerenderte Breite beim Anstoßen des Ladens schon feststand.
Die Ansicht weiß es dagegen sicher — die Entscheidung fällt dort, wo die Liste gebaut
wird (`feed--rail`).

**`hq720` gibt es nicht zu jedem Video, und das Fehlen sieht nicht nach Fehlen aus:**

```
GET https://i.ytimg.com/vi/jNQXAC9IVRw/hq720.jpg  →  404, 1097 Bytes Bilddaten
```

i.ytimg antwortet mit 404 **und** legt ein graues 120×90-Bild bei. Der Browser lädt das
klaglos, ein `error` gibt es nicht — die Karte zeigte einfach ein graues Rechteck.
Erkennbar ist der Platzhalter nur an seiner Größe, also fällt das Bild bei
`naturalWidth < 200` auf `mqdefault` zurück. Nachgemessen, Spalte bei 446 px:

```
Video mit hq720 : hq720.jpg,     naturalWidth 1280
Video ohne      : mqdefault.jpg, naturalWidth  320   (nach dem Rückfall)
```

### Asset-Versionierung

`index.html` wird nicht statisch ausgeliefert, sondern von `Program.cs` gerendert:
Ein Token aus den Änderungszeiten von `app.css`, `app.js` und `yt-logo.svg` landet
als `?v=…` in deren URLs, die Shell selbst geht mit `Cache-Control: no-store` raus.
Geänderte Dateien werden dadurch zu neuen URLs — ein Browser, der ein Asset einmal
mit langer `max-age` gecacht hat, kann so nicht auf einem alten Stand festhängen.
Deshalb ist `UseDefaultFiles()` bewusst nicht aktiv: sonst würde die statische
`index.html` schon `/` bedienen, bevor der Handler drankommt.

### Shorts erkennen

Die Data API hat keinen Shorts-Filter, deshalb drei Stufen in `ShortsAsync`:

1. `search.list` mit `q=#shorts`, `type=video`, `videoDuration=short` und
   `publishedAfter` (letzte 7 Tage, auf den Tagesbeginn gerundet — ein laufender
   Zeitstempel würde bei jeder Anfrage eine neue URL erzeugen und den Cache leerlaufen
   lassen). Bewusst **ohne** `order=viewCount`: das spült globale Viral-Clips nach oben
   und ignoriert Region und Sprache praktisch komplett.
2. `videos.list` und alles über 3 Minuten wegwerfen — normale Videos, die nur `#shorts`
   im Titel tragen.
3. Ein `HEAD` auf `youtube.com/shorts/<id>`: echte Shorts antworten **200**, alles andere
   **303** nach `/watch`. Dafür muss `AllowAutoRedirect = false` gesetzt sein (sonst ist
   beides 200), und in der EU braucht die Anfrage `Cookie: CONSENT=YES+cb; SOCS=CAI`,
   sonst landet man auf der Zustimmungsseite. Antwortet YouTube unerwartet, fällt die
   Liste auf die ungeprüften Kandidaten zurück statt leer zu bleiben.

Als Vorschaubild dient `frame0.jpg` (268×480, 16–29 KB). `mqdefault` wäre bei Shorts
16:9 mit unscharfen Seitenfüllern, `oardefault` ist mit 96–276 KB zu schwer.

`frame0.jpg` bekommt jeder Short beim Trennen zugewiesen, nicht erst in
`/api/shorts` — sonst zeigt die Zeile die 16:9-Vorschau des Rasters hochskaliert.

Getrennt wird überall gleich: Trends, Kategorien, Kanalseite, persönliche Mischung und
**Suche**. Nur die Suche blättert nicht nach, wenn eine Seite fast nur Shorts hergibt —
eine Chart-Seite kostet 1 Einheit, eine Suchseite 100 und zusätzlich einen Zähler des
Tageslimits für Suchen. Eine Suche nach „shorts" hat deshalb ein leeres Raster und eine
volle Zeile; „keine Treffer" meldet die Seite erst, wenn auch die Zeile leer bleibt.

Der Feed streut die Zeile alle vier Reihen ein. Wie viele Karten das sind, liest
`app.js` über `getComputedStyle` am Grid ab, statt die Spaltenzahl aus dem CSS zu
wiederholen: am Handy also nach vier Karten, am breiten Monitor nach zwanzig.

Der Takt gehört dabei den **Reihen, nicht den Seiten**: Die Zeile steht nach genau vier
vollen Reihen, unabhängig davon, wo eine nachgeladene Seite anfängt oder aufhört. Weil
ein Vielfaches der Spaltenzahl immer auf einer Reihengrenze liegt, bleibt neben ihr auch
nie eine halbe Reihe leer. Abgelesen wird die Spaltenzahl erst, wenn die erste Karte im
Raster steht — an einem leeren Grid löst der Browser `grid-template-columns` nicht auf,
und mit einer geratenen Spalte stünde die Zeile schon nach vier Karten.

Gezählt wird der Abstand **ab der letzten Zeile**, nicht ab dem Anfang des Feeds — nur
so bleiben es auch nach einer nachgereichten Zeile wieder volle vier Reihen bis zur
nächsten.

Nachgereicht wird sie da, wo der Takt sie so bald nicht bringt. „Film & Animation"
besteht zeitweise fast nur aus Shorts und liefert je Chart-Seite ein bis zwei
Raster-Videos; ein Kanal mit überwiegend Shorts genauso. Vier Reihen kämen dort erst
nach vielen Seiten zusammen. Ausgelöst wird das aber erst, wenn das feststeht — Feed zu
Ende, zwei geladene Seiten ohne vier volle Reihen, oder eine Seite ganz ohne
Raster-Video (die Suche nach „shorts") —, **nicht** schon nach der ersten Seite: Weil
die Startseite bewusst kurz anfängt, stünde die Zeile sonst nach einer einzigen Reihe
und der Takt wäre für den Rest der Seite verschoben.

Der Kopf der Startseite bereitet deshalb doppelt so viele Videos vor, wie er zeigt: Ein
Teil davon sind Shorts und wandert in die Zeile, sonst bliebe von den zwei Reihen nur
eine angebrochene übrig.

Der Blätterzeiger der persönlichen Mischung trägt deshalb zwei Zahlen,
`<Videos>:<Shorts>`: Die vollständige Mischung ersetzt die Kopf-Seite und prüft dabei
von vorn; ohne den zweiten Stand stünden deren Shorts ein zweites Mal in der Zeile.

**Und derselbe Austausch muss den Zeiger auch im Raster tragen.** Beide Zahlen sind
Positionen in einer Liste — nur eben in einer, die zwischen der ersten und der zweiten
Seite ausgewechselt wird. Der Kopf entsteht ohne die Abos, die vollständige Mischung mit
ihnen; stiegen sie mitten im Kopfbereich ein, rutschten dessen letzte Videos in der
vollständigen Mischung nach hinten und standen hinter dem Zeiger noch einmal da. Am
Übergang zur zweiten Seite kamen dadurch ein bis zwei Videos doppelt. Der Abo-Topf bleibt
deshalb genau so weit draußen, wie der Kopf reicht (`HeadDepth`, nicht `HeadSize`) — dann
beginnt die vollständige Mischung Platz für Platz genauso wie der Kopf, den sie ablöst.
Im Browser liegt zusätzlich ein Riegel: `feedList` merkt sich die gezeigten Video-IDs und
überspringt Wiederholungen, egal aus welcher Quelle sie kämen.

## Design

Dunkles Navy-Fundament und Typografie nach hytale.com, ohne Gaming-Bezug:
Navy `#151e2c` / `#162039` / `#10161e`, Text `#b7cedd`, Schriften **Lexend**
(Display) und **Nunito Sans** (Fließtext). Akzentfarbe ist YouTube-Rot
(`#ff0000` für Flächen, `#ff5252` für kleinen Text — pures Rot ist als
Textfarbe auf Dunkel zu kontrastarm). Der aktive Kategorie-Chip ist hell statt
farbig, wie bei YouTube selbst.

Das Layout nutzt die volle Bildschirmbreite. Ab 64rem wandert die Navigation aus
der unteren Leiste in die Kopfzeile, und die Watch-Ansicht wird zweispaltig:
Player, Titel, Beschreibung und Kommentare links, weitere Videos des Kanals rechts.
Die Seitenspalte stapelt wie der Hauptfeed, das Thumbnail nimmt also die volle
Spaltenbreite ein. Weil sie beide Grid-Zeilen überspannt und meist höher ist als
Player plus Inhalt, braucht das Layout `grid-template-rows: auto 1fr` — sonst
verteilt Grid den Überschuss auf beide Zeilen und reißt eine Lücke unter den Player.

Der Feed wächst per `auto-fill` mit, ab 92rem sind es feste **fünf Spalten** und die
Karten werden breiter statt zahlreicher. Genau die Zahl ergibt `auto-fill` an dieser
Schwelle ohnehin, der Übergang ist damit nahtlos. Shorts stehen zu siebt pro Zeile.

`wwwroot/yt-logo.svg` ist der offizielle YouTube-Lockup von Wikimedia Commons.
Der Schriftzug ist explizit auf `#fff` gesetzt. In der Originaldatei tragen die
sieben Buchstaben-Pfade ein `style="fill: rgb(40, 40, 40)"` — YouTubes
Light-Mode-Farbe, auf dunklem Grund praktisch unsichtbar. Wichtig: Ein
`style`-Attribut überstimmt das `fill`-Attribut, ein zusätzliches `fill="#fff"`
allein reicht also nicht. Die Inline-Styles sind deshalb entfernt.
Logo und Name sind Marken von Google —
für ein lokales, privates Projekt unkritisch, für alles Veröffentlichte nicht.

### Dunkle Player-Steuerung

Die eingebaute Steuerung des YouTube-Players lässt sich nicht dunkel schalten. Das ist
gemessen, nicht vermutet — hier der Stand.

Seit der Player seine Oberfläche auf Web-Components umgestellt hat, öffnet das Zahnrad
(`icon-button.player-settings-icon`) ein Bottom Sheet für Qualität, Geschwindigkeit,
Untertitel und Tonspur. Dessen Farbe steht fest im Regelwerk, das der Player selbst
nachlädt:

```
.ytSpecBottomSheetLayoutContainer { background-color: rgb(255, 255, 255); … }
```

Keine CSS-Variable, keine `[dark]`-Variante, kein `@media (prefers-color-scheme)` —
`www-player.css` (536 KB) enthält keinen einzigen Treffer für `prefers-color-scheme`,
und zu `ytSpecBottomSheet*` gibt es im Embed nur diese eine, helle Regelfamilie.
Gemessen wurde im Frame selbst, über `Target.attachToTarget` auf den
Out-of-Process-Frame.

Ohne Wirkung blieben:

| Versuch | Ergebnis |
| --- | --- |
| `?theme=dark` | kommt an (`userInterfaceTheme: USER_INTERFACE_THEME_DARK`), färbt aber nur die alten `ytp-`Menüs |
| `color-scheme: dark` am `<iframe>` | erreicht das fremde Dokument nicht |
| `prefers-color-scheme` im Frame emuliert (hell / dunkel / ohne) | Blatt bleibt `rgb(255, 255, 255)` |
| `dark=1`, `dark_theme=1`, `app_theme=dark`, Cookie `PREF=f6=400` | kein `dark`-Attribut am `<html>` des Embeds |
| `dark` bzw. `darker-dark-theme` testweise im Frame gesetzt | Blatt bleibt weiß |

**Wie es die mobile Seite löst:** `m.youtube.com` setzt den Schalter am eigenen
Wurzelelement — gemessen steht dort `<html darker-dark-theme lang="de-DE" …>`. Die
Seite wertet die Systemeinstellung selbst aus und stempelt das Attribut ins eigene
Dokument. Genau das ist im Embed nicht möglich: Das Attribut fehlt dort, und ein
fremdes Origin lässt sich von außen weder umstylen noch mit Attributen versehen. Zu
allem Überfluss liefert der Embed-Build für die Bottom-Sheet-Komponente gar keine
dunkle Regel mit, an der ein Attribut greifen könnte.

**Entscheidung:** Es bleibt bei der Steuerung des Players, samt hellem Blatt. Der
Gegenentwurf — `controls=0` und eine eigene Leiste über den `postMessage`-Kanal —
war gebaut und lief (Abspielen, Sprung, Tempo, Untertitel, Stumme, Vollbild), ist
aber wieder entfernt: Er ersetzt eine vertraute Bedienung durch eine eigene, und
zwei Punkte lassen sich dabei gar nicht nachbauen. Die **Qualität** nimmt der Player
über die API nicht mehr an (`setPlaybackQuality`, `setPlaybackQualityRange` bleiben
nachweislich wirkungslos), und **ob ein Video Untertitel hat**, verrät er erst als
Antwort auf `loadModule('captions')`. Ein helles Menü, das selten aufgeht, wiegt
weniger als eine Steuerung, der zwei Funktionen fehlen.

### Vorschau beim Hovern

Fährt die Maus über ein Thumbnail, startet nach 350 ms ein stummer Player und schiebt
sich über das Standbild. Die Data API kennt keine Vorschau-Clips, also läuft das über
denselben `youtube-nocookie`-Embed.

Der Aufbau ist ungewöhnlich, und zwar aus einem gemessenen Grund: Es gibt **genau
einen** Player für die ganze Sitzung. Er hängt am `<body>`, wird nie umgehängt, nie
entfernt und nach dem ersten Laden auch nie wieder auf eine andere Adresse geschickt.
Über die Karte kommt er allein über `left/top/width/height`, das Video wechselt über
`loadVideoById` auf dem `postMessage`-Kanal der IFrame-Player-API.

#### Warum kein iframe pro Karte

Ein Rahmen je Karte flackert den Mauszeiger. Sobald ein iframe ein Dokument
festschreibt, setzt Chromium die Zeigerform zurück: Die Hand springt für rund 350 ms
auf den Standardpfeil und dann von allein wieder zurück. Das passiert genau 350 ms
nach Hover-Beginn — im Moment des Einhängens, gut eine Sekunde bevor überhaupt ein
Videobild da ist.

Gemessen wurde das nicht am Bildschirmfoto, sondern direkt am X-Server: Brave läuft
auf einem `Xvfb` im Container, `xdotool` fährt den Zeiger, und ein kleiner Wächter
fragt per XFixes das aktuelle Zeigerbild ab. Ein Formwechsel ist damit eindeutig,
unabhängig davon, was sich im Hintergrund bewegt. Vorher/nachher:

```
alt   -863 ms  Pfeil     neu   -865 ms  Pfeil
       +72 ms  Hand             +63 ms  Hand
      +415 ms  Pfeil      ← das Flackern
      +761 ms  Hand
```

Was der Reihe nach ausprobiert und wieder verworfen wurde:

- `pointer-events: none` auf dem Rahmen — verhindert nur die Zustellung von
  Ereignissen, nicht die Cursor-Meldung des fremden Renderers.
- Eine durchsichtige Schutzschicht über dem Rahmen — wirkungslos.
- `visibility: hidden` während der Ladephase — wirkungslos.
- Den Rahmen *unter* das Standbild legen, damit sich das Ziel der Trefferermittlung
  nie ändert — wirkungslos.
- Den Rahmen früh anlegen und erst beim Hovern in die Karte umhängen — Umhängen im
  DOM lädt das Dokument neu, also derselbe Effekt.
- Den Rahmen unsichtbar in der gegenüberliegenden Bildschirmecke laden — flackert
  ebenfalls. Der Ort spielt keine Rolle, es ist wirklich das Festschreiben selbst.

Ein `<div>` oder ein `about:blank`-Rahmen an derselben Stelle flackert dagegen nicht.
Also darf während des Hoverns überhaupt kein Dokument mehr geladen werden — daher der
eine, dauerhafte Player.

#### Die übrigen Details

- Aufgewärmt wird bei der ersten Mausbewegung über dem Feed, nicht erst beim Hovern
  einer Karte: Solange der Zeiger in Bewegung ist, fällt das kurze Zurücksetzen der
  Zeigerform nicht auf. Der Preis ist ehrlich benannt — wer die Maus einmal über den
  Feed bewegt, lädt den Player, auch wenn er nie eine Karte hovert. Auf Touch-Geräten
  passiert das nie, `(hover: hover) and (pointer: fine)` schließt sie aus.
- Geparkt wird der Player unsichtbar (`opacity: 0`) in der Ecke des Sichtfelds, die am
  weitesten vom Zeiger weg ist — aber **innerhalb** des Sichtfelds. Außerhalb drosselt
  Chromium fremde Rahmen auf etwa ein Bild pro Sekunde; gemessen 1 statt 90 rAF-Ticks
  in anderthalb Sekunden. Dort käme der Player gar nicht erst hoch.
- Gestartet wird mit `loadVideoById({ videoId, playmuted: true })`. An diesem Schalter
  hängt, ob ein Pausenzeichen mitten im Bild steht — siehe unten.
- Eingeblendet wird nicht nach einer geratenen Wartezeit, sondern wenn der Player über
  denselben Kanal `playerState: 1` zurückmeldet — plus 350 ms Nachlauf, damit die
  Einblendung ein stehendes Bild erwischt. Der Zustand als Auslöser deckt zugleich den
  Fall ab, dass der Player gar nicht anläuft: Was nicht spielt, wird nicht gezeigt, und
  die Karte behält ihr Standbild. Videos, die eingebettet gesperrt sind, bleiben in
  Zustand `-1` stehen und lösen deshalb nie eine Einblendung aus.
- Stumme Wiedergaben beendet YouTube nach 30 Sekunden. Meldet der Player `playerState:
  0`, beginnt dasselbe Video von vorn; ein Mindestabstand von fünf Sekunden verhindert,
  dass ein sofort endendes Video in eine Ladeschleife läuft.
- Beim Verlassen geht ein `stopVideo` an den Player, kein Entladen: Das Dokument bleibt
  stehen, nur das Video endet.
- Beim Rollen führt ein auf ein Bild gebündelter `scroll`-Handler den Rahmen nach; er
  hängt ja am Sichtfeld, die Karte aber am Dokument.
- `pointer-events: none` bleibt trotzdem nötig, sonst läge der Zeiger über dem Player,
  die Karte bekäme ein `pointerout` und der Klick ginge an den Player statt an den Link.
- Das `pointerout` wird zusätzlich nachgemessen: Chromium meldet den Übertritt in einen
  fremden Frame als `relatedTarget: null`, was wie ein Verlassen der Karte aussieht.
  Liegt `clientX/clientY` noch im Rechteck der Karte, bleibt die Vorschau stehen.
- Gefiltert wird über `pointerType === 'mouse'`. Ein Tap erzeugt ebenfalls ein
  `pointerover`, aber mit `pointerType: 'touch'`; auf dem Handy startet also nie etwas
  von allein.
- Immer nur eine Vorschau gleichzeitig, und `prefers-reduced-motion` schaltet sie ab.
- Der Rahmen ragt oben und unten je 96px über die Karte hinaus und wird abgeschnitten.
  `controls=0` entfernt nur die Bedienleiste, nicht Titelzeile und Branding — die
  zeichnet der Player an seine eigenen Ränder. Das Videobild bleibt dabei vollständig:
  Im höheren Rahmen wird es auf die Breite gerechnet und senkrecht zentriert, landet
  also genau im sichtbaren Ausschnitt. Ein Zoom hätte stattdessen Rand gekostet.

Nicht einbettbare Videos bekommen erst gar keinen Vorschau-Haken — `status.embeddable`
steht ja schon in der Feed-Antwort.

#### Das Pausenzeichen in der Bildmitte

Über jeder Vorschau stand für knapp vier Sekunden ein runder Knopf mit Pausenzeichen
mitten im Bild. Das ist YouTubes neue Mitte-Steuerung, ein Web-Element namens
`<player-middle-controls>` mit dem Knopf `player-control-play-pause-icon`, 124 × 56
Pixel, exakt zentriert. Sie erscheint bei **jedem** Start, bekommt nach etwa drei
Sekunden `ytp-autohide` und ist nach knapp vier Sekunden weg.

Was daran nicht hilft, alles nachgemessen: `controls=0` schaltet nur die Leiste unten
ab (`ytp-hide-controls`), nicht die Mitte. Ebenso wirkungslos waren `modestbranding`,
`showinfo=0`, der Verzicht auf `enablejsapi`, `youtube.com` statt `youtube-nocookie`,
`loop`, `start`, drei verschiedene Playergrößen und alle drei Startwege (`autoplay=1`
in der Adresse, `loadVideoById`, Start auf einem schon laufenden Player).

Gelöst wird es über den Player-Var **`playmuted`**:

```js
loadVideoById({ videoId: id, playmuted: true })
```

Damit läuft das Video in YouTubes stummer Autowiedergabe — demselben Modus, in dem
auch YouTubes eigene Vorschau über den Vorschaubildern läuft, und der kommt ohne jede
Bedienoberfläche im Bild aus. `loadVideoById` reicht Zusatzangaben nur in der
Objektform durch; `playmuted` steht auf YouTubes eigener Liste erlaubter Schlüssel.
Die dokumentierte Methode `mutedAutoplay()` täte dasselbe, prüft aber vorher
`embedsHostFlags.allowMutedAutoplayDurationMode` und tut von einer beliebigen Adresse
aus deshalb nichts.

Der Preis ist die 30-Sekunden-Grenze für stumme Wiedergaben, die oben über
`playerState: 0` abgefangen wird. Videos, die sich ohnehin nicht abspielen lassen,
verhalten sich mit `playmuted` genauso wie ohne — sie bleiben in Zustand `-1` stehen,
und die Vorschau wird gar nicht erst sichtbar.

### Tastenkürzel

`0`–`9` springen auf das jeweilige Zehntel der Laufzeit. Der Befehl geht als
`postMessage` an den Player, in der Nachrichtenform der IFrame-Player-API:

```js
{ event: 'command', func: 'seekTo', args: [sekunden, true] }
```

Dafür braucht die Embed-URL `enablejsapi=1` und ein passendes `origin`; ohne das weist
der Player die Nachricht ab.

Das Player-JS von Google wird bewusst **nicht** geladen; die knapp 100 KB lohnen für
eine Handvoll Befehle nicht, und das Nachrichtenformat ist stabil dokumentiert.

#### Warum die Seite den Fokus an sich zieht

Der eingebettete Player beantwortet fast alle Kürzel selbst — die Zifferntasten aber
nicht: `0`–`9` gibt es nur auf der YouTube-Seite, nicht im Embed. Gemessen im echten
Browser, Fokus im Player:

| Taste | erwartet | gemessen |
| --- | --- | --- |
| `→` | +5 s | `0,9 → 11,6` ✓ |
| `k` / Leertaste | Pause | läuft → pausiert ✓ |
| `m` | stumm | stumm ✓ |
| `j` | −10 s | `15,9 → 6,9` ✓ |
| `5` | Sprung auf 50 % | `6,9 → 8,6` — nur weitergelaufen ✗ |

Solange der Player den Fokus hat, sieht die Seite die Tasten nicht: Über eine
Origin-Grenze werden keine Tastenereignisse gereicht, und geteilt wird der Fokus
nicht. Entweder gehört die Tastatur dem Player oder der Seite. Weil ein Klick auf
„Play" den Fokus in den iframe legt, waren die Ziffern im normalen Ablauf damit tot.

Deshalb nimmt die Seite den Fokus zurück, sobald er in den Player wandert
(`window`-`blur` → `frame.blur()`, abgesichert mit `document.hasFocus()`, damit ein
Fensterwechsel nicht mitgerissen wird). Die Kürzel, die dem Player dadurch entgehen,
reicht sie ihm als Befehl weiter:

| Taste | Wirkung | Befehl |
| --- | --- | --- |
| `0`–`9` | Zehntel der Laufzeit | `seekTo` |
| `←` / `→` | ∓5 s | `seekTo` |
| `j` / `l` | ∓10 s | `seekTo` |
| `k`, Leertaste | Pause / Weiter | `pauseVideo` / `playVideo` |
| `m` | stumm | `mute` / `unMute` |
| `f` | Vollbild | `requestFullscreen` auf dem iframe |

Sichtbar ändert sich dadurch nichts — die Steuerleiste bleibt YouTubes eigene.

Für die relativen Sprünge muss die Seite wissen, wo das Video gerade steht. Fragen
ließe sich der Player nur asynchron, für einen Tastendruck zu langsam; also merkt sie
sich die letzte `currentTime` aus den `infoDelivery`-Meldungen samt Zeitstempel und
rechnet zwischen zwei Meldungen weiter. Ohne dieses Weiterrechnen springt „+5 s" mal
vier und mal sechs Sekunden weit. Nach jedem Sprung zieht die Schätzung sofort mit,
damit zwei schnelle Tastendrücke sich addieren, statt beide vom selben Stand zu rechnen.

Die Laufzeit für die Ziffern kommt anfangs aus dem vorhandenen `durationLabel` — das
spart einen Round-Trip und trägt den ersten Tastendruck. Sobald der Player seine eigene
Dauer meldet, gilt die, weil sie genauer ist. Livestreams haben kein Label; dort
passiert bei den Ziffern nichts.

**Preis dafür:** In den Menüs des Players (Zahnrad) führen die Pfeiltasten nicht mehr
durch die Einträge, und in den Player hineintabben geht nicht. Mit der Maus ist alles
unverändert bedienbar. `c` (Untertitel) und `<`/`>` (Tempo) entfallen als Kürzel — über
`loadModule` ist der Untertitel-Zustand nicht zuverlässig zu treffen (siehe „Dunkle
Player-Steuerung"), und die Schaltflächen im Player tun beides weiterhin.

Eingabefelder sind ausgenommen, sonst ließe sich nichts mehr tippen; die Leertaste
zusätzlich für alles, worauf der Fokus gerade liegt, sonst löst sie es nicht mehr aus.
`↑`/`↓` bleiben beim Scrollen der Seite — wie auf youtube.com auch, wo sie die
Lautstärke nur bei fokussiertem Player verstellen.

#### Wann der Fokus zurückgeholt wird

Sofort zurückzugreifen war ein Fehler und hat den **Lautstärkeregler unbrauchbar**
gemacht: Ein Mausdruck im Player gibt dem Rahmen den Fokus, das Elternfenster bekommt
dadurch `blur` — und der Griff nahm ihn mitten im Ziehen wieder weg. Am Ziehen der
Zeitleiste fiel das nicht auf, am Regler schon.

Warten muss der Griff auf ein Signal, das die Seite auch wirklich bekommt. Gemessen,
per CDP mit gefahrenem Zeiger:

```
außerhalb des Players  : 5 Bewegungen gesehen
im Player              : 0 von 20 Bewegungen gesehen
mouseenter/mouseleave am <iframe>: feuern überhaupt nicht
```

Über dem fremden Rahmen ist die Seite also **blind** — und genau das ist die Auskunft.
Eine Mausbewegung, die hier ankommt, beweist, dass der Zeiger den Player verlassen hat.
Der Griff merkt sich beim `blur` nur, dass er noch aussteht, und fasst bei der nächsten
solchen Bewegung zu.

Auf die Mausbewegung allein zu warten war allerdings zu langsam: Wer die Lautstärke
verstellt und dann tippt, bewegt die Maus oft gar nicht mehr. Der Player liefert das
bessere Signal gleich mit. Gemessen an seinen `infoDelivery`-Meldungen:

```
Felder der laufenden Meldungen : currentTime, videoBytesLoaded, progressState, …
nach setVolume                 : volume: 30, danach wieder null
```

`volume` steht **nur** in der Meldung, wenn sich die Lautstärke gerade geändert hat.
Damit ist „am Regler wird gezogen" direkt ablesbar. Der Griff fasst deshalb 500 ms nach
der letzten solchen Meldung zu — und der Klick selbst zählt als Geste, weil ein Druck
auf den Regler die Lautstärke sofort ändert. Eine Mausbewegung außerhalb des Players
zieht ihn zusätzlich vor.

**Die erste Meldung eines Players ist aber keine Geste.** Sie ist sein Zustandsbericht
und trägt Lautstärke und Stummschaltung immer mit sich, ganz gleich, ob jemand etwas
verstellt hat; ihre Stelle steht außerdem gegen eine Schätzung, die es noch gar nicht
gibt. Zählte man sie mit, schob jede dieser Meldungen den Griff um weitere 500 ms nach
hinten, und nach einem Klick ins Video dauerte es ein bis zwei Sekunden, bis die
Zifferntasten wieder etwas taten. Als Geste gilt deshalb nur, was sich gegenüber einem
**schon bekannten** Stand bewegt: die erste Meldung normiert bloß ein, die zweite kann
eine Geste sein. Übrig bleibt der halbe Nachlauf des Klicks selbst.

Nachgemessen, per CDP:

```
vor dem Klick, Fokus im Player  : False
direkt nach dem Klick           : True
0,9 s später, ohne Mausbewegung : False   <- die Kürzel sind zurück
mitten im Regeln                : True    <- Ziehen bleibt unangetastet
0,8 s nach der letzten Änderung : False
```

Der naheliegende Gegenentwurf über `mouseenter`/`mouseleave` am Rahmen scheidet
nachweislich aus, die Ereignisse gibt es nicht.

#### Im Vollbild gilt dasselbe — eine Ausnahme war ein Fehler

Eine Weile stand hier das Gegenteil: Im Vollbild fasse der Griff gar nicht zu, dort
gebe es keine Seite drumherum, für die Tasten abzufangen wären. Der zweite Halbsatz
stimmt, der Schluss daraus nicht. Die Ziffern kennt nur die große YouTube-Seite, der
Embed beantwortet sie **nicht** — lag der Fokus im Rahmen, waren sie im Vollbild also
tot. Und ob er dort lag, hing daran, wo der letzte Klick hingegangen war. Genau daher
kam das „funktioniert nur noch unzuverlässig".

Nachgemessen am Prüfstand, beide Male derselbe Weg: Vollbild, Klick ins Video, dann
Taste `5` bei einem 521 s langen Video.

| | `activeElement` nach dem Klick | Taste kommt im Dokument an | Stelle danach |
| --- | --- | --- | --- |
| mit der Ausnahme | `IFRAME` | — | 0,0 s |
| ohne die Ausnahme | `BODY` | `5` | **260,5 s** — exakt die Hälfte |

Die Ausnahme ist ersatzlos weg; Vollbild und Fenster laufen durch dieselbe Funktion.
Der Gestenschutz bleibt derselbe, der oben gemessen ist — und ein Sprung in
`currentTime` zählt jetzt ebenfalls als laufende Geste, nicht mehr nur `volume`. Über
dem fremden Rahmen ist das das einzige Zeichen für einen Zug an der Zeitleiste, und im
Vollbild erst recht: Dort kommt nicht einmal eine Mausbewegung an.

### Der Mauszeiger im Vollbild

Im Vollbild soll der Zeiger nach kurzer Ruhe verschwinden. Der naheliegende Weg —
`cursor: none`, bei Bewegung zurück — funktioniert in Chromium **nicht**, und zwar aus
einem Grund, der weit außerhalb dieser App liegt.

#### Warum `cursor: none` nichts ausrichtet

Chromium zeichnet den Zeiger erst neu, wenn er sich bewegt: Die Zeigerform fällt allein
in `EventHandler::selectCursor`, und das läuft nur an echten Mausereignissen
([Issue 41031275](https://issues.chromium.org/issues/41031275), offen). WebKit hat es
behoben, indem es die Auffrischung in `Document::updateLayout` zieht
([PR 40887](https://github.com/WebKit/WebKit/pull/40887)); Firefox löst dasselbe, indem
es beim Vollbildwechsel intern einen `mousemove` schickt
([Bugzilla 1273091](https://bugzilla.mozilla.org/show_bug.cgi?id=1273091)). Chromium tut
weder das eine noch das andere.

Damit beißt sich jede Eigenbaulösung selbst in den Schwanz: Die Bewegung, die das
Ausblenden endlich zeichnen würde, ist genau die Bewegung, bei der wieder eingeblendet
werden muss.

Nachgemessen ist das nicht am berechneten Stil — der sagt nur, dass die Regel greift —
sondern am **gezeichneten Zeigerbild**: Xvfb im Container, Chromium mit Fenster,
`xdotool` fährt eine echte Maus, `XFixesGetCursorImage` liefert das Bild, gezählt werden
Pixel mit Alpha > 0. Ein Pfeil hat 272, eine Hand 335, ein leerer Zeiger 0.

| Versuch (Zeiger ruht) | still | nach 2 px |
| --- | --- | --- |
| Klasse am `body`, Element einfügen, `dialog.showModal()`, Element entfernen, Element tauschen | 272 | 0 |
| `void offsetHeight`, `cursor` erst `auto` dann `none`, in `requestAnimationFrame`, `display` aus/an | 272 | 0 |
| Seite scrollen, Container scrollen, `zoom`-Anstupser, synthetischer `mousemove`, `setPointerCapture`, durchsichtiger eigener Zeiger, Dialog zu/auf | 272 | 0 |
| Gehovertes Element wegnehmen (`remove`, `display`, `visibility`, `cursor`) — dabei feuert **kein** Ereignis | 272 | 0 |
| Modaler `<dialog>` über dem Vollbild-Rahmen: blind, `crosshair`, gestapelt | unverändert | — |
| Vollbild dem Rahmen wegnehmen und der eigenen Hülle geben | unverändert | — |

Zwanzig Wege, keiner trägt. Der `crosshair`-Versuch ist der aufschlussreichste: Die
Elternseite *bekommt* den Übertritt gemeldet (`mouseout IFRAME`, `mouseover DIALOG`) —
auf den Bildschirm kommt ihre Zeigerform trotzdem nie.

#### Warum YouTube es im Embed selbst nicht tut

Auf youtube.com blendet der Player den Zeiger mit aus. Im Embed nicht, und das liegt
nicht am Vorsatz:

```
autohide: true | cursor am Player: none | unter dem Zeiger: DIV.player-controls-background → auto
```

YouTube setzt `cursor: none` am Player — darüber liegt aber ein eigenes Feld mit
`cursor: auto` und schlägt es. Der Embed fährt nämlich YouTubes **mobilen** Player
(`ytm…`, `icon-button fullscreen-icon`), nicht den `ytp-`Desktop-Player, und **keine**
Parameter-Variante ändert das: nicht ohne `playsinline`, nicht ohne `modestbranding`,
nicht mit nacktem `enablejsapi`, nicht über `youtube.com` statt `youtube-nocookie.com`.

Nebenbei belegt dieselbe Messung, dass ein Zeiger sich sehr wohl ohne Bewegung neu
zeichnen lässt: Als YouTubes Leiste ausblendete, sprang der gezeichnete Zeiger von der
Hand (335) auf den Pfeil (272), ohne dass die Maus sich rührte. Nur eben nie auf `none`.

#### Was trägt: Pointer Lock

Bei Pointer Lock blendet der **Browser** den Zeiger aus. Kein Stil, kein Neuzeichnen,
keine Bewegung nötig. Gemessen im Vollbild, das über YouTubes eigenen Knopf begonnen hat:

```
Sperren, ohne jede Nutzergeste  → ok,  Zeigerpixel 0
Mausort vor/nach dem Sperren    → unverändert (x:802 y:452)
Bewegung kommt an               → mousemove 2,-2 · 5,0 · 6,0
Lösen                           → Zeiger sofort zurück, an derselben Stelle
Vollbild danach                 → IFRAME, unberührt
Esc                             → löst Sperre UND Vollbild in einem Zug
```

Eine Nutzergeste verlangt Chromium nur außerhalb des Vollbilds; im Vollbild gelingt die
Sperre auch bei `navigator.userActivation.isActive === false`. Ein Hinweisballon
(„Zum Beenden Esc drücken") erschien im Prüfstand nicht.

Der Ablauf ist dadurch schlicht:

1. Vollbild beginnt → drei Sekunden Ruhezeit.
2. Ruhezeit abgelaufen → sperren. Zeiger weg.
3. Erste Bewegung — die kommt jetzt bei uns an, weil die Sperre sie liefert — → lösen.
   Zeiger zurück, an derselben Stelle, YouTube sieht die Maus wieder.

Über dem fremden Rahmen ist Schritt 3 überhaupt nur so möglich: Ohne Sperre sieht die
Seite dort keine einzige Bewegung, gemessen 0 von 20.

#### Der Zug am Regler

Ein Zug an Zeitleiste oder Lautstärke kann länger als drei Sekunden dauern, und die
Seite sieht davon nichts. Zwei Zeichen aus dem Player halten die Ruhezeit deshalb am
Laufen: eine Meldung mit `volume` (die kommt nur, wenn sich etwas ändert — dasselbe
Zeichen, das schon den Fokusgriff steuert) und ein Sprung in `currentTime` gegen die
eigene Schätzung, also ein Spulen.

Nachgestellt mit vier Sekunden gedrückter Maustaste an der Zeitleiste im Vollbild:

```
während des Zugs gesperrt : False
Stelle vor/nach           : 22,1 s → 26,5 s
Protokoll                 : Sperre liegt → 19 ms später „Bewegung → freigeben"
```

Die Sperre fasst zwar zu, wird aber von der nächsten Bewegung nach 19 ms wieder gelöst —
gut eine Bildwiederholung. Der Zug läuft durch. Sichtbar ist davon nichts.

#### Warum die Sperre nicht im Takt zufassen darf

Jedes Sperren nimmt dem Fenster die Hardware-Zeigerebene, und das Videobild fällt dabei
kurz aus seiner Overlay-Ebene — sichtbar als Flackern, das wie ein VRR-Aussetzer
aussieht. Einmal beim Ausblenden ist das kein Thema; im Takt von drei Sekunden schon.

Und genau dazu neigt der Aufbau: Solange **nicht** gesperrt ist, sieht die Seite über
dem fremden Rahmen keine Bewegung — sie muss sperren, um nachzusehen. Ein zitternder
Mauszeiger löste die Sperre sofort wieder, drei Sekunden später fasste sie erneut zu.

Der erste Versuch, das über eine Wegstrecke abzufangen (acht Pixel aufsummiert), war
falsch: Solange gesperrt ist, **steht der Zeiger still**. Was bis zum Lösen an Bewegung
anfällt, ist verloren, und acht Pixel sind bei langsamer Führung mehrere Ereignisse —
das fühlt sich an wie eine Maus mit zu wenig Bildern je Sekunde. Die Schwelle muss klein
sein.

Zittern lässt sich nicht am Weg erkennen, wohl aber an der **Richtung**: Es pendelt um
null, echte Bewegung nicht. Gelöst wird deshalb

- bei einem einzelnen Ereignis über drei Pixel (`|dx| + |dy| >= 4`) — so fängt jede
  zügige Bewegung an, und die Sperre löst im ersten Ereignis;
- oder bei einem aufsummierten **Versatz** von drei Pixeln, mit Vorzeichen gerechnet und
  nach 200 ms Stille wieder verworfen — das trägt die langsame Führung.

Die Vier ist gemessen, nicht geraten: Ein Zittern von einem Pixel **diagonal** ergibt
`|dx| + |dy| == 2`, und mit einer Schwelle von zwei löste die Sperre prompt wieder aus.

Dazu wächst die Wartezeit um zwei Sekunden (höchstens neun), wenn eine Sperre keine
Sekunde gehalten hat — dann war die Maus wirklich in Bewegung. Hält sie länger, gelten
wieder drei Sekunden.

Nachgemessen mit demselben Code im Prüfstand, im Vollbild über YouTubes eigenen Knopf:

| Lage | Übergänge | verschluckte Bewegung |
| --- | --- | --- |
| Maus völlig still, 12 s | keine, bleibt gesperrt | — |
| Zittern ±1 px diagonal, 12 s | keine, bleibt gesperrt | — |
| langsam ziehen | löst | 3 px in 4 Ereignissen |
| zügig bewegen | löst | 20 px in 2 Ereignissen |

Die Ereigniszahl ist das Maß, nicht die Zeit: Der Prüfstand fährt die Maus in groben
Schritten, eine echte Maus meldet 125- bis 1000-mal je Sekunde. Vier Ereignisse sind
dort wenige Millisekunden.

#### Der Wecker darf nicht verstummen

Der Aufbau hat eine einzige Schwachstelle, und sie erklärt, warum der Zeiger sich lange
**unstet** anfühlte: mal verschwand er, mal für den Rest des Films nicht mehr.

Angestoßen wird das Ausblenden allein von einem Wecker (`setTimeout`), und der stellt
sich nicht von selbst neu. Neu gestellt wird er nur an vier Stellen: Vollbild beginnt,
Sperre gelöst, Sperre fehlgeschlagen, Geste im Player. Wer beim Wecken **stumm
zurückkehrt**, hat ihn damit abgeschaltet — bis zufällig eines dieser vier Ereignisse
kommt. Genau das taten zwei Ausgänge:

- `document.hidden || !document.hasFocus()` — der Reiter liegt im Hintergrund oder ein
  anderes Fenster hat den Fokus. Bei zwei Bildschirmen ist das der Alltag: einmal
  daneben geklickt, und das Vollbild auf dem anderen Schirm blendete nie wieder aus.
- Die abgewiesene Sperre. `requestPointerLock()` gibt in neueren Browsern ein Promise
  zurück; dessen `catch` verschluckte den Fehlschlag. Ob dabei zusätzlich
  `pointerlockerror` feuert — der einzige Pfad, der neu gestellt hätte —, hängt am
  Browser, und darauf darf man sich nicht verlassen.

Beide setzen jetzt den Wecker neu, statt aufzugeben. Doppeltes Ansetzen ist dabei kein
Problem: `zeigerNeuStarten` löscht immer erst den alten. Dazu stellen `focus` und
`visibilitychange` ihn beim Zurückkommen sofort, damit nicht noch der Nachlauf des
vergeblichen Versuchs abzuwarten ist.

Ein dritter Weg führte zum selben Bild, kam aber aus dem Player. Ein Sprung in
`currentTime` gilt als Spulen und hält die Ruhezeit am Laufen — gemessen wurde er gegen
die **Schätzung** (`playerTime`), die zwischen zwei Meldungen weiterrechnet. Steht
`state` einmal falsch auf „läuft", oder läuft die Wiedergabe nicht mit einfachem Tempo,
dann wächst dieser Abstand von selbst über die Schwelle: Alle anderthalb Sekunden meldete
sich ein „Spulen", das nie stattgefunden hat, und stellte den Wecker zurück — der Zeiger
blieb, solange das Video lief. Gemessen wird jetzt gegen den **kleineren** der beiden
Abstände, zur Schätzung und zur zuletzt gemeldeten Stelle. Ein echtes Spulen springt
gegen beide, ein Rechenfehler in der Schätzung gegen keinen von beiden.

Was bleibt, ist gewollt und trotzdem spürbar: Die Wartezeit steht zwischen drei und neun
Sekunden, je nachdem, wie viel die Maus zuletzt in Bewegung war. Kürzer geht es nicht,
ohne das Flackern zurückzuholen — solange nicht gesperrt ist, sieht die Seite über dem
fremden Rahmen keine Bewegung und kann die Ruhe nur durch einen Sperrversuch prüfen.

#### Preis, ehrlich benannt

Ein Klick, dem **keine** Mausbewegung vorausging, landet bei uns statt beim Player. Er
löst die Sperre und wird als Play/Pause weitergereicht, damit er nicht ins Leere geht
(nachgestellt: Video pausiert → läuft). Wer zum Klicken die Maus bewegt — praktisch
immer — merkt davon nichts, denn jede Bewegung löst die Sperre vorher.

#### Was am Ende doch nicht die Seite war

Danach blieb ein Rest: Der Zeiger fühlte sich im Vollbild an, „als hätte die Maus wenig
FPS". Naheliegend war, weiter an den Schwellen zu drehen. Falsch.

Eine Bildschirmaufnahme (2560×1440, 30 B/s) hat es entschieden. Der Pfeil ist ein
schwarzes Dreieck mit weißem Rand — als Vorlage über jedes der 401 Bilder geschoben,
ergibt das eine lückenlose Spur:

| Messung | Ergebnis |
| --- | --- |
| Bilder im Vollbild | 309 |
| davon mit sichtbarem Zeiger | **alle 309** |
| Bilder ganz ohne Zeigerbewegung | 6 — alle an echten Halten der Hand |
| Schrittlänge gegen die Nachbarschritte | Median 14 %, oberstes Zehntel 31 % |
| Fortschrittsleiste des Players | durchgehend gleich hell |

Damit fällt die Sperre als Erklärung aus — sie hat in diesen 13 Sekunden nie zugefasst —
und ein Zeiger, der nur mit 15 oder 20 Hz nachgeführt würde, stünde in jedem zweiten bis
dritten Bild still. Tut er nicht. Das Ruckeln sitzt also feiner als 33 ms, und genau da
ist eine 30-Bilder-Aufnahme blind.

Die Ursache lag außerhalb des Browsers:

```
Sitzung : Wayland, KDE      Brave: --ozone-platform=wayland
DP-1    : 2560x1440 @ 164,8 Hz   Adaptive Sync: Never
DP-2    : 2560x1440 @ 170,0 Hz   Adaptive Sync: Automatic
```

Auf dem Schirm mit **VRR auf „Automatisch"** folgt die Bildwiederholrate im Vollbild dem
Inhalt. Der Videoinhalt wechselt in praktisch jedem Aufnahmebild, läuft also mit 30 oder
60 fps — der Schirm geht von 170 Hz auf diese Rate herunter, und **der Zeiger wird nur
so oft neu gezeichnet wie der Schirm**. Auf dem zweiten Schirm, VRR aus, ist dieselbe
Seite im Vollbild sauber. Bestätigt.

Das ist kein Fehler der Seite und keiner des Players, und es lässt sich von hier aus
auch nicht beheben. Es steht hier, damit die nächste Runde nicht wieder an Schwellen
dreht, die nichts damit zu tun haben.

### Kein Rasterkind über seine Spur

Beim schnellen Schmalerziehen des Fensters lief die Seitenspalte der Watch-Ansicht aus
dem Bild — mit waagerechtem Rollbalken und einem stark vergrößerten Vorschaubild. Die
Ursache ist das Zusammenspiel zweier für sich harmloser Dinge:

`content-visibility: auto` überspringt Karten und Kommentare außerhalb des Sichtfelds
und benutzt für sie eine Ersatzgröße. Mit `contain-intrinsic-size: auto …` ist das die
**zuletzt gerenderte** Größe — also die aus dem breiteren Fenster von vorhin. Ein
Rasterkind ist zugleich von Haus aus mindestens so breit wie die Mindestbreite seines
Inhalts, und eine `1fr`-Spur bedeutet `minmax(auto, 1fr)`, deren Untergrenze ebenfalls
diese Mindestbreite ist. Beides zusammen hält die alte Breite fest:

```
Fenster 1600 -> Spalte 28rem = 448px, Karten rendern mit 448
Fenster 1509 -> Regel sagt 24rem = 384, Karten merken sich aber 448
              -> Spur 384, Karte 448, scrollWidth 1526 bei innerWidth 1509
```

Gemessen per CDP über eine Breitenreihe, mit Gegenprobe: Mit
`content-visibility: visible` verschwindet der Überlauf an jeder Breite — das Merken
ist wirklich der Auslöser.

Die Behebung stellt nicht `content-visibility` ab, sondern nimmt der Mindestbreite den
Hebel. Überall dort, wo ein Raster Kinder mit gemerkter Größe trägt:

| Stelle | vorher | nachher |
| --- | --- | --- |
| `.watch-layout` | keine Spaltenangabe (implizite `auto`-Spur) | `minmax(0, 1fr)` + `> * { min-width: 0 }` |
| `.feed--rail` | `1fr` | `minmax(0, 1fr)` |
| `.feed` ab 92rem | `repeat(5, 1fr)` | `repeat(5, minmax(0, 1fr))` |
| `.comments` | keine Spaltenangabe | `minmax(0, 1fr)` |
| `.comment` | `2.25rem 1fr` | `2.25rem minmax(0, 1fr)` |

Nachher über dieselbe Breitenreihe, Watch-Ansicht und Startseite, kein Überlauf mehr:

```
 1600  scrollWidth 1585  Spalte 448  Karte 448  ok
 1509  scrollWidth 1494  Spalte 384  Karte 384  ok
 1024  scrollWidth 1009  Spalte 384  Karte 384  ok
  900  scrollWidth  885  Spalte 837  Karte 837  ok
```

### Warum das Rollen jedes dritte Bild verlor

Das Scrollen fühlte sich an, „als wäre die Seite nicht grafikbeschleunigt" — und zwar
in allen Ansichten gleich. Das schloss den Inhalt einer einzelnen Ansicht schon aus:
Es musste etwas sein, das überall mitläuft. Drei Kandidaten kamen dafür in Frage, alle
drei im CSS: der feste Hintergrundverlauf am `body`, das Milchglas an Kopf- und
Fußleiste, und `content-visibility: auto` an Karten und Kommentaren.

Geraten wurde nicht. Eine Messseite lud das echte Stylesheet, baute Leisten und Feed
mit den echten Klassennamen nach, rollte skriptgesteuert darüber und zeichnete die
Abstände zwischen den Bildern auf — je Lauf mit genau einem abgeschalteten Kandidaten.
2560×1331 auf einem 164-Hz-Schirm ohne variable Bildrate, Bildperiode also 6,1 ms:

| Lauf | abgeschaltet | Median | p95 | zu lange | gelieferte Bilder |
| --- | --- | --- | --- | --- | --- |
| A | nichts | 6,10 | 12,20 | **306** | 1016 |
| B | fester Verlauf | 6,10 | 6,20 | 0 | **1320** |
| C | Milchglas | 6,10 | 12,20 | 309 | 1012 |
| D | `content-visibility` | 6,10 | 12,20 | 248 | 1073 |
| E | alle drei | 6,10 | 6,10 | 0 | 1320 |

Die Antwort steht in einer Zeile: **B allein ist so gut wie E.** Fast jedes dritte Bild
brauchte 12,20 ms — exakt das Doppelte der Bildperiode, also ein glatt ausgelassenes
Bild. Milchglas und `content-visibility` kosten nichts Messbares und blieben deshalb,
wie sie waren. Nebenbei die Antwort auf die Ausgangsfrage: Beschleunigt **wird**, die
Messung liest die Grafikkarte über WebGL aus und fand eine RTX 3070.

`background-attachment: fixed` heißt für Chromium: Der Hintergrund steht still,
während der Inhalt wandert — also muss er in jedem Rollbild neu gezeichnet werden,
über das ganze Sichtfeld. Bei 2560×1331 sind das zwei große Radialverläufe pro Bild.

Behoben ist es verlustfrei. Der Verlauf sitzt jetzt auf einer eigenen, fest
positionierten Ebene (`body::before`, `position: fixed; inset: 0; z-index: -1`) — er
deckt dasselbe Sichtfeld ab, die Prozentangaben beziehen sich hier wie dort darauf, und
beim Rollen wird er nur noch übereinandergelegt statt neu gezeichnet. Nachgemessen,
Bildschirmfotos bei Rollstand 0 und 2000 px vorher gegen nachher: größte Abweichung
2 von 255 Helligkeitsstufen, also Verlaufsrundung. Und die Bildzeiten danach:

| Lauf | zu lange | p95 | gelieferte Bilder |
| --- | --- | --- | --- |
| A, vorher | 306 von 1016 | 12,20 ms | 1016 |
| A, jetzt | **0 von 1320** | 6,20 ms | **1320** |

Dreißig Prozent mehr Bilder im selben Zeitfenster, keines davon zu spät.

Ein Nachtrag zur Methode: Drei Messläufe gingen daneben, weil der Browser das alte
Stylesheet aus dem Zwischenspeicher nahm, obwohl der Server `no-cache` schickt und das
HTML frisch kam. Gemerkt haben wir es nur, weil eine Variante noch wirkte, die es gar
nicht mehr geben konnte. Eine Messung, die ihren eigenen Prüfling nicht ausweist, ist
darum keine — die Seite meldet seither beim Laden, welchen Stand sie vor sich hat.

## Grenzen

- **Anmeldung nur am Rechner.** Google akzeptiert als Weiterleitungsziel nur HTTPS
  oder `localhost`; eine LAN-IP wie `192.168.x.x` lehnt es ab. HTTPS allein reicht dafür
  nicht: Das Entwicklerzertifikat kennt nur `localhost`, am Handy gäbe es eine
  Namenswarnung — und mit Zertifikatsfehler ist es kein sicherer Kontext mehr. Am Handy
  bleibt die App deshalb abgemeldet und auch nicht als Kachel installierbar; alles
  Öffentliche funktioniert dort weiter. Der Ausweg wäre ein Zertifikat, dem das Handy
  vertraut — siehe Setup-Schritt 3, „Warum HTTPS".
- **Anmeldung hält 7 Tage.** Google lässt Refresh-Tokens im Veröffentlichungsstatus
  *Test* verfallen, und „In Produktion" ist ohne eigene Domain nicht erreichbar
  (siehe Setup-Schritt 5). Danach einmal neu anmelden.
- **Kein Wiedergabeverlauf, kein „Später ansehen".** Das Schema führt `watchHistory` und
  `watchLater` zwar noch unter `relatedPlaylists`, live kommen aber nur `uploads` und
  `likes` zurück — Google hat die Felder 2016 stillgelegt. Die App kann deshalb nicht
  wissen, was du schon gesehen hast.
- **Keine echten Empfehlungen.** `activities.list?home` antwortet mit
  `homeParameterDeprecated` („The user's home page activity data is not available through
  this API") und lieferte selbst früher nur die Startseite eines *abgemeldeten* Nutzers.
  Die personalisierte Startseite ist deshalb eine eigene Mischung, kein Abbild von
  YouTubes Algorithmus.
- **Kein fertiger Abo-Feed.** `activities.list?home` ist abgeschafft. Die App baut den
  Feed selbst: Abo-Liste holen, je Kanal die Uploads-Playlist lesen (die Kanal-ID mit
  `UU` statt `UC`) und nach Datum mischen. Kostet rund eine Kontingenteinheit pro Kanal
  statt 100 wie der naheliegende Weg über `search.list`. **Kanalseiten gehen denselben
  Weg** — 1 Einheit statt 100, und 50 statt 24 Einträge je Seite, was beim Aussortieren
  der Shorts hilft. Bringt eine Seite nur Shorts, wird bis zu dreimal weitergeblättert,
  statt ein leeres Raster zu zeigen.
- **Keine echten „ähnlichen Videos".** Google hat `relatedToVideoId` aus `search.list`
  entfernt — der Parameter beantwortet heute jeden Aufruf mit `400`. Die Seitenspalte
  nähert die Liste stattdessen an: Sie sucht mit den **Stichwörtern** des laufenden
  Videos innerhalb **seiner eigenen Kategorie**, wirft das Video selbst raus und lässt
  höchstens **sechs** von zwanzig Treffern vom selben Kanal zu, damit daraus nicht doch
  wieder eine Kanalliste wird. `relevanceLanguage` kommt aus `defaultAudioLanguage` des Videos, nicht
  aus der Oberfläche — sonst stünden über einem englischen Video lauter Übersetzungen.

  Die Abfrage ist auf **sechs Wörter** gedeckelt. Mehr Stichwörter machen sie nicht
  treffender, sondern enger: Hängt an drei kurzen Begriffen noch der Serienname des
  Kanals, findet die Suche am Ende nur noch das Video selbst. Reicht eine Stufe nicht für
  fünf Treffer, folgt eine breitere — höchstens **drei Stufen** je Video, weil jede eine
  volle Suche kostet:

  | Stufe | mit Stichwörtern | ohne Stichwörter |
  | ----- | ---------------- | ---------------- |
  | 1 | Stichwörter bis sechs Wörter | Titel bis zum ersten Trennzeichen |
  | 2 | halb so viele Stichwörter | ganzer Titel, auf sechs Wörter gekürzt |
  | 3 | nur das erste Stichwort, **ohne Kategorie** | wie Stufe 1, **ohne Kategorie** |

  Titel werden dabei **nicht wortweise** gekürzt — das ergäbe Abfragen wie „Die
  europäische". Stattdessen zählt der Teil vor dem ersten Trennzeichen, denn danach steht
  meist der Serien- oder Kanalzusatz („Dürrecheck — Hätte auch noch schlimmer sein
  können"). Abgebrochen wird bei der ersten brauchbaren Stufe; im Normalfall bleibt es
  bei **einer** Suche.

  Bleiben am Ende weniger als fünf Treffer, füllen die neuesten Videos des Kanals auf
  (1 Einheit, ohne Kanaldeckel). Manche Titel sind Wortschöpfungen des Kanals, auf die
  keine Suche etwas findet — „Dürrecheck" etwa fand genau ein Video.

  Die Güte hängt daran, ob der Kanal Stichwörter vergibt. Viele tun das nicht mehr; dann
  bleibt der Titel als Abfrage, und die Treffer ähneln eher der **Formulierung** als dem
  Thema. Bleibt die Suche leer oder ist das Kontingent erschöpft, fällt die Spalte auf
  „Mehr von …" zurück.

  **Shorts** stehen in beiden Fällen nicht in der Spalte — außer das laufende Video ist
  selbst ein Short, dann sind sie genau das Erwartete. `GET /api/video/{id}` liefert
  dafür `isShort` mit; die Seitenspalte hängt bei einem Short `?shorts=1` an den
  Kanal-Rückfall.

  **Kosten:** rund 102 Einheiten je erstmalig geöffnetem Video (`search.list` allein 100).
  Das Ergebnis hält deshalb `RelatedCacheMinutes` lang — Standard **180 Minuten**, gegen
  10 Minuten für gewöhnliche Antworten.
- **Nicht einbettbare Videos** erkennt die App vorab über `status.embeddable` und
  verlinkt sie nach YouTube, statt einen kaputten Player zu zeigen.
- **Shorts** laufen im normalen Player, nicht in der vertikalen Wisch-Ansicht.
- **Kommentare** lassen sich ohne Anmeldung lesen, mit Anmeldung auch schreiben und
  beantworten. Antwortstränge kommen erst auf Klick, damit eine Kommentarseite nicht
  Dutzende Aufrufe auf einmal auslöst. Bei deaktivierten Kommentaren meldet die API
  einen 403 — das fängt die App ab und zeigt einen Hinweis statt eines Fehlers.
- **Frisch abgeschickte Kommentare** stellt die App selbst an den Anfang der Liste:
  `commentThreads.list` braucht einige Sekunden, bis es sie von sich aus zurückliefert.

## Kontingent

10.000 Einheiten/Tag. `videos.list` und `commentThreads.list` kosten je 1 Einheit,
**`search.list` kostet 100** — und unterliegt zusätzlich einem **eigenen Tageszähler**.
Ist der leer, antwortet die API mit `429 rateLimitExceeded` und der Meldung *„Quota
exceeded for quota metric 'Search Queries'"*, während die Einheiten noch reichen: Ein
`videos.list` geht dann weiter, jede Suche nicht. Dieser Zähler bindet in der Praxis
härter als die 10.000 Einheiten. Der aktuelle Stand steht in der Cloud Console unter
*APIs und Dienste → YouTube Data API v3 → Kontingente*.

Über `search.list` laufen Suche, die Shorts-Zeile und die Spalte „Ähnliche Videos";
Feed, Video, Kommentare und Kanalseiten nicht. Die Seitenspalte ist damit der stärkste
Verbraucher im Alltag — je erstmalig geöffnetem Video mindestens eine Suche. Deshalb
`RelatedMaxSearches` (**3**) als harte Obergrenze je Video und `RelatedCacheMinutes`
(**180**) als Streckmittel. Läuft der Zähler leer, fällt die Spalte auf „Mehr von …"
zurück und die Oberfläche zeigt eine verständliche Meldung statt der Rohantwort — die
nennt sonst die Projektnummer. Der Cache
in `CacheMinutes` fängt Wiederholungen ab; die Shorts liegen mit `ShortsCacheMinutes`
deutlich länger im Speicher, weil ihr Abruf teuer ist und sich Trend-Shorts ohnehin
nur langsam drehen.

Die personalisierte Startseite kostet rund **110 Einheiten** je vollständiger
Aktualisierung — 4 für die 200 Likes, je 1 pro Lieblings- und Abo-Kanal, 1 für die Trends
und 4 fürs Anreichern der 200 vorbereiteten Videos (`videos.list` nimmt 50 IDs je Aufruf).
Danach liegt die fertige Seite `CacheMinutes` lang im Speicher und Blättern ist gratis.
Die Shorts-Verifikation läuft über HEAD-Anfragen an youtube.com und kostet gar kein
Kontingent.

Schreibende Aufrufe kosten je **50 Einheiten**: ein Kommentar, eine Antwort, ein
Daumen und ein Abo-Klick. Der Abo-Feed kostet rund eine Einheit pro abonniertem Kanal
und liegt danach `CacheMinutes` lang im Speicher — bei 60 Abos also etwa 63 Einheiten
je Aktualisierung. Private Antworten landen dabei unter einem Schlüssel, der den
Benutzer enthält, damit kein Browser die Daten eines anderen zu sehen bekommt.
