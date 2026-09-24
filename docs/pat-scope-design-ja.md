# 組織・アカウント権限と fine-grained PAT の対象範囲

[English](pat-scope-design.md)

## 対象と境界

`TargetRequirement` はリポジトリ、組織、アカウントを型付きで表す。`Program` は要求を対象ごとにまとめて `PolicyStore.Evaluate(target, needs)` に渡す。分類器が登録した経路だけを許可し、すべての対象を評価してから `gh` を起動する。作業ツリーの `.gh-harness.json` は、その Git remote に対応するリポジトリの評価時だけ適用する。

GitHub の fine-grained PAT は、1 個の resource owner（ユーザーまたは組織）、リポジトリの選択、権限の組で制限される。アカウント権限は本人が resource owner の場合のみ、組織権限は組織が resource owner の場合のみ設定できる。組織の承認待ち、本人の権限、トークン期限なども API 成否に影響する。[PAT の作成・制限](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens)と[権限別 REST 一覧](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens)を根拠とする。

対応済みの経路は[権限対応表](permission-matrix-ja.md)を参照する。

## 型付き対象と権限

内部の対象を `TargetId(Host, Kind, Name)` とする。`Host` は当面 `github.com` のみ。`Kind` は以下の 3 種類で、名前だけから種別を推測しない。

| Kind | Name | 対応する権限名前空間 | 例 |
| --- | --- | --- | --- |
| `repository` | `OWNER/REPO` | `repository` | `acme/project` に `repository.secrets:write` |
| `organization` | 組織 login | `organization` | `acme` に `organization.secrets:write` |
| `account` | ユーザー login | `account` | `alice` に `account.emails:read` |

権限は `PermissionKey(Namespace, Name)` と `Level` に分ける。同名の `secrets` や `webhooks` を namespace なしで共有しない。`Level` は権限ごとの有効値を公式資料から定義し、将来必要な `admin` を表現できるようにする。既存のリポジトリ権限は従来どおり `none` / `read` / `write` とし、`access: allow` は PAT 権限とは別のポリシー判定とする。

分類結果は `Requirement(TargetId, PermissionKey, Level, AlternativeGroup?)` の列にする。既存の `TargetRequirement(Repository, Category, Level, AlternativeGroup)` は `repository` namespace と `repository` target へ移す。AND 条件は別要求、OR 条件は**同じ対象内**の group とする。経路が複数の対象に作用する場合は対象ごとに要求を出す。GitHub の「追加権限」欄にある複合条件は経路・メソッドごとの文書で確認し、`orgs/` や `user/` という接頭辞だけで権限を決めない。[REST 権限一覧](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens)は同じ `/orgs/` 以下でも organization 権限と repository 権限の経路があることを示している。

## ポリシー JSON

ポリシーは次の形式だけを受け付ける。`schemaVersion` などの version 指定は不要で、旧形式の `global`、`organizations`、`repositories` は受け付けない。

```json
{
  "targets": [
    {
      "target": { "kind": "repository", "pattern": "acme/project" },
      "access": "allow",
      "permissions": { "repository": { "secrets": "read" } }
    },
    {
      "target": { "kind": "organization", "pattern": "acme" },
      "access": "allow",
      "permissions": { "organization": { "secrets": "write" } }
    },
    {
      "target": { "kind": "account", "pattern": "alice" },
      "access": "allow",
      "permissions": { "account": { "emails": "read" } }
    }
  ]
}
```

各ルールの `target.kind` と `permissions` の namespace の一致を必須にする。アカウント名は完全一致とし、アカウントをまたぐ wildcard は許可しない。組織とリポジトリの pattern は大文字小文字を区別しない glob 規則を使う。未知の kind、namespace、権限名、level は拒否する。一致したルールをファイル名順、配列順に適用し、後の指定が同じ項目を上書きする。

ローカル `.gh-harness.json` は、**Git remote が指すその 1 リポジトリの repository ルールだけ**に適用する。organization/account ルールと PAT profile はホーム設定でのみ定義でき、ローカルファイルに含まれていたら設定エラーにする。これにより、作業ツリーの設定が組織全体、ユーザーアカウント、他リポジトリの権限を変更しない。

## 任意の PAT profile を上限として使う

ポリシーの `allow` はトークンの能力を証明しない。希望する利用者向けに、ホーム専用の `~/.gh-harness/pat-profiles.json` を**追加の上限**として使う。

