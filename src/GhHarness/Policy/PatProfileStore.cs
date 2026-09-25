using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GhHarness.Commands;

namespace GhHarness.Policy;

/// <summary>The declared upper bound for the credential that the GitHub CLI will use.</summary>
public sealed class PatProfileStore
{
    private static readonly Regex LoginPattern = new("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$", RegexOptions.CultureInvariant);
    private static readonly Regex RepositoryPattern = new("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> RepositoryPermissions = new(StringComparer.Ordinal)
    {
        "pullRequests", "issues", "actions", "contents", "workflows", "commitStatuses", "secrets", "variables",
        "environments", "administration", "deployments", "pages", "webhooks", "dependabotAlerts",
        "codeScanningAlerts", "secretScanningAlerts", "repositorySecurityAdvisories"
    };
    private static readonly Dictionary<string, HashSet<string>> Permissions = new(StringComparer.Ordinal)
    {
        ["repository"] = RepositoryPermissions,
        ["organization"] = new(StringComparer.Ordinal) { "secrets", "variables" },
        ["account"] = new(StringComparer.Ordinal) { "emails" }
    };

    private readonly IReadOnlyList<Profile> _profiles;

    private PatProfileStore(IReadOnlyList<Profile> profiles) => _profiles = profiles;

    /// <summary>Returns null when no optional profile file exists.</summary>
    public static PatProfileStore? Load(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        var path = Path.Combine(homeDirectory, ".gh-harness", "pat-profiles.json");
        if (!File.Exists(path)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = Properties(document.RootElement, "profile root");
            RequireFields(root, "profile root", ["mode", "profiles"]);
            if (String(root["mode"], "mode") != "enforce")
                throw new FormatException("mode must be enforce.");
            var entries = root["profiles"];
            if (entries.ValueKind != JsonValueKind.Array)
                throw new FormatException("profiles must be an array.");
            var profiles = entries.EnumerateArray().Select(ParseProfile).ToArray();
            return new PatProfileStore(profiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            // Parser errors mention property names but never token fingerprints or values.
            throw new PolicyConfigurationException($"Invalid PAT profile configuration '{path}': {ex.Message}", ex);
        }
    }

    public async Task<PatProfileDecision> EvaluateAsync(
        IReadOnlyList<TargetRequirement> requirements,
        string? ghExecutable,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(environment);
        var binding = await CredentialBinding.ResolveAsync(ghExecutable, environment, cancellationToken).ConfigureAwait(false);
        if (binding is null)
            return PatProfileDecision.Deny("Could not resolve the GitHub CLI credential.");
        return await EvaluateAsync(requirements, binding, ghExecutable!, environment, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PatProfileDecision> EvaluateAsync(
        IReadOnlyList<TargetRequirement> requirements,
        CredentialBinding binding,
        string ghExecutable,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(environment);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(binding.Token));
        var matches = _profiles.Where(profile => CryptographicOperations.FixedTimeEquals(profile.TokenHash, hash)).ToArray();
        if (matches.Length != 1)
            return PatProfileDecision.Deny(matches.Length == 0
                ? "No PAT profile matches the GitHub CLI credential."
                : "Multiple PAT profiles match the GitHub CLI credential.");

        var profile = matches[0];
        string? authenticatedLogin = null;
        if (requirements.Any(requirement => requirement.Target.Kind == "account"))
        {
            authenticatedLogin = await binding.GetAuthenticatedLoginAsync(
                ghExecutable, environment, cancellationToken).ConfigureAwait(false);
            if (authenticatedLogin is null)
                return PatProfileDecision.Deny("Could not verify the authenticated account.");
            if (!string.Equals(authenticatedLogin, profile.Account, StringComparison.OrdinalIgnoreCase))
                return PatProfileDecision.Deny("The authenticated account does not match the PAT profile.");
        }

        foreach (var requirement in requirements)
        {
            if (!AllowsTarget(profile, requirement.Target, authenticatedLogin))
                return PatProfileDecision.Deny($"PAT profile does not cover {requirement.Target.Kind} target {requirement.Target.Name}.");
        }

        var evaluatedGroups = new HashSet<(string Host, string Kind, string Name, string Group)>();
        foreach (var requirement in requirements)
        {
            if (requirement.AlternativeGroup is { } group)
            {
                if (!evaluatedGroups.Add((requirement.Target.Host.ToUpperInvariant(), requirement.Target.Kind,
                    requirement.Target.Name.ToUpperInvariant(), group))) continue;
                if (requirements.Where(other => SameTarget(other.Target, requirement.Target) && other.AlternativeGroup == group)
                    .Any(other => AllowsPermission(profile, other))) continue;
                return PatProfileDecision.Deny($"PAT profile permission denied for {requirement.Target.Kind} target {requirement.Target.Name}.");
            }
            if (!AllowsPermission(profile, requirement))
                return PatProfileDecision.Deny($"PAT profile permission denied for {requirement.Target.Kind} target {requirement.Target.Name}.");
        }
        return PatProfileDecision.Allow(binding.ChildEnvironmentOverrides);
    }

    private static bool SameTarget(TargetId left, TargetId right) =>
        left.Kind == right.Kind &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);

    private static bool AllowsTarget(Profile profile, TargetId target, string? authenticatedLogin)
    {
        if (!string.Equals(target.Host, profile.Host, StringComparison.OrdinalIgnoreCase)) return false;
        switch (target.Kind)
        {
            case "repository":
                if (!RepositoryPattern.IsMatch(target.Name)) return false;
                var owner = target.Name.Split('/')[0];
                return string.Equals(owner, profile.ResourceOwnerLogin, StringComparison.OrdinalIgnoreCase) &&
                    (profile.RepositorySelection == "all" || profile.Repositories.Contains(target.Name));
            case "organization":
                return profile.ResourceOwnerKind == "organization" &&
                    string.Equals(target.Name, profile.ResourceOwnerLogin, StringComparison.OrdinalIgnoreCase);
            case "account":
                return profile.ResourceOwnerKind == "account" && authenticatedLogin is not null &&
                    string.Equals(target.Name, authenticatedLogin, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(target.Name, profile.ResourceOwnerLogin, StringComparison.OrdinalIgnoreCase);
            default:
                return false;
        }
    }

    private static bool AllowsPermission(Profile profile, TargetRequirement requirement)
    {
        if (requirement.Permission.Namespace == requirement.Target.Kind &&
            requirement.Permission.Name == "access" && requirement.Level == "allow")
            return true; // Harness policy access is not a PAT permission.
        if (requirement.Target.Kind != requirement.Permission.Namespace) return false;
        if (!profile.Permissions.TryGetValue(requirement.Permission, out var granted)) return false;
        return requirement.Level switch
        {
            "read" => granted is "read" or "write",
            "write" => granted == "write",
            _ => false
        };
    }

    private static Profile ParseProfile(JsonElement element)
    {
        var properties = Properties(element, "profile");
        RequireFields(properties, "profile", ["host", "tokenSha256", "account", "resourceOwner", "repositoryAccess", "permissions"]);
        var host = String(properties["host"], "host");
        if (host != "github.com") throw new FormatException("host must be github.com.");
        var fingerprint = String(properties["tokenSha256"], "tokenSha256");
        if (fingerprint.Length != 64 || fingerprint.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new FormatException("tokenSha256 must be 64 lowercase hexadecimal characters.");
        var account = Login(properties["account"], "account");

        var owner = Properties(properties["resourceOwner"], "resourceOwner");
        RequireFields(owner, "resourceOwner", ["kind", "login"]);
        var ownerKind = String(owner["kind"], "resourceOwner.kind");
        if (ownerKind is not ("organization" or "account"))
            throw new FormatException("resourceOwner.kind must be organization or account.");
        var ownerLogin = Login(owner["login"], "resourceOwner.login");
        if (ownerKind == "account" && !string.Equals(account, ownerLogin, StringComparison.OrdinalIgnoreCase))
            throw new FormatException("Account resource owner must match account.");

        var repositoryAccess = Properties(properties["repositoryAccess"], "repositoryAccess");
        RequireFields(repositoryAccess, "repositoryAccess", ["selection"], ["names"]);
        var selection = String(repositoryAccess["selection"], "repositoryAccess.selection");
        if (selection is not ("all" or "selected"))
            throw new FormatException("repositoryAccess.selection must be all or selected.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (repositoryAccess.TryGetValue("names", out var nameElement))
        {
            if (nameElement.ValueKind != JsonValueKind.Array)
                throw new FormatException("repositoryAccess.names must be an array.");
            foreach (var item in nameElement.EnumerateArray())
            {
                var name = String(item, "repositoryAccess.names item");
                if (!RepositoryPattern.IsMatch(name) || !string.Equals(name.Split('/')[0], ownerLogin, StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("repositoryAccess.names must contain OWNER/REPO names for the resource owner.");
                if (!names.Add(name)) throw new FormatException("Duplicate repositoryAccess.names item.");
            }
        }
        if (selection == "selected" && !repositoryAccess.ContainsKey("names"))
            throw new FormatException("Selected repository access requires names.");
        if (selection == "all" && names.Count > 0)
            throw new FormatException("All repository access cannot specify names.");

        var permissions = new Dictionary<PermissionKey, string>();
        foreach (var (namespaceName, namespaceElement) in Properties(properties["permissions"], "permissions"))
        {
            if (!Permissions.TryGetValue(namespaceName, out var known) ||
                namespaceName == "organization" && ownerKind != "organization" ||
                namespaceName == "account" && ownerKind != "account")
                throw new FormatException($"Invalid permission namespace '{namespaceName}'.");
            foreach (var (name, value) in Properties(namespaceElement, $"permissions.{namespaceName}"))
            {
                if (!known.Contains(name)) throw new FormatException($"Unknown permission '{namespaceName}.{name}'.");
                var level = String(value, $"permissions.{namespaceName}.{name}");
                if (level is not ("none" or "read" or "write"))
                    throw new FormatException($"Invalid permission level for '{namespaceName}.{name}'.");
                permissions.Add(new PermissionKey(namespaceName, name), level);
            }
        }
        return new Profile(host, Convert.FromHexString(fingerprint), account, ownerKind, ownerLogin, selection, names, permissions);
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException($"{name} must be an object.");
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!result.TryAdd(property.Name, property.Value))
                throw new FormatException($"Duplicate property '{property.Name}'.");
        return result;
    }

    private static void RequireFields(Dictionary<string, JsonElement> properties, string name,
        IReadOnlyList<string> required, IReadOnlyList<string>? optional = null)
    {
        foreach (var field in required)
            if (!properties.ContainsKey(field)) throw new FormatException($"{name} requires {field}.");
        foreach (var field in properties.Keys)
            if (!required.Contains(field) && (optional is null || !optional.Contains(field)))
                throw new FormatException($"Unknown {name} property '{field}'.");
    }

    private static string String(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new FormatException($"{name} must be a string.");

    private static string Login(JsonElement element, string name)
    {
        var value = String(element, name);
        if (!LoginPattern.IsMatch(value)) throw new FormatException($"{name} must be a GitHub login.");
        return value;
    }

    private sealed record Profile(string Host, byte[] TokenHash, string Account, string ResourceOwnerKind,
        string ResourceOwnerLogin, string RepositorySelection, HashSet<string> Repositories,
        Dictionary<PermissionKey, string> Permissions);
}

public sealed class PatProfileDecision
{
    private PatProfileDecision(bool allowed, string reason, IReadOnlyDictionary<string, string?> overrides)
    {
        Allowed = allowed;
        Reason = reason;
        ChildEnvironmentOverrides = overrides;
    }

    public bool Allowed { get; }
    public string Reason { get; }
    internal IReadOnlyDictionary<string, string?> ChildEnvironmentOverrides { get; }

    internal static PatProfileDecision Allow(IReadOnlyDictionary<string, string?> overrides) => new(true, "Allowed by PAT profile.", overrides);
    internal static PatProfileDecision Deny(string reason) => new(false, reason, new Dictionary<string, string?>());
}

/// <summary>Resolves and pins the credential used by the GitHub CLI.</summary>
public sealed class CredentialBinding
{
    private string? _authenticatedLogin;
    private bool _loginResolved;

    private CredentialBinding(string token) => Token = token;

    internal string Token { get; }
    internal IReadOnlyDictionary<string, string?> ChildEnvironmentOverrides => PinToken(Token);

    public static async Task<CredentialBinding?> ResolveAsync(string? ghExecutable,
        IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (ghExecutable is null) return null;
        var output = await CaptureAsync(ghExecutable, ["auth", "token", "--hostname", "github.com"],
            environment, null, cancellationToken).ConfigureAwait(false);
        if (output is null) return null;
        var token = output.TrimEnd('\r', '\n');
        return token.Length > 0 && !token.Any(char.IsWhiteSpace) ? new CredentialBinding(token) : null;
    }

    public async Task<string?> GetAuthenticatedLoginAsync(string ghExecutable,
        IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken = default)
    {
        if (_loginResolved) return _authenticatedLogin;
        var output = await CaptureAsync(ghExecutable, ["api", "/user"], environment, Token, cancellationToken).ConfigureAwait(false);
        if (output is null) return null;
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.String)
            {
                var value = login.GetString();
                if (value is not null && Regex.IsMatch(value, "^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$"))
                {
                    _authenticatedLogin = value;
                    _loginResolved = true;
                    return value;
                }
            }
        }
        catch (JsonException) { }
        return null;
    }

    internal static IReadOnlyDictionary<string, string?> PinToken(string token) =>
        new Dictionary<string, string?> { ["GH_TOKEN"] = token, ["GITHUB_TOKEN"] = null, ["GH_HOST"] = "github.com" };

    private static async Task<string?> CaptureAsync(string executable, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment, string? pinnedToken, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executable),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };
        start.Environment.Clear();
        foreach (var (name, value) in environment)
            if (value is not null) start.Environment[name] = value;
        if (pinnedToken is not null)
            foreach (var (name, value) in PinToken(pinnedToken))
                if (value is null) start.Environment.Remove(name); else start.Environment[name] = value;
        start.Environment[GhProcess.RecursionGuardEnvironmentVariable] = "1";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            if (!process.Start()) return null;
            started = true;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false); // Never expose CLI output, including on failure.
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // The process may have exited after HasExited.
            }
        }
    }
}
