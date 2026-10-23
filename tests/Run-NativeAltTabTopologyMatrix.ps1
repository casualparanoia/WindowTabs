# Standalone 2x2 Explorer Alt+Tab topology probe.
# Compilation/plan checks are safe in CI: do not open windows or inject input.
# Live invocation is explicit and owns ONLY its own synthetic app/proxy HWNDs.
param(
    [switch]$BuildOnly,
    [switch]$Interactive,
    [ValidateRange(2,20)][int]$SwitchesPerCell=4
)
$ErrorActionPreference='Stop'
if (-not $BuildOnly -and -not $Interactive) {
    throw 'Use -BuildOnly for compile-only verification or -Interactive to permit disposable-window input.'
}
if ($BuildOnly -and $Interactive) { throw 'Use only one of -BuildOnly or -Interactive.' }
if ($SwitchesPerCell % 2 -ne 0) { throw 'SwitchesPerCell must be even.' }
if ($Interactive -and (Get-Process WindowTabs -ErrorAction SilentlyContinue)) {
    throw 'Exit ordinary WindowTabs before the isolated interactive fixture.'
}

$stage = Join-Path $PSScriptRoot ('Debug/topology-matrix-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stage | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'Missing 64-bit .NET Framework C# compiler.'
}
$source = Join-Path $PSScriptRoot 'NativeAltTabTopologyMatrix.cs'
$exe = Join-Path $stage 'NativeAltTabTopologyMatrix.exe'
& $compiler /nologo /target:exe /platform:x64 "/out:$exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll $source
if ($LASTEXITCODE -ne 0) { throw '2x2 topology fixture compilation failed.' }
& $exe --check-plan
if ($LASTEXITCODE -ne 0) { throw '2x2 topology factorial/keyboard-layout validation failed.' }
Write-Host "Compiled isolated 2x2 topology fixture: $exe"
if ($BuildOnly) { return }

$trace = Join-Path $stage 'topology-trace.jsonl'
Write-Host 'This test WILL briefly open its own test windows and send bounded native Alt+Tab input.'
Write-Host 'It never changes your normal application HWNDs or WindowTabs settings.'
Write-Host "Artifact directory: $stage"
& $exe --interactive --out $trace "$SwitchesPerCell"
$code = $LASTEXITCODE
Write-Host "Trace output: $trace"
if ($code -ne 0) {
    throw "Topology fixture stopped (exit code $code). Inspect the private JSONL before sharing."
}
Write-Host 'PASS: all four isolated registration/owner scenarios finished.'
