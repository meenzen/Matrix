#!/usr/bin/env bash

set -eo pipefail

# uniffi-bindgen-cs is installed by devenv, see UNIFFI_BINDGEN_CS_VERSION in devenv.nix

echo "=> Generating bindings..."

BASE_DIRECTORY=$(pwd)
UNIFFI_CONFIG=$BASE_DIRECTORY/src/Matrix.RustSdk.Bindings/uniffi.toml
RUST_SDK_DIRECTORY=external/matrix-rust-sdk
LIBRARY_FILE=target/debug/libmatrix_sdk_ffi.so
UNIFFI_OUTPUT=$BASE_DIRECTORY/external/matrix-rust-sdk/generated-bindings
OUTPUT=$BASE_DIRECTORY/src/Matrix.RustSdk.Bindings
ROOT_NAMESPACE=Matrix.RustSdk.Bindings

# matrix_sdk_ffi -> Matrix.RustSdk.Bindings, matrix_sdk_base -> Matrix.RustSdk.Bindings.Base, ruma_events -> Matrix.RustSdk.Bindings.RumaEvents
namespace_for() {
  case "$1" in
  matrix_sdk_ffi) echo "$ROOT_NAMESPACE" ;;
  matrix_sdk) echo "$ROOT_NAMESPACE.Sdk" ;;
  *) echo "$ROOT_NAMESPACE.$(echo "${1#matrix_sdk_}" | sed -E 's/(^|_)([a-z])/\U\2/g')" ;;
  esac
}

cd $RUST_SDK_DIRECTORY
rm -rf "$UNIFFI_OUTPUT"

# library mode needs to run inside the cargo workspace, it uses `cargo metadata` to locate the crates
echo "=> Discovering crates..."
uniffi-bindgen-cs $LIBRARY_FILE --library --no-format --out-dir "$UNIFFI_OUTPUT/discover" >/dev/null
CRATES=$(find "$UNIFFI_OUTPUT/discover" -name '*.cs' -printf '%f\n' | sed 's/\.cs$//' | sort)

# The --config file is applied to every crate, so each crate is generated separately with its own namespace.
# Types of the other crates are mapped to their namespaces with external_packages.
for CRATE in $CRATES; do
  CONFIG=$UNIFFI_OUTPUT/$CRATE.toml
  {
    cat "$UNIFFI_CONFIG"
    echo
    echo "namespace = \"$(namespace_for "$CRATE")\""
    echo
    echo "[bindings.csharp.external_packages]"
    for OTHER in $CRATES; do
      echo "$OTHER = \"$(namespace_for "$OTHER")\""
    done
  } >"$CONFIG"

  echo "=> Generating $CRATE ($(namespace_for "$CRATE"))"
  uniffi-bindgen-cs $LIBRARY_FILE --library --crate "$CRATE" --config "$CONFIG" --out-dir "$UNIFFI_OUTPUT" |
    sed '/^Writing bindings file/d'
done

echo "=> Copying generated bindings to the Matrix.RustSdk.Bindings project..."
cd $BASE_DIRECTORY
rm -f $OUTPUT/*.cs
cp $UNIFFI_OUTPUT/*.cs $OUTPUT/

echo "=> Cleaning up matrix-rust-sdk repository..."
rm -rf "$UNIFFI_OUTPUT"

# The push rule `Action` enum of matrix_sdk_ffi shadows `System.Action`, which the generated async helpers use.
# Renaming the enum via uniffi.toml makes uniffi-bindgen-cs panic, so qualify the delegate instead.
echo "=> Fixing System.Action references"
sed -i 's/InvokeCallbackOnce(Action invoke)/InvokeCallbackOnce(System.Action invoke)/' $OUTPUT/matrix_sdk_ffi.cs
