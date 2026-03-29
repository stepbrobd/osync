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

let readBeatmapSetIds (dataPath: string) : Set<int> =
    let realmPath = IO.Path.Combine(dataPath, "client.realm")
    let config = RealmConfiguration(realmPath)
    config.IsDynamic <- true

    use realm = Realm.GetInstance(config)

    realm.DynamicApi.All("BeatmapSet")
    |> Seq.filter (fun obj ->
        not (obj.DynamicApi.Get<bool>("DeletePending"))
        && obj.DynamicApi.Get<int>("OnlineID") > 0)
    |> Seq.map (fun obj -> obj.DynamicApi.Get<int>("OnlineID"))
    |> Set.ofSeq
