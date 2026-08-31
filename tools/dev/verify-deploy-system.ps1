# Acceptance harness for tools/deploy.ps1 - the deploy system for every McpLink deploy.
# Every check here follows the repo's verification discipline: each gate is proven able to
# FAIL (a control), not just observed passing. Everything runs against a throwaway sandbox;
# production paths are never touched - the script under test receives sandbox arguments,
# which is the whole point of its parameterization.
#
#  case 0  closed game       -> immediate deploy: both slots, verified backups, .PENDING gone,
#                               seed fidelity, outcome JSON carries hashes + expectation pair
#  case 1  backup HARD GATE  -> backups impossible => deploy REFUSES, game folder untouched;
#                               then unblocked => deploys (the gate can fail AND can pass)
#  case 2  stamp gate        -> unstamped payload REFUSED (positive leg is environment-gated
#                               and skips LOUDLY when no clean stamped payload exists)
#  case 3  process leg       -> game "running" by process name => stages, does not deploy
#  case 4  waiting path      -> locked dst: does NOT deploy while locked, DOES deploy on
#                               release; manifest consumed; second waiter exits (singleton)
#  case 5  IDEMPOTENT REPLACEMENT (user's exact requirement) -> stage A, stage B while A is
#                               pending and the waiter is already waiting, release the lock,
#                               prove B landed and A did NOT
#  case 6  corrupt stage     -> staged bytes != pin => waiter refuses even with the lock
#                               released; game folder untouched
#  case 7  source pin        -> -ExpectedSha256 mismatch refuses before staging
#  case 8  missing source    -> loud immediate failure
#  case 9  task lifecycle    -> scheduled child receives the resolved NON-PRODUCTION task
#                               identity and deletes it on completion/refusal/early exit;
#                               a direct waiter never invokes task cleanup
#
# Exits 0 when every assertion passes, 1 otherwise.
param(
    [string]$SandboxRoot = (Join-Path $env:TEMP 'mcplink-deploy-system-verify')
)

$ErrorActionPreference = 'Stop'
$script:fails = 0
function Assert([bool]$cond, [string]$what) {
    if ($cond) { Write-Output "PASS: $what" } else { $script:fails++; Write-Output "FAIL: $what" }
}

$scriptUnderTest = Join-Path (Split-Path $PSScriptRoot -Parent) 'deploy.ps1'
if (-not (Test-Path $scriptUnderTest)) { Write-Output "FAIL: script under test not found at $scriptUnderTest"; exit 1 }

if (Test-Path $SandboxRoot) { Remove-Item $SandboxRoot -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $SandboxRoot 'game\rml_mods\HotReloadMods') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $SandboxRoot 'cfg') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $SandboxRoot 'stage') | Out-Null
$srcA = Join-Path $SandboxRoot 'payload-a.dll'
$srcB = Join-Path $SandboxRoot 'payload-b.dll'
$dst = Join-Path $SandboxRoot 'game\rml_mods\McpLink.dll'
$dhr = Join-Path $SandboxRoot 'game\rml_mods\HotReloadMods\McpLink.dll'
$cfg = Join-Path $SandboxRoot 'cfg\McpLink.json'
$stage = Join-Path $SandboxRoot 'stage'
$log = Join-Path $SandboxRoot 'deploy-log.txt'
$stagedDll = Join-Path $stage 'staged-McpLink.dll'
$manifest = Join-Path $stage 'staged.json'
$outcomeFile = Join-Path $stage 'last-deploy.json'
$fakeSchtasks = Join-Path $SandboxRoot 'fake-schtasks.cmd'
$fakeSchtasksLog = Join-Path $SandboxRoot 'fake-schtasks.log'
$fakeSchtasksBody = "@echo off`r`necho %*>>`"$fakeSchtasksLog`"`r`nexit /b 0`r`n"
[System.IO.File]::WriteAllText($fakeSchtasks, $fakeSchtasksBody, [System.Text.Encoding]::ASCII)
$failingSchtasks = Join-Path $SandboxRoot 'failing-schtasks.cmd'
$failingSchtasksBody = "@echo off`r`necho %*>>`"$fakeSchtasksLog`"`r`nexit /b 5`r`n"
[System.IO.File]::WriteAllText($failingSchtasks, $failingSchtasksBody, [System.Text.Encoding]::ASCII)