```json
{
  "mode": "enforce",
  "profiles": [
    {
      "host": "github.com",
      "tokenSha256": "<64 桁の小文字 16 進数>",
      "account": "alice",
      "resourceOwner": { "kind": "organization", "login": "acme" },
      "repositoryAccess": { "selection": "selected", "names": ["acme/project"] },
      "permissions": {
        "repository": { "secrets": "read" },
        "organization": { "secrets": "write" }
      }
    }
  ]
}
```

`repositoryAccess.selection` は `all` または `selected`。`selected` は完全な `OWNER/REPO` の明示リストで、対象 owner と異なる名前を拒否する。`account` namespace は `resourceOwner.kind == account` かつ同じ login の profile でのみ有効にする。profile にない権限・対象は `none` とみなす。`repository`・`organization`・`account` のポリシー判定が `allow` であっても、profile の上限を超えれば拒否する。公開リポジトリへの例外的な読み取り能力がトークンにあっても、この上限は自動緩和しない。

このファイルが存在しない場合、現在と同じく profile 判定を行わない。存在して `mode: enforce` の場合、**実際に `gh` が使う資格情報**と一致する profile がなければ拒否する。`GH_TOKEN` は `GITHUB_TOKEN` と保存済み資格情報より優先されるため、active account 名だけで profile を選ばない。[GitHub CLI の環境変数](https://cli.github.com/manual/gh_help_environment)を踏まえ、実効トークンを `gh auth token --hostname github.com` など CLI と同じ解決規則で取得し、プロセス内で SHA-256 を計算して profile の指紋と照合する。生トークン、指紋、`gh auth token` の出力をログ・trace に出さない。複数 profile が一致したら拒否する。profile の宣言内容、失効、承認状態、実際の GitHub 側の権限はローカル JSON から検証できないため、これは**宣言された上限**であり、実際の API 成功保証ではない。組織が承認を要求する場合、承認待ち PAT は公開資源の読み取りに限定される。[PAT の承認条件](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens)を参照。

## アカウントと複数対象の結び付け

アカウント対象の `/user/...` 経路は URL に login がない。実行時に同じ実効資格情報で `GET /user` の認証済み login を取得し、`TargetId(account, login)` を確定する。明示 login を持つ経路でも、自分のアカウント権限を使う操作なら認証済み login と一致させる。取得失敗、資格情報の変更、profile の `account` との不一致は拒否する。`GH_REPO`、Git remote、組織 owner 名からアカウント login を推測しない。[GitHub CLI の認証情報の優先順位](https://cli.github.com/manual/gh_help_environment)と[本人だけが使える account 権限](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens)に合わせる。

分類器はコマンドから影響する対象をすべて特定し、各対象のアクセス・権限・profile 上限を評価する。たとえば `repo sync` は移動元と移動先の別リポジトリ、組織 secret の対象リポジトリ設定は組織と必要なリポジトリを分けて扱う。複数対象が異なる resource owner に属する場合、1 個の fine-grained PAT profile では全件を満たせないので拒否する。判定不能なフラグ、本文中の対象 ID、動的に決まる対象は許可せず、対象を列挙できる操作だけ段階的に登録する。すべての対象を検証してから `gh` を 1 回起動し、途中の一部だけを許可しない。`--explain` には対象別の要求と拒否理由を表示するが、資格情報は表示しない。

## 実装と受け入れ

1. `TargetId` と namespace 付き `PermissionKey` を導入し、既存の repository 要求を変換する。複数リポジトリ操作と `--explain` を対象別に検証する。
2. 新しい `targets` JSON の構文と対象別評価を導入する。ローカル設定の repository 限定、同名の repository/organization 権限の独立、未知値と account wildcard の拒否をテストする。
3. 実効資格情報と account login の結び付けを導入する。環境トークンの優先順位、保存済み資格情報、認証失敗、資格情報変更をテストする。ネットワークを伴う確認の失敗は fail closed とする。
4. 任意 profile の上限判定を導入する。resource owner、選択リポジトリ、namespace 別 level、複数 owner、profile 不一致をテストする。ローカル設定から profile を定義・無効化できないことを確認する。
5. 具体的な `gh` 操作・REST メソッドを公式エンドポイント文書と照合して追加する。現時点の対応は組織 Actions Secrets / Variables の一部、対応する `gh secret --org` / `gh variable --org`、本人の `GET /user/emails`。経路別の条件は[権限対応表](permission-matrix-ja.md)に記録する。

GitHub が記載する fine-grained PAT の制限には、複数組織への同時アクセス、外部コラボレーターとしてのアクセス、Packages、Checks API などがある。対象種別の追加だけではこれらを解消できない。[PAT の制限](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens)に従い、対応しない経路は引き続き拒否する。
