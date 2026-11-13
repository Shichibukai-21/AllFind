# Publishes a new AllFind version: every installed copy (yours and your friends') updates itself within a few hours,
# or right away from the tray menu > Check for updates.
# Easiest: double-click "Release AllFind.cmd" in the AllFind folder. Or, from anywhere:
#   powershell -ExecutionPolicy Bypass -File "%USERPROFILE%\Documents\AllFind\src\release.ps1" 1.0.1 "What changed"
param([string]$Version = "", [string]$Notes = "", [switch]$DryRun)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
Set-Location $root     # works no matter which folder it was started from

# what was released last, and is there anything new since then?
$last = (git describe --tags --abbrev=0 2>$null)
if ($LASTEXITCODE -ne 0) { $last = "" }
# only changes to the app itself count (not the README, notes or this script)
$changed = @()
if ($last) { $changed += git diff --name-only $last } else { $changed += git ls-files }
$changed += git ls-files --others --exclude-standard
$changed = $changed | Where-Object { $_ -like 'src/*' -and $_ -notin @('src/release.ps1', 'src/Version.cs') }
if (-not $changed) {
    Write-Host "Nothing has changed since $last, so there is nothing to release." -ForegroundColor Yellow
    exit 1
}

if ($Version -eq "") {
    Write-Host "Last release: $(if ($last) { $last } else { 'none' })"
    $Version = Read-Host "New version number (for example 1.0.1)"
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3" }
if ($last -and ([version]$Version -le [version]($last.TrimStart('v')))) { throw "Version must be higher than $last" }
if ($Notes -eq "") { $Notes = Read-Host "What changed? (shown on the download page)" }
if ($Notes -eq "" -or $Notes -eq "What changed") { throw "Please describe what changed" }
if ($DryRun) { Write-Host "Dry run: would release v$Version ($Notes)"; exit 0 }

$v4 = "$Version.0"
$text = "// set by release.ps1`n[assembly: System.Reflection.AssemblyVersion(`"$v4`")]`n[assembly: System.Reflection.AssemblyFileVersion(`"$v4`")]`n[assembly: System.Reflection.AssemblyTitle(`"AllFind`")]`n[assembly: System.Reflection.AssemblyProduct(`"AllFind`")]`n"
[IO.File]::WriteAllText("$here\Version.cs", $text, (New-Object Text.UTF8Encoding $false))

& powershell -NoProfile -ExecutionPolicy Bypass -File "$here\build.ps1"
if ($LASTEXITCODE -ne 0) { throw "build failed" }

git add -A
git commit -m "Release v$Version" | Out-Null
git tag "v$Version"
git push origin HEAD --tags
if ($LASTEXITCODE -ne 0) { throw "push failed" }
gh release create "v$Version" "$root\AllFind\AllFind.exe" --title "AllFind $Version" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "release failed" }
Write-Host "Released v$Version - installed copies will update themselves." -ForegroundColor Green
