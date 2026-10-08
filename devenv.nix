{
  pkgs,
  lib,
  config,
  inputs,
  ...
}: {
  # https://devenv.sh/basics/
  env = {
    # No uniffi-bindgen-cs release supports uniffi 0.32 yet, the revision is the commit of the open upgrade pull request
    # https://github.com/NordSecurity/uniffi-bindgen-cs/pull/176 (version 0.12.0), patches/uniffi-bindgen-cs/*.patch
    # are applied on top. The version is only used for the release notes (.github/scripts/create-release.sh).
    UNIFFI_BINDGEN_CS_VERSION = "0.12.0";
    UNIFFI_BINDGEN_CS_REV = "0fc022aa1d73fb1dda91a778b63f2824d7dca58b";
    UNIFFI_RS_VERSION = "0.32.0";
  };

  # https://devenv.sh/packages/
  packages = [
    pkgs.git
    pkgs.sqlite
    # generates THIRD-PARTY-NOTICES.txt, see scripts/generate-notices.sh
    pkgs.cargo-about
  ];

  # https://devenv.sh/languages/
  languages = {
    rust = {
      enable = true;
      channel = "stable";
      # release builds for all platforms happen natively on CI runners, see .github/workflows/nuget.yml
    };
    dotnet = {
      enable = true;
      package = pkgs.dotnetCorePackages.sdk_10_0;
    };
  };

  # https://devenv.sh/scripts/
  scripts = {
    uniffi-bindgen-cs.exec = ''$DEVENV_STATE/cargo-install/bin/uniffi-bindgen-cs "$@"'';
    # uniffi-bindgen-cs expects csharpier to be in the path
    csharpier.exec = ''dotnet csharpier "$@"'';
  };

  # https://devenv.sh/tasks/
  tasks = {
    "dotnet:tool:restore".exec = "dotnet tool restore";
    "cargo:install:bindgen" = {
      exec = ''
        set -euo pipefail
        SRC=$DEVENV_STATE/uniffi-bindgen-cs
        rm -rf "$SRC"
        git init -q "$SRC"
        git -C "$SRC" fetch -q --depth 1 https://github.com/NordSecurity/uniffi-bindgen-cs "$UNIFFI_BINDGEN_CS_REV"
        git -C "$SRC" checkout -q FETCH_HEAD
        for PATCH in "$DEVENV_ROOT"/patches/uniffi-bindgen-cs/*.patch; do
          git -C "$SRC" apply "$PATCH"
        done
        cargo install --locked --force --path "$SRC/bindgen"
        echo "$UNIFFI_BINDGEN_CS_REV $(cat "$DEVENV_ROOT"/patches/uniffi-bindgen-cs/*.patch | sha256sum)" >"$SRC.stamp"
      '';
      # reinstall when the commit or the patches change
      status = ''[ "$(cat "$DEVENV_STATE/uniffi-bindgen-cs.stamp")" = "$UNIFFI_BINDGEN_CS_REV $(cat "$DEVENV_ROOT"/patches/uniffi-bindgen-cs/*.patch | sha256sum)" ]'';
    };
    "devenv:enterShell".after = [
      "dotnet:tool:restore"
      "cargo:install:bindgen"
    ];
  };

  # https://devenv.sh/tests/
  enterTest = ''
    echo "Running tests"
    git --version | grep --color=auto "${pkgs.git.version}"
  '';

  # https://devenv.sh/git-hooks/
  # git-hooks.hooks.shellcheck.enable = true;
  git-hooks.hooks = {
    alejandra.enable = true;
    csharpier = {
      enable = true;
      name = "CSharpier";
      entry = "dotnet csharpier format";
    };
  };

  # See full reference at https://devenv.sh/reference/options/
}
