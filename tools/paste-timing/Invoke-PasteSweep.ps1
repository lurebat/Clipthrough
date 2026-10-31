#Requires -Version 7
<#
.SYNOPSIS
    Measures how reliably Clipthrough's paste-and-hide lands in real applications
    at different settle margins (PasteTiming.SettleAfterForeground).

.DESCRIPTION
    For each target application and each settle value, Clipthrough is started
    with CLIPTHROUGH_PASTE_SETTLE_MS set, two unique markers are copied (so
    Clipthrough captures both), the popup is summoned with Alt+V, the OLDER
    marker is chosen (Down, Enter) and the script checks whether that marker
    arrived in the target. Choosing the older one matters: the clipboard held
    the newer one, so a paste that lands is Clipthrough's, not a leftover.

    150 ms reproduces the old fixed sleep exactly (the budget clips it), so it
    is the baseline the shorter values must match.

    The machine must be quiet: this drives the real keyboard and clipboard.
    Do not touch the mouse or keyboard while it runs. It refuses to start while
    any Clipthrough is running - only one may run at a time - and never stops
    one it did not start; the instances it starts it stops itself.

    Targets:
      Word      a new blank document via COM, closed without saving at the end
      Edge      a data: page with a textarea; the page title mirrors its text
      Terminal  Windows Terminal running a line reader that appends to a file
      Elevated  the same, elevated: you will be asked to approve UAC. A paste
                into an elevated window is blocked by Windows (UIPI) for any
                non-elevated sender; this records that it fails the same way
                at every settle value and that the wait does not hang.

.EXAMPLE
    pwsh tools\paste-timing\Invoke-PasteSweep.ps1 -ClipthroughExe .\Clipthrough\bin\Release\net10.0-windows10.0.19041.0\Clipthrough.exe -DataDir C:\scratch\paste-sweep
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$ClipthroughExe,
    # An isolated data folder. Created if missing; never your real library.
    [Parameter(Mandatory)] [string]$DataDir,
    [ValidateSet('Word', 'Edge', 'Terminal', 'Elevated')]
    [string[]]$Targets = @('Word', 'Edge', 'Terminal', 'Elevated'),
    [int[]]$SettleValues = @(150, 60, 30, 15, 0),
    [int]$Trials = 10,
    [string]$OutputCsv = (Join-Path $DataDir 'paste-sweep.csv')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -Namespace PasteSweep -Name Win32 -MemberDefinition @"
[DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool BringWindowToTop(System.IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(System.IntPtr hWnd, int cmd);
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint pid);
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, System.UIntPtr extra);
"@

$VK = @{ Alt = 0x12; V = 0x56; Down = 0x28; Enter = 0x0D }
function Send-Key([byte]$vk, [switch]$Up) { [PasteSweep.Win32]::keybd_event($vk, 0, $(if ($Up) { 2 } else { 0 }), [UIntPtr]::Zero) }
function Press([byte]$vk) { Send-Key $vk; Start-Sleep -Milliseconds 15; Send-Key $vk -Up }
function Send-AltV { Send-Key $VK.Alt; Start-Sleep -Milliseconds 15; Press $VK.V; Start-Sleep -Milliseconds 15; Send-Key $VK.Alt -Up }

function Set-Foreground([IntPtr]$hwnd) {
    $fg = [PasteSweep.Win32]::GetForegroundWindow()
    $pidOut = [uint32]0
    $fgThread = [PasteSweep.Win32]::GetWindowThreadProcessId($fg, [ref]$pidOut)
    $me = [PasteSweep.Win32]::GetCurrentThreadId()
    [void][PasteSweep.Win32]::AttachThreadInput($me, $fgThread, $true)
    try {
        [void][PasteSweep.Win32]::ShowWindow($hwnd, 9)
        [void][PasteSweep.Win32]::BringWindowToTop($hwnd)
        [void][PasteSweep.Win32]::SetForegroundWindow($hwnd)
    }
    finally { [void][PasteSweep.Win32]::AttachThreadInput($me, $fgThread, $false) }
    Wait-Until { [PasteSweep.Win32]::GetForegroundWindow() -eq $hwnd } 2000 | Out-Null
}

function Wait-Until([scriptblock]$condition, [int]$timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 20 }
    return $false
}

function Find-Window([string]$titleLike, [int]$timeoutMs = 15000) {
    $script:found = [IntPtr]::Zero
    Wait-Until {
        $p = Get-Process | Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like $titleLike } | Select-Object -First 1
        if ($p) { $script:found = $p.MainWindowHandle; $true } else { $false }
    } $timeoutMs | Out-Null
    if ($script:found -eq [IntPtr]::Zero) { throw "No window titled like '$titleLike' appeared." }
    return $script:found
}

