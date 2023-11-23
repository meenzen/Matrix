#!/usr/bin/env bash

set -eo pipefail

echo "=> Building rust-sdk..."

BASE_DIRECTORY=$(pwd)
SDK_DIRECTORY=external/matrix-rust-sdk/bindings/matrix-sdk-ffi
CROSS_TOML_FILE=external/matrix-rust-sdk/Cross.toml

cp Cross.toml $CROSS_TOML_FILE
cd $SDK_DIRECTORY

echo "=> Starting build for target: x86_64-pc-windows-gnu"
cross build --target x86_64-pc-windows-gnu --release

echo "=> Starting build for target: i686-pc-windows-gnu"
cross build --target i686-pc-windows-gnu --release

echo "=> Starting build for target: x86_64-unknown-linux-gnu"
cross build --target x86_64-unknown-linux-gnu --release

echo "=> Starting build for target: aarch64-unknown-linux-gnu"
cross build --target aarch64-unknown-linux-gnu --release

cd $BASE_DIRECTORY
rm $CROSS_TOML_FILE