param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if(!$OutputDirectory){$OutputDirectory=Join-Path $repo ('work\reset-tests-'+[Guid]::NewGuid().ToString('N'))}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources=@(Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Filter '*.cs' -File | ForEach-Object {$_.FullName})
$exe=Join-Path $OutputDirectory 'ResetTests.exe'
& $compiler /nologo /target:exe /main:CodexUsageTray.ResetAnnouncementTests ('/out:'+$exe) /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll @sources (Join-Path $PSScriptRoot 'TestResetAnnouncements.cs')
if($LASTEXITCODE -ne 0){throw 'Reset regression test compilation failed'}
& $exe (Join-Path $OutputDirectory ('data-'+[Guid]::NewGuid().ToString('N')))
if($LASTEXITCODE -ne 0){throw 'Reset regression tests failed'}
