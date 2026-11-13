# Builds AllFind.exe with the C# compiler built into Windows (.NET Framework 4.8)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out  = Join-Path (Split-Path -Parent $here) 'AllFind'
New-Item -ItemType Directory -Force $out | Out-Null
$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc = "$fw\csc.exe"
$wpf = @("WPF\PresentationFramework.dll","WPF\PresentationCore.dll","WPF\WindowsBase.dll","System.Xaml.dll") | ForEach-Object { "/r:$fw\$_" }

# 1) render the logo into app.ico
& $csc /nologo /target:exe "/out:$env:TEMP\icongen.exe" $wpf "$here\Logo.cs" "$here\IconGen.cs"
if ($LASTEXITCODE -ne 0) { throw "icongen compile failed" }
& "$env:TEMP\icongen.exe" "$here\app.ico"

# 2) compile the app
$refs = $wpf + (@("System.Windows.Forms.dll","System.Drawing.dll","Microsoft.CSharp.dll","System.Core.dll","System.Data.dll","System.Xml.dll","WPF\UIAutomationClient.dll","WPF\UIAutomationTypes.dll") | ForEach-Object { "/r:$fw\$_" })
& $csc /nologo /unsafe /codepage:65001 /optimize+ /target:winexe /platform:x64 "/out:$out\AllFind.exe" "/win32icon:$here\app.ico" "/win32manifest:$here\app.manifest" $refs "$here\AllFind.cs" "$here\Logo.cs" "$here\Taskbar.cs"
if ($LASTEXITCODE -ne 0) { throw "compile failed" }
Write-Host "Built $out\AllFind.exe"
