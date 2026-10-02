#!/usr/bin/env bash

# Generates THIRD-PARTY-NOTICES.txt with the licenses and copyright notices of matrix-rust-sdk and all crates linked
# into the native library, it's included in every package. Requires cargo-about (provided by devenv).

set -euo pipefail

OUTPUT=${1:-THIRD-PARTY-NOTICES.txt}

echo "=> Generating $OUTPUT..."

cargo about generate \
  --manifest-path external/matrix-rust-sdk/bindings/matrix-sdk-ffi/Cargo.toml \
  --config licenses/about.toml \
  --fail \
  --output-file "$OUTPUT" \
  licenses/about.hbs
