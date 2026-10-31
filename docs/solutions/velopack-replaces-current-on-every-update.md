---
tags: [storage, velopack, updates, portable, startup]
version: unreleased (after v0.17.1)
severity: p1
status: active
---

# Velopack replaces current\ on every update

## Problem

The configurable data folder has three things that must survive an update:
the portable marker, the portable data, and the data folder choice itself.
Under Velopack, each of these can be lost by an update with no error.

- A Velopack package runs the real executable from `current\`. This is true of
  the Setup install (`%LOCALAPPDATA%\Clipthrough\current\Clipthrough.exe`) and of
  the `-Portable.zip`. An update renames `current\` away and extracts the new
  version in its place. A `portable.flag` "beside the exe" in `current\` is
  therefore deleted by the next update, and so is a `ClipthroughData` folder
  there. The next launch quietly falls back to `%LOCALAPPDATA%`, and the
  library appears to be lost.
- A relative library path resolved against the working directory lands in
  `current\` as well, because shortcuts and the launcher stub start the app
  there.
- Velopack restarts the app after applying an update, with no arguments
  unless it is given some. A session started with `--data-dir` comes back on
  the default folder.

## Root cause

Under Velopack, "next to the executable" is not a stable location. The stable
location is the application root: the folder that holds `Update.exe`, the
launcher stub and `packages\`. Velopack's own guidance is to store anything that must
survive an update one level up, outside `current\`.

## Solution

- `AppDataRootResolver.FindApplicationRoot` uses Velopack's own rule from
  `WindowsVelopackLocator` 1.2.0. When `Update.exe` is in the parent folder and
  the `sq.version` manifest is beside the executable, the parent is the
  application root. Otherwise the executable's own folder is the root, as for
  a plain ZIP. The marker is looked for only at the application root, and the
  portable data goes to `ClipthroughData` beside it. A marker inside `current\`
  is ignored, because honouring it would move the data folder at the next
  update.
- `StorageOptions.Normalize(dataRoot)` resolves a relative library path against
  the data root. In portable mode, `AppDataRoot.ToStoredPath` writes a library
  inside the portable folder relative to it.
- `AppDataRoot.RelaunchArguments` (`--data-dir <folder>` when the command line
  chose the folder) is passed to `ApplyUpdatesAndRestart`. The variable and the
  marker survive a restart on their own.
- Velopack's hooks run before the data folder is resolved. A bad `--data-dir`
  or `CLIPTHROUGH_DATA_DIR` can then never block an install, update or uninstall
  hook, and a hook never writes into the data folder.

Velopack's own `.portable` file is not treated as the marker. It is present in
every `-Portable.zip`, so honouring it would move existing portable users' data
away from `%LOCALAPPDATA%` on upgrade.

## Prevention

- `AppDataRootResolverTests` builds fake Velopack layouts (`Update.exe` +
  `current\sq.version`). It pins the marker to the root, ignores one inside
  `current\`, and keeps a plain copy below an unrelated `Update.exe` its own
  root. `PortableDataRootTests` moves a portable folder and reopens the
  library. `UpdateServiceTests` pins the restart arguments. Each test has a
  mutant in `Clipthrough.Tests/Mutation/mutants.json` (`dataroot-*`).
- The layout rule was checked against a real update with vpk 1.2.0. A probe
  app was packed as 1.0.0 and 1.0.1, and its 1.0.0 `-Portable.zip` was
  extracted with `portable.flag` and `ClipthroughData\` at the root and a decoy
  marker in `current\`. Then `Update.exe apply --package <1.0.1-full.nupkg>
  --norestart --silent` was run. `current\` came back as 1.0.1 without the
  decoy, the root marker and data were untouched, and the resolver gave the
  same folder before and after. Re-run this check after a Velopack upgrade:
  the rule copies Velopack's locator, and a layout change there will not make
  any test fail.
