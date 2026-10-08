#!/usr/bin/env bash

# Verifies that the external/matrix-rust-sdk submodule is checked out at the MatrixSdkFfiTag from Directory.Build.props,
# which is used for the package release notes and assembly metadata.

set -euo pipefail

SDK_DIRECTORY=external/matrix-rust-sdk
TAG=$(sed -nE 's#.*<MatrixSdkFfiTag>(.+)</MatrixSdkFfiTag>.*#\1#p' Directory.Build.props)

if [[ -z "$TAG" ]]; then
  echo "error: MatrixSdkFfiTag not found in Directory.Build.props" >&2
  exit 1
fi

# the submodule is usually a shallow checkout without tags
git -C "$SDK_DIRECTORY" fetch --quiet --depth 1 origin "refs/tags/$TAG:refs/tags/$TAG"

EXPECTED=$(git -C "$SDK_DIRECTORY" rev-parse "$TAG^{commit}")
ACTUAL=$(git -C "$SDK_DIRECTORY" rev-parse HEAD)

if [[ "$EXPECTED" != "$ACTUAL" ]]; then
  echo "error: $SDK_DIRECTORY is at $ACTUAL, but MatrixSdkFfiTag is $TAG ($EXPECTED)" >&2
  echo "Update the submodule or MatrixSdkFfiTag in Directory.Build.props, see RELEASING.md." >&2
  exit 1
fi

echo "=> $SDK_DIRECTORY is at $TAG"
