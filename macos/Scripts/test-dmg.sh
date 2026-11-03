#!/bin/bash
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
IMAGE="$PROJECT_DIR/dist/Discord-Timer-macOS-arm64.dmg"
MOUNT_DIR="$(mktemp -d "${TMPDIR:-/tmp}/discordtimer-mount.XXXXXX")"
MOUNTED=0
cleanup() {
  if [[ "$MOUNTED" == 1 ]]; then hdiutil detach "$MOUNT_DIR" >/dev/null || return 1; fi
  rmdir "$MOUNT_DIR"
}
trap cleanup EXIT
hdiutil verify "$IMAGE" >/dev/null
hdiutil attach -readonly -nobrowse -noautoopen -mountpoint "$MOUNT_DIR" "$IMAGE" >/dev/null
MOUNTED=1
[[ -d "$MOUNT_DIR/Discord Timer.app" ]]
[[ -L "$MOUNT_DIR/Applications" && "$(readlink "$MOUNT_DIR/Applications")" == /Applications ]]
[[ -s "$MOUNT_DIR/.DS_Store" && -s "$MOUNT_DIR/.background.png" ]]
codesign --verify --deep --strict "$MOUNT_DIR/Discord Timer.app"
# Ensure Finder records and icon placement were embedded, not merely the files.
"${DISCORD_DMG_PYTHON:-python3}" - "$MOUNT_DIR/.DS_Store" <<'PY'
import sys
from ds_store import DSStore
with DSStore.open(sys.argv[1], 'r') as store:
    for name in ('Discord Timer.app', 'Applications'):
        if store[name]['Iloc'] is None:
            raise SystemExit('Missing Finder position for ' + name)
    if store['.']['icvp'] is None:
        raise SystemExit('Missing Finder icon view settings')
PY
printf '%s\n' 'DMG integrity, contents, Finder metadata and app signature verified.'
