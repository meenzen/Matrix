# Agent guide

C# bindings for [matrix-rust-sdk](https://github.com/matrix-org/matrix-rust-sdk), generated with
[uniffi-bindgen-cs](https://github.com/NordSecurity/uniffi-bindgen-cs) from the `matrix-sdk-ffi` crate. Read
[README.md](README.md) for an overview and [RELEASING.md](RELEASING.md) for versioning, releases and updating
matrix-rust-sdk.

## Layout

- `external/matrix-rust-sdk`: submodule, pinned to a `matrix-sdk-ffi/*` release (`MatrixSdkFfiTag` in
  `Directory.Build.props` has to match, `scripts/check-sdk-version.sh` verifies it)
- `src/Matrix.RustSdk.Bindings`: the generated bindings (`*.cs`, committed) and `uniffi.toml`
- `src/Matrix.RustSdk.Bindings.Native.*`: packages containing only the native libraries, shared logic in
  `src/Native.targets`
- `src/Matrix.RustSdk`: future helpers on top of the bindings, not published yet
- `test/Matrix.RustSdk.Tests`: TUnit tests
- `scripts/`: local development scripts, `.github/scripts/`: scripts used by CI
- `.github/workflows/`: `build.yml` (PRs and main), `packages.yml` (reusable: native builds for all platforms, tests,
  packing), `nuget.yml` (manual publishing), `codeql.yml`

## Development

Use the devenv shell, it provides Rust (stable), the .NET SDK, uniffi-bindgen-cs and csharpier:

```bash
git submodule update --init
devenv shell
./scripts/build-debug.sh  # cargo build of matrix-sdk-ffi for the host + regenerates the bindings
dotnet build
dotnet test               # requires docker
```

- Never edit the generated `src/Matrix.RustSdk.Bindings/*.cs` by hand, change `scripts/generate-bindings.sh` or
  `uniffi.toml` and regenerate. Regenerating with the same SDK version must not produce a diff.
- `TreatWarningsAsErrors` is enabled (except for the generated bindings), builds have to be warning free.
- C# and project files are formatted with csharpier (`dotnet csharpier format .`), a git hook installed by devenv
  checks it. Outside the devenv shell run `dotnet tool restore` first, otherwise the hook fails.
- Commits follow conventional commits (`feat:`, `fix:`, `chore:`, `ci:`, `test:`, `build:`, `docs:`).

## Bindings generation

`scripts/generate-bindings.sh` runs uniffi-bindgen-cs in library mode against the debug build. Things that aren't
obvious:

- The uniffi version of matrix-rust-sdk must match the uniffi-bindgen-cs version (`UNIFFI_RS_VERSION` in
  `devenv.nix`), see RELEASING.md for how to find compatible versions.
- Library mode has to run inside the cargo workspace, it uses `cargo metadata` to find the crates.
- The `--config` file is applied to **every** crate. All crates in one namespace don't compile (each generated file
  contains its own uniffi helper types), so the script generates each crate separately with its own namespace
  (`Matrix.RustSdk.Bindings` for `matrix_sdk_ffi`, `Matrix.RustSdk.Bindings.<Crate>` for the others) and maps the other
  crates with `external_packages`. New crates are discovered automatically.
- The push rule enum `Action` shadows `System.Action` in the generated async helpers. Renaming it via `uniffi.toml`
  makes uniffi-bindgen-cs panic, so the script qualifies the delegate with `sed` after generation.
- uniffi 0.31 keys renames in `uniffi.toml` by module path (`[bindings.csharp.rename.<crate>]`), the
  uniffi-bindgen-cs docs show an outdated format.
- uniffi-bindgen-cs generates `internal` types by default, `uniffi.toml` sets `access_modifier = "public"`.
- uniffi-bindgen-cs formats the output by calling `csharpier format`, devenv provides a `csharpier` wrapper script.

## Tests

- TUnit on Microsoft.Testing.Platform (`global.json` switches `dotnet test` to it). Don't add `Microsoft.NET.Test.Sdk`
  or coverlet, they break TUnit. Coverage: `dotnet test --coverage --coverage-output-format cobertura`.
- `SdkTests` run against a tuwunel homeserver started with Testcontainers, they are in the `Homeserver` category.
  `NativeLibraryTests` are smoke tests without a homeserver, they run on every platform.
- The native library under test defaults to the debug build, `-p:NativeLibraryPath=...` selects another one (CI uses
  this to test the release libraries). Filter out the homeserver tests with
  `--treenode-filter '/*/*/*/*[Category!=Homeserver]'`.

## CI and packaging

- `packages.yml` builds the native library with the `small-release-stripped` profile on native runners for every
  platform, tests that exact library on its runner and packs the packages. Linux and musl run all tests, Windows and
  macOS runners have no linux containers and only run the smoke tests.
- musl: GitHub's javascript actions don't run in alpine containers on arm64, so the jobs start containers themselves
  (`.github/scripts/build-musl.sh` in `rust:1-alpine`, `.github/scripts/test-musl.sh` in the .NET alpine image with the
  docker socket). musl shared libraries need `-C target-feature=-crt-static`. The scripts can be run locally with the
  `docker run` commands in their headers.
- Windows arm64: aws-lc-sys requires clang-cl, the workflow adds the one shipped with Visual Studio to the `PATH`.
- Release builds of the native packages fail if a library is missing in `native/runtimes/<rid>/native`, CI assembles
  that directory from the artifacts. Debug builds don't pack the native packages.
- The native packages depend on the exact version of `Matrix.RustSdk.Bindings` (`ExactProjectReferenceVersions`), see
  RELEASING.md for why.
- Every package contains `LICENSE` and `THIRD-PARTY-NOTICES.txt` (licenses of matrix-rust-sdk and all linked crates,
  generated with cargo-about by `scripts/generate-notices.sh`), release builds fail without the notices. Don't accept
  new licenses in `licenses/about.toml` without checking them, see RELEASING.md.
- Publishing uses nuget.org trusted publishing (OIDC, `NuGet/login`), there's no API key secret. Afterwards the
  `release` job creates the GitHub release and tag with the job token (`.github/scripts/create-release.sh`).
- nuget.org rejects packages over 250 MB, the native libraries are ~25 MB compressed each, keep the per-OS split in
  mind when adding platforms.
- Cross compiling the native libraries locally (cross, cargo-zigbuild, cargo-xwin) was tried and removed in favor of
  native runners, don't reintroduce it.
