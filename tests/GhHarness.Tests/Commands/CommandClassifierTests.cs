using System.Diagnostics;
using GhHarness.Commands;
using GhHarness.Policy;
using Xunit;

namespace GhHarness.Tests.Commands;

public sealed class CommandClassifierTests
{
    private static readonly IReadOnlyDictionary<string, string?> EmptyEnvironment = new Dictionary<string, string?>();

    private static TargetRequirement RepoRequirement(string repository, string category, string level, string? alternativeGroup = null) =>
        new(new TargetId("github.com", "repository", repository), new PermissionKey("repository", category), level, alternativeGroup);

    [Theory]
    [InlineData("pr", "view", "pullRequests", "read")]
    [InlineData("pr", "create", "pullRequests", "write")]
    [InlineData("pr", "merge", "contents", "write")]
    [InlineData("issue", "list", "issues", "read")]
    [InlineData("issue", "close", "issues", "write")]
    [InlineData("workflow", "run", "actions", "write")]
    [InlineData("run", "view", "actions", "read")]
    [InlineData("repo", "clone", "contents", "read")]
    [InlineData("repo", "view", "access", "allow")]
    public void StandardCommandsHaveRequiredPermission(string command, string subcommand, string category, string level)
    {
        var args = new List<string> { command, subcommand, "-R", "acme/service" };
        if (command == "pr" && subcommand == "create")
            args.AddRange(["--head", "feature"]);
        if (command == "repo" && subcommand == "clone")
            args.Add("acme/service");

        var result = Classify(args.ToArray());

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Contains(RepoRequirement("acme/service", category, level), result.Requirements);
        Assert.Equal("acme/service", result.ChildEnvironmentOverrides["GH_REPO"]);
        Assert.Equal("github.com", result.ChildEnvironmentOverrides["GH_HOST"]);
    }

    [Theory]
    [InlineData("secret", "list", null, "secrets", "read")]
    [InlineData("secret", "set", "TOKEN", "secrets", "write")]
    [InlineData("secret", "delete", "TOKEN", "secrets", "write")]
    [InlineData("variable", "list", null, "variables", "read")]
    [InlineData("variable", "get", "MODE", "variables", "read")]
    [InlineData("variable", "set", "MODE", "variables", "write")]
    [InlineData("variable", "delete", "MODE", "variables", "write")]
    public void RepositorySecretAndVariableCommandsRequireIndependentPermissions(string command, string subcommand, string? name, string category, string level)
    {
        var args = new List<string> { command, subcommand, "-R", "acme/repo" };
        if (name is not null) args.Add(name);

        var result = Classify(args.ToArray());

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal(new[] { RepoRequirement("acme/repo", category, level) }, result.Requirements);
    }

    [Theory]
    [InlineData("secret", "list", null, "read")]
    [InlineData("secret", "set", "TOKEN", "write")]
    [InlineData("secret", "delete", "TOKEN", "write")]
    [InlineData("variable", "list", null, "read")]
    [InlineData("variable", "get", "MODE", "read")]
    [InlineData("variable", "set", "MODE", "write")]
    [InlineData("variable", "delete", "MODE", "write")]
    public void EnvironmentSecretAndVariableCommandsRequireEnvironmentsPermission(string command, string subcommand, string? name, string level)
    {
        var args = new List<string> { command, subcommand, "-R", "acme/repo", "--env", "production" };
        if (name is not null) args.Add(name);

        var result = Classify(args.ToArray());

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal(new[] { RepoRequirement("acme/repo", "environments", level) }, result.Requirements);
    }

    [Theory]
    [InlineData("secret", "list", null, "secrets", "read")]
    [InlineData("secret", "set", "TOKEN", "secrets", "write")]
    [InlineData("secret", "delete", "TOKEN", "secrets", "write")]
    [InlineData("variable", "list", null, "variables", "read")]
    [InlineData("variable", "get", "MODE", "variables", "read")]
    [InlineData("variable", "set", "MODE", "variables", "write")]
    [InlineData("variable", "delete", "MODE", "variables", "write")]
    public void OrganizationSecretAndVariableCommandsHaveTypedRequirements(string command, string subcommand, string? name, string category, string level)
    {
        var args = new List<string> { command, subcommand, "--org", "acme" };
        if (name is not null) args.Add(name);
        var result = CommandClassifier.Classify([.. args], Path.GetTempPath(), EmptyEnvironment);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal(new[] { new TargetRequirement(new TargetId("github.com", "organization", "acme"), new PermissionKey("organization", category), level) }, result.Requirements);
        Assert.Null(result.PrimaryRepository);
        Assert.Null(result.ChildEnvironmentOverrides["GH_REPO"]);
    }

    [Theory]
    [InlineData("secret", "set", "TOKEN", "--org", "acme", "--env", "production")]
    [InlineData("secret", "set", "TOKEN", "--org", "acme", "-R", "acme/repo")]
    [InlineData("secret", "set", "TOKEN", "--org", "acme", "--org", "other")]
    [InlineData("variable", "set", "MODE", "--org", "acme", "--repos", "repo")]
    [InlineData("variable", "set", "MODE", "--org", "acme", "--visibility", "selected")]
    public void AmbiguousOrganizationSecretAndVariableCommandsAreDenied(params string[] args)
    {
        Assert.False(Classify(args).AllowedToEvaluate);
    }

