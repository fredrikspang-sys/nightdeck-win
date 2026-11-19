# Publishes Nightdeck.exe (unless -SkipPublish) and compiles installer\nightdeck.iss.
param(
    [switch]$SkipPublish
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
Set-Location $root

$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command iscc, ISCC.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) {
    throw "Inno Setup compiler (ISCC.exe) not found. Install it with: winget install --id JRSoftware.InnoSetup -e"
}

if (-not $SkipPublish) {
    & "$root\build.ps1"
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "build.ps1 failed" }
}
if (-not (Test-Path "$root\out\Nightdeck.exe")) { throw "publish did not produce out\Nightdeck.exe" }

$outDir = "$root\installer\Output"
& $iscc /O"$outDir" "$root\installer\nightdeck.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Get-ChildItem $outDir -Filter "Nightdeck-*-setup.exe" | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Output ("{0}  {1:N0} bytes  sha256:{2}" -f $_.FullName, $_.Length, $hash)
}
