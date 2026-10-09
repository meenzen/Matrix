#!/bin/sh
# Builds matrix-sdk-ffi for musl inside an alpine container, used by the native workflow:
#
#   docker run --rm -v "$PWD:/work" -w /work -e TARGET -e CARGO_PROFILE -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
#     public.ecr.aws/docker/library/rust:1-alpine .github/scripts/build-musl.sh
#
# GitHub's javascript actions can't run inside alpine containers on arm64, so the job runs the container itself.

set -eu

apk add --no-cache build-base git

# the repository is owned by the host user, vergen needs git to work in it
git config --global --add safe.directory '*'

# musl targets link the C runtime statically by default, which isn't supported for shared libraries
TARGET_ENV=$(echo "$TARGET" | tr 'a-z-' 'A-Z_')
export "CARGO_TARGET_${TARGET_ENV}_RUSTFLAGS=-C target-feature=-crt-static"

cd external/matrix-rust-sdk
cargo build --locked -p matrix-sdk-ffi --profile "$CARGO_PROFILE" --target "$TARGET"

# hand the build output back to the host user, the container runs as root
chown -R "$HOST_UID:$HOST_GID" target "${CARGO_HOME:-/usr/local/cargo}"