    [Theory]
    [InlineData("GET", "orgs/acme/actions/secrets", "secrets", "read")]
    [InlineData("GET", "orgs/acme/actions/secrets/public-key", "secrets", "read")]
    [InlineData("GET", "orgs/acme/actions/secrets/TOKEN", "secrets", "read")]
    [InlineData("DELETE", "orgs/acme/actions/secrets/TOKEN", "secrets", "write")]
    [InlineData("GET", "orgs/acme/actions/variables", "variables", "read")]
    [InlineData("GET", "orgs/acme/actions/variables/MODE", "variables", "read")]
    [InlineData("DELETE", "orgs/acme/actions/variables/MODE", "variables", "write")]
    public void OrganizationActionsRestRoutesHaveTypedRequirements(string method, string endpoint, string category, string level)
    {
        var result = CommandClassifier.Classify(["api", "-X", method, endpoint], Path.GetTempPath(), EmptyEnvironment);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal(new[] { new TargetRequirement(new TargetId("github.com", "organization", "acme"), new PermissionKey("organization", category), level) }, result.Requirements);
    }

    [Fact]
    public void AccountEmailRestRouteRequiresAuthenticatedAccount()
    {
        var result = CommandClassifier.Classify(["api", "user/emails"], Path.GetTempPath(), EmptyEnvironment);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal(new[] { new TargetRequirement(new TargetId("github.com", "account", "@authenticated"), new PermissionKey("account", "emails"), "read") }, result.Requirements);
    }

    [Theory]
    [InlineData("POST", "orgs/acme/actions/variables")]
    [InlineData("PUT", "orgs/acme/actions/secrets/TOKEN")]
    [InlineData("DELETE", "orgs/acme/actions/secrets/public-key")]
    [InlineData("GET", "orgs/acme/actions/variables/MODE/repositories")]
    [InlineData("PATCH", "user/emails")]
    public void UnclassifiedOrganizationAndAccountRestRoutesAreDenied(string method, string endpoint)
    {
        Assert.False(CommandClassifier.Classify(["api", "-X", method, endpoint], Path.GetTempPath(), EmptyEnvironment).AllowedToEvaluate);
    }

    [Fact]
    public void ActionsSecretAppAndSupportedFormattingFlagsAreAccepted()
    {
        Assert.True(Classify(["secret", "set", "TOKEN", "--app", "actions", "--body", "value", "-R", "acme/repo"]).AllowedToEvaluate);
        Assert.True(Classify(["secret", "list", "-a", "actions", "--json", "name", "-R", "acme/repo"]).AllowedToEvaluate);
        Assert.True(Classify(["variable", "get", "MODE", "--env=production", "--jq", ".value", "-R", "acme/repo"]).AllowedToEvaluate);
    }

    [Theory]
    [InlineData("secret", "set", "TOKEN", "--user")]
    [InlineData("secret", "list", "--user")]
    [InlineData("secret", "list", "-u")]
    [InlineData("secret", "set", "TOKEN", "--app", "dependabot")]
    [InlineData("secret", "set", "TOKEN", "--app", "codespaces")]
    [InlineData("secret", "set", "TOKEN", "--visibility", "all")]
    [InlineData("secret", "set", "TOKEN", "--repos", "another/repo")]
    [InlineData("secret", "set", "TOKEN", "--no-store")]
    [InlineData("secret", "set", "TOKEN", "--env-file", ".env")]
    [InlineData("secret", "set", "TOKEN", "--env", "--org")]
    [InlineData("variable", "set", "MODE", "--visibility", "all")]
    [InlineData("variable", "set", "MODE", "--repos", "another/repo")]
    [InlineData("variable", "set", "MODE", "--env-file", ".env")]
    [InlineData("variable", "get", "MODE", "--app", "actions")]
    [InlineData("variable", "list", "MODE")]
    [InlineData("variable", "get", "MODE", "EXTRA")]
    [InlineData("secret", "delete", "TOKEN", "EXTRA")]
    [InlineData("secret", "get", "TOKEN")]
    public void UnsupportedSecretAndVariableTargetsAndShapesAreDenied(params string[] args)
    {
        Assert.False(Classify([.. args, "-R", "acme/repo"]).AllowedToEvaluate);
    }

    [Fact]
    public void ExplicitRepositoryOverridesEnvironment()
    {
        var result = Classify(["pr", "list", "-R", "acme/explicit"], new Dictionary<string, string?> { ["GH_REPO"] = "acme/from-env" });

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal("acme/explicit", result.PrimaryRepository);
    }

    [Theory]
    [InlineData("github.com/acme/repo")]
    [InlineData("https://github.com/acme/repo")]
    public void GithubHostQualifiedRepositoryNormalizes(string input)
    {
        var result = Classify(["pr", "list", "-R", input]);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal("acme/repo", result.PrimaryRepository);
    }

    [Fact]
    public void EnterpriseHostQualifiedRepositoryIsDenied()
    {
        Assert.False(Classify(["pr", "list", "-R", "git.example/acme/repo"]).AllowedToEvaluate);
    }

    [Fact]
    public void ConflictingExplicitTargetsAreDenied()
    {
        var result = Classify(["pr", "view", "https://github.com/acme/one/pull/1", "-R", "acme/two"]);

        Assert.False(result.AllowedToEvaluate);
        Assert.Contains("Conflicting", result.Error);
    }

    [Fact]
    public void UrlInBodyIsNotInterpretedAsTarget()
    {
        var result = Classify(["pr", "create", "--head", "feature", "--body", "https://github.com/other/repo", "-R", "acme/repo"]);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal("acme/repo", result.PrimaryRepository);
    }

    [Fact]
    public void FlagLookingBodyValueIsNotTreatedAsHeadFlag()
    {
        var result = Classify(["pr", "create", "--body", "--head", "feature", "-R", "acme/repo"]);

        Assert.False(result.AllowedToEvaluate);
    }

    [Fact]
    public void RepoReadFileUsesRepoFlagNotPathAsTarget()
    {
        var result = Classify(["repo", "read-file", "owner/file.txt", "-R", "acme/repo"]);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal("acme/repo", result.PrimaryRepository);
    }

