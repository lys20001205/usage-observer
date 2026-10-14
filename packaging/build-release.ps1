param([string]$OutputDirectory,[string]$NetworkingPython)
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version='1.0.4.12'
$buildRoot=Join-Path $repoRoot ('work\release-'+$version)
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
if(!$OutputDirectory){$OutputDirectory=Join-Path $buildRoot 'artifacts'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
# Unpatched updaters decode JSON with the Windows ANSI code page. Keep release
# text ASCII until all supported clients explicitly decode UTF-8.
$releaseBody=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'release-notes.txt'),[Text.Encoding]::UTF8)
if($releaseBody -match '[^\x00-\x7F]'){throw 'Release notes must remain ASCII for old-updater compatibility'}
@{tag_name=('v'+$version);name=('UsageObserver '+$version+' - Private GitHub device sync');body=$releaseBody;draft=$true;prerelease=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'release-metadata.json') -Encoding ASCII
$baseline=Join-Path $buildRoot 'UsageObserver-Setup-1.0.4.1.exe'
if(!(Test-Path -LiteralPath $baseline)){Invoke-WebRequest -Uri 'https://github.com/lys20001205/usage-observer/releases/download/v1.0.4.1/UsageObserver-Setup.exe' -OutFile $baseline -TimeoutSec 120}
if((Get-FileHash -LiteralPath $baseline -Algorithm SHA256).Hash -ne '6B172C478B81D98E5E273BB603A6F70A67D33A461DAD13C2D9C4DAC1E9AA1BE9'){throw 'Baseline installer SHA-256 mismatch'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$original=[Reflection.Assembly]::LoadFile($baseline)
$payload=Join-Path $buildRoot 'baseline-payload.zip'
$inputStream=$original.GetManifestResourceStream('payload.zip');$outputStream=[IO.File]::Create($payload)
try{$inputStream.CopyTo($outputStream)}finally{$outputStream.Dispose();$inputStream.Dispose()}
$stage=Join-Path $buildRoot ('stage-'+[Guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($payload,$stage)
$manifest=Get-Content -LiteralPath (Join-Path $stage 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$app=Join-Path $stage 'app'
foreach($file in $manifest.files){
 $path=[IO.Path]::GetFullPath((Join-Path $app $file.path))
 if(!$path.StartsWith($app+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid baseline package path'}
 if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256){throw ('Baseline file hash mismatch: '+$file.path)}
}
if(!$NetworkingPython){$NetworkingPython=$env:USAGE_OBSERVER_BUILD_PYTHON}
if(!$NetworkingPython){throw 'Pass -NetworkingPython with a complete Windows x64 CPython 3.12 executable'}
& $NetworkingPython (Join-Path $PSScriptRoot 'prepare-network-runtime.py') --target (Join-Path $app 'runtime\python')
if($LASTEXITCODE -ne 0){throw 'Networking runtime dependency preparation failed'}
& (Join-Path $repoRoot 'src\build-ui.ps1')
foreach($file in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src') -File){Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $app $file.Name) -Force}
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $stage 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'GITHUB-SYNC.md') -Destination (Join-Path $stage 'GITHUB-SYNC.md') -Force
# Keep the published, rollback-capable installation script and bundled runtimes.
# Normalize its shortcut label without touching existing user shortcuts.
$installer=Join-Path $stage 'install.ps1'
$text=Get-Content -LiteralPath $installer -Raw -Encoding UTF8
$text=$text.Replace("(Join-Path `$PSScriptRoot 'manifest.json') -Raw |","(Join-Path `$PSScriptRoot 'manifest.json') -Raw -Encoding UTF8 |")
$text=$text.Replace("`$_.CommandLine.Contains((Join-Path `$targetRoot 'monitor.py'))","(`$_.CommandLine.Contains((Join-Path `$targetRoot 'monitor.py')) -or `$_.CommandLine.Contains((Join-Path `$targetRoot 'github_sync.py')))")
$text=$text.Replace("Set-Content -LiteralPath (Join-Path `$dataRoot 'stop.request')", "Set-Content -LiteralPath (Join-Path `$dataRoot 'github-sync-stop.request') -Value 'Installer update requested' -Encoding UTF8`r`n        Set-Content -LiteralPath (Join-Path `$dataRoot 'stop.request')")
$text=[regex]::Replace($text,"(?m)^\s*\`$shortcutPath=Join-Path .*?$",'        $shortcutPath=Join-Path ([Environment]::GetFolderPath(''Desktop'')) ''UsageObserver.lnk''')
[IO.File]::WriteAllText($installer,$text,(New-Object Text.UTF8Encoding($true)))
@('@echo off','powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0install.ps1"') | Set-Content -LiteralPath (Join-Path $stage 'Install.cmd') -Encoding ASCII
@('@echo off','start "" "%~dp0app\UsageObserver.exe"') | Set-Content -LiteralPath (Join-Path $stage 'Run.cmd') -Encoding ASCII
$entries=@()
foreach($file in Get-ChildItem -LiteralPath $app -File -Recurse){
 $relative=$file.FullName.Substring($app.Length+1).Replace('\','/')
 if($relative -match '(?i)(^|/)(trend-settings|theme-settings|ui-settings|connection-settings|device-id|lan-key|status|monitor.pid|token.*\.jsonl|\.credentials|usage.*\.db)' -or $relative -match '(?i)(^|/)(__pycache__|\.git)(/|$)'){throw ('Unexpected local data: '+$relative)}
 $entries+=@{path=$relative;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
@{product='UsageObserver';version=$version;platform='Windows 10/11 x64';files=$entries} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $stage 'manifest.json') -Encoding UTF8
$zip=Join-Path $OutputDirectory 'UsageObserver-Windows-x64.zip'
if(Test-Path -LiteralPath $zip){Remove-Item -LiteralPath $zip}
[IO.Compression.ZipFile]::CreateFromDirectory($stage,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$setup=Join-Path $OutputDirectory 'UsageObserver-Setup.exe'
& $compiler /nologo /target:winexe /optimize+ /platform:anycpu ('/win32manifest:'+(Join-Path $repoRoot 'src\app.manifest')) ('/out:'+$setup) ('/resource:'+$zip+',payload.zip') /reference:System.Windows.Forms.dll /reference:System.IO.Compression.FileSystem.dll /reference:System.IO.Compression.dll /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'Setup.cs')
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}
[pscustomobject]@{version=$version;stage=$stage;files=$entries.Count;setup=$setup;setup_sha256=(Get-FileHash -LiteralPath $setup).Hash.ToLowerInvariant();zip=$zip;zip_sha256=(Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant()} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $buildRoot 'build-evidence.json') -Encoding UTF8
Get-Content -LiteralPath (Join-Path $buildRoot 'build-evidence.json')
