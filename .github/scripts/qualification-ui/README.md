# Hosted-only installed-candidate UI smoke

This standalone Windows Desktop .NET 10 executable has **no NuGet dependencies**
and can launch only the specified installed candidate on the hosted VM after
proving real foreground input delivery. Do not run it locally, including on a developer
desktop with synthetic data. Static compilation is safe; execution is not.

The parent qualification runner owns installation, seed, restart, candidate
identity/version verification, and cleanup. This harness does not edit those
scripts or use shipping service instances. After feed restart, invoke against
the **installed** `current\Clipthrough.exe` and the same seeded root. Pass
`--launch` when the parent has stopped the candidate, or its PID when it is
already running.
Both root and evidence directory must be beneath `GITHUB_WORKSPACE`. Write
`.hosted-synthetic-root` under that throwaway root with this exact content:

```text
Clipthrough hosted synthetic qualification root
```

Build (static; never `dotnet test`):

```powershell
dotnet build .github\scripts\qualification-ui\HostedUiHarness.csproj -c Release
```

On a free GitHub-hosted Windows runner only:

```powershell
dotnet .github\scripts\qualification-ui\bin\Release\net10.0-windows\HostedUiHarness.dll `
    --launch "$installRoot\current\Clipthrough.exe" $chosenDataRoot $uiEvidenceDirectory
```

Use an external **300-second process timeout**, capture stdout/stderr, and always
upload the evidence directory. Terminate only the harness PID on timeout.
The harness also has a 240-second watchdog; if UIA hangs, the parent must record
the noncompletion as blocked/failed, never infer a pass from partial artifacts.
With `--launch`, it starts the actual installed executable with `--data-dir`,
then closes/stops only that owned PID and disposes its receiver. With an existing
PID it leaves the app running. On watchdog/parent timeout the parent must clean
up the owned candidate too; its PID is written to evidence before scenarios.
No user clipboard restoration is attempted on the disposable VM.

Guards run before handles, windows, root reads, input or clipboard:
`GITHUB_ACTIONS=true`, `RUNNER_OS=Windows`,
`RUNNER_ENVIRONMENT=github-hosted`. The guarded input probe opens the input
desktop, foregrounds a harness-owned native RichEdit, sends a random token with
Win32 `SendInput`, and requires exact receipt. A desktop handle alone is not
evidence. No app interaction or clipboard access follows a blocked probe.

Scenarios use the app's default `Alt+V`, `Ctrl+,`, search, selection, and Enter
paste path. If the seeded database lacks `storage.json`, the harness requires
the Welcome UI to display that exact synthetic database path and invokes
**Create library** without changing any draft values. This exercises actual
app storage configuration instead of manufacturing a service-only configuration.
It otherwise cancels Settings and never changes display settings:

- Show each seeded `... synthetic clip alpha` / `beta` row, dump accessible
  content, screenshot it; inspect the real Settings database path and require
  its file under the logged synthetic command-line root.
- Toggle the real popup with Alt+V; require actual foreground on summon and
  hidden/minimized/DWM-cloaked presentation on dismissal (not `IsVisible` alone).
- Publish unique Unicode and RTF via Win32 clipboard, allow normal capture,
  find/select the real app rows, replace the clipboard with independently
  verified plain poison lacking RTF/PNG formats, and press Enter. Require exact
  received text, proving restoration from history rather than forwarding the
  original capture input.
  Native RichEdit must render the RTF marker bold/red and its suffix non-bold;
  persist received RTF and a target screenshot. This is **RTF rich paste**, not
  HTML paste qualification; HTML rendering remains a separate unqualified gate.
- Publish an asymmetric 321x197 PNG through Win32, select the captured image,
  screenshot/dump dimensions in the app, paste through the app to a visible
  image receiver. Decode received PNG, require dimensions and **every pixel**
  equal to the fixture, save full-detail PNG and target screenshot. The custom
  receiver only accepts actual Ctrl+V; it does not fetch clipboard preemptively.

`ui-evidence.json` reports each gate's `status`/`detail`, process identity,
input-delivery token and semantic assertions. Exit codes: `0` all passed,
`1` failed/fatal, `2` refused/invalid invocation before evidence,
`3` blocked/incomplete. Missing accessibility, welcome/unlock overlays, missing
input/foreground, or inaccessible Settings are honest blocked outcomes.
Timeouts are blocked, not semantic successes. Inspect artifacts before mapping
these independent gates into the parent report: no physical GPU, monitor, IME,
multi-DPI-transition or HTML rendering qualification is claimed.

Startup discovery foregrounds the verified receiver and sends actual Alt+V before
looking for a presented, foreground candidate-owned UIA window. It identifies
the main window by the Search clips control or first-run Create library button,
not by a literal title (the app appends its display version). It never bypasses
the registered hotkey with ShowWindow. Before/after discovery, and on failure,
`startup-*-top-level-uia.json` inventories top-level PID, title, HWND, foreground,
offscreen state and bounds; failure also saves a desktop screenshot.
