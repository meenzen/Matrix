#!/usr/bin/env bash

set -eo pipefail

echo "Formatting generated bindings..."
dotnet csharpier src/Matrix.RustSdk.Bindings/Bindings.cs
