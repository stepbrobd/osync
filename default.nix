{ lib
, buildDotnetModule
, dotnetCorePackages
, makeWrapper
, openssh
, rsync
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
  makeWrapperArgs = [ "--set" "DOTNET_SYSTEM_GLOBALIZATION_INVARIANT" "1" ];

  src = lib.fileset.toSource {
    root = ./.;
    fileset = lib.fileset.unions [
      ./src
      ./osync.fsproj
      ./nuget.config
      ./version.txt
    ];
  };

  nugetDeps = ./nuget.json;

  nativeBuildInputs = [ makeWrapper ];

  postFixup = ''
    wrapProgram $out/bin/osync \
      --prefix PATH : ${lib.makeBinPath [ openssh rsync ]}
  '';

  executables = [ "osync" ];
}
