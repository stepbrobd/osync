# osync!

Sync osu! lazer state between machines. It reads the realm database and
`game.ini`, shows differences, and transfers what is missing.

What it does:

- compare beatmap set ids between two hosts
- compare skins between two hosts
- compare scores (play history) between two hosts
- sync missing beatmaps by copying realm entries and hashed files
- sync missing skins and scores the same way
- interactively diff and sync selected `game.ini` keys
- preserve comments and blank lines when patching `game.ini`
- use SSH for remote extraction, rsync for file transfer

The tool reads 2 files:

**`client.realm`**: it opens the realm database dynamically, walks the
`BeatmapSet` objects, filters out entries with `DeletePending = true`, and keeps
only positive `OnlineID` values. It also reads `Skin` objects, filtering out
deleted and built-in (protected) skins, and `Score` objects for play history.
During sync, it reads full object data from the source realm and writes missing
entries to the destination realm.

osu! uses versioned realm files (`client_{N}.realm`) in debug builds and plain
`client.realm` in release builds. osync checks for both, preferring the
versioned file if it exists.

**`game.ini`**: it parses setting lines by looking for `=` (with surrounding
spaces). Lines without that pattern are not treated as settings. Blank lines,
comments, and other non-setting lines are carried through untouched when the
file is later patched.

The extracted JSON includes:

- `beatmapSetIds`
- `skinIdentifiers`
- `scoreHashes`
- `settings`
- `rawSettingsLines`
- `autoDownload`
- `dataPath`
- `realmPath`
- `settingsPath`

`rawSettingsLines` exists so the patching pass can preserve file structure
instead of regenerating `game.ini` from a map and throwing everything else away.

Beatmap and skin sync works by copying the source `client.realm` to a temp file,
reading full object data for missing items, rsyncing the hashed files, and
writing the realm entries on the destination. osu! stores files using SHA-256
hashes in a nested directory structure (`files/{hash[0]}/{hash[0:2]}/{hash}`),
so rsync only transfers files the destination does not already have.

When the destination is remote, the source realm is copied to a temp file on the
remote host, and `osync import --source-realm <path>` is run over SSH to write
the entries there. This mirrors the existing `extract` pattern: `extract` reads
state over SSH, `import` writes state over SSH.

Beatmaps are identified by `OnlineID`. Skins are identified by content hash and
displayed by name. Built-in skins (argon, classic, etc.) are skipped. Scores are
identified by their content hash and linked to the local beatmap by
`BeatmapHash` if the matching beatmap exists on the destination.

The following realm data is intentionally not synced:

- **collections**: beatmap groupings are identified by name and contain MD5 hash
  lists. Easy to add if needed.
- **mod presets**: named mod combinations with a ruleset reference and JSON mods
  string. Easy to add if needed.
- **key bindings**: per-machine by nature. Syncing them would overwrite
  intentional per-device customizations.
- **ruleset settings**: per-ruleset config values. Already covered by `game.ini`
  sync where applicable.

The sync is one-directional: from `--from` to `--to`. Run it in reverse for the
other direction. osu! must not be running on the destination when syncing,
because realm locks the database file.

Settings sync is interactive. For each difference you get:

- `F` to take the `from` side
- `T` to take the `to` side
- `S` to skip the key

If a key only exists on one side, the missing side is shown as `(missing)`.
Choosing the missing side deletes the key from the other file.

These keys are intentionally excluded from the normal settings diff:

- `Token`
- `Version`
- `ReleaseStream`
- `LastProcessedMetadataId`
- `ShowFirstRunSetup`
- `ShowMobileDisclaimer`
- `AutomaticallyDownloadMissingBeatmaps`
- `Skin`

`AutomaticallyDownloadMissingBeatmaps` is managed separately by the dedicated
auto-download prompt. `Skin` references a realm object by GUID, which differs
between machines even when the skin content is the same. Neither is shown in the
normal settings diff.

When osync writes `game.ini`, it patches in place:

- changed keys are updated where they already exist
- deleted keys are removed
- duplicate keys are collapsed to a single effective entry
- new keys are appended at the end
- non-setting lines are preserved

Automatic path detection:

- macOS: `~/Library/Application Support/osu`
- Linux: `~/.local/share/osu`

That is what `getOsuDataPath` implements. If your lazer data lives somewhere
else, pass `--dir` / `--from-dir` / `--to-dir`.

This project does not currently try to support Windows path discovery.

Remote operations go through plain `ssh`. File transfer uses `rsync` with
`--files-from` to only copy the specific hashed files that are missing. The
remote host is expected to have an `osync` binary available in `PATH`, unless
you override it with `--from-osync`, `--to-osync`, `--between-osync`, or
`--and-osync`. The value is interpolated into the SSH command string as-is, so
you can pass a multi-word command like `nix run github:user/osync --`. If the
path contains spaces, quote it for the remote shell:
`--from-osync "'/path with spaces/osync'"`.

The remote protocol uses `osync extract` over SSH for reading state and
`osync import` over SSH for writing beatmaps, skins, and scores. Neither is
versioned. In practice you should run the same osync build on both sides.

Help:

```bash
osync --help
osync sync --help
osync diff --help
```

Compare local machine with a remote host:

```bash
osync diff --and desktop
```

