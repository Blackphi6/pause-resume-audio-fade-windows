# Chocolatey package

`choco install pause-resume-audio-fade` 用のパッケージ定義です(公開後)。

## ビルドとローカル試験(管理者のPowerShell)

```powershell
cd packaging\chocolatey
choco pack
choco install pause-resume-audio-fade -s . -y     # ローカルのnupkgから導入(GitHubからexeをダウンロードしてSHA-256を検証)
choco uninstall pause-resume-audio-fade -y
```

## 新しい版を出すとき

1. GitHub Release に `PauseResumeAudioFade.exe` を添付する。
2. `pause-resume-audio-fade.nuspec` の `<version>` / `<releaseNotes>` と、`tools\chocolateyinstall.ps1` の `url64bit` / `checksum64` を更新する
   (SHA-256: `(Get-FileHash PauseResumeAudioFade.exe -Algorithm SHA256).Hash.ToLower()`)。
3. `choco pack` → `choco push pause-resume-audio-fade.<version>.nupkg --source https://push.chocolatey.org/ --api-key <キー>`。

## 公開(初回)

community.chocolatey.org のアカウントを作り、APIキーで `choco push` します。公開パッケージは、自動検証(ウイルススキャン等)と、
ボランティアのモデレーターの審査を通ってから公開されます(数日かかることがあります)。
