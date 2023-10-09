#!/usr/bin/env bash

set -eo pipefail

# cargo install uniffi-bindgen-cs --git https://github.com/NordSecurity/uniffi-bindgen-cs --tag v0.2.4+v0.23.0

uniffi-bindgen-cs external/matrix-rust-sdk/bindings/matrix-sdk-ffi/src/api.udl --config src/Matrix.RustSdk.Bindings/uniffi.toml
mv external/matrix-rust-sdk/bindings/matrix-sdk-ffi/src/matrix_sdk_ffi.cs src/Matrix.RustSdk.Bindings/Bindings.cs
dotnet csharpier src/Matrix.RustSdk.Bindings/Bindings.cs