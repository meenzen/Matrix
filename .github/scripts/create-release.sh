#!/usr/bin/env bash
# Creates the GitHub release for the published packages, used by the NuGet workflow:
#
#   GH_TOKEN=... .github/scripts/create-release.sh <directory with the published .nupkg files>
#
# Versions without a prerelease suffix (0.1.0) are marked as the latest release, everything else (0.1.0-rc.3) as a
# prerelease. The release notes name the upstream matrix-rust-sdk release, followed by the generated changelog.

set -euo pipefail

PACKAGES_DIRECTORY=$1

# the version of the published packages, e.g. Matrix.RustSdk.Bindings.0.1.0-rc.3.nupkg -> 0.1.0-rc.3
PACKAGE=$(find "$PACKAGES_DIRECTORY" -name 'Matrix.RustSdk.Bindings.[0-9]*.nupkg' -printf '%f\n' | head -n 1)
VERSION=$(echo "$PACKAGE" | sed -E 's/^Matrix\.RustSdk\.Bindings\.(.+)\.nupkg$/\1/')
if [[ -z "$VERSION" ]]; then
  echo "error: Matrix.RustSdk.Bindings package not found in $PACKAGES_DIRECTORY" >&2
  exit 1
fi

TAG="v$VERSION"
SDK_TAG=$(sed -nE 's#.*<MatrixSdkFfiTag>(.+)</MatrixSdkFfiTag>.*#\1#p' Directory.Build.props)
BINDGEN_VERSION=$(sed -nE 's/.*UNIFFI_BINDGEN_CS_VERSION = "(.+)";/\1/p' devenv.nix)
UNIFFI_VERSION=$(sed -nE 's/.*UNIFFI_RS_VERSION = "(.+)";/\1/p' devenv.nix)

if gh release view "$TAG" >/dev/null 2>&1; then
  echo "=> Release $TAG already exists, skipping"
  exit 0
fi

# gh release create silently uses an existing tag and ignores --target, so a tag on another commit would create the
# release for the wrong commit. The peeled ref (^{}) is the commit of an annotated tag.
TAG_REFS=$(git ls-remote --tags origin "refs/tags/$TAG" "refs/tags/$TAG^{}")
TAG_SHA=$(echo "$TAG_REFS" | awk '$2 ~ /\^\{\}$/ { print $1; exit }')
TAG_SHA=${TAG_SHA:-$(echo "$TAG_REFS" | awk 'NR == 1 { print $1 }')}
if [[ -n "$TAG_SHA" && "$TAG_SHA" != "$GITHUB_SHA" ]]; then
  echo "error: tag $TAG already exists on $TAG_SHA, but the packages were published from $GITHUB_SHA" >&2
  exit 1
fi

if [[ "$VERSION" == *-* ]]; then
  RELEASE_TYPE=(--prerelease --latest=false)
else
  RELEASE_TYPE=(--latest)
fi

NOTES=$(
  cat <<NOTES
Built from matrix-rust-sdk [\`$SDK_TAG\`](https://github.com/matrix-org/matrix-rust-sdk/tree/$SDK_TAG), bindings generated with uniffi-bindgen-cs \`v$BINDGEN_VERSION+v$UNIFFI_VERSION\`.

## Packages

| Package | Platforms |
|---------|-----------|
| [Matrix.RustSdk.Bindings](https://www.nuget.org/packages/Matrix.RustSdk.Bindings/$VERSION) | managed bindings |
| [Matrix.RustSdk.Bindings.Native.Linux](https://www.nuget.org/packages/Matrix.RustSdk.Bindings.Native.Linux/$VERSION) | \`linux-x64\`, \`linux-arm64\`, \`linux-musl-x64\`, \`linux-musl-arm64\` |
| [Matrix.RustSdk.Bindings.Native.Windows](https://www.nuget.org/packages/Matrix.RustSdk.Bindings.Native.Windows/$VERSION) | \`win-x64\`, \`win-arm64\` |
| [Matrix.RustSdk.Bindings.Native.MacOS](https://www.nuget.org/packages/Matrix.RustSdk.Bindings.Native.MacOS/$VERSION) | \`osx-x64\`, \`osx-arm64\` |
| [Matrix.RustSdk.Bindings.Native.All](https://www.nuget.org/packages/Matrix.RustSdk.Bindings.Native.All/$VERSION) | all of the above |

\`\`\`xml
<PackageReference Include="Matrix.RustSdk.Bindings.Native.Linux" Version="$VERSION" />
\`\`\`
NOTES
)

echo "=> Creating release $TAG (${RELEASE_TYPE[*]}) for $GITHUB_SHA"
gh release create "$TAG" \
  --target "$GITHUB_SHA" \
  --title "$VERSION" \
  --notes "$NOTES" \
  --generate-notes \
  "${RELEASE_TYPE[@]}"
