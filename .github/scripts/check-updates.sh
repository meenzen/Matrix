#!/usr/bin/env bash
# Checks for new matrix-rust-sdk FFI snapshots and uniffi-bindgen-cs releases, used by the update check workflow:
#
#   GH_TOKEN=... .github/scripts/check-updates.sh
#   DRY_RUN=1 .github/scripts/check-updates.sh  # prints the issues instead of creating them
#
# Keeps one open issue per topic up to date instead of creating a new one for every snapshot, and closes it once the
# repository is up to date. The issues are found by a marker comment in their body.
#
# matrix-rust-sdk snapshots are `matrix-sdk-ffi/YYYYMMDD` tags, sometimes only a branch of that name exists. Updating
# needs a uniffi-bindgen-cs built for the same uniffi minor version, see RELEASING.md.

set -euo pipefail

SDK_REPO=matrix-org/matrix-rust-sdk
BINDGEN_REPO=NordSecurity/uniffi-bindgen-cs
SDK_MARKER="<!-- check-updates: matrix-rust-sdk -->"
BINDGEN_MARKER="<!-- check-updates: uniffi-bindgen-cs -->"
REPO=${GITHUB_REPOSITORY:-$(gh repo view --json nameWithOwner --jq .nameWithOwner)}
ASSIGNEE=${GITHUB_REPOSITORY_OWNER:-${REPO%%/*}}

CURRENT_SDK=$(sed -nE 's#.*<MatrixSdkFfiTag>(.+)</MatrixSdkFfiTag>.*#\1#p' Directory.Build.props)
BINDGEN_VERSION=$(sed -nE 's/.*UNIFFI_BINDGEN_CS_VERSION = "(.+)";/\1/p' devenv.nix)
BINDGEN_REV=$(sed -nE 's/.*UNIFFI_BINDGEN_CS_REV = "(.+)";/\1/p' devenv.nix)
BINDGEN_UNIFFI=$(sed -nE 's/.*UNIFFI_RS_VERSION = "(.+)";/\1/p' devenv.nix)

# 0.32.1 -> 0.32
minor() {
  echo "$1" | cut -d. -f1,2
}

# true if version $1 is newer than $2 (version sort, works for the dated snapshot names too)
newer() {
  [[ "$1" != "$2" && "$(printf '%s\n' "$1" "$2" | sort -V | tail -n 1)" == "$1" ]]
}

# uniffi version of a matrix-rust-sdk ref
sdk_uniffi() {
  curl -fsSL "https://raw.githubusercontent.com/$SDK_REPO/$1/Cargo.toml" |
    sed -nE 's/^uniffi = .*version = "([0-9.]+)".*/\1/p'
}

# creates or updates the open issue with the marker $1, does nothing if title and body are unchanged. New issues are
# assigned to the repository owner and changes are announced with the comment $4, editing an issue doesn't notify anyone.
upsert_issue() {
  local marker=$1 title=$2 body="$3

$1" comment=$4
  if [[ -n "${DRY_RUN:-}" ]]; then
    printf '=> Would create or update issue "%s" (assigned to %s, comment on update: "%s"):\n\n%s\n\n' \
      "$title" "$ASSIGNEE" "$comment" "$body"
    return
  fi
  local issue
  issue=$(gh issue list --state open --limit 100 --json number,title,body |
    jq -c --arg marker "$marker" 'map(select(.body | contains($marker))) | first // empty')
  if [[ -z "$issue" ]]; then
    echo "=> Creating issue \"$title\""
    gh issue create --title "$title" --body "$body" --assignee "$ASSIGNEE"
  elif [[ "$(jq -r .title <<<"$issue")" != "$title" || "$(jq -r .body <<<"$issue")" != "$body" ]]; then
    echo "=> Updating issue #$(jq -r .number <<<"$issue") \"$title\""
    gh issue edit "$(jq -r .number <<<"$issue")" --title "$title" --body "$body"
    gh issue comment "$(jq -r .number <<<"$issue")" --body "$comment"
  else
    echo "=> Issue #$(jq -r .number <<<"$issue") \"$title\" is up to date"
  fi
}

# closes the open issue with the marker $1, if there is one
close_issue() {
  local marker=$1 comment=$2
  if [[ -n "${DRY_RUN:-}" ]]; then
    echo "=> Would close the open issue with $marker: $comment"
    return
  fi
  local number
  number=$(gh issue list --state open --limit 100 --json number,body |
    jq -r --arg marker "$marker" 'map(select(.body | contains($marker))) | first | .number // empty')
  if [[ -n "$number" ]]; then
    echo "=> Closing issue #$number"
    gh issue close "$number" --comment "$comment"
  fi
}

echo "=> Current: matrix-rust-sdk $CURRENT_SDK, uniffi-bindgen-cs $BINDGEN_VERSION${BINDGEN_REV:+ ($BINDGEN_REV)} for uniffi $BINDGEN_UNIFFI"

# latest uniffi-bindgen-cs release, tags are named vX.Y.Z+vA.B.C (A.B.C is the uniffi version)
LATEST_BINDGEN_TAG=$(git ls-remote --tags "https://github.com/$BINDGEN_REPO" |
  awk '{ print $2 }' | grep -v '\^{}$' | sed 's#refs/tags/##' | grep -E '^v[0-9.]+\+v[0-9.]+$' | sort -V | tail -n 1)
LATEST_BINDGEN=$(echo "$LATEST_BINDGEN_TAG" | sed -E 's/^v([0-9.]+)\+v([0-9.]+)$/\1/')
LATEST_BINDGEN_UNIFFI=$(echo "$LATEST_BINDGEN_TAG" | sed -E 's/^v([0-9.]+)\+v([0-9.]+)$/\2/')
echo "=> Latest uniffi-bindgen-cs release: $LATEST_BINDGEN_TAG"

# latest matrix-rust-sdk snapshot, from tags and branches
SDK_REFS=$(git ls-remote --tags --heads "https://github.com/$SDK_REPO" 'matrix-sdk-ffi/*' |
  awk '{ print $2 }' | grep -v '\^{}$')
LATEST_SDK=$(echo "$SDK_REFS" | sed -E 's#refs/(tags|heads)/##' | sort -V | tail -n 1)
if echo "$SDK_REFS" | grep -qx "refs/tags/$LATEST_SDK"; then
  LATEST_SDK_KIND=tag
else
  LATEST_SDK_KIND="branch, not tagged yet"
fi
echo "=> Latest matrix-rust-sdk snapshot: $LATEST_SDK ($LATEST_SDK_KIND)"

if newer "$LATEST_SDK" "$CURRENT_SDK"; then
  CURRENT_SDK_UNIFFI=$(sdk_uniffi "$CURRENT_SDK")
  LATEST_SDK_UNIFFI=$(sdk_uniffi "$LATEST_SDK")

  if [[ "$(minor "$LATEST_SDK_UNIFFI")" == "$(minor "$BINDGEN_UNIFFI")" ]]; then
    COMPATIBILITY="✅ The configured uniffi-bindgen-cs supports uniffi $(minor "$LATEST_SDK_UNIFFI")."
  elif [[ "$(minor "$LATEST_SDK_UNIFFI")" == "$(minor "$LATEST_BINDGEN_UNIFFI")" ]]; then
    COMPATIBILITY="⚠️ Needs the latest uniffi-bindgen-cs release \`$LATEST_BINDGEN_TAG\` for uniffi $(minor "$LATEST_SDK_UNIFFI"), update it in \`devenv.nix\`."
  else
    COMPATIBILITY="❌ No uniffi-bindgen-cs release supports uniffi $(minor "$LATEST_SDK_UNIFFI") yet (latest: \`$LATEST_BINDGEN_TAG\`). Check its [pull requests](https://github.com/$BINDGEN_REPO/pulls) for an upgrade that can be built from a commit."
  fi

  TAG_NOTE=""
  if [[ "$LATEST_SDK_KIND" != tag ]]; then
    TAG_NOTE="

\`$LATEST_SDK\` is only a branch so far, \`scripts/check-sdk-version.sh\` requires a tag."
  fi

  upsert_issue "$SDK_MARKER" "Update matrix-rust-sdk to $LATEST_SDK" "$(
    cat <<BODY
A newer matrix-rust-sdk FFI snapshot is available.

| | matrix-rust-sdk | uniffi |
|---|---|---|
| current | [\`$CURRENT_SDK\`](https://github.com/$SDK_REPO/tree/$CURRENT_SDK) | $CURRENT_SDK_UNIFFI |
| latest | [\`$LATEST_SDK\`](https://github.com/$SDK_REPO/tree/$LATEST_SDK) ($LATEST_SDK_KIND) | $LATEST_SDK_UNIFFI |

$COMPATIBILITY$TAG_NOTE

- [Changes](https://github.com/$SDK_REPO/compare/$CURRENT_SDK...$LATEST_SDK)
- [matrix-sdk-ffi changelog](https://github.com/$SDK_REPO/blob/$LATEST_SDK/bindings/matrix-sdk-ffi/CHANGELOG.md)
- Update steps: [RELEASING.md](https://github.com/$REPO/blob/main/RELEASING.md#updating-matrix-rust-sdk)

This issue is updated by the [update check workflow](https://github.com/$REPO/actions/workflows/check-updates.yml).
BODY
  )" "Updated for \`$LATEST_SDK\` ($LATEST_SDK_KIND, uniffi $LATEST_SDK_UNIFFI): $COMPATIBILITY"
else
  close_issue "$SDK_MARKER" "matrix-rust-sdk is up to date ($CURRENT_SDK)."
fi

# a newer release, or a release of the version that is currently built from a commit
if newer "$LATEST_BINDGEN" "$BINDGEN_VERSION" || [[ -n "$BINDGEN_REV" && "$LATEST_BINDGEN" == "$BINDGEN_VERSION" ]]; then
  if [[ -n "$BINDGEN_REV" ]]; then
    CONFIGURED="\`$BINDGEN_VERSION\`, built from [\`${BINDGEN_REV:0:9}\`](https://github.com/$BINDGEN_REPO/commit/$BINDGEN_REV) with the patches in \`patches/uniffi-bindgen-cs\`"
  else
    CONFIGURED="\`v$BINDGEN_VERSION+v$BINDGEN_UNIFFI\`"
  fi
  upsert_issue "$BINDGEN_MARKER" "Update uniffi-bindgen-cs to $LATEST_BINDGEN_TAG" "$(
    cat <<BODY
A new uniffi-bindgen-cs release is available: [\`$LATEST_BINDGEN_TAG\`](https://github.com/$BINDGEN_REPO/releases/tag/$LATEST_BINDGEN_TAG) for uniffi $LATEST_BINDGEN_UNIFFI.

Configured in \`devenv.nix\`: $CONFIGURED for uniffi $BINDGEN_UNIFFI.

It has to match the uniffi version of matrix-rust-sdk (\`$CURRENT_SDK\`, latest snapshot \`$LATEST_SDK\`), usually it's updated together with matrix-rust-sdk. Remove the patches that are included in the release.

- [Changelog](https://github.com/$BINDGEN_REPO/blob/main/CHANGELOG.md)
- Update steps: [RELEASING.md](https://github.com/$REPO/blob/main/RELEASING.md#updating-matrix-rust-sdk)

This issue is updated by the [update check workflow](https://github.com/$REPO/actions/workflows/check-updates.yml).
BODY
  )" "Updated for \`$LATEST_BINDGEN_TAG\`."
else
  close_issue "$BINDGEN_MARKER" "uniffi-bindgen-cs is up to date ($BINDGEN_VERSION)."
fi