    [Fact]
    public void RepoCloneRequiresExplicitFullRepositoryAndNoPassThrough()
    {
        Assert.False(Classify(["repo", "clone", "short-name"], new Dictionary<string, string?> { ["GH_REPO"] = "acme/repo" }).AllowedToEvaluate);
        Assert.False(Classify(["repo", "clone", "acme/repo", "--", "-c", "core.hooksPath=/tmp/x"]).AllowedToEvaluate);
        Assert.False(Classify(["repo", "view", "short-name"], new Dictionary<string, string?> { ["GH_REPO"] = "acme/repo" }).AllowedToEvaluate);
        Assert.False(Classify(["issue", "edit", "1", "2", "-R", "acme/repo"]).AllowedToEvaluate);
    }

    [Fact]
    public void NonGithubHostIsDenied()
    {
        var result = Classify(["pr", "list", "-R", "acme/repo"], new Dictionary<string, string?> { ["GH_HOST"] = "github.enterprise.test" });

        Assert.False(result.AllowedToEvaluate);
    }

    [Theory]
    [InlineData("pr", "create", "-R", "acme/repo")]
    [InlineData("pr", "create", "-R", "acme/repo", "--head", "other:feature")]
    [InlineData("pr", "merge", "-R", "acme/repo", "--delete-branch")]
    [InlineData("pr", "close", "-R", "acme/repo", "-d")]
    [InlineData("pr", "checks", "-R", "acme/repo")]
    [InlineData("pr", "status", "-R", "acme/repo")]
    [InlineData("pr", "create", "-R", "acme/repo", "--head", "feature", "--attach", "file.txt")]
    [InlineData("pr", "close", "-R", "acme/repo", "--comment", "https://github.com/other/repo")]
    [InlineData("pr", "merge", "-m", "https://github.com/denied/repo/pull/1", "-R", "acme/repo")]
    [InlineData("pr", "merge", "-s", "https://github.com/denied/repo/pull/1", "-R", "acme/repo")]
    [InlineData("pr", "merge", "-r", "https://github.com/denied/repo/pull/1", "-R", "acme/repo")]
    [InlineData("pr", "merge", "-a", "https://github.com/denied/repo/pull/1", "-R", "acme/repo")]
    [InlineData("pr", "view", "-R", "acme/repo", "--json", "statusCheckRollup")]
    [InlineData("issue", "view", "-R", "acme/repo", "--json", "projectItems")]
    [InlineData("pr", "checkout", "-R", "acme/repo")]
    [InlineData("extension", "install", "owner/name")]
    public void UnsupportedOrUnsafeCommandsAreDenied(params string[] args)
    {
        Assert.False(Classify(args).AllowedToEvaluate);
    }

    [Fact]
    public void TransferRequiresBothRepositories()
    {
        var result = Classify(["issue", "transfer", "42", "acme/destination", "-R", "acme/source"]);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Contains(RepoRequirement("acme/source", "issues", "write"), result.Requirements);
        Assert.Contains(RepoRequirement("acme/destination", "issues", "write"), result.Requirements);
    }

    [Fact]
    public void RepoSyncRequiresExplicitSourceAndDestination()
    {
        var result = Classify(["repo", "sync", "acme/destination", "--source", "acme/source"]);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Contains(RepoRequirement("acme/source", "contents", "read"), result.Requirements);
        Assert.Contains(RepoRequirement("acme/destination", "contents", "write"), result.Requirements);
        Assert.False(Classify(["repo", "sync", "acme/destination"]).AllowedToEvaluate);
    }

