param(
    [Parameter(Mandatory)]
    [string] $PublishDirectory,

    [Parameter(Mandatory)]
    [string] $SymbolsDirectory
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PublishDirectory -PathType Container)) {
    throw "Publish directory not found: $PublishDirectory"
}

New-Item -ItemType Directory -Force -Path $SymbolsDirectory | Out-Null

foreach ($symbolName in @('libSkiaSharp.pdb', 'libHarfBuzzSharp.pdb')) {
    $symbolFiles = @(Get-ChildItem -LiteralPath $PublishDirectory -Filter $symbolName -File -Recurse)
    if ($symbolFiles.Count -ne 1) {
        throw "Expected exactly one $symbolName in $PublishDirectory; found $($symbolFiles.Count)."
    }

    $symbolFile = $symbolFiles[0]
    Copy-Item -LiteralPath $symbolFile.FullName -Destination (Join-Path $SymbolsDirectory $symbolName)
    Remove-Item -LiteralPath $symbolFile.FullName
}

$depsPath = Join-Path $PublishDirectory 'Clipthrough.deps.json'
$deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
if (-not $deps.runtimeTarget.name.EndsWith('/win-x64', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Expected a win-x64 dependency target in $depsPath; found $($deps.runtimeTarget.name)."
}

$target = $deps.targets.PSObject.Properties[$deps.runtimeTarget.name].Value
$publishAssetNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$symbolAssetNames = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

foreach ($file in Get-ChildItem -LiteralPath $PublishDirectory -File -Recurse) {
    [void]$publishAssetNames.Add($file.Name)
}
foreach ($file in Get-ChildItem -LiteralPath $SymbolsDirectory -File -Recurse) {
    [void]$symbolAssetNames.Add($file.Name)
}

foreach ($library in $target.PSObject.Properties) {
    foreach ($assetKind in @('runtime', 'native')) {
        $assets = $library.Value.$assetKind
        if ($null -eq $assets) {
            continue
        }

        foreach ($assetPath in $assets.PSObject.Properties.Name) {
            if ($assetPath -eq '_._') {
                continue
            }

            $assetName = Split-Path $assetPath -Leaf
            $isSymbol = [System.IO.Path]::GetExtension($assetName).Equals('.pdb', [StringComparison]::OrdinalIgnoreCase)
            $assetExists = if ($isSymbol) {
                $symbolAssetNames.Contains($assetName)
            } else {
                $publishAssetNames.Contains($assetName)
            }
            if (-not $assetExists) {
                throw "Missing $assetKind asset '$assetPath' from '$($library.Name)' in the publish or symbols output."
            }
        }
    }
}
