module Osync.Program

open Argu

[<CliPrefix(CliPrefix.DoubleDash)>]
type ExtractArgs =
    | [<AltCommandLine("-d")>] Dir of path: string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Dir _ -> "Override the osu! data directory path."

[<CliPrefix(CliPrefix.DoubleDash)>]
type SyncArgs =
    | [<AltCommandLine("-f")>] From of host: string
    | [<AltCommandLine("-t")>] To of host: string
    | From_Dir of path: string
    | To_Dir of path: string
    | From_Osync of path: string
    | To_Osync of path: string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | From _ -> "Source host (SSH hostname or omit for localhost)."
            | To _ -> "Destination host (SSH hostname or omit for localhost)."
            | From_Dir _ -> "Override the osu! data directory on the source side."
            | To_Dir _ -> "Override the osu! data directory on the destination side."
            | From_Osync _ -> "Path to osync binary on the source host."
            | To_Osync _ -> "Path to osync binary on the destination host."

[<CliPrefix(CliPrefix.DoubleDash)>]
type DiffArgs =
    | [<AltCommandLine("-b")>] Between of host: string
    | [<AltCommandLine("-a")>] And of host: string
    | Between_Dir of path: string
    | And_Dir of path: string
    | Between_Osync of path: string
    | And_Osync of path: string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Between _ -> "First host to compare (SSH hostname or omit for localhost)."
            | And _ -> "Second host to compare (SSH hostname or omit for localhost)."
            | Between_Dir _ -> "Override the osu! data directory for the first host."
            | And_Dir _ -> "Override the osu! data directory for the second host."
            | Between_Osync _ -> "Path to osync binary on the first host."
            | And_Osync _ -> "Path to osync binary on the second host."

let version =
    System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
    |> fun v -> $"{v.Major}.{v.Minor}.{v.Build}"

type OsyncArgs =
    | [<CliPrefix(CliPrefix.None)>] Sync of ParseResults<SyncArgs>
    | [<CliPrefix(CliPrefix.None)>] Diff of ParseResults<DiffArgs>
    | [<CliPrefix(CliPrefix.None)>] Extract of ParseResults<ExtractArgs>
    | [<CliPrefix(CliPrefix.DoubleDash)>] Version

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Sync _ -> "Synchronize beatmaps and settings between two hosts."
            | Diff _ -> "Show differences between two hosts without making changes."
            | Extract _ -> "Extract local state as JSON (used internally over SSH)."
            | Version -> "Show version."

let private runExtract (results: ParseResults<ExtractArgs>) : int =
    let dirOverride = results.TryGetResult ExtractArgs.Dir

    match Sync.extractLocal dirOverride with
    | Ok state ->
        let json = Sync.serializeExtractJson state
        printfn "%s" json
        0
    | Error e ->
        eprintfn "Error: %s" e
        1

let private runSync (results: ParseResults<SyncArgs>) : int =
    let fromHost = results.TryGetResult SyncArgs.From
    let toHost = results.TryGetResult SyncArgs.To

    let fromDir = results.TryGetResult SyncArgs.From_Dir
    let toDir = results.TryGetResult SyncArgs.To_Dir

    if fromHost.IsNone && toHost.IsNone && fromDir.IsNone && toDir.IsNone then
        eprintfn "Error: At least one of --from or --to must be provided."
        1
    else
        let fromResolved =
            match fromHost with
            | Some h -> Sync.Remote h
            | None -> Sync.Localhost

        let toResolved =
            match toHost with
            | Some h -> Sync.Remote h
            | None -> Sync.Localhost

        let fromLabel =
            match fromHost with
            | Some h -> h
            | None -> "localhost"

        let toLabel =
            match toHost with
            | Some h -> h
            | None -> "localhost"

        Sync.run
            { Mode = Sync.Sync
              FromHost = fromResolved
              ToHost = toResolved
              FromDir = fromDir
              ToDir = toDir
              FromOsync = results.GetResult(SyncArgs.From_Osync, "osync")
              ToOsync = results.GetResult(SyncArgs.To_Osync, "osync")
              FromLabel = fromLabel
              ToLabel = toLabel }

let private runDiff (results: ParseResults<DiffArgs>) : int =
    let betweenHost = results.TryGetResult DiffArgs.Between
    let andHost = results.TryGetResult DiffArgs.And

    let betweenDir = results.TryGetResult DiffArgs.Between_Dir
    let andDir = results.TryGetResult DiffArgs.And_Dir

    if betweenHost.IsNone && andHost.IsNone && betweenDir.IsNone && andDir.IsNone then
        eprintfn "Error: At least one of --between or --and must be provided."
        1
    else
        let fromResolved =
            match betweenHost with
            | Some h -> Sync.Remote h
            | None -> Sync.Localhost

        let toResolved =
            match andHost with
            | Some h -> Sync.Remote h
            | None -> Sync.Localhost

        let fromLabel =
            match betweenHost with
            | Some h -> h
            | None -> "localhost"

        let toLabel =
            match andHost with
            | Some h -> h
            | None -> "localhost"

        Sync.run
            { Mode = Sync.Diff
              FromHost = fromResolved
              ToHost = toResolved
              FromDir = betweenDir
              ToDir = andDir
              FromOsync = results.GetResult(DiffArgs.Between_Osync, "osync")
              ToOsync = results.GetResult(DiffArgs.And_Osync, "osync")
              FromLabel = fromLabel
              ToLabel = toLabel }

[<EntryPoint>]
let main argv =
    let parser = ArgumentParser.Create<OsyncArgs>(programName = "osync")

    try
        let results = parser.ParseCommandLine(inputs = argv, raiseOnUsage = true)

        if results.Contains OsyncArgs.Version then
            printfn "osync %s" version
            0
        else
            match results.GetSubCommand() with
            | Sync syncResults -> runSync syncResults
            | Diff diffResults -> runDiff diffResults
            | Extract extractResults -> runExtract extractResults
            | Version -> 0
    with
    | :? ArguParseException as ex ->
        eprintfn "%s" ex.Message
        if ex.ErrorCode = ErrorCode.HelpText then 0 else 1
    | ex ->
        eprintfn "Error: %s" ex.Message
        1
