using GhHarness.Policy;
using Xunit;

namespace GhHarness.Tests.Policy;

public sealed class PolicyStoreTests
{
    private static TargetId Repo(string name = "acme/project") => new("github.com", "repository", name);
    private static TargetId Org(string name = "acme") => new("github.com", "organization", name);
    private static TargetId Account(string name = "alice") => new("github.com", "account", name);
    private static PermissionNeed Need(string kind, string name, string level, string? group = null) =>
        new(new PermissionKey(kind, name), level, group);

    [Fact]
    public void RulesApplyInFileAndArrayOrderForEachTarget()
    {
        using var fixture = new Fixture();
        fixture.Home("a.gh-harness.json", """
            { "targets": [
              { "target": { "kind": "repository", "pattern": "acme/*" },
                "access": "allow", "permissions": { "repository": { "contents": "read", "secrets": "write" } } },
              { "target": { "kind": "organization", "pattern": "acme" },
                "access": "allow", "permissions": { "organization": { "secrets": "read" } } }
            ] }
            """);
        fixture.Home("b.gh-harness.json", """
            { "targets": [
              { "target": { "kind": "repository", "pattern": "ACME/project" },
                "permissions": { "repository": { "contents": "write" } } },
              { "target": { "kind": "repository", "pattern": "acme/project" },
                "permissions": { "repository": { "secrets": "none" } } }
            ] }
            """);
        var policy = fixture.Load();
        var repository = policy.Evaluate(Repo(), [Need("repository", "contents", "write")]);
        Assert.True(repository.Allowed);
        Assert.Equal(["acme/*", "ACME/project", "acme/project"], repository.Trace.Select(trace => trace.Pattern));
        Assert.False(policy.Evaluate(Repo(), [Need("repository", "secrets", "read")]).Allowed);
        Assert.True(policy.Evaluate(Org(), [Need("organization", "secrets", "read")]).Allowed);
        Assert.False(policy.Evaluate(Org(), [Need("organization", "secrets", "write")]).Allowed);
        Assert.False(policy.Evaluate(Account(), [Need("account", "emails", "read")]).Allowed);
    }

    [Fact]
    public void AlternativeGroupsAreOrWithinOneTarget()
    {
        using var fixture = new Fixture();
        fixture.Home("policy.gh-harness.json", """
            { "targets": [
              { "target": { "kind": "repository", "pattern": "acme/project" },
                "access": "allow", "permissions": { "repository": { "issues": "read" } } }
            ] }
            """);
        var policy = fixture.Load();
        Assert.True(policy.Evaluate(Repo(), [
            Need("repository", "issues", "read", "issue-or-pr"),
            Need("repository", "pullRequests", "read", "issue-or-pr")
        ]).Allowed);
        Assert.False(policy.Evaluate(Repo(), [
            Need("repository", "issues", "write", "issue-or-pr"),
            Need("repository", "pullRequests", "write", "issue-or-pr")
        ]).Allowed);
        Assert.False(policy.Evaluate(Repo(), [Need("organization", "issues", "read")]).Allowed);
    }

    [Fact]
    public void LocalRulesApplyOnlyToGitRemoteRepository()
    {
        using var fixture = new Fixture();
        fixture.Home("policy.gh-harness.json", """
            { "targets": [
              { "target": { "kind": "repository", "pattern": "acme/*" },
                "access": "allow", "permissions": { "repository": { "contents": "read" } } }
            ] }
            """);
        var local = fixture.Local("""
            { "targets": [
              { "target": { "kind": "repository", "pattern": "acme/*" },
                "permissions": { "repository": { "contents": "write" } } }
            ] }
            """);
        var policy = fixture.Load(local, "acme/project");
        Assert.True(policy.Evaluate(Repo(), [Need("repository", "contents", "write")]).Allowed);
        Assert.False(policy.Evaluate(Repo("acme/other"), [Need("repository", "contents", "write")]).Allowed);
        Assert.False(fixture.Load(local, null).Evaluate(Repo(), [Need("repository", "contents", "write")]).Allowed);
    }

    [Fact]
    public void AccountPatternIsExactAndCaseInsensitive()
    {
        using var fixture = new Fixture();
        fixture.Home("policy.gh-harness.json", """
            { "targets": [
              { "target": { "kind": "account", "pattern": "Alice" },
                "access": "allow", "permissions": { "account": { "emails": "read" } } }
            ] }
            """);
        var policy = fixture.Load();
        Assert.True(policy.Evaluate(Account(), [Need("account", "emails", "read")]).Allowed);
        Assert.False(policy.Evaluate(Account("bob"), [Need("account", "emails", "read")]).Allowed);
    }

    [Theory]
    [InlineData("{\"global\":{\"access\":\"allow\"}}")]
    [InlineData("{\"schemaVersion\":2,\"targets\":[]}")]
    [InlineData("{\"targets\":{},\"targets\":[]}")]
    [InlineData("{\"targets\":[{\"access\":\"allow\"}]}")]
    [InlineData("{\"targets\":[{\"target\":{\"kind\":\"team\",\"pattern\":\"acme\"}}]}")]
    [InlineData("{\"targets\":[{\"target\":{\"kind\":\"account\",\"pattern\":\"a*\"}}]}")]
    [InlineData("{\"targets\":[{\"target\":{\"kind\":\"repository\",\"pattern\":\"acme/project\"},\"permissions\":{\"organization\":{\"secrets\":\"read\"}}}]}")]
    [InlineData("{\"targets\":[{\"target\":{\"kind\":\"organization\",\"pattern\":\"acme\"},\"permissions\":{\"organization\":{\"unknown\":\"read\"}}}]}")]
    [InlineData("{\"targets\":[{\"target\":{\"kind\":\"organization\",\"pattern\":\"acme\"},\"permissions\":{\"organization\":{\"secrets\":\"admin\"}}}]}")]
    [InlineData("{\"targets\":[{\"target\":{\"kind\":\"repository\",\"pattern\":\"acme/project\"},\"permissions\":{\"unknown\":{}}}]}")]
    public void InvalidPolicyIsRejected(string json)
    {
        using var fixture = new Fixture();
        fixture.Home("policy.gh-harness.json", json);
        Assert.Throws<PolicyConfigurationException>(() => fixture.Load());
    }

    [Fact]
    public void LocalOrganizationRuleIsRejectedEvenWhenEvaluatingAnotherRepository()
    {
        using var fixture = new Fixture();
        var local = fixture.Local("""
            { "targets": [
              { "target": { "kind": "organization", "pattern": "acme" },
                "access": "allow", "permissions": { "organization": { "secrets": "write" } } }
            ] }
            """);
        Assert.Throws<PolicyConfigurationException>(() => fixture.Load(local, "other/repo"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gh-harness-policy-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(_root);
        public void Home(string name, string json)
        {
            var directory = Path.Combine(_root, ".gh-harness");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, name), json);
        }
        public string Local(string json)
        {
            var path = Path.Combine(_root, ".gh-harness.json");
            File.WriteAllText(path, json);
            return path;
        }
        public PolicyStore Load(string? local = null, string? repository = null) => PolicyStore.Load(_root, local, repository);
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
