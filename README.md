[![GitHub](https://img.shields.io/github/license/meenzen/Matrix.svg)](https://github.com/meenzen/Matrix/blob/main/LICENSE)
[![codecov](https://codecov.io/gh/meenzen/Matrix/graph/badge.svg?token=OTzMAH3dRO)](https://codecov.io/gh/meenzen/Matrix)
[![NuGet](https://img.shields.io/nuget/v/Matrix.RustSdk)](https://www.nuget.org/packages/Matrix.RustSdk)
[![NuGet](https://img.shields.io/nuget/dt/Matrix.RustSdk.svg)](https://www.nuget.org/packages/Matrix.RustSdk)

# Matrix.RustSdk

A C# wrapper for the matrix-rust-sdk crate.

## Development

The development environment is managed with [devenv](https://devenv.sh), `devenv shell` provides Rust, .NET and
[uniffi-bindgen-cs](https://github.com/NordSecurity/uniffi-bindgen-cs).

```bash
git submodule update --init
devenv shell
./scripts/build-debug.sh  # builds matrix-sdk-ffi for the host and regenerates the C# bindings
dotnet test               # runs the tests against a tuwunel homeserver, requires docker
```

The uniffi version used by `external/matrix-rust-sdk` has to match the one uniffi-bindgen-cs is built for
(`UNIFFI_RS_VERSION` in `devenv.nix`). When updating the submodule, pick a `matrix-sdk-ffi/*` tag whose `uniffi`
version matches the latest uniffi-bindgen-cs release.

## Releases

The native libraries are built on native GitHub runners for `linux-x64`, `linux-arm64`, `win-x64` and `win-arm64`
and packed into the NuGet package by the [Publish NuGet package](.github/workflows/nuget.yml) workflow.
Each library is tested on its runner before it is packed: the Linux runners run all tests, the Windows runners can't
run the Linux homeserver container and only run the smoke tests that don't need a homeserver.
