# McpLink deploy - THE single entry point for every deploy, game open or closed.
# Policy this implements: docs/dev/CONTRIBUTING.md "Deploy policy" (user ruling 2026-08-28).
# No hand-rolled copies, ever - including by agents in a hurry.
#
#   Game CLOSED  -> deploys immediately. No arming, no scheduled task, no waiting.
#   Game OPEN    -> stages a SNAPSHOT and arms a detached waiter that deploys the moment the
#                   file lock releases AND the game process is gone. Nothing is ever written
#                   into the game folder while Resonite runs.
#
# IDEMPOTENT BY REPLACEMENT (user's exact requirement): calling this again while a deploy is
# still waiting re-stages a fresh snapshot that REPLACES the pending one. At most one deploy
# is ever pending, always the newest; re-running is always safe.
#
# THE PIN: the payload is snapshotted into the stage directory and ITS hash is measured at
# stage time. The waiter deploys the snapshot and verifies against that pin - never re-reads
# bin\Release. This kills the old copier's deepest flaw (payload re-read at close time) and
# with it LIFTS a real constraint: nobody has to freeze canonical Release builds while a
# deploy is pending anymore. If you see that freeze practiced, it is folklore from before
# this script existed - point people here.
#
# BACKUP IS A HARD GATE, not a step: the deploy REFUSES to touch the game folder unless it
# has already written a backup of every outgoing DLL and verified the backup's hash against
# the file it is about to overwrite. No verified backup, no deploy, loud failure.
# (2026-08-28: a deploy overwrote 2.10.0 with no backup taken because backing up was a step
# that could be skipped without the run failing. A skippable step is an abstention.)
#
# HOT RELOAD IS NOT A DEPLOY: hot reloading is for rapid prototyping during implementation.
# A stable deploy is always file-copy plus the user's next launch. The success criterion of
# this script is "the files are on disk in both slots" - NEVER "the running game picked it
# up", and no outcome this script reports means that.
#
# STAMP-BLINDNESS WARNING, learned 2026-08-28: a Debug and a Release build of the same
# commit carry the SAME informational-version stamp. A stamp-only check calls the wrong one
# "arrived". That is why the outcome file and the expectation pair carry FILE HASHES, and
# why post-deploy verification must compare hashes, not stamps alone.
#
# EXIT CODES: 0 deployed, or staged-and-waiting (see outcome file for which)
#             2 verification CRITICAL (corrupt stage / post-copy mismatch); nothing cleaned up
#             3 refused: -ExpectedSha256 given and the source does not match it
#             4 copy failed for a non-lock reason
#             5 source missing or unreadable
#             6 refused: backup could not be written or verified (game folder untouched)
#             8 refused: build stamp does not name the repo HEAD, or stamp unverifiable
#             9 staged, but the waiter task could not be armed (deploy will NOT happen
#               until this script is invoked again - treat as action-required)
#
# The machine-readable outcome of every invocation is written to <StageDir>\last-deploy.json.
#
# *** FIRST-REAL-DEPLOY MILESTONE - remove this block once it has happened and verified. ***
# As of 2026-08-28 every gate here is control-tested (53-check harness, build-control on both
# trees) but the system has NEVER performed a real deploy: the waiter has never fired on an
# actual game close, and a real payload has never landed in the real slots through this
# script. All of that proves it writes nothing when it should write nothing; none of it
# proves it writes the right thing when it should. Treat the first real deploy as a
# MILESTONE TO WATCH, not a routine event: report it with full before/after hashes, and if
# ANYTHING looks wrong, STOP and fall back to a hash-verified manual copy with the game
# closed - never debug this script live against the user's install.

param(
    [string]$Src = 'E:\Libraries\Desktop\resonite\mcplink\bin\Release\McpLink.dll',
    [string]$Dst = 'C:\Program Files (x86)\Steam\steamapps\common\Resonite\rml_mods\McpLink.dll',
    [string]$DstHotReload = 'C:\Program Files (x86)\Steam\steamapps\common\Resonite\rml_mods\HotReloadMods\McpLink.dll',
    [string]$Cfg = 'C:\Program Files (x86)\Steam\steamapps\common\Resonite\rml_config\McpLink.json',
    [string]$StageDir = (Join-Path $env:LOCALAPPDATA 'McpLink\deploy'),
    [string]$Log = (Join-Path $env:LOCALAPPDATA 'McpLink\deploy-log.txt'),
    [string]$RepoPath = 'E:\Libraries\Desktop\resonite\mcplink',
    [string]$GitExe = '',
    [string]$VersionLabel = '',
    [string]$ExpectedSha256 = '',
    [string]$SeedPromptDefaultOrg = '',
    [string]$SeedPromptHireDir = '',
    [string]$GameProcessName = 'Resonite',
    [int]$RetrySeconds = 60,
    [ValidateSet('schtask', 'none')][string]$WaiterLaunch = 'schtask',
    [string]$WaiterTaskName = 'McpLinkDeployWaiter',
    [switch]$SkipStampCheck,
    [switch]$Waiter
)

