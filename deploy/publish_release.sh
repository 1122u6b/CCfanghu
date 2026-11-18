#!/usr/bin/env bash
# Upload a tested installer to the server in resumable 16 MB pieces, then publish it.
# Usage: publish_release.sh <dir containing Leyra-X.Y.Z-setup.exe and .sha256>   (needs LEYRA_SSH_KEY)
set -euo pipefail
dir=$1
# Windows: Leyra-X.Y.Z-setup.exe; Mac: Leyra-X.Y.Z.pkg (each with a .sha256 next to it).
if ls "$dir"/Leyra-*-setup.exe >/dev/null 2>&1; then
  setup=$(ls "$dir"/Leyra-*-setup.exe | head -n1); platform=windows
  version=$(basename "$setup" | sed -E 's/^Leyra-(.+)-setup\.exe$/\1/')
else
  setup=$(ls "$dir"/Leyra-*.pkg | head -n1); platform=mac
  version=$(basename "$setup" | sed -E 's/^Leyra-(.+)\.pkg$/\1/')
fi
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[A-Za-z0-9.]+)?$ ]]
(cd "$dir" && sha256sum -c "$(basename "$setup" | sed -E 's/\.(exe|pkg)$//').sha256")
sha=$(sha256sum "$setup" | cut -d' ' -f1)
key_file="${RUNNER_TEMP:-/tmp}/leyra-key"; umask 077
printf '%s\n' "$LEYRA_SSH_KEY" | tr -d '\r' > "$key_file"
host=ubuntu@129.226.74.173
stage="/tmp/leyra-release-$version-$sha"
here=$(cd "$(dirname "$0")" && pwd)
ssh_options=(-T -F /dev/null -i "$key_file" -o IdentitiesOnly=yes -o BatchMode=yes -o ConnectTimeout=12 -o ServerAliveInterval=15 -o ServerAliveCountMax=4 -o StrictHostKeyChecking=yes -o "UserKnownHostsFile=$here/known_hosts")
trap 'rm -f "$key_file"; rm -rf "$parts"' EXIT
parts=$(mktemp -d)
split -b 16M -d -a 3 "$setup" "$parts/p"
ssh "${ssh_options[@]}" "$host" "mkdir -p -m 700 -- $stage"
total=$(ls "$parts" | wc -l); n=0
for part in "$parts"/p*; do
  name=$(basename "$part"); want=$(stat -c %s "$part"); n=$((n+1))
  for attempt in 1 2 3 4 5 6; do
    have=$(timeout 30 ssh "${ssh_options[@]}" "$host" "stat -c %s $stage/$name 2>/dev/null || echo 0" || echo 0)
    [ "$have" = "$want" ] && break
    if timeout 240 ssh "${ssh_options[@]}" "$host" "cat > $stage/$name.tmp && mv $stage/$name.tmp $stage/$name" < "$part"; then continue; fi
    echo "piece $n/$total attempt $attempt failed"; sleep 5
    [ "$attempt" = 6 ] && { echo "piece $n/$total could not be uploaded"; exit 1; }
  done
  echo "piece $n/$total uploaded"
done
ssh "${ssh_options[@]}" "$host" "cd $stage && cat p[0-9][0-9][0-9] > setup.exe && echo '$sha  setup.exe' | sha256sum -c --quiet"
ssh "${ssh_options[@]}" "$host" "release=\$(ls -d /opt/leyra-connect/releases/*/ | sort | tail -n1); sudo -n python3 \"\${release}admin.py\" --profile /root/leyra-server-connection.json --connect 127.0.0.1 publish-update $version $stage/setup.exe --platform $platform --notes 'Leyra $version' | tail -n1"
ssh "${ssh_options[@]}" "$host" "rm -rf -- $stage" || true
echo "LEYRA_RELEASE_PUBLISHED=$platform $version"
