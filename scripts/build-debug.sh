#!/usr/bin/env bash

set -eo pipefail

echo "=> Building rust-sdk..."

BASE_DIRECTORY=$(pwd)
SDK_DIRECTORY=external/matrix-rust-sdk/bindings/matrix-sdk-ffi

# enable the experimental parallel front-end, see https://blog.rust-lang.org/2023/11/09/parallel-rustc.html
export RUSTFLAGS="-Z threads=8"

cd $SDK_DIRECTORY
cargo build

cd $BASE_DIRECTORY
./scripts/generate-bindings.sh
