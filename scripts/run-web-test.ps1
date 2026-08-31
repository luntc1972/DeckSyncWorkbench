# Headless TEST launcher for DeckFlow web (http://localhost:5173).
#
# RULE: UI testing must NEVER open a browser on Windows. This script guarantees
# that by setting DECKFLOW_DISABLE_AUTO_BROWSER=true (the gate read in Program.cs
# IsAutoBrowserDisabled) BEFORE starting the server, so no Edge/Chrome window pops
# on each start during Playwright / curl / headless verification. Use this for any
# automated or screenshot-driven UI testing instead of run-web.ps1 / run-web-uat.ps1.
#
# SECURITY: the admin creds below are local-machine-only PLACEHOLDERS, not secrets.
# NEVER put a real/prod password here (public repo). Override via $env: before running.
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

# THE point of this script: suppress the Development auto-browser launch.
$env:DECKFLOW_DISABLE_AUTO_BROWSER = "true"
if (-not $env:ASPNETCORE_ENVIRONMENT) { $env:ASPNETCORE_ENVIRONMENT = "Development" }
if (-not $env:FEEDBACK_ADMIN_USER)     { $env:FEEDBACK_ADMIN_USER = "admin" }
if (-not $env:FEEDBACK_ADMIN_PASSWORD) { $env:FEEDBACK_ADMIN_PASSWORD = "changeme-local" }

$port = if ($env:DECKFLOW_E2E_PORT) {
    $env:DECKFLOW_E2E_PORT
} elseif ($env:PORT) {
    $env:PORT
} else {
    $webRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\DeckFlow.Web"))
    $portHash = 0
    foreach ($character in $webRoot.ToCharArray()) {
        $portHash = ($portHash * 31 + [int][char]$character) % 10000
    }

    20000 + $portHash
}

Write-Host "DeckFlow.Web (headless test mode) -> http://localhost:$port"
Write-Host "Auto-browser: DISABLED (DECKFLOW_DISABLE_AUTO_BROWSER=true). No Windows browser will open."
Write-Host "Admin login: $($env:FEEDBACK_ADMIN_USER) / $($env:FEEDBACK_ADMIN_PASSWORD)"

dotnet run --project DeckFlow.Web --urls "http://localhost:$port"
