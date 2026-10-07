#!/usr/bin/env bash
# Сборка Windows-версии и архива: ./build.sh [версия]
set -euo pipefail
VER="${1:-1.0.0}"
ROOT="$(cd "$(dirname "$0")" && pwd)"
OUT="$ROOT/dist/GnirehtetSquad"
rm -rf "$ROOT/dist" && mkdir -p "$OUT"
(cd "$ROOT/app" && GOOS=windows GOARCH=amd64 CGO_ENABLED=0 \
  go build -trimpath -ldflags "-s -w -H=windowsgui" -o "$OUT/GnirehtetSquad.exe" .)
cp "$ROOT"/bin/gnirehtet.exe "$ROOT"/bin/gnirehtet.apk "$ROOT"/bin/gnirehtet-run.cmd "$OUT/"
cp "$ROOT/docs/README.txt" "$OUT/README.txt"
(cd "$ROOT/dist" && zip -qr "GnirehtetSquad-$VER-win64.zip" GnirehtetSquad)
echo "Готово: dist/GnirehtetSquad-$VER-win64.zip"
