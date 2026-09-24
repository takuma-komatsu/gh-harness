# Permission matrix

English | [Japanese](permission-matrix-ja.md)

This matrix records the **permissions that `gh-harness` requires under its policy before running a command**. It can help you configure a GitHub fine-grained personal access token (PAT), but actual API access also depends on the token's resource owner, selected repositories, approval status, your own access, and other GitHub requirements. See GitHub's [PAT documentation](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens) and [REST permission reference](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens).

The `read` and `write` values in the table refer to the policy's `permissions` settings. `write` satisfies a `read` requirement. `A + B` requires both permissions; `A or B` requires either permission for the same target. Rows with multiple targets are evaluated separately for each one. Every row also requires `access: allow`. This setting belongs to `gh-harness`; it is not a PAT permission.

## `gh` commands

| Command | Target | Required permission |
| --- | --- | --- |
| `pr list/view/diff` | Selected repository | Pull requests: read |
| `pr create/close/comment/edit/lock/ready/reopen/review/unlock` | Selected repository | Pull requests: write |
| `pr merge` | Selected repository | Contents: write |
| `issue list/view/status` | Selected repository | Issues: read |
| `issue create/edit/close/reopen/comment/delete/lock/unlock/pin/unpin` | Selected repository | Issues: write |
| `issue transfer` | Source and destination | Issues: write for each |
| `run list/view/watch/download`, `workflow list/view`, `cache list` | Selected repository | Actions: read |
| `run rerun/cancel/delete`, `workflow run/enable/disable`, `cache delete` | Selected repository | Actions: write |
| `release list/view/download` | Selected repository | Contents: read |
| `release create/edit` | Selected repository | Contents: write + Workflows: write |
| `release delete/delete-asset/upload` | Selected repository | Contents: write |
| `secret list` / `secret set/delete` | Selected repository | Secrets: read / write; with `--env`: Environments: read / write |
| `variable list/get` / `variable set/delete` | Selected repository | Variables: read / write; with `--env`: Environments: read / write |
| `secret list` / `secret set/delete --org LOGIN` | Specified organization | Organization Secrets: read / write |
| `variable list/get` / `variable set/delete --org LOGIN` | Specified organization | Organization Variables: read / write |
| `repo clone/read-file/read-dir` | Target repository | Contents: read |
| `repo sync` | Source and destination | Contents: read for source + Contents: write for destination |
| `repo view` | Target repository | `access: allow` only |

