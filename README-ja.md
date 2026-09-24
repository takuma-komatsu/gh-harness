# gh-harness

[English](README.md)

`gh-harness` は GitHub CLI (`gh`) の実行前に対象リポジトリと必要な操作権限を検査する .NET 10 製のラッパーです。macOS、Windows、Linux で `dotnet tool` として利用できます。許可されたコマンドは元の引数、標準入出力、終了コードを保って実体の `gh` に渡します。

## インストール

.NET 10 SDK と GitHub CLI をインストールし、両方が PATH から使える状態にしてください。このリポジトリからローカルパッケージを作る場合:

```sh
dotnet pack src/GhHarness/GhHarness.csproj -c Release -o ./artifacts
dotnet tool install --global gh-harness --version 0.1.0 --add-source ./artifacts
```

通常の対話シェルで `gh` を置き換えるには、bash/zsh の起動ファイルに次を追加します。

```sh
alias gh='gh-harness'
```

PowerShell では `$PROFILE` に次を追加します。

```powershell
function gh { & gh-harness @args }
```

`gh-harness` は PATH 上の実体 `gh` を絶対パスで起動します。エイリアスはシェル内だけで有効です。実体 `gh`、別の GitHub API クライアント、スクリプト内の直接呼び出しを強制的に遮断するセキュリティ境界ではありません。

