# LogScope

大容量ログの検索・絞り込み・注記・調査保存を行うWindows用WPFアプリです。
ログ本文は元ファイルを参照し、調査情報をSQLiteに保存します。

## 開発・ビルド

Windowsと.NET 9 SDKが必要です。

```powershell
dotnet build LogScope.sln -c Release
dotnet run --project src/LogScope.Wpf/LogScope.Wpf.csproj
```

## 配布用exeの作成

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Build-Release.ps1 -Version 0.1.0
```

`artifacts/releases/0.1.0/LogScope-Windows-0.1.0-Setup.exe` を配布します。
.NETランタイムを同梱するため、利用者PCへの.NETの事前導入は不要です。
現在のビルドは未署名です。Windowsが発行元の確認を表示する場合があります。

## 自動更新

配布先は `src/LogScope.Wpf/distribution.json` で設定します。
インストール済みアプリは起動約3秒後と6時間ごとにGitHubの公開済み安定版を確認し、更新を自動取得します。
「再起動して更新」で未保存の調査を確認してから適用します。
開発中の起動では自動更新は無効です。
アプリ本体のインストール先と、調査・お気に入りの保存先は分離しています。

### GitHub Actionsから公開

新しいバージョンのタグをpushすると、更新処理の検証・ビルド・Release公開が自動実行されます。
同じバージョンを再利用しないでください。

```powershell
git tag v0.1.0
git push origin v0.1.0
```

手動実行はActionsの「Windows release」から行えます。
手動実行の初期設定ではDraftを作成し、`publish` を有効にすると即時公開します。
`GITHUB_TOKEN` はActionsのアップロード処理だけで使用し、アプリには含めません。

### 開発PCからDraftを作成

`VPK_TOKEN` 環境変数にGitHubのリリース作成権限を持つトークンを設定して実行します。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish-Release.ps1 -Version 0.1.0
```

GitHubでDraftの内容を確認して公開すると更新配信が始まります。
`-Publish` を付けると公開まで実行します。
トークンをソースに記載しないでください。
インストーラーと更新フィード・パッケージはスクリプトでまとめてアップロードします。

## 更新処理の検証

```powershell
dotnet run --project tools/UpdateVerification -c Release
```

このリポジトリには、アプリのソース、配布設定、公開スクリプト、公開時に必要な検証コードを収録しています。
ビルド成果物・利用者のログ・調査データ・ローカル設定・検証結果は含めません。
