module Osync.Sync

open System
open System.IO
open System.Text.Json

type MachineState =
    { BeatmapSetIds: Set<int>
      SkinIdentifiers: Map<string, string> // hash -> name
      Settings: Map<string, string>
      RawSettingsLines: string array
      AutoDownload: bool
      DataPath: string
      RealmPath: string
      SettingsPath: string }

// --- Extraction ---

let extractLocal (dirOverride: string option) : Result<MachineState, string> =
    try
        let dataPath = dirOverride |> Option.defaultWith Realm.getOsuDataPath

        let settingsPath = Path.Combine(dataPath, "game.ini")

        let beatmapSetIds = Realm.readBeatmapSetIds dataPath
        let skinIdentifiers = Realm.readSkinIdentifiers dataPath

        let rawLines =
            if File.Exists(settingsPath) then
                File.ReadAllLines(settingsPath)
            else
                Array.empty

        let settings = Settings.parseLines rawLines

        let autoDownload =
            settings
            |> Map.tryFind "AutomaticallyDownloadMissingBeatmaps"
            |> Option.map (fun v -> v.ToLowerInvariant() = "true" || v = "1")
            |> Option.defaultValue false

        let realmPath = Realm.findRealmPath dataPath

        Ok
            { BeatmapSetIds = beatmapSetIds
              SkinIdentifiers = skinIdentifiers
              Settings = settings
              RawSettingsLines = rawLines
              AutoDownload = autoDownload
              DataPath = dataPath
              RealmPath = realmPath
              SettingsPath = settingsPath }
    with ex ->
        Error $"Failed to extract local state: {ex.Message}"

let serializeExtractJson (state: MachineState) : string =
    use stream = new MemoryStream()
    use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))

    writer.WriteStartObject()

    writer.WritePropertyName("beatmapSetIds")
    writer.WriteStartArray()

    for id in state.BeatmapSetIds do
        writer.WriteNumberValue(id)

    writer.WriteEndArray()

    writer.WritePropertyName("skinIdentifiers")
    writer.WriteStartObject()

    for kv in state.SkinIdentifiers do
        writer.WriteString(kv.Key, kv.Value)

    writer.WriteEndObject()

    writer.WritePropertyName("settings")
    writer.WriteStartObject()

    for kv in state.Settings do
        writer.WriteString(kv.Key, kv.Value)

    writer.WriteEndObject()

    writer.WritePropertyName("rawSettingsLines")
    writer.WriteStartArray()

    for line in state.RawSettingsLines do
        writer.WriteStringValue(line)

    writer.WriteEndArray()

    writer.WriteBoolean("autoDownload", state.AutoDownload)
    writer.WriteString("dataPath", state.DataPath)
    writer.WriteString("realmPath", state.RealmPath)
    writer.WriteString("settingsPath", state.SettingsPath)

    writer.WriteEndObject()
    writer.Flush()

    System.Text.Encoding.UTF8.GetString(stream.ToArray())

let parseExtractJson (json: string) : Result<MachineState, string> =
    try
        let doc = JsonDocument.Parse(json)
        let root = doc.RootElement

        let beatmapSetIds =
            root.GetProperty("beatmapSetIds").EnumerateArray()
            |> Seq.map (fun e -> e.GetInt32())
            |> Set.ofSeq

        let skinIdentifiers =
            match root.TryGetProperty("skinIdentifiers") with
            | true, prop ->
                prop.EnumerateObject()
                |> Seq.map (fun p -> p.Name, p.Value.GetString())
                |> Map.ofSeq
            | _ -> Map.empty

        let settings =
            root.GetProperty("settings").EnumerateObject()
            |> Seq.map (fun p -> p.Name, p.Value.GetString())
            |> Map.ofSeq

        let rawSettingsLines =
            root.GetProperty("rawSettingsLines").EnumerateArray()
            |> Seq.map (fun e -> e.GetString())
            |> Seq.toArray

        let autoDownload = root.GetProperty("autoDownload").GetBoolean()
        let dataPath = root.GetProperty("dataPath").GetString()

        let realmPath =
            match root.TryGetProperty("realmPath") with
            | true, prop -> prop.GetString()
            | _ -> IO.Path.Combine(dataPath, "client.realm")

        let settingsPath = root.GetProperty("settingsPath").GetString()

        Ok
            { BeatmapSetIds = beatmapSetIds
              SkinIdentifiers = skinIdentifiers
              Settings = settings
              RawSettingsLines = rawSettingsLines
              AutoDownload = autoDownload
              DataPath = dataPath
              RealmPath = realmPath
              SettingsPath = settingsPath }
    with ex ->
        Error $"Failed to parse extract JSON: {ex.Message}"