$ErrorActionPreference = 'Stop'
$StagedDll = Join-Path $StageDir 'staged-McpLink.dll'
$ManifestPath = Join-Path $StageDir 'staged.json'
$OutcomePath = Join-Path $StageDir 'last-deploy.json'

# BOM-less UTF-8 for EVERY file this script writes. On PS 5.1, -Encoding utf8 emits a BOM
# (a BOM at offset 0 breaks strict JSON parsers reading the outcome/manifest contract
# files - half of what corrupted four published release notes), and a bare
# Set-Content/Add-Content uses the machine's ANSI codepage, which silently destroys
# non-ASCII characters in paths. Measured on this machine; docs/dev/CONTRIBUTING.md.
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)
function Write-TextFile([string]$path, [string]$content) {
    [System.IO.File]::WriteAllText($path, $content, $script:Utf8NoBom)
}

function Write-Log([string]$msg) {
    $dir = Split-Path $Log -Parent
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    [System.IO.File]::AppendAllText($Log, ("{0} {1}`r`n" -f (Get-Date -Format s), $msg), $script:Utf8NoBom)
}

function Get-Sha([string]$path) { return (Get-FileHash $path -Algorithm SHA256).Hash }

function Write-Outcome([hashtable]$o) {
    if (-not (Test-Path $StageDir)) { New-Item -ItemType Directory -Force $StageDir | Out-Null }
    $o['writtenAtUtc'] = [DateTime]::UtcNow.ToString('o')
    $tmp = "$OutcomePath.tmp"
    Write-TextFile $tmp ($o | ConvertTo-Json -Depth 8)
    Move-Item $tmp $OutcomePath -Force
}

# "Closed" means BOTH: no game process, and the destination file writable. The process check
# catches the launcher window where the game is starting but has not locked the file yet.
function Test-GameClosed([string]$dstPath, [string]$procName) {
    if (Get-Process $procName -ErrorAction SilentlyContinue) { return $false }
    if (-not (Test-Path $dstPath)) { return $true }
    try {
        $fs = [System.IO.File]::Open($dstPath, 'Open', 'ReadWrite', 'None')
        $fs.Close()
        return $true
    } catch { return $false }
}

function Read-Manifest {
    if (-not (Test-Path $ManifestPath)) { return $null }
    try { return (Get-Content $ManifestPath -Raw | ConvertFrom-Json) } catch { return $null }
}

