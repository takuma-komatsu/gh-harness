# 権限対応表

[English](permission-matrix.md) | 日本語

この表は `gh-harness` が **実行前に要求するポリシー上の権限**を記録します。GitHub の fine-grained personal access token（fine-grained PAT）に設定する権限の参考になりますが、実際の API 成否はトークンの resource owner、選択リポジトリ、承認状態、ユーザー自身の権限、GitHub 側の追加条件にも依存します。[GitHub の PAT 説明](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens)と[REST 権限一覧](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens)を参照してください。

表の `read` / `write` はポリシーの `permissions` の値です。`write` は `read` を満たします。`A + B` は両方、`A または B` は同じ対象でいずれか一方を要求します。複数の対象がある行は各対象で別々に評価します。いずれも `access: allow` が前提です。`access: allow` は `gh-harness` 固有の設定で、PAT の権限名ではありません。

## `gh` コマンド

| コマンド | 対象 | 要求する権限 |
| --- | --- | --- |
| `pr list/view/diff` | 選択リポジトリ | Pull requests: read |
| `pr create/close/comment/edit/lock/ready/reopen/review/unlock` | 選択リポジトリ | Pull requests: write |
| `pr merge` | 選択リポジトリ | Contents: write |
| `issue list/view/status` | 選択リポジトリ | Issues: read |
| `issue create/edit/close/reopen/comment/delete/lock/unlock/pin/unpin` | 選択リポジトリ | Issues: write |
| `issue transfer` | 移動元、移動先 | それぞれ Issues: write |
| `run list/view/watch/download`、`workflow list/view`、`cache list` | 選択リポジトリ | Actions: read |
| `run rerun/cancel/delete`、`workflow run/enable/disable`、`cache delete` | 選択リポジトリ | Actions: write |
| `release list/view/download` | 選択リポジトリ | Contents: read |
| `release create/edit` | 選択リポジトリ | Contents: write + Workflows: write |
| `release delete/delete-asset/upload` | 選択リポジトリ | Contents: write |
| `secret list` / `secret set/delete` | 選択リポジトリ | Secrets: read / write。`--env` 指定時は Environments: read / write |
| `variable list/get` / `variable set/delete` | 選択リポジトリ | Variables: read / write。`--env` 指定時は Environments: read / write |
| `secret list` / `secret set/delete --org LOGIN` | 指定組織 | Organization Secrets: read / write |
| `variable list/get` / `variable set/delete --org LOGIN` | 指定組織 | Organization Variables: read / write |
| `repo clone/read-file/read-dir` | 対象リポジトリ | Contents: read |
| `repo sync` | ソース、移動先 | ソース Contents: read + 移動先 Contents: write |
| `repo view` | 対象リポジトリ | `access: allow` のみ |