// --- Host resolution ---

type ResolvedHost =
    | Localhost
    | Remote of string

let extractState (host: ResolvedHost) (dirOverride: string option) (osyncPath: string) : Result<MachineState, string> =
    match host with
    | Localhost -> extractLocal dirOverride
    | Remote hostname ->
        let cmd =
            match dirOverride with
            | Some d -> $"{Ssh.shellEscape osyncPath} extract --dir {Ssh.shellEscape d}"
            | None -> $"{Ssh.shellEscape osyncPath} extract"

        Ssh.runRemote hostname cmd |> Result.bind parseExtractJson

// --- Helpers ---

let private mapUrl (id: int) = $"https://osu.ppy.sh/beatmapsets/{id}"

let private promptYesNo (message: string) : bool =
    eprintfn "%s [y/N] " message
    let input = Console.ReadLine()

    match input with
    | null -> false
    | s -> s.Trim().ToLowerInvariant() = "y"

let private promptSettingChoice (diff: Settings.SettingsDiff) (fromLabel: string) (toLabel: string) : char =
    eprintfn ""

    match diff.LocalValue, diff.RemoteValue with
    | Some lv, Some rv -> eprintfn "  %s: %s=%s, %s=%s" diff.Key fromLabel lv toLabel rv
    | Some lv, None -> eprintfn "  %s: %s=%s, %s=(missing)" diff.Key fromLabel lv toLabel
    | None, Some rv -> eprintfn "  %s: %s=(missing), %s=%s" diff.Key fromLabel toLabel rv
    | None, None -> ()

    eprintfn "  [F]rom (%s) / [T]o (%s) / [S]kip?" fromLabel toLabel

    let rec loop () =
        eprintf "  > "
        let input = Console.ReadLine()

        match input with
        | null -> 'S'
        | s ->
            match s.Trim().ToUpperInvariant() with
            | "F" -> 'L'
            | "T" -> 'R'
            | "S" -> 'S'
            | _ ->
                eprintfn "  Invalid choice. Enter F, T, or S."
                loop ()

    loop ()

let private writeSettings
    (host: ResolvedHost)
    (path: string)
    (rawLines: string array)
    (updates: Map<string, string>)
    : Result<unit, string> =
    let patched = Settings.patchLines rawLines updates
    let content = String.Join("\n", patched)

    match host with
    | Localhost ->
        try
            File.WriteAllText(path, content)
            Ok()
        with ex ->
            Error $"Failed to write settings to {path}: {ex.Message}"
    | Remote hostname -> Ssh.writeRemoteFile hostname path content

let private enableAutoDownload (host: ResolvedHost) (state: MachineState) : Result<unit, string> =
    let updated = Map.add "AutomaticallyDownloadMissingBeatmaps" "True" state.Settings
    writeSettings host state.SettingsPath state.RawSettingsLines updated

// --- Run modes ---

type SyncMode =
    | Sync
    | Diff

type RunConfig =
    { Mode: SyncMode
      FromHost: ResolvedHost
      ToHost: ResolvedHost
      FromDir: string option
      ToDir: string option
      FromOsync: string
      ToOsync: string
      FromLabel: string
      ToLabel: string }

let private printBeatmapDiffs (fromState: MachineState) (toState: MachineState) (fromLabel: string) (toLabel: string) =
    let missingOnTo = Set.difference fromState.BeatmapSetIds toState.BeatmapSetIds
    let missingOnFrom = Set.difference toState.BeatmapSetIds fromState.BeatmapSetIds

    eprintfn ""
    eprintfn "=== Beatmap Differences ==="

    if Set.isEmpty missingOnTo && Set.isEmpty missingOnFrom then
        eprintfn "  No beatmap differences found."
    else
        if not (Set.isEmpty missingOnTo) then
            eprintfn ""
            eprintfn "  Missing on %s (%d maps):" toLabel (Set.count missingOnTo)

            for id in missingOnTo |> Set.toList |> List.truncate 20 do
                eprintfn "    %s" (mapUrl id)

            if Set.count missingOnTo > 20 then
                eprintfn "    ... and %d more" (Set.count missingOnTo - 20)

        if not (Set.isEmpty missingOnFrom) then
            eprintfn ""
            eprintfn "  Missing on %s (%d maps):" fromLabel (Set.count missingOnFrom)

            for id in missingOnFrom |> Set.toList |> List.truncate 20 do
                eprintfn "    %s" (mapUrl id)

            if Set.count missingOnFrom > 20 then
                eprintfn "    ... and %d more" (Set.count missingOnFrom - 20)

    (missingOnFrom, missingOnTo)

