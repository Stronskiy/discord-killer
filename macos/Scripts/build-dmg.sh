#!/bin/bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
APP="$PROJECT_DIR/dist/Discord Timer.app"
OUTPUT="$PROJECT_DIR/dist/Discord-Timer-macOS-arm64.dmg"
PYTHON="${DISCORD_DMG_PYTHON:-python3}"

if [[ ! -d "$APP" ]]; then
  printf '%s\n' 'Build the app first: bash macos/Scripts/build-app.sh' >&2
  exit 1
fi
if ! "$PYTHON" -c 'import dmgbuild' >/dev/null 2>&1; then
  printf '%s\n' 'Install DMG build dependencies: python3 -m pip install -r macos/Packaging/requirements.txt' >&2
  exit 1
fi
TMP_DIR="$(mktemp -d "${TMPDIR:-/tmp}/discordtimer-dmg.XXXXXX")"
trap 'rm -rf "$TMP_DIR"' EXIT
swift -module-cache-path "$TMP_DIR/module-cache" "$PROJECT_DIR/Packaging/generate-background.swift" "$TMP_DIR/background.png"
export DISCORD_DMG_APP="$APP"
export DISCORD_DMG_BACKGROUND="$TMP_DIR/background.png"
"$PYTHON" -m dmgbuild --no-hidpi -s "$PROJECT_DIR/Packaging/dmg-settings.py" 'Discord Timer' "$TMP_DIR/output.dmg"
hdiutil verify "$TMP_DIR/output.dmg"
mv -f "$TMP_DIR/output.dmg" "$OUTPUT"
printf 'Packaged:  %s\n' "$OUTPUT"