# ---- guards -----------------------------------------------------------------
$running = Get-Process -Name Clipthrough -ErrorAction SilentlyContinue
if ($running) {
    throw "Clipthrough is running (pid $($running.Id -join ', ')). Quit it first - only one instance can run, and this script never stops one it did not start."
}
$ClipthroughExe = (Resolve-Path $ClipthroughExe).Path
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
$DataDir = (Resolve-Path $DataDir).Path
$termLog = Join-Path $DataDir 'terminal-lines.txt'
$results = [System.Collections.Generic.List[object]]::new()
Remove-Item -LiteralPath $OutputCsv -ErrorAction SilentlyContinue

# ---- targets ----------------------------------------------------------------
# The command lines exactly as Start-Process will build them. Test-SweepArguments.ps1
# checks each arrives at its process intact.
function Get-EdgeArguments {
    $html = '<title>PasteSweep</title><textarea autofocus style="width:100%;height:90vh" oninput="document.title=''PasteSweep ''+this.value.replace(/\s+/g,'' '')"></textarea>'
    # Percent-encoded: Start-Process quotes nothing, so a space would split the page into pieces.
    return "--new-window --inprivate data:text/html,$([Uri]::EscapeDataString($html))"
}

function Get-TerminalArguments([string]$title, [string]$linesFile) {
    $reader = "`$host.UI.RawUI.WindowTitle='$title'; while(`$true){ `$l=[Console]::ReadLine(); Add-Content -LiteralPath '$linesFile' -Value `$l }"
    # wt starts a new command at every ';', quoted or not, so the reader travels
    # as -EncodedCommand: Base64 has neither ';' nor spaces.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($reader))
    return "-w new --title $title pwsh -NoLogo -NoProfile -EncodedCommand $encoded"
}
$word = $null; $doc = $null; $startedWord = $false
function Open-Target([string]$name) {
    switch ($name) {
        'Word' {
            $script:startedWord = -not (Get-Process WINWORD -ErrorAction SilentlyContinue)
            $script:word = New-Object -ComObject Word.Application
            $script:word.Visible = $true
            $script:doc = $script:word.Documents.Add()
            $script:doc.Activate()
            return [IntPtr]$script:word.ActiveWindow.Hwnd
        }
        'Edge' {
            Start-Process msedge -ArgumentList (Get-EdgeArguments) | Out-Null
            return Find-Window 'PasteSweep*'
        }
        { $_ -in 'Terminal', 'Elevated' } {
            Set-Content -LiteralPath $termLog -Value $null
            $title = if ($name -eq 'Elevated') { 'PasteSweepElevated' } else { 'PasteSweepTerm' }
            $wtArgs = Get-TerminalArguments $title $termLog
            if ($name -eq 'Elevated') {
                Write-Host 'Approve the UAC prompt for the elevated terminal.' -ForegroundColor Yellow
                Start-Process wt -ArgumentList $wtArgs -Verb RunAs | Out-Null
            }
            else {
                Start-Process wt -ArgumentList $wtArgs | Out-Null
            }
            return Find-Window "$title*" 60000
        }
    }
}

function Read-Target([string]$name, [IntPtr]$hwnd) {
    switch ($name) {
        'Word' { return [string]$script:doc.Content.Text }
        'Edge' { return (Get-Process | Where-Object MainWindowHandle -eq $hwnd | Select-Object -First 1).MainWindowTitle }
        default { Press $VK.Enter; Start-Sleep -Milliseconds 300; return (Get-Content -LiteralPath $termLog -Raw -ErrorAction SilentlyContinue) }
    }
}

function Close-Target([string]$name, [IntPtr]$hwnd) {
    switch ($name) {
        'Word' {
            $script:doc.Close(0)   # wdDoNotSaveChanges
            if ($script:startedWord) { $script:word.Quit() }
        }
        default {
            Get-Process | Where-Object MainWindowHandle -eq $hwnd | ForEach-Object { [void]$_.CloseMainWindow() }
        }
    }
}

