param([switch]$NoLaunch,[string]$InstallDirectory,[string]$DataDirectory,[switch]$CheckOnly)
$ErrorActionPreference='Stop'
$env:PSModulePath=(Join-Path $PSHOME 'Modules')+';'+(Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\Modules')+';'+(Join-Path $env:ProgramFiles 'WindowsPowerShell\Modules')
function Read-Sha256([string]$Path){
    $algorithm=[Security.Cryptography.SHA256]::Create();$stream=[IO.File]::OpenRead($Path)
    try{return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose();$algorithm.Dispose()}
}
$appSource=Join-Path $PSScriptRoot 'app'
$targetRoot=if($InstallDirectory){[IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')}else{Join-Path $env:LOCALAPPDATA 'Programs\UsageObserver'}
$dataRoot=if($DataDirectory){[IO.Path]::GetFullPath($DataDirectory)}elseif($env:USAGE_OBSERVER_DATA){[IO.Path]::GetFullPath($env:USAGE_OBSERVER_DATA)}else{Join-Path $env:LOCALAPPDATA 'UsageObserver'}
if($targetRoot -eq [IO.Path]::GetPathRoot($targetRoot).TrimEnd('\')){throw 'Invalid installation root'}
$canonicalExe=Join-Path $targetRoot 'UsageObserver.exe'
$manifest=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if($manifest.product -ne 'UsageObserver'){throw 'Invalid product manifest'}
$files=@();$seen=@{}
foreach($file in $manifest.files){
    $sourceFile=[IO.Path]::GetFullPath((Join-Path $appSource $file.path));$destFile=[IO.Path]::GetFullPath((Join-Path $targetRoot $file.path))
    if(-not $sourceFile.StartsWith($appSource.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or -not $destFile.StartsWith($targetRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or $file.path.Contains(':') -or $seen.ContainsKey($destFile)){throw 'Invalid package file path'}
    if((Read-Sha256 $sourceFile) -ne $file.sha256){throw ('Package verification failed: '+$file.path)}
    $seen[$destFile]=$true;$files+=@{Source=$sourceFile;Destination=$destFile;Relative=$file.path;Hash=$file.sha256}
}
if(-not $seen.ContainsKey($canonicalExe)){throw 'Missing application executable'}
if($CheckOnly){Write-Output ('Verified: '+$manifest.version+' / '+$files.Count+' files');exit 0}
# Verify all package entries, but replace only changed files. Unchanged runtime
# binaries may be in use by the recorder or a companion sync process.
$updates=@($files | Where-Object {!(Test-Path -LiteralPath $_.Destination) -or (Read-Sha256 $_.Destination) -ne $_.Hash})
$recorderChanges=@($updates | Where-Object {$_.Relative -match '^(runtime[/\\]|node_modules[/\\]|monitor\.py$|collector\.py$|portable\.py$|quota_core\.py$|deepseek|lan-agent|start\.ps1$|stop\.ps1$)'})
$stopRecorder=$recorderChanges.Count -gt 0

$mutexKey=[Text.Encoding]::UTF8.GetBytes($targetRoot.ToLowerInvariant());$sha=[Security.Cryptography.SHA256]::Create()
$mutexName='Local\UsageObserver_Install_'+[BitConverter]::ToString($sha.ComputeHash($mutexKey)).Replace('-','');$sha.Dispose()
$mutex=New-Object Threading.Mutex($false,$mutexName)
if(-not $mutex.WaitOne(0)){throw 'Another update is already running'}
$backupRoot=Join-Path ([IO.Path]::GetDirectoryName($targetRoot)) ('.UsageObserver-backup-'+[Guid]::NewGuid().ToString('N'))
$backed=@();$changed=@();$stopped=$false;$oldData=$env:USAGE_OBSERVER_DATA;$committed=$false;$uiWasRunning=$false
try{
    foreach($file in $updates){if(Test-Path -LiteralPath $file.Destination){
        $backupFile=Join-Path $backupRoot $file.Relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($backupFile)) -Force | Out-Null
        Copy-Item -LiteralPath $file.Destination -Destination $backupFile -Force
        $backed+=@{Source=$backupFile;Destination=$file.Destination}
    }}
    if(Test-Path -LiteralPath $canonicalExe){
        $knownProcesses=@(Get-CimInstance Win32_Process | Where-Object {($_.ExecutablePath -eq $canonicalExe) -or ($_.CommandLine -and $_.CommandLine.Contains((Join-Path $targetRoot 'monitor.py')))})
        $uiWasRunning=@($knownProcesses | Where-Object {$_.ExecutablePath -eq $canonicalExe}).Count -gt 0
        $env:USAGE_OBSERVER_DATA=$dataRoot
        New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
        Start-Process -FilePath $canonicalExe -ArgumentList '--quit-ui' -WindowStyle Hidden -Wait
        if($stopRecorder){Set-Content -LiteralPath (Join-Path $dataRoot 'stop.request') -Value 'Installer update requested' -Encoding UTF8}
        foreach($process in $knownProcesses){
            if($process.ExecutablePath -ne $canonicalExe -and !$stopRecorder){continue}
            $instance=Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
            if($instance -and -not $instance.WaitForExit(60000)){throw 'Existing recorder is still finishing a read; no files were replaced. Retry shortly.'}
        }
        $stopped=$true
    }
    New-Item -ItemType Directory -Path $targetRoot -Force | Out-Null
    foreach($file in $updates){
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($file.Destination)) -Force | Out-Null
        $changed+=$file.Destination
        Copy-Item -LiteralPath $file.Source -Destination $file.Destination -Force
        if((Read-Sha256 $file.Destination) -ne $file.Hash){throw 'Installed file verification failed'}
    }
    if(-not $InstallDirectory){
        $shortcutPath=Join-Path ([Environment]::GetFolderPath('Desktop')) 'UsageObserver.lnk'
        $shell=New-Object -ComObject WScript.Shell;$link=$shell.CreateShortcut($shortcutPath)
        $link.TargetPath=$canonicalExe;$link.WorkingDirectory=$targetRoot;$link.IconLocation=$canonicalExe;$link.Save()
    }
    if(-not $NoLaunch){$env:USAGE_OBSERVER_DATA=$dataRoot;Start-Process -FilePath $canonicalExe -WindowStyle Hidden}
    $committed=$true
    Write-Output ('Installed: '+$canonicalExe+' / '+$manifest.version)
}
catch{
    if($changed.Count -gt 0){
        foreach($destination in $changed){if(-not ($backed | Where-Object {$_.Destination -eq $destination})){Remove-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue}}
        foreach($file in $backed){Copy-Item -LiteralPath $file.Source -Destination $file.Destination -Force}
        $committed=$true
    }
    if(($stopped -or $uiWasRunning) -and -not $NoLaunch -and (Test-Path -LiteralPath $canonicalExe)){Start-Process -FilePath $canonicalExe -WindowStyle Hidden}
    throw
}
finally{
    $env:USAGE_OBSERVER_DATA=$oldData
    if($committed -and (Test-Path -LiteralPath $backupRoot) -and [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($backupRoot)) -eq [IO.Path]::GetDirectoryName($targetRoot)){Remove-Item -LiteralPath $backupRoot -Recurse -Force}
    $mutex.ReleaseMutex();$mutex.Dispose()
}
