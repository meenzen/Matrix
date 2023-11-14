#!/usr/bin/env bash

set -eo pipefail

echo "=> Working directory: $(pwd)"

./scripts/build-debug.sh
./scripts/build-rust-sdk.sh