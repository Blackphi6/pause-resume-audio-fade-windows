# Chocolatey package

`choco install pause-resume-audio-fade` 用のパッケージ定義です(ダウンロード型: GitHub Release の exe を SHA-256 検証つきで取得)。

## ビルドとローカル試験(管理者のPowerShell)

```powershell
cd packaging\chocolatey
choco pack
choco install pause-resume-audio-fade -s . -y     # ローカルのnupkgから導入
choco uninstall pause-resume-audio-fade -y
```

## コミュニティリポジトリへの公開(初回)

1. <https://community.chocolatey.org/account/Register> でアカウントを作る(メール認証)。
2. アカウントページ(API Key)でキーを確認し、**自分のPCで**登録する(キーは他人に見せない):
   `choco apikey add --source https://push.chocolatey.org/ --key <APIキー>`
3. `choco pack` → `choco push pause-resume-audio-fade.<version>.nupkg --source https://push.chocolatey.org/`
4. 自動チェックが走る(Validator=規則の検査 / Verifier=別環境での導入・削除テスト / Scanner=ウイルススキャン)。通ると人間のモデレーターの審査に回る。
   指摘(Requirements)が来たら直して、**同じ版を再提出**する。`Waiting` のまま20日放置すると催促、さらに15日で自動却下。

### 検証ルールへの対応状況(CPMR)
- アイコンは jsDelivr 経由(raw.githubusercontent は不可: CPMR0076)
- `packageSourceUrl` あり(CPMR0040)、`projectSourceUrl` は `projectUrl` と別のURL(CPMR0041)
- 説明文に Markdown の見出しを使わない(CPMR0030)
- ダウンロードURLは HTTPS で、`checksum64` / `checksumType64` を指定(CPMR0027)。ダウンロード元はプロジェクトのGitHub Release
- スクリプトは UTF-8 BOM

## 新しい版を出すとき

1. GitHub Release に `PauseResumeAudioFade.exe` を添付する。
2. `pause-resume-audio-fade.nuspec` の `<version>` / `<releaseNotes>` / `<iconUrl>`(タグ)と、`tools\chocolateyinstall.ps1` の `url64bit` / `checksum64` を更新する
   (SHA-256: `(Get-FileHash PauseResumeAudioFade.exe -Algorithm SHA256).Hash.ToLower()`)。
3. `choco pack` → `choco push`。