# The deploy itself. $m is the manifest object. Returns 'deployed' or 'busy' (game re-opened
# or re-locked between the probe and the copy - caller decides whether to wait); every other
# failure exits the process with its code after writing the outcome.
function Invoke-Deploy($m) {
    $pin = $m.pinSha256

    # ---- BACKUP HARD GATE ------------------------------------------------------------
    # Refuse to touch the game folder unless every outgoing DLL has a hash-verified backup.
    $stampDir = [DateTime]::Now.ToString('yyyyMMdd-HHmmss')
    $bakDir = Join-Path $StageDir ("backups\" + $stampDir)
    $old = @{}
    foreach ($slot in @(@{name = 'rmlMods'; path = $m.dst }, @{name = 'hotReloadMods'; path = $m.dstHotReload })) {
        if (Test-Path $slot.path) {
            $old[$slot.name] = @{
                sha            = Get-Sha $slot.path
                productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($slot.path).ProductVersion
            }
        } else {
            $old[$slot.name] = @{ sha = 'absent'; productVersion = 'absent' }
        }
    }
    try {
        New-Item -ItemType Directory -Force $bakDir | Out-Null
        $lines = @("Outgoing files backed up $stampDir before deploying pin $pin")
        foreach ($slot in @(@{name = 'rmlMods'; path = $m.dst }, @{name = 'hotReloadMods'; path = $m.dstHotReload })) {
            if ($old[$slot.name].sha -ne 'absent') {
                $bakFile = Join-Path $bakDir ($slot.name + '-McpLink.dll')
                Copy-Item $slot.path $bakFile -Force
                $bakSha = Get-Sha $bakFile
                if ($bakSha -ne $old[$slot.name].sha) { throw "backup hash mismatch for $($slot.name): $bakSha vs $($old[$slot.name].sha)" }
                $lines += "$($slot.name): sha256 $bakSha (stamp $($old[$slot.name].productVersion))"
            } else {
                $lines += "$($slot.name): absent before this deploy (nothing to back up)"
            }
        }
        Write-TextFile (Join-Path $bakDir 'hashes.txt') ($lines -join "`r`n")
    } catch {
        Write-Log "REFUSED: backup could not be written/verified ($($_.Exception.Message)) - game folder untouched"
        Write-Outcome @{ outcome = 'refused-backup'; error = "$($_.Exception.Message)"; pin = $pin; backupDir = $bakDir; needsUserAction = 'none - deploy did not happen; fix the backup location and re-run' }
        exit 6
    }

    # Re-probe at the last moment: the game may have launched between the caller's probe and
    # here. Never write into the game folder while it runs.
    if (-not (Test-GameClosed $m.dst $m.gameProcessName)) { return 'busy' }

    try {
        Copy-Item $StagedDll $m.dst -Force
    } catch [System.IO.IOException] {
        return 'busy'
    } catch {
        Write-Log "COPY FAILED (non-lock): $($_.Exception.Message)"
        Write-Outcome @{ outcome = 'failed-copy'; error = "$($_.Exception.Message)"; pin = $pin }
        exit 4
    }
    $dstSha = Get-Sha $m.dst
    if ($dstSha -ne $pin) {
        Write-Log "CRITICAL: post-copy verify FAILED: dst=$dstSha pin=$pin"
        Write-Outcome @{ outcome = 'critical-verify-failed'; slot = 'rmlMods'; got = $dstSha; pin = $pin }
        exit 2
    }

    # BOTH slots, deliberately - and this is the OPPOSITE of relying on hot reload. The
    # HotReloadMods copy keeps the pair consistent so a stale second copy cannot be picked
    # up by a later hot_reload. Do not "simplify" this to a single copy: removing it
    # re-opens the stale-hot-reload hazard (see docs/dev/CONTRIBUTING.md, deploy policy).
    # Mirrored FROM the verified destination, not from the stage, so the two slots cannot
    # diverge even if the stage is replaced right now.
    try {
        Copy-Item $m.dst $m.dstHotReload -Force
    } catch {
        Write-Log "CRITICAL: HotReloadMods copy failed: $($_.Exception.Message) - slots inconsistent"
        Write-Outcome @{ outcome = 'critical-verify-failed'; slot = 'hotReloadMods'; error = "$($_.Exception.Message)"; pin = $pin }
        exit 2
    }
    $hrSha = Get-Sha $m.dstHotReload
    if ($hrSha -ne $pin) {
        Write-Log "CRITICAL: HotReloadMods verify FAILED: got=$hrSha pin=$pin"
        Write-Outcome @{ outcome = 'critical-verify-failed'; slot = 'hotReloadMods'; got = $hrSha; pin = $pin }
        exit 2
    }
    Write-Log "deployed: $($m.dst) and $($m.dstHotReload) sha256=$pin (verified on both)"

    # .PENDING is a LEGACY artifact: nothing writes it anymore (the build's half-done-deploy
    # machinery was removed with the auto-deploy, 2026-08-28). A note here can only be a
    # leftover from a pre-upgrade build, and a verified deploy makes it false - clean it up.
    Remove-Item "$($m.dst).PENDING" -Force -ErrorAction SilentlyContinue

    # Config seed: only requested keys, only when absent, logged by what was ACTUALLY added.
    $added = @()
    $want = [ordered]@{}
    if ($m.seedPromptDefaultOrg) { $want['promptDefaultOrg'] = $m.seedPromptDefaultOrg }
    if ($m.seedPromptHireDir) { $want['promptHireDir'] = $m.seedPromptHireDir }
    if ($want.Count -gt 0) {
        try {
            $j = Get-Content $m.cfg -Raw | ConvertFrom-Json
            if ($null -eq $j.values) { throw "no 'values' object in $($m.cfg)" }
            foreach ($k in $want.Keys) {
                if ($null -eq $j.values.PSObject.Properties[$k]) {
                    $j.values | Add-Member -NotePropertyName $k -NotePropertyValue $want[$k]
                    $added += $k
                }
            }
            if ($added.Count -gt 0) {
                Write-TextFile $m.cfg ($j | ConvertTo-Json -Depth 5)
                Write-Log ("seeded config keys: " + ($added -join ', '))
            } else {
                Write-Log 'config seed: nothing to do (all requested keys present)'
            }
        } catch {
            Write-Log "CONFIG SEED FAILED (deploy itself succeeded): $_"
        }
    }

    # Success criterion: files on disk in both slots; the user's NEXT LAUNCH runs this
    # build. Nothing here means or reports "the running game picked it up".
    Write-Outcome @{
        outcome         = 'deployed'
        pin             = $pin
        productVersion  = $m.productVersion
        versionLabel    = $m.versionLabel
        slots           = @{
            rmlMods       = @{ old = $old.rmlMods; new = $dstSha }
            hotReloadMods = @{ old = $old.hotReloadMods; new = $hrSha }
        }
        backupDir       = $bakDir
        seededKeys      = $added
        expectation     = @{
            expected     = @{ versionLabel = $m.versionLabel; productVersion = $m.productVersion; sha256 = $pin }
            neverArrived = @{ productVersion = $old.rmlMods.productVersion; sha256 = $old.rmlMods.sha }
            note         = 'three outcomes: expected values = arrived; neverArrived values = never arrived; NEITHER = wrong payload. Compare HASHES, not stamps alone - Debug and Release builds of one commit share a stamp.'
        }
        needsUserAction = 'none - the next game launch runs this build'
        resolved        = @{ src = $m.sourcePath; dst = $m.dst; dstHotReload = $m.dstHotReload; cfg = $m.cfg; stageDir = $StageDir; log = $m.log }
    }
    Remove-Item $ManifestPath -Force -ErrorAction SilentlyContinue
    return 'deployed'
}

function Invoke-Waiter {
    # One waiter per stage directory, machine-wide; a second instance exits immediately.
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $dirKey = -join ($md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($StageDir.ToLowerInvariant())) | ForEach-Object { $_.ToString('x2') })
    $created = $false
    $mutex = New-Object System.Threading.Mutex($true, "Global\McpLinkDeployWaiter_$dirKey", [ref]$created)
    if (-not $created) {
        Write-Log 'waiter: another waiter already holds this stage directory - exiting'
        exit 0
    }
    try {
        $corruptTicks = 0
        while ($true) {
            $m = Read-Manifest
            if ($null -eq $m) { Write-Log 'waiter: nothing staged - exiting'; exit 0 }
            if (-not (Test-Path $StagedDll)) { $stagedSha = 'missing' } else { $stagedSha = Get-Sha $StagedDll }
            if ($stagedSha -ne $m.pinSha256) {
                # Usually a replacement mid-write; give it a few ticks, then refuse loudly.
                $corruptTicks++
                if ($corruptTicks -ge 4) {
                    Write-Log "REFUSED: staged payload sha $stagedSha does not match manifest pin $($m.pinSha256) after $corruptTicks checks - corrupt stage, not deploying"
                    Write-Outcome @{ outcome = 'refused-corrupt-stage'; stagedSha = $stagedSha; pin = $m.pinSha256; needsUserAction = 'restage by running tools/deploy.ps1 again' }
                    exit 2
                }
                Start-Sleep -Seconds 2
                continue
            }
            $corruptTicks = 0
            if (Test-GameClosed $m.dst $m.gameProcessName) {
                $r = Invoke-Deploy $m
                if ($r -eq 'deployed') { exit 0 }
                # 'busy': game snuck back between probe and copy - fall through to wait.
            }
            Start-Sleep -Seconds ([int]$m.retrySeconds)
            # Loop re-reads the manifest, so a payload staged while we waited replaces the
            # old one automatically - the newest snapshot always wins.
        }
    } finally {
        $mutex.ReleaseMutex() | Out-Null
        $mutex.Dispose()
    }
}

