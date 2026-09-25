param([ValidateSet('win-x64','win-arm64')][string]$Runtime = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') {'win-arm64'} else {'win-x64'}))
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK installieren: https://dotnet.microsoft.com/download/dotnet/10.0' }
$target=Join-Path $env:LOCALAPPDATA 'Jarvis/app'
dotnet publish (Join-Path $repoRoot 'agents/windows/Jarvis.Windows.csproj') -c Release -r $Runtime --self-contained true -o $target
if ($LASTEXITCODE -ne 0) { throw 'Agent-Build fehlgeschlagen.' }
Write-Host "Agent installiert: $target"
Write-Host 'Öffne Jarvis.Windows.exe und verbinde ihn mit Code + Pairing-Token aus dem Dashboard.'
Write-Host 'Für Autostart kann eine Verknüpfung in shell:startup angelegt werden. Der Installer verändert den Autostart nicht automatisch.'
