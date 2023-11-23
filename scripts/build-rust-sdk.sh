#!/usr/bin/env bash

set -eo pipefail

echo "=> Building rust-sdk..."

BASE_DIRECTORY=$(pwd)
SDK_DIRECTORY=external/matrix-rust-sdk/bindings/matrix-sdk-ffi
CROSS_TOML_FILE=external/matrix-rust-sdk/Cross.toml

# enable the experimental parallel front-end, see https://blog.rust-lang.org/2023/11/09/parallel-rustc.html
export RUSTFLAGS="-Z threads=8"
export CARGO_TARGET_X86_64_PC_WINDOWS_GNU_RUSTFLAGS="$RUSTFLAGS"
export CARGO_TARGET_I686_PC_WINDOWS_GNU_RUSTFLAGS="$RUSTFLAGS"
export CARGO_TARGET_X86_64_UNKNOWN_LINUX_GNU_RUSTFLAGS="$RUSTFLAGS"
export CARGO_TARGET_AARCH64_UNKNOWN_LINUX_GNU_RUSTFLAGS="$RUSTFLAGS"

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