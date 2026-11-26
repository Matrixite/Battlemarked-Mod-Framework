#!/usr/bin/env bash
set -euo pipefail

# The workflow grants write access only to this publication job, not tests.
repo="Matrixite/Battlemarked-Mod-Framework"
tag="v1.0.0"
[[ "${GITHUB_REPOSITORY:-}" == "$repo" && "${GITHUB_REF:-}" == "refs/heads/main" ]] ||
  { echo "Unexpected repository or branch." >&2; exit 1; }
[[ "${GITHUB_SHA:-}" =~ ^[0-9a-f]{40}$ && "$(git rev-parse HEAD)" == "$GITHUB_SHA" ]] ||
  { echo "Checkout does not match the approved commit." >&2; exit 1; }
[[ "$(python3 scripts/release_gate.py)" == "eligible=true" ]] ||
  { echo "Release gate refused this snapshot." >&2; exit 1; }

# Never replace an existing release or move a tag, including a manually created release.
if draft=$(gh release view "$tag" --repo "$repo" --json isDraft --jq .isDraft 2>/dev/null); then
  if [[ "$draft" == "false" ]]; then
    echo "Release $tag already exists; nothing to do."
    exit 0
  fi
  echo "A draft $tag release already exists; inspect it before trying again." >&2
  exit 1
fi

tag_commit() {
  local refs
  refs=$(git ls-remote --tags origin "refs/tags/$tag" "refs/tags/$tag^{}") || return 1
  printf '%s\n' "$refs" | awk -v ref="refs/tags/$tag" '
    $2 == ref { direct=$1 }
    $2 == ref "^{}" { peeled=$1 }
    END { print (peeled ? peeled : direct) }'
}

# Build the user-facing source installer package before creating any remote ref.
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
name="Battlemarked-Mod-Framework-$tag.zip"
git archive --format=zip --prefix="Battlemarked-Mod-Framework-$tag/" --output="$work/$name" "$GITHUB_SHA"
(cd "$work" && sha256sum "$name" > "$name.sha256")
cat > "$work/notes.md" <<'NOTES'
First stable release of the Battlemarked Mod Framework.

**Installation:** Download the versioned ZIP, extract it, and run Install-BMF.cmd with Battlemarked closed. The installer builds against the required local BepInEx Unity IL2CPP interop and .NET SDK; no proprietary game assemblies are bundled. See README.md for prerequisites, configuration, and safety information.

Existing installations can update through the in-game Mod Manager. After queuing the update, close the game and wait until Steam shows Play and the installer finishes before relaunching.
NOTES

tagged=$(tag_commit)
if [[ -z "$tagged" ]]; then
  gh api --method POST "repos/$repo/git/refs" -f ref="refs/tags/$tag" -f sha="$GITHUB_SHA" >/dev/null
  tagged=$(tag_commit)
fi
[[ "$tagged" == "$GITHUB_SHA" ]] ||
  { echo "Tag $tag does not identify the approved commit; refusing to change it." >&2; exit 1; }

gh release create "$tag" "$work/$name" "$work/$name.sha256" \
  --repo "$repo" --verify-tag --title "Battlemarked Mod Framework $tag" \
  --notes-file "$work/notes.md" --latest
