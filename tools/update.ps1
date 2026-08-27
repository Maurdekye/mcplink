# Updates an installed McpLink to the latest GitHub release.
#
# Version truth: builds are not byte-reproducible, so file dates and hashes across versions
# prove nothing. When the game is RUNNING, this script asks the live server its version over
# MCP `initialize`; if an update is needed the game must be closed for the swap (the DLL is
# file-locked), and the script says so instead of half-installing. When the game is closed,
# it swaps unconditionally and hash-verifies the copy.
#
#   powershell -File tools\update.ps1
#   powershell -File tools\update.ps1 -ResonitePath "D:\Games\Resonite"
#
# Windows PowerShell 5.1 compatible.
param(
    [string]$ResonitePath = "C:\Program Files (x86)\Steam\steamapps\common\Resonite",
    [int]$Port = 7357
)

$ErrorActionPreference = "Stop"   # NOTE: Assert-SameFile below no longer DEPENDS on this, but
                                  # relaxing it still weakens every other Test-Path/throw here.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$apiLatest = "https://api.github.com/repos/Maurdekye/mcplink/releases/latest"

# Copy verification, self-contained ON PURPOSE (2026-08-27 guard sweep).
#
# This used to be `if ((Get-FileHash $a).Hash -ne (Get-FileHash $b).Hash) { throw }`, which was
# correct only by accident of the line above: with $ErrorActionPreference = "Stop", Get-FileHash
# THROWS on a missing file (measured: ItemNotFoundException), so the comparison is never reached.
# Relax that preference -- one word, at the top of the file, for some unrelated reason -- and both
# sides become $null, `$null -ne $null` is FALSE, and this guard silently stops verifying while
# still printing its success message. That is the exact shape of the release.ps1 asset gate that
# had been vacuous for months.
#
# So the check no longer depends on a global: it asserts BOTH files exist and BOTH hashes are
# non-empty before comparing them, and says which precondition failed.
function Assert-SameFile([string]$expected, [string]$actual, [string]$what) {
    foreach ($p in @($expected, $actual)) {
        if (-not (Test-Path $p)) { throw "VERIFY FAILED: $what (missing file: $p -- the copy did not happen)" }
    }
    $a = (Get-FileHash $expected).Hash
    $b = (Get-FileHash $actual).Hash
    if ([string]::IsNullOrWhiteSpace($a) -or [string]::IsNullOrWhiteSpace($b)) {
        throw "VERIFY FAILED: $what (could not hash both files -- the check could not run, so nothing is verified)"
    }
    if ($a -ne $b) { throw "VERIFY FAILED: $what" }
}

function Test-FileLocked([string]$path) {
    if (-not (Test-Path $path)) { return $false }
    try { $s = [IO.File]::Open($path, 'Open', 'ReadWrite', 'None'); $s.Close(); return $false }
    catch { return $true }
}

$modsDir = Join-Path $ResonitePath "rml_mods"
$targetDll = Join-Path $modsDir "McpLink.dll"
if (-not (Test-Path $targetDll)) {
    throw "McpLink is not installed at '$modsDir' -- run tools\install.ps1 instead."
}

# --- what version is actually running (only answerable while the game is up) ---
$runningVersion = $null
try {
    $body = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"update.ps1","version":"1.0"}}}'
    $resp = Invoke-RestMethod -Uri "http://localhost:$Port/mcp" -Method Post -ContentType "application/json" `
        -Headers @{ Accept = "application/json, text/event-stream" } -Body $body -TimeoutSec 3
    $runningVersion = $resp.result.serverInfo.version
    Write-Host "Live server answered: McpLink $runningVersion is running."
} catch {
    Write-Host "No live server on port $Port (game closed, or the mod isn't loaded)."
}

# --- latest release ---
$release = Invoke-RestMethod -Uri $apiLatest
$latest = $release.tag_name -replace '^v', ''
Write-Host "Latest release: $latest"

if ($null -ne $runningVersion -and $runningVersion -eq $latest) {
    Write-Host "Already up to date." -ForegroundColor Green
    exit 0
}

# --- the swap needs the file unlocked, i.e. the game closed ---
if (Test-FileLocked $targetDll) {
    Write-Host ""
    Write-Host ("UPDATE PENDING: $latest is available but rml_mods\McpLink.dll is locked -- " +
                "Resonite is running. Nothing was changed. Close the game and run this again.") -ForegroundColor Yellow
    exit 2
}

$zipAsset = $release.assets | Where-Object { $_.name -like "McpLink-*.zip" } | Select-Object -First 1
if ($null -eq $zipAsset) { throw "Release $($release.tag_name) has no McpLink-*.zip asset -- report this as a bug." }

$stage = Join-Path $env:TEMP ("mcplink-update-" + [IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Force $stage | Out-Null
try {
    $zipPath = Join-Path $stage $zipAsset.name
    Write-Host "Downloading $($zipAsset.name)..."
    Invoke-WebRequest -Uri $zipAsset.browser_download_url -OutFile $zipPath
    Expand-Archive -Path $zipPath -DestinationPath $stage
    $srcDll = Join-Path $stage "rml_mods\McpLink.dll"
    if (-not (Test-Path $srcDll)) { throw "Downloaded zip is missing rml_mods\McpLink.dll -- report this as a bug." }

    Copy-Item $srcDll $targetDll -Force
    Assert-SameFile $srcDll $targetDll "installed DLL does not match the downloaded one. Re-run the update."

    # update the eval companion only if it was installed before (respect the user's choice)
    $libsDir = Join-Path $modsDir "McpLink_libs"
    $srcLibs = Join-Path $stage "rml_mods\McpLink_libs"
    if ((Test-Path $libsDir) -and (Test-Path $srcLibs)) {
        Copy-Item (Join-Path $srcLibs "*.dll") $libsDir -Force
        Write-Host "Updated the eval companion (McpLink_libs)."
    }

    # a stale PENDING note (left by a lock-blocked developer build) is now false -- remove it
    $pending = "$targetDll.PENDING"
    if (Test-Path $pending) { Remove-Item $pending -Force -Confirm:$false }

    Write-Host ""
    Write-Host "McpLink updated to $latest (hash-verified)." -ForegroundColor Green
    Write-Host "Start Resonite, then RESTART your MCP client / Claude session too:"
    Write-Host "clients cache tool schemas per session and would keep showing the old tools."
} finally {
    Remove-Item $stage -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
}
