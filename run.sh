#!/usr/bin/env bash
# Startet die App lokal. Seit der Umstellung auf Native AOT läuft die vorab übersetzte
# Binärdatei aus ./nativ — nicht mehr `dotnet run`. Ist eine Quelldatei jünger als die
# Binärdatei, wird vorher neu gebaut; ein Befehl wie bisher, nur gelegentlich langsamer.
set -euo pipefail

export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export ASPNETCORE_URLS="${ASPNETCORE_URLS:-https://0.0.0.0:5099}"
# Muss Development sein: dotnet user-secrets wird sonst gar nicht erst geladen.
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"

cd "$(dirname "$0")"

# obj/ und bin/ bleiben außen vor — dort schreibt der Bau selbst hinein und würde sich
# damit beim nächsten Start als „neue Quelle“ ausgeben. wwwroot zählt mit, denn auch
# CSS und JS werden beim Veröffentlichen nach nativ/ kopiert.
if [[ ! -x nativ/YtMobile ]]; then
  echo "→ Noch keine Binärdatei."
  ./bauen.sh
elif [[ -n "$(find YtMobile \( -name obj -o -name bin \) -prune -o \
              -type f -newer nativ/YtMobile -print -quit)" ]]; then
  echo "→ Quellcode ist neuer als die Binärdatei."
  ./bauen.sh
fi

# Ohne Zertifikat startet Kestrel gar nicht erst — lieber hier eine Zeile sagen, was
# fehlt, als eine Ausnahme aus der Tiefe.
if [[ "$ASPNETCORE_URLS" == https* ]] && ! dotnet dev-certs https --check >/dev/null 2>&1; then
  echo "! Kein Entwicklerzertifikat. Einmalig:  dotnet dev-certs https" >&2
  echo "  Danach vertrauen, siehe README, \"Warum HTTPS\"." >&2
fi

# Läuft der Dienst schon, gehört ihm der Port. Ohne diese Zeile bräche Kestrel weiter
# unten mit „address already in use" aus der Tiefe ab.
if ss -ltn 2>/dev/null | grep -q ':5099 '; then
  echo "! Auf 5099 lauscht schon etwas — vermutlich der Dienst." >&2
  echo "  systemctl --user stop ytmobile" >&2
  exit 1
fi

echo "→ https://localhost:5099"
# 0.0.0.0 hält die App auch vom Handy im gleichen WLAN erreichbar. Das Zertifikat
# kennt allerdings nur localhost, dort kommt also eine Namenswarnung — und mit
# Zertifikatsfehler bietet der Browser das Installieren als Kachel nicht an.
ip -4 addr show scope global 2>/dev/null \
  | awk '/inet /{split($2,a,"/"); print "→ https://" a[1] ":5099 (im WLAN, mit Zertifikatswarnung)"}' || true

# Zurück auf unverschlüsselt, falls das Zertifikat einmal klemmt:
#   ASPNETCORE_URLS=http://0.0.0.0:5099 ./run.sh
#
# Aus nativ/ heraus starten, nicht von außen: Der Inhaltsstamm ist das
# Arbeitsverzeichnis, und ohne ihn findet die App ihr wwwroot nicht.
cd nativ
exec ./YtMobile "$@"
