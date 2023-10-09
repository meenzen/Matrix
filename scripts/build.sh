#!/usr/bin/env bash

set -eo pipefail

echo "Working directory: $(pwd)"

USER_ID=$(id -u)
GROUP_ID=$(id -g)

echo "Building rust-tools image..."
docker build -t rust-tools -f Dockerfile .

docker run --rm --user $USER_ID:$GROUP_ID -v $(pwd):/app rust-tools bash -c ./scripts/generate-bindings.sh
