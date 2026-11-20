#!/bin/bash
# Builds Leyra-<version>.pkg on a Mac (GitHub macOS runner). One package for Apple silicon and Intel Macs.
# Not signed with an Apple developer certificate: binaries get an ad-hoc signature (required on Apple silicon).
# The repository is public, so the macOS runners used by mac-validation.yml cost nothing.
# Version comes from windows/Leyra.Windows.Core/ProductInfo.cs (shared by both clients).
set -euo pipefail
cd "$(dirname "$0")/.."
V=$(grep -o 'Version="[^"]*"' windows/Leyra.Windows.Core/ProductInfo.cs | cut -d'"' -f2)
OUT="$PWD/mac/out"; rm -rf "$OUT"; ROOT="$OUT/root"; SUP="$ROOT/Library/Application Support/Leyra"
mkdir -p "$ROOT/Applications" "$ROOT/Library/LaunchDaemons" "$ROOT/Library/LaunchAgents" "$SUP"
MIHOMO=v1.19.33
for pair in "osx-arm64 agent-arm64 darwin-arm64" "osx-x64 agent-x64 darwin-amd64-compatible"; do
  set -- $pair
  # The service is compiled to machine code, from its code with names renamed and strings hidden first.
  python3 scripts/aot_publish.py mac/Leyra.Mac.Agent/Leyra.Mac.Agent.csproj "$1" "$SUP/$2" leyra-agent.dll Leyra.Windows.Core.dll
  curl -fsSL --retry 3 -o "$OUT/mihomo.gz" "https://github.com/MetaCubeX/mihomo/releases/download/$MIHOMO/mihomo-$3-$MIHOMO.gz"
  gunzip -c "$OUT/mihomo.gz" > "$SUP/$2/mihomo"; chmod 755 "$SUP/$2/mihomo"; rm "$OUT/mihomo.gz"
  codesign --force -s - "$SUP/$2/mihomo" "$SUP/$2/leyra-agent"
  SHA=$(shasum -a 256 "$SUP/$2/mihomo" | cut -d' ' -f1)
  printf '{"version":"%s","source":"https://github.com/MetaCubeX/mihomo/releases/download/%s/mihomo-%s-%s.gz","binary_sha256":"%s"}\n' "$MIHOMO" "$MIHOMO" "$3" "$MIHOMO" "$SHA" > "$SUP/$2/kernel-origin.json"
  rm -f "$SUP/$2"/*.pdb "$SUP/$2"/*.dbg
done
cp mac/packaging/xin.leyra.agent.plist "$ROOT/Library/LaunchDaemons/"
cp mac/packaging/xin.leyra.app.plist "$ROOT/Library/LaunchAgents/"
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
(cd "$OUT" && shasum -a 256 "Leyra-$V.pkg" > "Leyra-$V.sha256")
echo "built $OUT/Leyra-$V.pkg"
