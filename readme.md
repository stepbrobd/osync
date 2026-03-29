# osync!

Sync osu! lazer state between machines. It reads the realm database and
`game.ini`, shows differences, and transfers what is missing.

What it does:

- compare beatmap set ids between two hosts
- compare skins between two hosts
- sync missing beatmaps by copying realm entries and hashed files
- sync missing skins the same way
- interactively diff and sync selected `game.ini` keys
- preserve comments and blank lines when patching `game.ini`
- use SSH for remote extraction, rsync for file transfer

The tool reads 2 files:

**`client.realm`**: it opens the realm database dynamically, walks the
`BeatmapSet` objects, filters out entries with `DeletePending = true`, and keeps
only positive `OnlineID` values. It also reads `Skin` objects, filtering out
deleted and built-in (protected) skins. During sync, it reads full object data
from the source realm and writes missing entries to the destination realm.

osu! uses versioned realm files (`client_{N}.realm`) in debug builds and plain
`client.realm` in release builds. osync checks for both, preferring the
versioned file if it exists.

**`game.ini`**: it parses setting lines by looking for ` = ` (with spaces). Lines
without that pattern are not treated as settings. Blank lines, comments, and
other non-setting lines are carried through untouched when the file is later
patched.

The extracted JSON includes:

- `beatmapSetIds`
- `skinIdentifiers`
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
displayed by name. Built-in skins (argon, classic, etc.) are skipped.

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

The last key is managed separately by the dedicated auto-download prompt. It is
not shown again in the normal settings diff, because that just creates a second
prompt for the same decision and makes it easy to undo yourself.

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
`osync import` over SSH for writing beatmaps and skins. Neither is versioned. In
practice you should run the same osync build on both sides.

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

Use a nix flake as the remote osync:

```bash
osync sync --from laptop \
  --from-osync 'nix run github:user/osync --'
```

Dump the local extracted state as JSON:

```bash
osync extract
osync extract --dir /some/other/osu/path
```

`extract` exists mostly for the SSH path, but it is also useful for debugging.

Import missing beatmaps and skins from a source realm file:

```bash
osync import --source-realm /tmp/source.realm
osync import --source-realm /tmp/source.realm --dir /some/other/osu/path
```

`import` is used internally by the remote destination sync path. It reads
missing items from the source realm and writes them to the local realm. You
generally do not need to call this directly.
