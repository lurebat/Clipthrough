param(
    [Parameter(Mandatory)]
    [string] $PublishDirectory,
    [Parameter(Mandatory)]
    [string] $FeedDirectory,
    [Parameter(Mandatory)]
    [string] $HarnessDirectory,
    [Parameter(Mandatory)]
    [string] $EvidencePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$publish = (Resolve-Path $PublishDirectory).Path
$feed = (Resolve-Path $FeedDirectory).Path
$harness = (Resolve-Path $HarnessDirectory).Path
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
$candidateVersion = '1.0.0'
$workspace = Join-Path $evidenceDirectory 'runner-state'
$downloadDirectory = Join-Path $workspace 'downloads'
$installRoot = Join-Path $workspace 'installed'
$chosenDataRoot = Join-Path $workspace 'chosen-data'
$portableRoot = Join-Path $workspace 'portable'
$portableDataRoot = Join-Path $portableRoot 'ClipthroughData'
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
        @{ Name = 'failed_update_rollback'; Required = $true },
        @{ Name = 'interactive_clipboard_paste_hotkey_smoke'; Required = $false },
        @{ Name = 'normal_ui_storage_and_record_visibility'; Required = $false },
        @{ Name = 'rich_paste_target_content'; Required = $false },
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
        $appEntries = @($archive.Entries | Where-Object { $_.FullName.StartsWith('lib/app/', [StringComparison]::OrdinalIgnoreCase) -and -not $_.FullName.EndsWith('/') })
        if ($appEntries.Count -ne 105) { throw "Expected 105 lib/app payloads in the old full package; found $($appEntries.Count)." }
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
                @{ Source = 'Clipthrough_ExecutionStub.exe'; Destination = 'Clipthrough.exe' },
                @{ Source = 'Squirrel.exe'; Destination = 'Update.exe' }
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

        if ($mapping.Count -ne 107) { throw "Expected 107 verified payload mappings; found $($mapping.Count)." }
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
    param([ValidateSet('seed', 'verify')][string] $Mode, [string] $DataRoot, [string] $Label)
    $env:CLIPTHROUGH_QUALIFICATION_DATA_ROOT = $DataRoot
    $env:CLIPTHROUGH_QUALIFICATION_MODE = $Mode
    $env:CLIPTHROUGH_QUALIFICATION_SOURCE = if ($Label -like '*portable*') { 'Portable' } else { 'CommandLine' }
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
    if ($exitCode -ne 0) { throw "Shipping storage qualification test ($Label) exited with $exitCode." }
}

function Read-VersionManifest {
    param([string] $Root)
    $path = Join-Path $Root 'current\sq.version'
    if (-not (Test-Path $path)) { throw "Velopack version manifest is missing: $path" }
    return (Get-Content -Raw -Path $path).Trim()
}

