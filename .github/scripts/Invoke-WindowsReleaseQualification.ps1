param(
    [Parameter(Mandatory)]
    [string] $PublishDirectory,
    [Parameter(Mandatory)]
    [string] $FeedDirectory,
    [Parameter(Mandatory)]
    [string] $HarnessDirectory,
    [Parameter(Mandatory)]
    [string] $UiHarnessDirectory,
    [Parameter(Mandatory)]
    [string] $CandidateVersion,
    [Parameter(Mandatory)]
    [string] $EvidencePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows') {
    throw 'Installer and legacy app callbacks are permitted only on an isolated GitHub-hosted Windows runner.'
}

$publish = (Resolve-Path $PublishDirectory).Path
$feed = (Resolve-Path $FeedDirectory).Path
$harness = (Resolve-Path $HarnessDirectory).Path
$uiHarness = (Resolve-Path $UiHarnessDirectory).Path
$evidence = [System.IO.Path]::GetFullPath($EvidencePath)
$evidenceDirectory = [System.IO.Path]::GetDirectoryName($evidence)
New-Item -ItemType Directory -Force -Path $evidenceDirectory | Out-Null
$transcriptPath = Join-Path $evidenceDirectory 'qualification.log'
Start-Transcript -Path $transcriptPath -Force | Out-Null

$setupUrl = 'https://github.com/lurebat/Clipthrough/releases/download/v0.17.1/Clipthrough-win-Setup.exe'
$setupSha256 = '1a415df7f2204e4596a9da13b91a72a130a1cd1aa71c189eac4c0064de251175'
$fullPackageUrl = 'https://github.com/lurebat/Clipthrough/releases/download/v0.17.1/Clipthrough-0.17.1-full.nupkg'
$fullPackageSha256 = '6bf69a537b9a77c056cfab59261b92718036ce01162064643a82a01b2a73e083'
$portableUrl = 'https://github.com/lurebat/Clipthrough/releases/download/v0.17.1/Clipthrough-win-Portable.zip'
$portableSha256 = '40fdc2af97e5c28b3477ab2c54293cfc76fac26e6c90370324b84dd6cbb13fa0'
$candidateVersion = $CandidateVersion
$workspace = Join-Path $evidenceDirectory 'runner-state'
$downloadDirectory = Join-Path $workspace 'downloads'
$installRoot = Join-Path $workspace 'installed'
$chosenDataRoot = Join-Path $workspace 'chosen-data'
$portableRoot = Join-Path $workspace 'portable'
$portableDataRoot = Join-Path $portableRoot 'ClipthroughData'
$rollbackRoot = Join-Path $workspace 'failed-apply-portable'
$rollbackDataRoot = $rollbackRoot
$minimumRuntime = [version]'10.0.14'

$script:gates = [System.Collections.Generic.List[object]]::new()
foreach ($gate in @(
        @{ Name = 'hosted_input_desktop'; Required = $false },
        @{ Name = 'old_setup_asset_integrity'; Required = $true },
        @{ Name = 'old_setup_install'; Required = $true },
        @{ Name = 'old_package_payload_mapping'; Required = $true },
        @{ Name = 'synthetic_data_seed_and_read'; Required = $true },
        @{ Name = 'candidate_feed_download_and_apply'; Required = $true },
        @{ Name = 'explicit_data_root_restart_survival'; Required = $true },
        @{ Name = 'portable_update_survival'; Required = $true },
        @{ Name = 'runtime_floor'; Required = $true },
        @{ Name = 'velopack_prerequisite_below_floor'; Required = $false },
        @{ Name = 'failed_package_rejection_preserves_version'; Required = $true },
        @{ Name = 'failed_apply_expected_broken_state_confirmed'; Required = $true },
        @{ Name = 'failed_apply_seeded_database_preserved'; Required = $true },
        @{ Name = 'accepted_stable12_apply_failure_and_recovery'; Required = $true },
        @{ Name = 'interactive_clipboard_paste_hotkey_smoke'; Required = $true },
        @{ Name = 'normal_ui_storage_and_record_visibility'; Required = $true },
        @{ Name = 'rich_paste_target_content'; Required = $true },
        @{ Name = 'physical_monitor_gpu_ime'; Required = $false }
    )) {
    $script:gates.Add([ordered]@{
        name = $gate.Name
        status = 'blocked'
        required = $gate.Required
        detail = 'Not reached.'
    })
}

function Get-ArchiveEntryHash {
    param([System.IO.Compression.ZipArchiveEntry] $Entry)
    $stream = $Entry.Open()
    try { return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
    finally { $stream.Dispose() }
}

function Assert-LegacyNupkgMapping {
    param([string] $PackagePath, [string] $Root)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $aliasSources = @('lib/app/Clipthrough_ExecutionStub.exe', 'lib/app/Squirrel.exe')
        $appEntries = @($archive.Entries | Where-Object { $_.FullName.StartsWith('lib/app/', [StringComparison]::OrdinalIgnoreCase) -and -not $_.FullName.EndsWith('/') -and $_.FullName -notin $aliasSources })
        if ($appEntries.Count -eq 0) { throw 'The pinned old full package contains no lib/app payloads.' }
        $mapping = [System.Collections.Generic.List[object]]::new()
        foreach ($entry in $appEntries) {
            $relative = $entry.FullName.Substring('lib/app/'.Length).Replace('/', '\')
            $destination = Join-Path (Join-Path $Root 'current') $relative
            if (-not (Test-Path $destination -PathType Leaf)) { throw "Installed lib/app payload is missing: $destination" }
            $sourceHash = Get-ArchiveEntryHash $entry
            $targetHash = (Get-FileHash -Algorithm SHA256 $destination).Hash.ToLowerInvariant()
            if ($sourceHash -ne $targetHash) { throw "Installed payload differs from lib/app package entry $($entry.FullName)." }
            $mapping.Add([ordered]@{ source = $entry.FullName; destination = $destination; sha256 = $sourceHash })
        }

        foreach ($alias in @(
                @{ Source = 'lib/app/Clipthrough_ExecutionStub.exe'; Destination = 'Clipthrough.exe' },
                @{ Source = 'lib/app/Squirrel.exe'; Destination = 'Update.exe' }
            )) {
            $entry = $archive.GetEntry($alias.Source)
            if ($null -eq $entry) { throw "The legacy package alias source is missing: $($alias.Source)" }
            $destination = Join-Path $Root $alias.Destination
            if (-not (Test-Path $destination -PathType Leaf)) { throw "The installed alias destination is missing: $destination" }
            $sourceHash = Get-ArchiveEntryHash $entry
            $targetHash = (Get-FileHash -Algorithm SHA256 $destination).Hash.ToLowerInvariant()
            if ($sourceHash -ne $targetHash) { throw "Installed alias $($alias.Source) -> $($alias.Destination) does not byte-match." }
            $mapping.Add([ordered]@{ source = $alias.Source; destination = $destination; sha256 = $sourceHash })
        }

        if ($mapping.Count -ne $appEntries.Count + 2) { throw 'Not every payload and root alias was verified.' }
        [System.IO.File]::WriteAllText(
            (Join-Path $evidenceDirectory 'legacy-package-mapping.json'),
            ($mapping | ConvertTo-Json -Depth 4),
            [System.Text.UTF8Encoding]::new($false))
    }
    finally {
        $archive.Dispose()
    }
}

function Set-Gate {
    param([string] $Name, [ValidateSet('passed', 'failed', 'blocked')][string] $Status, [string] $Detail)
    $gate = $script:gates | Where-Object name -eq $Name | Select-Object -First 1
    if ($null -eq $gate) { throw "Unknown evidence gate '$Name'." }
    $gate.status = $Status
    $gate.detail = $Detail
    Write-Host "[$Status] $Name - $Detail"
}

function Invoke-CheckedProcess {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $ArgumentList,
        [Parameter(Mandatory)][string] $StdoutPath,
        [Parameter(Mandatory)][string] $StderrPath,
        [string] $WorkingDirectory = (Get-Location).Path,
        [int] $TimeoutSeconds = 600,
        [int[]] $AllowedExitCodes = @(0),
        [switch] $AllowAnyExitCode
    )

    $process = Start-Process -FilePath $FilePath -ArgumentList $ArgumentList -PassThru `
        -WorkingDirectory $WorkingDirectory `
        -RedirectStandardOutput $StdoutPath -RedirectStandardError $StderrPath
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        Stop-Process -Id $process.Id -Force
        throw "$FilePath timed out after $TimeoutSeconds seconds."
    }

    $process.Refresh()
    if (-not $AllowAnyExitCode -and $process.ExitCode -notin $AllowedExitCodes) {
        throw "$FilePath exited with $($process.ExitCode). See $StdoutPath and $StderrPath."
    }
    return $process.ExitCode
}

function Invoke-QualificationTest {
    param(
        [ValidateSet('seed', 'verify')][string] $Mode,
        [string] $DataRoot,
        [string] $Label,
        [string] $DatabasePath,
        [ValidateSet('Default', 'CommandLine', 'EnvironmentVariable', 'Portable')][string] $SourceName
    )
    $env:CLIPTHROUGH_QUALIFICATION_DATA_ROOT = $DataRoot
    $env:CLIPTHROUGH_QUALIFICATION_MODE = $Mode
    if ([string]::IsNullOrWhiteSpace($SourceName)) {
        $SourceName = if ($Label -like '*portable*') { 'Portable' } else { 'CommandLine' }
    }
    $env:CLIPTHROUGH_QUALIFICATION_SOURCE = $SourceName
    if ([string]::IsNullOrWhiteSpace($DatabasePath)) {
        Remove-Item Env:\CLIPTHROUGH_QUALIFICATION_DATABASE_PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:CLIPTHROUGH_QUALIFICATION_DATABASE_PATH = $DatabasePath
    }
    $resultDirectory = Join-Path $evidenceDirectory "test-$Label"
    New-Item -ItemType Directory -Force -Path $resultDirectory | Out-Null
    & dotnet test .\Clipthrough.Tests\Clipthrough.Tests.csproj `
        --configuration Release `
        --filter 'FullyQualifiedName~HostedUpgradeQualificationTests' `
        --logger "trx;LogFileName=$Label.trx" `
        --results-directory $resultDirectory
    $exitCode = $LASTEXITCODE
    Remove-Item Env:\CLIPTHROUGH_QUALIFICATION_DATA_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:\CLIPTHROUGH_QUALIFICATION_MODE -ErrorAction SilentlyContinue
    Remove-Item Env:\CLIPTHROUGH_QUALIFICATION_SOURCE -ErrorAction SilentlyContinue
    Remove-Item Env:\CLIPTHROUGH_QUALIFICATION_DATABASE_PATH -ErrorAction SilentlyContinue
    if ($exitCode -ne 0) { throw "Shipping storage qualification test ($Label) exited with $exitCode." }
}

function Read-VersionManifest {
    param([string] $Root)
    $path = Join-Path $Root 'current\sq.version'
    if (-not (Test-Path $path)) { throw "Velopack version manifest is missing: $path" }
    [xml]$manifest = Get-Content -Raw -LiteralPath $path
    $versionNode = $manifest.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='version']")
    if ($null -eq $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
        throw "Velopack version manifest has no metadata version: $path"
    }
    return $versionNode.InnerText.Trim()
}

