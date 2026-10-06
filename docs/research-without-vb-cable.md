# VB-CABLE なしで実現できるか — 調査と実測(2026-10-06)

## 結論

**「ドライバを入れず、設定も触らず、他アプリの音を一時停止の前に遅延させてフェードする」方法は、Windowsには無い。**
必ずどれかの「Windowsの内側に入る仕掛け」が要る。現実的な選択肢は下の4つで、
**現時点のおすすめは「VB-CABLE(既定)+ 既存の別デバイスをシンクに使う簡易モード」**。

なぜ仮想デバイスが要るのか: フェードアウトは「止まる前の音」を遅延させて加工する必要があり、そのためには
「元の音を鳴らさず、加工後の音だけを鳴らす」ことが必要。Mac(Core Audio)やAndroid(AudioEffect)にはこれを許す
公式の入り口があるが、Windowsのユーザーモードには、他アプリの音の出口に割り込む入り口が無い。

## 実測した2つの抜け道(どちらも不成立)

テスト環境: Windows 11 / Senary Audio(Fortemedia FMAPO64.dll のメーカー製エフェクト入り)。
ツール: `poc/ProcLoopSessionMute`。

### 抜け道1: プロセス別ループバックで取り込み → 元アプリのセッションをミュート → 再生
プロセス別ループバック(`AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`)が「セッション音量の手前」を取るなら成立する。

| 条件 | 取り込みのピーク |
|---|---|
| セッション音量 1 | 0.43 |
| セッション音量 0 | **0.0000** |
| セッションミュート | **0.0000** |
| 戻す | 0.47 |

**不成立。** 取り込みは音量・ミュートの**後**。(同じ発想の他プロジェクトの issue でも「未検証の理論」のまま。)

### 抜け道2: セッション音量を極小(例 1e-4 = −80dB)にして、取り込み側をデジタルで補正
floatなので精度は足りる。元の音は−80dBまで下がり実質聞こえない。

補正後ピーク/基準ピーク(音量1のときを1とする)を、音量を変えて測った結果:

| セッション音量 | 0.5 | 0.1 | 0.05 | 0.01 | 0.001 | 1e-4 | 1e-5 |
|---|---|---|---|---|---|---|---|
| 補正後/基準(トーン0.2) | 1.67 | 4.9 | 5.4 | 2.3 | 1.0 | 0.79 | 0.76 |
| 補正後/基準(トーン0.4) | 1.2 | 4.0 | 6.9 | 5.8 | 1.6 | 0.94 | 0.92 |

**この機器では不成立。** 値が信号の大きさで変わる(=レベル依存の処理)。WinMM経由でもWASAPI直でも同じ。
原因はメーカー製のエフェクト(Fortemedia APO、`FMAPO64.dll`)と推定。極小音量側だけは一定の静的ゲインに見えるが、
周波数特性が未確認で、機器ごとに違うため、一般解にならない。

## 実現方法の比較

| 方法 | 追加インストール | 署名/管理者 | 品質 | 主なリスク |
|---|---|---|---|---|
| **A. VB-CABLE(現行)** | VB-CABLE(管理者で1回) | 不要(署名済み) | ループバック+遅延で良好 | 外部ソフト依存。個人利用は無料(donationware) |
| **B. 既存の別デバイスをシンクに使う** | なし | 不要 | Aと同じ | 使える「余ったデバイス」がPCにあるときだけ(HDMI/DP出力、内蔵スピーカー等) |
| **C. 自前の仮想オーディオドライバ** | 自前ドライバ | **Microsoftの署名が必須**(Partner Center + EVコード署名証明書)。自分用のテスト署名は `bcdedit /set testsigning on` が要る | Aと同じ | 開発・署名・保守の負担が大きい。ベースは MIT+MS-PL の Virtual-Audio-Driver / SYSVAD |
| **D. システム全体のAPO(Equalizer APO方式)** | 自前のC++ DLLを機器ごとに登録 | 未署名なら `DisableProtectedAudioDG=1`(セキュリティ低下) | 最も素直(ミックス全体、ルーティング不要、先読み遅延も `GetLatency` で申告可) | 1機器につきSFX/MFXは各1つ → **この機器のメーカーAPOと衝突**。公式の導入経路は「各ドライバが自分のAPOを登録」で、第三者の全体登録は非公式。Windows/ドライバ更新で外れる。C#では書けない(C++) |

## ライセンス(VB-CABLE)
- 個人利用は無料(donationware、使い方に応じた寄付は任意)。
- 他製品への同梱・再配布は条件付きで可(VB-CABLEの出所の明記、donationwareであることの明示など)。企業の組み込みは別途契約を推奨。
- インストールは管理者で実行、再起動が必要な場合あり。Windows XP〜11 / Arm64 対応(Pack 45)。

## 方針
1. **A(VB-CABLE)を正式ルートとして維持**(規約上も個人利用は問題なし)。
2. **B**: 既に `VirtualDeviceName` 設定で任意のデバイスを指定できる(開発用 `--sink`)。「余ったデバイスを自動検出して提案する」UIを足す価値あり。
   ただしこのPCの「スピーカー」のような端子連動デバイスは、スピーカーで聴きたいときに使えない。
3. **C/D**は、一般配布するときに初めて検討(署名・保守コストが大きい)。

## 出典
- 他アプリのミュート+プロセス別ループバック再生は未検証の理論段階: <https://github.com/Raaif-Yousuf/Wingman/issues/365>
- プロセス別ループバックの仕様: <https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/>
- APOの実装・登録・署名: <https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/implementing-audio-processing-objects>
- Equalizer APO と `DisableProtectedAudioDG`: <https://sourceforge.net/p/equalizerapo/discussion/general/thread/d3e27628/>
- 仮想オーディオドライバ(MIT / MS-PL、テスト署名が必要): <https://github.com/VirtualDrivers/Virtual-Audio-Driver>
- VB-CABLE の条件: <https://vb-audio.com/Cable/> , <https://vb-audio.com/Services/licensing.htm>
