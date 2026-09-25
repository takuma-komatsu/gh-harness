# gh-harness

[日本語](README-ja.md)

`gh-harness` is a .NET 10 wrapper for the GitHub CLI (`gh`). Before running a command, it checks which repository the command targets and whether your policy grants the required permissions. Allowed commands are passed to the real `gh` with their arguments, standard streams, and exit codes intact. It runs as a `dotnet tool` or a `gh` extension on macOS, Windows, and Linux.

## Install as a `gh` extension

Install GitHub CLI, then install the extension from GitHub:

```sh
gh extension install takuma-komatsu/gh-harness
```

The release assets are self-contained executables, so this installation does not need the .NET SDK or runtime. The release workflow builds binaries for Windows, macOS, and Linux on x64 and ARM64.

For a local checkout on macOS or Linux, install the .NET 10 SDK and run:

```sh
gh extension install .
```

Run guarded commands with `gh harness`, passing the usual `gh` arguments after `harness`:

```sh
gh harness --explain pr view -R acme-app/release-tools
gh harness pr view -R acme-app/release-tools
```

The local macOS/Linux extension runs the project from its source checkout with `dotnet run`. Its first invocation builds the project, so the .NET 10 SDK and checkout must remain available. Routine build output is suppressed, but SDK warnings and errors may still appear. Installing the extension does not require installing the `dotnet tool`. `gh harness` guards only commands invoked through that subcommand; other `gh` commands continue to run normally.

## Install as a .NET tool

Install the .NET 10 SDK and GitHub CLI, and make sure both are on your `PATH`. To build and install a local package from this repository:

```sh
dotnet pack src/GhHarness/GhHarness.csproj -c Release -o ./artifacts
dotnet tool install --global gh-harness --version 0.1.1 --add-source ./artifacts
```

To use the wrapper in place of `gh` in an interactive bash or zsh shell, add this to your shell startup file:

```sh
alias gh='gh-harness'
```

With this alias in place, use `command gh harness ...` to invoke the extension.

For PowerShell, add this to `$PROFILE`:

```powershell
function gh { & gh-harness @args }
```

On Windows, `gh.exe harness ...` bypasses this function and invokes the extension.

The wrapper launches the real `gh` from `PATH` by absolute path. A shell alias only affects that shell: it cannot prevent someone from calling the real `gh`, another GitHub API client, or a script directly. Treat `gh-harness` as a command guard, not a security boundary.
If the `gh` found on `PATH` invokes `gh-harness` again, the wrapper stops recursive execution.

