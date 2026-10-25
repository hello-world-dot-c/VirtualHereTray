# Publishes the app and compiles the per-user installer to installer\output\.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (https://jrsoftware.org/isinfo.php).' }

if (-not (Test-Path "$root\app.ico")) { & "$PSScriptRoot\make-icon.ps1" }

Stop-Process -Name VirtualHereTray -ErrorAction SilentlyContinue   # unlock dist\VirtualHereTray.exe
dotnet publish "$root\VirtualHereTray.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -o "$root\dist"
if ($LASTEXITCODE) { throw 'dotnet publish failed' }
Remove-Item "$root\dist\*.pdb" -ErrorAction SilentlyContinue

& $iscc "$PSScriptRoot\VirtualHereTray.iss"
if ($LASTEXITCODE) { throw 'Inno Setup failed' }
Get-ChildItem "$PSScriptRoot\output" | Select-Object Name, Length
