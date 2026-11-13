#!/bin/sh
# Tests the musl native library inside an alpine container, used by the native workflow:
#
#   docker run --rm --network host -v /var/run/docker.sock:/var/run/docker.sock -v "$PWD:/work" -w /work \
#     -e NATIVE_LIBRARY_PATH -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
#     mcr.microsoft.com/dotnet/sdk:11.0-alpine .github/scripts/test-musl.sh
#
# The docker socket and host network allow testcontainers to start the homeserver next to this container and reach
# its mapped port on localhost.

set -eu

# hand the build output back to the host user, the container runs as root
trap 'chown -R "$HOST_UID:$HOST_GID" src test TestResults 2>/dev/null || true' EXIT

# the repository is owned by the host user, Nerdbank.GitVersioning needs git to work in it
git config --global --add safe.directory '*'

export TESTCONTAINERS_HOST_OVERRIDE=localhost

# one target framework, the net10.0 tests run in build.yml (the image has no .NET 10 runtime)
dotnet test --project test/Matrix.RustSdk.Tests --framework net11.0 -p:NativeLibraryPath="$NATIVE_LIBRARY_PATH"
