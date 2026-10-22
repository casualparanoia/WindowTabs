# Opt-in, passive recorder for real manual native Alt+Tab events.
# Unlike DesktopE2E, normal WindowTabs and browsers MUST remain running.
# This script does not inject input, create test windows, or alter settings.
param(
    [switch]$BuildOnly,
    [switch]$Interactive,
    [ValidateRange(1,30)][int]$Minutes=10
)
$ErrorActionPreference='Stop'
if (-not $BuildOnly -and -not $Interactive) {
    throw 'This recorder observes desktop foreground and Alt+Tab key timing. Pass -Interactive to opt in, or -BuildOnly for compile-only validation.'
}
$stage=Join-Path $PSScriptRoot ('Debug/passive-alt-tab-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$source=Join-Path $PSScriptRoot 'PassiveAltTabTrace.cs'
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'Missing 64-bit .NET Framework C# compiler (Framework64/v4.0.30319/csc.exe).'
}
$exe=Join-Path $stage 'PassiveAltTabTrace.exe'
& $compiler /nologo /target:exe /platform:x64 "/out:$exe" /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Drawing.dll $source
if ($LASTEXITCODE -ne 0) { throw 'Passive Alt+Tab recorder failed to compile.' }
Write-Host "Compiled passive Alt+Tab recorder: $exe"
if ($BuildOnly) { return }
$trace=Join-Path $stage 'foreground-trace.jsonl'
Write-Host "Trace output: $trace"
Write-Host 'Normal WindowTabs, Vivaldi, and other apps can stay open.'
Write-Host 'This only observes foreground windows, Alt+Tab and the failure marker.'
Write-Host 'Press Ctrl+Shift+F12 immediately after a visible mis-switch to mark it.'
Write-Host 'Stop with Ctrl+C or allow the requested time window to elapse.'
& $exe $trace "$Minutes"
if ($LASTEXITCODE -ne 0) {
    throw "Passive trace exited with code $LASTEXITCODE; see $stage"
}
Write-Host "Finished. Review the privacy notes before sharing: $trace"
