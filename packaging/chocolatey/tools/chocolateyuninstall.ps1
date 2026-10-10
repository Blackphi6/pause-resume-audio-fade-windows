$ErrorActionPreference = 'Continue'

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$exe      = Join-Path $toolsDir 'PauseResumeAudioFade.exe'

# If it is running, ask it to exit cleanly (this restores the default playback device and mute state).
if (Test-Path $exe) {
    try { & $exe --quit | Out-Null; Start-Sleep -Seconds 3 } catch { }
}

# Start menu shortcut
$shortcut = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'Pause Resume Audio Fade.lnk'
Remove-Item $shortcut -Force -ErrorAction SilentlyContinue

# Remove the auto-start entry if the user enabled "start with Windows" (current user).
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'PauseResumeAudioFade' -ErrorAction SilentlyContinue
