{ lib
, buildDotnetModule
, dotnetCorePackages
}:

buildDotnetModule {
  meta.mainProgram = "osync";
  pname = "osync";
  projectFile = "osync.fsproj";
  version = lib.fileContents ./version.txt;

  dotnet-sdk = dotnetCorePackages.sdk_10_0;
  dotnet-runtime = dotnetCorePackages.runtime_10_0;

  selfContainedBuild = true;

  env.DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = 1;

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
}
