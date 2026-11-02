# Runs `dotnet <args>` at BelowNormal priority, echoes its output and returns its exit code.
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$DotnetArgs)
$root = $PSScriptRoot
$out = Join-Path $root ('.dn-' + [guid]::NewGuid().ToString('N') + '.out')
$err = "$out.err"
$argLine = ($DotnetArgs | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
$p = Start-Process dotnet -ArgumentList $argLine -NoNewWindow -PassThru -RedirectStandardOutput $out -RedirectStandardError $err -WorkingDirectory $root
try { $p.PriorityClass = 'BelowNormal' } catch { }
$p.WaitForExit()
Get-Content $out -Encoding utf8
Get-Content $err -Encoding utf8
Remove-Item $out, $err -ErrorAction SilentlyContinue
exit $p.ExitCode