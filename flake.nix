{
  inputs.nixpkgs.url = "github:nixos/nixpkgs/nixos-unstable";
  inputs.systems.url = "github:nix-systems/default";
  inputs.parts.url = "github:hercules-ci/flake-parts";
  inputs.parts.inputs.nixpkgs-lib.follows = "nixpkgs";

  outputs = inputs: inputs.parts.lib.mkFlake { inherit inputs; } {
    systems = import inputs.systems;

    perSystem = { lib, pkgs, system, ... }:
      let
        dotnet-sdk = pkgs.dotnetCorePackages.sdk_10_0;
        dotnet-runtime = pkgs.dotnetCorePackages.runtime_10_0;
      in
      {
        _module.args.pkgs = import inputs.nixpkgs { inherit system; };

        devShells.default = pkgs.mkShell {
          packages = with pkgs; [
            dotnet-sdk
            fantomas
            fsautocomplete
            nixpkgs-fmt
            openssh
          ];

          shellHook = ''
            export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
            export NUGET_PACKAGES=$PWD/.nuget/packages
            mkdir -p "$NUGET_PACKAGES"
          '';
        };

        packages.default = pkgs.buildDotnetModule {
          inherit dotnet-sdk dotnet-runtime;

          pname = "osync";
          projectFile = "osync.fsproj";
          version = lib.fileContents ./version.txt;

          src = lib.fileset.toSource {
            root = ./.;
            fileset = lib.fileset.unions [
              ./src
              ./osync.fsproj
              ./version.txt
            ];
          };

          nugetDeps = ./nuget.json;

          executables = [ "osync" ];
        };

        formatter = pkgs.writeShellScriptBin "formatter" ''
          set -eoux pipefail
          shopt -s globstar

          root="$PWD"
          while [[ ! -f "$root/.git/index" ]]; do
            if [[ "$root" == "/" ]]; then
              exit 1
            fi
            root="$(dirname "$root")"
          done
          pushd "$root" > /dev/null

          ${lib.getExe pkgs.deno} fmt .
          ${lib.getExe pkgs.fantomas} --verbosity detailed src/
          ${lib.getExe pkgs.gitleaks} git --pre-commit --staged --verbose
          ${lib.getExe pkgs.nixpkgs-fmt} .

          popd
        '';
      };
  };
}
