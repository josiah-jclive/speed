#!/bin/bash
# Builds JSPEED.app and a .dmg for one Mac architecture. Run on a Mac.
#   packaging/mac/build-dmg.sh osx-arm64   (Apple silicon)
#   packaging/mac/build-dmg.sh osx-x64     (Intel)
# Build the DeckLink bridge first (see README) to include UltraStudio output.
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/artifacts/$RID"
APP="$OUT/JSPEED.app"

rm -rf "$OUT"
dotnet publish "$ROOT/src/Speed/Speed.csproj" -c Release -r "$RID" --self-contained true \
  -p:UseAppHost=true -o "$OUT/publish"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/publish/." "$APP/Contents/MacOS/"
cp "$ROOT/packaging/mac/Info.plist" "$APP/Contents/Info.plist"
cp "$ROOT/packaging/mac/JSPEED.icns" "$APP/Contents/Resources/JSPEED.icns"

BRIDGE="$ROOT/native/build/libspeed_decklink.dylib"
if [[ -f "$BRIDGE" ]]; then
  cp "$BRIDGE" "$APP/Contents/MacOS/"
else
  echo "Note: $BRIDGE not found, so this build has no UltraStudio output."
fi

# Ad-hoc signature. For distribution, sign with a Developer ID and notarize instead (see README).
codesign --force --deep --sign - "$APP"

STAGE="$OUT/dmg"
mkdir -p "$STAGE"
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "JSPEED" -srcfolder "$STAGE" -ov -format UDZO "$OUT/JSPEED-$RID.dmg"
echo "Built $OUT/JSPEED-$RID.dmg"
