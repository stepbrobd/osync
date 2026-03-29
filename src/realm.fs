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

let openRealm (dataPath: string) =
    let realmPath = findRealmPath dataPath
    let config = RealmConfiguration(realmPath)
    config.IsDynamic <- true
    Realm.GetInstance(config)

let openRealmAt (path: string) =
    let config = RealmConfiguration(path)
    config.IsDynamic <- true
    Realm.GetInstance(config)

// --- Lightweight reads (IDs only) ---

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

// --- Intermediate types for full data transfer ---

type FileRef = { Hash: string; Filename: string }

type UserData =
    { OnlineID: int
      Username: string
      CountryCode: string }

type MetadataData =
    { Title: string
      TitleUnicode: string
      Artist: string
      ArtistUnicode: string
      Source: string
      Tags: string
      PreviewTime: int
      AudioFile: string
      BackgroundFile: string
      Author: UserData }

type DifficultyData =
    { DrainRate: float32
      CircleSize: float32
      OverallDifficulty: float32
      ApproachRate: float32
      SliderMultiplier: float
      SliderTickRate: float }

type BeatmapData =
    { DifficultyName: string
      RulesetShortName: string
      Status: int
      OnlineID: int
      Length: float
      BPM: float
      Hash: string
      StarRating: float
      MD5Hash: string
      OnlineMD5Hash: string
      Hidden: bool
      EndTimeObjectCount: int
      TotalObjectCount: int
      BeatDivisor: int
      EditorTimestamp: Nullable<float>
      LastLocalUpdate: Nullable<DateTimeOffset>
      LastOnlineUpdate: Nullable<DateTimeOffset>
      LastPlayed: Nullable<DateTimeOffset>
      Metadata: MetadataData
      Difficulty: DifficultyData
      Offset: float }

type BeatmapSetData =
    { OnlineID: int
      DateAdded: DateTimeOffset
      DateSubmitted: Nullable<DateTimeOffset>
      DateRanked: Nullable<DateTimeOffset>
      Status: int
      Hash: string
      Files: FileRef list
      Beatmaps: BeatmapData list }

type SkinData =
    { Name: string
      Creator: string
      InstantiationInfo: string
      Hash: string
      Files: FileRef list }

// --- Full reads ---

let private readFileRefs (obj: IRealmObjectBase) : FileRef list =
    obj.DynamicApi.GetList<IEmbeddedObject>("Files")
    |> Seq.map (fun f ->
        let file = f.DynamicApi.Get<IRealmObject>("File")

        { Hash = file.DynamicApi.Get<string>("Hash")
          Filename = f.DynamicApi.Get<string>("Filename") })
    |> Seq.toList

let private readUser (obj: IEmbeddedObject) : UserData =
    { OnlineID = obj.DynamicApi.Get<int>("OnlineID")
      Username = obj.DynamicApi.Get<string>("Username")
      CountryCode = obj.DynamicApi.Get<string>("CountryCode") }

let private readMetadata (obj: IEmbeddedObject) : MetadataData =
    { Title = obj.DynamicApi.Get<string>("Title")
      TitleUnicode = obj.DynamicApi.Get<string>("TitleUnicode")
      Artist = obj.DynamicApi.Get<string>("Artist")
      ArtistUnicode = obj.DynamicApi.Get<string>("ArtistUnicode")
      Source = obj.DynamicApi.Get<string>("Source")
      Tags = obj.DynamicApi.Get<string>("Tags")
      PreviewTime = obj.DynamicApi.Get<int>("PreviewTime")
      AudioFile = obj.DynamicApi.Get<string>("AudioFile")
      BackgroundFile = obj.DynamicApi.Get<string>("BackgroundFile")
      Author = readUser (obj.DynamicApi.Get<IEmbeddedObject>("Author")) }

let private readDifficulty (obj: IEmbeddedObject) : DifficultyData =
    { DrainRate = obj.DynamicApi.Get<float32>("DrainRate")
      CircleSize = obj.DynamicApi.Get<float32>("CircleSize")
      OverallDifficulty = obj.DynamicApi.Get<float32>("OverallDifficulty")
      ApproachRate = obj.DynamicApi.Get<float32>("ApproachRate")
      SliderMultiplier = obj.DynamicApi.Get<float>("SliderMultiplier")
      SliderTickRate = obj.DynamicApi.Get<float>("SliderTickRate") }

