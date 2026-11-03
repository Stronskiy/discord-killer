#!/bin/bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
TEST_TMP="$(mktemp -d "${TMPDIR:-/tmp}/discordtimer-tests.XXXXXX")"
trap 'rm -rf "$TEST_TMP"' EXIT

swiftc -swift-version 5 -target arm64-apple-macosx13.0 \
  -module-cache-path "$TEST_TMP/module-cache" -framework AppKit \
  "$PROJECT_DIR/Sources/ShutdownSnapshot.swift" \
  "$PROJECT_DIR/Sources/TimerController.swift" \
  "$PROJECT_DIR/Tests/TimerTests.swift" \
  -o "$TEST_TMP/timer-tests"
"$TEST_TMP/timer-tests"
