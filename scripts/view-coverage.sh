#!/usr/bin/env bash
set -euo pipefail

if ! command -v gh >/dev/null; then
  echo "gh missing" >&2
  exit 1
fi

if ! command -v fzf >/dev/null; then
  echo "fzf missing" >&2
  exit 1
fi

cd "$(git rev-parse --show-toplevel 2>/dev/null || pwd)"

repo="$(gh repo view --json nameWithOwner -q .nameWithOwner)"

run_id="$(
  gh run list --repo "$repo" --limit 20 \
    --json databaseId,displayTitle,headBranch,status,conclusion \
    --jq '.[] | "\(.databaseId)\t\(.displayTitle)\t\(.headBranch)\t\(.status)/\(.conclusion)"' \
  | fzf --prompt='Run > ' --with-nth=2.. \
  | cut -f1
)"
if [ -z "$run_id" ]; then
  exit 0
fi

artifact="$(
  gh api "repos/$repo/actions/runs/$run_id/artifacts" --jq '.artifacts[] | "\(.name)\t\(.size_in_bytes) bytes"' \
  | fzf --prompt='Artifact > ' --with-nth=1,2 \
  | cut -f1
)"
if [ -z "$artifact" ]; then
  exit 0
fi

dest="$(mktemp -d /tmp/gh_artifact_XXXX)"
gh run download "$run_id" --repo "$repo" --name "$artifact" --dir "$dest"

index=$(find "$dest" -name index.html | head -n1 || true)
if [ -n "$index" ]; then
  if command -v xdg-open >/dev/null; then
    xdg-open "$index" >/dev/null 2>&1 &
  elif command -v open >/dev/null; then
    open "$index" >/dev/null 2>&1 &
  else
    echo "Open index at: $index"
  fi
fi
