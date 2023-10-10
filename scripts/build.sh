#!/usr/bin/env bash

set -eo pipefail

echo "Working directory: $(pwd)"

./scripts/generate-bindings.sh
./scripts/build-rust-sdk.sh