    [Theory]
    [InlineData("GET", "repos/acme/repo/pulls", "pullRequests", "read")]
    [InlineData("POST", "repos/acme/repo/issues", "issues", "write")]
    [InlineData("PUT", "repos/acme/repo/pulls/12/merge", "contents", "write")]
    [InlineData("POST", "repos/acme/repo/actions/workflows/build.yml/dispatches", "actions", "write")]
    [InlineData("GET", "repos/acme/repo/contents/src/file.txt", "contents", "read")]
    [InlineData("GET", "repos/acme/repo/contents", "contents", "read")]
    [InlineData("GET", "repos/acme/repo/pulls/comments", "pullRequests", "read")]
    [InlineData("GET", "repos/acme/repo/pulls/comments/456", "pullRequests", "read")]
    [InlineData("PATCH", "repos/acme/repo/pulls/comments/456", "pullRequests", "write")]
    [InlineData("DELETE", "repos/acme/repo/pulls/comments/456", "pullRequests", "write")]
    [InlineData("GET", "repos/acme/repo/pulls/12/comments", "pullRequests", "read")]
    [InlineData("POST", "repos/acme/repo/pulls/12/comments", "pullRequests", "write")]
    [InlineData("POST", "repos/acme/repo/pulls/12/comments/456/replies", "pullRequests", "write")]
    [InlineData("GET", "repos/acme/repo/pulls/12/reviews/34/comments", "pullRequests", "read")]
    [InlineData("GET", "repos/acme/repo/pulls/12/commits", "pullRequests", "read")]
    [InlineData("GET", "repos/acme/repo/pulls/12/files", "pullRequests", "read")]
    [InlineData("GET", "repos/acme/repo/pulls/12/merge", "pullRequests", "read")]
    [InlineData("GET", "repos/acme/repo/pulls/12/requested_reviewers", "pullRequests", "read")]
    [InlineData("POST", "repos/acme/repo/pulls/12/requested_reviewers", "pullRequests", "write")]
    [InlineData("DELETE", "repos/acme/repo/pulls/12/requested_reviewers", "pullRequests", "write")]
    [InlineData("GET", "repos/acme/repo/pulls/12/reviews", "pullRequests", "read")]
    [InlineData("POST", "repos/acme/repo/pulls/12/reviews", "pullRequests", "write")]
    [InlineData("GET", "repos/acme/repo/pulls/12/reviews/34", "pullRequests", "read")]
    [InlineData("PUT", "repos/acme/repo/pulls/12/reviews/34", "pullRequests", "write")]
    [InlineData("DELETE", "repos/acme/repo/pulls/12/reviews/34", "pullRequests", "write")]
    [InlineData("POST", "repos/acme/repo/pulls/12/reviews/34/events", "pullRequests", "write")]
    [InlineData("PUT", "repos/acme/repo/pulls/12/reviews/34/dismissals", "pullRequests", "write")]
    [InlineData("PUT", "repos/acme/repo/pulls/12/update-branch", "pullRequests", "write")]
    [InlineData("GET", "repos/acme/repo/actions/jobs/45", "actions", "read")]
    [InlineData("GET", "repos/acme/repo/actions/jobs/45/logs", "actions", "read")]
    [InlineData("POST", "repos/acme/repo/actions/jobs/45/rerun", "actions", "write")]
    [InlineData("GET", "repos/acme/repo/actions/artifacts", "actions", "read")]
    [InlineData("GET", "repos/acme/repo/actions/artifacts/45/zip", "actions", "read")]
    [InlineData("DELETE", "repos/acme/repo/actions/artifacts/45", "actions", "write")]
    [InlineData("GET", "repos/acme/repo/actions/caches", "actions", "read")]
    [InlineData("DELETE", "repos/acme/repo/actions/caches", "actions", "write")]
    [InlineData("DELETE", "repos/acme/repo/actions/caches/45", "actions", "write")]
    [InlineData("GET", "repos/acme/repo/actions/cache/usage", "actions", "read")]
    [InlineData("GET", "repos/acme/repo/actions/runs/45/jobs", "actions", "read")]
    [InlineData("GET", "repos/acme/repo/actions/runs/45/logs", "actions", "read")]
    [InlineData("DELETE", "repos/acme/repo/actions/runs/45/logs", "actions", "write")]
    [InlineData("GET", "repos/acme/repo/actions/runs/45/artifacts", "actions", "read")]
    [InlineData("GET", "repos/acme/repo/actions/runs/45/attempts/2/jobs", "actions", "read")]
    [InlineData("GET", "repos/acme/repo/actions/runs/45/attempts/2/logs", "actions", "read")]
    [InlineData("GET", "repos/acme/repo/actions/runs/45/attempts/2", "actions", "read")]
    [InlineData("POST", "repos/acme/repo/actions/runs/45/approve", "actions", "write")]
    [InlineData("POST", "repos/acme/repo/actions/runs/45/rerun-failed-jobs", "actions", "write")]
    [InlineData("POST", "repos/acme/repo/actions/runs/45/force-cancel", "actions", "write")]
    [InlineData("PUT", "repos/acme/repo/actions/workflows/build.yml/enable", "actions", "write")]
    [InlineData("PUT", "repos/acme/repo/actions/workflows/build.yml/disable", "actions", "write")]
    [InlineData("GET", "repos/acme/repo/actions/workflows/build.yml/runs", "actions", "read")]
    [InlineData("GET", "repos/acme/repo/actions/workflows/build.yml/timing", "actions", "read")]
    [InlineData("GET", "repos/acme/repo/commits/main/status", "commitStatuses", "read")]
    [InlineData("GET", "repos/acme/repo/commits/abc123/statuses", "commitStatuses", "read")]
    [InlineData("POST", "repos/acme/repo/statuses/abc123", "commitStatuses", "write")]
    [InlineData("GET", "repos/acme/repo/actions/secrets", "secrets", "read")]
    [InlineData("GET", "repos/acme/repo/actions/secrets/public-key", "secrets", "read")]
    [InlineData("GET", "repos/acme/repo/actions/secrets/TOKEN", "secrets", "read")]
    [InlineData("PUT", "repos/acme/repo/actions/secrets/TOKEN", "secrets", "write")]
    [InlineData("DELETE", "repos/acme/repo/actions/secrets/TOKEN", "secrets", "write")]
    [InlineData("GET", "repos/acme/repo/actions/variables", "variables", "read")]
    [InlineData("POST", "repos/acme/repo/actions/variables", "variables", "write")]
    [InlineData("GET", "repos/acme/repo/actions/variables/MODE", "variables", "read")]
    [InlineData("PATCH", "repos/acme/repo/actions/variables/MODE", "variables", "write")]
    [InlineData("DELETE", "repos/acme/repo/actions/variables/MODE", "variables", "write")]
    [InlineData("GET", "repos/acme/repo/environments/production/secrets", "environments", "read")]
    [InlineData("GET", "repos/acme/repo/environments/production/secrets/public-key", "environments", "read")]
    [InlineData("GET", "repos/acme/repo/environments/production/secrets/TOKEN", "environments", "read")]
    [InlineData("PUT", "repos/acme/repo/environments/production/secrets/TOKEN", "environments", "write")]
    [InlineData("DELETE", "repos/acme/repo/environments/production/secrets/TOKEN", "environments", "write")]
    [InlineData("GET", "repos/acme/repo/environments/production/variables", "environments", "read")]
    [InlineData("POST", "repos/acme/repo/environments/production/variables", "environments", "write")]
    [InlineData("GET", "repos/acme/repo/environments/production/variables/MODE", "environments", "read")]
    [InlineData("PATCH", "repos/acme/repo/environments/production/variables/MODE", "environments", "write")]
    [InlineData("DELETE", "repos/acme/repo/environments/production/variables/MODE", "environments", "write")]
    public void ApiRestTableClassifiesMethodAndPath(string method, string endpoint, string category, string level)
    {
        var result = Classify(["api", "-X", method, endpoint]);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Contains(RepoRequirement("acme/repo", category, level), result.Requirements);
    }