# ======================= main =======================

if ($Waiter) { Invoke-Waiter; exit 0 }

if (-not (Test-Path $Src)) {
    Write-Log "SOURCE MISSING: $Src - nothing staged"
    Write-Outcome @{ outcome = 'failed-source-missing'; src = $Src }
    exit 5
}
$srcSha = Get-Sha $Src
if ($ExpectedSha256) {
    $want = $ExpectedSha256.Trim().ToUpperInvariant()
    if ($srcSha -ne $want) {
        Write-Log "REFUSED: source sha $srcSha does not match -ExpectedSha256 $want - nothing staged"
        Write-Outcome @{ outcome = 'refused-source-pin'; got = $srcSha; expected = $want }
        exit 3
    }
}

$productVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Src).ProductVersion
if (-not $SkipStampCheck) {
    # The payload must be a clean build of the repo's current HEAD. Fail-loud when the check
    # cannot run: an unverifiable stamp is a refusal, not a pass (a check that abstains
    # reads exactly like a pass - docs/dev/CONTRIBUTING.md, verification discipline).
    $git = $GitExe
    if (-not $git) {
        $cmd = Get-Command git.exe -ErrorAction SilentlyContinue
        if ($cmd) { $git = $cmd.Source }
        elseif (Test-Path 'C:\Program Files\Git\cmd\git.exe') { $git = 'C:\Program Files\Git\cmd\git.exe' }
    }
    $head = $null
    if ($git) { try { $head = (& $git -C $RepoPath rev-parse --short=12 HEAD 2>$null | Select-Object -First 1) } catch { $head = $null } }
    if (-not $head) {
        Write-Log "REFUSED: cannot resolve repo HEAD (git='$git', repo='$RepoPath') - stamp unverifiable. Pass -SkipStampCheck to override (loudly)."
        Write-Outcome @{ outcome = 'refused-stamp-unverifiable'; gitExe = "$git"; repoPath = $RepoPath }
        exit 8
    }
    $expectedStamp = "g$($head.Trim())"
    if ($productVersion -ne $expectedStamp) {
        Write-Log "REFUSED: payload stamp '$productVersion' is not '$expectedStamp' (repo HEAD, clean) - wrong or dirty build. Nothing staged."
        Write-Outcome @{ outcome = 'refused-stamp'; payloadStamp = "$productVersion"; expectedStamp = $expectedStamp; repoPath = $RepoPath }
        exit 8
    }
} else {
    Write-Log "WARNING: -SkipStampCheck passed - payload stamp '$productVersion' NOT verified against the repo"
}

