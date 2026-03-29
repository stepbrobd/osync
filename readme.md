# osync!

This tool does not try to be a backup system, it does not try to transfer
beatmap files, and it does not try to be clever about conflict resolution. It
reads the parts of lazer state that actually matter (what I think) across
machines, shows the differences, and patches the local `game.ini` files you
explicitly choose to converge.

This is meant to be a very small tool:

- compare beatmap set ids between two hosts
- show missing beatmap set URLs
- interactively diff and sync selected `game.ini` keys
- preserve comments and blank lines when patching `game.ini`
- use SSH for remote extraction and remote writes
- expose a small `extract` command used internally over SSH

The tool read 2 files:

**`client.realm`**: it opens the realm database dynamically, walks the
`BeatmapSet` objects, filters out entries with `DeletePending = true`, and keeps
only positive `OnlineID` values. The result is a set of beatmap set ids.

**`game.ini`**: it parses setting lines by splitting on the first `=` only.
Malformed lines are ignored. Blank lines, comments, and other non-setting lines
are carried through untouched when the file is later patched.

The extracted JSON includes:

- `beatmapSetIds`
- `settings`
- `rawSettingsLines`
- `autoDownload`
- `dataPath`
- `settingsPath`

`rawSettingsLines` exists so the patching pass can preserve file structure
instead of regenerating `game.ini` from a map and throwing everything else away.

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

Remote operations go through plain `ssh`. The remote host is expected to have an
`osync` binary available in `PATH`, unless you override it with `--from-osync`,
`--to-osync`, `--between-osync`, or `--and-osync`.

The remote protocol is just `osync extract` over SSH with JSON on stdout. It is
not versioned. In practice you should run the same osync build on both sides.

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

Sync local machine to a remote host:

```bash
osync sync --to desktop
```

Compare two remote hosts:

```bash
osync diff --between laptop --and desktop
```

Sync two explicit local directories:

```bash
osync sync --from-dir /tmp/osu-a --to-dir /tmp/osu-b
```

Use a non-default remote binary path:

```bash
osync sync --from laptop --to desktop \
  --from-osync /home/user/.local/bin/osync \
  --to-osync /home/user/.local/bin/osync
```

Dump the local extracted state as JSON:

```bash
osync extract
osync extract --dir /some/other/osu/path
```

`extract` exists mostly for the SSH path, but it is also useful for debugging.