let private printSkinDiffs (fromState: MachineState) (toState: MachineState) (fromLabel: string) (toLabel: string) =
    let fromHashes = fromState.SkinIdentifiers |> Map.keys |> Set.ofSeq
    let toHashes = toState.SkinIdentifiers |> Map.keys |> Set.ofSeq
    let missingOnTo = Set.difference fromHashes toHashes
    let missingOnFrom = Set.difference toHashes fromHashes

    eprintfn ""
    eprintfn "=== Skin Differences ==="

    if Set.isEmpty missingOnTo && Set.isEmpty missingOnFrom then
        eprintfn "  No skin differences found."
    else
        if not (Set.isEmpty missingOnTo) then
            eprintfn ""
            eprintfn "  Missing on %s (%d skins):" toLabel (Set.count missingOnTo)

            for hash in missingOnTo |> Set.toList |> List.truncate 20 do
                let name = Map.find hash fromState.SkinIdentifiers
                eprintfn "    %s" name

            if Set.count missingOnTo > 20 then
                eprintfn "    ... and %d more" (Set.count missingOnTo - 20)

        if not (Set.isEmpty missingOnFrom) then
            eprintfn ""
            eprintfn "  Missing on %s (%d skins):" fromLabel (Set.count missingOnFrom)

            for hash in missingOnFrom |> Set.toList |> List.truncate 20 do
                let name = Map.find hash toState.SkinIdentifiers
                eprintfn "    %s" name

            if Set.count missingOnFrom > 20 then
                eprintfn "    ... and %d more" (Set.count missingOnFrom - 20)

    (missingOnFrom, missingOnTo)

let private updateAutoDownload (state: MachineState) : MachineState =
    let key = "AutomaticallyDownloadMissingBeatmaps"

    { state with
        Settings = Map.add key "True" state.Settings
        RawSettingsLines = Settings.patchLines state.RawSettingsLines (Map.add key "True" state.Settings)
        AutoDownload = true }

let private handleAutoDownload
    (config: RunConfig)
    (fromState: MachineState)
    (toState: MachineState)
    (missingOnFrom: Set<int>)
    (missingOnTo: Set<int>)
    : bool * MachineState * MachineState =
    let mutable hadError = false
    let mutable from = fromState
    let mutable ``to`` = toState

    if not (Set.isEmpty missingOnTo) && not toState.AutoDownload then
        eprintfn ""

        if promptYesNo $"  AutomaticallyDownloadMissingBeatmaps is disabled on {config.ToLabel}. Enable it?" then
            ``to`` <- updateAutoDownload ``to``

            match enableAutoDownload config.ToHost ``to`` with
            | Ok() -> eprintfn "  Enabled auto-download on %s." config.ToLabel
            | Error e ->
                eprintfn "  Error enabling auto-download on %s: %s" config.ToLabel e
                hadError <- true

    if not (Set.isEmpty missingOnFrom) && not fromState.AutoDownload then
        eprintfn ""

        if promptYesNo $"  AutomaticallyDownloadMissingBeatmaps is disabled on {config.FromLabel}. Enable it?" then
            from <- updateAutoDownload from

            match enableAutoDownload config.FromHost from with
            | Ok() -> eprintfn "  Enabled auto-download on %s." config.FromLabel
            | Error e ->
                eprintfn "  Error enabling auto-download on %s: %s" config.FromLabel e
                hadError <- true

    (hadError, from, ``to``)

