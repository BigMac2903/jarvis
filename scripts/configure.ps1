param([string]$Destination = (Join-Path (Split-Path $PSScriptRoot -Parent) '.env'))
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (Test-Path -LiteralPath $Destination) { $content = Get-Content -LiteralPath $Destination -Raw } else { $content = Get-Content -LiteralPath (Join-Path $repoRoot '.env.example') -Raw }
foreach ($key in @('POSTGRES_PASSWORD','REDIS_PASSWORD','MASTER_KEY','BROWSER_MASTER_KEY','BROWSER_TOKEN','SETUP_TOKEN','SEARXNG_SECRET','SIP_SERVICE_TOKEN')) {
    if ($content -notmatch "(?m)^$key=") { $content += "`n$key=`n" }
    if ($content -match "(?m)^$key=\s*$") {
        $bytes = New-Object byte[] 32
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
        $value = if ($key -in @('MASTER_KEY','BROWSER_MASTER_KEY')) { [Convert]::ToBase64String($bytes) } else { [BitConverter]::ToString($bytes).Replace('-','').ToLowerInvariant() }
        $content = [regex]::Replace($content, "(?m)^$key=[ \t]*\r?$", "$key=$value")
    }
}
[IO.File]::WriteAllText($Destination, $content, (New-Object Text.UTF8Encoding($false)))
Write-Host "Konfiguration bereit: $Destination"