$rA = New-Object byte[] 300000; (New-Object System.Random).NextBytes($rA); [System.IO.File]::WriteAllBytes($srcA, $rA)
$rB = New-Object byte[] 310000; (New-Object System.Random).NextBytes($rB); [System.IO.File]::WriteAllBytes($srcB, $rB)
$rOld = New-Object byte[] 250000; (New-Object System.Random).NextBytes($rOld)
$hashA = (Get-FileHash $srcA -Algorithm SHA256).Hash
$hashB = (Get-FileHash $srcB -Algorithm SHA256).Hash

function Reset-GameDir {
    [System.IO.File]::WriteAllBytes($dst, $rOld)
    [System.IO.File]::WriteAllBytes($dhr, $rOld)
    Set-Content "$dst.PENDING" 'sentinel: deleted only by a verified deploy' -Encoding ascii
    Set-Content $cfg '{"values":{"promptHireDir":"SENTINEL-do-not-stomp"}}' -Encoding ascii
    Remove-Item $manifest -Force -ErrorAction SilentlyContinue
}
$oldHash = $null

# Common arguments: everything explicit - a check that inherits ambient environment is
# abstaining, not testing. The fake process name simulates "game closed".
# Start-Process joins -ArgumentList with SPACES and does NOT quote, so any argument
# containing a space (e.g. a git.exe under Program Files) must be wrapped here or it
# arrives truncated - found when '-GitExe C:\Program Files\...' reached the script as
# 'C:\Program' and the stamp gate (correctly) refused as unverifiable.
function Quote-Args([string[]]$argv) {
    return ($argv | ForEach-Object { if ($_ -match ' ') { '"' + $_ + '"' } else { $_ } })
}
function Deploy-Args([string[]]$extra, [string]$proc = 'McpLinkNoSuchProcess', [string]$waiterLaunch = 'none') {
    return @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scriptUnderTest,
             '-Dst', $dst, '-DstHotReload', $dhr, '-Cfg', $cfg, '-StageDir', $stage,
             '-Log', $log, '-RetrySeconds', '5', '-WaiterLaunch', $waiterLaunch,
             '-GameProcessName', $proc) + $extra
}
function Invoke-DeployScript([string[]]$extra, [string]$proc = 'McpLinkNoSuchProcess', [string]$waiterLaunch = 'none') {
    return Start-Process powershell -ArgumentList (Quote-Args (Deploy-Args $extra $proc $waiterLaunch)) -PassThru -Wait -WindowStyle Hidden
}
function Start-WaiterProc([string]$taskName = '', [string]$schedulerExe = '', [bool]$scheduledOwner = $true) {
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scriptUnderTest,
              '-Waiter', '-StageDir', $stage, '-Log', $log)
    if ($taskName) {
        if (-not $schedulerExe) { $schedulerExe = $fakeSchtasks }
        $argv += @('-WaiterTaskName', $taskName, '-SchtasksExe', $schedulerExe)
        if ($scheduledOwner) { $argv += '-ScheduledWaiter' }
    }
    return Start-Process powershell -ArgumentList (Quote-Args $argv) -PassThru -WindowStyle Hidden
}
function Get-Outcome { return (Get-Content $outcomeFile -Raw | ConvertFrom-Json) }
function Clear-FakeSchtasksLog { Remove-Item $fakeSchtasksLog -Force -ErrorAction SilentlyContinue }
function Get-FakeSchtasksLines {
    if (Test-Path $fakeSchtasksLog) { return @(Get-Content $fakeSchtasksLog) }
    return @()
}
function Saw-TaskDelete([string]$taskName) {
    $want = "/delete /f /tn $taskName"
    return @((Get-FakeSchtasksLines) | Where-Object { $_ -eq $want }).Count -eq 1
}
function Lock-Dst([int]$seconds) {
    $cmd = "`$f = [System.IO.File]::Open('$dst', 'Open', 'Read', 'Read'); Start-Sleep $seconds; `$f.Close()"
    $p = Start-Process powershell -ArgumentList '-NoProfile', '-Command', $cmd -PassThru -WindowStyle Hidden
    Start-Sleep 2
    return $p
}