    [Theory]
    [InlineData("GET", "deployments", "deployments", "read")]
    [InlineData("POST", "deployments", "deployments", "write")]
    [InlineData("GET", "deployments/42", "deployments", "read")]
    [InlineData("DELETE", "deployments/42", "deployments", "write")]
    [InlineData("POST", "deployments/42/statuses", "deployments", "write")]
    [InlineData("GET", "deployments/42/statuses/7", "deployments", "read")]
    [InlineData("POST", "actions/runs/42/pending_deployments", "deployments", "write")]
    [InlineData("GET", "pages", "pages", "read")]
    [InlineData("POST", "pages/builds", "pages", "write")]
    [InlineData("GET", "pages/builds/latest", "pages", "read")]
    [InlineData("GET", "pages/builds/42", "pages", "read")]
    [InlineData("POST", "pages/deployments", "pages", "write")]
    [InlineData("GET", "pages/deployments/deploy-abc", "pages", "read")]
    [InlineData("POST", "pages/deployments/deploy-abc/cancel", "pages", "write")]
    [InlineData("GET", "hooks", "webhooks", "read")]
    [InlineData("POST", "hooks", "webhooks", "write")]
    [InlineData("PATCH", "hooks/42", "webhooks", "write")]
    [InlineData("DELETE", "hooks/42", "webhooks", "write")]
    [InlineData("GET", "hooks/42/config", "webhooks", "read")]
    [InlineData("PATCH", "hooks/42/config", "webhooks", "write")]
    [InlineData("GET", "hooks/42/deliveries", "webhooks", "read")]
    [InlineData("GET", "hooks/42/deliveries/7", "webhooks", "read")]
    [InlineData("POST", "hooks/42/deliveries/7/attempts", "webhooks", "write")]
    [InlineData("POST", "hooks/42/pings", "webhooks", "read")]
    [InlineData("POST", "hooks/42/tests", "webhooks", "read")]
    [InlineData("GET", "dependabot/alerts", "dependabotAlerts", "read")]
    [InlineData("GET", "dependabot/alerts/42", "dependabotAlerts", "read")]
    [InlineData("PATCH", "dependabot/alerts/42", "dependabotAlerts", "write")]
    [InlineData("GET", "code-scanning/alerts", "codeScanningAlerts", "read")]
    [InlineData("PATCH", "code-scanning/alerts/42", "codeScanningAlerts", "write")]
    [InlineData("GET", "code-scanning/alerts/42/instances", "codeScanningAlerts", "read")]
    [InlineData("POST", "code-scanning/alerts/42/autofix", "codeScanningAlerts", "write")]
    [InlineData("GET", "code-scanning/analyses/42", "codeScanningAlerts", "read")]
    [InlineData("DELETE", "code-scanning/analyses/42", "codeScanningAlerts", "write")]
    [InlineData("POST", "code-scanning/sarifs", "codeScanningAlerts", "write")]
    [InlineData("GET", "code-scanning/sarifs/abc", "codeScanningAlerts", "read")]
    [InlineData("GET", "secret-scanning/alerts", "secretScanningAlerts", "read")]
    [InlineData("PATCH", "secret-scanning/alerts/42", "secretScanningAlerts", "write")]
    [InlineData("GET", "secret-scanning/alerts/42/locations", "secretScanningAlerts", "read")]
    [InlineData("GET", "secret-scanning/scan-history", "secretScanningAlerts", "read")]
    [InlineData("GET", "security-advisories", "repositorySecurityAdvisories", "read")]
    [InlineData("POST", "security-advisories", "repositorySecurityAdvisories", "write")]
    [InlineData("POST", "security-advisories/reports", "repositorySecurityAdvisories", "write")]
    [InlineData("PATCH", "security-advisories/GHSA-abcd-1234-efgh", "repositorySecurityAdvisories", "write")]
    [InlineData("POST", "security-advisories/GHSA-abcd-1234-efgh/cve", "repositorySecurityAdvisories", "write")]
    public void AdditionalRepositoryRestFamiliesHaveExactPermissions(string method, string path, string category, string level)
    {
        var result = Classify(["api", "-X", method, $"repos/acme/repo/{path}"]);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal(new[] { RepoRequirement("acme/repo", category, level) }, result.Requirements);
    }

    [Theory]
    [InlineData("POST", "pages", "pages", "write")]
    [InlineData("PUT", "pages", "pages", "write")]
    [InlineData("DELETE", "pages", "pages", "write")]
    [InlineData("GET", "pages/health", "pages", "write")]
    [InlineData("POST", "security-advisories/GHSA-abcd-1234-efgh/forks", "repositorySecurityAdvisories", "read")]
    public void AdditionalRestFamiliesRequireAdministrationForCompoundRoutes(string method, string path, string category, string level)
    {
        var result = Classify(["api", "-X", method, $"repos/acme/repo/{path}"]);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal(new[]
        {
            RepoRequirement("acme/repo", category, level),
            RepoRequirement("acme/repo", "administration", "write")
        }, result.Requirements);
    }

