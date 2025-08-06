# https://github.com/cross-rs/cross/issues/1676#issuecomment-3156552870
{
  pkgs,
  lib,
  ...
}:
pkgs.rustPackages.rustPlatform.buildRustPackage rec {
  pname = "cargo-cross";
  version = "e281947";

  src = pkgs.fetchFromGitHub {
    owner = "cross-rs";
    repo = "cross";
    rev = "e281947ca900da425e4ecea7483cfde646c8a1ea";
    sha256 = "sha256-92fpq9lsnxU51X8Mmk/34fd4921nu8tFJYLVgIm35kk=";
  };

  useFetchCargoVendor = true;
  cargoHash = "sha256-wUWhWbnjKTPiAlc/c8TR7GM4nLhcO4ARZH+7sAc8BHo=";

  checkFlags = [
    "--skip=docker::shared::tests::directories::test_host"

    # The following tests require empty CARGO_BUILD_TARGET env variable, but we
    # set it ever since https://github.com/NixOS/nixpkgs/pull/298108.
    "--skip=config::tests::test_config::no_env_and_no_toml_default_target_then_none"
    "--skip=config::tests::test_config::no_env_but_toml_default_target_then_use_toml"
  ];
}
