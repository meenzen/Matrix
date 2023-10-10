#!/usr/bin/env bash

set -eo pipefail

./scripts/generate-bindings.sh

echo "Building rust-sdk..."

SDK_DIRECTORY=external/matrix-rust-sdk/bindings/matrix-sdk-ffi
cd $SDK_DIRECTORY
cargo build
