#Requires -Version 7
# Checks that Invoke-PasteSweep.ps1 hands Edge and Windows Terminal their arguments intact.
# Start-Process joins -ArgumentList with spaces and quotes nothing, and wt splits on every ';'.
$ErrorActionPreference = 'Stop'
Add-Type -Namespace PasteSweepCheck -Name Argv -MemberDefinition @"
[DllImport("shell32.dll", SetLastError = true)] static extern System.IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string cmd, out int n);
[DllImport("kernel32.dll")] static extern System.IntPtr LocalFree(System.IntPtr p);
public static string[] Split(string cmd) {
    int n; var p = CommandLineToArgvW("x " + cmd, out n); var r = new string[n - 1];
    for (int i = 1; i < n; i++) r[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * System.IntPtr.Size));
    LocalFree(p); return r; }
"@
$script = Join-Path $PSScriptRoot 'Invoke-PasteSweep.ps1'
$ast = [System.Management.Automation.Language.Parser]::ParseFile($script, [ref]$null, [ref]$null)
$fn = $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $args[0].Name -in 'Get-EdgeArguments', 'Get-TerminalArguments' }, $true)
if ($fn.Count -ne 2) { throw "Get-EdgeArguments / Get-TerminalArguments not found in $script" }
foreach ($f in $fn) { Invoke-Expression $f.Extent.Text }
$failures = 0
$edgeArgv = [PasteSweepCheck.Argv]::Split((Get-EdgeArguments))
$url = $edgeArgv | Where-Object { $_ -like 'data:*' }
$page = if (@($url).Count -eq 1) { [Uri]::UnescapeDataString($url) } else { '' }
if (@($url).Count -ne 1 -or $page -notlike '*<textarea autofocus*oninput=*</textarea>') { Write-Host "FAIL Edge: the page arrives in pieces: $($edgeArgv -join ' | ')" -ForegroundColor Red; $failures++ } else { Write-Host 'ok   Edge: the data: URL arrives as one argument' }
foreach ($title in 'PasteSweepTerm', 'PasteSweepElevated') {
    $wtArgv = [PasteSweepCheck.Argv]::Split((Get-TerminalArguments $title 'C:\x\lines.txt'))
    $unescaped = $wtArgv | Where-Object { $_ -match '(?<!\\);' }
    if ($unescaped) { Write-Host "FAIL Terminal: wt would split at ';' in: $($unescaped -join ' | ')" -ForegroundColor Red; $failures++ }
    else {
        $enc = $wtArgv[[array]::IndexOf($wtArgv, '-EncodedCommand') + 1]
        $decoded = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($enc))
        if ($decoded -notlike "*WindowTitle='$title'*ReadLine()*Add-Content*lines.txt*") { Write-Host "FAIL Terminal: reader decodes to: $decoded" -ForegroundColor Red; $failures++ }
        else { Write-Host "ok   Terminal ($title): one command, reader intact" }
    }
}
exit $failures
