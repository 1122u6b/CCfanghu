#!/bin/bash
# Builds Leyra-<version>.pkg on a Mac (GitHub macOS runner). One package for Apple silicon and Intel Macs.
# Not signed with an Apple developer certificate: binaries get an ad-hoc signature (required on Apple silicon).
set -euo pipefail
cd "$(dirname "$0")/.."
V=$(grep -o 'Version="[^"]*"' windows/Leyra.Windows.Core/ProductInfo.cs | cut -d'"' -f2)
OUT="$PWD/mac/out"; rm -rf "$OUT"; ROOT="$OUT/root"; SUP="$ROOT/Library/Application Support/Leyra"
mkdir -p "$ROOT/Applications" "$ROOT/Library/LaunchDaemons" "$SUP"
MIHOMO=v1.19.33
for pair in "osx-arm64 agent-arm64 darwin-arm64" "osx-x64 agent-x64 darwin-amd64-compatible"; do
  set -- $pair
  dotnet publish mac/Leyra.Mac.Agent/Leyra.Mac.Agent.csproj -c Release -r "$1" --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$SUP/$2" -v q
  curl -fsSL --retry 3 -o "$OUT/mihomo.gz" "https://github.com/MetaCubeX/mihomo/releases/download/$MIHOMO/mihomo-$3-$MIHOMO.gz"
  gunzip -c "$OUT/mihomo.gz" > "$SUP/$2/mihomo"; chmod 755 "$SUP/$2/mihomo"; rm "$OUT/mihomo.gz"
  codesign --force -s - "$SUP/$2/mihomo" "$SUP/$2/leyra-agent"
  SHA=$(shasum -a 256 "$SUP/$2/mihomo" | cut -d' ' -f1)
  printf '{"version":"%s","source":"https://github.com/MetaCubeX/mihomo/releases/download/%s/mihomo-%s-%s.gz","binary_sha256":"%s"}\n' "$MIHOMO" "$MIHOMO" "$3" "$MIHOMO" "$SHA" > "$SUP/$2/kernel-origin.json"
  rm -f "$SUP/$2"/*.pdb
done
cp mac/packaging/xin.leyra.agent.plist "$ROOT/Library/LaunchDaemons/"
APP="$ROOT/Applications/Leyra.app"; mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
swiftc -O -target arm64-apple-macos12 mac/LeyraApp/main.swift -o "$OUT/Leyra-arm64"
swiftc -O -target x86_64-apple-macos12 mac/LeyraApp/main.swift -o "$OUT/Leyra-x86_64"
lipo -create "$OUT/Leyra-arm64" "$OUT/Leyra-x86_64" -output "$APP/Contents/MacOS/Leyra"
sed "s/__VERSION__/$V/g" mac/packaging/Info.plist > "$APP/Contents/Info.plist"
cp -R client-assets "$APP/Contents/Resources/client-assets"
codesign --force --deep -s - "$APP"
pkgbuild --analyze --root "$ROOT" "$OUT/components.plist"
plutil -replace 0.BundleIsRelocatable -bool false "$OUT/components.plist"
pkgbuild --root "$ROOT" --component-plist "$OUT/components.plist" --scripts mac/packaging/scripts \
  --identifier xin.leyra.pkg --version "$V" --install-location / "$OUT/Leyra-$V.pkg"
echo "built $OUT/Leyra-$V.pkg"
