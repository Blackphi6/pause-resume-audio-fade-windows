$ErrorActionPreference = 'Stop'

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$exe      = Join-Path $toolsDir 'PauseResumeAudioFade.exe'

# If an older version is running, ask it to exit cleanly so the file can be replaced (no-op otherwise).
if (Test-Path $exe) {
    try { & $exe --quit | Out-Null; Start-Sleep -Seconds 2 } catch { }
}

$packageArgs = @{
    packageName    = $env:ChocolateyPackageName
    fileFullPath   = $exe
    url64bit       = 'https://github.com/Blackphi6/pause-resume-audio-fade-windows/releases/download/v1.0.2/PauseResumeAudioFade.exe'
    checksum64     = '1c2488e9d3636231ca2d0d3d295b0a1283e5614acbde50b2900097898046df50'
    checksumType64 = 'sha256'
}
Get-ChocolateyWebFile @packageArgs

# GUI app: tell the shim not to wait for the process (so `choco install` and the shim command do not block).
New-Item -ItemType File -Path "$exe.gui" -Force | Out-Null

# Start menu shortcut
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Pause Resume Audio Fade.lnk'
Install-ChocolateyShortcut -ShortcutFilePath $shortcut -TargetPath $exe -WorkingDirectory $toolsDir -Description 'Fade audio on pause, resume and seek'

Write-Host ''
Write-Host 'Installed. Start "Pause Resume Audio Fade" from the Start menu (or run: PauseResumeAudioFade).'
Write-Host 'A first-run guide explains the one thing it may need: an unused output device or VB-CABLE.'
