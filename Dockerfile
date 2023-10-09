FROM rust:1.73

RUN cargo install uniffi-bindgen-cs --git https://github.com/NordSecurity/uniffi-bindgen-cs --tag v0.2.4+v0.23.0

WORKDIR /app