    [Theory]
    [InlineData("PATCH", "deployments/42/statuses")]
    [InlineData("GET", "deployments/42/statuses/7/extra")]
    [InlineData("GET", "pages/deployments")]
    [InlineData("GET", "pages/health/extra")]
    [InlineData("POST", "hooks/42/deliveries")]
    [InlineData("GET", "hooks/42/pings")]
    [InlineData("POST", "dependabot/alerts")]
    [InlineData("GET", "dependabot/secrets")]
    [InlineData("POST", "code-scanning/alerts/42/instances")]
    [InlineData("GET", "code-scanning/alerts/abc")]
    [InlineData("GET", "secret-scanning/alerts/abc")]
    [InlineData("GET", "security-advisories/reports")]
    [InlineData("DELETE", "security-advisories/GHSA-abcd-1234-efgh")]
    public void AdditionalRestFamiliesRejectUnknownMethodOrPath(string method, string path)
    {
        Assert.False(Classify(["api", "-X", method, $"repos/acme/repo/{path}"]).AllowedToEvaluate);
    }

    [Fact]
    public void DuplicateTargetAndMethodSelectorsAreRejected()
    {
        Assert.False(Classify(["api", "--method=GET", "-X", "DELETE", "repos/acme/repo/issues/1"]).AllowedToEvaluate);
        Assert.False(Classify(["repo", "sync", "acme/dest", "--source", "acme/source", "--source=other/repo"]).AllowedToEvaluate);
        Assert.False(Classify(["pr", "create", "-R", "acme/repo", "--head=local", "-H", "other:branch"]).AllowedToEvaluate);
        Assert.False(Classify(["secret", "set", "TOKEN", "-R", "acme/repo", "--app=actions", "-a", "dependabot"]).AllowedToEvaluate);
        Assert.False(Classify(["secret", "set", "TOKEN", "-R", "acme/repo", "--env=production", "-e", "staging"]).AllowedToEvaluate);
        Assert.False(Classify(["variable", "set", "MODE", "-R", "acme/repo", "--env=production", "-e", "staging"]).AllowedToEvaluate);
    }

    [Fact]
    public void ApiFieldsDefaultToPost()
    {
        var result = Classify(["api", "repos/acme/repo/issues", "-f", "title=example"]);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Contains(RepoRequirement("acme/repo", "issues", "write"), result.Requirements);
    }

    [Fact]
    public void InlineReviewReplyWithFieldsRequiresOnlyPullRequestWrite()
    {
        var result = Classify(["api", "repos/acme/repo/pulls/12/comments/456/replies", "-f", "body=Thanks"]);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal(new[] { RepoRequirement("acme/repo", "pullRequests", "write") }, result.Requirements);
    }

    [Theory]
    [InlineData("PATCH", "repos/acme/repo/issues/12", "write")]
    [InlineData("GET", "repos/acme/repo/issues/12/comments", "read")]
    [InlineData("POST", "repos/acme/repo/issues/12/comments", "write")]
    [InlineData("GET", "repos/acme/repo/issues/comments", "read")]
    [InlineData("GET", "repos/acme/repo/issues/comments/45", "read")]
    [InlineData("PATCH", "repos/acme/repo/issues/comments/45", "write")]
    [InlineData("DELETE", "repos/acme/repo/issues/comments/45", "write")]
    [InlineData("GET", "repos/acme/repo/issues/12/labels", "read")]
    [InlineData("POST", "repos/acme/repo/issues/12/labels", "write")]
    [InlineData("PUT", "repos/acme/repo/issues/12/labels", "write")]
    [InlineData("DELETE", "repos/acme/repo/issues/12/labels", "write")]
    [InlineData("DELETE", "repos/acme/repo/issues/12/labels/bug", "write")]
    [InlineData("GET", "repos/acme/repo/labels", "read")]
    [InlineData("POST", "repos/acme/repo/labels", "write")]
    [InlineData("PATCH", "repos/acme/repo/labels/bug", "write")]
    [InlineData("GET", "repos/acme/repo/labels/bug", "read")]
    [InlineData("DELETE", "repos/acme/repo/labels/bug", "write")]
    [InlineData("GET", "repos/acme/repo/milestones", "read")]
    [InlineData("POST", "repos/acme/repo/milestones", "write")]
    [InlineData("GET", "repos/acme/repo/milestones/4/labels", "read")]
    [InlineData("GET", "repos/acme/repo/milestones/4", "read")]
    [InlineData("PATCH", "repos/acme/repo/milestones/4", "write")]
    [InlineData("DELETE", "repos/acme/repo/milestones/4", "write")]
    public void IssueApiAllowsEitherIssueOrPullRequestPermission(string method, string endpoint, string level)
    {
        var result = Classify(["api", "-X", method, endpoint]);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal(new[]
        {
            RepoRequirement("acme/repo", "issues", level, "issue-or-pull-request"),
            RepoRequirement("acme/repo", "pullRequests", level, "issue-or-pull-request")
        }, result.Requirements);
    }

    [Fact]
    public void GetIssueRequiresIssuesReadOnly()
    {
        var result = Classify(["api", "repos/acme/repo/issues/12"]);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal(new[] { RepoRequirement("acme/repo", "issues", "read") }, result.Requirements);
    }

    [Theory]
    [InlineData("PUT", "repos/acme/repo/contents/.github/workflows/build.yml")]
    [InlineData("DELETE", "repos/acme/repo/contents/.github/workflows/build.yml")]
    [InlineData("PUT", "repos/acme/repo/contents/.github/workflows/nested/build.yml")]
    public void WorkflowFileWritesRequireContentsAndWorkflows(string method, string endpoint)
    {
        var result = Classify(["api", "-X", method, endpoint]);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal(new[]
        {
            RepoRequirement("acme/repo", "contents", "write"),
            RepoRequirement("acme/repo", "workflows", "write")
        }, result.Requirements);
    }