let private handleSettingsDiff (config: RunConfig) (fromState: MachineState) (toState: MachineState) : bool =
    let diffs = Settings.diff fromState.Settings toState.Settings
    let mutable hadError = false

    eprintfn ""
    eprintfn "=== Settings Differences ==="

    if List.isEmpty diffs then
        eprintfn "  No settings differences found."
    else
        eprintfn "  Found %d setting(s) that differ." (List.length diffs)

        match config.Mode with
        | Diff ->
            for d in diffs do
                match d.LocalValue, d.RemoteValue with
                | Some lv, Some rv -> eprintfn "  %s: %s=%s, %s=%s" d.Key config.FromLabel lv config.ToLabel rv
                | Some lv, None -> eprintfn "  %s: %s=%s, %s=(missing)" d.Key config.FromLabel lv config.ToLabel
                | None, Some rv -> eprintfn "  %s: %s=(missing), %s=%s" d.Key config.FromLabel config.ToLabel rv
                | None, None -> ()
        | Sync ->
            let choices =
                diffs
                |> List.map (fun d ->
                    let choice = promptSettingChoice d config.FromLabel config.ToLabel
                    (d.Key, choice))

            let activeChoices = choices |> List.filter (fun (_, c) -> c <> 'S')

            if not (List.isEmpty activeChoices) then
                let (updatedFrom, updatedTo) =
                    Settings.applyChoice fromState.Settings toState.Settings activeChoices

                if updatedFrom <> fromState.Settings then
                    eprintfn ""
                    eprintfn "  Writing updated settings to %s..." config.FromLabel

                    match
                        writeSettings config.FromHost fromState.SettingsPath fromState.RawSettingsLines updatedFrom
                    with
                    | Ok() -> eprintfn "  Done."
                    | Error e ->
                        eprintfn "  Error: %s" e
                        hadError <- true

                if updatedTo <> toState.Settings then
                    eprintfn ""
                    eprintfn "  Writing updated settings to %s..." config.ToLabel

                    match writeSettings config.ToHost toState.SettingsPath toState.RawSettingsLines updatedTo with
                    | Ok() -> eprintfn "  Done."
                    | Error e ->
                        eprintfn "  Error: %s" e
                        hadError <- true

    hadError

let private hostOption (h: ResolvedHost) =
    match h with
    | Remote h -> Some h
    | Localhost -> None

let private syncFilesLocal
    (config: RunConfig)
    (fromState: MachineState)
    (toState: MachineState)
    (missingBeatmapIds: Set<int>)
    (missingSkinHashes: Set<string>)
    : bool =
    let totalMaps = Set.count missingBeatmapIds
    let totalSkins = Set.count missingSkinHashes

    eprintfn ""
    eprintfn "  Copying realm database from %s..." config.FromLabel

    match Ssh.copyRealmToTemp (hostOption config.FromHost) fromState.RealmPath with
    | Error e ->
        eprintfn "  Error: %s" e
        true
    | Ok tempRealmPath ->
        try
            try
                use sourceRealm = Realm.openRealmAt tempRealmPath

                let beatmapSets =
                    if Set.isEmpty missingBeatmapIds then
                        []
                    else
                        eprintfn "  Reading %d beatmap set(s) from %s..." totalMaps config.FromLabel
                        Realm.readBeatmapSets sourceRealm missingBeatmapIds

                let skins =
                    if Set.isEmpty missingSkinHashes then
                        []
                    else
                        eprintfn "  Reading %d skin(s) from %s..." totalSkins config.FromLabel
                        Realm.readSkins sourceRealm missingSkinHashes

                let fileHashes = Realm.collectFileHashes beatmapSets skins

                if not (Set.isEmpty fileHashes) then
                    eprintfn "  Syncing %d file(s)..." (Set.count fileHashes)

                    match
                        Ssh.rsyncFiles (hostOption config.FromHost) None fromState.DataPath toState.DataPath fileHashes
                    with
                    | Error e ->
                        eprintfn "  Error syncing files: %s" e
                        true
                    | Ok() ->
                        eprintfn "  Writing to realm on %s..." config.ToLabel

                        use destRealm = Realm.openRealm toState.DataPath

                        if not (List.isEmpty beatmapSets) then
                            Realm.writeBeatmapSets destRealm beatmapSets

                        if not (List.isEmpty skins) then
                            Realm.writeSkins destRealm skins

                        eprintfn "  Synced %d beatmap(s) and %d skin(s)." (List.length beatmapSets) (List.length skins)
                        false
                else
                    eprintfn "  No files to sync."
                    false
            with ex ->
                eprintfn "  Error during sync: %s" ex.Message
                true
        finally
            if File.Exists(tempRealmPath) then
                File.Delete(tempRealmPath)