Sync from a remote host to local machine:

```bash
osync sync --from macbook
```

Sync local machine to a remote host:

```bash
osync sync --to desktop
```

Sync two explicit local directories:

```bash
osync sync --from-dir /tmp/osu-a --to-dir /tmp/osu-b
```

Use a non-default remote osync command:

```bash
osync sync --from laptop \
  --from-osync /home/user/.local/bin/osync
```

If both machines have nix installed, you can run osync without installing
anything:

```bash
nix run github:stepbrobd/osync -- sync --from macbook \
  --from-osync 'nix run github:stepbrobd/osync --'
```

Use `--refresh` to pull the latest version:

```bash
nix run --refresh github:stepbrobd/osync -- sync --from macbook \
  --from-osync 'nix run --refresh github:stepbrobd/osync --'
```

Dump the local extracted state as JSON:

```bash
osync extract
osync extract --dir /some/other/osu/path
```

`extract` exists mostly for the SSH path, but it is also useful for debugging.

Import missing beatmaps, skins, and scores from a source realm file:

```bash
osync import --source-realm /tmp/source.realm
osync import --source-realm /tmp/source.realm --dir /some/other/osu/path
```

`import` is used internally by the remote destination sync path. It reads
missing items from the source realm and writes them to the local realm. You
generally do not need to call this directly.

Sample output (note that I have setup direct ssh access between these 2
machines):

```console
$ nix run --refresh github:stepbrobd/osync -- sync --from framework --from-osync 'nix run --refresh github:stepbrobd/osync --'
Extracting state from framework...
Extracting state from localhost...

=== Beatmap Differences ===

  Missing on localhost (796 maps):
    https://osu.ppy.sh/beatmapsets/12563
    https://osu.ppy.sh/beatmapsets/15211
    https://osu.ppy.sh/beatmapsets/18775
    https://osu.ppy.sh/beatmapsets/21872
    https://osu.ppy.sh/beatmapsets/36333
    https://osu.ppy.sh/beatmapsets/38629
    https://osu.ppy.sh/beatmapsets/43701
    https://osu.ppy.sh/beatmapsets/45720
    https://osu.ppy.sh/beatmapsets/48076
    https://osu.ppy.sh/beatmapsets/52506
    https://osu.ppy.sh/beatmapsets/54707
    https://osu.ppy.sh/beatmapsets/54782
    https://osu.ppy.sh/beatmapsets/55699
    https://osu.ppy.sh/beatmapsets/56404
    https://osu.ppy.sh/beatmapsets/57368
    https://osu.ppy.sh/beatmapsets/63089
    https://osu.ppy.sh/beatmapsets/73844
    https://osu.ppy.sh/beatmapsets/80659
    https://osu.ppy.sh/beatmapsets/104541
    https://osu.ppy.sh/beatmapsets/107796
    ... and 776 more

=== Skin Differences ===

  Missing on localhost (1 skins):
    osu! "argon" pro (2022) (modified)

=== Score Differences ===
  Missing on localhost: 4326 score(s)

  Sync 796 beatmap(s), 1 skin(s), and 4326 score(s) from framework to localhost? [y/N]
y

  Copying realm database from framework...
client.realm                                                    100%   10MB  96.0MB/s   00:00
  Reading missing data from framework...
  Syncing 18821 file(s)...
  9,227,345,310 100%  103.39MB/s    0:01:25 (xfr#18821, to-chk=0/19093)
  Writing to realm on localhost...
  Synced 796 beatmap(s), 1 skin(s), and 4326 score(s).

=== Settings Differences ===
  Found 21 setting(s) that differ.

  AutomaticallyAdjustBeatmapOffset: framework=True, localhost=False
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  BeatmapDetailModsFilter: framework=True, localhost=False
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  BeatmapListingFeaturedArtistFilter: framework=False, localhost=True
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  DimLevel: framework=1.0, localhost=0.7
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  ExternalLinkWarning: framework=False, localhost=True
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  IntroSequence: framework=Random, localhost=Triangles
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  LastOnlineTagsPopulation: framework=02/03/2026 00:00:00, localhost=
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  MenuBackgroundSource: framework=BeatmapWithStoryboard, localhost=Skin
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  MouseDisableButtons: framework=True, localhost=False
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  MouseDisableWheel: framework=True, localhost=False
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  MultiplayerRoomFilter: framework=Public, localhost=All
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  Prefer24HourTime: framework=True, localhost=False
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  Ruleset: framework=osu, localhost=
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  ShowConvertedBeatmaps: framework=False, localhost=True
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  ShowFpsDisplay: framework=True, localhost=False
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  ShowOnlineExplicitContent: framework=True, localhost=False
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  Skin: framework=62d19f56-6215-4ee3-8ad4-19bfa9aeebfb, localhost=cffa69de-b3e3-4dee-8563-3c4f425c05d0
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > s

  SongSelectSortingMode: framework=DateAdded, localhost=Title
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  TouchDisableGameplayTaps: framework=True, localhost=False
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  Username: framework=stepbrobd, localhost=
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  WasSupporter: framework=True, localhost=False
  [F]rom (framework) / [T]o (localhost) / [S]kip?
  > f

  Writing updated settings to localhost...
  Done.

Done.
```
