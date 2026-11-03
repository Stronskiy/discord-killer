#!/usr/bin/env bash
set -euo pipefail

ignored_paths=(
  "docs/example.md" "nested/docs/example.md" ".DS_Store" "macos/.DS_Store"
  "windows/bin/app.exe" "windows/obj/cache" ".env" "nested/.env.local"
  "Resources/DiscordSource.png" "macos/Resources/DiscordMark.png"
  "Resources/AppIcon.icns" ".superpowers/sdd/progress.md"
)
allowed_paths=(
  "README.md" "LICENSE" "AGENTS.md" ".env.example" "nested/.env.example"
  ".github/pull_request_template.md" ".github/workflows/repository-hygiene.yml"
  "windows/assets/logo.png" "windows/packages.lock.json"
)

for path in "${ignored_paths[@]}"; do
  if ! git check-ignore -q --no-index -- "$path"; then
    printf 'Expected path to be ignored: %s\n' "$path" >&2
    exit 1
  fi
done

for path in "${allowed_paths[@]}"; do
  if git check-ignore -q --no-index -- "$path"; then
    printf 'Expected path to be allowed: %s\n' "$path" >&2
    exit 1
  fi
done

while IFS= read -r -d '' path; do
  if git check-ignore -q --no-index -- "$path"; then
    printf 'Unexpected tracked path covered by an ignore rule: %s\n' "$path" >&2
    exit 1
  fi
done < <(git ls-files -z)

printf 'Repository hygiene checks passed.\n'