# ---- case 0: game closed => immediate deploy --------------------------------------------
Write-Output '--- case 0: immediate deploy when the game is closed ---'
Reset-GameDir
$oldHash = (Get-FileHash $dst -Algorithm SHA256).Hash
$p = Invoke-DeployScript @('-Src', $srcA, '-SkipStampCheck', '-SeedPromptDefaultOrg', 'resonite')
Assert ($p.ExitCode -eq 0) "case0: exit 0 (got $($p.ExitCode))"
Assert ((Get-FileHash $dst -Algorithm SHA256).Hash -eq $hashA) 'case0: rml_mods slot = payload'
Assert ((Get-FileHash $dhr -Algorithm SHA256).Hash -eq $hashA) 'case0: HotReloadMods slot = payload (pair consistent)'
Assert (-not (Test-Path "$dst.PENDING")) 'case0: stale .PENDING removed'
Assert (-not (Test-Path $manifest)) 'case0: manifest consumed after deploy'
$o = Get-Outcome
Assert ($o.outcome -eq 'deployed') 'case0: outcome = deployed'
Assert ($o.pin -eq $hashA) 'case0: outcome pin = payload hash'
Assert ($o.slots.rmlMods.old.sha -eq $oldHash) 'case0: outcome records old slot hash'
Assert ($o.expectation.neverArrived.sha256 -eq $oldHash) 'case0: expectation pair carries never-arrived hash'
Assert ($o.expectation.expected.sha256 -eq $hashA) 'case0: expectation pair carries expected hash'
Assert ($o.resolved.dst -eq $dst) 'case0: outcome echoes the RESOLVED dst (no ambient paths)'
Assert (([System.IO.File]::ReadAllBytes($outcomeFile))[0] -eq 0x7B) 'case0: outcome JSON is BOM-less (first byte is {, strict-parser safe)'
$bakDir = $o.backupDir
Assert ((Test-Path (Join-Path $bakDir 'rmlMods-McpLink.dll')) -and ((Get-FileHash (Join-Path $bakDir 'rmlMods-McpLink.dll') -Algorithm SHA256).Hash -eq $oldHash)) 'case0: rml_mods backup exists and hash-matches the outgoing file'
Assert ((Test-Path (Join-Path $bakDir 'hotReloadMods-McpLink.dll')) -and ((Get-FileHash (Join-Path $bakDir 'hotReloadMods-McpLink.dll') -Algorithm SHA256).Hash -eq $oldHash)) 'case0: HotReloadMods backup exists and hash-matches'
Assert (Test-Path (Join-Path $bakDir 'hashes.txt')) 'case0: backup hashes.txt written'
$j = Get-Content $cfg -Raw | ConvertFrom-Json
Assert ($j.values.promptDefaultOrg -eq 'resonite') 'case0: absent seed key added'
Assert ($j.values.promptHireDir -eq 'SENTINEL-do-not-stomp') 'case0: present seed key not stomped'
Assert (@($o.seededKeys).Count -eq 1 -and @($o.seededKeys)[0] -eq 'promptDefaultOrg') 'case0: outcome names ONLY the actually-added seed key'

# ---- case 1: backup hard gate ------------------------------------------------------------
Write-Output '--- case 1: backup hard gate refuses, then passes when unblocked ---'
Reset-GameDir
# a FILE named "backups" makes the backup directory impossible to create
$blocker = Join-Path $stage 'backups'
if (Test-Path $blocker) { Remove-Item $blocker -Recurse -Force }
Set-Content $blocker 'block' -Encoding ascii
$p = Invoke-DeployScript @('-Src', $srcA, '-SkipStampCheck')
Assert ($p.ExitCode -eq 6) "case1: refused with exit 6 (got $($p.ExitCode))"
$o = Get-Outcome
Assert ($o.outcome -eq 'refused-backup') 'case1: outcome = refused-backup'
Assert ((Get-FileHash $dst -Algorithm SHA256).Hash -eq $oldHash) 'case1: game folder untouched on refusal'
Assert (Test-Path "$dst.PENDING") 'case1: .PENDING untouched on refusal'
Remove-Item $blocker -Force
$p = Invoke-DeployScript @('-Src', $srcA, '-SkipStampCheck')
Assert ($p.ExitCode -eq 0 -and (Get-FileHash $dst -Algorithm SHA256).Hash -eq $hashA) 'case1: same deploy succeeds once the backup can be written (gate discriminates)'

