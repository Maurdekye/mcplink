# Control for the "builds never deploy" gate (user ruling 2026-08-28; docs/dev/CONTRIBUTING.md
# deploy policy): a plain `dotnet build` - in ANY tree, canonical included - must leave the
# game folder untouched; only an explicit -p:StageHotReload=true writes, and then ONLY the
# hot-reload slot. Replaces verify-deploy-warning.sh, whose subject (the DeployToMods
# locked-copy warning MCPLINK001) was removed together with the build auto-deploy it warned
# about. The known-positive for this gate is the 2026-08-28 incident itself: a canonical
# Debug build that DID deploy, which is exactly what this control must show cannot recur.
#
# Case 0 proves the detector can fail (a diff scanner that cannot go red makes every later
# PASS vacuous). Run from any checkout; the sandbox stands in for the game folder via
# -p:ModsDeployRoot, which redirects the staging target without weakening the opt-in gate.
param(
    [string]$SandboxRoot = (Join-Path $env:TEMP 'mcplink-build-never-deploys')
)

$ErrorActionPreference = 'Stop'
$script:fails = 0
function Assert([bool]$cond, [string]$what) {
    if ($cond) { Write-Output "PASS: $what" } else { $script:fails++; Write-Output "FAIL: $what" }
}
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$csproj = Join-Path $repoRoot 'McpLink.csproj'
if (-not (Test-Path $csproj)) { Write-Output "FAIL: McpLink.csproj not found at $csproj"; exit 1 }

function Snapshot {
    $m = @{}
    Get-ChildItem $SandboxRoot -Recurse -File | ForEach-Object { $m[$_.FullName] = (Get-FileHash $_.FullName -Algorithm SHA256).Hash }
    return $m
}
function Diff-Snapshots($a, $b) {
    $d = @()
    foreach ($k in $b.Keys) { if (-not $a.ContainsKey($k)) { $d += "added: $k" } elseif ($a[$k] -ne $b[$k]) { $d += "changed: $k" } }
    foreach ($k in $a.Keys) { if (-not $b.ContainsKey($k)) { $d += "removed: $k" } }
    return $d
}

if (Test-Path $SandboxRoot) { Remove-Item $SandboxRoot -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $SandboxRoot 'rml_mods\HotReloadMods') | Out-Null
$r = New-Object byte[] 4096; (New-Object System.Random).NextBytes($r)
[System.IO.File]::WriteAllBytes((Join-Path $SandboxRoot 'rml_mods\McpLink.dll'), $r)
[System.IO.File]::WriteAllBytes((Join-Path $SandboxRoot 'rml_mods\HotReloadMods\McpLink.dll'), $r)
$base = Snapshot

Write-Output '--- case 0: the detector can fail (known-positive control) ---'
Add-Content (Join-Path $SandboxRoot 'rml_mods\McpLink.dll') 'x' -Encoding ascii
$mut = Snapshot
Assert ((Diff-Snapshots $base $mut).Count -eq 1) 'case0: comparator detects a planted change'
[System.IO.File]::WriteAllBytes((Join-Path $SandboxRoot 'rml_mods\McpLink.dll'), $r)
$base = Snapshot

Write-Output '--- case 1: plain build writes NOTHING into the game folder ---'
dotnet build $csproj -c Debug -p:ModsDeployRoot="$SandboxRoot" --nologo -v:q | Out-Null
Assert ($LASTEXITCODE -eq 0) 'case1: build succeeded'
$d1 = @(Diff-Snapshots $base (Snapshot))
Assert ($d1.Count -eq 0) ("case1: game folder untouched by a default build (diff: " + ($d1 -join '; ') + ")")

Write-Output '--- case 2: -p:StageHotReload=true writes ONLY the hot-reload slot ---'
dotnet build $csproj -c Debug -p:ModsDeployRoot="$SandboxRoot" -p:StageHotReload=true --nologo -v:q | Out-Null
Assert ($LASTEXITCODE -eq 0) 'case2: build succeeded'
$after2 = Snapshot
# @() because PowerShell unrolls a single-element return into a bare string, whose [0] is a
# CHARACTER - the assertion below would then compare 'c' against the path pattern and fail
# on a correct diff (found on this harness's first run).
$d2 = @(Diff-Snapshots $base $after2)
Assert ($d2.Count -eq 1 -and $d2[0] -match 'HotReloadMods\\McpLink\.dll$') ("case2: exactly the hot-reload slot changed (diff: " + ($d2 -join '; ') + ")")
$mainSlot = Join-Path $SandboxRoot 'rml_mods\McpLink.dll'
Assert ($after2[$mainSlot] -eq $base[$mainSlot]) 'case2: rml_mods\McpLink.dll untouched (tools/deploy.ps1 exclusive property)'

Write-Output ''
if ($script:fails -eq 0) { Write-Output 'ALL CHECKS PASSED'; exit 0 }
else { Write-Output "$($script:fails) CHECKS FAILED"; exit 1 }
