module Osync.Sync

open System
open System.IO
open System.Text.Json

type MachineState =
    { BeatmapSetIds: Set<int>
      Settings: Map<string, string>
      AutoDownload: bool
      DataPath: string
      SettingsPath: string }

let extractLocal (dirOverride: string option) : Result<MachineState, string> =
    try
        let dataPath = dirOverride |> Option.defaultWith Realm.getOsuDataPath

        let settingsPath = Path.Combine(dataPath, "game.ini")

        let beatmapSetIds = Realm.readBeatmapSetIds dataPath

        let settings =
            if File.Exists(settingsPath) then
                Settings.parse settingsPath
            else
                Map.empty

        let autoDownload =
            settings
            |> Map.tryFind "AutomaticallyDownloadMissingBeatmaps"
            |> Option.map (fun v -> v.ToLowerInvariant() = "true" || v = "1")
            |> Option.defaultValue false

        Ok
            { BeatmapSetIds = beatmapSetIds
              Settings = settings
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

    writer.WritePropertyName("settings")
    writer.WriteStartObject()

    for kv in state.Settings do
        writer.WriteString(kv.Key, kv.Value)

    writer.WriteEndObject()

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

        let settings =
            root.GetProperty("settings").EnumerateObject()
            |> Seq.map (fun p -> p.Name, p.Value.GetString())
            |> Map.ofSeq

        let autoDownload = root.GetProperty("autoDownload").GetBoolean()
        let dataPath = root.GetProperty("dataPath").GetString()
        let settingsPath = root.GetProperty("settingsPath").GetString()

        Ok
            { BeatmapSetIds = beatmapSetIds
              Settings = settings
              AutoDownload = autoDownload
              DataPath = dataPath
              SettingsPath = settingsPath }
    with ex ->
        Error $"Failed to parse extract JSON: {ex.Message}"

type ResolvedHost =
    | Localhost
    | Remote of string

let resolveHost (host: string option) : ResolvedHost =
    match host with
    | None -> Localhost
    | Some h -> Remote h

let extractState (host: ResolvedHost) (dirOverride: string option) (osyncPath: string) : Result<MachineState, string> =
    match host with
    | Localhost -> extractLocal dirOverride
    | Remote hostname ->
        let cmd =
            match dirOverride with
            | Some d -> $"{osyncPath} extract --dir '{d}'"
            | None -> $"{osyncPath} extract"

        Ssh.runRemote hostname cmd |> Result.bind parseExtractJson

let private mapUrl (id: int) = $"https://osu.ppy.sh/beatmapsets/{id}"

let private promptYesNo (message: string) : bool =
    eprintfn "%s [y/N] " message
    let input = Console.ReadLine()

    match input with
    | null -> false
    | s -> s.Trim().ToLowerInvariant() = "y"

let private promptSettingChoice (diff: Settings.SettingsDiff) (fromLabel: string) (toLabel: string) : char =
    eprintfn ""
    eprintfn "  %s: %s=%s, %s=%s" diff.Key fromLabel diff.LocalValue toLabel diff.RemoteValue
    eprintfn "  [F]rom (%s) / [T]o (%s) / [S]kip?" fromLabel toLabel

    let rec loop () =
        eprintf "  > "
        let input = Console.ReadLine()

        match input with
        | null -> 'S'
        | s ->
            match s.Trim().ToUpperInvariant() with
            | "F" -> 'L' // 'L' = local/from side in applyChoice
            | "T" -> 'R' // 'R' = remote/to side in applyChoice
            | "S" -> 'S'
            | _ ->
                eprintfn "  Invalid choice. Enter F, T, or S."
                loop ()

    loop ()

let private writeSettings (host: ResolvedHost) (path: string) (settings: Map<string, string>) : Result<unit, string> =
    let content = Settings.serialize settings

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
    writeSettings host state.SettingsPath updated

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

let private handleAutoDownload
    (config: RunConfig)
    (fromState: MachineState)
    (toState: MachineState)
    (missingOnFrom: Set<int>)
    (missingOnTo: Set<int>)
    =
    if not (Set.isEmpty missingOnTo) && not toState.AutoDownload then
        eprintfn ""

        if promptYesNo $"  AutomaticallyDownloadMissingBeatmaps is disabled on {config.ToLabel}. Enable it?" then
            match enableAutoDownload config.ToHost toState with
            | Ok() -> eprintfn "  Enabled auto-download on %s." config.ToLabel
            | Error e -> eprintfn "  Error enabling auto-download on %s: %s" config.ToLabel e

    if not (Set.isEmpty missingOnFrom) && not fromState.AutoDownload then
        eprintfn ""

        if promptYesNo $"  AutomaticallyDownloadMissingBeatmaps is disabled on {config.FromLabel}. Enable it?" then
            match enableAutoDownload config.FromHost fromState with
            | Ok() -> eprintfn "  Enabled auto-download on %s." config.FromLabel
            | Error e -> eprintfn "  Error enabling auto-download on %s: %s" config.FromLabel e

let private handleSettingsDiff (config: RunConfig) (fromState: MachineState) (toState: MachineState) =
    let diffs = Settings.diff fromState.Settings toState.Settings

    eprintfn ""
    eprintfn "=== Settings Differences ==="

    if List.isEmpty diffs then
        eprintfn "  No settings differences found."
    else
        eprintfn "  Found %d setting(s) that differ." (List.length diffs)

        match config.Mode with
        | Diff ->
            for d in diffs do
                eprintfn "  %s: %s=%s, %s=%s" d.Key config.FromLabel d.LocalValue config.ToLabel d.RemoteValue
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

                    match writeSettings config.FromHost fromState.SettingsPath updatedFrom with
                    | Ok() -> eprintfn "  Done."
                    | Error e -> eprintfn "  Error: %s" e

                if updatedTo <> toState.Settings then
                    eprintfn ""
                    eprintfn "  Writing updated settings to %s..." config.ToLabel

                    match writeSettings config.ToHost toState.SettingsPath updatedTo with
                    | Ok() -> eprintfn "  Done."
                    | Error e -> eprintfn "  Error: %s" e

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

        match config.Mode with
        | Sync -> handleAutoDownload config fromState toState missingOnFrom missingOnTo
        | Diff -> ()

        handleSettingsDiff config fromState toState

        eprintfn ""
        eprintfn "Done."
        0
