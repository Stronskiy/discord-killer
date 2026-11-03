#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
BUILD_DIR="$PROJECT_DIR/build"
DIST_DIR="$PROJECT_DIR/dist"
APP_BUNDLE="$DIST_DIR/Discord Timer.app"
ZIP_PATH="$DIST_DIR/Discord Timer.zip"
BUILD_TMP="$(mktemp -d "${TMPDIR:-/tmp}/discordtimer-build.XXXXXX")"
trap 'rm -rf "$BUILD_TMP"' EXIT

mkdir -p "$DIST_DIR"
rm -rf "$APP_BUNDLE"
rm -f "$ZIP_PATH"
mkdir -p "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources"

swift -module-cache-path "$BUILD_TMP/module-cache" \
  "$SCRIPT_DIR/generate-app-icon.swift" "$BUILD_TMP/Brand.icns"

SOURCES=("$PROJECT_DIR"/Sources/*.swift)
swiftc \
  -swift-version 5 \
  -target arm64-apple-macosx13.0 \
  -module-cache-path "$BUILD_TMP/module-cache" \
  -framework SwiftUI \
  -framework AppKit \
  "${SOURCES[@]}" \
  -o "$APP_BUNDLE/Contents/MacOS/DiscordTimer"

cp "$PROJECT_DIR/Resources/Info.plist" "$APP_BUNDLE/Contents/Info.plist"
cp "$BUILD_TMP/Brand.icns" "$APP_BUNDLE/Contents/Resources/Brand.icns"
printf 'APPL????' > "$APP_BUNDLE/Contents/PkgInfo"

codesign --force --deep --sign - "$APP_BUNDLE"
ditto -c -k --sequesterRsrc --keepParent "$APP_BUNDLE" "$ZIP_PATH"

printf 'Built app: %s\n' "$APP_BUNDLE"
printf 'Packaged:  %s\n' "$ZIP_PATH"