function Wait-ForCandidateManifest {
    param([string] $Root, [int] $TimeoutSeconds = 600)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $manifestPath = Join-Path $Root 'current\sq.version'
        if (Test-Path $manifestPath) {
            $manifest = (Get-Content -Raw -Path $manifestPath).Trim()
            if ($manifest -match [regex]::Escape($candidateVersion)) { return $manifest }
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Velopack did not finish applying candidate $candidateVersion within $TimeoutSeconds seconds."
}

function Wait-ForDataRootLog {
    param([string] $Root, [string] $SourceName, [int] $TimeoutSeconds = 180)
    $log = Join-Path $Root 'logs\clipthrough.log'
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $expectedLine = "Data folder: $Root ($SourceName)."
    while ([DateTime]::UtcNow -lt $deadline) {
        if (Test-Path $log) {
            try {
                $content = Get-Content -Raw -Path $log
                if ($content.Contains($expectedLine, [StringComparison]::OrdinalIgnoreCase)) { return $log }
            }
            catch [System.IO.IOException] {
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
    $setupProcess = Start-Process -FilePath $setupPath `
        -ArgumentList @('--silent', '--installTo', "`"$installRoot`"") `
        -PassThru `
        -RedirectStandardOutput $setupStdout `
        -RedirectStandardError $setupStderr
    if (-not $setupProcess.WaitForExit(600000)) {
        Stop-Process -Id $setupProcess.Id -Force
        throw 'The legacy Setup install exceeded ten minutes.'
    }
    $setupProcess.Refresh()
    if ($setupProcess.ExitCode -ne 0) { throw "Legacy Setup exited with $($setupProcess.ExitCode)." }
    $oldManifest = Read-VersionManifest $installRoot
    if ($oldManifest -notmatch '0\.17\.1') { throw "The Setup installed unexpected version '$oldManifest'." }
    if (-not (Test-Path (Join-Path $installRoot 'Update.exe'))) { throw 'The Setup install has no Update.exe.' }
    if (-not (Test-Path $setupStdout) -or -not (Test-Path $setupStderr)) { throw 'Setup process logs were not captured.' }
    Set-Gate 'old_setup_install' 'passed' "Silent real Setup install exited 0; Update.exe and current/sq.version prove installed version $oldManifest."
    Assert-LegacyNupkgMapping -PackagePath $fullPackagePath -Root $installRoot
    Set-Gate 'old_package_payload_mapping' 'passed' 'All 105 lib/app entries byte-match under current; explicit package-root aliases Clipthrough_ExecutionStub.exe -> Clipthrough.exe and Squirrel.exe -> Update.exe also match (107 total).'

    Invoke-QualificationTest -Mode seed -DataRoot $chosenDataRoot -Label 'seed-installed'
    Set-Gate 'synthetic_data_seed_and_read' 'passed' 'The shipping DatabaseInitializer and ClipStoreService seeded two isolated synthetic clips and read them back by id.'

    $installedUpdate = Invoke-FeedUpdate -Root $installRoot -DataRoot $chosenDataRoot
    Invoke-QualificationTest -Mode verify -DataRoot $chosenDataRoot -Label 'verify-installed-update'
    Set-Gate 'candidate_feed_download_and_apply' 'passed' "Velopack 1.2.0 SimpleWebSource consumed the generated stable feed over loopback HTTP, downloaded the full candidate and UpdateManager applied it through the real installed locator: $($installedUpdate.manifest). Feed transport was local fixture, not the production GitHub release endpoint."
    Set-Gate 'explicit_data_root_restart_survival' 'passed' "The actual post-update app restart logged CommandLine root $chosenDataRoot; the shipping store read the seeded IDs after the restart."

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
    Set-Gate 'failed_update_rollback' 'blocked' "Malformed package rejection left $beforeFailure installed (Update.exe exit $applyExit), proving pre-apply preservation only. Velopack post-replacement startup rollback is not exercised; no destructive test seam is present."
}
catch {
    $fatalError = $_.Exception.ToString()
    Write-Host $fatalError -ForegroundColor Red
}
finally {
    Remove-Item Env:\CLIPTHROUGH_QUALIFICATION_DATA_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:\CLIPTHROUGH_QUALIFICATION_MODE -ErrorAction SilentlyContinue
    Remove-Item Env:\CLIPTHROUGH_QUALIFICATION_SOURCE -ErrorAction SilentlyContinue
    if ($fatalError) {
        foreach ($gate in $script:gates | Where-Object { $_.status -eq 'blocked' -and $_.detail -eq 'Not reached.' }) {
            $gate.detail = "Not reached because an earlier operation failed: $fatalError"
        }
    }
    $requiredGateFailure = [bool]($script:gates | Where-Object { $_.required -and $_.status -ne 'passed' })
    $report = [ordered]@{
        schemaVersion = 1
        workflow = 'Windows release qualification (validation only)'
        sourceCommit = $env:GITHUB_SHA
        candidateVersion = $candidateVersion
        generatedAtUtc = [DateTime]::UtcNow.ToString('O')
        outcome = if ($fatalError) { 'failed' } elseif ($requiredGateFailure) { 'blocked' } else { 'passed' }
        fatalError = $fatalError
        limitations = @(
            'No clipboard, foreground paste, image, detail-view or hotkey smoke checks are performed.',
            'The shipping storage service validates the actual configured DB path and synthetic records; visible normal-window row presentation is not tested.',
            'A visible window station and accessible input desktop are not proof of a physical monitor, hardware GPU, graphics-driver behavior or IME.',
            'The update feed uses Velopack 1.2.0 SimpleWebSource against a local HTTP fixture because this workflow has read-only GitHub permissions and does not publish a release.',
            'The failed-package case verifies rejection before replacement, not rollback after a new app version fails during startup.'
        )
        gates = @($script:gates)
    }
    [System.IO.File]::WriteAllText($evidence, ($report | ConvertTo-Json -Depth 8), [System.Text.UTF8Encoding]::new($false))
    Stop-Transcript | Out-Null
}

if ($fatalError) { throw 'Hosted qualification aborted; inspect evidence.json and qualification.log.' }
if ($requiredGateFailure) { throw 'A required hosted qualification gate failed or remains blocked; inspect evidence.json.' }
