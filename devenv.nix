{
  pkgs,
  lib,
  config,
  inputs,
  ...
}: {
  # https://devenv.sh/basics/
  env = {
    UNIFFI_BINDGEN_CS_VERSION = "0.9.1";
    UNIFFI_RS_VERSION = "0.28.3";
    CROSS_VERSION = "0.2.5";
  };

  # https://devenv.sh/packages/
  packages = [
    pkgs.git
    pkgs.openssl
    pkgs.sqlite
  ];

  # https://devenv.sh/languages/
  languages = {
    rust = {
      enable = true;
      channel = "nightly";
    };
    dotnet = {
      enable = true;
      package = pkgs.dotnetCorePackages.sdk_9_0;
    };
  };

  # https://devenv.sh/scripts/
  scripts = {
    uniffi-bindgen-cs.exec = ''$DEVENV_STATE/cargo-install/bin/uniffi-bindgen-cs "$@"'';
    cross.exec = ''$DEVENV_STATE/cargo-install/bin/cross "$@"'';
    cross-util.exec = ''$DEVENV_STATE/cargo-install/bin/cross-util "$@"'';

    # uniffi-bindgen-cs expects csharpier to be in the path
    dotnet-csharpier.exec = ''dotnet csharpier format "$@"'';
  };

  # https://devenv.sh/tasks/
  tasks = {
    "dotnet:tool:restore".exec = "dotnet tool restore";
    "cargo:install:bindgen" = {
      exec = "cargo install --git https://github.com/NordSecurity/uniffi-bindgen-cs --tag v$UNIFFI_BINDGEN_CS_VERSION+v$UNIFFI_RS_VERSION";
      #status = ''$DEVENV_STATE/cargo-install/bin/uniffi-bindgen-cs --version | grep -q -F "$UNIFFI_BINDGEN_CS_VERSION+v$UNIFFI_RS_VERSION"'';
      # workaround wrong version number: https://github.com/NordSecurity/uniffi-bindgen-cs/issues/115
      status = ''$DEVENV_STATE/cargo-install/bin/uniffi-bindgen-cs --version | grep -q -F "v$UNIFFI_RS_VERSION"'';
    };
    "cargo:install:cross" = {
      exec = "cargo install cross --git https://github.com/cross-rs/cross";
      status = ''$DEVENV_STATE/cargo-install/bin/cross --version | grep -q -F "$CROSS_VERSION"'';
    };
    "devenv:enterShell".after = [
      "dotnet:tool:restore"
      "cargo:install:bindgen"
      "cargo:install:cross"
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