`gh api` の権限判定は指定された最初の URL に対して行います。GitHub が別リポジトリへリダイレクトを返し、実体の `gh api` がそれを追った場合、移動先のリポジトリは再評価されません。例と詳細は[権限対応表の制約](docs/permission-matrix-ja.md#現在の範囲と追加時の手順)を参照してください。

## ポリシー

`~/.gh-harness/` 直下の `*.gh-harness.json` をファイル名順にすべて読みます。現在の作業ツリーのルートに `.gh-harness.json` がある場合、その Git remote が示すリポジトリを評価するときだけ最後に適用します。各ファイルには `targets` を指定します。

```json
{
  "targets": [
    {
      "target": { "kind": "repository", "pattern": "acme-*/release-*" },
      "access": "allow",
      "permissions": { "repository": { "pullRequests": "write", "contents": "write" } }
    },
    {
      "target": { "kind": "organization", "pattern": "acme" },
      "access": "allow",
      "permissions": { "organization": { "secrets": "write", "variables": "read" } }
    }
  ]
}
```

`access` は `allow` / `deny`、各権限は `none` / `read` / `write` です。`write` は `read` を含みます。未指定の対象と権限は拒否します。一致したルールをファイル名順、配列順に適用し、後の指定が同じ項目を上書きします。リポジトリ直下の設定は、Git remote が示すそのリポジトリへの repository ルールだけを記述できます。構文エラーや未知の設定値がある場合は実行を拒否します。旧形式の `global` / `organizations` / `repositories` は受け付けません。

`repository` の pattern は `OWNER/REPO`、`organization` は組織 login、`account` はユーザー login に照合します。リポジトリと組織では大文字小文字を区別しない `*`、`?`、`[]`、`**` を使えます。アカウントは完全一致です。否定には `access: "deny"` を使います。

### PAT profile

`~/.gh-harness/pat-profiles.json` を置くと、実際に `gh` が使うトークンの SHA-256 指紋に一致する profile を追加の上限として適用します。ファイルがなければ profile 判定は行いません。profile の申告内容から GitHub 側の承認・失効・実権限は検証できません。

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
        "repository": { "contents": "read" },
        "organization": { "secrets": "write" }
      }
    }
  ]
}
```

`selected` には完全な `OWNER/REPO` 名を列挙します。`selection: "all"` では `names` を省略します。profile にない対象・権限は許可されず、一致する profile がない場合も拒否します。生トークンと指紋は表示しません。

## 操作と権限

対象は明示的な GitHub URL または `-R` / `--repo`、`GH_REPO`、現在の Git リポジトリの remote の順に決まります。初版は `github.com` のみを対象とし、判定後は子プロセスの `GH_REPO` と `GH_HOST` を固定します。複数の明示対象が矛盾する場合は拒否します。

全コマンドと REST 経路の判定条件、公式文書への参照、追加時の手順は[権限対応表](docs/permission-matrix-ja.md)を参照してください。

組織・アカウント権限と fine-grained PAT の resource owner／選択リポジトリの扱いは[対象範囲の設計](docs/pat-scope-design-ja.md)を参照してください。

| コマンド | 必要な権限 |
| --- | --- |
| `pr list/view/diff` | Pull requests: read |
| `pr create/close/comment/edit/lock/ready/reopen/review/unlock` | Pull requests: write |
| `pr merge` | Contents: write |
| `issue list/view/status` | Issues: read |
| `issue create/edit/close/reopen/comment/delete/lock/unlock/pin/unpin` | Issues: write |
| `issue transfer` | 移動元・移動先の Issues: write |
| `run list/view/watch/download`、`workflow list/view`、`cache list` | Actions: read |
| `run rerun/cancel/delete`、`workflow run/enable/disable`、`cache delete` | Actions: write |
| `release list/view/download` | Contents: read |
| `release create/edit` | Contents: write + Workflows: write |
| `release delete/delete-asset/upload` | Contents: write |
| `secret list` / `secret set/delete` | Secrets: read / write。`--env` 指定時は Environments: read / write |
| `variable list/get` / `variable set/delete` | Variables: read / write。`--env` 指定時は Environments: read / write |
| `repo clone/read-file/read-dir` | Contents: read |
| `repo sync` | 移動元の Contents: read、移動先の Contents: write |
| `repo view` | リポジトリへの access: allow |

`pr create` にはローカルブランチを示す `--head` が必要です。`repo sync` には明示的な移動先 `OWNER/REPO` と `--source OWNER/REPO` が必要です。GraphQL、拡張機能、gh 内エイリアス、未分類コマンド、判定できない副作用を持つフラグは拒否します。

`release create/edit` の Workflows: write は、対象コミットによって必要になる条件を実行前に確定できないため、常に要求します。release の discussion 作成、tag の同時削除、attestation 検証は未対応です。

`secret` は Actions のリポジトリ・環境・組織 secret に対応します。`--app actions` は指定できます。`variable` はリポジトリ・環境・組織 variable に対応します。組織対象は `--org LOGIN` で指定します。`gh api user/emails` の GET は本人の `account.emails:read` を要求します。認証済み login を `GET /user` で確認し、同じトークンを実行時にも使います。`--user`、他の `--app`、組織向けの公開範囲・リポジトリ選択、`--env-file` は未対応です。

### `gh api` の対応 REST エンドポイント

パスには `repos/OWNER/REPO/` を先頭に付けます。`NUMBER`、`COMMENT_ID`、`REVIEW_ID` は実際の ID、`PATH` はファイルまたはディレクトリのパスに置き換えます。`ID` は対象の ID を表し、ワークフローでは `build.yml` のようなファイル名も使えます。メソッドを省略した場合は通常 GET、`-f` / `-F`（`--raw-field` / `--field`）を使った場合は POST です。

| パス | メソッドと必要な権限 |
| --- | --- |
| `pulls` | GET: Pull requests: read / POST: Pull requests: write |
| `pulls/NUMBER` | GET: Pull requests: read / PATCH: Pull requests: write |
| `pulls/NUMBER/merge` | PUT: Contents: write |
| `pulls/comments` | GET: Pull requests: read |
| `pulls/comments/COMMENT_ID` | GET: Pull requests: read / PATCH・DELETE: Pull requests: write |
| `pulls/NUMBER/comments` | GET: Pull requests: read / POST: Pull requests: write |
| `pulls/NUMBER/comments/COMMENT_ID/replies` | POST: Pull requests: write |
| `pulls/NUMBER/reviews/REVIEW_ID/comments` | GET: Pull requests: read |
| `pulls/NUMBER/commits`、`files`、`merge` | GET: Pull requests: read |
| `pulls/NUMBER/requested_reviewers`、`reviews` | GET: Pull requests: read / POST: Pull requests: write。`requested_reviewers` は DELETE も write |
| `pulls/NUMBER/reviews/REVIEW_ID` | GET: Pull requests: read / PUT・DELETE: Pull requests: write |
| `pulls/NUMBER/reviews/REVIEW_ID/events`、`dismissals` | POST（events）・PUT（dismissals）: Pull requests: write |
| `pulls/NUMBER/update-branch` | PUT: Pull requests: write |
| `issues` | GET: Issues: read / POST: Issues: write |
| `issues/NUMBER` | GET: Issues: read / PATCH: Issues: write **または** Pull requests: write |
| `issues/NUMBER/comments` | GET: Issues: read **または** Pull requests: read / POST: Issues: write **または** Pull requests: write |
| `issues/comments`、`issues/comments/COMMENT_ID` | GET: Issues: read **または** Pull requests: read。単体コメントの PATCH・DELETE は Issues: write **または** Pull requests: write |
| `issues/NUMBER/labels`、`labels`、`labels/NAME`、`milestones`、`milestones/NUMBER`、`milestones/NUMBER/labels` | メソッド別の対応は[権限対応表](docs/permission-matrix-ja.md)を参照。読み取りは Issues: read **または** Pull requests: read、書き込みは Issues: write **または** Pull requests: write |
| `commits/REF/status`、`commits/REF/statuses` | GET: Commit statuses: read |
| `statuses/SHA` | POST: Commit statuses: write |
| `actions/workflows` | GET: Actions: read |
| `actions/workflows/ID` | GET: Actions: read |
| `actions/workflows/ID/dispatches` | POST: Actions: write |
| `actions/runs` | GET: Actions: read |
| `actions/runs/ID` | GET: Actions: read / DELETE: Actions: write |
| `actions/runs/ID/rerun` | POST: Actions: write |
| `actions/runs/ID/cancel` | POST: Actions: write |
| `actions/runs/ID/approve`、`force-cancel`、`rerun-failed-jobs` | POST: Actions: write |
| `actions/runs/ID/jobs`、`logs`、`artifacts`、`approvals`、`pending_deployments`、`timing`、`attempts/NUMBER`、`attempts/NUMBER/jobs`、`attempts/NUMBER/logs` | GET: Actions: read。`logs` の DELETE は Actions: write |
| `actions/jobs/ID`、`actions/jobs/ID/logs`、`actions/artifacts`、`actions/artifacts/ID`、`actions/artifacts/ID/zip`、`actions/caches`、`actions/cache/usage` | GET: Actions: read。job rerun、artifact 削除、cache 削除は Actions: write |
| `actions/workflows/ID/runs`、`actions/workflows/ID/timing` | GET: Actions: read |
| `actions/workflows/ID/enable`、`actions/workflows/ID/disable` | PUT: Actions: write |
| `actions/secrets`、`actions/secrets/public-key` | GET: Secrets: read |
| `actions/secrets/NAME` | GET: Secrets: read / PUT・DELETE: Secrets: write |
| `actions/variables` | GET: Variables: read / POST: Variables: write |
| `actions/variables/NAME` | GET: Variables: read / PATCH・DELETE: Variables: write |
| `environments/NAME/secrets`、`environments/NAME/secrets/public-key` | GET: Environments: read |
| `environments/NAME/secrets/SECRET_NAME` | GET: Environments: read / PUT・DELETE: Environments: write |
| `environments/NAME/variables` | GET: Environments: read / POST: Environments: write |
| `environments/NAME/variables/VARIABLE_NAME` | GET: Environments: read / PATCH・DELETE: Environments: write |
| `contents` | GET: Contents: read |
| `contents/PATH` | GET: Contents: read / PUT・DELETE: Contents: write。`.github/workflows/` 以下の PUT・DELETE は Workflows: write も必要 |
| `releases` | GET: Contents: read / POST: Contents: write + Workflows: write |
| `releases/latest`、`releases/tags/TAG` | GET: Contents: read |
| `releases/ID` | GET: Contents: read / PATCH: Contents: write + Workflows: write / DELETE: Contents: write |
| `releases/ID/assets`、`releases/assets/ASSET_ID` | GET: Contents: read。単体 asset の PATCH・DELETE は Contents: write |
| `releases/generate-notes` | POST: Contents: write |
| `deployments`、`deployments/ID`、`deployments/ID/statuses`、`actions/runs/ID/pending_deployments` | メソッド別に Deployments: read / write |
| `pages`、`pages/builds`、`pages/deployments` | メソッド別に Pages: read / write。`pages` の POST・PUT・DELETE と `pages/health` の GET は Administration: write も必要 |
| `hooks`、`hooks/ID`、`hooks/ID/config`、`hooks/ID/deliveries` | メソッド別に Webhooks: read / write。`hooks/ID/pings`・`tests` の POST は read |
| `dependabot/alerts`、`dependabot/alerts/NUMBER` | GET: Dependabot alerts: read / 単体 PATCH: write |
| `code-scanning/alerts`、`analyses`、`sarifs` | メソッド別に Code scanning alerts: read / write |
| `secret-scanning/alerts`、`scan-history` | メソッド別に Secret scanning alerts: read / write |
| `security-advisories`、`security-advisories/GHSA_ID` | メソッド別に Repository security advisories: read / write。一時 fork の POST は read と Administration: write |

「または」は同じリポジトリでいずれか一方の権限があれば満たします。別の操作や別のリポジトリに必要な権限はそれぞれ判定します。表にない REST メソッド・パスの組み合わせは拒否します。

Commit statuses と Checks は別の API です。[GitHub の PAT 説明](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens)は fine-grained PAT による Checks API 呼び出しを未対応とし、[fine-grained PAT の権限一覧](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens)にも Checks 権限はありません。そのため Checks REST 経路と `gh pr checks` は対応対象に含めていません。詳しくは[権限対応表](docs/permission-matrix-ja.md)を参照してください。

判定内容を確認するには `--explain` を付けます。実体の `gh` は起動されません。

```sh
gh-harness --explain pr view -R acme-app/release-tools
```

## 開発

```sh
dotnet test tests/GhHarness.Tests/GhHarness.Tests.csproj
dotnet pack src/GhHarness/GhHarness.csproj -c Release -o ./artifacts
```
