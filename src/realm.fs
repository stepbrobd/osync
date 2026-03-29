module Osync.Realm

open System
open System.Runtime.InteropServices
open Realms

let getOsuDataPath () =
    if RuntimeInformation.IsOSPlatform(OSPlatform.OSX) then
        IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library",
            "Application Support",
            "osu"
        )
    else
        IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "osu")

let findRealmPath (dataPath: string) : string =
    let clientRealm = IO.Path.Combine(dataPath, "client.realm")

    let versioned =
        IO.Directory.GetFiles(dataPath, "client_*.realm")
        |> Array.sortDescending
        |> Array.tryHead

    versioned |> Option.defaultValue clientRealm

let private openRealm (dataPath: string) =
    let realmPath = findRealmPath dataPath
    let config = RealmConfiguration(realmPath)
    config.IsDynamic <- true
    Realm.GetInstance(config)

let readBeatmapSetIds (dataPath: string) : Set<int> =
    use realm = openRealm dataPath

    realm.DynamicApi.All("BeatmapSet")
    |> Seq.filter (fun obj ->
        not (obj.DynamicApi.Get<bool>("DeletePending"))
        && obj.DynamicApi.Get<int>("OnlineID") > 0)
    |> Seq.map (fun obj -> obj.DynamicApi.Get<int>("OnlineID"))
    |> Set.ofSeq

let readSkinIdentifiers (dataPath: string) : Map<string, string> =
    use realm = openRealm dataPath

    realm.DynamicApi.All("Skin")
    |> Seq.filter (fun obj ->
        not (obj.DynamicApi.Get<bool>("DeletePending"))
        && not (obj.DynamicApi.Get<bool>("Protected")))
    |> Seq.map (fun obj ->
        let hash = obj.DynamicApi.Get<string>("Hash")
        let name = obj.DynamicApi.Get<string>("Name")
        (hash, name))
    |> Map.ofSeq