let private readBeatmap (obj: IRealmObject) : BeatmapData =
    let ruleset = obj.DynamicApi.Get<IRealmObject>("Ruleset")

    { DifficultyName = obj.DynamicApi.Get<string>("DifficultyName")
      RulesetShortName = ruleset.DynamicApi.Get<string>("ShortName")
      Status = obj.DynamicApi.Get<int>("Status")
      OnlineID = obj.DynamicApi.Get<int>("OnlineID")
      Length = obj.DynamicApi.Get<float>("Length")
      BPM = obj.DynamicApi.Get<float>("BPM")
      Hash = obj.DynamicApi.Get<string>("Hash")
      StarRating = obj.DynamicApi.Get<float>("StarRating")
      MD5Hash = obj.DynamicApi.Get<string>("MD5Hash")
      OnlineMD5Hash = obj.DynamicApi.Get<string>("OnlineMD5Hash")
      Hidden = obj.DynamicApi.Get<bool>("Hidden")
      EndTimeObjectCount = obj.DynamicApi.Get<int>("EndTimeObjectCount")
      TotalObjectCount = obj.DynamicApi.Get<int>("TotalObjectCount")
      BeatDivisor = obj.DynamicApi.Get<int>("BeatDivisor")
      EditorTimestamp = obj.DynamicApi.Get<Nullable<float>>("EditorTimestamp")
      LastLocalUpdate = obj.DynamicApi.Get<Nullable<DateTimeOffset>>("LastLocalUpdate")
      LastOnlineUpdate = obj.DynamicApi.Get<Nullable<DateTimeOffset>>("LastOnlineUpdate")
      LastPlayed = obj.DynamicApi.Get<Nullable<DateTimeOffset>>("LastPlayed")
      Metadata = readMetadata (obj.DynamicApi.Get<IEmbeddedObject>("Metadata"))
      Difficulty = readDifficulty (obj.DynamicApi.Get<IEmbeddedObject>("Difficulty"))
      Offset =
        let us = obj.DynamicApi.Get<IEmbeddedObject>("UserSettings")
        us.DynamicApi.Get<float>("Offset") }

let readBeatmapSets (realm: Realm) (onlineIds: Set<int>) : BeatmapSetData list =
    realm.DynamicApi.All("BeatmapSet")
    |> Seq.filter (fun obj ->
        not (obj.DynamicApi.Get<bool>("DeletePending"))
        && Set.contains (obj.DynamicApi.Get<int>("OnlineID")) onlineIds)
    |> Seq.map (fun obj ->
        { OnlineID = obj.DynamicApi.Get<int>("OnlineID")
          DateAdded = obj.DynamicApi.Get<DateTimeOffset>("DateAdded")
          DateSubmitted = obj.DynamicApi.Get<Nullable<DateTimeOffset>>("DateSubmitted")
          DateRanked = obj.DynamicApi.Get<Nullable<DateTimeOffset>>("DateRanked")
          Status = obj.DynamicApi.Get<int>("Status")
          Hash = obj.DynamicApi.Get<string>("Hash")
          Files = readFileRefs obj
          Beatmaps =
            obj.DynamicApi.GetList<IRealmObject>("Beatmaps")
            |> Seq.map readBeatmap
            |> Seq.toList })
    |> Seq.toList

let readSkins (realm: Realm) (hashes: Set<string>) : SkinData list =
    realm.DynamicApi.All("Skin")
    |> Seq.filter (fun obj ->
        not (obj.DynamicApi.Get<bool>("DeletePending"))
        && not (obj.DynamicApi.Get<bool>("Protected"))
        && Set.contains (obj.DynamicApi.Get<string>("Hash")) hashes)
    |> Seq.map (fun obj ->
        { Name = obj.DynamicApi.Get<string>("Name")
          Creator = obj.DynamicApi.Get<string>("Creator")
          InstantiationInfo = obj.DynamicApi.Get<string>("InstantiationInfo")
          Hash = obj.DynamicApi.Get<string>("Hash")
          Files = readFileRefs obj })
    |> Seq.toList
