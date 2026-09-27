#!/usr/bin/env bash
# Build "TOC Extractor.app" and a disk image of it for one Mac architecture.
#
#   packaging/macos/make-app.sh osx-arm64   # Apple silicon
#   packaging/macos/make-app.sh osx-x64     # Intel
#
# Writes dist/TOC-Extractor-<version>-macos-<arch>.dmg: open it and drag the
# app onto Applications. Self-contained: the person downloading it needs no
# .NET installed.
set -euo pipefail

rid="${1:?usage: make-app.sh osx-arm64|osx-x64}"
root="$(cd "$(dirname "$0")/../.." && pwd)"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/dotnet/Directory.Build.props")"
arch="${rid#osx-}"
out="$root/dist"
stage="$out/stage-$rid"
app="$stage/TOC Extractor.app"

rm -rf "$stage"
mkdir -p "$out"

dotnet publish "$root/dotnet/src/TocExtractor.Desktop" \
  --configuration Release \
  --runtime "$rid" \
  --self-contained true \
  -p:PublishSingleFile=false \
  -p:UseAppHost=true \
  --output "$stage/publish"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$stage/publish/." "$app/Contents/MacOS/"
# Contents/MacOS may hold only code. The driver is node plus a folder of
# scripts, so it goes in Resources and the app points Playwright at it.
mv "$app/Contents/MacOS/.playwright" "$app/Contents/Resources/.playwright"
cp "$root/packaging/icon/TocExtractor.icns" "$app/Contents/Resources/"
sed "s/@VERSION@/$version/g" "$root/packaging/macos/Info.plist" > "$app/Contents/Info.plist"
chmod +x "$app/Contents/MacOS/TocExtractor"
find "$app/Contents/Resources/.playwright/node" -name node -exec chmod +x {} +

# Ad-hoc signature over the whole bundle, nested driver included. Apple
# silicon refuses to run unsigned code at all; this is not notarisation,
# so the first open still needs right click > Open (see the README).
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

# An Intel build is checked under Rosetta on an Apple silicon machine.
if [ "$rid" = "osx-x64" ] && [ "$(uname -m)" = "arm64" ]; then
  arch -x86_64 "$app/Contents/MacOS/TocExtractor" --self-test
else
  "$app/Contents/MacOS/TocExtractor" --self-test
fi

# The disk image holds the app and a shortcut to Applications, so installing
# is one drag.
dmg="$out/TOC-Extractor-$version-macos-$arch.dmg"
volume="$stage/volume"
rm -rf "$volume" "$dmg"
mkdir -p "$volume"
ditto "$app" "$volume/TOC Extractor.app"
ln -s /Applications "$volume/Applications"
hdiutil create -volname "TOC Extractor" -srcfolder "$volume" -fs HFS+ \
  -format UDZO -imagekey zlib-level=9 -ov "$dmg"
hdiutil verify "$dmg"
echo "built $dmg"