# ---- Clipthrough --------------------------------------------------------------
function Start-Clipthrough([int]$settle) {
    $env:CLIPTHROUGH_PASTE_SETTLE_MS = "$settle"
    $p = Start-Process -FilePath $ClipthroughExe -ArgumentList '--data-dir', $DataDir, '--log-popup-timings' -PassThru
    Remove-Item Env:\CLIPTHROUGH_PASTE_SETTLE_MS
    Wait-Until { $p.Refresh(); $p.MainWindowHandle -ne 0 } 30000 | Out-Null
    if (-not (Test-Path (Join-Path $DataDir 'settings.json'))) {
        Write-Host 'First run: finish the welcome screen in Clipthrough (Create library, keep Alt+V), then press Enter here.' -ForegroundColor Yellow
        [void](Read-Host)
    }
    return $p
}

function Get-LastPasteWait {
    $log = Get-ChildItem -LiteralPath $DataDir -Recurse -Filter 'clipthrough*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $log) { return $null }
    $m = Select-String -LiteralPath $log.FullName -Pattern '\[Paste\] target \S+ (forward|not forward) after ([\d.]+) ms' | Select-Object -Last 1
    if ($m) { return [pscustomobject]@{ Forward = $m.Matches[0].Groups[1].Value -eq 'forward'; Ms = [double]$m.Matches[0].Groups[2].Value } }
    return $null
}

# ---- sweep ------------------------------------------------------------------
Write-Host "Sweeping $($Targets -join ', ') x settle $($SettleValues -join '/') ms x $Trials trials. Hands off the keyboard and mouse." -ForegroundColor Cyan
foreach ($settle in $SettleValues) {
    $clip = Start-Clipthrough $settle
    try {
        $clipHwnd = $clip.MainWindowHandle
        foreach ($target in $Targets) {
            $hwnd = Open-Target $target
            try {
                for ($i = 1; $i -le $Trials; $i++) {
                    $older = "PS$settle-$target-$i-A-$([guid]::NewGuid().ToString('N').Substring(0, 6))"
                    $newer = $older.Replace('-A-', '-B-')
                    Set-Clipboard -Value $older; Start-Sleep -Milliseconds 500
                    Set-Clipboard -Value $newer; Start-Sleep -Milliseconds 500

                    Set-Foreground $hwnd
                    Send-AltV
                    $summoned = Wait-Until { [PasteSweep.Win32]::GetForegroundWindow() -eq $clipHwnd } 2000
                    Start-Sleep -Milliseconds 150
                    Press $VK.Down; Start-Sleep -Milliseconds 80
                    $sent = [Diagnostics.Stopwatch]::StartNew()
                    Press $VK.Enter
                    Start-Sleep -Milliseconds 700
                    $text = Read-Target $target $hwnd
                    $wait = Get-LastPasteWait
                    $row = [pscustomobject]@{
                        Target = $target; SettleMs = $settle; Trial = $i; Summoned = $summoned
                        Landed = [bool]($text -and $text.Contains($older))
                        WrongClip = [bool]($text -and $text.Contains($newer))
                        TargetForward = ${wait}?.Forward; ForegroundWaitMs = ${wait}?.Ms
                    }
                    $results.Add($row)
                    # Written as it happens, so a failure later in the run keeps what was measured.
                    $row | Export-Csv -LiteralPath $OutputCsv -NoTypeInformation -Append
                    Write-Host ("{0,-8} settle {1,3} ms  trial {2,2}: {3}" -f $target, $settle, $i, $(if ($row.Landed) { 'landed' } elseif (-not $summoned) { 'popup did not open' } else { 'MISSED' }))
                }
            }
            finally { Close-Target $target $hwnd }
        }
    }
    finally {
        # The instance this script started (close only sends it to the tray).
        if (-not $clip.HasExited) { Stop-Process -Id $clip.Id }
        [void](Wait-Until { $clip.HasExited } 5000)
    }
}

Write-Host "`nLanded / summoned, per target and settle value:" -ForegroundColor Cyan
$results | Group-Object Target, SettleMs | ForEach-Object {
    $ok = @($_.Group | Where-Object Landed).Count
    $summoned = @($_.Group | Where-Object Summoned).Count
    $waits = @($_.Group | Where-Object { $null -ne $_.ForegroundWaitMs } | ForEach-Object ForegroundWaitMs)
    [pscustomobject]@{
        Target = $_.Group[0].Target; SettleMs = $_.Group[0].SettleMs
        Landed = "$ok/$summoned"; WrongClip = @($_.Group | Where-Object WrongClip).Count
        MedianForegroundWaitMs = if ($waits) { ($waits | Sort-Object)[[int][Math]::Floor($waits.Count / 2)] } else { $null }
    }
} | Format-Table -AutoSize
Write-Host "Raw results: $OutputCsv"