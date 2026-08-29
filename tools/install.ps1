# Installs McpLink into a Resonite install -- from the latest GitHub release by default,
# or from a local build with -FromBuild. Loud about everything that can go silently wrong:
# a missing ResoniteModLoader, and the game's file lock on rml_mods\McpLink.dll.
#
#   powershell -File tools\install.ps1                       # latest release, Steam-path default
#   powershell -File tools\install.ps1 -ResonitePath "D:\Games\Resonite"
#   powershell -File tools\install.ps1 -FromBuild            # use this clone's bin\Release output
#   powershell -File tools\install.ps1 -SkipEval             # skip the optional eval companion
#
# Windows PowerShell 5.1 compatible.
param(
    [string]$ResonitePath = "C:\Program Files (x86)\Steam\steamapps\common\Resonite",
    [switch]$FromBuild,
    [switch]$SkipEval
)

$ErrorActionPreference = "Stop"   # NOTE: Assert-SameFile below no longer DEPENDS on this, but
                                  # relaxing it still weakens every other Test-Path/throw here.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$repoRoot = Split-Path $PSScriptRoot -Parent
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

# --- 1. validate the Resonite install and ResoniteModLoader ---
if (-not (Test-Path (Join-Path $ResonitePath "Resonite.exe"))) {
    throw "No Resonite install at '$ResonitePath'. Pass -ResonitePath 'C:\path\to\Resonite'."
}
if (-not (Test-Path (Join-Path $ResonitePath "Libraries\ResoniteModLoader.dll"))) {
    throw ("ResoniteModLoader is not installed at '$ResonitePath' -- McpLink is an RML mod and " +
           "does nothing without it. Install it first: " +
           "https://github.com/resonite-modding-group/ResoniteModLoader")
}
$modsDir = Join-Path $ResonitePath "rml_mods"
New-Item -ItemType Directory -Force $modsDir | Out-Null
$targetDll = Join-Path $modsDir "McpLink.dll"

# --- 2. gather the files to install ---
$stage = Join-Path $env:TEMP ("mcplink-install-" + [IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Force $stage | Out-Null
try {
    if ($FromBuild) {
        $srcDll = Join-Path $repoRoot "bin\Release\McpLink.dll"
        if (-not (Test-Path $srcDll)) {
            throw "No local build at '$srcDll'. Build first: dotnet build -c Release (see README)."
        }
        $version = "local build"
        $srcLibs = Join-Path $repoRoot "eval\bin\Release"
        $haveLibs = Test-Path (Join-Path $srcLibs "McpLinkEval.dll")
    } else {
        Write-Host "Fetching latest release info..."
        $release = Invoke-RestMethod -Uri $apiLatest
        $version = $release.tag_name
        $zipAsset = $release.assets | Where-Object { $_.name -like "McpLink-*.zip" } | Select-Object -First 1
        if ($null -eq $zipAsset) { throw "Release $version has no McpLink-*.zip asset -- report this as a bug." }
        $zipPath = Join-Path $stage $zipAsset.name
        Write-Host "Downloading $($zipAsset.name) ($([math]::Round($zipAsset.size / 1MB, 1)) MB)..."
        Invoke-WebRequest -Uri $zipAsset.browser_download_url -OutFile $zipPath
        Expand-Archive -Path $zipPath -DestinationPath $stage
        $srcDll = Join-Path $stage "rml_mods\McpLink.dll"
        if (-not (Test-Path $srcDll)) { throw "Downloaded zip is missing rml_mods\McpLink.dll -- report this as a bug." }
        $srcLibs = Join-Path $stage "rml_mods\McpLink_libs"
        $haveLibs = Test-Path $srcLibs
    }

    # --- 3. the file lock: a copy blocked by a running game must NEVER be silent ---
    if (Test-FileLocked $targetDll) {
        throw ("rml_mods\McpLink.dll is LOCKED -- Resonite is running. Nothing was changed. " +
               "Close the game and run this again.")
    }

    # --- 4. copy + verify (hash compare source vs installed; never trust a copy blindly) ---
    Copy-Item $srcDll $targetDll -Force
    Assert-SameFile $srcDll $targetDll "installed DLL does not match the source. Re-run the install."

    if ($haveLibs -and -not $SkipEval) {
        $libsDir = Join-Path $modsDir "McpLink_libs"
        New-Item -ItemType Directory -Force $libsDir | Out-Null
        Copy-Item (Join-Path $srcLibs "*.dll") $libsDir -Force
        Write-Host "Installed eval companion (McpLink_libs) -- the C# 'eval' tool is available."
    } elseif ($SkipEval) {
        Write-Host "Skipped the eval companion (-SkipEval) -- every tool except 'eval' works."
    }

    # a stale PENDING note (left by a lock-blocked developer build) is now false -- remove it
    $pending = "$targetDll.PENDING"
    if (Test-Path $pending) { Remove-Item $pending -Force -Confirm:$false }

    Write-Host ""
    Write-Host "McpLink $version installed to $modsDir (hash-verified)." -ForegroundColor Green
    Write-Host ""
    Write-Host "Next steps:"
    Write-Host "  1. Start Resonite; the log should show '[McpLink] MCP server listening on http://localhost:7357/mcp'."
    $proxyPath = Join-Path $repoRoot "proxy\mcplink_proxy.py"
    Write-Host "  2. Connect Codex or Claude Code (recommended route, needs Python 3.8+):"
    Write-Host "       codex mcp add mcplink -- python `"$proxyPath`""
    Write-Host "       claude mcp add mcplink -- python `"$proxyPath`""
    Write-Host "     or direct HTTP (only connects while the game runs):"
    Write-Host "       codex mcp add mcplink --url http://localhost:7357/mcp"
    Write-Host "       claude mcp add --transport http mcplink http://localhost:7357/mcp"
    Write-Host "  3. See README.md for teaching the agent (CLAUDE-MCPLINK.md) and configuration."
} finally {
    Remove-Item $stage -Recurse -Force -Confirm:$false -ErrorAction SilentlyContinue
}
