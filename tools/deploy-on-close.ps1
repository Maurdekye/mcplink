# McpLink deploy-on-close: waits for the game to release the locked mod DLL (game close),
# then deploys a PINNED build to BOTH mod slots and seeds config keys the shipped build
# expects. One-shot: exits after one successful deploy or one refusal. Re-arm per deploy.
#
# WHY THE PIN IS MANDATORY: this script re-reads -Src at copy time, so between arming and
# the game closing, anything that rebuilds the canonical tree silently changes the payload.
# -ExpectedSha256 freezes INTENT at arm time: if the bytes at -Src no longer match when the
# lock finally releases, the script REFUSES and logs instead of deploying whatever happens
# to be there. (2026-08-27: a stale pre-engine-update DLL nearly shipped exactly this way.)
#
# WHY BOTH SLOTS: rml_mods\McpLink.dll is what a cold launch loads; rml_mods\HotReloadMods\
# McpLink.dll is what the hot_reload tool loads. Deploying only the first leaves a stale
# (possibly broken) build one hot_reload away, and session_info reports deployConsistent
# false. The HotReloadMods copy is made FROM the verified destination, not from -Src, so the
# two slots are identical even if -Src changes mid-deploy.
#
# WHY THE SEED HAPPENS HERE: ResoniteModLoader's shutdown hook rewrites the config file from
# the RUNNING mod's known keys at every game close, erasing keys hand-added while it played
# (ilspy: ModConfiguration.ShutdownHook; AutoSave defaults true). Seeding strictly after the
# lock releases lands after RML's save. Seed-if-absent only: a value the user later changes
# in-game is never stomped by a redeploy. Seed values arrive as parameters (empty = skip)
# so the repo carries no machine-specific paths; flat strings, not a hashtable, because
# powershell.exe -File (how the scheduled task invokes this) passes arguments as strings.
#
# OPERATIONAL CAVEATS THAT LIVE OUTSIDE THIS SCRIPT:
#   - The scheduled task that arms it has an execution time limit (72 h on this machine's
#     \McpLinkCopyOnGameClose): if the game does not close within that window, the task is
#     killed and the deploy silently does not happen. Re-arm if the window slips.
#   - Post-deploy verification of the RUNNING game (session_info version/stamp) is external
#     by design; this script verifies bytes on disk against the pin, nothing more.
#
# EXIT CODES: 0 deployed (seed attempted, seed failure is non-fatal and logged)
#             2 post-copy verification failed (.PENDING left in place)
#             3 refused: source hash did not match the pin
#             4 copy failed for a non-lock reason
#             5 source file missing or unreadable
param(
    [Parameter(Mandatory = $true)][string]$ExpectedSha256,
    [string]$Src = 'E:\Libraries\Desktop\resonite\mcplink\bin\Release\McpLink.dll',
    [string]$Dst = 'C:\Program Files (x86)\Steam\steamapps\common\Resonite\rml_mods\McpLink.dll',
    [string]$DstHotReload = 'C:\Program Files (x86)\Steam\steamapps\common\Resonite\rml_mods\HotReloadMods\McpLink.dll',
    [string]$Cfg = 'C:\Program Files (x86)\Steam\steamapps\common\Resonite\rml_config\McpLink.json',
    [string]$Log = (Join-Path $env:LOCALAPPDATA 'McpLink\deploy-on-close-log.txt'),
    [string]$SeedPromptDefaultOrg = '',
    [string]$SeedPromptHireDir = '',
    [int]$RetrySeconds = 60
)

$ErrorActionPreference = 'Stop'

function Write-Log([string]$msg) {
    $dir = Split-Path $Log -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    Add-Content $Log ("{0} {1}" -f (Get-Date -Format s), $msg)
}

$ExpectedSha256 = $ExpectedSha256.Trim().ToUpperInvariant()
Write-Log "armed: pin=$ExpectedSha256 src=$Src dst=$Dst hotreload=$DstHotReload retry=${RetrySeconds}s"

# Wait for the destination lock to release. Each iteration re-verifies the source against
# the pin FIRST, so a payload swapped in during the wait is refused, not shipped. Only a
# genuine sharing/lock violation keeps the loop alive; every other failure is loud.
while ($true) {
    try {
        $srcHash = (Get-FileHash $Src -Algorithm SHA256).Hash
    } catch {
        Write-Log "SOURCE MISSING or unreadable: $Src ($($_.Exception.Message)) - not deploying"
        exit 5
    }
    if ($srcHash -ne $ExpectedSha256) {
        Write-Log "REFUSED: source sha256 $srcHash does not match pinned $ExpectedSha256 - not deploying"
        exit 3
    }
    try {
        Copy-Item $Src $Dst -Force
        break
    } catch [System.IO.IOException] {
        Start-Sleep -Seconds $RetrySeconds
    } catch {
        Write-Log "COPY FAILED (non-lock): $($_.Exception.Message) - not deploying"
        exit 4
    }
}

# Destination landed; verify it against the pin, then mirror the VERIFIED bytes into the
# HotReloadMods slot so the pair cannot diverge even if -Src changes under us right now.
$dstHash = (Get-FileHash $Dst -Algorithm SHA256).Hash
if ($dstHash -ne $ExpectedSha256) {
    Write-Log "CRITICAL: post-copy verify FAILED: dst=$dstHash pin=$ExpectedSha256 - .PENDING left in place"
    exit 2
}
try {
    Copy-Item $Dst $DstHotReload -Force
} catch {
    Write-Log "CRITICAL: HotReloadMods copy failed: $($_.Exception.Message) - slots inconsistent, .PENDING left in place"
    exit 2
}
$hrHash = (Get-FileHash $DstHotReload -Algorithm SHA256).Hash
if ($hrHash -ne $ExpectedSha256) {
    Write-Log "CRITICAL: HotReloadMods verify FAILED: hotreload=$hrHash pin=$ExpectedSha256 - .PENDING left in place"
    exit 2
}
Write-Log "deployed: $Dst and $DstHotReload sha256=$dstHash (pin verified on both)"

# Both slots verified: the build's half-done-deploy note is now false, so remove it.
Remove-Item "$Dst.PENDING" -Force -ErrorAction SilentlyContinue

# Config seed (only the keys actually requested, only when absent, logged by what was
# actually added - not by what was requested).
$want = [ordered]@{}
if ($SeedPromptDefaultOrg) { $want['promptDefaultOrg'] = $SeedPromptDefaultOrg }
if ($SeedPromptHireDir)    { $want['promptHireDir']    = $SeedPromptHireDir }
if ($want.Count -gt 0) {
    try {
        $j = Get-Content $Cfg -Raw | ConvertFrom-Json
        if ($null -eq $j.values) { throw "no 'values' object in $Cfg" }
        $added = @()
        foreach ($k in $want.Keys) {
            if ($null -eq $j.values.PSObject.Properties[$k]) {
                $j.values | Add-Member -NotePropertyName $k -NotePropertyValue $want[$k]
                $added += $k
            }
        }
        if ($added.Count -gt 0) {
            $j | ConvertTo-Json -Depth 5 | Out-File $Cfg -Encoding utf8
            Write-Log ("seeded config keys: " + ($added -join ', '))
        } else {
            Write-Log 'config seed: nothing to do (all requested keys present)'
        }
    } catch {
        Write-Log "CONFIG SEED FAILED: $_"
    }
}
exit 0