let private syncFilesRemote
    (config: RunConfig)
    (fromState: MachineState)
    (toState: MachineState)
    (missingBeatmapIds: Set<int>)
    (missingSkinHashes: Set<string>)
    : bool =
    let toHostname =
        match config.ToHost with
        | Remote h -> h
        | Localhost -> failwith "syncFilesRemote called with local destination"

    let totalMaps = Set.count missingBeatmapIds
    let totalSkins = Set.count missingSkinHashes

    eprintfn ""
    eprintfn "  Copying realm database from %s..." config.FromLabel

    // Get a local copy of the source realm
    let localRealmResult =
        match config.FromHost with
        | Localhost -> Ok fromState.RealmPath
        | Remote _ -> Ssh.copyRealmToTemp (hostOption config.FromHost) fromState.RealmPath

    match localRealmResult with
    | Error e ->
        eprintfn "  Error: %s" e
        true
    | Ok localRealmPath ->
        let isTemp = localRealmPath <> fromState.RealmPath

        try
            try
                use sourceRealm = Realm.openRealmAt localRealmPath

                let beatmapSets =
                    if Set.isEmpty missingBeatmapIds then
                        []
                    else
                        eprintfn "  Reading %d beatmap set(s) from %s..." totalMaps config.FromLabel
                        Realm.readBeatmapSets sourceRealm missingBeatmapIds

                let skins =
                    if Set.isEmpty missingSkinHashes then
                        []
                    else
                        eprintfn "  Reading %d skin(s) from %s..." totalSkins config.FromLabel
                        Realm.readSkins sourceRealm missingSkinHashes

                let fileHashes = Realm.collectFileHashes beatmapSets skins

                if not (Set.isEmpty fileHashes) then
                    eprintfn "  Syncing %d file(s) to %s..." (Set.count fileHashes) config.ToLabel

                    match
                        Ssh.rsyncFiles
                            (hostOption config.FromHost)
                            (Some toHostname)
                            fromState.DataPath
                            toState.DataPath
                            fileHashes
                    with
                    | Error e ->
                        eprintfn "  Error syncing files: %s" e
                        true
                    | Ok() ->
                        // scp source realm to remote temp, run osync import there
                        let remoteTempRealm = $"/tmp/osync-import-{System.IO.Path.GetRandomFileName()}"

                        eprintfn "  Writing to realm on %s..." config.ToLabel

                        match Ssh.scpToRemote localRealmPath toHostname remoteTempRealm with
                        | Error e ->
                            eprintfn "  Error copying realm to %s: %s" config.ToLabel e
                            true
                        | Ok _ ->
                            let importCmd =
                                let osync = Ssh.shellEscape config.ToOsync

                                let dirArg =
                                    match config.ToDir with
                                    | Some d -> $" --dir {Ssh.shellEscape d}"
                                    | None -> ""

                                $"{osync} import --source-realm {Ssh.shellEscape remoteTempRealm}{dirArg}"

                            match Ssh.runRemote toHostname importCmd with
                            | Error e ->
                                eprintfn "  Error running import on %s: %s" config.ToLabel e
                                // clean up remote temp
                                Ssh.runRemote toHostname $"rm -f {Ssh.shellEscape remoteTempRealm}" |> ignore

                                true
                            | Ok output ->
                                if output.Length > 0 then
                                    eprintf "%s" output

                                // clean up remote temp
                                Ssh.runRemote toHostname $"rm -f {Ssh.shellEscape remoteTempRealm}" |> ignore

                                eprintfn
                                    "  Synced %d beatmap(s) and %d skin(s)."
                                    (List.length beatmapSets)
                                    (List.length skins)

                                false
                else
                    eprintfn "  No files to sync."
                    false
            with ex ->
                eprintfn "  Error during sync: %s" ex.Message
                true
        finally
            if isTemp && File.Exists(localRealmPath) then
                File.Delete(localRealmPath)

let private syncFiles
    (config: RunConfig)
    (fromState: MachineState)
    (toState: MachineState)
    (missingBeatmapIds: Set<int>)
    (missingSkinHashes: Set<string>)
    : bool =
    let totalMaps = Set.count missingBeatmapIds
    let totalSkins = Set.count missingSkinHashes

    if totalMaps = 0 && totalSkins = 0 then
        false
    else if
        not (
            promptYesNo
                $"  Sync %d{totalMaps} beatmap(s) and %d{totalSkins} skin(s) from %s{config.FromLabel} to %s{config.ToLabel}?"
        )
    then
        false
    else
        match config.ToHost with
        | Localhost -> syncFilesLocal config fromState toState missingBeatmapIds missingSkinHashes
        | Remote _ -> syncFilesRemote config fromState toState missingBeatmapIds missingSkinHashes

