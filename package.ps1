# Assembles the distributable McpLink release zip: build -> smoke suite -> zip.
# Usage: powershell -File package.ps1  (from anywhere; -SkipBuild / -SkipTests to shortcut)
param([switch]$SkipBuild, [switch]$SkipTests)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# version from the single source of truth in McpLinkMod.cs
$verMatch = Select-String -Path "$root\Source\McpLinkMod.cs" -Pattern 'VERSION\s*=\s*"([^"]+)"'
$version = $verMatch.Matches[0].Groups[1].Value
Write-Host "Packaging McpLink $version"

if (-not $SkipBuild) {
    dotnet build "$root\McpLink.csproj" -c Release -v minimal
    if ($LASTEXITCODE -ne 0) { throw "McpLink build failed" }
    dotnet build "$root\eval\McpLinkEval.csproj" -c Release -v minimal
    if ($LASTEXITCODE -ne 0) { throw "McpLinkEval build failed" }
}

if (-not $SkipTests) {
    Write-Host "Running offline smoke suite (release gate)..."
    dotnet run --project "$root\test\McpLinkSmoke.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "Smoke suite FAILED - not packaging a broken build" }
}

$stage = "$root\release\staging"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -Confirm:$false }
New-Item -ItemType Directory -Force "$stage\rml_mods\McpLink_libs" | Out-Null
New-Item -ItemType Directory -Force "$stage\proxy" | Out-Null

Copy-Item "$root\bin\Release\McpLink.dll" "$stage\rml_mods\"
Copy-Item "$root\eval\bin\Release\*.dll" "$stage\rml_mods\McpLink_libs\"
Copy-Item "$root\proxy\mcplink_proxy.py" "$stage\proxy\"
Copy-Item "$root\INSTALL.md", "$root\CLAUDE-MCPLINK.md", "$root\CHANGELOG.md", "$root\LICENSE" $stage

$zip = "$root\release\McpLink-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force -Confirm:$false }
# NOT Compress-Archive / ZipFile.CreateFromDirectory: under Windows PowerShell 5.1
# both write '\' in nested entry names (zip spec requires '/'; powershell.exe's old
# target framework flips the ZipFile.UseBackslash compat switch), so extractors
# unpack everything flat. Build entries by hand with '/' separators.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    Get-ChildItem $stage -Recurse -File | ForEach-Object {
        $entryName = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $_.FullName, $entryName) | Out-Null
    }
} finally {
    $archive.Dispose()
}
Remove-Item $stage -Recurse -Force -Confirm:$false

$size = [math]::Round((Get-Item $zip).Length / 1MB, 2)
Write-Host "Release ready: $zip ($size MB)"
