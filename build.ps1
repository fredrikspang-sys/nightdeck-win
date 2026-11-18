# Publishes a framework-dependent single-file Nightdeck.exe into .\out
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
if (Test-Path "$root\out") { Remove-Item "$root\out" -Recurse -Force }
dotnet publish "$root\src\Nightdeck\Nightdeck.csproj" -c Release -r win-x64 --self-contained false -o "$root\out" `
    -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true
Get-ChildItem "$root\out" -Filter *.pdb -File -ErrorAction SilentlyContinue | Remove-Item -Force
if (-not (Test-Path "$root\out\Nightdeck.exe")) { throw "publish did not produce out\Nightdeck.exe" }
$extra = @(Get-ChildItem "$root\out" -Force | Where-Object { $_.Name -ne "Nightdeck.exe" })
if ($extra.Count -ne 0) { throw ("publish left extra files: " + ($extra.Name -join ", ")) }
Get-Item "$root\out\Nightdeck.exe" | ForEach-Object { Write-Output ("{0}  {1:N0} bytes" -f $_.FullName, $_.Length) }
