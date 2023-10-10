#!/usr/bin/env bash

set -eo pipefail

echo "Building rust-sdk..."

SDK_DIRECTORY=external/matrix-rust-sdk/bindings/matrix-sdk-ffi

cp Cross.toml external/matrix-rust-sdk/Cross.toml
cd $SDK_DIRECTORY

echo "Starting build for target: x86_64-pc-windows-gnu"
cross build --target x86_64-pc-windows-gnu --release

echo "Starting build for target: i686-pc-windows-gnu"
cross build --target i686-pc-windows-gnu --release

echo "Starting build for target: x86_64-unknown-linux-gnu"
cross build --target x86_64-unknown-linux-gnu --release

echo "Starting build for target: aarch64-unknown-linux-gnu"
cross build --target aarch64-unknown-linux-gnu --release