module Osync.Settings

open System.IO

let excludedKeys =
    Set.ofList
        [ "Token"
          "Version"
          "ReleaseStream"
          "LastProcessedMetadataId"
          "ShowFirstRunSetup"
          "ShowMobileDisclaimer" ]

type SettingsDiff =
    { Key: string
      LocalValue: string
      RemoteValue: string }

let parse (path: string) : Map<string, string> =
    File.ReadAllLines(path)
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
                  LocalValue = lv
                  RemoteValue = rv }
        | _ -> None)
    |> Seq.toList

let serialize (settings: Map<string, string>) : string =
    settings |> Map.fold (fun acc key value -> acc + $"{key} = {value}\n") ""

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
                | None -> (l, r)
            | 'R' ->
                match Map.tryFind key r with
                | Some rv -> (Map.add key rv l, r)
                | None -> (l, r)
            | _ -> (l, r))
        (local, remote)
