# Publishes a new AllFind version: every installed copy (yours and your friends') updates itself within a few hours,
# or right away from the tray menu > Check for updates.
#   powershell -ExecutionPolicy Bypass -File src\release.ps1 1.0.1 "What changed"
param([Parameter(Mandatory = $true)][string]$Version, [string]$Notes = "")
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3" }
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
Set-Location $root

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
if ($Notes -eq "") { $Notes = "AllFind $Version" }
gh release create "v$Version" "$root\AllFind\AllFind.exe" --title "AllFind $Version" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw "release failed" }
Write-Host "Released v$Version"
