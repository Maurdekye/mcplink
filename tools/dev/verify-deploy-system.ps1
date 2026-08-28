# Acceptance harness for tools/deploy-on-close.ps1 - the sandboxed demonstration that the
# game-close deploy actually works, kept runnable so "has the copier ever been shown to
# copy" stays permanently answered. Everything runs against a throwaway sandbox; production
# paths are never touched (the script under test receives sandbox arguments - the whole
# point of its parameterization).
#
# Case 1: destination locked (FileShare.Read, faithful to how the game holds the DLL:
#         readable but not writable) -> script retries; lock released -> copy lands in BOTH
#         slots, .PENDING removed, absent seed key added, present seed key NOT stomped, log
#         names only the actually-added key and uses actual paths. Exit 0.
# Case 2: pinned hash does not match the source -> refused, nothing written, .PENDING kept.
#         Exit 3.
# Case 3: source file missing -> loud immediate failure, not a silent retry loop. Exit 5.
#
# Exits 0 when every assertion passes, 1 otherwise.
param(
    [string]$SandboxRoot = (Join-Path $env:TEMP 'mcplink-deploy-close-verify')
)

$ErrorActionPreference = 'Stop'
$script:fails = 0
function Assert([bool]$cond, [string]$what) {
    if ($cond) { Write-Output "PASS: $what" } else { $script:fails++; Write-Output "FAIL: $what" }
}

$scriptUnderTest = Join-Path (Split-Path $PSScriptRoot -Parent) 'deploy-on-close.ps1'
if (-not (Test-Path $scriptUnderTest)) { Write-Output "FAIL: script under test not found at $scriptUnderTest"; exit 1 }

# --- fresh sandbox ---------------------------------------------------------------------
if (Test-Path $SandboxRoot) { Remove-Item $SandboxRoot -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $SandboxRoot 'dstdir\HotReloadMods') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $SandboxRoot 'cfgdir') | Out-Null
$src = Join-Path $SandboxRoot 'src-McpLink.dll'
$dst = Join-Path $SandboxRoot 'dstdir\McpLink.dll'
$dhr = Join-Path $SandboxRoot 'dstdir\HotReloadMods\McpLink.dll'
$cfg = Join-Path $SandboxRoot 'cfgdir\McpLink.json'
$log = Join-Path $SandboxRoot 'verify-log.txt'

$r1 = New-Object byte[] 300000; (New-Object System.Random).NextBytes($r1); [System.IO.File]::WriteAllBytes($src, $r1)
$r2 = New-Object byte[] 250000; (New-Object System.Random).NextBytes($r2); [System.IO.File]::WriteAllBytes($dst, $r2)
Copy-Item $dst $dhr -Force
Set-Content "$dst.PENDING" 'sentinel: must be deleted only by a verified deploy' -Encoding ascii
Set-Content $cfg '{"values":{"promptHireDir":"SENTINEL-do-not-stomp"}}' -Encoding ascii
$srcHash = (Get-FileHash $src -Algorithm SHA256).Hash
$oldDstHash = (Get-FileHash $dst -Algorithm SHA256).Hash

function Invoke-Deploy([string[]]$extraArgs, [switch]$NoWait) {
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $scriptUnderTest,
              '-Dst', $dst, '-DstHotReload', $dhr, '-Cfg', $cfg, '-Log', $log,
              '-RetrySeconds', '8') + $extraArgs
    if ($NoWait) { return Start-Process powershell -ArgumentList $args -PassThru -WindowStyle Hidden }
    $p = Start-Process powershell -ArgumentList $args -PassThru -Wait -WindowStyle Hidden
    return $p
}

# --- case 1: happy path under lock -----------------------------------------------------
Write-Output '--- case 1: locked destination, correct pin ---'
$lockCmd = "`$f = [System.IO.File]::Open('$dst', 'Open', 'Read', 'Read'); Start-Sleep 25; `$f.Close()"
$locker = Start-Process powershell -ArgumentList '-NoProfile', '-Command', $lockCmd -PassThru -WindowStyle Hidden
Start-Sleep 2
$run = Invoke-Deploy @('-ExpectedSha256', $srcHash, '-Src', $src, '-SeedPromptDefaultOrg', 'resonite') -NoWait
Start-Sleep 5
Assert (-not $run.HasExited) 'case1: script still waiting while dst is locked'
Assert ((Get-FileHash $dst -Algorithm SHA256).Hash -eq $oldDstHash) 'case1: dst bytes untouched while locked'
$run.WaitForExit(60000) | Out-Null
if (-not $run.HasExited) { Stop-Process -Id $run.Id -Force; Assert $false 'case1: script exited within 60s' }
else { Assert ($run.ExitCode -eq 0) "case1: exit code 0 (got $($run.ExitCode))" }
if (-not $locker.HasExited) { Stop-Process -Id $locker.Id -Force }
Assert ((Get-FileHash $dst -Algorithm SHA256).Hash -eq $srcHash) 'case1: dst deployed to pinned bytes'
Assert ((Get-FileHash $dhr -Algorithm SHA256).Hash -eq $srcHash) 'case1: HotReloadMods slot deployed (pairing)'
Assert (-not (Test-Path "$dst.PENDING")) 'case1: .PENDING removed after verified deploy'
$j = Get-Content $cfg -Raw | ConvertFrom-Json
Assert ($j.values.promptDefaultOrg -eq 'resonite') 'case1: absent seed key added'
Assert ($j.values.promptHireDir -eq 'SENTINEL-do-not-stomp') 'case1: present seed key not stomped'
$logText = Get-Content $log -Raw
Assert ($logText -match 'seeded config keys: promptDefaultOrg\s*$' -or $logText -match 'seeded config keys: promptDefaultOrg\r?\n') 'case1: seed log names ONLY the actually-added key'
Assert ($logText -match [regex]::Escape($dst)) 'case1: log prose uses the actual destination path'

# --- case 2: pin mismatch is refused ---------------------------------------------------
Write-Output '--- case 2: pin mismatch ---'
[System.IO.File]::WriteAllBytes($dst, $r2)
Copy-Item $dst $dhr -Force
Set-Content "$dst.PENDING" 'sentinel again' -Encoding ascii
$wrongPin = ('0' * 64)
$p2 = Invoke-Deploy @('-ExpectedSha256', $wrongPin, '-Src', $src)
Assert ($p2.ExitCode -eq 3) "case2: refused with exit 3 (got $($p2.ExitCode))"
Assert ((Get-FileHash $dst -Algorithm SHA256).Hash -eq $oldDstHash) 'case2: dst untouched on refusal'
Assert (Test-Path "$dst.PENDING") 'case2: .PENDING left in place on refusal'
Assert ((Get-Content $log -Raw) -match 'REFUSED') 'case2: refusal logged'

# --- case 3: missing source fails loudly, not silently ---------------------------------
Write-Output '--- case 3: missing source ---'
$p3 = Invoke-Deploy @('-ExpectedSha256', $srcHash, '-Src', (Join-Path $SandboxRoot 'does-not-exist.dll'))
Assert ($p3.ExitCode -eq 5) "case3: missing source exits 5 (got $($p3.ExitCode))"
Assert ((Get-Content $log -Raw) -match 'SOURCE MISSING') 'case3: missing source logged'

Write-Output ''
if ($script:fails -eq 0) { Write-Output 'ALL CHECKS PASSED'; exit 0 }
else { Write-Output "$($script:fails) CHECKS FAILED"; exit 1 }
