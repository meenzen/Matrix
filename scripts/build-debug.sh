#!/usr/bin/env bash

set -eo pipefail

echo "=> Building rust-sdk..."

BASE_DIRECTORY=$(pwd)
SDK_DIRECTORY=external/matrix-rust-sdk/bindings/matrix-sdk-ffi

cd $SDK_DIRECTORY
cargo build

cd $BASE_DIRECTORY
./scripts/generate-bindings.sh