# ---- case 2: stamp gate ------------------------------------------------------------------
Write-Output '--- case 2: stamp gate ---'
Reset-GameDir
$gitExe = (Get-Command git.exe).Source
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$p = Invoke-DeployScript @('-Src', $srcA, '-RepoPath', $repoRoot, '-GitExe', $gitExe)
Assert ($p.ExitCode -eq 8) "case2: unstamped payload refused with exit 8 (got $($p.ExitCode))"
$o = Get-Outcome
Assert ($o.outcome -eq 'refused-stamp') 'case2: outcome = refused-stamp'
Assert ((Get-FileHash $dst -Algorithm SHA256).Hash -eq $oldHash) 'case2: game folder untouched'
Assert (-not (Test-Path $manifest)) 'case2: nothing staged on stamp refusal'
# positive leg: needs a payload whose stamp equals a clean repo HEAD. Environment-gated;
# skipping is LOUD so it cannot be mistaken for a pass.
$head = (& $gitExe -C $repoRoot rev-parse --short=12 HEAD).Trim()
$dirty = (& $gitExe -C $repoRoot status --porcelain --untracked-files=no | Measure-Object).Count
$candidate = Join-Path $repoRoot 'bin\Release\McpLink.dll'
$stampOk = $false
if ($dirty -eq 0 -and (Test-Path $candidate)) {
    $pv = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($candidate).ProductVersion
    if ($pv -eq "g$head") { $stampOk = $true }
}
if ($stampOk) {
    $p = Invoke-DeployScript @('-Src', $candidate, '-RepoPath', $repoRoot, '-GitExe', $gitExe)
    Assert ($p.ExitCode -eq 0) 'case2: stamped clean payload passes the gate'
} else {
    Write-Output "SKIP (loud): case2 positive leg - no payload with stamp g$head against a clean tree (dirty=$dirty). Run this harness from a clean tree with a fresh Release build to exercise it."
}

# ---- case 3: process leg of the closed-check ---------------------------------------------
Write-Output '--- case 3: a live game process blocks immediate deploy ---'
Reset-GameDir
$p = Invoke-DeployScript @('-Src', $srcA, '-SkipStampCheck') -proc 'powershell'
Assert ($p.ExitCode -eq 0) 'case3: exit 0 (staged is a success)'
$o = Get-Outcome
Assert ($o.outcome -eq 'staged-waiting') 'case3: outcome = staged-waiting (process present, lock free)'
Assert ((Get-FileHash $dst -Algorithm SHA256).Hash -eq $oldHash) 'case3: game folder untouched while "game" runs'

# ---- case 4: waiting path fires + waiter singleton ---------------------------------------
Write-Output '--- case 4: locked destination - waits, then deploys on release ---'
Reset-GameDir
$locker = Lock-Dst 20
$p = Invoke-DeployScript @('-Src', $srcA, '-SkipStampCheck')
Assert ($p.ExitCode -eq 0) 'case4: staging call exits 0'
Assert ((Get-Outcome).outcome -eq 'staged-waiting') 'case4: outcome = staged-waiting'
Clear-FakeSchtasksLog
$case4Task = 'McpLinkDeployWaiter_VerifySuccess'
$w1 = Start-WaiterProc $case4Task
Start-Sleep 4
Assert (-not $w1.HasExited) 'case4: waiter alive while dst locked'
Assert ((Get-FileHash $dst -Algorithm SHA256).Hash -eq $oldHash) 'case4: did NOT deploy while locked'
$w2 = Start-WaiterProc
$w2.WaitForExit(8000) | Out-Null
Assert ($w2.HasExited -and $w2.ExitCode -eq 0 -and -not $w1.HasExited) 'case4: second waiter exits immediately (singleton), first still waiting'
$w1.WaitForExit(60000) | Out-Null
if (-not $w1.HasExited) { Stop-Process -Id $w1.Id -Force; Assert $false 'case4: waiter finished within 60s' }
else { Assert ($w1.ExitCode -eq 0) "case4: waiter exit 0 (got $($w1.ExitCode))" }
if (-not $locker.HasExited) { Stop-Process -Id $locker.Id -Force }
Assert ((Get-FileHash $dst -Algorithm SHA256).Hash -eq $hashA) 'case4: deployed after lock release'
Assert ((Get-FileHash $dhr -Algorithm SHA256).Hash -eq $hashA) 'case4: pair consistent'
Assert (-not (Test-Path $manifest)) 'case4: manifest consumed'
Assert (Saw-TaskDelete $case4Task) 'case4: successful scheduled waiter deletes its exact non-production task name'

