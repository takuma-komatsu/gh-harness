using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GhHarness.Commands;
using GhHarness.Policy;
using Xunit;

namespace GhHarness.Tests.Policy;

public sealed class PatProfileStoreTests
{
    [Fact]
    public void MissingFileDisablesProfileCap()
    {
        using var fixture = new Fixture();
        Assert.Null(PatProfileStore.Load(fixture.Home));
    }

    [Fact]
    public void InvalidProfileDoesNotExposeFingerprint()
    {
        using var fixture = new Fixture();
        const string fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        fixture.WriteRaw("{\"mode\":\"enforce\",\"profiles\":[{\"host\":\"github.com\",\"tokenSha256\":\"" +
            fingerprint + "\",\"account\":\"alice\",\"resourceOwner\":{\"kind\":\"organization\",\"login\":\"acme\"}," +
            "\"repositoryAccess\":{\"selection\":\"selected\",\"names\":[\"other/repo\"]},\"permissions\":{}}]}");
        var error = Assert.Throws<PolicyConfigurationException>(() => PatProfileStore.Load(fixture.Home));
        Assert.DoesNotContain(fingerprint, error.Message);
    }

    [Fact]
    public async Task ResolvesPrecedenceAndEnforcesSelectedRepositoryAndPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.WriteProfile("primary", "organization", "acme", "selected", ["acme/project"],
            new { repository = new { secrets = "read" }, organization = new { secrets = "write" } });
        var store = PatProfileStore.Load(fixture.Home)!;
        var environment = new Dictionary<string, string?>
        {
            ["GH_TOKEN"] = "primary",
            ["GITHUB_TOKEN"] = "secondary",
            ["FAKE_LOGIN"] = "alice"
        };
        var gh = fixture.FakeGh();

        Assert.True((await store.EvaluateAsync([Repo("acme/project", "secrets", "read")], gh, environment)).Allowed);
        Assert.False((await store.EvaluateAsync([Repo("acme/project", "secrets", "write")], gh, environment)).Allowed);
        Assert.False((await store.EvaluateAsync([Repo("acme/other", "secrets", "read")], gh, environment)).Allowed);
        Assert.False((await store.EvaluateAsync([Repo("other/project", "secrets", "read")], gh, environment)).Allowed);
        Assert.True((await store.EvaluateAsync([Org("acme", "secrets", "write")], gh, environment)).Allowed);
        Assert.False((await store.EvaluateAsync([Org("other", "secrets", "read")], gh, environment)).Allowed);
        Assert.True((await store.EvaluateAsync([Repo("acme/project", "access", "allow")], gh, environment)).Allowed);

        environment["GH_TOKEN"] = null;
        var mismatch = await store.EvaluateAsync([Repo("acme/project", "secrets", "read")], gh, environment);
        Assert.False(mismatch.Allowed);
        Assert.DoesNotContain("secondary", mismatch.Reason);
        Assert.DoesNotContain("primary", mismatch.Reason);
    }

    [Fact]
    public async Task AccountLoginMustMatchCredentialAndProfile()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.WriteProfile("account-token", "account", "alice", "all", [], new { account = new { emails = "read" } });
        var store = PatProfileStore.Load(fixture.Home)!;
        var gh = fixture.FakeGh();
        var environment = new Dictionary<string, string?> { ["GH_TOKEN"] = "account-token", ["FAKE_LOGIN"] = "alice" };
        var binding = await CredentialBinding.ResolveAsync(gh, environment);
        Assert.NotNull(binding);
        Assert.Equal("alice", await binding.GetAuthenticatedLoginAsync(gh, environment));
        Assert.True((await store.EvaluateAsync([Account("alice", "emails", "read")], binding, gh, environment)).Allowed);
        Assert.False((await store.EvaluateAsync([Account("bob", "emails", "read")], binding, gh, environment)).Allowed);

        environment["FAKE_LOGIN"] = "bob";
        var newBinding = await CredentialBinding.ResolveAsync(gh, environment);
        Assert.NotNull(newBinding);
        Assert.False((await store.EvaluateAsync([Account("alice", "emails", "read")], newBinding, gh, environment)).Allowed);
    }

    [Fact]
    public async Task AlternativePermissionsAreCappedPerTarget()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        fixture.WriteProfile("primary", "organization", "acme", "all", [],
            new { repository = new { issues = "read", pullRequests = "none" } });
        var store = PatProfileStore.Load(fixture.Home)!;
        var gh = fixture.FakeGh();
        var environment = new Dictionary<string, string?> { ["GH_TOKEN"] = "primary" };
        var allowed = await store.EvaluateAsync([
            Repo("acme/project", "pullRequests", "read", "either"),
            Repo("ACME/PROJECT", "issues", "read", "either")], gh, environment);
        Assert.True(allowed.Allowed);
    }

    [Fact]
    public async Task CredentialLookupMarksChildAsHarnessInvocation()
    {
        if (OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var gh = Path.Combine(fixture.Home, "gh");
        File.WriteAllText(gh, "#!/bin/sh\nprintf '%s' \"$GH_HARNESS_RECURSION_GUARD\"\n");
        File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var binding = await CredentialBinding.ResolveAsync(gh, new Dictionary<string, string?>());

        Assert.NotNull(binding);
    }

    private static TargetRequirement Repo(string name, string category, string level, string? group = null) =>
        new(new TargetId("github.com", "repository", name), new PermissionKey("repository", category), level, group);

    private static TargetRequirement Org(string name, string category, string level) =>
        new(new TargetId("github.com", "organization", name), new PermissionKey("organization", category), level);

    private static TargetRequirement Account(string name, string category, string level) =>
        new(new TargetId("github.com", "account", name), new PermissionKey("account", category), level);

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Home = Path.Combine(Path.GetTempPath(), "gh-harness-profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Home, ".gh-harness"));
        }

        public string Home { get; }

        public void WriteRaw(string content) => File.WriteAllText(Path.Combine(Home, ".gh-harness", "pat-profiles.json"), content);

        public void WriteProfile(string token, string ownerKind, string owner, string selection, string[] names, object permissions)
        {
            var profile = new
            {
                mode = "enforce",
                profiles = new[]
                {
                    new
                    {
                        host = "github.com",
                        tokenSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
                        account = "alice",
                        resourceOwner = new { kind = ownerKind, login = owner },
                        repositoryAccess = new { selection, names },
                        permissions
                    }
                }
            };
            WriteRaw(JsonSerializer.Serialize(profile));
        }

        public string FakeGh()
        {
            var path = Path.Combine(Home, "gh");
            File.WriteAllText(path, "#!/bin/sh\n" +
                "if [ \"$1\" = auth ]; then\n" +
                "  printf '%s\\n' \"${GH_TOKEN:-${GITHUB_TOKEN:-stored-token}}\"\n" +
                "elif [ \"$1\" = api ]; then\n" +
                "  printf '{\"login\":\"%s\"}\\n' \"${FAKE_LOGIN:-alice}\"\n" +
                "else exit 1; fi\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        public void Dispose() => Directory.Delete(Home, recursive: true);
    }
}