    [Theory]
    [InlineData("GET", "repos/acme/repo/contents/.github/workflows/build.yml", "read")]
    [InlineData("PUT", "repos/acme/repo/contents/.github/workflows-other/build.yml", "write")]
    [InlineData("DELETE", "repos/acme/repo/contents/docs/.github/workflows/build.yml", "write")]
    [InlineData("PUT", "repos/acme/repo/contents/.github/Workflows/build.yml", "write")]
    public void OtherContentsPathsRequireOnlyContents(string method, string endpoint, string level)
    {
        var result = Classify(["api", "-X", method, endpoint]);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal(new[] { RepoRequirement("acme/repo", "contents", level) }, result.Requirements);
    }

    [Theory]
    [InlineData("GET", "releases", "read", false)]
    [InlineData("GET", "releases/latest", "read", false)]
    [InlineData("GET", "releases/tags/v1.2.3", "read", false)]
    [InlineData("GET", "releases/42", "read", false)]
    [InlineData("GET", "releases/42/assets", "read", false)]
    [InlineData("GET", "releases/assets/9", "read", false)]
    [InlineData("POST", "releases", "write", true)]
    [InlineData("PATCH", "releases/42", "write", true)]
    [InlineData("DELETE", "releases/42", "write", false)]
    [InlineData("POST", "releases/generate-notes", "write", false)]
    [InlineData("PATCH", "releases/assets/9", "write", false)]
    [InlineData("DELETE", "releases/assets/9", "write", false)]
    public void ReleaseApiRoutesHaveRequiredPermissions(string method, string path, string level, bool workflows)
    {
        var result = Classify(["api", "-X", method, $"repos/acme/repo/{path}"]);

        Assert.True(result.AllowedToEvaluate, result.Error);
        var expected = new List<TargetRequirement> { RepoRequirement("acme/repo", "contents", level) };
        if (workflows)
            expected.Add(RepoRequirement("acme/repo", "workflows", "write"));
        Assert.Equal(expected, result.Requirements);
    }

    [Theory]
    [InlineData("list", "read")]
    [InlineData("view", "read")]
    [InlineData("download", "read")]
    [InlineData("create", "write")]
    [InlineData("edit", "write")]
    [InlineData("delete", "write")]
    [InlineData("delete-asset", "write")]
    [InlineData("upload", "write")]
    public void ReleaseCommandsHaveRequiredPermissions(string subcommand, string level)
    {
        var args = new List<string> { "release", subcommand, "-R", "acme/repo" };
        if (subcommand is not ("list" or "view" or "download"))
            args.Add("v1.0");
        if (subcommand == "delete-asset")
            args.Add("binary.zip");
        if (subcommand == "upload")
            args.Add("./binary.zip");

        var result = Classify(args.ToArray());

        Assert.True(result.AllowedToEvaluate, result.Error);
        var expected = new List<TargetRequirement> { RepoRequirement("acme/repo", "contents", level) };
        if (subcommand is "create" or "edit")
            expected.Add(RepoRequirement("acme/repo", "workflows", "write"));
        Assert.Equal(expected, result.Requirements);
    }

    [Theory]
    [InlineData("list", "--order", "asc")]
    [InlineData("view", "--template", "{{.name}}")]
    [InlineData("download", "-A", "zip")]
    [InlineData("download", "-p", "*.zip")]
    [InlineData("create", "-t", "Release title")]
    [InlineData("edit", "--notes", "Updated")]
    public void ReleaseValueFlagsDoNotBecomePositionals(string subcommand, string flag, string value)
    {
        var args = new List<string> { "release", subcommand, "-R", "acme/repo", flag, value };
        if (subcommand is "create" or "edit")
            args.Add("v1.0");

        Assert.True(Classify(args.ToArray()).AllowedToEvaluate);
    }

    [Theory]
    [InlineData("release", "verify")]
    [InlineData("release", "delete")]
    [InlineData("release", "delete-asset", "v1")]
    [InlineData("release", "upload", "v1")]
    [InlineData("release", "create")]
    [InlineData("release", "list", "unexpected")]
    [InlineData("release", "view", "one", "two")]
    [InlineData("release", "create", "v1", "--discussion-category", "General")]
    [InlineData("release", "edit", "v1", "--verify-tag")]
    [InlineData("release", "delete", "v1", "--cleanup-tag")]
    [InlineData("release", "view", "v1", "--web")]
    [InlineData("release", "create", "v1", "-p")]
    [InlineData("release", "download", "--archive", "zip", "--output")]
    public void UnsafeOrMalformedReleaseCommandsAreDenied(params string[] args)
    {
        Assert.False(Classify([.. args, "-R", "acme/repo"]).AllowedToEvaluate);
    }

    [Theory]
    [InlineData("api", "-X", "PUT", "repos/acme/repo/releases/1")]
    [InlineData("api", "-X", "DELETE", "repos/acme/repo/releases")]
    [InlineData("api", "-X", "POST", "repos/acme/repo/releases/1/assets")]
    [InlineData("api", "repos/acme/repo/releases/tags")]
    [InlineData("api", "repos/acme/repo/releases/assets/abc")]
    [InlineData("api", "https://uploads.github.com/repos/acme/repo/releases/1/assets?name=x")]
    [InlineData("api", "-X", "POST", "-f", "discussion_category_name=General", "repos/acme/repo/releases")]
    public void UnsupportedReleaseApiRoutesAndFieldsAreDenied(params string[] args)
    {
        Assert.False(Classify(args).AllowedToEvaluate);
    }

