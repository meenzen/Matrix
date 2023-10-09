#!/usr/bin/env bash

set -eo pipefail

echo "User ID: $(id -u), Group ID: $(id -g), Working directory: $(pwd)"

cd external/matrix-rust-sdk/bindings/matrix-sdk-ffi
cargo build --release