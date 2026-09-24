# Organization and Account Permissions in Fine-Grained PATs

[日本語](pat-scope-design-ja.md)

## Scope and boundaries

`TargetRequirement` represents repositories, organizations, and accounts as distinct target types. `Program` groups requirements by target and passes each group to `PolicyStore.Evaluate(target, needs)`. Only routes registered by a classifier are allowed, and every target is evaluated before `gh` starts. A working tree's `.gh-harness.json` applies only when evaluating the repository identified by that tree's Git remote.

A GitHub fine-grained PAT is limited to one resource owner (a user or organization), a repository selection, and a set of permissions. Account permissions can be set only when the user is the resource owner; organization permissions can be set only when an organization is the resource owner. Organization approval, the user's own access, token expiration, and other factors also affect whether an API call succeeds. See GitHub's documentation on [creating and limiting PATs](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens) and its [REST permission reference](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens).

The [permission matrix](permission-matrix.md) lists supported routes.

## Typed targets and permissions

Internally, a target is `TargetId(Host, Kind, Name)`. For now, `Host` is limited to `github.com`. `Kind` has the following three values; it must never be inferred from a name alone.

| Kind | Name | Permission namespace | Example |
| --- | --- | --- | --- |
| `repository` | `OWNER/REPO` | `repository` | `repository.secrets:write` on `acme/project` |
| `organization` | Organization login | `organization` | `organization.secrets:write` on `acme` |
| `account` | User login | `account` | `account.emails:read` on `alice` |

A permission consists of `PermissionKey(Namespace, Name)` and a `Level`. Names such as `secrets` and `webhooks` must not be shared across namespaces. Define valid levels per permission from the official documentation, with room for a future `admin` level. Existing repository permissions retain `none`, `read`, and `write`. `access: allow` remains a policy decision separate from PAT permissions.

The classifier produces a sequence of `Requirement(TargetId, PermissionKey, Level, AlternativeGroup?)` values. Map the existing `TargetRequirement(Repository, Category, Level, AlternativeGroup)` to a `repository` target and the `repository` namespace. Express AND conditions as separate requirements and OR conditions as a group **within the same target**. A route that affects multiple targets must emit requirements for each one. Check composite conditions listed as “additional permissions” in GitHub's documentation for each route and method; prefixes such as `orgs/` and `user/` do not determine the required permission. The [REST permission reference](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens) includes routes under `/orgs/` that require organization permissions and others that require repository permissions.

## Policy JSON

Accept only the following policy shape. There is no need for a `schemaVersion` or other version field, and the old `global`, `organizations`, and `repositories` format is not accepted.

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

Each rule's `target.kind` must match the namespace in `permissions`. Account names require an exact match; wildcards spanning accounts are not allowed. Organization and repository patterns use case-insensitive glob matching. Reject unknown kinds, namespaces, permission names, and levels. Apply matching rules in filename order and then array order, with later values overriding earlier values for the same item.

A local `.gh-harness.json` applies **only to repository rules for the one repository identified by its Git remote**. Organization and account rules, along with PAT profiles, can be defined only in the home configuration. Their presence in a local file is a configuration error. This prevents a working tree's configuration from changing permissions for an entire organization, a user account, or another repository.

## Optional PAT profiles as an upper bound

A policy's `allow` does not establish what a token can do. Users who want an additional limit can define a home-only `~/.gh-harness/pat-profiles.json` file.

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
        "repository": { "secrets": "read" },
        "organization": { "secrets": "write" }
      }
    }
  ]
}
```

`repositoryAccess.selection` is either `all` or `selected`. A `selected` profile lists full `OWNER/REPO` names explicitly; names with a different owner are rejected. The `account` namespace is valid only for a profile whose `resourceOwner.kind == account` and whose login matches. Permissions and targets absent from a profile count as `none`. Even if a policy allows a repository, organization, or account target, a request exceeding the profile's upper bound is denied. Exceptional read access a token may have to public repositories does not loosen this bound automatically.

When the file is absent, profile checks are skipped, as they are today. When it exists with `mode: enforce`, a request is denied unless a profile matches **the credentials `gh` will actually use**. `GH_TOKEN` takes precedence over `GITHUB_TOKEN` and stored credentials, so the active account name alone cannot select a profile. Following the [GitHub CLI environment variable rules](https://cli.github.com/manual/gh_help_environment), resolve the effective token with `gh auth token --hostname github.com` or an equivalent method, compute its SHA-256 hash in the process, and compare it with profile fingerprints. Never write raw tokens, fingerprints, or `gh auth token` output to logs or traces. Reject multiple matching profiles. Local JSON cannot verify a profile's claims, revocation or approval status, or the token's actual GitHub permissions. A profile is therefore a **declared upper bound**, not a guarantee that an API call will succeed. When an organization requires approval, a pending PAT is limited to reading public resources. See GitHub's [PAT approval requirements](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens).

## Binding account identity and multiple targets

Account-scoped `/user/...` routes have no login in the URL. At runtime, call `GET /user` with the same effective credentials to obtain the authenticated login and establish `TargetId(account, login)`. Even if a route names a login explicitly, an operation using the caller's account permissions must match the authenticated login. Deny the request if identity lookup fails, credentials change, or the login differs from the profile's `account`. Do not infer the account login from `GH_REPO`, a Git remote, or an organization owner. This follows the [GitHub CLI credential precedence rules](https://cli.github.com/manual/gh_help_environment) and the [restriction of account permissions to the resource owner](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens).

The classifier identifies every target affected by a command and evaluates each target's access, permissions, and profile limit. For example, `repo sync` has distinct source and destination repositories; setting an organization secret's repository access involves the organization and any required repositories. If the targets belong to different resource owners, one fine-grained PAT profile cannot cover them all, so deny the request. Do not allow flags with indeterminate effects, target IDs embedded in a request body, or dynamically chosen targets. Register operations incrementally only when their targets can be enumerated. Verify all targets before launching `gh` once; never authorize only part of an operation. `--explain` shows requirements and denial reasons for each target without displaying credentials.

## Implementation and acceptance

1. Introduce `TargetId` and namespaced `PermissionKey`, and migrate existing repository requirements. Verify commands that operate on multiple repositories and target-specific `--explain` output.
2. Introduce the new `targets` JSON syntax and per-target evaluation. Test that local configuration is restricted to repositories, identically named repository and organization permissions remain independent, and unknown values and account wildcards are rejected.
3. Bind account login to the effective credentials. Test environment token precedence, stored credentials, authentication failure, and credential changes. Fail closed when a network identity check fails.
4. Introduce the optional profile limit. Test resource owners, selected repositories, levels within each namespace, multiple owners, and mismatched profiles. Verify that local configuration cannot define or disable profiles.
5. Add specific `gh` operations and REST methods after checking the official endpoint documentation. Current support covers some organization Actions Secrets and Variables routes, the corresponding `gh secret --org` and `gh variable --org` commands, and the caller's `GET /user/emails`. Record route-specific conditions in the [permission matrix](permission-matrix.md).

GitHub lists further fine-grained PAT limitations, including simultaneous access to multiple organizations, access as an outside collaborator, Packages, and the Checks API. Adding target kinds alone cannot address these limitations. Continue to deny unsupported routes in accordance with GitHub's [PAT limitations](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens).
