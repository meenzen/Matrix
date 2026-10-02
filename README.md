[![GitHub](https://img.shields.io/github/license/meenzen/Matrix.svg)](https://github.com/meenzen/Matrix/blob/main/LICENSE)
[![codecov](https://codecov.io/gh/meenzen/Matrix/graph/badge.svg?token=OTzMAH3dRO)](https://codecov.io/gh/meenzen/Matrix)
[![NuGet](https://img.shields.io/nuget/v/Matrix.RustSdk)](https://www.nuget.org/packages/Matrix.RustSdk)
[![NuGet](https://img.shields.io/nuget/dt/Matrix.RustSdk.svg)](https://www.nuget.org/packages/Matrix.RustSdk)

# Matrix.RustSdk

A C# wrapper for the matrix-rust-sdk crate.

## Installation

The managed bindings and the native libraries are shipped in separate packages, so applications only download the
platforms they need. Reference `Matrix.RustSdk` and the native packages for the platforms the application runs on:

```xml
<PackageReference Include="Matrix.RustSdk" Version="..." />
<PackageReference Include="Matrix.RustSdk.Bindings.Native.Linux" Version="..." />
```

| Package                                  | Platforms                                                       |
|------------------------------------------|-----------------------------------------------------------------|
| `Matrix.RustSdk.Bindings.Native.Linux`   | `linux-x64`, `linux-arm64`, `linux-musl-x64`, `linux-musl-arm64` |
| `Matrix.RustSdk.Bindings.Native.Windows` | `win-x64`, `win-arm64`                                          |
| `Matrix.RustSdk.Bindings.Native.MacOS`   | `osx-x64`, `osx-arm64`                                          |
| `Matrix.RustSdk.Bindings.Native.All`     | all of the above                                                |

The native packages have to use the same version as `Matrix.RustSdk`, a mismatch is detected when the library is
loaded. The glibc libraries require glibc 2.35 or newer (Ubuntu 22.04, Debian 12, RHEL 10), the musl libraries
`libgcc` (installed with .NET on Alpine).

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

The [Packages](.github/workflows/packages.yml) workflow builds the native libraries on native GitHub runners for every
platform, tests them and packs the NuGet packages, the [Publish NuGet package](.github/workflows/nuget.yml) workflow
publishes them. Pull requests run the same builds once the basic build and tests passed.

Each library is tested on its runner before it is packed. The Linux runners run all tests, the musl libraries are
built and tested in Alpine containers. The Windows and macOS runners can't run the Linux homeserver container and only
run the smoke tests that don't need a homeserver.