let run (config: RunConfig) : int =
    eprintfn "Extracting state from %s..." config.FromLabel
    let fromResult = extractState config.FromHost config.FromDir config.FromOsync

    eprintfn "Extracting state from %s..." config.ToLabel
    let toResult = extractState config.ToHost config.ToDir config.ToOsync

    match fromResult, toResult with
    | Error e, _ ->
        eprintfn "Error extracting from %s: %s" config.FromLabel e
        1
    | _, Error e ->
        eprintfn "Error extracting from %s: %s" config.ToLabel e
        1
    | Ok fromState, Ok toState ->
        let (missingOnFrom, missingOnTo) =
            printBeatmapDiffs fromState toState config.FromLabel config.ToLabel

        let (_skinsMissingOnFrom, skinsMissingOnTo) =
            printSkinDiffs fromState toState config.FromLabel config.ToLabel

        let mutable hadError = false
        let mutable from = fromState
        let mutable ``to`` = toState

        match config.Mode with
        | Sync ->
            let (err, f, t) = handleAutoDownload config from ``to`` missingOnFrom missingOnTo
            hadError <- err
            from <- f
            ``to`` <- t

            eprintfn ""

            if syncFiles config from ``to`` missingOnTo skinsMissingOnTo then
                hadError <- true
        | Diff -> ()

        if handleSettingsDiff config from ``to`` then
            hadError <- true

        eprintfn ""

        if hadError then
            eprintfn "Done with errors."
            1
        else
            eprintfn "Done."
            0

let runImport (sourceRealmPath: string) (dirOverride: string option) : int =
    try
        let destDataPath = dirOverride |> Option.defaultWith Realm.getOsuDataPath
        use sourceRealm = Realm.openRealmAt sourceRealmPath
        use destRealm = Realm.openRealm destDataPath

        let sourceIds =
            sourceRealm.DynamicApi.All("BeatmapSet")
            |> Seq.filter (fun obj ->
                not (obj.DynamicApi.Get<bool>("DeletePending"))
                && obj.DynamicApi.Get<int>("OnlineID") > 0)
            |> Seq.map (fun obj -> obj.DynamicApi.Get<int>("OnlineID"))
            |> Set.ofSeq

        let destIds =
            destRealm.DynamicApi.All("BeatmapSet")
            |> Seq.filter (fun obj ->
                not (obj.DynamicApi.Get<bool>("DeletePending"))
                && obj.DynamicApi.Get<int>("OnlineID") > 0)
            |> Seq.map (fun obj -> obj.DynamicApi.Get<int>("OnlineID"))
            |> Set.ofSeq

        let missingBeatmapIds = Set.difference sourceIds destIds

        let sourceSkinHashes =
            sourceRealm.DynamicApi.All("Skin")
            |> Seq.filter (fun obj ->
                not (obj.DynamicApi.Get<bool>("DeletePending"))
                && not (obj.DynamicApi.Get<bool>("Protected")))
            |> Seq.map (fun obj -> obj.DynamicApi.Get<string>("Hash"))
            |> Set.ofSeq

        let destSkinHashes =
            destRealm.DynamicApi.All("Skin")
            |> Seq.filter (fun obj ->
                not (obj.DynamicApi.Get<bool>("DeletePending"))
                && not (obj.DynamicApi.Get<bool>("Protected")))
            |> Seq.map (fun obj -> obj.DynamicApi.Get<string>("Hash"))
            |> Set.ofSeq

        let missingSkinHashes = Set.difference sourceSkinHashes destSkinHashes

        if Set.isEmpty missingBeatmapIds && Set.isEmpty missingSkinHashes then
            eprintfn "  Nothing to import."
            0
        else
            let beatmapSets =
                if Set.isEmpty missingBeatmapIds then
                    []
                else
                    Realm.readBeatmapSets sourceRealm missingBeatmapIds

            let skins =
                if Set.isEmpty missingSkinHashes then
                    []
                else
                    Realm.readSkins sourceRealm missingSkinHashes

            if not (List.isEmpty beatmapSets) then
                Realm.writeBeatmapSets destRealm beatmapSets

            if not (List.isEmpty skins) then
                Realm.writeSkins destRealm skins

            eprintfn "  Imported %d beatmap(s) and %d skin(s)." (List.length beatmapSets) (List.length skins)
            0
    with ex ->
        eprintfn "Error: %s" ex.Message
        1