# ---- case 5: idempotent replacement (stage B while A pending and waiter waiting) ---------
Write-Output '--- case 5: IDEMPOTENT REPLACEMENT - B replaces pending A, B lands, A does not ---'
Reset-GameDir
$locker = Lock-Dst 25
$p = Invoke-DeployScript @('-Src', $srcA, '-SkipStampCheck')
Assert ((Get-Content $manifest -Raw | ConvertFrom-Json).pinSha256 -eq $hashA) 'case5: A staged (pin = A)'
$w1 = Start-WaiterProc
Start-Sleep 3
Assert (-not $w1.HasExited) 'case5: waiter waiting on the lock'
$p = Invoke-DeployScript @('-Src', $srcB, '-SkipStampCheck')
$m = Get-Content $manifest -Raw | ConvertFrom-Json
Assert ($m.pinSha256 -eq $hashB) 'case5: restaging replaced the pin (now B)'
Assert ((Get-FileHash $stagedDll -Algorithm SHA256).Hash -eq $hashB) 'case5: staged payload bytes are B'
$w1.WaitForExit(60000) | Out-Null
if (-not $w1.HasExited) { Stop-Process -Id $w1.Id -Force; Assert $false 'case5: waiter finished within 60s' }
if (-not $locker.HasExited) { Stop-Process -Id $locker.Id -Force }
$final = (Get-FileHash $dst -Algorithm SHA256).Hash
Assert ($final -eq $hashB) 'case5: B landed'
Assert ($final -ne $hashA) 'case5: A did NOT land'
Assert ((Get-Outcome).pin -eq $hashB) 'case5: outcome records B as the deployed pin'

# ---- case 6: corrupt stage refused even with the lock gone -------------------------------
Write-Output '--- case 6: corrupt staged payload is refused ---'
Reset-GameDir
$locker = Lock-Dst 5
$p = Invoke-DeployScript @('-Src', $srcA, '-SkipStampCheck')
Add-Content $stagedDll 'CORRUPTION' -Encoding ascii
Clear-FakeSchtasksLog
$case6Task = 'McpLinkDeployWaiter_VerifyRefusal'
$w1 = Start-WaiterProc $case6Task
$w1.WaitForExit(45000) | Out-Null
if (-not $w1.HasExited) { Stop-Process -Id $w1.Id -Force; Assert $false 'case6: waiter exited' }
else { Assert ($w1.ExitCode -eq 2) "case6: refused with exit 2 (got $($w1.ExitCode))" }
Assert ((Get-Outcome).outcome -eq 'refused-corrupt-stage') 'case6: outcome = refused-corrupt-stage'
Assert ((Get-FileHash $dst -Algorithm SHA256).Hash -eq $oldHash) 'case6: game folder untouched (lock was long gone)'
Assert (Test-Path $manifest) 'case6: manifest kept for a restage'
Assert (Saw-TaskDelete $case6Task) 'case6: corrupt-stage exit 2 still deletes its exact non-production task name'

# ---- case 7: -ExpectedSha256 refuses before staging --------------------------------------
Write-Output '--- case 7: source pin mismatch ---'
Reset-GameDir
$p = Invoke-DeployScript @('-Src', $srcA, '-SkipStampCheck', '-ExpectedSha256', ('0' * 64))
Assert ($p.ExitCode -eq 3) "case7: refused with exit 3 (got $($p.ExitCode))"
Assert (-not (Test-Path $manifest)) 'case7: nothing staged'
Assert ((Get-FileHash $dst -Algorithm SHA256).Hash -eq $oldHash) 'case7: game folder untouched'

# ---- case 8: missing source --------------------------------------------------------------
Write-Output '--- case 8: missing source fails loudly ---'
$p = Invoke-DeployScript @('-Src', (Join-Path $SandboxRoot 'does-not-exist.dll'), '-SkipStampCheck')
Assert ($p.ExitCode -eq 5) "case8: exit 5 (got $($p.ExitCode))"
Assert ((Get-Outcome).outcome -eq 'failed-source-missing') 'case8: outcome = failed-source-missing'