function Wait-ForCandidateManifest {
    param([string] $Root, [int] $TimeoutSeconds = 600)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $manifestPath = Join-Path $Root 'current\sq.version'
        if (Test-Path $manifestPath) {
            $manifest = Read-VersionManifest $Root
            if ($manifest -eq $candidateVersion) { return $manifest }
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Velopack did not finish applying candidate $candidateVersion within $TimeoutSeconds seconds."
}

function Wait-ForDataRootLog {
    param([string] $Root, [string] $SourceName, [long] $StartOffset = -1, [int] $TimeoutSeconds = 180)
    $log = Join-Path $Root 'logs\clipthrough.log'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $expectedLine = "Data folder: $Root ($SourceName)."
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path $log) {
            $stream = $null
            $reader = $null
            try {
                $stream = [System.IO.File]::Open($log, [System.IO.FileMode]::Open,
                    [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
                $offset = if ($StartOffset -ge 0 -and $stream.Length -ge $StartOffset) { $StartOffset } else { 0 }
                [void]$stream.Seek($offset, [System.IO.SeekOrigin]::Begin)
                $reader = [System.IO.StreamReader]::new($stream)
                $newContent = $reader.ReadToEnd()
                if ($newContent.Contains($expectedLine, [StringComparison]::OrdinalIgnoreCase)) { return $log }
            }
            catch [System.IO.IOException] {
            }
            finally {
                if ($null -ne $reader) { $reader.Dispose() }
                elseif ($null -ne $stream) { $stream.Dispose() }
            }
        }
        Start-Sleep -Seconds 2
    }
    throw "The restarted app did not log its resolved data root: $expectedLine"
}

function Stop-NewClipthroughProcesses {
    param([int[]] $PriorIds)
    Get-Process -Name Clipthrough -ErrorAction SilentlyContinue |
        Where-Object { $_.Id -notin $PriorIds } |
        ForEach-Object { Stop-Process -Id $_.Id -Force }
}

function Get-HostedInputDesktopProbe {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HostedDesktopProbe {
    [StructLayout(LayoutKind.Sequential)]
    public struct UserObjectFlags { public int Inherit; public int Reserved; public uint Flags; }
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetUserObjectInformation(IntPtr handle, int index, out UserObjectFlags info, uint length, out uint needed);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetCursorPos(out Point point);
    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X; public int Y; }
}
'@
    $station = [HostedDesktopProbe]::GetProcessWindowStation()
    if ($station -eq [IntPtr]::Zero) { return 'blocked: no process window station' }
    $flags = [HostedDesktopProbe+UserObjectFlags]::new()
    [uint32]$needed = 0
    if (-not [HostedDesktopProbe]::GetUserObjectInformation($station, 1, [ref]$flags, [uint32][Runtime.InteropServices.Marshal]::SizeOf($flags), [ref]$needed)) {
        return "blocked: cannot read window-station flags (Win32 $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))"
    }
    if (($flags.Flags -band 1) -eq 0) { return 'blocked: window station is not marked visible' }
    $desktop = [HostedDesktopProbe]::OpenInputDesktop(0, $false, 0x0100 -bor 0x0001)
    if ($desktop -eq [IntPtr]::Zero) {
        return "blocked: cannot open input desktop (Win32 $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))"
    }
    try {
        $cursor = [HostedDesktopProbe+Point]::new()
        if ([HostedDesktopProbe]::GetForegroundWindow() -eq [IntPtr]::Zero -or -not [HostedDesktopProbe]::GetCursorPos([ref]$cursor)) {
            return 'blocked: input desktop has no readable foreground window or cursor'
        }
    }
    finally {
        [void][HostedDesktopProbe]::CloseDesktop($desktop)
    }
    return 'passed: visible window station, open input desktop, foreground window and cursor are accessible'
}

function Invoke-FeedUpdate {
    param([string] $Root, [string] $DataRoot, [switch] $Portable)
    $priorIds = @(Get-Process -Name Clipthrough -ErrorAction SilentlyContinue | ForEach-Object Id)
    $currentDirectory = Join-Path $Root 'current'
    Copy-Item -Path (Join-Path $harness '*') -Destination $currentDirectory -Force
    $harnessExe = Join-Path $currentDirectory 'FeedApplyHarness.exe'
    $arguments = @($feed)
    if (-not $Portable) { $arguments += $DataRoot }
    $label = if ($Portable) { 'portable-feed-apply' } else { 'installed-feed-apply' }
    $stdout = Join-Path $evidenceDirectory "$label.stdout.log"
    $stderr = Join-Path $evidenceDirectory "$label.stderr.log"
    Invoke-CheckedProcess -FilePath $harnessExe -ArgumentList $arguments -StdoutPath $stdout -StderrPath $stderr `
        -WorkingDirectory $currentDirectory -TimeoutSeconds 900 | Out-Null
    $manifest = Wait-ForCandidateManifest -Root $Root
    $sourceName = if ($Portable) { 'Portable' } else { 'CommandLine' }
    $dataLog = Wait-ForDataRootLog -Root $DataRoot -SourceName $sourceName
    Stop-NewClipthroughProcesses -PriorIds $priorIds
    return [ordered]@{ manifest = $manifest; dataRootLog = $dataLog; updaterOutput = $stdout }
}

function Invoke-HostedUiQualification {
    $uiDirectory = Join-Path $evidenceDirectory 'live-ui'
    New-Item -ItemType Directory -Force -Path $uiDirectory | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $chosenDataRoot '.hosted-synthetic-root'),
        'Clipthrough hosted synthetic qualification root',
        [Text.UTF8Encoding]::new($false))
    $uiExit = Invoke-CheckedProcess -FilePath (Join-Path $uiHarness 'HostedUiHarness.exe') `
        -ArgumentList @('--launch', (Join-Path $installRoot 'current\Clipthrough.exe'), $chosenDataRoot, $uiDirectory) `
        -StdoutPath (Join-Path $uiDirectory 'harness.stdout.log') `
        -StderrPath (Join-Path $uiDirectory 'harness.stderr.log') `
        -TimeoutSeconds 300 -AllowAnyExitCode
    $uiReportPath = Join-Path $uiDirectory 'ui-evidence.json'
    if (-not (Test-Path $uiReportPath)) {
        throw "UI harness exited $uiExit without a completed evidence report."
    }
    $uiReport = Get-Content -Raw -LiteralPath $uiReportPath | ConvertFrom-Json
    foreach ($gateName in @('hosted_input_desktop', 'normal_ui_storage_and_record_visibility', 'rich_paste_target_content')) {
        $uiGate = $uiReport.gates.$gateName
        Set-Gate $gateName $uiGate.status $uiGate.detail
    }
    $smokeGates = @('hotkey_summon_dismiss', 'plain_paste_target_content', 'png_full_detail_paste')
    $smokeStatus = if (@($smokeGates | Where-Object { $uiReport.gates.$_.status -eq 'failed' }).Count) {
        'failed'
    } elseif (@($smokeGates | Where-Object { $uiReport.gates.$_.status -ne 'passed' }).Count) {
        'blocked'
    } else {
        'passed'
    }
    Set-Gate 'interactive_clipboard_paste_hotkey_smoke' $smokeStatus `
        "Actual foreground input, popup hotkeys, plain target paste and full PNG pixel comparison; see live-ui/ui-evidence.json. Harness exit=$uiExit."
    if ($uiExit -eq 1 -or $uiExit -eq 2) {
        Set-Gate 'interactive_clipboard_paste_hotkey_smoke' 'failed' `
            "Hosted UI harness failed with exit $uiExit; inspect its evidence. Other independent fixture gates will still run."
    }
}

function Get-RollbackFileEvidence {
    param([string] $Path)
    $stream = [System.IO.File]::Open(
        $Path,
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete)
    try {
        return [ordered]@{
            length = $stream.Length
            sha256 = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-RollbackDataRootSnapshot {
    param([string] $DataRoot)
    $files = @(
        'settings.json',
        'storage.json',
        'window.json',
        'copilot-token.bin',
        'logs\clipthrough.log'
    )
    return @(
        foreach ($relativePath in $files) {
            $path = Join-Path $DataRoot $relativePath
            if (Test-Path $path -PathType Leaf) {
                $file = Get-RollbackFileEvidence $path
                [ordered]@{
                    name = $relativePath
                    exists = $true
                    length = $file.length
                    sha256 = $file.sha256
                }
            }
            else {
                [ordered]@{ name = $relativePath; exists = $false; length = $null; sha256 = $null }
            }
        }
    )
}

function Get-RollbackDataRootChanges {
    param([object[]] $Before, [object[]] $After)
    $beforeByName = @{}
    foreach ($entry in $Before) { $beforeByName[$entry.name] = $entry }
    return @(
        foreach ($entry in $After) {
            $prior = $beforeByName[$entry.name]
            $state = if (-not $entry.exists) {
                'missing'
            }
            elseif (-not $prior.exists) {
                'created'
            }
            elseif ($entry.sha256 -eq $prior.sha256) {
                'unchanged'
            }
            else {
                'modified'
            }
            [ordered]@{ name = $entry.name; state = $state; beforeSha256 = $prior.sha256; afterSha256 = $entry.sha256 }
        }
    )
}

function Get-RollbackDatabaseSnapshot {
    param([string] $DatabasePath)
    return @(
        foreach ($suffix in @('', '-wal', '-shm')) {
            $path = "$DatabasePath$suffix"
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                $file = Get-RollbackFileEvidence $path
                [ordered]@{ name = [System.IO.Path]::GetFileName($path); exists = $true; length = $file.length; sha256 = $file.sha256 }
            }
            else {
                [ordered]@{ name = [System.IO.Path]::GetFileName($path); exists = $false; length = $null; sha256 = $null }
            }
        }
    )
}

function Test-RollbackDatabaseSnapshotMatch {
    param([object[]] $Expected, [object[]] $Actual)
    if ($Expected.Count -ne $Actual.Count) { return $false }
    for ($i = 0; $i -lt $Expected.Count; $i++) {
        if ($Expected[$i].name -ne $Actual[$i].name -or
            $Expected[$i].exists -ne $Actual[$i].exists -or
            $Expected[$i].sha256 -ne $Actual[$i].sha256) {
            return $false
        }
    }
    return $true
}

function Save-RollbackRecoveryBackup {
    param([string] $InstallationRoot, [string] $DataRoot, [string] $DatabasePath, [string] $BackupRoot)
    $fullBackupRoot = [System.IO.Path]::GetFullPath($BackupRoot)
    $fullDataRoot = [System.IO.Path]::GetFullPath($DataRoot)
    foreach ($protectedRoot in @($InstallationRoot, $DataRoot)) {
        $fullProtectedRoot = [System.IO.Path]::GetFullPath($protectedRoot).TrimEnd('\')
        $backupInsideProtectedRoot = $fullBackupRoot.Equals($fullProtectedRoot, [StringComparison]::OrdinalIgnoreCase) `
            -or $fullBackupRoot.StartsWith($fullProtectedRoot + '\', [StringComparison]::OrdinalIgnoreCase)
        if ($backupInsideProtectedRoot) {
            throw "Recovery backup must be outside both the installation and data roots: $fullBackupRoot"
        }
    }
    if (Test-Path -LiteralPath $fullBackupRoot) {
        throw "Recovery backup path already exists; refusing reuse: $fullBackupRoot"
    }
    New-Item -ItemType Directory -Force -Path $fullBackupRoot | Out-Null
    foreach ($relativePath in @('settings.json', 'storage.json', 'window.json', 'copilot-token.bin', 'logs\clipthrough.log')) {
        $source = Join-Path $fullDataRoot $relativePath
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            $destination = Join-Path $fullBackupRoot "data\$relativePath"
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination
        }
    }
    $databaseSnapshot = Get-RollbackDatabaseSnapshot $DatabasePath
    if (-not $databaseSnapshot[0].exists) { throw "Cannot back up the missing synthetic library: $DatabasePath" }
    $backupDatabaseRoot = Join-Path $fullBackupRoot 'library'
    New-Item -ItemType Directory -Force -Path $backupDatabaseRoot | Out-Null
    foreach ($entry in $databaseSnapshot) {
        $source = if ($entry.name -eq [System.IO.Path]::GetFileName($DatabasePath)) { $DatabasePath } else { "$DatabasePath$($entry.name.Substring([System.IO.Path]::GetFileName($DatabasePath).Length))" }
        if ($entry.exists) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $backupDatabaseRoot $entry.name)
        }
    }
    return [ordered]@{
        path = $fullBackupRoot
        databaseFiles = $databaseSnapshot
        sourceSnapshot = Get-RollbackDataRootSnapshot $fullDataRoot
    }
}