For `gh api`, the permission check covers the first URL supplied. If GitHub redirects the request to a different repository and the real `gh api` follows that redirect, the destination is not checked again. See the [limitations in the permission matrix](docs/permission-matrix.md#current-scope-and-adding-support) for details and examples.

## Configure a policy

`gh-harness` loads every `*.gh-harness.json` file directly under `~/.gh-harness/`, in filename order. If the current working tree has a `.gh-harness.json` at its root, that file is applied last, and only when evaluating the repository identified by that tree's Git remote. Each file defines a `targets` array:

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

`access` is either `allow` or `deny`; each permission is `none`, `read`, or `write`. `write` includes `read`. Targets and permissions without an explicit grant are denied. Matching rules are applied in file order, then array order, with later values overriding earlier ones for the same item. A working-tree policy may contain only repository rules for the repository identified by its Git remote. Invalid syntax or unknown configuration values cause the command to be denied. The older `global`, `organizations`, and `repositories` format is not accepted.

Repository patterns match `OWNER/REPO`, organization patterns match an organization login, and account patterns match a user login. Repository and organization patterns support case-insensitive `*`, `?`, `[]`, and `**` wildcards; account patterns require an exact match. Use `"access": "deny"` to explicitly deny a target.

### Fine-grained PAT profiles

Optionally, create `~/.gh-harness/pat-profiles.json` to impose an additional limit based on the SHA-256 fingerprint of the token that `gh` actually uses. If the file is absent, profile checks are skipped. A profile is a declaration: `gh-harness` cannot verify the token's approval, revocation, or effective permissions on GitHub.

```json
{
  "mode": "enforce",
  "profiles": [
    {
      "host": "github.com",
      "tokenSha256": "<64 lowercase hexadecimal characters>",
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

With `"selection": "selected"`, list full `OWNER/REPO` names. With `"selection": "all"`, omit `names`. A profile grants nothing beyond the targets and permissions it declares; a token with no matching profile is denied. Raw tokens and fingerprints are never displayed.

## Commands and permissions

The target is resolved from an explicit GitHub URL first, then `-R`/`--repo`, `GH_REPO`, and finally the current Git repository's remote. This version supports `github.com` only. After checking the target, the wrapper pins `GH_REPO` and `GH_HOST` for the child process. Conflicting explicit targets are denied.

The [permission matrix](docs/permission-matrix.md) documents the checks for every supported command and REST route, with links to official documentation and guidance for adding routes. The [PAT scope design](docs/pat-scope-design.md) covers organization and account permissions, resource owners, and selected repositories for fine-grained PATs.

| Command | Required permission |
| --- | --- |
| `pr list/view/diff` | Pull requests: read |
| `pr create/close/comment/edit/lock/ready/reopen/review/unlock` | Pull requests: write |
| `pr merge` | Contents: write |
| `issue list/view/status` | Issues: read |
| `issue create/edit/close/reopen/comment/delete/lock/unlock/pin/unpin` | Issues: write |
| `issue transfer` | Issues: write on both the source and destination |
| `run list/view/watch/download`, `workflow list/view`, `cache list` | Actions: read |
| `run rerun/cancel/delete`, `workflow run/enable/disable`, `cache delete` | Actions: write |
| `release list/view/download` | Contents: read |
| `release create/edit` | Contents: write + Workflows: write |
| `release delete/delete-asset/upload` | Contents: write |
| `secret list` / `secret set/delete` | Secrets: read / write; with `--env`, Environments: read / write |
| `variable list/get` / `variable set/delete` | Variables: read / write; with `--env`, Environments: read / write |
| `repo clone/read-file/read-dir` | Contents: read |
| `repo sync` | Contents: read on the source; Contents: write on the destination |
| `repo view` | Repository `access: allow` |

`pr create` requires `--head` naming a local branch. `repo sync` requires an explicit destination `OWNER/REPO` and `--source OWNER/REPO`. GraphQL, extensions, aliases defined within `gh`, unclassified commands, and flags with side effects that cannot be determined are denied.

`release create/edit` always requires Workflows: write, because the wrapper cannot determine in advance whether the target commit will require it. Creating release discussions, deleting a tag along with a release, and verifying attestations are unsupported.

`secret` supports Actions secrets for repositories, environments, and organizations; `--app actions` is allowed. `variable` supports variables at the same scopes. Use `--org LOGIN` for an organization target. A GET request to `gh api user/emails` requires `account.emails:read` for the authenticated user. The wrapper verifies the login with `GET /user` and uses the same token when it runs the command. `--user`, other `--app` values, organization visibility and repository selection options, and `--env-file` are unsupported.

### Supported `gh api` REST endpoints

Prefix the paths below with `repos/OWNER/REPO/`. Replace `NUMBER`, `COMMENT_ID`, `REVIEW_ID`, and similar placeholders with actual IDs; `PATH` is a file or directory path. `ID` identifies the resource and may also be a workflow filename such as `build.yml`. Without an explicit method, `gh api` normally uses GET, or POST when `-f`/`-F` (`--raw-field`/`--field`) is present.

| Path | Methods and required permissions |
| --- | --- |
| `pulls` | GET: Pull requests: read / POST: Pull requests: write |
| `pulls/NUMBER` | GET: Pull requests: read / PATCH: Pull requests: write |
| `pulls/NUMBER/merge` | PUT: Contents: write |
| `pulls/comments` | GET: Pull requests: read |
| `pulls/comments/COMMENT_ID` | GET: Pull requests: read / PATCH, DELETE: Pull requests: write |
| `pulls/NUMBER/comments` | GET: Pull requests: read / POST: Pull requests: write |
| `pulls/NUMBER/comments/COMMENT_ID/replies` | POST: Pull requests: write |
| `pulls/NUMBER/reviews/REVIEW_ID/comments` | GET: Pull requests: read |
| `pulls/NUMBER/commits`, `files`, `merge` | GET: Pull requests: read |
| `pulls/NUMBER/requested_reviewers`, `reviews` | GET: Pull requests: read / POST: Pull requests: write; DELETE for `requested_reviewers`: write |
| `pulls/NUMBER/reviews/REVIEW_ID` | GET: Pull requests: read / PUT, DELETE: Pull requests: write |
| `pulls/NUMBER/reviews/REVIEW_ID/events`, `dismissals` | POST (`events`), PUT (`dismissals`): Pull requests: write |
| `pulls/NUMBER/update-branch` | PUT: Pull requests: write |
| `issues` | GET: Issues: read / POST: Issues: write |
| `issues/NUMBER` | GET: Issues: read / PATCH: Issues: write **or** Pull requests: write |
| `issues/NUMBER/comments` | GET: Issues: read **or** Pull requests: read / POST: Issues: write **or** Pull requests: write |
| `issues/comments`, `issues/comments/COMMENT_ID` | GET: Issues: read **or** Pull requests: read; PATCH, DELETE on an individual comment: Issues: write **or** Pull requests: write |
| `issues/NUMBER/labels`, `labels`, `labels/NAME`, `milestones`, `milestones/NUMBER`, `milestones/NUMBER/labels` | See the [permission matrix](docs/permission-matrix.md) for methods; reads require Issues: read **or** Pull requests: read, writes require Issues: write **or** Pull requests: write |
| `commits/REF/status`, `commits/REF/statuses` | GET: Commit statuses: read |
| `statuses/SHA` | POST: Commit statuses: write |
| `actions/workflows`, `actions/workflows/ID` | GET: Actions: read |
| `actions/workflows/ID/dispatches` | POST: Actions: write |
| `actions/runs` | GET: Actions: read |
| `actions/runs/ID` | GET: Actions: read / DELETE: Actions: write |
| `actions/runs/ID/rerun`, `actions/runs/ID/cancel` | POST: Actions: write |
| `actions/runs/ID/approve`, `force-cancel`, `rerun-failed-jobs` | POST: Actions: write |
| `actions/runs/ID/jobs`, `logs`, `artifacts`, `approvals`, `pending_deployments`, `timing`, `attempts/NUMBER`, `attempts/NUMBER/jobs`, `attempts/NUMBER/logs` | GET: Actions: read; DELETE for `logs`: Actions: write |
| `actions/jobs/ID`, `actions/jobs/ID/logs`, `actions/artifacts`, `actions/artifacts/ID`, `actions/artifacts/ID/zip`, `actions/caches`, `actions/cache/usage` | GET: Actions: read; job reruns, artifact deletion, and cache deletion require Actions: write |
| `actions/workflows/ID/runs`, `actions/workflows/ID/timing` | GET: Actions: read |
| `actions/workflows/ID/enable`, `actions/workflows/ID/disable` | PUT: Actions: write |
| `actions/secrets`, `actions/secrets/public-key` | GET: Secrets: read |
| `actions/secrets/NAME` | GET: Secrets: read / PUT, DELETE: Secrets: write |
| `actions/variables` | GET: Variables: read / POST: Variables: write |
| `actions/variables/NAME` | GET: Variables: read / PATCH, DELETE: Variables: write |
| `environments/NAME/secrets`, `environments/NAME/secrets/public-key` | GET: Environments: read |
| `environments/NAME/secrets/SECRET_NAME` | GET: Environments: read / PUT, DELETE: Environments: write |
| `environments/NAME/variables` | GET: Environments: read / POST: Environments: write |
| `environments/NAME/variables/VARIABLE_NAME` | GET: Environments: read / PATCH, DELETE: Environments: write |
| `contents` | GET: Contents: read |
| `contents/PATH` | GET: Contents: read / PUT, DELETE: Contents: write; PUT, DELETE under `.github/workflows/` also require Workflows: write |
| `releases` | GET: Contents: read / POST: Contents: write + Workflows: write |
| `releases/latest`, `releases/tags/TAG` | GET: Contents: read |
| `releases/ID` | GET: Contents: read / PATCH: Contents: write + Workflows: write / DELETE: Contents: write |
| `releases/ID/assets`, `releases/assets/ASSET_ID` | GET: Contents: read; PATCH, DELETE on an individual asset: Contents: write |
| `releases/generate-notes` | POST: Contents: write |
| `deployments`, `deployments/ID`, `deployments/ID/statuses`, `actions/runs/ID/pending_deployments` | Deployments: read / write, depending on method |
| `pages`, `pages/builds`, `pages/deployments` | Pages: read / write, depending on method; POST, PUT, DELETE on `pages` and GET on `pages/health` also require Administration: write |
| `hooks`, `hooks/ID`, `hooks/ID/config`, `hooks/ID/deliveries` | Webhooks: read / write, depending on method; POST to `hooks/ID/pings` or `tests` requires read |
| `dependabot/alerts`, `dependabot/alerts/NUMBER` | GET: Dependabot alerts: read / PATCH on an individual alert: write |
| `code-scanning/alerts`, `analyses`, `sarifs` | Code scanning alerts: read / write, depending on method |
| `secret-scanning/alerts`, `scan-history` | Secret scanning alerts: read / write, depending on method |
| `security-advisories`, `security-advisories/GHSA_ID` | Repository security advisories: read / write, depending on method; POST to create a temporary fork requires read plus Administration: write |

For an **or** entry, either permission satisfies that check for the same repository. Permissions for separate operations or repositories are checked independently. REST path and method combinations absent from the table are denied.

Commit statuses and Checks are separate APIs. [GitHub's PAT documentation](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens) says the Checks API does not support fine-grained PATs, and [GitHub's fine-grained PAT permission list](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens) has no Checks permission. Accordingly, Checks REST routes and `gh pr checks` are outside the supported scope. See the [permission matrix](docs/permission-matrix.md) for details.

Use `--explain` to inspect a decision without launching the real `gh`:

```sh
gh-harness --explain pr view -R acme-app/release-tools
```

## Develop

```sh
dotnet test tests/GhHarness.Tests/GhHarness.Tests.csproj
dotnet pack src/GhHarness/GhHarness.csproj -c Release -o ./artifacts
```
