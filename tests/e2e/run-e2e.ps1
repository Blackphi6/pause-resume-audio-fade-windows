# E2E検証: テスト音源(ブラウザの一時停止=厳密な無音を再現)→ 実エンジン → エンジンの出力を録音して波形解析。
#
# 前提: poc\* と src\PauseResumeAudioFade をビルド済み(Release/Debug)。
# このPCのようにスピーカーとヘッドホンが両方有効な環境では、仮想ケーブルの代わりに
#   -Sink スピーカー -Real ヘッドホン -MuteSink
# で全経路を測れる。VB-CABLE導入後は  -Sink "CABLE Input" -Real <実デバイス名>  にする(MuteSinkは不要)。
param(
    [string]$Script = "tone:2,gap:0.5,tone:2,gap:0.15,tone:2,gap:0.02,tone:2",
    [int]$Seconds = 15,
    [string]$Sink = "スピーカー",
    [string]$Real = "ヘッドホン",
    [switch]$MuteSink,
    [switch]$Auto,        # 引数なしの自動選択で起動する(ケーブルが無ければ余った出力を借りる経路の検証)
    [switch]$KeepEngine
)

$root = Resolve-Path "$PSScriptRoot\..\.."
$tool = "$root\poc\AudioTool\bin\Release\net9.0-windows\AudioTool.exe"
$app  = "$root\src\PauseResumeAudioFade\bin\Release\net9.0-windows\PauseResumeAudioFade.exe"
$tone = "$root\poc\ToneTestPlayer\bin\Debug\net9.0-windows\ToneTestPlayer.exe"
$rec  = "$root\poc\E2eRecorder\bin\Release\net9.0-windows\E2eRecorder.exe"

function Get-MainPid {
    (Get-CimInstance Win32_Process -Filter "Name='PauseResumeAudioFade.exe'" |
        Where-Object { $_.CommandLine -notmatch '--watchdog' } | Select-Object -First 1).ProcessId
}

Get-Process ToneTestPlayer, E2eRecorder -ErrorAction SilentlyContinue | Stop-Process -Force
$started = $false
if (-not (Get-MainPid)) {
    Remove-Item "$env:APPDATA\PauseResumeAudioFade\log.txt" -ErrorAction SilentlyContinue
    $argsList = @("--sink", $Sink, "--real", $Real, "--no-tray")
    if ($MuteSink) { $argsList += "--mute-sink" }
    if ($Auto) { $argsList = @("--no-tray") }
    Start-Process -FilePath $app -ArgumentList $argsList | Out-Null
    $started = $true
    Start-Sleep -Seconds 3
}
$main = Get-MainPid
if (-not $main) { Write-Error "engine did not start"; exit 1 }
Write-Output "engine pid = $main"

# ToneTestPlayerのセッション音量(Windowsがexe単位で記憶する)を確実に1.0へ
$w = Start-Process -FilePath $tone -ArgumentList @("script", "tone:3") -PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\e2e_warm.txt"
Start-Sleep -Seconds 1.5
& $tool fixvol | Out-Null
Wait-Process -Id $w.Id -Timeout 15 -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

$recJob = Start-Process -FilePath $rec -ArgumentList @("$main", "$Seconds") -PassThru -RedirectStandardOutput "$env:TEMP\e2e_rec.txt" -NoNewWindow
Start-Sleep -Seconds 2
Start-Process -FilePath $tone -ArgumentList @("script", $Script) -PassThru -RedirectStandardOutput "$env:TEMP\e2e_tone.txt" -NoNewWindow | Out-Null
$recJob.WaitForExit(($Seconds + 30) * 1000) | Out-Null

Write-Output "======== engine log (gaps) ========"
Get-Content "$env:APPDATA\PauseResumeAudioFade\log.txt" -Encoding UTF8 | Select-String "gap detected|status:|ERROR|WARN" | Select-Object -Last 12
Write-Output "======== recorder report ========"
Get-Content "$env:TEMP\e2e_rec.txt"

if ($started -and -not $KeepEngine) {
    & $app --quit
    Start-Sleep -Seconds 2
    Write-Output "engine stopped; default device:"
    & $tool status | Select-Object -First 2
}