# ---- case 9: scheduled task identity + early-exit cleanup -------------------------------
Write-Output '--- case 9: scheduled waiter task is self-cleaning; direct waiter is not an owner ---'
Reset-GameDir
Clear-FakeSchtasksLog
$case9Task = 'McpLinkDeployWaiter_VerifyEarlyExit'
$p = Invoke-DeployScript @('-Src', $srcA, '-SkipStampCheck',
                            '-WaiterTaskName', $case9Task, '-SchtasksExe', $fakeSchtasks) `
                         -proc 'powershell' -waiterLaunch 'schtask'
Assert ($p.ExitCode -eq 0) 'case9: parent stages successfully through the fake scheduler'
$taskLines = Get-FakeSchtasksLines
$createLine = @($taskLines | Where-Object { $_ -like '/create *' } | Select-Object -First 1)
$runLine = @($taskLines | Where-Object { $_ -like '/run *' } | Select-Object -First 1)
Assert ($createLine.Count -eq 1) 'case9: fake scheduler observed exactly one create command'
Assert ($runLine.Count -eq 1 -and $runLine[0] -eq "/run /tn $case9Task") 'case9: run command uses the resolved non-production task name'
$action = if ($createLine.Count -eq 1) { $createLine[0] } else { '' }
Assert ($action -match [regex]::Escape("/tn $case9Task")) 'case9: create command uses the resolved non-production task name'
Assert ($action -match [regex]::Escape('-ScheduledWaiter')) 'case9: scheduled child is explicitly marked as the task owner'
Assert ($action -match [regex]::Escape("-WaiterTaskName `"$case9Task`"")) 'case9: scheduled child receives the exact resolved task name'
Assert ($action -notmatch [regex]::Escape('-SchtasksExe')) 'case9: task action stays below the scheduler limit by using the child scheduler default'

# Remove the stage so the scheduled child takes Invoke-Waiter's earliest exit. The outer finally
# must still delete the task definition. No production task name appears anywhere in this leg.
Remove-Item $manifest -Force
Clear-FakeSchtasksLog
$w1 = Start-WaiterProc $case9Task
$w1.WaitForExit(15000) | Out-Null
if (-not $w1.HasExited) { Stop-Process -Id $w1.Id -Force; Assert $false 'case9: no-manifest scheduled waiter exited within 15s' }
else { Assert ($w1.ExitCode -eq 0) "case9: no-manifest scheduled waiter exit 0 (got $($w1.ExitCode))" }
Assert (Saw-TaskDelete $case9Task) 'case9: no-manifest early exit deletes its exact non-production task name'

# Positive discriminator for ownership: the same direct waiter path, without ScheduledWaiter,
# must not invoke any scheduler command. This protects a real pending task from test/manual waiters.
Clear-FakeSchtasksLog
$case9DirectTask = 'McpLinkDeployWaiter_VerifyDirectNonOwner'
$w1 = Start-WaiterProc $case9DirectTask $fakeSchtasks $false
$w1.WaitForExit(15000) | Out-Null
if (-not $w1.HasExited) { Stop-Process -Id $w1.Id -Force; Assert $false 'case9: direct waiter exited within 15s' }
else { Assert ($w1.ExitCode -eq 0) "case9: direct waiter exit 0 (got $($w1.ExitCode))" }
Assert ((Get-FakeSchtasksLines).Count -eq 0) 'case9 CONTROL: direct waiter never touches a scheduled task'

# Cleanup is best-effort: a scheduler failure is loud, but cannot replace the waiter's result.
Clear-FakeSchtasksLog
$case9FailureTask = 'McpLinkDeployWaiter_VerifyCleanupFailure'
$w1 = Start-WaiterProc $case9FailureTask $failingSchtasks
$w1.WaitForExit(15000) | Out-Null
if (-not $w1.HasExited) { Stop-Process -Id $w1.Id -Force; Assert $false 'case9: cleanup-failure waiter exited within 15s' }
else { Assert ($w1.ExitCode -eq 0) "case9: cleanup failure preserves the no-manifest exit 0 (got $($w1.ExitCode))" }
$failureLog = if (Test-Path $log) { Get-Content $log -Raw } else { '' }
Assert ($failureLog -match [regex]::Escape("WAITER CLEANUP FAILED:") -and
        $failureLog -match [regex]::Escape($case9FailureTask)) `
       'case9: cleanup failure is logged loudly with the exact task identity'

Write-Output ''
if ($script:fails -eq 0) { Write-Output 'ALL CHECKS PASSED'; exit 0 }
else { Write-Output "$($script:fails) CHECKS FAILED"; exit 1 }
