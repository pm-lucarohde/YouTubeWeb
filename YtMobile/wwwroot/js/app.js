/* YouTube — leichtgewichtige Oberfläche.
   No framework: a hash router, fetch calls against /api, and direct DOM
   construction. Keeps the payload tiny and the main thread quiet on low-end
   phones. */

const main = document.getElementById('main');
const form = document.getElementById('search');
const input = document.getElementById('q');
const clearBtn = document.getElementById('clear');

/** Cache of the current feed page so "load more" can append. */
let feed = null;
/** Incremented on every navigation; stale async responses check it and bail. */
let navToken = 0;

// ---------------------------------------------------------------- helpers

const el = (tag, props = {}, children = []) => {
  const node = Object.assign(document.createElement(tag), props);
  for (const child of [].concat(children)) {
    if (child != null && child !== false) node.append(child);
  }
  return node;
};

class ApiError extends Error {
  constructor(message, status) {
    super(message);
    this.status = status;
  }
}

const request = async (path, options = {}) => {
  const res = await fetch(path, {
    ...options,
    headers: {
      Accept: 'application/json',
      ...(options.body ? { 'Content-Type': 'application/json' } : {}),
    },
  });
  const data = await res.json().catch(() => null);

  if (!res.ok) {
    // 401 heißt: Die Anmeldung ist weg (Token widerrufen, Cookie abgelaufen).
    // Der Zustand wird sofort verworfen, damit die Oberfläche wieder die
    // Anmeldeschaltfläche zeigt, statt einen roten Fehler stehen zu lassen.
    if (res.status === 401) signedOut();
    throw new ApiError(data?.error ?? `Fehler ${res.status}`, res.status);
  }
  return data;
};

const api = (path) => request(path);

const send = (path, method, body) =>
  request(path, { method, body: body === undefined ? undefined : JSON.stringify(body) });