この一覧は `CommandClassifier.ClassifyStandard` の判定です。`gh` が内部で呼び出す API の全経路を保証するものではありません。特にコマンドのフラグや対象の状態で追加の権限が必要になり得るため、追加時には [GitHub CLI manual](https://cli.github.com/manual/) と該当する [REST エンドポイントの文書](https://docs.github.com/en/rest)を照合します。現在 `pr create` はローカルブランチの `--head`、`repo sync` は明示した移動先と `--source` が必要です。

`release create/edit` は対象コミットが workflow ファイルを変更する場合に Workflows: write が追加で必要です。対象コミットを実行前に確定できないため、常に両権限を要求します。`release upload` の Contents: write は [release asset API](https://docs.github.com/en/rest/releases/assets) と CLI の動作からの推定です。asset upload 用の `uploads.github.com` は `gh api` の許可ホストに含めません。CLI の `--discussion-category`、`--cleanup-tag`、`--verify-tag`、`release verify/verify-asset` は未対応です。

`secret` は [GitHub CLI の secret manual](https://cli.github.com/manual/gh_secret) にある Actions のリポジトリ・環境・組織対象を分類します。`--app actions` を指定できます。`variable` は [variable manual](https://cli.github.com/manual/gh_variable) にあるリポジトリ・環境・組織対象を分類します。`--org LOGIN` は組織 Secrets / Variables 権限を要求します。`--user`、他の `--app`、組織向けの `--visibility` / `--repos`、複数項目を一度に設定する `--env-file`、secret の `--no-store` は未対応です。環境に対する操作は Secrets / Variables 権限ではなく Environments 権限を要求します。

## `gh api` の REST 経路

以下の相対パスには `repos/OWNER/REPO/` を付けます。`NUMBER`、`COMMENT_ID`、`REVIEW_ID`、`ID` は 1 個のパス要素、`PATH` は 1 個以上のパス要素です。表にないメソッドと経路の組み合わせは拒否します。メソッドを省略すると GET、`-f` / `-F`（`--raw-field` / `--field`）を指定すると POST です。

| 相対パス | メソッド | 要求する権限 | GitHub の参照先 |
| --- | --- | --- | --- |
| `pulls` | GET / POST | Pull requests: read / write | [Pull requests](https://docs.github.com/en/rest/pulls/pulls) |
| `pulls/NUMBER` | GET / PATCH | Pull requests: read / write | [Pull requests](https://docs.github.com/en/rest/pulls/pulls) |
| `pulls/NUMBER/merge` | PUT | Contents: write | [Merges](https://docs.github.com/en/rest/pulls/pulls#merge-a-pull-request) |
| `pulls/comments` | GET | Pull requests: read | [Review comments](https://docs.github.com/en/rest/pulls/comments) |
| `pulls/comments/COMMENT_ID` | GET / PATCH / DELETE | Pull requests: read / write / write | [Review comments](https://docs.github.com/en/rest/pulls/comments) |
| `pulls/NUMBER/comments` | GET / POST | Pull requests: read / write | [Review comments](https://docs.github.com/en/rest/pulls/comments) |
| `pulls/NUMBER/comments/COMMENT_ID/replies` | POST | Pull requests: write | [Review comments](https://docs.github.com/en/rest/pulls/comments) |
| `pulls/NUMBER/reviews/REVIEW_ID/comments` | GET | Pull requests: read | [Reviews](https://docs.github.com/en/rest/pulls/reviews) |
| `pulls/NUMBER/commits`、`pulls/NUMBER/files`、`pulls/NUMBER/merge` | GET | Pull requests: read | [Pull requests](https://docs.github.com/en/rest/pulls/pulls) |
| `pulls/NUMBER/requested_reviewers` | GET / POST / DELETE | Pull requests: read / write / write | [Review requests](https://docs.github.com/en/rest/pulls/review-requests) |
| `pulls/NUMBER/reviews` | GET / POST | Pull requests: read / write | [Reviews](https://docs.github.com/en/rest/pulls/reviews) |
| `pulls/NUMBER/reviews/REVIEW_ID` | GET / PUT / DELETE | Pull requests: read / write / write | [Reviews](https://docs.github.com/en/rest/pulls/reviews) |
| `pulls/NUMBER/reviews/REVIEW_ID/events` | POST | Pull requests: write | [Reviews](https://docs.github.com/en/rest/pulls/reviews) |
| `pulls/NUMBER/reviews/REVIEW_ID/dismissals` | PUT | Pull requests: write | [Reviews](https://docs.github.com/en/rest/pulls/reviews) |
| `pulls/NUMBER/update-branch` | PUT | Pull requests: write | [Pull requests](https://docs.github.com/en/rest/pulls/pulls) |
| `issues` | GET / POST | Issues: read / write | [Issues](https://docs.github.com/en/rest/issues/issues) |
| `issues/NUMBER` | GET / PATCH | Issues: read / (Issues: write または Pull requests: write) | [Issues](https://docs.github.com/en/rest/issues/issues) |
| `issues/NUMBER/comments` | GET / POST | Issues: read または Pull requests: read / Issues: write または Pull requests: write | [Issue comments](https://docs.github.com/en/rest/issues/comments) |
| `issues/comments` | GET | Issues: read または Pull requests: read | [Issue comments](https://docs.github.com/en/rest/issues/comments) |
| `issues/comments/COMMENT_ID` | GET / PATCH / DELETE | Issues: read または Pull requests: read / Issues: write または Pull requests: write / Issues: write または Pull requests: write | [Issue comments](https://docs.github.com/en/rest/issues/comments) |
| `issues/NUMBER/labels` | GET / POST / PUT / DELETE | Issues: read または Pull requests: read / 書き込みは Issues: write または Pull requests: write | [Labels](https://docs.github.com/en/rest/issues/labels) |
| `issues/NUMBER/labels/NAME` | DELETE | Issues: write または Pull requests: write | [Labels](https://docs.github.com/en/rest/issues/labels) |
| `labels`、`labels/NAME` | GET / POST（一覧のみ）/ PATCH・DELETE（単体のみ） | 読み取りは Issues: read または Pull requests: read、書き込みは Issues: write または Pull requests: write | [Labels](https://docs.github.com/en/rest/issues/labels) |
| `milestones`、`milestones/NUMBER` | GET / POST（一覧のみ）/ PATCH・DELETE（単体のみ） | 読み取りは Issues: read または Pull requests: read、書き込みは Issues: write または Pull requests: write | [Milestones](https://docs.github.com/en/rest/issues/milestones) |
| `milestones/NUMBER/labels` | GET | Issues: read または Pull requests: read | [Labels](https://docs.github.com/en/rest/issues/labels) |
| `commits/REF/status` | GET | Commit statuses: read | [Commit statuses](https://docs.github.com/en/rest/commits/statuses#get-the-combined-status-for-a-specific-reference) |
| `commits/REF/statuses` | GET | Commit statuses: read | [Commit statuses](https://docs.github.com/en/rest/commits/statuses#list-commit-statuses-for-a-reference) |
| `statuses/SHA` | POST | Commit statuses: write | [Commit statuses](https://docs.github.com/en/rest/commits/statuses#create-a-commit-status) |
| `actions/workflows` | GET | Actions: read | [Workflows](https://docs.github.com/en/rest/actions/workflows) |
| `actions/workflows/ID` | GET | Actions: read | [Workflows](https://docs.github.com/en/rest/actions/workflows) |
| `actions/workflows/ID/dispatches` | POST | Actions: write | [Workflows](https://docs.github.com/en/rest/actions/workflows) |
| `actions/runs` | GET | Actions: read | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs) |
| `actions/runs/ID` | GET / DELETE | Actions: read / write | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs) |
| `actions/runs/ID/rerun` | POST | Actions: write | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs) |
| `actions/runs/ID/cancel` | POST | Actions: write | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs) |
| `actions/runs/ID/approve`、`force-cancel`、`rerun-failed-jobs` | POST | Actions: write | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs) |
| `actions/runs/ID/pending_deployments` | POST | Deployments: write | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs#review-pending-deployments-for-a-workflow-run) |
| `actions/runs/ID/jobs`、`logs`、`artifacts`、`approvals`、`pending_deployments`、`timing` | GET | Actions: read | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs) |
| `actions/runs/ID/logs` | DELETE | Actions: write | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs) |
| `actions/runs/ID/attempts/NUMBER`、`actions/runs/ID/attempts/NUMBER/jobs`、`actions/runs/ID/attempts/NUMBER/logs` | GET | Actions: read | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs) |
| `actions/jobs/ID`、`actions/jobs/ID/logs` | GET | Actions: read | [Workflow jobs](https://docs.github.com/en/rest/actions/workflow-jobs) |
| `actions/jobs/ID/rerun` | POST | Actions: write | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs#re-run-a-job-from-a-workflow-run) |
| `actions/artifacts`、`actions/artifacts/ID`、`actions/artifacts/ID/zip` | GET | Actions: read | [Artifacts](https://docs.github.com/en/rest/actions/artifacts) |
| `actions/artifacts/ID` | DELETE | Actions: write | [Artifacts](https://docs.github.com/en/rest/actions/artifacts) |
| `actions/caches`、`actions/cache/usage` | GET | Actions: read | [Caches](https://docs.github.com/en/rest/actions/cache) |
| `actions/caches`、`actions/caches/ID` | DELETE | Actions: write | [Caches](https://docs.github.com/en/rest/actions/cache) |
| `actions/workflows/ID/runs`、`actions/workflows/ID/timing` | GET | Actions: read | [Workflows](https://docs.github.com/en/rest/actions/workflows) |
| `actions/workflows/ID/enable`、`actions/workflows/ID/disable` | PUT | Actions: write | [Workflows](https://docs.github.com/en/rest/actions/workflows) |
| `actions/secrets`、`actions/secrets/public-key` | GET | Secrets: read | [Actions secrets](https://docs.github.com/en/rest/actions/secrets) |
| `actions/secrets/NAME` | GET / PUT / DELETE | Secrets: read / write / write | [Actions secrets](https://docs.github.com/en/rest/actions/secrets) |
| `actions/variables` | GET / POST | Variables: read / write | [Actions variables](https://docs.github.com/en/rest/actions/variables) |
| `actions/variables/NAME` | GET / PATCH / DELETE | Variables: read / write / write | [Actions variables](https://docs.github.com/en/rest/actions/variables) |
| `environments/NAME/secrets`、`environments/NAME/secrets/public-key` | GET | Environments: read | [Actions secrets](https://docs.github.com/en/rest/actions/secrets) |
| `environments/NAME/secrets/SECRET_NAME` | GET / PUT / DELETE | Environments: read / write / write | [Actions secrets](https://docs.github.com/en/rest/actions/secrets) |
| `environments/NAME/variables` | GET / POST | Environments: read / write | [Actions variables](https://docs.github.com/en/rest/actions/variables) |
| `environments/NAME/variables/VARIABLE_NAME` | GET / PATCH / DELETE | Environments: read / write / write | [Actions variables](https://docs.github.com/en/rest/actions/variables) |
| `contents` | GET | Contents: read | [Repository contents](https://docs.github.com/en/rest/repos/contents) |
| `contents/PATH` | GET / PUT / DELETE | Contents: read / write / write | [Repository contents](https://docs.github.com/en/rest/repos/contents) |
| `contents/.github/workflows/PATH` | PUT / DELETE | Contents: write + Workflows: write | [Repository contents](https://docs.github.com/en/rest/repos/contents) |
| `releases` | GET / POST | Contents: read / (Contents: write + Workflows: write) | [Releases](https://docs.github.com/en/rest/releases/releases) |
| `releases/latest`、`releases/tags/TAG`、`releases/ID` | GET | Contents: read | [Releases](https://docs.github.com/en/rest/releases/releases) |
| `releases/ID` | PATCH / DELETE | (Contents: write + Workflows: write) / Contents: write | [Releases](https://docs.github.com/en/rest/releases/releases) |
| `releases/generate-notes` | POST | Contents: write | [Releases](https://docs.github.com/en/rest/releases/releases) |
| `releases/ID/assets`、`releases/assets/ASSET_ID` | GET | Contents: read | [Release assets](https://docs.github.com/en/rest/releases/assets) |
| `releases/assets/ASSET_ID` | PATCH / DELETE | Contents: write | [Release assets](https://docs.github.com/en/rest/releases/assets) |
| `deployments` | GET / POST | Deployments: read / write | [Deployments](https://docs.github.com/en/rest/deployments/deployments) |
| `deployments/ID` | GET / DELETE | Deployments: read / write | [Deployments](https://docs.github.com/en/rest/deployments/deployments) |
| `deployments/ID/statuses` | GET / POST | Deployments: read / write | [Deployment statuses](https://docs.github.com/en/rest/deployments/statuses) |
| `deployments/ID/statuses/ID` | GET | Deployments: read | [Deployment statuses](https://docs.github.com/en/rest/deployments/statuses) |
| `pages` | GET / POST / PUT / DELETE | Pages: read / Pages: write + Administration: write | [Pages](https://docs.github.com/en/rest/pages/pages) |
| `pages/health` | GET | Pages: write + Administration: write | [Pages](https://docs.github.com/en/rest/pages/pages) |
| `pages/builds` | GET / POST | Pages: read / write | [Pages builds](https://docs.github.com/en/rest/pages/pages) |
| `pages/builds/latest`、`pages/builds/ID` | GET | Pages: read | [Pages builds](https://docs.github.com/en/rest/pages/pages) |
| `pages/deployments` | POST | Pages: write | [Pages deployments](https://docs.github.com/en/rest/pages/pages) |
| `pages/deployments/ID` | GET | Pages: read | [Pages deployments](https://docs.github.com/en/rest/pages/pages) |
| `pages/deployments/ID/cancel` | POST | Pages: write | [Pages deployments](https://docs.github.com/en/rest/pages/pages) |
| `hooks` | GET / POST | Webhooks: read / write | [Repository webhooks](https://docs.github.com/en/rest/repos/webhooks) |
| `hooks/ID` | GET / PATCH / DELETE | Webhooks: read / write / write | [Repository webhooks](https://docs.github.com/en/rest/repos/webhooks) |
| `hooks/ID/config` | GET / PATCH | Webhooks: read / write | [Repository webhooks](https://docs.github.com/en/rest/repos/webhooks) |
| `hooks/ID/deliveries`、`hooks/ID/deliveries/ID` | GET | Webhooks: read | [Repository webhooks](https://docs.github.com/en/rest/repos/webhooks) |
| `hooks/ID/deliveries/ID/attempts` | POST | Webhooks: write | [Repository webhooks](https://docs.github.com/en/rest/repos/webhooks) |
| `hooks/ID/pings`、`hooks/ID/tests` | POST | Webhooks: read | [Repository webhooks](https://docs.github.com/en/rest/repos/webhooks) |
| `dependabot/alerts` | GET | Dependabot alerts: read | [Dependabot alerts](https://docs.github.com/en/rest/dependabot/alerts) |
| `dependabot/alerts/NUMBER` | GET / PATCH | Dependabot alerts: read / write | [Dependabot alerts](https://docs.github.com/en/rest/dependabot/alerts) |
| `code-scanning/ai-scan`、`code-scanning/alerts`、`code-scanning/analyses` | GET | Code scanning alerts: read | [Code scanning](https://docs.github.com/en/rest/code-scanning/code-scanning) |
| `code-scanning/alerts/NUMBER` | GET / PATCH | Code scanning alerts: read / write | [Code scanning](https://docs.github.com/en/rest/code-scanning/code-scanning) |
| `code-scanning/alerts/NUMBER/autofix` | GET / POST | Code scanning alerts: read / write | [Code scanning](https://docs.github.com/en/rest/code-scanning/code-scanning) |
| `code-scanning/alerts/NUMBER/instances` | GET | Code scanning alerts: read | [Code scanning](https://docs.github.com/en/rest/code-scanning/code-scanning) |
| `code-scanning/analyses/ID` | GET / DELETE | Code scanning alerts: read / write | [Code scanning](https://docs.github.com/en/rest/code-scanning/code-scanning) |
| `code-scanning/sarifs`、`code-scanning/sarifs/ID` | POST（一覧）/ GET（単体） | Code scanning alerts: write / read | [Code scanning](https://docs.github.com/en/rest/code-scanning/code-scanning) |
| `secret-scanning/alerts`、`secret-scanning/scan-history` | GET | Secret scanning alerts: read | [Secret scanning](https://docs.github.com/en/rest/secret-scanning/secret-scanning) |
| `secret-scanning/alerts/NUMBER` | GET / PATCH | Secret scanning alerts: read / write | [Secret scanning](https://docs.github.com/en/rest/secret-scanning/secret-scanning) |
| `secret-scanning/alerts/NUMBER/locations` | GET | Secret scanning alerts: read | [Secret scanning](https://docs.github.com/en/rest/secret-scanning/secret-scanning) |
| `security-advisories` | GET / POST | Repository security advisories: read / write | [Repository advisories](https://docs.github.com/en/rest/security-advisories/repository-advisories) |
| `security-advisories/reports` | POST | Repository security advisories: write | [Repository advisories](https://docs.github.com/en/rest/security-advisories/repository-advisories) |
| `security-advisories/GHSA_ID` | GET / PATCH | Repository security advisories: read / write | [Repository advisories](https://docs.github.com/en/rest/security-advisories/repository-advisories) |
| `security-advisories/GHSA_ID/cve` | POST | Repository security advisories: write | [Repository advisories](https://docs.github.com/en/rest/security-advisories/repository-advisories) |
| `security-advisories/GHSA_ID/forks` | POST | Repository security advisories: read + Administration: write | [Repository advisories](https://docs.github.com/en/rest/security-advisories/repository-advisories) |

`contents/.github/workflows/PATH` の行は `contents/PATH` に対する条件付きの追加権限です。`.github/workflows/` 以下のファイルが対象のときだけ適用され、GET には適用されません。この条件は、[Contents API](https://docs.github.com/en/rest/repos/contents) の classic PAT に関する workflow scope の注記と fine-grained token の権限セットの記述から導いた `gh-harness` の保守的な判定です。GitHub の文書がこのパス条件を fine-grained PAT 向けに明示しているわけではありません。

組織と本人アカウントの REST 経路は次のとおりです。本人アカウントは同じ実効資格情報による `GET /user` で確定します。

| 経路 | メソッド | 要求する権限 | GitHub の参照先 |
| --- | --- | --- | --- |
| `orgs/ORG/actions/secrets`、`orgs/ORG/actions/secrets/public-key`、`orgs/ORG/actions/secrets/NAME` | GET | Organization Secrets: read | [Actions secrets](https://docs.github.com/en/rest/actions/secrets) |
| `orgs/ORG/actions/secrets/NAME` | DELETE | Organization Secrets: write | [Actions secrets](https://docs.github.com/en/rest/actions/secrets) |
| `orgs/ORG/actions/variables`、`orgs/ORG/actions/variables/NAME` | GET | Organization Variables: read | [Actions variables](https://docs.github.com/en/rest/actions/variables) |
| `orgs/ORG/actions/variables/NAME` | DELETE | Organization Variables: write | [Actions variables](https://docs.github.com/en/rest/actions/variables) |
| `user/emails` | GET | Account Emails: read | [User emails](https://docs.github.com/en/rest/users/emails) |

Deployments、Pages、Webhooks、各種 security alerts、Repository security advisories は上記のリポジトリ REST 経路に限定します。その他の `orgs/`・`user/` 経路、Dependabot secrets、secret scanning の push protection などは含めません。Pages の設定操作と health、advisory の一時 fork は各エンドポイント文書の複合権限を要求します。`--method` / `-X`、`--source`、`--head` / `-H`、`--app` / `-a`、`--env` / `-e` の重複指定は拒否します。

## 現在の範囲と追加時の手順

`gh-harness` は `github.com` の登録済みリポジトリ・組織・本人アカウント操作を分類します。GraphQL、拡張機能、`gh` 内エイリアス、未登録経路、副作用を安全に判定できないフラグは拒否します。任意の PAT profile で resource owner と選択リポジトリを上限として指定できます。詳細は[対象範囲の設計](pat-scope-design-ja.md)を参照してください。[GitHub の PAT 説明](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens)には、複数組織への同時アクセス、外部コラボレーターとしてのアクセス、Packages などの fine-grained PAT の制限も記載されています。

`gh api` は最初に指定された URL のリポジトリだけを評価し、実体の `gh` が追うリダイレクト先は再評価しません。たとえば [転送済み Issue の取得](https://docs.github.com/en/rest/issues/issues#get-an-issue)は別リポジトリへの `301` を返すことがあり、[GitHub CLI はリダイレクトを追います](https://github.com/cli/cli/issues/8055)。このため、`gh api` の最終的なアクセス先が評価済みリポジトリに限られる保証はありません。

同じ PAT 説明は fine-grained PAT による Checks API 呼び出しを未対応の機能として挙げています。一部の [Checks エンドポイント文書](https://docs.github.com/en/rest/checks/runs)にはトークンに関する記述もありますが、[fine-grained PAT の権限一覧](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens)に Checks カテゴリはありません。このため、Checks 権限・Checks REST 経路・`gh pr checks` は登録していません。上記の Commit statuses は別の権限と API です。

対応を追加するときは次を一緒に更新します。

1. 対象操作の公式文書で fine-grained PAT 対応と必要権限を確認し、メソッド、経路、フラグによる副作用、複数権限の AND / OR 条件を決める。
2. `CommandClassifier` と必要に応じて `PolicyStore` の権限カテゴリを更新する。別リポジトリや組織・ユーザー対象の操作は対象ごとに判定する。
3. 許可と拒否の分類テスト、各権限だけを付与した評価テストを追加する。未知のメソッド・経路が拒否されることも確かめる。
4. この表と README の概要を更新し、公式参照先と条件付き権限を明記する。

GitHub の仕様変更や実際の API 応答で疑義があるときは、該当 REST 文書と `X-Accepted-GitHub-Permissions` ヘッダーを確認します。[GitHub のトラブルシューティング](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api)にはヘッダーの AND / OR 表記例があります。