function Restore-RollbackRecoveryBackup {
    param([string] $DataRoot, [string] $DatabasePath, [string] $BackupRoot)
    foreach ($relativePath in @('settings.json', 'storage.json', 'window.json', 'copilot-token.bin', 'logs\clipthrough.log')) {
        $source = Join-Path $BackupRoot "data\$relativePath"
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            $destination = Join-Path $DataRoot $relativePath
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }
    }
    $backupDatabaseRoot = Join-Path $BackupRoot 'library'
    $mainDatabaseName = [System.IO.Path]::GetFileName($DatabasePath)
    if (-not (Test-Path -LiteralPath (Join-Path $backupDatabaseRoot $mainDatabaseName) -PathType Leaf)) {
        throw "Recovery backup is missing its library copy: $(Join-Path $backupDatabaseRoot $mainDatabaseName)"
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DatabasePath) | Out-Null
    foreach ($name in @($mainDatabaseName, "$mainDatabaseName-wal", "$mainDatabaseName-shm")) {
        $source = Join-Path $backupDatabaseRoot $name
        $destination = Join-Path (Split-Path -Parent $DatabasePath) $name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination $destination -Force
        }
        elseif (Test-Path -LiteralPath $destination -PathType Leaf) {
            Remove-Item -LiteralPath $destination -Force
        }
    }
}

function Get-RollbackInstallationInventory {
    param([string] $Root, [string[]] $ExcludedRoots = @())
    $parent = Split-Path -Parent $Root
    $excluded = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($excludedRoot in $ExcludedRoots) {
        if (-not [string]::IsNullOrWhiteSpace($excludedRoot)) {
            [void]$excluded.Add([System.IO.Path]::GetFullPath($excludedRoot))
        }
    }

    $directories = [System.Collections.Generic.List[string]]::new()
    if (Test-Path $Root -PathType Container) { $directories.Add([System.IO.Path]::GetFullPath($Root)) }
    foreach ($directory in Get-ChildItem -LiteralPath $parent -Directory -ErrorAction Stop) {
        $fullPath = [System.IO.Path]::GetFullPath($directory.FullName)
        if ($fullPath -ne [System.IO.Path]::GetFullPath($Root) -and -not $excluded.Contains($fullPath)) {
            $directories.Add($fullPath)
        }
    }

    $knownFiles = @(
        'current\sq.version',
        'current\Clipthrough.dll',
        'settings.json',
        'storage.json',
        'window.json',
        'copilot-token.bin',
        'logs\clipthrough.log',
        'clipthrough.db',
        'clipthrough.db-wal',
        'clipthrough.db-shm',
        'ClipthroughData\clipthrough.db',
        'ClipthroughData\clipthrough.db-wal',
        'ClipthroughData\clipthrough.db-shm'
    )
    return @(
        foreach ($directory in $directories | Select-Object -Unique) {
            $files = @(
                foreach ($relativePath in $knownFiles) {
                    $path = Join-Path $directory $relativePath
                    if (-not (Test-Path $path -PathType Leaf)) {
                        [ordered]@{ name = $relativePath; exists = $false; length = $null; sha256 = $null }
                        continue
                    }

                    $file = Get-RollbackFileEvidence $path
                    [ordered]@{ name = $relativePath; exists = $true; length = $file.length; sha256 = $file.sha256 }
                }
            )
            [ordered]@{ path = $directory; files = $files }
        }
    )
}

