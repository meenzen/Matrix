#!/usr/bin/env bash

set -eo pipefail

# cargo install uniffi-bindgen-cs --git https://github.com/NordSecurity/uniffi-bindgen-cs --tag v0.7.0+v0.25.0

echo "=> Generating bindings..."

BASE_DIRECTORY=$(pwd)
UNIFFI_CONFIG=src/Matrix.RustSdk.Bindings/uniffi.toml
RUST_SDK_DIRECTORY=external/matrix-rust-sdk
LIBRARY_FILE=target/debug/libmatrix_sdk_ffi.so
UNIFFI_OUTPUT=external/matrix-rust-sdk/generated-bindings
OUTPUT=src/Matrix.RustSdk.Bindings

#cp $UNIFFI_CONFIG $RUST_SDK_DIRECTORY/bindings/matrix-sdk-ffi/uniffi.toml
cd $RUST_SDK_DIRECTORY

uniffi-bindgen-cs $LIBRARY_FILE --library --out-dir generated-bindings

echo "=> Copying generated bindings to the Matrix.RustSdk.Bindings project..."
cd $BASE_DIRECTORY
cp -r $UNIFFI_OUTPUT/. $OUTPUT/

echo "=> Cleaning up matrix-rust-sdk repository..."
rm -rf $UNIFFI_OUTPUT

cd $BASE_DIRECTORY

echo "=> Removing broken usings"
sed -i '/using uniffi.matrix_sdk_base\..*;/d' $OUTPUT/matrix_sdk_ffi.cs
sed -i '/using uniffi.matrix_sdk_common\..*;/d' $OUTPUT/matrix_sdk_ffi.cs
sed -i '/using uniffi.matrix_sdk_crypto\..*;/d' $OUTPUT/matrix_sdk_ffi.cs
sed -i '/using uniffi.matrix_sdk_ui\..*;/d' $OUTPUT/matrix_sdk_ffi.cs
sed -i '/using uniffi.matrix_sdk\..*;/d' $OUTPUT/matrix_sdk_ffi.cs

echo "=> Adding correct usings"
USINGS="using uniffi.matrix_sdk_base;\nusing uniffi.matrix_sdk_common;\nusing uniffi.matrix_sdk_crypto;\nusing uniffi.matrix_sdk_ui;\nusing uniffi.matrix_sdk;"
sed -i "s/#nullable enable/#nullable enable\n\n${USINGS}/" $OUTPUT/matrix_sdk_ffi.cs