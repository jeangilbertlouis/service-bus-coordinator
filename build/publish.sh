#!/usr/bin/env bash
# Publishes Service Bus Coordinator as a self-contained single-file app.
#   linux-*  -> artifacts/ServiceBusCoordinator-<version>-<rid>.tar.gz
#   osx-*    -> artifacts/ServiceBusCoordinator-<version>-<rid>.zip containing "Service Bus Coordinator.app"
# Usage: build/publish.sh <rid> [version]      e.g. build/publish.sh osx-arm64 1.2.3
set -euo pipefail

RID="${1:?usage: build/publish.sh <rid> [version]}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT/src/ServiceBusCoordinator.App/ServiceBusCoordinator.App.csproj"
VERSION="${2:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -n 1)}"
ARTIFACTS="$ROOT/artifacts"
OUT="$ARTIFACTS/$RID"
NAME="ServiceBusCoordinator-$VERSION-$RID"

rm -rf "$OUT"
mkdir -p "$OUT"

echo "Publishing $VERSION for $RID..."
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:Version="$VERSION" -p:DebugType=none -o "$OUT/publish"

# Native packages (SkiaSharp, HarfBuzz) ship ~100 MB of debug symbols; users don't need them.
find "$OUT/publish" -name '*.pdb' -delete

case "$RID" in
  osx-*)
    APP="$OUT/Service Bus Coordinator.app"
    mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
    sed "s/__VERSION__/$VERSION/g" "$ROOT/build/macos/Info.plist" > "$APP/Contents/Info.plist"
    chmod +x "$APP/Contents/MacOS/ServiceBusCoordinator"
    rm -f "$ARTIFACTS/$NAME.zip"
    (cd "$OUT" && ditto -c -k --keepParent "Service Bus Coordinator.app" "$ARTIFACTS/$NAME.zip" 2>/dev/null \
      || zip -qry "$ARTIFACTS/$NAME.zip" "Service Bus Coordinator.app")
    echo "Created $ARTIFACTS/$NAME.zip"
    ;;
  linux-*)
    chmod +x "$OUT/publish/ServiceBusCoordinator"
    tar -czf "$ARTIFACTS/$NAME.tar.gz" -C "$OUT/publish" .
    echo "Created $ARTIFACTS/$NAME.tar.gz"
    ;;
  win-*)
    rm -f "$ARTIFACTS/$NAME.zip"
    (cd "$OUT/publish" && zip -qr "$ARTIFACTS/$NAME.zip" .)
    echo "Created $ARTIFACTS/$NAME.zip"
    ;;
  *)
    echo "Unknown runtime '$RID'" >&2
    exit 1
    ;;
esac
