{
  pkgs,
  lib,
  config,
  inputs,
  ...
}: {
  # https://devenv.sh/basics/
  env = {
    UNIFFI_BINDGEN_CS_VERSION = "0.11.0";
    UNIFFI_RS_VERSION = "0.31.0";
  };

  # https://devenv.sh/packages/
  packages = [
    pkgs.git
    pkgs.sqlite
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
      package = pkgs.dotnetCorePackages.sdk_9_0;
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
      exec = "cargo install --git https://github.com/NordSecurity/uniffi-bindgen-cs --tag v$UNIFFI_BINDGEN_CS_VERSION+v$UNIFFI_RS_VERSION";
      #status = ''$DEVENV_STATE/cargo-install/bin/uniffi-bindgen-cs --version | grep -q -F "$UNIFFI_BINDGEN_CS_VERSION+v$UNIFFI_RS_VERSION"'';
      # workaround wrong version number: https://github.com/NordSecurity/uniffi-bindgen-cs/issues/115
      status = ''$DEVENV_STATE/cargo-install/bin/uniffi-bindgen-cs --version | grep -q -F "v$UNIFFI_RS_VERSION"'';
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