function Invoke-RollbackCandidateAppStart {
    param([string] $Root, [string] $DataRoot, [string] $SourceName, [string] $Label)
    $priorIds = @(Get-Process -Name Clipthrough -ErrorAction SilentlyContinue | ForEach-Object Id)
    $expectedAppPath = [System.IO.Path]::GetFullPath((Join-Path $Root 'current\Clipthrough.exe'))
    $logPath = Join-Path $DataRoot 'logs\clipthrough.log'
    $logOffset = if (Test-Path $logPath -PathType Leaf) { (Get-Item -LiteralPath $logPath).Length } else { 0 }
    $appStdout = Join-Path $evidenceDirectory "$Label-app.stdout.log"
    $appStderr = Join-Path $evidenceDirectory "$Label-app.stderr.log"
    $appArguments = if ($SourceName -eq 'CommandLine') { @('--data-dir', "`"$DataRoot`"") } else { @() }
    $app = Start-Process -FilePath (Join-Path $Root 'Clipthrough.exe') `
        -ArgumentList $appArguments `
        -WorkingDirectory $Root `
        -PassThru `
        -RedirectStandardOutput $appStdout `
        -RedirectStandardError $appStderr
    try {
        $dataRootLog = Wait-ForDataRootLog -Root $DataRoot -SourceName $SourceName -StartOffset $logOffset -TimeoutSeconds 180
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $candidateApp = $null
        do {
            $candidateApp = Get-Process -Name Clipthrough -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.Id -notin $priorIds `
                        -and [System.IO.Path]::GetFullPath($_.Path).Equals($expectedAppPath, [StringComparison]::OrdinalIgnoreCase)
                } |
                Select-Object -First 1
            if ($null -eq $candidateApp) { Start-Sleep -Milliseconds 250 }
        } while ($null -eq $candidateApp -and [DateTime]::UtcNow -lt $deadline)
        if ($null -eq $candidateApp) {
            throw "The root execution stub logged startup, but no live candidate process was found at $expectedAppPath."
        }
        Start-Sleep -Seconds 3
        $candidateApp.Refresh()
        if ($candidateApp.HasExited -or -not [System.IO.Path]::GetFullPath($candidateApp.Path).Equals(
                $expectedAppPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The verified current\Clipthrough.exe process exited or changed image before the recovery check completed.'
        }
        return [ordered]@{
            processStarted = $true
            processId = $candidateApp.Id
            processPath = $candidateApp.Path
            executionStubProcessId = $app.Id
            executionStubExited = $app.HasExited
            dataRootLog = $dataRootLog
            dataRootLogOffsetBeforeLaunch = $logOffset
            stillRunningAfterThreeSeconds = $true
        }
    }
    finally {
        Stop-NewClipthroughProcesses -PriorIds $priorIds
    }
}

function Invoke-FailedApplyRollback {
    $packagePath = Join-Path $feed "Clipthrough-$candidateVersion-full.nupkg"
    $candidateSetup = @(Get-ChildItem -LiteralPath $feed -Filter '*Setup.exe' -File)
    $candidatePortable = @(Get-ChildItem -LiteralPath $feed -Filter '*Portable.zip' -File)
    if (-not (Test-Path $packagePath)) {
        throw "The generated full update package required for the apply-failure characterization is missing: $packagePath"
    }
    if ($candidateSetup.Count -ne 1 -or $candidatePortable.Count -ne 1) {
        throw "Expected one candidate Setup.exe and one candidate Portable.zip in $feed; found $($candidateSetup.Count) and $($candidatePortable.Count)."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $candidateArchive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $updaterEntry = $candidateArchive.GetEntry('lib/app/Squirrel.exe')
        $payloadEntry = $candidateArchive.GetEntry('lib/app/Clipthrough.dll')
        $manifestEntry = $candidateArchive.GetEntry('lib/app/sq.version')
        if ($null -eq $updaterEntry -or $null -eq $payloadEntry -or $null -eq $manifestEntry) {
            throw 'The candidate package lacks its updater or main managed payload needed for the failure injection.'
        }
        $expectedUpdaterHash = Get-ArchiveEntryHash $updaterEntry
        $candidatePayloadHash = Get-ArchiveEntryHash $payloadEntry
        $candidateManifestHash = Get-ArchiveEntryHash $manifestEntry
    }
    finally {
        $candidateArchive.Dispose()
    }
    $candidatePackageHash = (Get-FileHash -Algorithm SHA256 $packagePath).Hash.ToLowerInvariant()

    $installedRollbackRoot = Join-Path $workspace 'failed-apply-installed'
    $fixtures = @(
        [ordered]@{
            name = 'installed'
            root = $installedRollbackRoot
            dataRoot = $installedRollbackRoot
            databasePath = Join-Path $installedRollbackRoot 'ClipthroughData\clipthrough.db'
            defaultDataRoot = $null
            defaultDatabasePath = $null
            source = 'CommandLine'
            isPortable = $false
            prepared = $false
            preparationError = $null
            failure = [ordered]@{
                replacementFailureObserved = $false
                deleteSharingDenied = $false
                applyLogReachedReplacement = $false
                replacementFailureObservedWhileLocked = $false
                currentManifestAbsent = $false
                currentPayloadAbsent = $false
                oldManifestRestoredToCurrent = $false
                oldPayloadRestoredToCurrent = $false
                updateExitCode = $null
                databaseByteIdenticalAfterApply = $false
                databaseRecordsReadableAfterApply = $false
                installationInventoryBeforeApply = @()
                installationInventoryAfterApply = @()
                installationInventoryError = $null
                newSiblingDirectoriesAfterApply = @()
            }
            recovery = [ordered]@{
                error = $null
                manifestIsCandidate = $false
                appStart = [ordered]@{ processStarted = $false; stillRunningAfterThreeSeconds = $false }
                databaseRecordsReadableAfterRecovery = $false
                databaseByteIdenticalAfterRecovery = $false
                databaseByteIdenticalAfterRestore = $false
                databaseByteIdenticalImmediatelyAfterSetupOrOverlay = $false
                installationInventoryBeforeRecovery = @()
                installationInventoryAfterRecovery = @()
                newSiblingDirectoriesAfterRecovery = @()
                installationInventoryAfterAppStart = @()
                newSiblingDirectoriesAfterAppStart = @()
                renamedOldInstallationRootsAfterRecovery = @()
                installationInventoryError = $null
                storageConfigRestorationRequired = $false
                storageConfigStillPointsToPreservedLibrary = $false
                rootFilesRestoredByteIdentically = $false
                recoveryBackup = $null
                portableFlagPreserved = $true
                userVisibleDataReset = $false
                rootFileChanges = @()
                recovered = $false
            }
        },
        [ordered]@{
            name = 'installed-default-layout'
            root = Join-Path $workspace 'simulated-localappdata\Clipthrough'
            dataRoot = Join-Path $workspace 'simulated-localappdata\Clipthrough'
            databasePath = Join-Path $workspace 'simulated-localappdata\ClipthroughData\clipthrough.db'
            defaultDataRoot = Join-Path $env:LOCALAPPDATA 'Clipthrough'
            defaultDatabasePath = Join-Path $env:LOCALAPPDATA 'ClipthroughData\clipthrough.db'
            simulatedDefaultDataRoot = Join-Path $workspace 'simulated-localappdata\Clipthrough'
            simulatedDefaultDatabasePath = Join-Path $workspace 'simulated-localappdata\ClipthroughData\clipthrough.db'
            source = 'CommandLine'
            isPortable = $false
            prepared = $false
            preparationError = $null
            failure = [ordered]@{
                replacementFailureObserved = $false
                deleteSharingDenied = $false
                applyLogReachedReplacement = $false
                replacementFailureObservedWhileLocked = $false
                currentManifestAbsent = $false
                currentPayloadAbsent = $false
                oldManifestRestoredToCurrent = $false
                oldPayloadRestoredToCurrent = $false
                updateExitCode = $null
                databaseByteIdenticalAfterApply = $false
                databaseRecordsReadableAfterApply = $false
                installationInventoryBeforeApply = @()
                installationInventoryAfterApply = @()
                installationInventoryError = $null
                newSiblingDirectoriesAfterApply = @()
            }
            recovery = [ordered]@{
                error = $null
                manifestIsCandidate = $false
                appStart = [ordered]@{ processStarted = $false; stillRunningAfterThreeSeconds = $false }
                databaseRecordsReadableAfterRecovery = $false
                databaseByteIdenticalAfterRecovery = $false
                databaseByteIdenticalAfterRestore = $false
                databaseByteIdenticalImmediatelyAfterSetupOrOverlay = $false
                installationInventoryBeforeRecovery = @()
                installationInventoryAfterRecovery = @()
                newSiblingDirectoriesAfterRecovery = @()
                installationInventoryAfterAppStart = @()
                newSiblingDirectoriesAfterAppStart = @()
                renamedOldInstallationRootsAfterRecovery = @()
                installationInventoryError = $null
                storageConfigRestorationRequired = $false
                storageConfigStillPointsToPreservedLibrary = $false
                portableFlagPreserved = $true
                userVisibleDataReset = $false
                rootFileChanges = @()
                rootFilesRestoredByteIdentically = $false
                recoveryBackup = $null
                recovered = $false
            }
        },
        [ordered]@{
            name = 'portable'
            root = $rollbackRoot
            dataRoot = Join-Path $rollbackRoot 'ClipthroughData'
            databasePath = Join-Path $rollbackRoot 'ClipthroughData\clipthrough.db'
            defaultDataRoot = $null
            defaultDatabasePath = $null
            source = 'CommandLine'
            isPortable = $true
            prepared = $false
            preparationError = $null
            failure = [ordered]@{
                replacementFailureObserved = $false
                deleteSharingDenied = $false
                applyLogReachedReplacement = $false
                replacementFailureObservedWhileLocked = $false
                currentManifestAbsent = $false
                currentPayloadAbsent = $false
                oldManifestRestoredToCurrent = $false
                oldPayloadRestoredToCurrent = $false
                updateExitCode = $null
                databaseByteIdenticalAfterApply = $false
                databaseRecordsReadableAfterApply = $false
                installationInventoryBeforeApply = @()
                installationInventoryAfterApply = @()
                installationInventoryError = $null
                newSiblingDirectoriesAfterApply = @()
            }
            recovery = [ordered]@{
                error = $null
                manifestIsCandidate = $false
                appStart = [ordered]@{ processStarted = $false; stillRunningAfterThreeSeconds = $false }
                databaseRecordsReadableAfterRecovery = $false
                databaseByteIdenticalAfterRecovery = $false
                databaseByteIdenticalAfterRestore = $false
                databaseByteIdenticalImmediatelyAfterSetupOrOverlay = $false
                installationInventoryBeforeRecovery = @()
                installationInventoryAfterRecovery = @()
                newSiblingDirectoriesAfterRecovery = @()
                installationInventoryAfterAppStart = @()
                newSiblingDirectoriesAfterAppStart = @()
                renamedOldInstallationRootsAfterRecovery = @()
                installationInventoryError = $null
                storageConfigRestorationRequired = $false
                storageConfigStillPointsToPreservedLibrary = $false
                rootFilesRestoredByteIdentically = $false
                recoveryBackup = $null
                portableFlagPreserved = $false
                userVisibleDataReset = $false
                rootFileChanges = @()
                recovered = $false
            }
        }
    )

    foreach ($fixture in $fixtures) {
        try {
            if ((Test-Path $fixture.root) -or (Test-Path $fixture.dataRoot)) {
                throw "The fresh hosted runner already contains fixture paths for $($fixture.name); refusing to reuse them."
            }
            if ($fixture.isPortable) {
                Expand-Archive -LiteralPath $portablePath -DestinationPath $fixture.root -Force
                if (-not (Test-Path (Join-Path $fixture.root '.portable')) -or -not (Test-Path (Join-Path $fixture.root 'Update.exe'))) {
                    throw 'The old portable archive did not produce a Velopack portable fixture.'
                }
                [System.IO.File]::WriteAllText(
                    (Join-Path $fixture.root 'portable.flag'),
                    'hosted synthetic portable marker',
                    [System.Text.UTF8Encoding]::new($false))
            }
            else {
                New-Item -ItemType Directory -Force -Path (Split-Path -Parent $fixture.root) | Out-Null
                $setupStdout = Join-Path $evidenceDirectory 'rollback-old-installed-setup.stdout.log'
                $setupStderr = Join-Path $evidenceDirectory 'rollback-old-installed-setup.stderr.log'
                $priorIds = @(Get-Process -Name Clipthrough -ErrorAction SilentlyContinue | ForEach-Object Id)
                $oldSetup = Start-Process -FilePath $setupPath `
                    -ArgumentList @('--silent', '--installto', "`"$($fixture.root)`"") `
                    -PassThru `
                    -RedirectStandardOutput $setupStdout `
                    -RedirectStandardError $setupStderr
                if (-not $oldSetup.WaitForExit(600000)) {
                    Stop-Process -Id $oldSetup.Id -Force
                    throw 'The isolated old Setup install exceeded ten minutes.'
                }
                $oldSetup.Refresh()
                if ($oldSetup.ExitCode -ne 0) { throw "The isolated old Setup install exited with $($oldSetup.ExitCode)." }
                Stop-NewClipthroughProcesses -PriorIds $priorIds
                if ((Read-VersionManifest $fixture.root) -notmatch '0\.17\.1') {
                    throw 'The isolated installed-style fixture is not the expected old v0.17.1 Setup.'
                }
            }

            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $fixture.databasePath) | Out-Null
            $storedDatabasePath = [System.IO.Path]::GetRelativePath($fixture.dataRoot, $fixture.databasePath)
            $storageConfig = [ordered]@{
                databasePath = $storedDatabasePath
                rememberPassword = $false
            } | ConvertTo-Json
            [System.IO.File]::WriteAllText(
                (Join-Path $fixture.dataRoot 'storage.json'),
                $storageConfig,
                [System.Text.UTF8Encoding]::new($false))
            $fixture.source = if ($fixture.isPortable) { 'Portable' } else { 'CommandLine' }
            Invoke-QualificationTest -Mode seed `
                -DataRoot $fixture.dataRoot `
                -Label "seed-failed-apply-$($fixture.name)" `
                -DatabasePath $fixture.databasePath `
                -SourceName $fixture.source
            Invoke-QualificationTest -Mode verify `
                -DataRoot $fixture.dataRoot `
                -Label "verify-before-failed-apply-$($fixture.name)" `
                -DatabasePath $fixture.databasePath `
                -SourceName $fixture.source
            $databasePath = $fixture.databasePath
            if (-not (Test-Path $databasePath -PathType Leaf)) {
                throw "The shipping storage-service fixture did not create its synthetic library: $databasePath"
            }
            $fixture.databaseSha256Before = (Get-RollbackFileEvidence $databasePath).sha256
            $fixture.databaseSnapshotBefore = Get-RollbackDatabaseSnapshot $databasePath

            $settingsPath = Join-Path $fixture.dataRoot 'settings.json'
            $windowPath = Join-Path $fixture.dataRoot 'window.json'
            $tokenPath = Join-Path $fixture.dataRoot 'copilot-token.bin'
            $logPath = Join-Path $fixture.dataRoot 'logs\clipthrough.log'
            New-Item -ItemType Directory -Force -Path (Split-Path -Parent $logPath) | Out-Null
            [System.IO.File]::WriteAllText($settingsPath, '{}', [System.Text.UTF8Encoding]::new($false))
            [System.IO.File]::WriteAllText(
                $windowPath,
                '{"x":0,"y":0,"width":640,"height":480,"maximized":false}',
                [System.Text.UTF8Encoding]::new($false))
            [System.IO.File]::WriteAllBytes(
                $tokenPath,
                [System.Text.Encoding]::UTF8.GetBytes("SYNTHETIC QUALIFICATION DATA - NOT A TOKEN - $($fixture.name)"))
            [System.IO.File]::WriteAllText(
                $logPath,
                "Synthetic hosted qualification log sentinel for $($fixture.name).",
                [System.Text.UTF8Encoding]::new($false))
            if (-not (Test-Path (Join-Path $fixture.dataRoot 'storage.json') -PathType Leaf)) {
                throw 'The shipping storage service did not create storage.json for the synthetic data root.'
            }

            $fixture.oldManifest = Read-VersionManifest $fixture.root
            $fixture.oldManifestSha256 = (Get-FileHash -Algorithm SHA256 (Join-Path $fixture.root 'current\sq.version')).Hash.ToLowerInvariant()
            $fixture.oldPayloadSha256 = (Get-FileHash -Algorithm SHA256 (Join-Path $fixture.root 'current\Clipthrough.dll')).Hash.ToLowerInvariant()
            $fixture.dataRootSnapshotBefore = Get-RollbackDataRootSnapshot $fixture.dataRoot
            $fixture.recovery.layout = [ordered]@{
                installationRoot = $fixture.root
                syntheticAppDataRoot = $fixture.dataRoot
                syntheticLibraryPath = $fixture.databasePath
                currentUserDefaultAppDataRoot = $fixture.defaultDataRoot
                currentUserDefaultLibraryPath = $fixture.defaultDatabasePath
                simulatedDefaultLayoutAppDataRoot = $fixture.simulatedDefaultDataRoot
                simulatedDefaultLayoutLibraryPath = $fixture.simulatedDefaultDatabasePath
                defaultLayoutSimulation = $fixture.name -eq 'installed-default-layout'
                appDataRootInsideInstallRoot = [System.IO.Path]::GetFullPath($fixture.dataRoot).Equals(
                    [System.IO.Path]::GetFullPath($fixture.root),
                    [StringComparison]::OrdinalIgnoreCase) -or $fixture.dataRoot.StartsWith(
                        [System.IO.Path]::GetFullPath($fixture.root).TrimEnd('\') + '\',
                        [StringComparison]::OrdinalIgnoreCase)
            }
            $fixture.portableFlagSha256Before = if ($fixture.isPortable) {
                (Get-FileHash -Algorithm SHA256 (Join-Path $fixture.root 'portable.flag')).Hash.ToLowerInvariant()
            } else { $null }
            $fixture.prepared = $true
        }
        catch {
            $fixture.preparationError = $_.Exception.ToString()
        }
    }

    foreach ($fixture in $fixtures) {
        if (-not $fixture.prepared) {
            $fixture.failure.qualified = $false
            $fixture.failure.error = "Fixture preparation failed: $($fixture.preparationError)"
            continue
        }

        try {
            try {
                $fixture.failure.installationInventoryBeforeApply = @(
                    Get-RollbackInstallationInventory -Root $fixture.root -ExcludedRoots @($fixtures | ForEach-Object root)
                )
            }
            catch {
                $fixture.failure.installationInventoryError = "Before apply: $($_.Exception.Message)"
            }
            $bootstrapDirectory = Join-Path $fixture.root 'current'
            Copy-Item -Path (Join-Path $harness '*') -Destination $bootstrapDirectory -Force
            $stageStdout = Join-Path $evidenceDirectory "rollback-$($fixture.name)-bootstrap.stdout.log"
            $stageStderr = Join-Path $evidenceDirectory "rollback-$($fixture.name)-bootstrap.stderr.log"
            Invoke-CheckedProcess -FilePath (Join-Path $bootstrapDirectory 'FeedApplyHarness.exe') `
                -ArgumentList @($feed, '--stage-only') `
                -WorkingDirectory $bootstrapDirectory `
                -StdoutPath $stageStdout `
                -StderrPath $stageStderr `
                -TimeoutSeconds 900 | Out-Null
            $actualUpdaterHash = (Get-FileHash -Algorithm SHA256 (Join-Path $fixture.root 'Update.exe')).Hash.ToLowerInvariant()
            $fixture.failure.bootstrappedUpdaterSha256 = $actualUpdaterHash
            $fixture.failure.bootstrappedUpdaterMatchesPackage = $actualUpdaterHash -eq $expectedUpdaterHash

            $updaterLog = Join-Path $evidenceDirectory "failed-replacement-$($fixture.name).log"
            $updaterStdout = Join-Path $evidenceDirectory "failed-replacement-$($fixture.name).stdout.log"
            $updaterStderr = Join-Path $evidenceDirectory "failed-replacement-$($fixture.name).stderr.log"
            $fixture.failure.updaterLog = $updaterLog
            $updater = Start-Process -FilePath (Join-Path $fixture.root 'Update.exe') `
                -ArgumentList @('--verbose', '--log', "`"$updaterLog`"", 'apply', '--silent', '--norestart', '--package', "`"$packagePath`"") `
                -WorkingDirectory $fixture.root `
                -PassThru `
                -RedirectStandardOutput $updaterStdout `
                -RedirectStandardError $updaterStderr

            $packagesDirectory = Join-Path $fixture.root 'packages'
            New-Item -ItemType Directory -Force -Path $packagesDirectory | Out-Null
            $lockedStream = $null
            $lockHashMatches = $false
            $lockFile = $null
            $lockedPayloadHash = $null
            $timedOut = $false
            $deadline = [DateTime]::UtcNow.AddMinutes(3)
            try {
                while ([DateTime]::UtcNow -lt $deadline) {
                    $updater.Refresh()
                    if ($updater.HasExited) { break }

                    if ($null -eq $lockedStream) {
                        foreach ($candidateFile in Get-ChildItem -LiteralPath $packagesDirectory -Filter 'Clipthrough.dll' -File -Recurse -ErrorAction SilentlyContinue) {
                            $extractedManifest = Join-Path $candidateFile.DirectoryName 'sq.version'
                            if (-not (Test-Path $extractedManifest -PathType Leaf) `
                                -or (Get-FileHash -Algorithm SHA256 $extractedManifest).Hash.ToLowerInvariant() -ne $candidateManifestHash) {
                                continue
                            }
                            try {
                                # Hold the exact package payload without delete sharing until the updater exits.
                                $stream = [System.IO.File]::Open(
                                    $candidateFile.FullName,
                                    [System.IO.FileMode]::Open,
                                    [System.IO.FileAccess]::Read,
                                    [System.IO.FileShare]::ReadWrite)
                                $streamHash = (Get-FileHash -Algorithm SHA256 $candidateFile.FullName).Hash.ToLowerInvariant()
                                if ($stream.Length -gt 0 -and $streamHash -eq $candidatePayloadHash) {
                                    $lockedStream = $stream
                                    $lockHashMatches = $true
                                    $lockFile = $candidateFile.FullName
                                    $lockedPayloadHash = $streamHash
                                    break
                                }
                                $stream.Dispose()
                            }
                            catch [System.IO.IOException] {
                            }
                            catch [System.UnauthorizedAccessException] {
                            }
                        }
                    }
                    Start-Sleep -Milliseconds 100
                }

                $updater.Refresh()
                if (-not $updater.HasExited) {
                    $timedOut = $true
                    Stop-Process -Id $updater.Id -Force
                }
                else {
                    [void]$updater.WaitForExit()
                    $updater.Refresh()
                    $fixture.failure.updateExitCode = $updater.ExitCode
                }
                $fixture.failure.payloadLockHeldThroughUpdaterExit = $null -ne $lockedStream -and -not $timedOut
                if ($fixture.failure.payloadLockHeldThroughUpdaterExit -and (Test-Path $updaterLog -PathType Leaf)) {
                    $lockedLogText = (Get-Content -Raw -LiteralPath $updaterLog) `
                        + (Get-Content -Raw -LiteralPath $updaterStdout -ErrorAction SilentlyContinue) `
                        + (Get-Content -Raw -LiteralPath $updaterStderr -ErrorAction SilentlyContinue)
                    $fixture.failure.replacementFailureObservedWhileLocked = (
                        $lockedLogText.Contains('Replacing current dir', [StringComparison]::OrdinalIgnoreCase) `
                            -and (
                                $lockedLogText.Contains('Failed to replace current dir', [StringComparison]::OrdinalIgnoreCase) `
                                    -or $lockedLogText.Contains('Unable to complete the update, and the app was left in a broken state.', [StringComparison]::OrdinalIgnoreCase)))
                }
            }
            finally {
                if ($null -ne $lockedStream) { $lockedStream.Dispose() }
            }

            $logText = (Get-Content -Raw -LiteralPath $updaterLog -ErrorAction SilentlyContinue) `
                + (Get-Content -Raw -LiteralPath $updaterStdout -ErrorAction SilentlyContinue) `
                + (Get-Content -Raw -LiteralPath $updaterStderr -ErrorAction SilentlyContinue)
            $fixture.failure.newExtractionLockedFile = $lockFile
            $fixture.failure.newExtractionPayloadSha256 = $lockedPayloadHash
            $fixture.failure.newExtractionPayloadMatchesPackage = $lockHashMatches
            $fixture.failure.deleteSharingDenied = $lockHashMatches -and ($null -ne $lockedStream)
            $fixture.failure.fileShareMode = 'ReadWrite without Delete'
            $fixture.failure.applyLogReachedReplacement = $logText.Contains('Replacing current dir', [StringComparison]::OrdinalIgnoreCase)
            $fixture.failure.applyLogReportedReplacementFailure = (
                $logText.Contains('Failed to replace current dir', [StringComparison]::OrdinalIgnoreCase) `
                    -or $logText.Contains('Unable to complete the update, and the app was left in a broken state.', [StringComparison]::OrdinalIgnoreCase))
            $fixture.failure.applyLogReportedBrokenState = $logText.Contains('app was left in a broken state', [StringComparison]::OrdinalIgnoreCase)
            $fixture.failure.currentManifestAbsent = -not (Test-Path (Join-Path $fixture.root 'current\sq.version') -PathType Leaf)
            $fixture.failure.currentPayloadAbsent = -not (Test-Path (Join-Path $fixture.root 'current\Clipthrough.dll') -PathType Leaf)
            if (-not $fixture.failure.currentManifestAbsent) {
                $fixture.failure.oldManifestRestoredToCurrent = (Read-VersionManifest $fixture.root) -eq $fixture.oldManifest
            }
            if (-not $fixture.failure.currentPayloadAbsent) {
                $fixture.failure.oldPayloadRestoredToCurrent = (
                    (Get-FileHash -Algorithm SHA256 (Join-Path $fixture.root 'current\Clipthrough.dll')).Hash.ToLowerInvariant() `
                        -eq $fixture.oldPayloadSha256)
            }
            $fixture.failure.replacementFailureObserved = (
                $fixture.failure.bootstrappedUpdaterMatchesPackage `
                    -and $fixture.failure.deleteSharingDenied `
                    -and $fixture.failure.payloadLockHeldThroughUpdaterExit `
                    -and $fixture.failure.replacementFailureObservedWhileLocked `
                    -and $fixture.failure.newExtractionPayloadMatchesPackage `
                    -and $fixture.failure.applyLogReachedReplacement `
                    -and $fixture.failure.applyLogReportedReplacementFailure `
                    -and $fixture.failure.applyLogReportedBrokenState `
                    -and $null -ne $fixture.failure.updateExitCode `
                    -and $fixture.failure.updateExitCode -ne 0 `
                    -and $fixture.failure.currentManifestAbsent `
                    -and $fixture.failure.currentPayloadAbsent)

            $databaseHashAfterApply = if (Test-Path $fixture.databasePath -PathType Leaf) {
                (Get-RollbackFileEvidence $fixture.databasePath).sha256
            } else { $null }
            $fixture.failure.databaseSha256AfterApply = $databaseHashAfterApply
            $fixture.failure.databaseSnapshotAfterApply = Get-RollbackDatabaseSnapshot $fixture.databasePath
            $fixture.failure.databaseByteIdenticalAfterApply = Test-RollbackDatabaseSnapshotMatch `
                -Expected $fixture.databaseSnapshotBefore `
                -Actual $fixture.failure.databaseSnapshotAfterApply
            try {
                $verifyLabel = "verify-after-failed-apply-$($fixture.name)"
                Invoke-QualificationTest -Mode verify `
                    -DataRoot $fixture.dataRoot `
                    -Label $verifyLabel `
                    -DatabasePath $fixture.databasePath `
                    -SourceName CommandLine
                $fixture.failure.databaseRecordsReadableAfterApply = $true
            }
            catch {
                $fixture.failure.databaseRecordsReadableAfterApply = $false
                $fixture.failure.databaseReadFailure = $_.Exception.ToString()
            }
            $fixture.failure.timedOut = $timedOut
            try {
                $fixture.failure.dataRootSnapshotAfterApply = Get-RollbackDataRootSnapshot $fixture.dataRoot
                $fixture.failure.rootFileChangesAfterApply = Get-RollbackDataRootChanges `
                    -Before $fixture.dataRootSnapshotBefore `
                    -After $fixture.failure.dataRootSnapshotAfterApply
                $fixture.failure.installationInventoryAfterApply = @(
                    Get-RollbackInstallationInventory -Root $fixture.root -ExcludedRoots @($fixtures | ForEach-Object root)
                )
                $beforePaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($entry in $fixture.failure.installationInventoryBeforeApply) { [void]$beforePaths.Add($entry.path) }
                if ([string]::IsNullOrWhiteSpace($fixture.failure.installationInventoryError)) {
                    $fixture.failure.newSiblingDirectoriesAfterApply = @(
                        $fixture.failure.installationInventoryAfterApply |
                            Where-Object { $_.path -ne [System.IO.Path]::GetFullPath($fixture.root) -and -not $beforePaths.Contains($_.path) }
                    )
                }
            }
            catch {
                $fixture.failure.installationInventoryError = "After apply: $($_.Exception.Message)"
            }
        }
        catch {
            $fixture.failure.error = $_.Exception.ToString()
            $fixture.failure.replacementFailureObserved = $false
        }
    }

    foreach ($fixture in $fixtures) {
        if (-not $fixture.prepared) {
            $fixture.recovery.error = "Recovery was not attempted because fixture preparation failed: $($fixture.preparationError)"
            continue
        }
        try {
            try {
                $fixture.recovery.installationInventoryBeforeRecovery = @(
                    Get-RollbackInstallationInventory -Root $fixture.root -ExcludedRoots @($fixtures | ForEach-Object root)
                )
            }
            catch {
                $fixture.recovery.installationInventoryError = "Before recovery: $($_.Exception.Message)"
            }
            $backupPath = Join-Path $workspace "recovery-backups\$($fixture.name)"
            $fixture.recovery.recoveryBackup = Save-RollbackRecoveryBackup `
                -InstallationRoot $fixture.root `
                -DataRoot $fixture.dataRoot `
                -DatabasePath $fixture.databasePath `
                -BackupRoot $backupPath
            if ($fixture.isPortable) {
                Expand-Archive -LiteralPath $candidatePortable[0].FullName -DestinationPath $fixture.root -Force
                if (-not (Test-Path (Join-Path $fixture.root 'portable.flag') -PathType Leaf) `
                    -or -not (Test-Path (Join-Path $fixture.root '.portable') -PathType Leaf)) {
                    throw 'Portable overlay removed a required portable marker.'
                }
                $fixture.recovery.method = 'overlay candidate portable ZIP onto the broken portable installation'
            }
            else {
                $setupStdout = Join-Path $evidenceDirectory 'rollback-candidate-setup.stdout.log'
                $setupStderr = Join-Path $evidenceDirectory 'rollback-candidate-setup.stderr.log'
                $priorIds = @(Get-Process -Name Clipthrough -ErrorAction SilentlyContinue | ForEach-Object Id)
                $candidateSetupProcess = Start-Process -FilePath $candidateSetup[0].FullName `
                    -ArgumentList @('--silent', '--installto', "`"$($fixture.root)`"") `
                    -PassThru `
                    -RedirectStandardOutput $setupStdout `
                    -RedirectStandardError $setupStderr
                if (-not $candidateSetupProcess.WaitForExit(600000)) {
                    Stop-Process -Id $candidateSetupProcess.Id -Force
                    throw 'Candidate Setup recovery exceeded ten minutes.'
                }
                $candidateSetupProcess.Refresh()
                $fixture.recovery.setupExitCode = $candidateSetupProcess.ExitCode
                if ($candidateSetupProcess.ExitCode -ne 0) {
                    throw "Candidate Setup recovery exited with $($candidateSetupProcess.ExitCode)."
                }
                Stop-NewClipthroughProcesses -PriorIds $priorIds
                $fixture.recovery.method = 'real candidate Setup.exe with --silent --installto and no EXEARGS'
            }

            try {
                $fixture.recovery.installationInventoryAfterRecovery = @(
                    Get-RollbackInstallationInventory -Root $fixture.root -ExcludedRoots @($fixtures | ForEach-Object root)
                )
                $beforePaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($entry in $fixture.recovery.installationInventoryBeforeRecovery) { [void]$beforePaths.Add($entry.path) }
                $newSiblingDirectories = if ([string]::IsNullOrWhiteSpace($fixture.recovery.installationInventoryError)) {
                    @(
                        $fixture.recovery.installationInventoryAfterRecovery |
                            Where-Object { $_.path -ne [System.IO.Path]::GetFullPath($fixture.root) -and -not $beforePaths.Contains($_.path) }
                    )
                } else { @() }
                $fixture.recovery.newSiblingDirectoriesAfterRecovery = @(
                    foreach ($entry in $newSiblingDirectories) {
                        $fileByName = @{}
                        foreach ($file in $entry.files) { $fileByName[$file.name] = $file }
                        $matchedSentinels = @(
                            foreach ($prior in $fixture.dataRootSnapshotBefore) {
                                if ($prior.exists -and $fileByName.ContainsKey($prior.name) `
                                    -and $fileByName[$prior.name].exists `
                                    -and $fileByName[$prior.name].sha256 -eq $prior.sha256) {
                                    $prior.name
                                }
                            }
                        )
                        $database = $fileByName['ClipthroughData\clipthrough.db']
                        if ($database.exists -and $database.sha256 -eq $fixture.databaseSha256Before) {
                            $matchedSentinels += 'ClipthroughData\clipthrough.db'
                        }
                        [ordered]@{
                            path = $entry.path
                            files = $entry.files
                            matchingPreApplySentinels = $matchedSentinels
                            containsByteIdenticalSyntheticLibrary = $database.exists -and $database.sha256 -eq $fixture.databaseSha256Before
                        }
                    }
                )
                $fixture.recovery.renamedOldInstallationRootsAfterRecovery = @(
                    $fixture.recovery.newSiblingDirectoriesAfterRecovery |
                        Where-Object { $_.containsByteIdenticalSyntheticLibrary -and @($_.matchingPreApplySentinels).Count -gt 0 }
                )
            }
            catch {
                $fixture.recovery.installationInventoryError = "After recovery: $($_.Exception.Message)"
            }

            $fixture.recovery.manifestAfterRecovery = Read-VersionManifest $fixture.root
            $fixture.recovery.manifestIsCandidate = $fixture.recovery.manifestAfterRecovery -eq $candidateVersion
            if (-not $fixture.recovery.manifestIsCandidate) {
                throw "Recovery produced version $($fixture.recovery.manifestAfterRecovery), expected $candidateVersion."
            }
            $fixture.recovery.manifestSha256 = (Get-FileHash -Algorithm SHA256 (Join-Path $fixture.root 'current\sq.version')).Hash.ToLowerInvariant()
            $fixture.recovery.payloadSha256 = (Get-FileHash -Algorithm SHA256 (Join-Path $fixture.root 'current\Clipthrough.dll')).Hash.ToLowerInvariant()
            $databaseHashImmediatelyAfterRepair = if (Test-Path $fixture.databasePath -PathType Leaf) {
                (Get-RollbackFileEvidence $fixture.databasePath).sha256
            } else { $null }
            $fixture.recovery.databaseSha256ImmediatelyAfterSetupOrOverlay = $databaseHashImmediatelyAfterRepair
            $fixture.recovery.databaseSnapshotImmediatelyAfterSetupOrOverlay = Get-RollbackDatabaseSnapshot $fixture.databasePath
            $fixture.recovery.databaseByteIdenticalImmediatelyAfterSetupOrOverlay = `
                Test-RollbackDatabaseSnapshotMatch `
                    -Expected $fixture.databaseSnapshotBefore `
                    -Actual $fixture.recovery.databaseSnapshotImmediatelyAfterSetupOrOverlay
            $fixture.recovery.rootFilesAfterRecovery = Get-RollbackDataRootSnapshot $fixture.dataRoot
            $fixture.recovery.rootFileChanges = Get-RollbackDataRootChanges `
                -Before $fixture.dataRootSnapshotBefore `
                -After $fixture.recovery.rootFilesAfterRecovery
            $storageConfigPath = Join-Path $fixture.dataRoot 'storage.json'
            $fixture.recovery.storageConfigSha256BeforeRepair = if (Test-Path $storageConfigPath -PathType Leaf) {
                (Get-RollbackFileEvidence $storageConfigPath).sha256
            } else { $null }
            $storageConfigValidForLibrary = $false
            if (Test-Path $storageConfigPath -PathType Leaf) {
                try {
                    $storedOptions = Get-Content -Raw -LiteralPath $storageConfigPath | ConvertFrom-Json
                    $storedPath = [System.IO.Path]::GetFullPath([string]$storedOptions.databasePath, $fixture.dataRoot)
                    $storageConfigValidForLibrary = $storedPath.Equals(
                        [System.IO.Path]::GetFullPath($fixture.databasePath),
                        [StringComparison]::OrdinalIgnoreCase)
                }
                catch {
                    $fixture.recovery.storageConfigReadFailure = $_.Exception.ToString()
                }
            }
            $fixture.recovery.storageConfigRestorationRequired = -not $storageConfigValidForLibrary
            $fixture.recovery.storageConfigSha256AfterSetupOrOverlay = if (Test-Path $storageConfigPath -PathType Leaf) {
                (Get-RollbackFileEvidence $storageConfigPath).sha256
            } else { $null }
            $fixture.recovery.storageConfigStillPointsToPreservedLibrary = $storageConfigValidForLibrary
            $fixture.recovery.userVisibleDataReset = @(
                $fixture.recovery.rootFileChanges |
                    Where-Object { $_.name -in @('settings.json', 'storage.json', 'window.json') -and $_.state -ne 'unchanged' }
            ).Count -gt 0
            if ($fixture.isPortable) {
                $flagAfter = (Get-FileHash -Algorithm SHA256 (Join-Path $fixture.root 'portable.flag')).Hash.ToLowerInvariant()
                $fixture.recovery.portableFlagSha256After = $flagAfter
                $fixture.recovery.portableFlagPreserved = $flagAfter -eq $fixture.portableFlagSha256Before
            }

            Restore-RollbackRecoveryBackup `
                -DataRoot $fixture.dataRoot `
                -DatabasePath $fixture.databasePath `
                -BackupRoot $backupPath
            $fixture.recovery.rootFilesAfterRestore = Get-RollbackDataRootSnapshot $fixture.dataRoot
            $fixture.recovery.rootFilesRestoredByteIdentically = @(
                $fixture.recovery.rootFilesAfterRestore | Where-Object {
                    $before = $fixture.dataRootSnapshotBefore | Where-Object name -eq $_.name | Select-Object -First 1
                    -not $before.exists -or -not $_.exists -or $_.sha256 -ne $before.sha256
                }
            ).Count -eq 0
            $fixture.recovery.databaseSnapshotAfterRestore = Get-RollbackDatabaseSnapshot $fixture.databasePath
            $databaseHashAfterRestore = if ($fixture.recovery.databaseSnapshotAfterRestore[0].exists) {
                $fixture.recovery.databaseSnapshotAfterRestore[0].sha256
            } else { $null }
            $fixture.recovery.databaseSha256AfterRestore = $databaseHashAfterRestore
            $fixture.recovery.databaseByteIdenticalAfterRestore = Test-RollbackDatabaseSnapshotMatch `
                -Expected $fixture.databaseSnapshotBefore `
                -Actual $fixture.recovery.databaseSnapshotAfterRestore
            $restoredStorageConfig = Get-Content -Raw -LiteralPath (Join-Path $fixture.dataRoot 'storage.json') | ConvertFrom-Json
            $restoredDatabasePath = [System.IO.Path]::GetFullPath([string]$restoredStorageConfig.databasePath, $fixture.dataRoot)
            $fixture.recovery.storageConfigStillPointsToPreservedLibrary = $restoredDatabasePath.Equals(
                [System.IO.Path]::GetFullPath($fixture.databasePath),
                [StringComparison]::OrdinalIgnoreCase)
            if (-not $fixture.recovery.rootFilesRestoredByteIdentically `
                -or -not $fixture.recovery.databaseByteIdenticalAfterRestore `
                -or -not $fixture.recovery.storageConfigStillPointsToPreservedLibrary) {
                throw 'Backup restore did not recover the original root files, configured library path, and byte-identical database.'
            }
            try {
                Invoke-QualificationTest -Mode verify `
                    -DataRoot $fixture.dataRoot `
                    -Label "verify-after-restored-backup-$($fixture.name)" `
                    -DatabasePath $fixture.databasePath `
                    -SourceName CommandLine
                $fixture.recovery.databaseRecordsReadableAfterRestore = $true
            }
            catch {
                $fixture.recovery.databaseRecordsReadableAfterRestore = $false
                $fixture.recovery.databaseReadFailureAfterRestore = $_.Exception.ToString()
            }
            if (-not $fixture.recovery.databaseRecordsReadableAfterRestore) {
                throw 'Restored backup database is not readable through the shipping storage services.'
            }

            $sourceName = $fixture.source
            $fixture.recovery.appStart = Invoke-RollbackCandidateAppStart `
                -Root $fixture.root `
                -DataRoot $fixture.dataRoot `
                -SourceName $sourceName `
                -Label "rollback-$($fixture.name)-recovery"
            try {
                Invoke-QualificationTest -Mode verify `
                    -DataRoot $fixture.dataRoot `
                    -Label "verify-after-recovery-$($fixture.name)" `
                    -DatabasePath $fixture.databasePath `
                    -SourceName CommandLine
                $fixture.recovery.databaseRecordsReadableAfterRecovery = $true
            }
            catch {
                $fixture.recovery.databaseRecordsReadableAfterRecovery = $false
                $fixture.recovery.databaseReadFailure = $_.Exception.ToString()
            }
            $fixture.recovery.databaseSnapshotAfterAppStart = Get-RollbackDatabaseSnapshot $fixture.databasePath
            $databaseHashAfterRecovery = if ($fixture.recovery.databaseSnapshotAfterAppStart[0].exists) {
                $fixture.recovery.databaseSnapshotAfterAppStart[0].sha256
            } else { $null }
            $fixture.recovery.databaseSha256AfterRecovery = $databaseHashAfterRecovery
            $fixture.recovery.databaseByteIdenticalAfterRecovery = Test-RollbackDatabaseSnapshotMatch `
                -Expected $fixture.databaseSnapshotBefore `
                -Actual $fixture.recovery.databaseSnapshotAfterAppStart

            $fixture.recovery.rootFilesAfterAppStart = Get-RollbackDataRootSnapshot $fixture.dataRoot
            $fixture.recovery.rootFileChangesAfterAppStart = Get-RollbackDataRootChanges `
                -Before $fixture.dataRootSnapshotBefore `
                -After $fixture.recovery.rootFilesAfterAppStart
            $fixture.recovery.installationInventoryAfterAppStart = @(
                Get-RollbackInstallationInventory -Root $fixture.root -ExcludedRoots @($fixtures | ForEach-Object root)
            )
            $afterRecoveryPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($entry in $fixture.recovery.installationInventoryAfterRecovery) { [void]$afterRecoveryPaths.Add($entry.path) }
            $fixture.recovery.newSiblingDirectoriesAfterAppStart = @(
                $fixture.recovery.installationInventoryAfterAppStart |
                    Where-Object { $_.path -ne [System.IO.Path]::GetFullPath($fixture.root) -and -not $afterRecoveryPaths.Contains($_.path) }
            )
        }
        catch {
            $fixture.recovery.error = $_.Exception.ToString()
            $fixture.recovery.recovered = $false
        }
        if ($fixture.recovery.manifestIsCandidate `
            -and [string]::IsNullOrWhiteSpace($fixture.recovery.error) `
            -and [string]::IsNullOrWhiteSpace($fixture.recovery.installationInventoryError) `
            -and $fixture.recovery.appStart.processStarted `
            -and $fixture.recovery.appStart.stillRunningAfterThreeSeconds `
            -and $fixture.recovery.databaseRecordsReadableAfterRecovery `
            -and $fixture.recovery.databaseRecordsReadableAfterRestore `
            -and $fixture.recovery.databaseByteIdenticalAfterRestore `
            -and $fixture.recovery.rootFilesRestoredByteIdentically `
            -and $fixture.recovery.storageConfigStillPointsToPreservedLibrary `
            -and (-not $fixture.isPortable -or $fixture.recovery.portableFlagPreserved)) {
            $fixture.recovery.recovered = $true
        }
        else {
            $fixture.recovery.recovered = $false
        }
    }

    $expectedFailureEverywhere = @($fixtures | Where-Object { -not $_.failure.replacementFailureObserved }).Count -eq 0
    $databasePreservedEverywhere = @($fixtures | Where-Object {
        -not $_.failure.databaseByteIdenticalAfterApply -or -not $_.failure.databaseRecordsReadableAfterApply
    }).Count -eq 0
    $recoveryMeasuredEverywhere = @($fixtures | Where-Object { -not $_.recovery.recovered }).Count -eq 0
    $allFixturesPassed = $expectedFailureEverywhere -and $databasePreservedEverywhere -and $recoveryMeasuredEverywhere
    $unexpectedRollback = @($fixtures | Where-Object {
        $_.failure.payloadLockHeldThroughUpdaterExit `
            -and $_.failure.applyLogReachedReplacement `
            -and $_.failure.applyLogReportedReplacementFailure `
            -and $_.failure.oldManifestRestoredToCurrent `
            -and $_.failure.oldPayloadRestoredToCurrent
    }).Count -gt 0
    $contractMismatchDetail = if ($unexpectedRollback) {
        'The known limitation no longer reproduces; re-evaluate real rollback behavior.'
    } else {
        'The exact expected failure was not established; investigate the recorded fixture failure.'
    }
    $resetSummary = ($fixtures | ForEach-Object {
        $changed = @(
            $_.recovery.rootFileChanges |
                Where-Object { $_.name -in @('settings.json', 'storage.json', 'window.json') -and $_.state -ne 'unchanged' } |
                ForEach-Object { "$($_.name)=$($_.state)" }
        )
        $storageRepair = "storage config still points to preserved library=$($_.recovery.storageConfigStillPointsToPreservedLibrary); restoration required=$($_.recovery.storageConfigRestorationRequired)"
        if ($changed.Count) {
            "$($_.name) user-visible file changes: $($changed -join ', '); $storageRepair"
        }
        else {
            "$($_.name) settings/storage/window unchanged; $storageRepair"
        }
    }) -join '; '
    $rollbackEvidence = [ordered]@{
        disposition = 'Accepted characterization of the Velopack 1.2.0 apply-time replacement failure; this test does not claim rollback.'
        velopackStableVersion = '1.2.0'
        knownFailure = 'The replacement rename fails after current is moved aside; 1.2.0 logs that the app was left in a broken state and does not restore current.'
        upstreamIssue = 'https://github.com/velopack/velopack/issues/1086'
        upstreamFix = 'https://github.com/velopack/velopack/pull/1087'
        fixPrerelease = '1.3.39-g8d2844f'
        recoveryGuidanceForReleaseNotes = 'Before recovery, copy settings.json, storage.json, window.json, copilot-token.bin, logs, and the complete library database outside the installation/data root. Run candidate Setup for installed deployments or overlay the candidate portable ZIP while retaining .portable/portable.flag, then restore the saved files and database to their original paths and verify the configured database and records before resuming use.'
        syntheticCopilotTokenNote = 'copilot-token.bin contains dummy synthetic bytes only; byte changes do not prove authentication or sign-in state.'
        candidatePackage = $packagePath
        candidatePackageSha256 = $candidatePackageHash
        candidateManifestSha256 = $candidateManifestHash
        candidatePayloadSha256 = $candidatePayloadHash
        candidateSetup = $candidateSetup[0].FullName
        candidateSetupSha256 = (Get-FileHash -Algorithm SHA256 $candidateSetup[0].FullName).Hash.ToLowerInvariant()
        candidatePortableZip = $candidatePortable[0].FullName
        candidatePortableZipSha256 = (Get-FileHash -Algorithm SHA256 $candidatePortable[0].FullName).Hash.ToLowerInvariant()
        expectedFailureEverywhere = $expectedFailureEverywhere
        databasePreservedEverywhere = $databasePreservedEverywhere
        recoveryMeasuredEverywhere = $recoveryMeasuredEverywhere
        userVisibleDataResetSummary = $resetSummary
        fixtures = @($fixtures)
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $evidenceDirectory 'failed-apply-rollback.json'),
        ($rollbackEvidence | ConvertTo-Json -Depth 12),
        [System.Text.UTF8Encoding]::new($false))

    if ($expectedFailureEverywhere) {
        Set-Gate 'failed_apply_expected_broken_state_confirmed' 'passed' `
            'Both installed and portable fixtures reached the exact Velopack 1.2.0 replacement failure: hash-matched candidate DLL locked through updater exit, replacement failure and broken-state log, nonzero exit, and old manifest/payload absent from current.'
    }
    else {
        Set-Gate 'failed_apply_expected_broken_state_confirmed' 'failed' `
            "$contractMismatchDetail Both fresh fixtures must show the exact accepted Velopack 1.2.0 broken-state failure; inspect failed-apply-rollback.json."
    }
    if ($databasePreservedEverywhere) {
        Set-Gate 'failed_apply_seeded_database_preserved' 'passed' `
            'Both fixtures kept the exact seeded library SHA-256 after failed apply, and the shipping storage service read both synthetic clip IDs.'
    }
    else {
        Set-Gate 'failed_apply_seeded_database_preserved' 'failed' `
            'A fixture changed or could not read its seeded library after failed apply; inspect failed-apply-rollback.json.'
    }
    if ($allFixturesPassed) {
        Set-Gate 'accepted_stable12_apply_failure_and_recovery' 'passed' `
            "Accepted 1.2.0 apply-time broken-state characterization and measured recovery passed for both fixture types. $resetSummary. This test does not claim automatic rollback; see Velopack #1086/#1087 and prerelease 1.3.39."
    }
    else {
        Set-Gate 'accepted_stable12_apply_failure_and_recovery' 'failed' `
            "The exact accepted failure, data preservation, or measured recovery did not pass for every fixture. $contractMismatchDetail $resetSummary Inspect failed-apply-rollback.json."
    }
}

