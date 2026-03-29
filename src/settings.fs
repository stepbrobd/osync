module Osync.Settings

let excludedKeys =
    Set.ofList
        [ "Token"
          "Version"
          "ReleaseStream"
          "LastProcessedMetadataId"
          "ShowFirstRunSetup"
          "ShowMobileDisclaimer"
          "AutomaticallyDownloadMissingBeatmaps" ]

type SettingsDiff =
    { Key: string
      LocalValue: string option
      RemoteValue: string option }

let parseLines (lines: string array) : Map<string, string> =
    lines
    |> Array.choose (fun line ->
        match line.IndexOf(" = ") with
        | -1 -> None
        | idx ->
            let key = line.Substring(0, idx)
            let value = line.Substring(idx + 3)
            Some(key, value))
    |> Map.ofArray

let diff (local: Map<string, string>) (remote: Map<string, string>) : SettingsDiff list =
    let allKeys =
        Seq.append (Map.keys local) (Map.keys remote)
        |> Seq.distinct
        |> Seq.filter (fun k -> not (Set.contains k excludedKeys))

    allKeys
    |> Seq.choose (fun key ->
        match Map.tryFind key local, Map.tryFind key remote with
        | Some lv, Some rv when lv <> rv ->
            Some
                { Key = key
                  LocalValue = Some lv
                  RemoteValue = Some rv }
        | Some lv, None ->
            Some
                { Key = key
                  LocalValue = Some lv
                  RemoteValue = None }
        | None, Some rv ->
            Some
                { Key = key
                  LocalValue = None
                  RemoteValue = Some rv }
        | _ -> None)
    |> Seq.toList

/// Patch raw file lines to match the desired settings map. Preserves non-setting
/// lines (blanks, comments). Updates changed values, removes deleted keys, and
/// appends new keys that don't exist in the original file.
let patchLines (lines: string array) (desired: Map<string, string>) : string array =
    let mutable remaining = desired
    let mutable result = ResizeArray<string>()

    for line in lines do
        match line.IndexOf(" = ") with
        | -1 -> result.Add(line)
        | idx ->
            let key = line.Substring(0, idx)

            match Map.tryFind key remaining with
            | Some newValue ->
                result.Add($"{key} = {newValue}")
                remaining <- Map.remove key remaining
            | None -> () // drop duplicate or removed key

    for kv in remaining do
        result.Add($"{kv.Key} = {kv.Value}")

    result.ToArray()

let applyChoice
    (local: Map<string, string>)
    (remote: Map<string, string>)
    (choices: (string * char) list)
    : Map<string, string> * Map<string, string> =
    choices
    |> List.fold
        (fun (l, r) (key, choice) ->
            match choice with
            | 'L' ->
                match Map.tryFind key l with
                | Some lv -> (l, Map.add key lv r)
                | None -> (Map.remove key l, Map.remove key r)
            | 'R' ->
                match Map.tryFind key r with
                | Some rv -> (Map.add key rv l, r)
                | None -> (Map.remove key l, Map.remove key r)
            | _ -> (l, r))
        (local, remote)