/** Splits text on URLs so they can be rendered as real links, safely. */
const linkify = (text) => {
  const parts = text.split(/(https?:\/\/[^\s<>"']+)/g);
  return parts.map((part, i) =>
    i % 2 ? el('a', { href: part, textContent: part, rel: 'noopener noreferrer', target: '_blank' }) : part
  );
};

// ---------------------------------------------------------------- konto

/** Angemeldeter Kanal ({ channelId, name, avatar }) oder null. */
let user = null;
/** Ob der Server überhaupt OAuth-Zugangsdaten hat. */
let loginAvailable = false;

const accountBox = document.getElementById('account');

const loginHref = () => `/api/auth/login?return=${encodeURIComponent(location.hash || '#/')}`;

/** Nach dem Anmelden dorthin zurück, wo man war. */
function signedOut() {
  if (!user) return;
  user = null;
  if (config) config.user = null;
  renderAccount();
  route();
}

function renderAccount() {
  if (!accountBox) return;

  if (!loginAvailable) {
    accountBox.replaceChildren();
    return;
  }

  if (!user) {
    accountBox.replaceChildren(
      el('a', { className: 'btn account__login', href: loginHref(), textContent: 'Anmelden' })
    );
    return;
  }

  const avatar = user.avatar
    ? el('img', { className: 'account__avatar', src: user.avatar, alt: '', width: 32, height: 32, referrerPolicy: 'no-referrer' })
    : el('span', { className: 'account__avatar account__avatar--blank', textContent: (user.name[0] ?? '?').toUpperCase() });

  const menu = el('div', { className: 'account__menu', hidden: true }, [
    el('p', { className: 'account__name', textContent: user.name }),
    el('button', { className: 'account__item', type: 'button', textContent: 'Abmelden', onclick: logout }),
  ]);

  const toggle = el('button', { className: 'account__button', type: 'button' }, [avatar]);
  toggle.setAttribute('aria-label', `Konto: ${user.name}`);
  toggle.setAttribute('aria-expanded', 'false');
  toggle.onclick = () => {
    menu.hidden = !menu.hidden;
    toggle.setAttribute('aria-expanded', String(!menu.hidden));
  };

  accountBox.replaceChildren(toggle, menu);
}

document.addEventListener('click', (e) => {
  if (e.target.closest?.('.account')) return;
  const menu = accountBox?.querySelector('.account__menu');
  if (menu) menu.hidden = true;
  accountBox?.querySelector('.account__button')?.setAttribute('aria-expanded', 'false');
});

async function logout() {
  // Scheitert der Widerruf bei Google, ist die Sitzung hier trotzdem beendet.
  try { await send('/api/auth/logout', 'POST'); } catch { /* egal */ }
  user = null;
  if (config) config.user = null;
  renderAccount();
  route();
}

/** Einzeiler unter der Kopfzeile für Meldungen, die keine ganze Ansicht verdienen. */
function showBanner(message) {
  document.querySelector('.banner')?.remove();
  const banner = el('div', { className: 'banner' }, [
    el('p', { className: 'banner__text', textContent: message }),
    el('button', { className: 'banner__close', type: 'button', textContent: '\u00d7', onclick: () => banner.remove() }),
  ]);
  banner.setAttribute('role', 'alert');
  banner.querySelector('.banner__close').setAttribute('aria-label', 'Meldung schließen');
  document.getElementById('appbar').after(banner);
}

// ---------------------------------------------------------------- player

/** youtube-nocookie setzt bis zum ersten Play keine Tracking-Cookies. */
const EMBED_ORIGIN = 'https://www.youtube-nocookie.com';

/**
 * Baut eine Embed-URL. `theme=dark` landet in der Player-Konfiguration als
 * `USER_INTERFACE_THEME_DARK` und hält die alten `ytp-`Menüs und Fehlermeldungen
 * dunkel. Das neue Einstellungsblatt des Players (Qualität, Tempo, Untertitel)
 * erreicht der Parameter nicht: Dessen Farbe steht fest im nachgeladenen Stil, ohne
 * dunkle Variante — und in ein fremdes Origin lässt sich das nicht ändern. Siehe
 * README, „Dunkle Player-Steuerung".
 */
const embedUrl = (id, params) =>
  `${EMBED_ORIGIN}/embed/${encodeURIComponent(id)}?theme=dark&rel=0&playsinline=1&modestbranding=1&${params}`;

/** Steuerbefehl an den Player, gleiche Nachrichtenform wie die IFrame-Player-API. */
const playerCommand = (frame, func, args = []) =>
  frame.contentWindow?.postMessage(JSON.stringify({ event: 'command', func, args }), EMBED_ORIGIN);

/** "1:02:03" -> 3723. null bei Livestreams, die keine Länge haben. */
function labelSeconds(label) {
  if (!label) return null;
  const parts = label.split(':').map(Number);
  if (parts.some((n) => !Number.isFinite(n))) return null;
  return parts.reduce((total, n) => total * 60 + n, 0);
}

/** Zustand der IFrame-Player-API: 1 läuft, 2 pausiert, 0 durchgelaufen. */
const PLAYING = 1;
const PAUSED = 2;
const ENDED = 0;

/**
 * Der Player der Watch-Seite, solange einer sichtbar ist. `time` und `at` gehören
 * zusammen: die zuletzt gemeldete Stelle und der Moment, in dem sie gemeldet wurde.
 * Fragen könnte man den Player nur asynchron — für eine Taste zu langsam.
 */
let watchPlayer = null;

/**
 * Geschätzte Stelle im Video. Zwischen zwei Meldungen läuft die Wiedergabe weiter;
 * ohne das Weiterrechnen springt „5 Sekunden vor“ mal 4 und mal 6 Sekunden weit.
 */
const playerTime = (p) => (p.state === PLAYING ? p.time + (performance.now() - p.at) / 1000 : p.time);

/** Sprung an eine feste Stelle. Die Schätzung zieht sofort mit, damit zwei schnelle
    Tastendrücke sich addieren, statt beide vom selben Stand aus zu rechnen. */
function playerSeek(p, target) {
  const to = Math.max(0, p.seconds ? Math.min(target, p.seconds) : target);
  p.time = to;
  p.at = performance.now();
  playerCommand(p.frame, 'seekTo', [to, true]);
}

/* Der Player meldet ungefragt Stelle und Zustand, sobald der Handshake steht. */
addEventListener('message', (e) => {
  const p = watchPlayer;
  if (!p || e.origin !== EMBED_ORIGIN || e.source !== p.frame.contentWindow) return;
  let info;
  try {
    info = JSON.parse(e.data)?.info;
  } catch {
    return;
  }
  if (!info) return;

  // Die erste Meldung eines Players ist sein Zustandsbericht, keine Geste. Sie trägt
  // Lautstärke und Stummschaltung immer mit sich, und ihre Stelle steht gegen eine
  // Schätzung, die es noch gar nicht gibt. Wer sie mitzählt, schiebt den Griff nach dem
  // Fokus nach jedem Klick ins Video um den Nachlauf je Meldung weiter — und genau so
  // lange taten die Zifferntasten nichts.
  const bekannt = p.gemeldet === true;
  p.gemeldet = true;

  const geste = () => {
    letzteGeste = performance.now();
    zeigerNeuStarten();
  };

  // Die gemeldete Dauer ist genauer als die aus dem Etikett und kennt auch Livestreams.
  if (info.duration > 0) p.seconds = info.duration;
  if (typeof info.currentTime === 'number') {
    // Ein Sprung gegen die eigene Schätzung heißt: es wurde gespult, also bedient.
    // Über dem fremden Rahmen ist das das einzige Zeichen für einen laufenden Zug an
    // der Zeitleiste — ohne es fiele die Zeigersperre mitten hinein, und der Griff
    // nach dem Fokus risse den Zug ab.
    // Gegen die Schätzung *und* gegen die zuletzt gemeldete Stelle, der kleinere
    // Abstand zählt. Allein gegen die Schätzung wäre jede Ungenauigkeit darin ein
    // Dauer-Spulen: Steht `state` falsch auf „läuft" oder läuft die Wiedergabe nicht
    // mit einfachem Tempo, wächst der Abstand von selbst über die Schwelle, alle
    // anderthalb Sekunden käme eine „Geste" — und die stellt den Wecker jedes Mal
    // zurück, ohne dass jemand die Maus angerührt hätte. Ein echtes Spulen springt
    // dagegen auch gegen die letzte Meldung, die eine Viertelsekunde alt ist.
    const sprung = Math.min(
      Math.abs(info.currentTime - playerTime(p)),
      Math.abs(info.currentTime - p.time));
    if (bekannt && sprung > 1.5) geste();
    p.time = info.currentTime;
    p.at = performance.now();
  }
  if (typeof info.playerState === 'number') p.state = info.playerState;

  // `volume` steht nur in der Meldung, wenn es sich gerade geändert hat — die
  // laufenden Meldungen tragen bloß Stelle und Fortschritt. Gemessen: setVolume
  // erzeugte genau eine Meldung mit `volume`, die folgenden hatten keine. Damit ist
  // das ein sauberes Zeichen für „am Regler wird gerade gezogen" — aber nur, wenn
  // sich der Wert gegenüber dem zuletzt bekannten wirklich bewegt hat.
  const amRegler =
    (typeof info.volume === 'number' && info.volume !== p.volume)
    || (typeof info.muted === 'boolean' && info.muted !== p.muted);
  if (typeof info.volume === 'number') p.volume = info.volume;
  if (typeof info.muted === 'boolean') p.muted = info.muted;
  // Dasselbe Zeichen hält auch die Zeigersperre vom Lautstärkeregler fern.
  if (bekannt && amRegler) geste();
});

// ---------------------------------------------------- Zeiger im Vollbild

/**
 * Im Vollbild soll der Zeiger nach kurzer Ruhe verschwinden. Über `cursor: none` geht
 * das nicht: Chromium zeichnet den Zeiger erst neu, wenn er sich bewegt — und genau
 * die Bewegung hebt das Ausblenden wieder auf. Zwanzig Wege sind daran gescheitert,
 * am X-Server nachgemessen (README, „Der Mauszeiger im Vollbild").
 *
 * Was trägt, ist Pointer Lock: Den Zeiger blendet der Browser dabei selbst aus, ohne
 * auf eine Bewegung zu warten. Im Vollbild braucht es dafür keine Nutzergeste, der
 * Zeiger bleibt an seiner Stelle stehen, und das Vollbild bleibt unberührt.
 */
const ZEIGER_RUHE = 3000;

/**
 * Jedes Sperren nimmt dem Fenster die Hardware-Zeigerebene, und das Videobild fällt
 * dabei kurz aus seiner Overlay-Ebene — sichtbar als Flackern. Solange nicht gesperrt
 * ist, sieht die Seite über dem fremden Rahmen aber keine Bewegung und muss zum
 * Nachsehen sperren. Zwei Bremsen halten diesen Takt klein:
 *
 * - Zittern wird an der *Richtung* erkannt, nicht am Weg: Es pendelt um null, echte
 *   Bewegung nicht. Gelöst wird deshalb beim ersten spürbaren Ereignis oder sobald
 *   der Versatz drei Pixel erreicht — beides binnen weniger Millisekunden.
 * - Wird trotzdem sofort wieder gelöst, war die Maus wirklich in Bewegung. Dann
 *   wächst die Wartezeit, statt es gleich noch einmal zu versuchen.
 *
 * Die Schwelle muss klein bleiben: Solange gesperrt ist, steht der Zeiger still. Was
 * bis zum Lösen an Bewegung anfällt, ist verloren, und das fühlt sich als hakender
 * Zeiger an. Drei Pixel sind ein bis zwei Ereignisse, also unter einer Bildwiederholung.
 */
// Ein Zittern von einem Pixel diagonal ergibt bereits 2 — gemessen, es löste die
// Sperre. Der Sprung muss also darüber liegen; jede zügige Bewegung bringt im ersten
// Ereignis ein Vielfaches davon mit.
const ZEIGER_SPRUNG = 4;
const ZEIGER_VERSATZ = 3;
const ZEIGER_ABKLINGEN = 200;
const ZEIGER_RUHE_MAX = 9000;
const ZEIGER_KURZ = 1000;

let zeigerTimer = 0;
let zeigerAbklingen = 0;
let zeigerWartezeit = ZEIGER_RUHE;
let zeigerSeit = 0;
let zeigerNetzX = 0;
let zeigerNetzY = 0;

/**
 * Ausgeschlossen werden nur Geräte ohne feinen Zeiger — dort gibt es nichts zu
 * verstecken. `pointer: coarse` allein wäre die falsche Frage: Es beschreibt nur das
 * erste Zeigegerät, ein Notebook mit Touchscreen meldet damit `coarse`, obwohl eine
 * Maus daran hängt. Erst `any-pointer` fragt nach dem ganzen Bestand. Kennt ein
 * Browser die Schreibweise nicht, wird die Abfrage zu `not all` und trifft nie — die
 * harmlosere Richtung.
 */
const groberZeiger = matchMedia('(pointer: coarse) and (not (any-pointer: fine))');

/**
 * Nur der Player der Watch-Seite. YouTubes eigener Knopf fordert das Vollbild von
 * innen an; dem Elterndokument wird dann das <iframe> gemeldet. `contains` deckt
 * zusätzlich den Fall ab, dass ein Vorfahr das Vollbildelement ist.
 */
const imPlayerVollbild = () => {
  const gross = document.fullscreenElement;
  return !!watchPlayer && !!gross &&
    (gross === watchPlayer.frame || gross.contains(watchPlayer.frame));
};

/**
 * Keiner der Ausgänge hier darf aufgeben. Der Wecker ist die einzige Stelle, die das
 * Ausblenden überhaupt anstößt, und er stellt sich nicht von selbst neu: Wer hier
 * stumm zurückkehrt, hat den Zeiger für den Rest des Vollbilds sichtbar gelassen.
 * Genau das war das unstete Verhalten — einmal auf den zweiten Bildschirm geklickt,
 * und die Seite versuchte es nie wieder.
 */
function zeigerSperren() {
  if (!imPlayerVollbild() || document.pointerLockElement) return;
  // Liegt der Reiter im Hintergrund oder hat ein anderes Fenster den Fokus, gehört der
  // Zeiger gerade nicht uns. Später nachsehen, nicht aufgeben.
  if (document.hidden || !document.hasFocus()) {
    zeigerNeuStarten();
    return;
  }
  // Ältere Browser geben nichts zurück, neuere ein Promise. Schlägt es fehl, bleibt der
  // Zeiger stehen — kein Schaden, aber der nächste Versuch muss trotzdem kommen. Ob
  // dabei zusätzlich `pointerlockerror` feuert, hängt am Browser; `zeigerNeuStarten`
  // löscht den alten Wecker, doppeltes Ansetzen macht deshalb nichts.
  try {
    document.body.requestPointerLock()?.catch?.(() => zeigerNeuStarten());
  } catch {
    zeigerNeuStarten();
  }
}

function zeigerFreigeben() {
  if (document.pointerLockElement) document.exitPointerLock();
}

function zeigerNeuStarten() {
  clearTimeout(zeigerTimer);
  if (!imPlayerVollbild() || groberZeiger.matches) return;
  zeigerTimer = setTimeout(zeigerSperren, zeigerWartezeit);
}

function zeigerAufraeumen() {
  clearTimeout(zeigerTimer);
  clearTimeout(zeigerAbklingen);
  zeigerWartezeit = ZEIGER_RUHE;
  zeigerFreigeben();
}

/**
 * Solange gesperrt ist, kommen Mausbewegungen bei uns an — das ist der einzige Weg,
 * das Ende der Ruhe zu bemerken. Ohne Sperre sieht die Seite über dem fremden Rahmen
 * keine einzige Bewegung, gemessen 0 von 20.
 *
 * Zwei Wege lösen, und beide sind schnell: ein einzelnes Ereignis von mehr als einem
 * Pixel — so fängt jede zügige Bewegung an — oder ein aufsummierter *Versatz* von drei
 * Pixeln, für den langsamen Griff zur Maus. Summiert wird mit Vorzeichen und nach
 * kurzer Stille wieder verworfen: Zittern pendelt um null und kommt so nie an.
 */
addEventListener('mousemove', (e) => {
  if (!document.pointerLockElement) return;
  zeigerNetzX += e.movementX;
  zeigerNetzY += e.movementY;
  clearTimeout(zeigerAbklingen);
  zeigerAbklingen = setTimeout(() => { zeigerNetzX = 0; zeigerNetzY = 0; }, ZEIGER_ABKLINGEN);
  if (Math.abs(e.movementX) + Math.abs(e.movementY) >= ZEIGER_SPRUNG ||
      Math.abs(zeigerNetzX) + Math.abs(zeigerNetzY) >= ZEIGER_VERSATZ) {
    zeigerFreigeben();
  }
}, { passive: true, capture: true });

/**
 * Ein Klick, dem keine Bewegung vorausging, landet bei uns statt beim Player. Er löst
 * die Sperre und wird als Play/Pause weitergereicht, damit er nicht ins Leere geht.
 */
addEventListener('mousedown', (e) => {
  if (!document.pointerLockElement || !watchPlayer) return;
  zeigerFreigeben();
  if (e.button === 0) {
    playerCommand(watchPlayer.frame, watchPlayer.state === PLAYING ? 'pauseVideo' : 'playVideo');
  }
});

document.addEventListener('pointerlockchange', () => {
  if (document.pointerLockElement) {
    zeigerSeit = performance.now();
    zeigerNetzX = 0;
    zeigerNetzY = 0;
    return;
  }
  // Hielt die Sperre nur einen Augenblick, war die Maus in Bewegung — dann seltener
  // nachfassen. Hielt sie länger, lag wirklich Ruhe an, und die kurze Wartezeit gilt
  // wieder.
  zeigerWartezeit = performance.now() - zeigerSeit < ZEIGER_KURZ
    ? Math.min(zeigerWartezeit + 2000, ZEIGER_RUHE_MAX)
    : ZEIGER_RUHE;
  zeigerNeuStarten();
});
document.addEventListener('pointerlockerror', zeigerNeuStarten);

// Kommt der Fokus zurück, soll die Ruhezeit sofort wieder laufen, statt am Nachlauf des
// letzten vergeblichen Versuchs zu hängen. Feuert auch, wenn bloß der Rahmen den Fokus
// zurückgibt — dann setzt es denselben Wecker neu, und das schadet nicht.
addEventListener('focus', zeigerNeuStarten);
document.addEventListener('visibilitychange', () => {
  if (!document.hidden) zeigerNeuStarten();
});

// Kein `blur`-Horcher: window.blur feuert auch, wenn bloß der iframe den Fokus nimmt,
// und das passiert bei jedem Klick ins Video.
addEventListener('fullscreenchange', () => {
  if (imPlayerVollbild()) {
    zeigerWartezeit = ZEIGER_RUHE;
    zeigerNeuStarten();
  } else {
    zeigerAufraeumen();
  }
});

/**
 * Der eingebettete Player beantwortet fast alle Tastenkürzel selbst — die Ziffern
 * aber nicht, die kennt nur die YouTube-Seite. Mitlesen kann die Seite sie nicht,
 * solange der Player den Fokus hat: Über eine Origin-Grenze gibt es keine Tasten,
 * und geteilt wird der Fokus nicht. Also holt die Seite ihn sich nach einem Klick
 * ins Video zurück und reicht dem Player die Kürzel, die ihm dadurch entgehen,
 * weiter unten als Befehl weiter. Preis dafür: In den Menüs des Players führen die
 * Pfeiltasten nicht mehr durch die Einträge — mit der Maus bleibt alles, wie es war.
 */
/**
 * Der Griff darf nicht sofort zufassen. Gemessen hat er das Ziehen am
 * Lautstärkeregler abgebrochen: Ein Mausdruck im Player gibt dem Rahmen den Fokus,
 * das Elternfenster bekommt dadurch `blur`, und der Griff nahm ihn mitten in der
 * Geste wieder weg.
 *
 * Warten muss er auf ein Signal, das die Seite auch wirklich bekommt. Über dem
 * fremden Rahmen sieht sie **nichts** — gemessen: fünf Mausbewegungen außerhalb des
 * Players kamen an, zwanzig darin keine einzige, und `mouseenter`/`mouseleave` am
 * `<iframe>` feuern gar nicht. Umgekehrt heißt das: Eine Mausbewegung, die hier
 * ankommt, beweist, dass der Zeiger den Player verlassen hat. Genau darauf wartet er.
 *
 * Im Vollbild greift der Griff genauso. Eine Weile stand hier das Gegenteil — dort
 * beantworte der Player ohnehin alles selbst. Für die Ziffern stimmt das nicht, die
 * kennt nur die große YouTube-Seite: Lag der Fokus im Rahmen, waren sie im Vollbild
 * tot, und ob er dort lag, hing am letzten Klick. Genau das war das unzuverlässige
 * Verhalten. Der Preis ist derselbe wie außerhalb — in den Menüs des Players führen
 * die Pfeiltasten nicht mehr durch die Einträge.
 */
let fokusOffen = false;
let fokusTimer = 0;

/**
 * Solange der Player noch Änderungen meldet, läuft eine Geste — Lautstärke wie
 * Zeitleiste. Über dem fremden Rahmen ist das das einzige Zeichen dafür, und im
 * Vollbild erst recht: Dort kommt nicht einmal eine Mausbewegung an.
 */
let letzteGeste = 0;
const GESTE_NACHLAUF = 500;

function fokusZurueckholen() {
  // `hasFocus` bleibt wahr, solange der Fokus im eigenen Reiter liegt. Ohne die
  // Abfrage würde auch ein Wechsel in ein anderes Fenster den Fokus umlegen.
  if (!fokusOffen || !watchPlayer || !document.hasFocus()) return;

  // Noch mitten in einer Geste: später nochmal versuchen, statt sie abzuwürgen.
  const rest = GESTE_NACHLAUF - (performance.now() - letzteGeste);
  if (rest > 0) {
    clearTimeout(fokusTimer);
    fokusTimer = setTimeout(fokusZurueckholen, rest);
    return;
  }

  if (document.activeElement === watchPlayer.frame) watchPlayer.frame.blur();
  fokusOffen = false;
}

addEventListener('blur', () => setTimeout(() => {
  if (!watchPlayer || !document.hasFocus()) return;
  if (document.activeElement !== watchPlayer.frame) return;
  fokusOffen = true;
  // Der Klick selbst zählt als Geste: Ein Druck auf den Regler ändert die Lautstärke
  // sofort, und bis dessen Meldung da ist, darf nicht schon zugegriffen werden.
  letzteGeste = performance.now();
  fokusZurueckholen();
}));

// Eine Mausbewegung, die hier ankommt, beweist zusätzlich, dass der Zeiger den Player
// verlassen hat. `capture`, damit kein Handler weiter unten sie wegnehmen kann.
addEventListener('mousemove', fokusZurueckholen, { passive: true, capture: true });
addEventListener('fullscreenchange', () => setTimeout(fokusZurueckholen));

/** Relative Sprünge, mit denselben Tasten und Weiten wie beim Player selbst. */
const SEEK_KEYS = { ArrowRight: 5, ArrowLeft: -5, l: 10, j: -10 };

document.addEventListener('keydown', (e) => {
  const p = watchPlayer;
  if (!p || e.ctrlKey || e.altKey || e.metaKey) return;
  // Tippen bleibt Tippen. Die Leertaste gehört zusätzlich jedem Bedienelement,
  // auf dem der Fokus gerade liegt — sonst löst sie es nicht mehr aus.
  if (e.target.closest?.('input, textarea, select, [contenteditable]')) return;
  if (e.key === ' ' && e.target.closest?.('a, button, summary, [tabindex]')) return;

  const digit = e.key.length === 1 ? '0123456789'.indexOf(e.key) : -1;
  if (digit >= 0) {
    if (!p.seconds) return;                       // Livestream: kein Zehntel zu berechnen
    playerSeek(p, (p.seconds * digit) / 10);
  } else if (Object.hasOwn(SEEK_KEYS, e.key)) {
    playerSeek(p, playerTime(p) + SEEK_KEYS[e.key]);
  } else if (e.key === 'k' || e.key === ' ') {
    playerCommand(p.frame, p.state === PLAYING ? 'pauseVideo' : 'playVideo');
    p.state = p.state === PLAYING ? PAUSED : PLAYING;
  } else if (e.key === 'm') {
    playerCommand(p.frame, p.muted ? 'unMute' : 'mute');
    p.muted = !p.muted;
  } else if (e.key === 'f') {
    // Der iframe selbst geht ins Vollbild, nicht sein Rahmen: Der Rahmen hält ein
    // 16:9-Verhältnis fest, das auf einem anderen Bildschirmformat Ränder lässt.
    (document.fullscreenElement ? document.exitFullscreen() : p.frame.requestFullscreen())
      ?.catch(() => {});
  } else {
    return;
  }
  e.preventDefault();
});

// ---------------------------------------------------------------- hover preview

/* Wer Animationen abbestellt hat, will kein von selbst startendes Video. Die Abfrage
   bleibt live statt einmalig, damit ein Umschalten der Einstellung sofort greift. */
const calmly = matchMedia('(prefers-reduced-motion: reduce)');

/** Kurze Verzögerung, damit bloßes Überfahren des Feeds keine Videos lädt. */
const PREVIEW_DELAY = 350;

/**
 * Kurzer Nachlauf, nachdem der Player zu spielen begonnen hat, damit die Einblendung
 * ein stehendes Bild erwischt und nicht die letzten Zuckungen des Bildaufbaus.
 */
const PREVIEW_SETTLE = 350;

/** Frühestens so viel später darf ein durchgelaufenes Video von vorn beginnen. */
const PREVIEW_REPEAT = 5000;

/** Vorschauen gibt es nur mit echtem Mauszeiger — Touch-Geräte laden nie einen Player. */
const canHover = matchMedia('(hover: hover) and (pointer: fine)');

let previewTimer = 0;
let revealTimer = 0;
let previewBox = null;
let previewShell = null;
let previewFrame = null;
/** Steht der Befehlskanal zum Player? */
let previewLive = false;
/** Video, das starten soll, sobald der Kanal steht. */
let previewWanted = null;
/** Zählt jede Vorschau, damit späte Zustandsmeldungen nicht zur falschen Karte passen. */
let previewRun = 0;
/** Laufendes Vorschau-Video und wann es gestartet wurde — für den Neubeginn nach 30 s. */
let previewId = null;
let previewStarted = 0;
/** Letzte bekannte Zeigerposition, für die Wahl der Parkecke. */
let pointerAt = { x: 0, y: 0 };

/**
 * Baut den einen Player, den es für die ganze Sitzung gibt. Passiert bewusst schon
 * bei der ersten Mausbewegung über dem Feed und nicht erst beim Hovern einer Karte:
 * Solange der Zeiger in Bewegung ist, fällt das kurze Zurücksetzen der Zeigerform
 * nicht auf — siehe den Kommentar zu `.preview` in app.css.
 */
function warmPreview(seedId) {
  if (previewShell) return;
  previewFrame = el('iframe', {
    className: 'preview__frame',
    // enablejsapi öffnet den Befehlskanal, über den später die Videos gewechselt
    // werden; ohne origin weist der Player die Nachrichten zurück. autoplay=0, weil
    // beim Aufwärmen noch nichts laufen soll — das Startvideo ist nur ein Platzhalter.
    src: embedUrl(seedId, 'enablejsapi=1&autoplay=0&mute=1&controls=0&disablekb=1&fs=0'
      + `&iv_load_policy=3&origin=${encodeURIComponent(location.origin)}`),
    title: '',
    allow: 'autoplay; encrypted-media',
    referrerPolicy: 'strict-origin-when-cross-origin',
    tabIndex: -1,
  });
  previewFrame.addEventListener('load', () => {
    // Der Handshake der IFrame-Player-API; ohne ihn verwirft der Player alle Befehle
    // und meldet auch keine Zustände zurück.
    previewFrame.contentWindow?.postMessage(
      JSON.stringify({ event: 'listening', id: 1, channel: 'widget' }), EMBED_ORIGIN);
    // Einmal stummschalten reicht: Der Player behält den Zustand über Videowechsel
    // hinweg. Hier stört es nicht, der Rahmen steht unsichtbar in der Parkecke.
    playerCommand(previewFrame, 'mute');
    previewLive = true;
    if (previewWanted) {
      startPreviewVideo(previewWanted);
      previewWanted = null;
    }
  });
  previewShell = el('div', { className: 'preview' }, [previewFrame]);
  previewShell.setAttribute('aria-hidden', 'true');
  document.body.append(previewShell);
  parkPreview(320, 180);
}

/**
 * Startet ein Video im laufenden Player, ohne ihn neu zu laden.
 *
 * `playmuted` ist der Schalter, an dem alles hängt: Ohne ihn legt der Player bei jedem
 * Start seine Mitte-Steuerung über das Bild — den runden Knopf mit dem Pausenzeichen
 * (`player-middle-controls`) — und blendet sie erst nach knapp vier Sekunden aus.
 * `controls=0` hilft dagegen nicht, das betrifft nur die Leiste unten.
 *
 * Mit `playmuted` läuft das Video stattdessen in YouTubes stummer Autowiedergabe, so
 * wie in YouTubes eigener Vorschau über den Vorschaubildern — und die kommt ohne jede
 * Bedienoberfläche im Bild aus. Der Objektaufbau ist nötig, weil `loadVideoById` nur
 * in dieser Form zusätzliche Player-Angaben durchreicht. (Die dokumentierte Methode
 * `mutedAutoplay()` täte dasselbe, ist aber auf Partnerseiten beschränkt und tut von
 * unserer Adresse aus nichts.)
 */
function startPreviewVideo(id) {
  previewId = id;
  previewStarted = performance.now();
  playerCommand(previewFrame, 'loadVideoById', [{ videoId: id, playmuted: true }]);
}

/**
 * Stumme Wiedergaben beendet YouTube nach 30 Sekunden. Wer so lange auf einer Karte
 * stehen bleibt, soll kein Standbild sehen, also beginnt das Video wieder von vorn.
 * Der Mindestabstand fängt Videos ab, die sofort enden, damit daraus keine Schleife
 * aus Ladevorgängen wird.
 */
function repeatPreviewVideo() {
  if (!previewBox || !previewId) return;
  if (performance.now() - previewStarted < PREVIEW_REPEAT) return;
  startPreviewVideo(previewId);
}

/* Der Player meldet über denselben Kanal zurück, was er gerade tut. Sichtbar wird die
   Vorschau erst, wenn er wirklich spielt — nicht nach einer geratenen Wartezeit. Damit
   ist auch ausgeschlossen, dass ein pausierter Player mit seinem Symbol in der
   Bildmitte zu sehen ist: Was nicht läuft, wird nicht eingeblendet. */
addEventListener('message', (e) => {
  if (e.origin !== EMBED_ORIGIN || !previewFrame || e.source !== previewFrame.contentWindow) return;
  let data;
  try {
    data = JSON.parse(e.data);
  } catch {
    return;
  }
  const state = data?.event === 'onStateChange' ? data.info : data?.info?.playerState;
  if (state === ENDED) {
    repeatPreviewVideo();
    return;
  }
  if (state !== PLAYING || !previewBox || revealTimer) return;
  const run = previewRun;
  revealTimer = setTimeout(() => run === previewRun && revealPreview(), PREVIEW_SETTLE);
});

function placePreview(left, top, width, height) {
  Object.assign(previewShell.style, {
    left: `${Math.round(left)}px`,
    top: `${Math.round(top)}px`,
    width: `${Math.round(width)}px`,
    height: `${Math.round(height)}px`,
  });
}

/** Parkstelle beim Laden: die Ecke des Sichtfelds, die am weitesten vom Zeiger weg ist. */
function parkPreview(width, height) {
  const left = pointerAt.x > innerWidth / 2 ? 0 : Math.max(0, innerWidth - width);
  const top = pointerAt.y > innerHeight / 2 ? 0 : Math.max(0, innerHeight - height);
  placePreview(left, top, width, height);
}

function coverBox(box) {
  const r = box.getBoundingClientRect();
  placePreview(r.left, r.top, r.width, r.height);
}

// Beim Rollen wandert die Karte, der Rahmen hängt aber am Sichtfeld fest. Das
// Nachführen läuft auf ein Bild gebündelt, damit es auch auf schwachen Geräten
// nicht bei jedem Roll-Ereignis neu rechnet.
let following = false;
function followBox() {
  if (following || !previewBox) return;
  following = true;
  requestAnimationFrame(() => {
    following = false;
    if (previewBox && previewShell?.classList.contains('is-ready')) coverBox(previewBox);
  });
}

function stopPreview() {
  clearTimeout(previewTimer);
  clearTimeout(revealTimer);
  previewTimer = 0;
  revealTimer = 0;
  removeEventListener('scroll', followBox, true);
  removeEventListener('resize', followBox);
  previewBox?.classList.remove('is-previewing', 'is-ready');
  previewBox = null;
  previewWanted = null;
  if (!previewShell) return;
  previewShell.classList.remove('is-ready');
  parkPreview(previewShell.offsetWidth, previewShell.offsetHeight);
  // Anhalten statt entladen: Der Player behält sein Dokument, nur das Video endet.
  if (previewLive) playerCommand(previewFrame, 'stopVideo');
}

function startPreview(box) {
  // Ohne aufgewärmten Player keine Vorschau. Ihn jetzt erst zu bauen wäre genau das,
  // was den Zeiger flackern lässt.
  if (!previewShell) return;
  const r = box.getBoundingClientRect();
  previewRun++;
  previewShell.style.borderRadius = getComputedStyle(box).borderRadius;
  parkPreview(r.width, r.height);
  // Videowechsel über den Befehlskanal — der Player lädt kein neues Dokument.
  if (previewLive) startPreviewVideo(box.dataset.preview);
  else previewWanted = box.dataset.preview;
  box.classList.add('is-previewing');
  // Eingeblendet wird nicht hier, sondern erst wenn der Player meldet, dass er läuft.
}

/** Schiebt den Player über die Karte und blendet ihn ein. */
function revealPreview() {
  const box = previewBox;
  if (!box) return;
  coverBox(box);
  previewShell.classList.add('is-ready');
  box.classList.add('is-ready');
  addEventListener('scroll', followBox, { passive: true, capture: true });
  addEventListener('resize', followBox, { passive: true });
}

/** Steht der Zeiger geometrisch noch über der Box? */
function pointerInside(box, e) {
  const r = box.getBoundingClientRect();
  return e.clientX >= r.left && e.clientX < r.right && e.clientY >= r.top && e.clientY < r.bottom;
}

/** Wärmt den Player bei der ersten Mausbewegung über einem gefüllten Feed auf. */
function maybeWarm(e) {
  if (e.pointerType !== 'mouse') return;
  pointerAt = { x: e.clientX, y: e.clientY };
  const seed = calmly.matches ? null : main.querySelector('[data-preview]')?.dataset.preview;
  if (!seed) return;
  main.removeEventListener('pointermove', maybeWarm);
  warmPreview(seed);
}
if (canHover.matches) main.addEventListener('pointermove', maybeWarm, { passive: true });

main.addEventListener('pointerover', (e) => {
  // Nur echte Zeiger: Ein Tap erzeugt ebenfalls ein pointerover, dann aber mit
  // pointerType "touch" — auf dem Handy soll nichts von allein losspielen.
  if (e.pointerType !== 'mouse' || calmly.matches) return;
  pointerAt = { x: e.clientX, y: e.clientY };
  const box = e.target.closest('[data-preview]');
  if (box === previewBox) return;
  stopPreview();
  if (!box) return;
  previewBox = box;
  previewTimer = setTimeout(() => startPreview(box), PREVIEW_DELAY);
});

main.addEventListener('pointerout', (e) => {
  if (!previewBox) return;
  pointerAt = { x: e.clientX, y: e.clientY };
  // Ein Wechsel innerhalb der Karte ist kein Verlassen. Chromium meldet den Übertritt
  // in einen fremden Frame außerdem als relatedTarget = null, was wie ein Verlassen
  // aussieht — dann würde die Vorschau abgeräumt, sofort neu aufgebaut und im Takt
  // der Verzögerung weiterflackern. Also nachmessen.
  if (previewBox.contains(e.relatedTarget) || pointerInside(previewBox, e)) return;
  stopPreview();
});

// ---------------------------------------------------------------- components

/**
 * Dasselbe Bild trägt zwei sehr verschiedene Größen: im Raster rund 283 px breit, in
 * der Spalte neben dem Player rund 446 px. `mqdefault` ist 320x180 — im Raster scharf,
 * in der Spalte um das 1,39-fache hochgerechnet. Dort, und nur dort, lohnt `hq720`
 * (1280x720): 68 KB gegen 6 KB, für zwanzig Karten neben dem Video vertretbar, für
 * zweihundert im Raster nicht.
 *
 * Über `srcset` liefe das nicht verlässlich: `sizes="auto"` misst die gerenderte Breite
 * erst, wenn es sie gibt, und wählte in der Messung mal die kleine, mal die große
 * Fassung für dieselbe Karte. Die Ansicht weiß es dagegen sicher.
 */
function grosseFassung(url) {
  return url.endsWith('/mqdefault.jpg') ? url.replace(/mqdefault\.jpg$/, 'hq720.jpg') : null;
}

/**
 * `gross` gilt für die Spalte neben dem Player. `hq720` gibt es nicht zu jedem Video;
 * fehlt es, antwortet i.ytimg mit 404, legt aber ein graues 120x90-Bild bei — der
 * Browser lädt das ohne `error`. Erkennbar ist der Platzhalter nur an seiner Größe.
 */
function videoCard(v, gross = false) {
  const klein = v.thumb;
  const scharf = gross ? grosseFassung(klein) : null;

  const img = el('img', {
    className: 'card__img',
    src: scharf ?? klein,
    alt: '',
    loading: 'lazy',
    decoding: 'async',
    width: 320,
    height: 180,
  });
  img.dataset.loading = '';
  img.addEventListener('load', () => {
    delete img.dataset.loading;
    if (img.src === scharf && img.naturalWidth < 200) img.src = klein;
  });
  img.addEventListener('error', () => {
    if (img.src === scharf) { img.src = klein; return; }
    delete img.dataset.loading;
  });

  const badge = v.durationLabel
    ? el('span', { className: 'card__badge', textContent: v.durationLabel })
    : el('span', { className: 'card__badge card__badge--live', textContent: 'LIVE' });

  const meta = el('p', { className: 'card__meta' }, [
    el('a', {
      className: 'card__channel',
      href: `#/channel/${v.channelId}?name=${encodeURIComponent(v.channelTitle)}`,
      textContent: v.channelTitle,
    }),
  ]);
  if (v.viewsLabel) meta.append(el('span', { className: 'dot', textContent: '•' }), v.viewsLabel);
  meta.append(el('span', { className: 'dot', textContent: '•' }), v.publishedLabel);

  const media = el('div', { className: 'card__media' }, [img, badge]);
  if (v.embeddable) media.dataset.preview = v.id;

  // The channel link must stay outside the video link — nested <a> is invalid.
  return el('li', { className: 'card' }, [
    el('a', { className: 'card__link', href: `#/watch/${v.id}` }, [media]),
    el('div', { className: 'card__body' }, [
      el('h3', { className: 'card__title' }, [
        el('a', { href: `#/watch/${v.id}`, textContent: v.title }),
      ]),
      meta,
    ]),
  ]);
}

/** Hochkant-Karte für die Shorts-Zeile. */
function shortCard(v) {
  const img = el('img', {
    className: 'short__img',
    src: v.thumb,
    alt: '',
    loading: 'lazy',
    decoding: 'async',
    width: 270,
    height: 480,
  });

  const media = el('div', { className: 'short__media' }, [img]);
  if (v.embeddable) media.dataset.preview = v.id;

  return el('li', { className: 'short' }, [
    el('a', { className: 'short__link', href: `#/watch/${v.id}` }, [
      media,
      el('h3', { className: 'short__title', textContent: v.title }),
    ]),
    el('p', { className: 'short__meta', textContent: v.viewsLabel ?? v.channelTitle }),
  ]);
}

const SHORTS_PER_ROW = 7;
/** Abstand zwischen zwei Shorts-Zeilen, gemessen in Feed-Reihen. */
const ROWS_PER_SHELF = 4;

/** Spaltenzahl des Grids, direkt am Element abgelesen. Solange das Raster noch keine
 *  Karte enthält, steht sie nicht fest — dann 0, damit niemand mit einer geratenen
 *  Spalte rechnet. */
function columnCount(list) {
  const cols = getComputedStyle(list).gridTemplateColumns;
  return cols && cols !== 'none' ? cols.split(' ').length : 0;
}

/**
 * Fabrik für die Shorts-Zeilen einer Seite. Jede Zeile entsteht sofort als leerer,
 * versteckter Platzhalter und zeichnet sich, sobald genug Shorts da sind — der Feed
 * wartet also nie auf sie. Alle Zeilen teilen sich einen Vorrat, der mit jeder
 * nachgeladenen Feed-Seite wächst; gezeichnet wird immer die ganze Liste von oben nach
 * unten, damit die sichtbaren Zeilen zuerst stehen.
 *
 * Die Feeds reichen ihre eigenen, aus dem Raster aussortierten Shorts herein. Bleibt der
 * Vorrat am Ende leer und ist `fallback` gesetzt, springt die Zeile auf die Regions-Shorts
 * um — die Trend-Charts führen zeitweise gar keine eigenen, und ohne den Rückfall hätte
 * die abgemeldete Startseite plötzlich keine Shorts-Zeile mehr. Der Rückgabewert ist die
 * Zeilenfabrik selbst; `extend` und `done` hängen als Felder daran, damit `feedList` sie
 * füttern kann.
 */
function shortsShelfFactory(preloaded, { fallback = false } = {}) {
  const pool = [];
  const shelves = [];
  // Solange noch Nachschub kommen kann, bleibt eine leere Zeile als Platzhalter stehen,
  // statt sich zu entfernen — sonst verschwände sie kurz vor ihren eigenen Daten.
  let done = false;

  const draw = () => {
    for (const { node, start } of shelves) {
      const slice = pool.slice(start, start + SHORTS_PER_ROW);

      if (!slice.length) {
        if (done) node.remove();
        continue;
      }

      const head = el('h2', { className: 'shorts__head' });
      head.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M10 3.3 6.7 5.2a4.6 4.6 0 0 0 .3 8.2l1.5.8-2 1.1a4.6 4.6 0 1 0 4.6 8l3.3-1.9a4.6 4.6 0 0 0-.3-8.2l-1.5-.8 2-1.1a4.6 4.6 0 1 0-4.6-8ZM10 9l5 3-5 3V9Z"/></svg>';
      head.append('Shorts');

      node.hidden = false;
      node.replaceChildren(head, el('ul', { className: 'shorts__row' }, slice.map(shortCard)));
    }
  };

  const make = () => {
    const node = el('li', { className: 'shorts' });
    node.hidden = true;
    shelves.push({ node, start: shelves.length * SHORTS_PER_ROW });
    draw();
    return node;
  };

  const imPool = new Set();

  make.extend = (items) => {
    if (!items?.length) return;
    // Aus demselben Grund wie im Raster: Der Nachschub kommt seitenweise, und was schon
    // in der Zeile steht, darf nicht ein zweites Mal hineinrutschen.
    const frisch = items.filter((v) => !imPool.has(v.id));
    if (!frisch.length) return;
    for (const v of frisch) imPool.add(v.id);
    pool.push(...frisch);
    draw();
  };

  make.done = () => {
    done = true;
    draw();
  };

  make.extend(preloaded ?? []);

  // Brachte die erste Seite keine eigenen Shorts, kommen die der Region — sofort, nicht
  // erst am Ende des Feeds: Die Trend-Charts blättern seitenweise weiter, das Ende käme
  // nie. Der Abruf hält den Feed nicht auf.
  if (fallback && !pool.length) {
    api('/api/shorts')
      .then((d) => make.extend(d?.items ?? []))
      .catch(() => {})
      .finally(make.done);
  }

  return make;
}

function skeleton(count = 6) {
  const wrap = el('div', { className: 'skeleton' });
  for (let i = 0; i < count; i++) {
    wrap.append(el('div', { className: 'skeleton__card' }, [
      el('div', { className: 'skeleton__media' }),
      el('div', { className: 'skeleton__line' }),
      el('div', { className: 'skeleton__line skeleton__line--short' }),
    ]));
  }
  return wrap;
}

function stateView({ title, text, extra }) {
  return el('div', { className: 'state' }, [
    el('h2', { className: 'state__title', textContent: title }),
    el('p', { className: 'state__text', textContent: text }),
    extra,
  ]);
}

function errorView(message, retry) {
  return stateView({
    title: 'Das hat nicht geklappt',
    text: message,
    extra: retry && el('button', { className: 'btn btn--accent', textContent: 'Erneut versuchen', onclick: retry }),
  });
}

/** Renders a feed and wires up paging for the given loader.
    <paramref name="makeShelf"/> streut alle ROWS_PER_SHELF Reihen eine
    Shorts-Zeile ein; ohne die Funktion bleibt der Feed reines Querformat. */
function feedList(container, loadPage, className = 'feed', makeShelf = null) {
  // Die Spalte neben dem Player zeigt dieselbe Karte rund anderthalbmal so breit.
  const grosseBilder = className.includes('feed--rail');
  const list = el('ul', { className });
  const more = el('div', { className: 'more' });
  container.append(list, more);

  const token = navToken;
  let loading = false;
  let placed = 0;
  // Was schon im Raster steht. Der Seitenzeiger ist serverseitig eine Position in einer
  // Liste, und die Startseite tauscht ihre Liste einmal aus — vom kurzen Kopf auf die
  // vollständige Mischung. Dort ist die Ursache behoben; hier steht der Riegel, der auch
  // hält, wenn sich eine Liste zwischen zwei Seiten sonst irgendwie verschiebt.
  const gesehen = new Set();
  let shelves = 0;
  // Karten seit der letzten Shorts-Zeile. Der Abstand zählt ab der Zeile, nicht ab dem
  // Anfang des Feeds — nur so bleiben es auch nach einer nachgereichten Zeile wieder
  // volle vier Reihen bis zur nächsten.
  let since = 0;
  // Wie viele Seiten geladen wurden und wie viele Raster-Videos die letzte brachte.
  let pages = 0;
  let gain = 1;

  // Der Takt gehört den Reihen, nicht den Seiten: Die Shorts-Zeile steht nach genau
  // ROWS_PER_SHELF vollen Reihen, unabhängig davon, wo eine nachgeladene Seite anfängt
  // oder aufhört. Weil ein Vielfaches der Spaltenzahl immer auf einer Reihengrenze
  // liegt, bleibt dabei auch keine halbe Reihe neben ihr leer.
  const addCards = (items) => {
    let neu = 0;
    // Die Spaltenzahl kommt aus dem Grid selbst, statt sie im Skript zu wiederholen:
    // Am Handy steht die Shorts-Zeile so nach vier Karten, am breiten Monitor nach
    // zwanzig — beides sind genau vier Reihen. Abgelesen wird sie erst, wenn die erste
    // Karte drinsteht: An einem leeren Raster löst der Browser die Spalten nicht auf,
    // und mit einer geratenen Spalte stünde die Zeile schon nach vier Karten.
    let every = 0;
    for (const v of items) {
      if (gesehen.has(v.id)) continue;
      gesehen.add(v.id);
      neu++;
      list.append(videoCard(v, grosseBilder));
      if (!every && makeShelf) every = columnCount(list) * ROWS_PER_SHELF;
      placed++;
      since++;
      if (every && since === every) {
        list.append(makeShelf());
        shelves++;
        since = 0;
      }
    }
    return neu;
  };

  // Manche Feeds erreichen die vier Reihen so bald nicht: Eine Kategorie, die fast nur
  // aus Shorts besteht, liefert je Seite ein bis zwei Raster-Videos, und ein Kanal mit
  // überwiegend Shorts genauso. Dort wird die Zeile nachgereicht — aber erst, wenn das
  // feststeht (Feed zu Ende oder zwei Seiten ohne vier volle Reihen), nicht schon nach
  // der ersten Seite. Sonst stünde sie auf der Startseite nach einer einzigen Reihe.
  // Sie rückt dabei an die letzte Reihengrenze davor, damit keine halbe Reihe neben ihr
  // leer bleibt; die angebrochene Reihe fließt darunter weiter und zählt für den
  // nächsten Abstand schon mit.
  const ensureShelf = () => {
    if (!makeShelf || shelves !== 0) return;
    const rest = since % Math.max(1, columnCount(list));
    list.insertBefore(makeShelf(), rest ? list.children[list.children.length - rest] : null);
    shelves++;
    since = rest;
  };

  const MAX_SEITEN_OHNE_ZEILE = 2;

  const append = async () => {
    if (loading || !feed?.nextPageToken) return;
    loading = true;
    more.replaceChildren(el('p', { className: 'state__text', textContent: 'Lädt …' }));
    try {
      const next = await loadPage(feed.nextPageToken);
      if (token !== navToken) return;
      feed = next;
      // Jede nachgeladene Seite bringt ihre eigenen Shorts mit und vergrößert damit den
      // Vorrat, aus dem sich die Zeilen von oben nach unten bedienen.
      makeShelf?.extend?.(next.shorts);
      gain = addCards(next.items);
      pages++;
      if (!placed || !next.nextPageToken || pages >= MAX_SEITEN_OHNE_ZEILE) ensureShelf();
      if (!next.nextPageToken) makeShelf?.done?.();
      renderMore();
    } catch (err) {
      if (token !== navToken) return;
      more.replaceChildren(
        el('p', { className: 'state__text', textContent: err.message }),
        el('button', { className: 'btn', textContent: 'Nochmal', onclick: append })
      );
    } finally {
      loading = false;
    }
  };

  // Sentinel-driven prefetch, with a button as the accessible fallback.
  const observer = new IntersectionObserver((entries) => {
    if (entries.some((e) => e.isIntersecting)) append();
  }, { rootMargin: '600px 0px' });

  function renderMore() {
    observer.disconnect();
    if (!feed?.nextPageToken) {
      more.replaceChildren();
      return;
    }
    const btn = el('button', { className: 'btn', textContent: 'Mehr laden', onclick: append });
    more.replaceChildren(btn);
    // Brachte die letzte Seite gar kein Raster-Video, bleibt der Knopf unbeobachtet:
    // Sonst stünde er weiter im Blickfeld und löste Seite um Seite aus — bei der Suche
    // 100 Kontingenteinheiten je Runde.
    if (gain > 0) observer.observe(btn);
  }

  gain = addCards(feed.items);
  pages = 1;
  if (!placed || !feed?.nextPageToken) ensureShelf();
  if (!feed?.nextPageToken) makeShelf?.done?.();
  renderMore();

  // Die erste Seite ist bewusst kurz, damit früh etwas dasteht. Die zweite wird deshalb
  // sofort nachgeholt, statt auf den Sichtbarkeits-Beobachter zu warten — der meldet
  // nichts, solange der Knopf außerhalb des Sichtfelds liegt.
  append();
}

// ---------------------------------------------------------------- views

async function homeView(categoryId) {
  const token = navToken;

  // Angemeldet und ohne Kategoriefilter zeigt die Startseite die eigene Mischung;
  // ein Chip schaltet bewusst auf die Trends dieser Kategorie um.
  const personal = Boolean(user);

  main.replaceChildren(
    el('div', { className: 'section__head' }, [
      el('div', {}, [
        el('p', { className: 'eyebrow', textContent: personal ? 'Nach deinen Likes' : 'Gerade beliebt' }),
        el('h1', { className: 'section__title', textContent: personal ? 'Für dich' : 'Im Trend' }),
      ]),
    ]),
    skeleton()
  );

  // Chips are optional — a failure there must not block the feed.
  api('/api/categories').then((cats) => {
    if (token !== navToken || !cats?.length) return;
    const chips = el('div', { className: 'chips', role: 'group' });
    chips.setAttribute('aria-label', 'Kategorien');
    const all = [{ id: '', title: 'Alle' }, ...cats];
    for (const c of all) {
      const chip = el('button', { className: 'chip', type: 'button', textContent: c.title });
      chip.setAttribute('aria-pressed', String((categoryId ?? '') === c.id));
      chip.onclick = () => { location.hash = c.id ? `#/?category=${c.id}` : '#/'; };
      chips.append(chip);
    }
    main.querySelector('.section__head')?.after(chips);
  }).catch(() => {});

  try {
    // Leere Parameter werden weggelassen — ein `page=` ist serverseitig sonst ein
    // Seitenzeiger und keine erste Seite, und die Shorts-Zeile bliebe leer.
    const source = (page) => {
      const query = new URLSearchParams();
      if (categoryId) query.set('category', categoryId);
      if (page) query.set('page', page);
      const path = personal ? '/api/home' : '/api/feed';
      return api(query.size ? `${path}?${query}` : path);
    };

    feed = await source();
    if (token !== navToken) return;
    main.querySelector('.skeleton')?.remove();

    if (!feed.items.length) {
      main.append(stateView({
        title: 'Nichts gefunden',
        text: personal
          ? 'Für eine eigene Mischung fehlen noch Anhaltspunkte. Bewerte ein paar Videos mit „Gefällt mir".'
          : 'Für diese Kategorie gibt es gerade keine Trends.',
      }));
      return;
    }
    const body = el('div');
    main.append(body);
    // Die Shorts-Zeile speist sich jetzt in beiden Fällen aus dem Feed selbst — auch
    // die Trends bringen ihre eigenen mit, statt der Regions-Shorts.
    // Ohne Kategorie und ohne Anmeldung stehen die Regions-Shorts bereit, falls der
    // Trend-Chart selbst keine mitbringt.
    feedList(body, source, 'feed',
      shortsShelfFactory(feed.shorts, { fallback: !personal && !categoryId }));
  } catch (err) {
    if (token !== navToken) return;
    main.replaceChildren(errorView(err.message, () => homeView(categoryId)));
  }
}

async function exploreView() {
  const token = navToken;
  main.replaceChildren(
    el('div', { className: 'section__head' }, [
      el('div', {}, [
        el('p', { className: 'eyebrow', textContent: 'Kategorien' }),
        el('h1', { className: 'section__title', textContent: 'Entdecken' }),
      ]),
    ]),
    skeleton(3)
  );

  try {
    const cats = await api('/api/categories');
    if (token !== navToken) return;
    main.querySelector('.skeleton')?.remove();

    const grid = el('div', { className: 'chips', style: 'flex-wrap:wrap;overflow:visible' });
    for (const c of cats) {
      grid.append(el('a', { className: 'chip', href: `#/?category=${c.id}`, textContent: c.title }));
    }
    main.append(grid);
  } catch (err) {
    if (token !== navToken) return;
    main.replaceChildren(errorView(err.message, exploreView));
  }
}

async function searchView(query) {
  const token = navToken;

  if (!query) {
    main.replaceChildren(stateView({
      title: 'Wonach suchst du?',
      text: 'Tippe oben in das Suchfeld, um Videos zu finden.',
    }));
    input.focus();
    return;
  }

  main.replaceChildren(
    el('div', { className: 'section__head' }, [
      el('div', {}, [
        el('p', { className: 'eyebrow', textContent: 'Suchergebnisse' }),
        el('h1', { className: 'section__title', textContent: query }),
      ]),
    ]),
    skeleton()
  );

  try {
    feed = await api(`/api/search?q=${encodeURIComponent(query)}`);
    if (token !== navToken) return;
    main.querySelector('.skeleton')?.remove();

    // Eine Suche wie „shorts" liefert unter Umständen nur Shorts. Dann ist das Raster
    // leer, gefunden wurde aber sehr wohl etwas — die Zeile trägt es.
    if (!feed.items.length && !feed.shorts?.length) {
      main.append(stateView({ title: 'Keine Treffer', text: `Für „${query}" wurde nichts gefunden.` }));
      return;
    }
    const body = el('div');
    main.append(body);
    // Auch die Suche trennt die Shorts ab; sie stehen in ihrer eigenen Zeile.
    feedList(body, (page) => api(`/api/search?q=${encodeURIComponent(query)}&page=${page}`), 'feed',
      shortsShelfFactory(feed.shorts ?? []));
  } catch (err) {
    if (token !== navToken) return;
    main.replaceChildren(errorView(err.message, () => searchView(query)));
    abosAlsNotnagel(query, token);
  }
}

/** Die Suche braucht search.list und fällt mit dessen Tageslimit aus; die Abo-Liste
    braucht es nicht. Trifft die Anfrage einen abonnierten Kanalnamen, führt sie
    wenigstens dorthin. Bewusst getrennt beschriftet — das sind keine Suchtreffer, und
    es soll auch nicht so aussehen. */
function abosAlsNotnagel(query, token) {
  if (!user) return;

  api(`/api/search/channels?q=${encodeURIComponent(query)}`).then((channels) => {
    if (token !== navToken || !channels?.length) return;

    const row = el('div', { className: 'chips', role: 'group' });
    row.setAttribute('aria-label', 'Passende Kanäle aus deinen Abos');
    for (const c of channels) {
      row.append(el('a', {
        className: 'chip',
        href: `#/channel/${c.id}?name=${encodeURIComponent(c.title)}`,
        textContent: c.title,
      }));
    }

    main.append(
      el('div', { className: 'section__head' }, [
        el('div', {}, [
          el('p', { className: 'eyebrow', textContent: 'Ohne Suche gefunden' }),
          el('h2', { className: 'section__title', textContent: 'Kanäle aus deinen Abos' }),
        ]),
      ]),
      row
    );
  }).catch(() => {});
}

async function channelView(id, name) {
  const token = navToken;
  main.replaceChildren(
    el('div', { className: 'section__head' }, [
      el('div', {}, [
        el('p', { className: 'eyebrow', textContent: 'Kanal' }),
        el('h1', { className: 'section__title', textContent: name || 'Neueste Videos' }),
      ]),
    ]),
    skeleton()
  );

  try {
    feed = await api(`/api/channel/${encodeURIComponent(id)}`);
    if (token !== navToken) return;
    main.querySelector('.skeleton')?.remove();

    if (!feed.items.length) {
      main.append(stateView({ title: 'Keine Videos', text: 'Dieser Kanal hat nichts Öffentliches.' }));
      return;
    }
    const body = el('div');
    main.append(body);
    // Die Shorts des Kanals kommen getrennt mit und stehen in ihrer eigenen Zeile,
    // statt das Raster zu durchsetzen.
    feedList(body, (page) => api(`/api/channel/${encodeURIComponent(id)}?page=${page}`), 'feed',
      shortsShelfFactory(feed.shorts ?? []));
  } catch (err) {
    if (token !== navToken) return;
    main.replaceChildren(errorView(err.message, () => channelView(id, name)));
  }
}

function commentRow(c, videoId, token, nested = false) {
  const avatar = c.authorThumb
    ? el('img', { className: 'comment__avatar', src: c.authorThumb, alt: '', loading: 'lazy', decoding: 'async', width: 36, height: 36, referrerPolicy: 'no-referrer' })
    : el('div', { className: 'comment__avatar' });

  // Bild und Name führen zum Kanal, wie bei YouTube. Bei gelöschten Konten liefert
  // Google keine Kanal-Id mit — dann bleibt beides schlichter Text.
  const channelHref = c.authorChannelId
    ? `#/channel/${c.authorChannelId}?name=${encodeURIComponent(c.author)}`
    : null;
  const toChannel = (child, label) => {
    if (!channelHref) return child;
    const link = el('a', { className: 'comment__link', href: channelHref }, [child]);
    if (label) link.setAttribute('aria-label', label);
    return link;
  };

  const foot = el('div', { className: 'comment__foot' });
  if (c.likesLabel) {
    const like = el('span');
    like.innerHTML = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M2 21h4V9H2v12Zm20-10a2 2 0 0 0-2-2h-6.3l.95-4.57.03-.32a1.5 1.5 0 0 0-.44-1.06L13.17 2 6.6 8.59A2 2 0 0 0 6 10v9a2 2 0 0 0 2 2h9a2 2 0 0 0 1.84-1.22l3.02-7.05c.09-.23.14-.47.14-.73v-1Z"/></svg> ';
    like.append(c.likesLabel);
    foot.append(like);
  }

  const replies = el('ul', { className: 'comments comments--replies' });
  const replyBox = el('div');
  const editBox = el('div');
  const text = el('p', { className: 'comment__text' }, linkify(c.text));

  if (c.replyCount > 0) {
    // Die Antworten kommen erst auf Klick — ein eigener Aufruf pro Strang, sonst
    // würde jede Kommentarseite Dutzende davon auf einmal nachladen.
    let loaded = false;
    const label = c.replyCount === 1 ? '1 Antwort' : `${c.replyCount} Antworten`;
    const toggle = el('button', { className: 'link-btn', type: 'button', textContent: label });

    toggle.onclick = async () => {
      if (loaded) {
        replies.hidden = !replies.hidden;
        toggle.textContent = replies.hidden ? label : 'Antworten ausblenden';
        return;
      }
      toggle.disabled = true;
      try {
        const data = await api(`/api/comments/${encodeURIComponent(videoId)}/replies?parent=${encodeURIComponent(c.id)}`);
        if (token !== navToken) return;
        replies.append(...data.items.map((r) => commentRow(r, videoId, token, true)));
        loaded = true;
        replies.hidden = false;
        toggle.textContent = 'Antworten ausblenden';
      } catch (err) {
        showBanner(err.message);
      } finally {
        toggle.disabled = false;
      }
    };

    foot.append(toggle);
  }

  // Auf eine Antwort wird bei YouTube nicht noch einmal verschachtelt geantwortet,
  // deshalb bekommen nur Kommentare der obersten Ebene ein Antwortfeld.
  if (user && !nested) {
    const reply = el('button', { className: 'link-btn', type: 'button', textContent: 'Antworten' });
    // Solange das Feld offen steht, ist derselbe Knopf der Weg zurück — sonst ist nicht
    // zu erkennen, wie man es wieder zuklappt.
    const closeReply = () => {
      replyBox.replaceChildren();
      reply.textContent = 'Antworten';
    };
    reply.onclick = () => {
      if (replyBox.children.length) {
        closeReply();
        return;
      }
      replyBox.replaceChildren(composer('Antworten', 'Antwort schreiben …', async (text) => {
        const created = await send(`/api/comments/${encodeURIComponent(videoId)}/reply`, 'POST', { text, parentId: c.id });
        replies.hidden = false;
        replies.append(commentRow(created, videoId, token, true));
        closeReply();
      }));
      reply.textContent = 'Abbrechen';
      replyBox.querySelector('.composer__field')?.focus();
    };
    foot.append(reply);
  }

  // Eigene Kommentare lassen sich nachträglich ändern und löschen — beides gilt für
  // Antworten genauso, sie sind für die API dieselbe Ressource.
  if (user && c.isOwn) {
    const edit = el('button', { className: 'link-btn', type: 'button', textContent: 'Bearbeiten' });
    const closeEdit = () => {
      editBox.replaceChildren();
      text.hidden = false;
      edit.textContent = 'Bearbeiten';
    };
    edit.onclick = () => {
      if (editBox.children.length) {
        closeEdit();
        return;
      }
      text.hidden = true;
      editBox.replaceChildren(composer('Speichern', 'Kommentar bearbeiten …', async (next) => {
        const updated = await send(`/api/comments/${encodeURIComponent(c.id)}`, 'PATCH', { text: next });
        c.text = updated.text;
        text.replaceChildren(...linkify(c.text));
        closeEdit();
      }, c.text));
      edit.textContent = 'Abbrechen';
      editBox.querySelector('.composer__field')?.focus();
    };

    const remove = el('button', { className: 'link-btn link-btn--danger', type: 'button', textContent: 'Löschen' });
    remove.onclick = async () => {
      // Ein gelöschter Kommentar der obersten Ebene nimmt seinen ganzen Strang mit.
      const question = !nested && c.replyCount > 0
        ? 'Diesen Kommentar mit allen Antworten löschen?'
        : 'Diesen Kommentar löschen?';
      if (!confirm(question)) return;

      remove.disabled = true;
      try {
        await send(`/api/comments/${encodeURIComponent(c.id)}`, 'DELETE');
        row.remove();
      } catch (err) {
        showBanner(err.message);
        remove.disabled = false;
      }
    };

    foot.append(edit, remove);
  }

  const body = el('div', { className: 'comment__body' }, [
    el('div', { className: 'comment__head' }, [
      toChannel(el('span', { className: 'comment__author', textContent: c.author })),
      el('span', { className: 'comment__age', textContent: c.publishedLabel }),
    ]),
    text,
    editBox,
    foot.children.length ? foot : null,
    replyBox,
    replies,
  ]);

  const row = el('li', { className: 'comment' }, [toChannel(avatar, `Kanal von ${c.author}`), body]);
  if (c.isOwn) row.classList.add('comment--own');
  return row;
}

/** Eingabefeld zum Abschicken eines Kommentars. `onSubmit` bekommt den Text und darf
    werfen — die Meldung landet dann unter dem Feld statt in einem Banner. */
function composer(label, placeholder, onSubmit, initial = '') {
  const field = el('textarea', { className: 'composer__field', rows: 1, placeholder, value: initial });
  field.setAttribute('aria-label', label);

  const note = el('p', { className: 'composer__note' });
  const submit = el('button', { className: 'btn btn--accent', type: 'button', textContent: label });

  // Das Feld wächst mit dem Text, statt einen eigenen Rollbalken zu zeigen.
  const grow = () => {
    field.style.height = 'auto';
    field.style.height = `${field.scrollHeight}px`;
  };
  field.addEventListener('input', grow);

  submit.onclick = async () => {
    const text = field.value.trim();
    if (!text) return;

    submit.disabled = true;
    note.textContent = '';
    try {
      await onSubmit(text);
      field.value = '';
      grow();
    } catch (err) {
      note.textContent = err.message;
    } finally {
      submit.disabled = false;
    }
  };

  const box = el('div', { className: 'composer' }, [
    field,
    el('div', { className: 'composer__foot' }, [submit, note]),
  ]);
  // Ein vorbefülltes Feld startet mit der Höhe des Textes, nicht mit einer Zeile.
  if (initial) requestAnimationFrame(grow);
  return box;
}

/** Loads and appends comments into `container`, with its own paging state. */
async function mountComments(container, videoId, token) {
  const head = el('div', { className: 'comments__head' }, [
    el('h2', { className: 'comments__count', textContent: 'Kommentare' }),
  ]);
  const list = el('ul', { className: 'comments' });
  const more = el('div', { className: 'more' });
  const box = el('div');
  container.replaceChildren(head, box, list, more);

  if (user) {
    box.append(composer('Kommentieren', 'Öffentlich kommentieren …', async (text) => {
      const created = await send(`/api/comments/${encodeURIComponent(videoId)}`, 'POST', { text });
      // commentThreads.list braucht einige Sekunden, bis es den frischen Kommentar
      // selbst zurückliefert — deshalb wandert er sofort lokal nach oben.
      list.prepend(commentRow(created, videoId, token));
      if (!next) more.replaceChildren();
    }));
  }

  let next = null;
  let loading = false;

  const load = async () => {
    if (loading) return;
    loading = true;
    more.replaceChildren(el('p', { className: 'state__text', textContent: 'Lädt …' }));
    try {
      const data = await api(`/api/comments/${encodeURIComponent(videoId)}${next ? `?page=${next}` : ''}`);
      if (token !== navToken) return;

      if (data.disabled) {
        container.replaceChildren(el('div', { className: 'state state--inline' }, [
          el('p', { className: 'state__text', textContent: 'Für dieses Video sind Kommentare deaktiviert.' }),
        ]));
        return;
      }

      list.append(...data.items.map((c) => commentRow(c, videoId, token)));
      next = data.nextPageToken;
      more.replaceChildren(next
        ? el('button', { className: 'btn', textContent: 'Mehr Kommentare', onclick: load })
        : '');

      if (!list.children.length) {
        more.replaceChildren(el('p', { className: 'state__text', textContent: 'Noch keine Kommentare.' }));
      }
    } catch (err) {
      if (token !== navToken) return;
      more.replaceChildren(
        el('p', { className: 'state__text', textContent: err.message }),
        el('button', { className: 'btn', textContent: 'Nochmal', onclick: load })
      );
    } finally {
      loading = false;
    }
  };

  await load();
}

const THUMB_PATH = '<path d="M2 21h4V9H2v12Zm20-10a2 2 0 0 0-2-2h-6.3l.95-4.57.03-.32a1.5 1.5 0 0 0-.44-1.06L13.17 2 6.6 8.59A2 2 0 0 0 6 10v9a2 2 0 0 0 2 2h9a2 2 0 0 0 1.84-1.22l3.02-7.05c.09-.23.14-.47.14-.73v-1Z"/>';

/** Bewerten und Abonnieren. Beide Knöpfe schalten sofort um und nehmen die Änderung
    zurück, wenn YouTube sie ablehnt — 50 Kontingenteinheiten pro Klick sind zu teuer,
    um auf eine Bestätigung zu warten. */
function watchActions(v, token) {
  let rating = 'none';
  let subscribed = false;

  const thumb = (rotated) =>
    `<svg viewBox="0 0 24 24" aria-hidden="true"${rotated ? ' style="transform:rotate(180deg)"' : ''}>${THUMB_PATH}</svg>`;

  const up = el('button', { className: 'action', type: 'button' });
  up.innerHTML = thumb(false);
  up.append('Gefällt mir');

  const down = el('button', { className: 'action', type: 'button' });
  down.innerHTML = thumb(true);
  down.append('Gefällt mir nicht');

  const paintRating = () => {
    up.setAttribute('aria-pressed', String(rating === 'like'));
    down.setAttribute('aria-pressed', String(rating === 'dislike'));
  };

  const rate = async (wanted) => {
    const before = rating;
    // Ein zweiter Klick auf dieselbe Bewertung nimmt sie zurück, wie bei YouTube.
    rating = before === wanted ? 'none' : wanted;
    paintRating();
    try {
      await send(`/api/rating/${encodeURIComponent(v.id)}`, 'POST', { rating });
    } catch (err) {
      rating = before;
      paintRating();
      showBanner(err.message);
    }
  };

  up.onclick = () => rate('like');
  down.onclick = () => rate('dislike');
  paintRating();

  const sub = el('button', { className: 'action action--sub', type: 'button' });

  const paintSub = () => {
    sub.textContent = subscribed ? 'Abonniert' : 'Abonnieren';
    sub.classList.toggle('action--on', subscribed);
  };

  sub.onclick = async () => {
    const before = subscribed;
    subscribed = !before;
    paintSub();
    try {
      await send(`/api/subscriptions/${encodeURIComponent(v.channelId)}`, before ? 'DELETE' : 'POST');
    } catch (err) {
      subscribed = before;
      paintSub();
      showBanner(err.message);
    }
  };

  paintSub();

  // Die echten Zustände kommen nach; bis dahin stehen die Knöpfe auf "nicht bewertet"
  // und "nicht abonniert". Ein Fehler hier ist kein Grund, die Seite zu stören.
  api(`/api/rating/${encodeURIComponent(v.id)}`)
    .then((r) => { if (token === navToken) { rating = r.rating; paintRating(); } })
    .catch(() => {});

  api(`/api/subscriptions/state/${encodeURIComponent(v.channelId)}`)
    .then((state) => { if (token === navToken) { subscribed = state.subscribed; paintSub(); } })
    .catch(() => {});

  return el('div', { className: 'actions' }, [up, down, sub]);
}

async function watchView(id) {
  const token = navToken;
  main.replaceChildren(el('div', { className: 'watch-layout' }, [el('div', { className: 'player' })]));

  let detail;
  try {
    detail = await api(`/api/video/${encodeURIComponent(id)}`);
  } catch (err) {
    if (token !== navToken) return;
    main.replaceChildren(errorView(err.message, () => watchView(id)));
    return;
  }
  if (token !== navToken) return;

  const v = detail.video;
  document.title = `${v.title} — YouTube`;

  const player = el('div', { className: 'player' });
  if (v.embeddable) {
    // enablejsapi öffnet den postMessage-Kanal, über den die Tastenkürzel laufen.
    // origin muss dabei mit, sonst weist der Player die Nachrichten zurück.
    const frame = el('iframe', {
      src: embedUrl(id, `enablejsapi=1&origin=${encodeURIComponent(location.origin)}`),
      title: v.title,
      allow: 'accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture; web-share',
      allowFullscreen: true,
      referrerPolicy: 'strict-origin-when-cross-origin',
      loading: 'eager',
    });
    // Der Handshake der IFrame-Player-API; ohne ihn ignoriert der Player Befehle,
    // die vor dem ersten Klick ins Video ankommen.
    frame.addEventListener('load', () => {
      frame.contentWindow?.postMessage(
        JSON.stringify({ event: 'listening', id: 1, channel: 'widget' }), EMBED_ORIGIN);
    });

    // Die Länge aus dem Etikett trägt die Zifferntasten, bis der Player seine eigene
    // meldet — sonst liefe der erste Tastendruck ins Leere.
    watchPlayer = {
      frame,
      seconds: labelSeconds(v.durationLabel),
      time: 0,
      at: performance.now(),
      state: -1,
      muted: false,
      volume: null,
    };

    player.append(frame);
  } else {
    // The uploader disabled embedding; the API tells us up front, so offer the
    // one thing that does work instead of showing a broken player.
    player.append(el('div', { className: 'state', style: 'display:grid;place-content:center;height:100%' }, [
      el('p', { className: 'state__text', textContent: 'Dieses Video darf nicht eingebettet werden.' }),
      el('a', {
        className: 'btn btn--accent',
        href: `https://m.youtube.com/watch?v=${encodeURIComponent(id)}&persist_app=1&app=m`,
        target: '_blank',
        rel: 'noopener noreferrer',
        textContent: 'Auf YouTube ansehen',
      }),
    ]));
  }

  const meta = el('p', { className: 'watch__meta' }, [v.viewsLabel ?? 'Aufrufe verborgen']);
  meta.append(el('span', { className: 'dot', textContent: '•' }), v.publishedLabel);
  if (detail.likesLabel) {
    meta.append(el('span', { className: 'dot', textContent: '•' }), `${detail.likesLabel} Likes`);
  }

  const channelHref = `#/channel/${v.channelId}?name=${encodeURIComponent(v.channelTitle)}`;

  const column = el('div', { className: 'watch__main' }, [
    el('h1', { className: 'watch__title', textContent: v.title }),
    meta,
    el('div', { className: 'watch__channel' }, [
      el('a', { className: 'watch__channel-name', href: channelHref, textContent: v.channelTitle }),
      el('a', { className: 'btn', href: channelHref, textContent: 'Videos' }),
    ]),
  ]);

  if (user) column.append(watchActions(v, token));

  if (detail.description.trim()) {
    column.append(el('details', { className: 'desc' }, [
      el('summary', { textContent: 'Beschreibung' }),
      el('p', { className: 'desc__text' }, linkify(detail.description)),
    ]));
  }

  const commentsBox = el('section');
  column.append(commentsBox);

  // Die Data API hat relatedToVideoId gestrichen; /api/related nähert die Liste über
  // die Stichwörter des Videos an. Bleibt sie leer, springt die Spalte auf den Kanal
  // zurück — lieber etwas Passendes als gar nichts.
  const rail = el('aside', { className: 'watch__rail' }, [
    el('p', { className: 'eyebrow', textContent: 'Weiter geht’s' }),
  ]);
  const railTitle = el('h2', { className: 'section__title', style: 'margin-bottom:1rem', textContent: 'Ähnliche Videos' });
  rail.append(railTitle);
  const railBody = el('div');
  rail.append(railBody);

  main.replaceChildren(el('div', { className: 'watch-layout' }, [player, column, rail]));

  // Comments and the rail are independent; load them in parallel and let each
  // fail on its own without taking the page down.
  mountComments(commentsBox, id, token);

  try {
    // Ein Fehlschlag hier ist kein Grund für eine leere Spalte: Die Suche kann am
    // Tageskontingent scheitern, der Kanal-Weg kostet dagegen fast nichts.
    const similar = await api(`/api/related/${encodeURIComponent(id)}`).catch(() => null);
    if (token !== navToken) return;

    if (similar?.items.length) {
      feed = similar;
      feedList(railBody, () => Promise.resolve(similar), 'feed feed--rail');
      return;
    }

    // Neben einem laufenden Short dürfen auch in der Spalte Shorts stehen; sonst nicht.
    const channelFeed = (page) =>
      api(`/api/channel/${encodeURIComponent(v.channelId)}?page=${page ?? ''}${detail.isShort ? '&shorts=1' : ''}`);

    const more = await channelFeed();
    if (token !== navToken) return;
    railTitle.textContent = `Mehr von ${v.channelTitle}`;
    feed = { ...more, items: more.items.filter((x) => x.id !== id) };
    feedList(railBody, channelFeed, 'feed feed--rail');
  } catch {
    /* The main video already rendered; a failed rail is not worth an error. */
  }
}

async function likesView() {
  const token = navToken;

  if (!user) {
    main.replaceChildren(signInView());
    return;
  }

  main.replaceChildren(
    el('div', { className: 'section__head' }, [
      el('div', {}, [
        el('p', { className: 'eyebrow', textContent: 'Zuletzt bewertet' }),
        el('h1', { className: 'section__title', textContent: 'Gefällt mir' }),
      ]),
    ]),
    skeleton()
  );

  try {
    feed = await api('/api/likes');
    if (token !== navToken) return;
    main.querySelector('.skeleton')?.remove();

    if (!feed.items.length) {
      main.append(stateView({
        title: 'Noch nichts bewertet',
        text: 'Videos, die dir gefallen, sammeln sich hier — und prägen deine Startseite.',
      }));
      return;
    }

    const body = el('div');
    main.append(body);
    feedList(body, (page) => api(`/api/likes?page=${page}`));
  } catch (err) {
    if (token !== navToken) return;
    main.replaceChildren(user ? errorView(err.message, likesView) : signInView());
  }
}

function signInView() {
  return stateView({
    title: 'Mit YouTube anmelden',
    text: loginAvailable
      ? 'Melde dich mit deinem Google-Konto an, um deine Abos und den Abo-Feed zu sehen, zu kommentieren und zu bewerten.'
      : 'Für die Anmeldung fehlen dem Server noch die OAuth-Zugangsdaten.',
    extra: loginAvailable
      ? el('a', { className: 'btn btn--accent', href: loginHref(), textContent: 'Anmelden' })
      : el('div', {}, [
          // Die Adresse muss zu der passen, die der Server an Google schickt — und die
          // baut er aus Schema und Host der Anfrage (SessionStore.RedirectUri). Fest
          // eingetragen zeigte die Anleitung auf localhost, sobald man von woanders
          // kam, und der eingetragene Wert passte dann nicht.
          el('p', { className: 'state__text', textContent: `In der Google Cloud Console einen OAuth-Client (Webanwendung) anlegen, ${location.origin}/api/auth/callback als Weiterleitung eintragen und hinterlegen:` }),
          el('pre', { textContent: 'cd YtMobile\ndotnet user-secrets set "Google:ClientId" "…"\ndotnet user-secrets set "Google:ClientSecret" "…"' }),
        ]),
  });
}

async function abosView() {
  const token = navToken;

  if (!user) {
    main.replaceChildren(signInView());
    return;
  }

  main.replaceChildren(
    el('div', { className: 'section__head' }, [
      el('div', {}, [
        el('p', { className: 'eyebrow', textContent: 'Deine Kanäle' }),
        el('h1', { className: 'section__title', textContent: 'Abos' }),
      ]),
    ]),
    skeleton()
  );

  // Die Kanalzeile ist Beiwerk — ein Fehler dort darf den Feed nicht aufhalten.
  api('/api/subscriptions').then((channels) => {
    if (token !== navToken || !channels?.length) return;
    const row = el('div', { className: 'chips', role: 'group' });
    row.setAttribute('aria-label', 'Abonnierte Kanäle');
    for (const c of channels) {
      row.append(el('a', {
        className: 'chip',
        href: `#/channel/${c.id}?name=${encodeURIComponent(c.title)}`,
        textContent: c.title,
      }));
    }
    main.querySelector('.section__head')?.after(row);
  }).catch(() => {});

  try {
    feed = await api('/api/subscriptions/feed');
    if (token !== navToken) return;
    main.querySelector('.skeleton')?.remove();

    if (!feed.items.length) {
      main.append(stateView({
        title: 'Nichts Neues',
        text: 'In deinen Abos gibt es gerade keine neuen Videos.',
      }));
      return;
    }

    const body = el('div');
    main.append(body);
    feedList(
      body,
      (page) => api(`/api/subscriptions/feed?page=${page}`),
      'feed',
      shortsShelfFactory(feed.shorts ?? [])
    );
  } catch (err) {
    if (token !== navToken) return;
    // Ist die Anmeldung unterwegs verfallen, hat request() sie schon verworfen.
    main.replaceChildren(user ? errorView(err.message, abosView) : signInView());
  }
}

function setupView() {
  return stateView({
    title: 'Noch ein Schritt: API-Key',
    text: 'Die App holt ihre Daten über die offizielle YouTube Data API v3. Dafür braucht sie einen kostenlosen Google-API-Key.',
    extra: el('div', {}, [
      el('ol', {}, [
        el('li', {}, [
          'Projekt anlegen in der ',
          el('a', { href: 'https://console.cloud.google.com/', target: '_blank', rel: 'noopener noreferrer', textContent: 'Google Cloud Console', style: 'color:var(--gold-light);text-decoration:underline' }),
        ]),
        el('li', { textContent: '„YouTube Data API v3" aktivieren' }),
        el('li', { textContent: 'Unter „Anmeldedaten" einen API-Key erstellen' }),
        el('li', { textContent: 'Key hinterlegen und die App neu starten:' }),
      ]),
      el('pre', { textContent: 'cd YtMobile\ndotnet user-secrets set "YouTube:ApiKey" "DEIN_KEY"' }),
      el('p', { className: 'state__text', style: 'margin-top:1rem', textContent: 'Alternativ als Umgebungsvariable YOUTUBE_API_KEY.' }),
    ]),
  });
}

// ---------------------------------------------------------------- router

function parseRoute() {
  const raw = location.hash.slice(1) || '/';
  const [path, queryString] = raw.split('?');
  return { path, query: new URLSearchParams(queryString ?? '') };
}

async function route() {
  navToken++;
  feed = null;
  watchPlayer = null;
  zeigerAufraeumen();
  stopPreview();
  document.title = 'YouTube';

  const { path, query } = parseRoute();
  const segments = path.split('/').filter(Boolean);

  // Reflect the route in the tab bar and the search box.
  const tab = segments[0] === 'watch' || segments[0] === 'channel' ? null : (segments[0] || 'home');
  for (const link of document.querySelectorAll('.tab')) {
    if (link.dataset.tab === tab) link.setAttribute('aria-current', 'page');
    else link.removeAttribute('aria-current');
  }
  // Der Anmelde-Knopf der Kopfzeile wird nur einmal gebaut, die Route wechselt aber
  // ohne Neuladen — sein Rücksprungziel muss deshalb hier nachgezogen werden.
  accountBox?.querySelector('.account__login')?.setAttribute('href', loginHref());

  const q = segments[0] === 'search' ? query.get('q') ?? '' : '';
  input.value = q;
  clearBtn.hidden = !q;

  window.scrollTo(0, 0);

  const cfg = await ensureConfig();
  if (!cfg.configured) {
    main.replaceChildren(setupView());
    return;
  }

  switch (segments[0]) {
    case 'watch': return watchView(segments[1]);
    case 'search': return searchView(q);
    case 'explore': return exploreView();
    case 'abos': return abosView();
    case 'likes': return likesView();
    case 'channel': return channelView(segments[1], query.get('name'));
    default: return homeView(query.get('category'));
  }
}

/** Einmal geladene Serverkonfiguration samt Anmeldezustand. */
let config = null;

async function ensureConfig() {
  if (config) return config;
  // Die Seite bringt sie schon mit — das erspart eine Runde und, wenn jemand angemeldet
  // ist, den Token-Abruf bei Google davor. Der Aufruf bleibt für den Fall, dass die
  // Seite aus einem Cache kommt oder sich die Anmeldung inzwischen geändert hat.
  if (window.__config) {
    config = window.__config;
  } else {
    try {
      config = await api('/api/config');
    } catch {
      // Let the individual view surface the real error.
      config = { configured: true, loginAvailable: false, user: null };
    }
  }
  user = config.user ?? null;
  loginAvailable = config.loginAvailable ?? false;
  renderAccount();
  return config;
}

form.addEventListener('submit', (e) => {
  e.preventDefault();
  const q = input.value.trim();
  input.blur();
  location.hash = q ? `#/search?q=${encodeURIComponent(q)}` : '#/search';
});

input.addEventListener('input', () => { clearBtn.hidden = !input.value; });

clearBtn.addEventListener('click', () => {
  input.value = '';
  clearBtn.hidden = true;
  input.focus();
});

window.addEventListener('hashchange', route);

// Scheitert die Anmeldung, schickt der Server die Meldung als Query an die Wurzel-URL.
// Sie wird einmal gezeigt und danach aus der Adresszeile entfernt.
const authError = new URLSearchParams(location.search).get('authError');
if (authError) {
  history.replaceState(null, '', location.pathname + location.hash);
  showBanner(authError);
}

route();
