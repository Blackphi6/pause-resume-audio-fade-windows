# winget manifests

`winget install Blackphi6.PauseResumeAudioFade` 用のマニフェスト(スキーマ 1.12.0、InstallerType: portable)。
公式リポジトリ <https://github.com/microsoft/winget-pkgs> へは、`manifests/b/Blackphi6/PauseResumeAudioFade/<version>/` に置くプルリクエストで申請する。

- 初回申請: <https://github.com/microsoft/winget-pkgs/pull/450199>(v1.0.3)
- 申請前に: `winget validate --manifest <dir>`(CRLF・末尾改行・UTF-8。`.editorconfig` に従う)

## 新しい版を出すとき
1. GitHub Release に `PauseResumeAudioFade.exe` を添付する。
2. 新しいフォルダ `<version>/` に4ファイルをコピーし、`PackageVersion` / `InstallerUrl` / `InstallerSha256`(大文字) / `ReleaseDate` / `ReleaseNotes*` を更新する。
3. winget-pkgs のフォーク(Blackphi6/winget-pkgs)にブランチを作って、PRを出す
   (`Update: Blackphi6.PauseResumeAudioFade to <version>`)。または `wingetcreate update Blackphi6.PauseResumeAudioFade --version <version> --urls <url> --submit`。
   ※ Windowsでは `git config core.longpaths true` が必要(パスが長い場合)。