# Stage the snapshot. This REPLACES any pending stage: idempotent by replacement.
if (-not (Test-Path $StageDir)) { New-Item -ItemType Directory -Force $StageDir | Out-Null }
Copy-Item $Src $StagedDll -Force
$pin = Get-Sha $StagedDll
$manifest = [ordered]@{
    schemaVersion       = 1
    pinSha256           = $pin
    productVersion      = "$productVersion"
    versionLabel        = $VersionLabel
    sourcePath          = $Src
    sourceShaAtStage    = $srcSha
    stagedAtUtc         = [DateTime]::UtcNow.ToString('o')
    dst                 = $Dst
    dstHotReload        = $DstHotReload
    cfg                 = $Cfg
    log                 = $Log
    seedPromptDefaultOrg = $SeedPromptDefaultOrg
    seedPromptHireDir   = $SeedPromptHireDir
    gameProcessName     = $GameProcessName
    retrySeconds        = $RetrySeconds
    stampChecked        = (-not $SkipStampCheck)
}
$tmp = "$ManifestPath.tmp"
Write-TextFile $tmp ($manifest | ConvertTo-Json -Depth 4)
Move-Item $tmp $ManifestPath -Force
Write-Log "staged: pin=$pin stamp=$productVersion (replaces any pending stage)"

$m = Read-Manifest
if (Test-GameClosed $Dst $GameProcessName) {
    $r = Invoke-Deploy $m
    if ($r -eq 'deployed') { exit 0 }
    # fell to 'busy': game appeared mid-deploy - arm the waiter below.
}

if ($WaiterLaunch -eq 'schtask') {
    try {
        $tr = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Waiter -StageDir `"$StageDir`""
        schtasks /create /f /tn $WaiterTaskName /sc once /st 23:59 /tr $tr | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "schtasks /create exited $LASTEXITCODE" }
        schtasks /run /tn $WaiterTaskName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "schtasks /run exited $LASTEXITCODE" }
    } catch {
        Write-Log "STAGED BUT WAITER NOT ARMED: $($_.Exception.Message) - the deploy will NOT happen until deploy.ps1 runs again"
        Write-Outcome @{ outcome = 'staged-no-waiter'; pin = $pin; productVersion = "$productVersion"; error = "$($_.Exception.Message)"; needsUserAction = 'waiter could not be armed - re-run tools/deploy.ps1' }
        exit 9
    }
}
Write-Log "staged-waiting: game is open; waiter mode '$WaiterLaunch' will deploy on close"
Write-Outcome @{
    outcome         = 'staged-waiting'
    pin             = $pin
    productVersion  = "$productVersion"
    versionLabel    = $VersionLabel
    waiterMode      = $WaiterLaunch
    needsUserAction = 'ask the user to close the game so the pending deploy can land (frame it as a request they can decline)'
    resolved        = @{ src = $Src; dst = $Dst; dstHotReload = $DstHotReload; cfg = $Cfg; stageDir = $StageDir; log = $Log }
}
exit 0
