#!/usr/bin/env bash
# Übersetzt YtMobile vorab in eine native Binärdatei (Native AOT) nach ./nativ.
#
# Warum im Container: Native AOT bindet die Binärdatei mit clang und braucht dafür
# statische Systembibliotheken. Dieses System ist ostree-basiert — Einschichten hieße
# Neustart. Das Abbild ist dieselbe Fedora-Fassung wie der Wirt, die Bibliotheken
# passen also zusammen. SDK und NuGet-Zwischenspeicher werden aus dem Home
# hineingereicht, statt beides im Abbild zu doppeln.
set -euo pipefail
cd "$(dirname "$0")"

ABBILD=ytmobile-aot

if ! podman image exists "$ABBILD"; then
  echo "→ Bauabbild »$ABBILD« fehlt. Wird einmalig erzeugt, das dauert ein paar Minuten."
  podman build -t "$ABBILD" -f - . <<'INNEN'
FROM registry.fedoraproject.org/fedora:44
# clang und lld übersetzen und binden; die -devel-Pakete liefern die statischen
# Bibliotheken, gegen die der ILC bindet. libicu braucht schon das SDK selbst —
# ohne das bricht `dotnet` mit "Couldn't find a valid ICU package" ab, noch bevor
# irgendetwas übersetzt wird.
RUN dnf -y install --setopt=install_weak_deps=False \
      clang lld binutils libicu \
      zlib-ng-compat-devel openssl-devel krb5-devel \
 && dnf clean all
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
INNEN
fi

echo "→ Übersetze nativ …"
BEGINN=$SECONDS

# HOME und DOTNET_CLI_HOME zeigen auf /tmp: Das Home des Wirts ist nur lesbar
# eingehängt, das SDK will aber irgendwohin schreiben dürfen.
podman run --rm \
  -v "$HOME/.dotnet:/dotnet:ro,z" \
  -v "$HOME/.nuget:/nuget:z" \
  -v "$PWD:/w:z" \
  -e HOME=/tmp \
  -e DOTNET_CLI_HOME=/tmp \
  -e NUGET_PACKAGES=/nuget/packages \
  -e PATH=/dotnet:/usr/bin:/bin \
  "$ABBILD" \
  dotnet publish /w/YtMobile/YtMobile.csproj -r linux-x64 -c Release -o /w/nativ

DAUER=$(( SECONDS - BEGINN ))
GROESSE=$(du -h nativ/YtMobile | cut -f1)
echo "→ Fertig in ${DAUER}s — nativ/YtMobile, $GROESSE"

# Sonst liefe der Dienst weiter mit der alten Binärdatei, ohne dass irgendwo etwas davon
# stünde. Läuft keiner, passiert hier nichts.
if systemctl --user is-active --quiet ytmobile; then
  systemctl --user restart ytmobile
  echo "→ Dienst neu gestartet."
fi