$requiredGateFailure = $false
$fatalError = $null
try {
    New-Item -ItemType Directory -Force -Path $downloadDirectory, $installRoot, $chosenDataRoot | Out-Null
    $desktopProbe = Get-HostedInputDesktopProbe
    if ($desktopProbe.StartsWith('passed:', [StringComparison]::Ordinal)) {
        Set-Gate 'hosted_input_desktop' 'passed' $desktopProbe
    }
    else {
        Set-Gate 'hosted_input_desktop' 'blocked' $desktopProbe
    }
    Set-Gate 'interactive_clipboard_paste_hotkey_smoke' 'blocked' 'No desktop interaction harness is included. No clipboard, foreground paste, image, detail-view or hotkey assertion is counted.'
    Set-Gate 'normal_ui_storage_and_record_visibility' 'blocked' 'The app restart and resolved data-root trace are observed, and its configured database is checked with the shipping storage service, but no normal-window UI assertion proves visible clip rows.'
    Set-Gate 'rich_paste_target_content' 'blocked' 'No target application or paste interaction is driven; MIME format alone is not treated as proof of pasted content.'
    Set-Gate 'velopack_prerequisite_below_floor' 'blocked' 'The isolated runtime test below exercises the .NET host only; the updater prerequisite installer cannot be safely isolated from machine runtime discovery on this hosted runner.'
    Set-Gate 'physical_monitor_gpu_ime' 'blocked' 'GitHub-hosted VM evidence cannot qualify a physical monitor, GPU driver, hardware rendering path or IME.'

    if (Get-Process -Name Clipthrough -ErrorAction SilentlyContinue) {
        throw 'A Clipthrough process already exists on the allegedly fresh hosted runner; refusing to alter it.'
    }

    $setupPath = Join-Path $downloadDirectory 'Clipthrough-v0.17.1-Setup.exe'
    $fullPackagePath = Join-Path $downloadDirectory 'Clipthrough-0.17.1-full.nupkg'
    $portablePath = Join-Path $downloadDirectory 'Clipthrough-v0.17.1-Portable.zip'
    & curl.exe --fail --location --retry 3 --silent --show-error --output $setupPath $setupUrl
    if ($LASTEXITCODE -ne 0) { throw "Downloading the pinned v0.17.1 Setup failed with exit code $LASTEXITCODE." }
    & curl.exe --fail --location --retry 3 --silent --show-error --output $fullPackagePath $fullPackageUrl
    if ($LASTEXITCODE -ne 0) { throw "Downloading the pinned v0.17.1 full nupkg failed with exit code $LASTEXITCODE." }
    & curl.exe --fail --location --retry 3 --silent --show-error --output $portablePath $portableUrl
    if ($LASTEXITCODE -ne 0) { throw "Downloading the pinned v0.17.1 portable archive failed with exit code $LASTEXITCODE." }
    $setupHash = (Get-FileHash -Algorithm SHA256 $setupPath).Hash.ToLowerInvariant()
    $fullPackageHash = (Get-FileHash -Algorithm SHA256 $fullPackagePath).Hash.ToLowerInvariant()
    $portableHash = (Get-FileHash -Algorithm SHA256 $portablePath).Hash.ToLowerInvariant()
    if ($setupHash -ne $setupSha256 -or $fullPackageHash -ne $fullPackageSha256 -or $portableHash -ne $portableSha256) {
        Set-Gate 'old_setup_asset_integrity' 'failed' "Setup=$setupHash; full nupkg=$fullPackageHash; portable=$portableHash."
        throw 'The downloaded public v0.17.1 asset digest did not match verified release metadata.'
    }
    Set-Gate 'old_setup_asset_integrity' 'passed' "Verified v0.17.1 Setup, full nupkg and portable ZIP SHA-256 values against GitHub release metadata."

    $setupStdout = Join-Path $evidenceDirectory 'setup.stdout.log'
    $setupStderr = Join-Path $evidenceDirectory 'setup.stderr.log'
    $setupLog = Join-Path $evidenceDirectory 'setup-install.log'
    $setupProcess = Start-Process -FilePath $setupPath `
        -ArgumentList @('--silent', '--log', "`"$setupLog`"", '--installto', "`"$installRoot`"") `
        -PassThru `
        -RedirectStandardOutput $setupStdout `
        -RedirectStandardError $setupStderr
    if (-not $setupProcess.WaitForExit(600000)) {
        Stop-Process -Id $setupProcess.Id -Force
        throw 'The legacy Setup install exceeded ten minutes.'
    }
    $setupProcess.Refresh()
    if ($setupProcess.ExitCode -ne 0) { throw "Legacy Setup exited with $($setupProcess.ExitCode)." }
    $setupApps = @(Get-Process -Name Clipthrough -ErrorAction SilentlyContinue)
    foreach ($setupApp in $setupApps) {
        if (-not $setupApp.Path.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Setup started an app outside its isolated installation: $($setupApp.Path)"
        }
        Stop-Process -Id $setupApp.Id -Force
    }
    $oldManifest = Read-VersionManifest $installRoot
    if ($oldManifest -ne '0.17.1') { throw "The Setup installed unexpected version '$oldManifest'." }
    if (-not (Test-Path (Join-Path $installRoot 'Update.exe'))) { throw 'The Setup install has no Update.exe.' }
    if (-not (Test-Path $setupStdout) -or -not (Test-Path $setupStderr)) { throw 'Setup process logs were not captured.' }
    Set-Gate 'old_setup_install' 'passed' "Silent real Setup install exited 0; Update.exe and current/sq.version prove installed version $oldManifest."
    Assert-LegacyNupkgMapping -PackagePath $fullPackagePath -Root $installRoot
    Set-Gate 'old_package_payload_mapping' 'passed' 'Every lib/app entry in the hash-pinned old package byte-matches current; both explicit root aliases also match. Exact entry count and hashes are in legacy-package-mapping.json.'

    Invoke-QualificationTest -Mode seed -DataRoot $chosenDataRoot -Label 'seed-installed'
    Set-Gate 'synthetic_data_seed_and_read' 'passed' 'The shipping DatabaseInitializer and ClipStoreService seeded two isolated synthetic clips and read them back by id.'

    $installedUpdate = Invoke-FeedUpdate -Root $installRoot -DataRoot $chosenDataRoot
    Invoke-QualificationTest -Mode verify -DataRoot $chosenDataRoot -Label 'verify-installed-update'
    Set-Gate 'candidate_feed_download_and_apply' 'passed' "Velopack 1.2.0 SimpleWebSource consumed the generated stable feed over loopback HTTP, downloaded the full candidate and UpdateManager applied it through the real installed locator: $($installedUpdate.manifest). Feed transport was local fixture, not the production GitHub release endpoint."
    Set-Gate 'explicit_data_root_restart_survival' 'passed' "The actual post-update app restart logged CommandLine root $chosenDataRoot; the shipping store read the seeded IDs after the restart."
    Invoke-HostedUiQualification

    Expand-Archive -LiteralPath $portablePath -DestinationPath $portableRoot -Force
    if (-not (Test-Path (Join-Path $portableRoot '.portable')) -or -not (Test-Path (Join-Path $portableRoot 'Update.exe'))) {
        throw 'The pinned portable ZIP did not extract into a Velopack portable root.'
    }
    New-Item -ItemType File -Force -Path (Join-Path $portableRoot 'portable.flag') | Out-Null
    Invoke-QualificationTest -Mode seed -DataRoot $portableDataRoot -Label 'seed-portable'
    $portableUpdate = Invoke-FeedUpdate -Root $portableRoot -DataRoot $portableDataRoot -Portable
    Invoke-QualificationTest -Mode verify -DataRoot $portableDataRoot -Label 'verify-portable-update'
    $portableStoragePath = Join-Path $portableDataRoot 'storage.json'
    if (Test-Path $portableStoragePath) {
        $storage = Get-Content -Raw $portableStoragePath | ConvertFrom-Json
        if ([System.IO.Path]::IsPathRooted([string]$storage.databasePath)) {
            throw "Portable storage.json did not retain a relative database path: $($storage.databasePath)"
        }
    }
    elseif (-not (Test-Path (Join-Path $portableDataRoot 'clipthrough.db'))) {
        throw 'Portable storage.json is absent and the documented default database file is absent.'
    }
    Set-Gate 'portable_update_survival' 'passed' "The extracted v0.17.1 portable installation updated to $($portableUpdate.manifest), restarted with its portable marker, and the shipping store read both clips."

    $runtimeConfigPath = Join-Path $publish 'Clipthrough.runtimeconfig.json'
    $runtimeConfig = Get-Content -Raw $runtimeConfigPath | ConvertFrom-Json
    $runtimeVersion = [version]$runtimeConfig.runtimeOptions.framework.version
    if ($runtimeConfig.runtimeOptions.framework.name -ne 'Microsoft.NETCore.App' -or $runtimeVersion -lt $minimumRuntime) {
        throw "Published runtimeconfig requires $($runtimeConfig.runtimeOptions.framework.name) $runtimeVersion, below $minimumRuntime."
    }
    $runtimeFloor = Join-Path $workspace 'runtime-10.0.13'
    $dotnetInstallScript = Join-Path $downloadDirectory 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $dotnetInstallScript
    & pwsh -NoProfile -File $dotnetInstallScript -Runtime dotnet -Version 10.0.13 -InstallDir $runtimeFloor -NoPath
    if ($LASTEXITCODE -ne 0) { throw "Installing isolated .NET 10.0.13 runtime failed with $LASTEXITCODE." }
    $runtimeLog = Join-Path $evidenceDirectory 'runtime-floor.log'
    $runtimeError = Join-Path $evidenceDirectory 'runtime-floor.stderr.log'
    $runtimeExit = Invoke-CheckedProcess -FilePath (Join-Path $runtimeFloor 'dotnet.exe') `
        -ArgumentList @('exec', (Join-Path $publish 'Clipthrough.dll'), '--help') `
        -StdoutPath $runtimeLog `
        -StderrPath $runtimeError `
        -TimeoutSeconds 60 `
        -AllowAnyExitCode
    $runtimeText = (Get-Content -Raw $runtimeLog) + (Get-Content -Raw $runtimeError)
    if ($runtimeExit -eq 0 -or $runtimeText -notmatch 'Microsoft\.NETCore\.App' -or $runtimeText -notmatch '10\.0\.14') {
        throw 'The app host did not reject the isolated 10.0.13 runtime with the expected 10.0.14 floor diagnostic.'
    }
    Set-Gate 'runtime_floor' 'passed' "Published app requires Microsoft.NETCore.App $runtimeVersion and failed under isolated .NET 10.0.13 with the expected host diagnostic. This does not test Velopack prerequisite UX."

    $badPackage = Join-Path $workspace 'invalid-update.nupkg'
    [System.IO.File]::WriteAllText($badPackage, 'not a Velopack package', [System.Text.UTF8Encoding]::new($false))
    $beforeFailure = Read-VersionManifest $installRoot
    $updaterLog = Join-Path $evidenceDirectory 'failed-apply.log'
    $updaterError = Join-Path $evidenceDirectory 'failed-apply.stderr.log'
    $applyExit = Invoke-CheckedProcess -FilePath (Join-Path $installRoot 'Update.exe') `
        -ArgumentList @('apply', '--silent', '--norestart', '--package', $badPackage) `
        -StdoutPath $updaterLog `
        -StderrPath $updaterError `
        -TimeoutSeconds 180 `
        -AllowAnyExitCode
    if ($applyExit -eq 0) { throw 'Update.exe accepted a malformed package unexpectedly.' }
    $afterFailure = Read-VersionManifest $installRoot
    if ($afterFailure -ne $beforeFailure) { throw "Rejected package changed the installed version from $beforeFailure to $afterFailure." }
    Set-Gate 'failed_package_rejection_preserves_version' 'passed' "Update.exe rejected the malformed package with exit $applyExit and kept manifest version $beforeFailure."
    Invoke-FailedApplyRollback
}
catch {
    $fatalError = $_.Exception.ToString()
    Write-Host $fatalError -ForegroundColor Red
}
finally {
    Remove-Item Env:\CLIPTHROUGH_QUALIFICATION_DATA_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:\CLIPTHROUGH_QUALIFICATION_MODE -ErrorAction SilentlyContinue
    Remove-Item Env:\CLIPTHROUGH_QUALIFICATION_SOURCE -ErrorAction SilentlyContinue
    Remove-Item Env:\CLIPTHROUGH_QUALIFICATION_DATABASE_PATH -ErrorAction SilentlyContinue
    if ($fatalError) {
        foreach ($gate in $script:gates | Where-Object { $_.status -eq 'blocked' -and $_.detail -eq 'Not reached.' }) {
            $gate.detail = "Not reached because an earlier operation failed: $fatalError"
        }
    }
    $hasFailedGate = [bool]($script:gates | Where-Object { $_.status -eq 'failed' })
    $requiredGateFailure = $hasFailedGate -or [bool]($script:gates | Where-Object { $_.required -and $_.status -ne 'passed' })
    $report = [ordered]@{
        schemaVersion = 1
        workflow = 'Windows release qualification (validation only)'
        sourceCommit = $env:GITHUB_SHA
        candidateVersion = $candidateVersion
        generatedAtUtc = [DateTime]::UtcNow.ToString('O')
        outcome = if ($fatalError -or $hasFailedGate) { 'failed' } elseif ($requiredGateFailure) { 'blocked' } else { 'passed' }
        fatalError = $fatalError
        limitations = @(
            'Actual input delivery, normal UI rows/storage, RTF rendered paste, PNG full-detail and popup hotkeys are reported separately in live-ui/ui-evidence.json; unsupported or incomplete scenarios are not passes.',
            'HTML rendered paste is not covered by the native RTF target scenario.',
            'A visible window station and accessible input desktop are not proof of a physical monitor, hardware GPU, graphics-driver behavior or IME.',
            'The update feed uses Velopack 1.2.0 SimpleWebSource against a local HTTP fixture because this workflow has read-only GitHub permissions and does not publish a release.',
            'Velopack 1.2.0 is expected to leave current broken after an apply-time replacement failure. The qualification asserts that exact failure, then measures recovery by real candidate Setup or candidate portable ZIP overlay; it does not claim automatic rollback. See Velopack #1086/#1087 and prerelease 1.3.39.'
        )
        gates = @($script:gates)
    }
    [System.IO.File]::WriteAllText($evidence, ($report | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
    Stop-Transcript | Out-Null
}

if ($fatalError) { throw 'Hosted qualification aborted; inspect evidence.json and qualification.log.' }
if ($requiredGateFailure) { throw 'A required hosted qualification gate failed or remains blocked; inspect evidence.json.' }