These entries describe how `CommandClassifier.ClassifyStandard` classifies commands. They do not guarantee coverage of every API call that `gh` may make internally. Flags and target state can introduce further requirements. When adding support, check the [GitHub CLI manual](https://cli.github.com/manual/) and the relevant [REST endpoint documentation](https://docs.github.com/en/rest). Currently, `pr create` requires a local branch as `--head`, and `repo sync` requires an explicit destination and `--source`.

`release create/edit` also needs Workflows: write when the target commit changes a workflow file. Because the target commit cannot be determined before execution, the policy always requires both permissions. The Contents: write requirement for `release upload` is inferred from the [release asset API](https://docs.github.com/en/rest/releases/assets) and CLI behavior. `uploads.github.com`, which handles asset uploads, is not an allowed host for `gh api`. The CLI's `--discussion-category`, `--cleanup-tag`, and `--verify-tag` flags, as well as `release verify/verify-asset`, are unsupported.

For `secret`, the classifier covers Actions secrets at the repository, environment, and organization levels documented in the [GitHub CLI secret manual](https://cli.github.com/manual/gh_secret). `--app actions` is supported. For `variable`, it covers those same levels as documented in the [variable manual](https://cli.github.com/manual/gh_variable). `--org LOGIN` requires Organization Secrets or Organization Variables permission. Unsupported options include `--user`, other `--app` values, organization `--visibility` and `--repos`, `--env-file` for setting multiple items, and `--no-store` for secrets. Environment operations require Environments permission rather than Secrets or Variables permission.

## REST routes for `gh api`

Prefix the relative paths below with `repos/OWNER/REPO/`. `NUMBER`, `COMMENT_ID`, `REVIEW_ID`, and `ID` each represent one path segment; `PATH` represents one or more. Method and route combinations not listed here are denied. The method defaults to GET, or POST when `-f` / `-F` (`--raw-field` / `--field`) is used.

| Relative path | Method | Required permission | GitHub reference |
| --- | --- | --- | --- |
| `pulls` | GET / POST | Pull requests: read / write | [Pull requests](https://docs.github.com/en/rest/pulls/pulls) |
| `pulls/NUMBER` | GET / PATCH | Pull requests: read / write | [Pull requests](https://docs.github.com/en/rest/pulls/pulls) |
| `pulls/NUMBER/merge` | PUT | Contents: write | [Merges](https://docs.github.com/en/rest/pulls/pulls#merge-a-pull-request) |
| `pulls/comments` | GET | Pull requests: read | [Review comments](https://docs.github.com/en/rest/pulls/comments) |
| `pulls/comments/COMMENT_ID` | GET / PATCH / DELETE | Pull requests: read / write / write | [Review comments](https://docs.github.com/en/rest/pulls/comments) |
| `pulls/NUMBER/comments` | GET / POST | Pull requests: read / write | [Review comments](https://docs.github.com/en/rest/pulls/comments) |
| `pulls/NUMBER/comments/COMMENT_ID/replies` | POST | Pull requests: write | [Review comments](https://docs.github.com/en/rest/pulls/comments) |
| `pulls/NUMBER/reviews/REVIEW_ID/comments` | GET | Pull requests: read | [Reviews](https://docs.github.com/en/rest/pulls/reviews) |
| `pulls/NUMBER/commits`, `pulls/NUMBER/files`, `pulls/NUMBER/merge` | GET | Pull requests: read | [Pull requests](https://docs.github.com/en/rest/pulls/pulls) |
| `pulls/NUMBER/requested_reviewers` | GET / POST / DELETE | Pull requests: read / write / write | [Review requests](https://docs.github.com/en/rest/pulls/review-requests) |
| `pulls/NUMBER/reviews` | GET / POST | Pull requests: read / write | [Reviews](https://docs.github.com/en/rest/pulls/reviews) |
| `pulls/NUMBER/reviews/REVIEW_ID` | GET / PUT / DELETE | Pull requests: read / write / write | [Reviews](https://docs.github.com/en/rest/pulls/reviews) |
| `pulls/NUMBER/reviews/REVIEW_ID/events` | POST | Pull requests: write | [Reviews](https://docs.github.com/en/rest/pulls/reviews) |
| `pulls/NUMBER/reviews/REVIEW_ID/dismissals` | PUT | Pull requests: write | [Reviews](https://docs.github.com/en/rest/pulls/reviews) |
| `pulls/NUMBER/update-branch` | PUT | Pull requests: write | [Pull requests](https://docs.github.com/en/rest/pulls/pulls) |
| `issues` | GET / POST | Issues: read / write | [Issues](https://docs.github.com/en/rest/issues/issues) |
| `issues/NUMBER` | GET / PATCH | Issues: read / (Issues: write or Pull requests: write) | [Issues](https://docs.github.com/en/rest/issues/issues) |
| `issues/NUMBER/comments` | GET / POST | Issues: read or Pull requests: read / Issues: write or Pull requests: write | [Issue comments](https://docs.github.com/en/rest/issues/comments) |
| `issues/comments` | GET | Issues: read or Pull requests: read | [Issue comments](https://docs.github.com/en/rest/issues/comments) |
| `issues/comments/COMMENT_ID` | GET / PATCH / DELETE | Issues: read or Pull requests: read / Issues: write or Pull requests: write / Issues: write or Pull requests: write | [Issue comments](https://docs.github.com/en/rest/issues/comments) |
| `issues/NUMBER/labels` | GET / POST / PUT / DELETE | Issues: read or Pull requests: read / write: Issues: write or Pull requests: write | [Labels](https://docs.github.com/en/rest/issues/labels) |
| `issues/NUMBER/labels/NAME` | DELETE | Issues: write or Pull requests: write | [Labels](https://docs.github.com/en/rest/issues/labels) |
| `labels`, `labels/NAME` | GET / POST (collection only) / PATCH or DELETE (single item only) | Read: Issues: read or Pull requests: read; write: Issues: write or Pull requests: write | [Labels](https://docs.github.com/en/rest/issues/labels) |
| `milestones`, `milestones/NUMBER` | GET / POST (collection only) / PATCH or DELETE (single item only) | Read: Issues: read or Pull requests: read; write: Issues: write or Pull requests: write | [Milestones](https://docs.github.com/en/rest/issues/milestones) |
| `milestones/NUMBER/labels` | GET | Issues: read or Pull requests: read | [Labels](https://docs.github.com/en/rest/issues/labels) |
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
| `actions/runs/ID/approve`, `force-cancel`, `rerun-failed-jobs` | POST | Actions: write | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs) |
| `actions/runs/ID/pending_deployments` | POST | Deployments: write | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs#review-pending-deployments-for-a-workflow-run) |
| `actions/runs/ID/jobs`, `logs`, `artifacts`, `approvals`, `pending_deployments`, `timing` | GET | Actions: read | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs) |
| `actions/runs/ID/logs` | DELETE | Actions: write | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs) |
| `actions/runs/ID/attempts/NUMBER`, `actions/runs/ID/attempts/NUMBER/jobs`, `actions/runs/ID/attempts/NUMBER/logs` | GET | Actions: read | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs) |
| `actions/jobs/ID`, `actions/jobs/ID/logs` | GET | Actions: read | [Workflow jobs](https://docs.github.com/en/rest/actions/workflow-jobs) |
| `actions/jobs/ID/rerun` | POST | Actions: write | [Workflow runs](https://docs.github.com/en/rest/actions/workflow-runs#re-run-a-job-from-a-workflow-run) |
| `actions/artifacts`, `actions/artifacts/ID`, `actions/artifacts/ID/zip` | GET | Actions: read | [Artifacts](https://docs.github.com/en/rest/actions/artifacts) |
| `actions/artifacts/ID` | DELETE | Actions: write | [Artifacts](https://docs.github.com/en/rest/actions/artifacts) |
| `actions/caches`, `actions/cache/usage` | GET | Actions: read | [Caches](https://docs.github.com/en/rest/actions/cache) |
| `actions/caches`, `actions/caches/ID` | DELETE | Actions: write | [Caches](https://docs.github.com/en/rest/actions/cache) |
| `actions/workflows/ID/runs`, `actions/workflows/ID/timing` | GET | Actions: read | [Workflows](https://docs.github.com/en/rest/actions/workflows) |
| `actions/workflows/ID/enable`, `actions/workflows/ID/disable` | PUT | Actions: write | [Workflows](https://docs.github.com/en/rest/actions/workflows) |
| `actions/secrets`, `actions/secrets/public-key` | GET | Secrets: read | [Actions secrets](https://docs.github.com/en/rest/actions/secrets) |
| `actions/secrets/NAME` | GET / PUT / DELETE | Secrets: read / write / write | [Actions secrets](https://docs.github.com/en/rest/actions/secrets) |
| `actions/variables` | GET / POST | Variables: read / write | [Actions variables](https://docs.github.com/en/rest/actions/variables) |
| `actions/variables/NAME` | GET / PATCH / DELETE | Variables: read / write / write | [Actions variables](https://docs.github.com/en/rest/actions/variables) |
| `environments/NAME/secrets`, `environments/NAME/secrets/public-key` | GET | Environments: read | [Actions secrets](https://docs.github.com/en/rest/actions/secrets) |
| `environments/NAME/secrets/SECRET_NAME` | GET / PUT / DELETE | Environments: read / write / write | [Actions secrets](https://docs.github.com/en/rest/actions/secrets) |
| `environments/NAME/variables` | GET / POST | Environments: read / write | [Actions variables](https://docs.github.com/en/rest/actions/variables) |
| `environments/NAME/variables/VARIABLE_NAME` | GET / PATCH / DELETE | Environments: read / write / write | [Actions variables](https://docs.github.com/en/rest/actions/variables) |
| `contents` | GET | Contents: read | [Repository contents](https://docs.github.com/en/rest/repos/contents) |
| `contents/PATH` | GET / PUT / DELETE | Contents: read / write / write | [Repository contents](https://docs.github.com/en/rest/repos/contents) |
| `contents/.github/workflows/PATH` | PUT / DELETE | Contents: write + Workflows: write | [Repository contents](https://docs.github.com/en/rest/repos/contents) |
| `releases` | GET / POST | Contents: read / (Contents: write + Workflows: write) | [Releases](https://docs.github.com/en/rest/releases/releases) |
| `releases/latest`, `releases/tags/TAG`, `releases/ID` | GET | Contents: read | [Releases](https://docs.github.com/en/rest/releases/releases) |
| `releases/ID` | PATCH / DELETE | (Contents: write + Workflows: write) / Contents: write | [Releases](https://docs.github.com/en/rest/releases/releases) |
| `releases/generate-notes` | POST | Contents: write | [Releases](https://docs.github.com/en/rest/releases/releases) |
| `releases/ID/assets`, `releases/assets/ASSET_ID` | GET | Contents: read | [Release assets](https://docs.github.com/en/rest/releases/assets) |
| `releases/assets/ASSET_ID` | PATCH / DELETE | Contents: write | [Release assets](https://docs.github.com/en/rest/releases/assets) |
| `deployments` | GET / POST | Deployments: read / write | [Deployments](https://docs.github.com/en/rest/deployments/deployments) |
| `deployments/ID` | GET / DELETE | Deployments: read / write | [Deployments](https://docs.github.com/en/rest/deployments/deployments) |
| `deployments/ID/statuses` | GET / POST | Deployments: read / write | [Deployment statuses](https://docs.github.com/en/rest/deployments/statuses) |
| `deployments/ID/statuses/ID` | GET | Deployments: read | [Deployment statuses](https://docs.github.com/en/rest/deployments/statuses) |
| `pages` | GET / POST / PUT / DELETE | Pages: read / Pages: write + Administration: write | [Pages](https://docs.github.com/en/rest/pages/pages) |
| `pages/health` | GET | Pages: write + Administration: write | [Pages](https://docs.github.com/en/rest/pages/pages) |
| `pages/builds` | GET / POST | Pages: read / write | [Pages builds](https://docs.github.com/en/rest/pages/pages) |
| `pages/builds/latest`, `pages/builds/ID` | GET | Pages: read | [Pages builds](https://docs.github.com/en/rest/pages/pages) |
| `pages/deployments` | POST | Pages: write | [Pages deployments](https://docs.github.com/en/rest/pages/pages) |
| `pages/deployments/ID` | GET | Pages: read | [Pages deployments](https://docs.github.com/en/rest/pages/pages) |
| `pages/deployments/ID/cancel` | POST | Pages: write | [Pages deployments](https://docs.github.com/en/rest/pages/pages) |
| `hooks` | GET / POST | Webhooks: read / write | [Repository webhooks](https://docs.github.com/en/rest/repos/webhooks) |
| `hooks/ID` | GET / PATCH / DELETE | Webhooks: read / write / write | [Repository webhooks](https://docs.github.com/en/rest/repos/webhooks) |
| `hooks/ID/config` | GET / PATCH | Webhooks: read / write | [Repository webhooks](https://docs.github.com/en/rest/repos/webhooks) |
| `hooks/ID/deliveries`, `hooks/ID/deliveries/ID` | GET | Webhooks: read | [Repository webhooks](https://docs.github.com/en/rest/repos/webhooks) |
| `hooks/ID/deliveries/ID/attempts` | POST | Webhooks: write | [Repository webhooks](https://docs.github.com/en/rest/repos/webhooks) |
| `hooks/ID/pings`, `hooks/ID/tests` | POST | Webhooks: read | [Repository webhooks](https://docs.github.com/en/rest/repos/webhooks) |
| `dependabot/alerts` | GET | Dependabot alerts: read | [Dependabot alerts](https://docs.github.com/en/rest/dependabot/alerts) |
| `dependabot/alerts/NUMBER` | GET / PATCH | Dependabot alerts: read / write | [Dependabot alerts](https://docs.github.com/en/rest/dependabot/alerts) |
| `code-scanning/ai-scan`, `code-scanning/alerts`, `code-scanning/analyses` | GET | Code scanning alerts: read | [Code scanning](https://docs.github.com/en/rest/code-scanning/code-scanning) |
| `code-scanning/alerts/NUMBER` | GET / PATCH | Code scanning alerts: read / write | [Code scanning](https://docs.github.com/en/rest/code-scanning/code-scanning) |
| `code-scanning/alerts/NUMBER/autofix` | GET / POST | Code scanning alerts: read / write | [Code scanning](https://docs.github.com/en/rest/code-scanning/code-scanning) |
| `code-scanning/alerts/NUMBER/instances` | GET | Code scanning alerts: read | [Code scanning](https://docs.github.com/en/rest/code-scanning/code-scanning) |
| `code-scanning/analyses/ID` | GET / DELETE | Code scanning alerts: read / write | [Code scanning](https://docs.github.com/en/rest/code-scanning/code-scanning) |
| `code-scanning/sarifs`, `code-scanning/sarifs/ID` | POST (collection) / GET (single item) | Code scanning alerts: write / read | [Code scanning](https://docs.github.com/en/rest/code-scanning/code-scanning) |
| `secret-scanning/alerts`, `secret-scanning/scan-history` | GET | Secret scanning alerts: read | [Secret scanning](https://docs.github.com/en/rest/secret-scanning/secret-scanning) |
| `secret-scanning/alerts/NUMBER` | GET / PATCH | Secret scanning alerts: read / write | [Secret scanning](https://docs.github.com/en/rest/secret-scanning/secret-scanning) |
| `secret-scanning/alerts/NUMBER/locations` | GET | Secret scanning alerts: read | [Secret scanning](https://docs.github.com/en/rest/secret-scanning/secret-scanning) |
| `security-advisories` | GET / POST | Repository security advisories: read / write | [Repository advisories](https://docs.github.com/en/rest/security-advisories/repository-advisories) |
| `security-advisories/reports` | POST | Repository security advisories: write | [Repository advisories](https://docs.github.com/en/rest/security-advisories/repository-advisories) |
| `security-advisories/GHSA_ID` | GET / PATCH | Repository security advisories: read / write | [Repository advisories](https://docs.github.com/en/rest/security-advisories/repository-advisories) |
| `security-advisories/GHSA_ID/cve` | POST | Repository security advisories: write | [Repository advisories](https://docs.github.com/en/rest/security-advisories/repository-advisories) |
| `security-advisories/GHSA_ID/forks` | POST | Repository security advisories: read + Administration: write | [Repository advisories](https://docs.github.com/en/rest/security-advisories/repository-advisories) |

The `contents/.github/workflows/PATH` row adds a conditional requirement to `contents/PATH`. It applies only when the target file is under `.github/workflows/` and does not apply to GET. This is a conservative `gh-harness` decision based on the [Contents API](https://docs.github.com/en/rest/repos/contents) note about the workflow scope for classic PATs and the fine-grained token permission set. GitHub's documentation does not explicitly state this path condition for fine-grained PATs.

The following REST routes cover organizations and the authenticated user's account. The account is established with `GET /user` using the same effective credentials.

| Route | Method | Required permission | GitHub reference |
| --- | --- | --- | --- |
| `orgs/ORG/actions/secrets`, `orgs/ORG/actions/secrets/public-key`, `orgs/ORG/actions/secrets/NAME` | GET | Organization Secrets: read | [Actions secrets](https://docs.github.com/en/rest/actions/secrets) |
| `orgs/ORG/actions/secrets/NAME` | DELETE | Organization Secrets: write | [Actions secrets](https://docs.github.com/en/rest/actions/secrets) |
| `orgs/ORG/actions/variables`, `orgs/ORG/actions/variables/NAME` | GET | Organization Variables: read | [Actions variables](https://docs.github.com/en/rest/actions/variables) |
| `orgs/ORG/actions/variables/NAME` | DELETE | Organization Variables: write | [Actions variables](https://docs.github.com/en/rest/actions/variables) |
| `user/emails` | GET | Account Emails: read | [User emails](https://docs.github.com/en/rest/users/emails) |

Deployments, Pages, Webhooks, security alerts, and repository security advisories are limited to the repository REST routes above. Other `orgs/` and `user/` routes, Dependabot secrets, and secret scanning push protection are outside the supported set. Pages configuration and health operations, and temporary forks for advisories, require the combined permissions listed in their endpoint documentation. Duplicate uses of `--method` / `-X`, `--source`, `--head` / `-H`, `--app` / `-a`, or `--env` / `-e` are denied.

## Current scope and adding support

`gh-harness` classifies registered repository, organization, and authenticated-account operations on `github.com`. It denies GraphQL, extensions, aliases defined within `gh`, unregistered routes, and flags whose side effects cannot be assessed safely. An optional PAT profile can cap access by resource owner and selected repositories. See [PAT scope design](pat-scope-design.md) for details. GitHub's [PAT documentation](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens) also describes fine-grained PAT limitations involving access to multiple organizations, external collaborators, Packages, and more.

`gh api` evaluates only the repository in the initial URL; it does not reevaluate a redirect followed by the underlying `gh` command. For example, [fetching a transferred issue](https://docs.github.com/en/rest/issues/issues#get-an-issue) may return a `301` to another repository, and the [GitHub CLI follows redirects](https://github.com/cli/cli/issues/8055). Consequently, the final destination of a `gh api` request is not guaranteed to be the repository that was evaluated.

The same PAT documentation lists Checks API calls as unsupported for fine-grained PATs. Some [Checks endpoint documentation](https://docs.github.com/en/rest/checks/runs) mentions tokens, but the [fine-grained PAT permission reference](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens) has no Checks category. The policy therefore does not register Checks permission, Checks REST routes, or `gh pr checks`. Commit statuses above use a different permission and API.

When adding support, update all of the following:

1. Check the operation's official documentation for fine-grained PAT support and required permissions. Determine the method, route, flag side effects, and AND / OR relationships between permissions.
2. Update `CommandClassifier` and, if needed, the permission categories in `PolicyStore`. Evaluate each target separately for operations involving another repository, an organization, or a user.
3. Add classification tests for allowed and denied requests, plus evaluation tests that grant each permission in isolation. Confirm that unknown methods and routes are denied.
4. Update this matrix and the README summary, including official references and conditional permissions.

If GitHub behavior or an API response raises doubts, check the relevant REST documentation and the `X-Accepted-GitHub-Permissions` header. [GitHub's REST troubleshooting guide](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api) shows how the header expresses AND / OR requirements.