    [Theory]
    [InlineData("api", "graphql")]
    [InlineData("api", "repos/acme/repo/actions/runs/abc/logs")]
    [InlineData("api", "-X", "POST", "repos/acme/repo/actions/runs/1/logs")]
    [InlineData("api", "repos/acme/repo/actions/artifacts/45/tar")]
    [InlineData("api", "-X", "DELETE", "repos/acme/repo/actions/cache/usage")]
    [InlineData("api", "-X", "POST", "repos/acme/repo/actions/workflows/build.yml/enable")]
    [InlineData("api", "repos/acme/repo/issues/comments/not-a-number")]
    [InlineData("api", "repos/acme/repo/issues/not-a-number")]
    [InlineData("api", "-X", "POST", "repos/acme/repo/issues/12/labels/bug")]
    [InlineData("api", "-X", "PUT", "repos/acme/repo/milestones/4")]
    [InlineData("api", "-X", "DELETE", "repos/acme/repo/pulls/12/files")]
    [InlineData("api", "--input", "payload.json", "repos/acme/repo/issues")]
    [InlineData("api", "-X", "DELETE", "repos/acme/repo/pulls/1")]
    [InlineData("api", "https://api.evil.test/repos/acme/repo/issues")]
    [InlineData("api", "repos/acme/repo/contents/../issues")]
    [InlineData("api", "repos/acme/repo/contents/%2e%2e/issues")]
    [InlineData("api", "repos/acme/repo/contents/%2fissues")]
    [InlineData("api", "-X", "PUT", "repos/acme/repo/contents")]
    [InlineData("api", "--paginate", "-f", "title=x", "repos/acme/repo/issues")]
    [InlineData("api", "-X", "POST", "repos/acme/repo/pulls/comments")]
    [InlineData("api", "-X", "PATCH", "repos/acme/repo/pulls/comments")]
    [InlineData("api", "-X", "DELETE", "repos/acme/repo/pulls/12/comments")]
    [InlineData("api", "-X", "POST", "repos/acme/repo/pulls/comments/456/replies")]
    [InlineData("api", "-X", "POST", "repos/acme/repo/pulls/12/reviews/34/comments")]
    [InlineData("api", "repos/acme/repo/pulls/not-a-number/comments")]
    [InlineData("api", "repos/acme/repo/pulls/12/comments/not-a-number/replies")]
    [InlineData("api", "repos/acme/repo/pulls/comments/456/replies")]
    [InlineData("api", "-X", "POST", "repos/acme/repo/commits/main/status")]
    [InlineData("api", "-X", "PATCH", "repos/acme/repo/commits/main/statuses")]
    [InlineData("api", "repos/acme/repo/statuses/abc123")]
    [InlineData("api", "-X", "DELETE", "repos/acme/repo/statuses/abc123")]
    [InlineData("api", "repos/acme/repo/commits/main/status/extra")]
    [InlineData("api", "repos/acme/repo/commits/main/check-runs")]
    [InlineData("api", "repos/acme/repo/check-runs")]
    [InlineData("api", "-X", "POST", "repos/acme/repo/actions/secrets")]
    [InlineData("api", "-X", "PUT", "repos/acme/repo/actions/secrets/public-key")]
    [InlineData("api", "-X", "PATCH", "repos/acme/repo/actions/secrets/TOKEN")]
    [InlineData("api", "-X", "PUT", "repos/acme/repo/actions/variables/MODE")]
    [InlineData("api", "-X", "POST", "repos/acme/repo/actions/variables/MODE")]
    [InlineData("api", "-X", "POST", "repos/acme/repo/environments/production/secrets")]
    [InlineData("api", "-X", "PUT", "repos/acme/repo/environments/production/secrets/public-key")]
    [InlineData("api", "-X", "PUT", "repos/acme/repo/environments/production/variables/MODE")]
    [InlineData("api", "-X", "PUT", "repos/acme/repo/environments/production")]
    [InlineData("api", "repos/acme/repo/environments/production")]
    [InlineData("api", "repos/acme/repo/environments/production/deployment-branch-policies")]
    [InlineData("api", "repos/acme/repo/environments/production/secrets/TOKEN/extra")]
    public void UnknownApiRoutesAreDenied(params string[] args)
    {
        Assert.False(Classify(args).AllowedToEvaluate);
    }

    [Fact]
    public void GitOriginProvidesLocalRepositoryAndRoot()
    {
        using var repository = new TempGitRepository();
        repository.Run("remote", "add", "origin", "git@github.com:acme/local.git");

        var result = CommandClassifier.Classify(["pr", "list"], repository.Path, EmptyEnvironment);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal("acme/local", result.LocalRepository);
        Assert.Equal(System.IO.Path.GetFileName(repository.Path), System.IO.Path.GetFileName(result.RepositoryRoot));
        Assert.Equal("acme/local", result.PrimaryRepository);
    }

    [Fact]
    public void GhDefaultRemoteOverridesOrigin()
    {
        using var repository = new TempGitRepository();
        repository.Run("remote", "add", "origin", "git@github.com:alice/fork.git");
        repository.Run("remote", "add", "upstream", "git@github.com:acme/base.git");
        repository.Run("config", "remote.upstream.gh-resolved", "base");

        var result = CommandClassifier.Classify(["pr", "list"], repository.Path, EmptyEnvironment);

        Assert.True(result.AllowedToEvaluate, result.Error);
        Assert.Equal("acme/base", result.PrimaryRepository);
    }

    private static ClassificationResult Classify(string[] args, IReadOnlyDictionary<string, string?>? environment = null) =>
        CommandClassifier.Classify(args, Environment.CurrentDirectory, environment ?? EmptyEnvironment);

    private sealed class TempGitRepository : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gh-harness-classifier-" + Guid.NewGuid().ToString("N"));

        public TempGitRepository()
        {
            Directory.CreateDirectory(Path);
            Run("init", "--quiet");
        }

        public void Run(params string[] arguments)
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("git") { WorkingDirectory = Path, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
