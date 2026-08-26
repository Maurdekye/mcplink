# The STANDARD McpLink release task: every VERSION increase ships a GitHub Release
# (standing rule, 2026-08-26). One command takes a bumped version from "committed on main"
# to "published on the Releases page with both assets":
#
#   checks   VERSION bumped vs the latest published release, a matching CHANGELOG entry,
#            HEAD == main, clean tree, gh authenticated with push access
#   builds   package.ps1 (offline smoke suite is the gate) with deploys pinned OFF --
#            releasing never touches the live game install; local deploys stay the
#            separate existing machinery
#   ships    tag v<version> -> push main + tag -> GitHub Release with McpLink-<v>.zip
#            AND the bare McpLink.dll, notes auto-extracted from the CHANGELOG section
#   verifies the Release exists with exactly those two assets before declaring success
#
#   powershell -File tools\release.ps1            # the real thing
#   powershell -File tools\release.ps1 -DryRun    # all checks + build, no tag/push/release
#
# Windows PowerShell 5.1 compatible.
param([switch]$DryRun)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path $PSScriptRoot -Parent
$repoSlug = "Maurdekye/mcplink"

# --- resolve gh (PATH, then the winget shim, then the winget package dir) ---
$gh = $null
try { $gh = (Get-Command gh -ErrorAction Stop).Source } catch {}
if (-not $gh) {
    $shim = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\gh.exe"
    if (Test-Path $shim) { $gh = $shim }
}
if (-not $gh) {
    $pkg = Get-ChildItem (Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Packages") -Recurse -Filter gh.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($pkg) { $gh = $pkg.FullName }
}
if (-not $gh) { throw "GitHub CLI (gh) not found. Install it (winget install GitHub.cli) and run 'gh auth login' as the repo owner." }

# --- preconditions, cheapest first; every failure names its remedy ---
$verMatch = Select-String -Path "$root\Source\McpLinkMod.cs" -Pattern 'VERSION\s*=\s*"([^"]+)"'
$version = $verMatch.Matches[0].Groups[1].Value
Write-Host "Releasing McpLink $version from $root"

$changelog = Get-Content "$root\CHANGELOG.md" -Raw
if ($changelog -notmatch [regex]::Escape("## $version")) {
    throw "CHANGELOG.md has no '## $version' entry. A release without its changelog section is not a release -- write it first."
}

$head = (git -C $root rev-parse HEAD).Trim()
$mainSha = (git -C $root rev-parse main).Trim()
if ($head -ne $mainSha) { throw "HEAD ($($head.Substring(0,12))) is not main ($($mainSha.Substring(0,12))). Releases cut from main only -- merge first." }
$dirty = git -C $root status --porcelain --untracked-files=no
if ($dirty) { throw "Working tree is dirty. Commit or stash before releasing:`n$dirty" }

# via cmd: gh writes auth status to stderr even on SUCCESS, and under 5.1 + EAP Stop a
# PowerShell-side stderr redirect on a native exe throws despite exit code 0
cmd /c "`"$gh`" auth status >nul 2>&1"
if ($LASTEXITCODE -ne 0) { throw "gh is not authenticated. Run: gh auth login (as the repo owner)." }

$existingTag = git -C $root tag -l ("v" + $version)
if ($existingTag) { throw "Tag v$version already exists locally -- this version looks already released. Bump VERSION in McpLinkMod.cs first." }
try {
    $latest = & $gh release view --repo $repoSlug --json tagName --jq .tagName 2>$null
    if ($LASTEXITCODE -eq 0 -and ($latest -replace '^v', '') -eq $version) {
        throw "GitHub already has a release for $version. Bump VERSION in McpLinkMod.cs first."
    }
    if ($LASTEXITCODE -eq 0) { Write-Host "Latest published release: $latest -> publishing v$version" }
} catch { if ($_.Exception.Message -match 'already has a release') { throw } }

# --- build + gate (package.ps1 runs the offline smoke suite; deploys pinned off) ---
$env:CopyToMods = "false"
powershell -NoProfile -File "$root\package.ps1"
if ($LASTEXITCODE -ne 0) { throw "package.ps1 failed -- nothing was tagged or published." }
$zip = "$root\release\McpLink-$version.zip"
$dll = "$root\bin\Release\McpLink.dll"
if (-not (Test-Path $zip)) { throw "Expected artifact missing: $zip" }
if (-not (Test-Path $dll)) { throw "Expected artifact missing: $dll" }

# --- release notes: this version's CHANGELOG section + the self-identify markers ---
$stamp = (git -C $root rev-parse --short=12 HEAD).Trim()
$section = [regex]::Match($changelog, "(?s)## " + [regex]::Escape($version) + ".*?(?=\r?\n## |\z)").Value.Trim()
$notes = @"
$section

---
**Install**: see [INSTALL.md](https://github.com/$repoSlug/blob/main/INSTALL.md). ``McpLink.dll`` is the bare mod (drop into ``rml_mods``); the zip is the full bundle (eval companion, Claude Code proxy, docs).

**Which build am I running?** MCP ``initialize`` -> ``serverInfo.version`` = ``$version``; the ``session_info`` tool -> ``build.informationalVersion`` = ``g$stamp``. Trust those, never file timestamps -- and restart your MCP client after updating so cached tool schemas refresh.
"@
$notesFile = Join-Path $env:TEMP "mcplink-relnotes-$version.md"
Set-Content -Path $notesFile -Value $notes -Encoding utf8

if ($DryRun) {
    Write-Host ""
    Write-Host "DRY RUN complete: all checks passed, artifacts built ($zip)." -ForegroundColor Green
    Write-Host "Skipped: tag v$version, push, GitHub Release. Notes preview: $notesFile"
    exit 0
}

# --- tag, push (gh's credential helper per-invocation; the machine's default helper untouched) ---
$ghSh = "'" + ($gh -replace '\\', '/') + "'"
git -C $root tag ("v" + $version)
git -C $root -c credential.helper= -c "credential.helper=!$ghSh auth git-credential" push origin main
if ($LASTEXITCODE -ne 0) { git -C $root tag -d ("v" + $version); throw "Push of main failed -- tag rolled back, nothing published." }
git -C $root -c credential.helper= -c "credential.helper=!$ghSh auth git-credential" push origin ("v" + $version)
if ($LASTEXITCODE -ne 0) { throw "Tag push failed -- main is pushed but v$version is not; re-run after fixing." }

# --- the Release itself, then VERIFY it (a create that half-worked must not read as done) ---
& $gh release create ("v" + $version) --repo $repoSlug --title "McpLink $version" --notes-file $notesFile `
    $zip "$dll#McpLink.dll (bare mod DLL)" "$root\tools\mcp.py#mcp.py (agents' no-registration helper)"
if ($LASTEXITCODE -ne 0) { throw "gh release create failed -- tag v$version is pushed; create the release manually or re-run." }

$assets = & $gh release view ("v" + $version) --repo $repoSlug --json assets --jq '[.assets[].name] | join(", ")'
if ($assets -notmatch "McpLink-$version.zip" -or $assets -notmatch "McpLink.dll" -or $assets -notmatch "mcp.py") {
    throw "VERIFY FAILED: release exists but assets are [$assets] -- expected the zip, the bare DLL, and mcp.py. Fix on the Releases page."
}
Write-Host ""
Write-Host "Released McpLink $version -> https://github.com/$repoSlug/releases/tag/v$version" -ForegroundColor Green
Write-Host "Assets verified: $assets (build stamp g$stamp)"
