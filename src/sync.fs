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

        Ok
            { BeatmapSetIds = beatmapSetIds
              SkinIdentifiers = skinIdentifiers
              Settings = settings
              RawSettingsLines = rawLines
              AutoDownload = autoDownload
              DataPath = dataPath
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
        let settingsPath = root.GetProperty("settingsPath").GetString()

        Ok
            { BeatmapSetIds = beatmapSetIds
              SkinIdentifiers = skinIdentifiers
              Settings = settings
              RawSettingsLines = rawSettingsLines
              AutoDownload = autoDownload
              DataPath = dataPath
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

        let mutable hadError = false
        let mutable from = fromState
        let mutable ``to`` = toState

        match config.Mode with
        | Sync ->
            let (err, f, t) = handleAutoDownload config from ``to`` missingOnFrom missingOnTo
            hadError <- err
            from <- f
            ``to`` <- t
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
