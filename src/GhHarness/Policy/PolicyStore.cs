using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GhHarness.Policy;

public readonly record struct TargetId(string Host, string Kind, string Name);
public readonly record struct PermissionKey(string Namespace, string Name);
public readonly record struct PermissionNeed(PermissionKey Key, string Level, string? AlternativeGroup = null);
public sealed record PolicyTrace(string Source, string Scope, string? Pattern, string? Access,
    IReadOnlyDictionary<PermissionKey, string> Permissions);
public sealed record PolicyDecision(bool Allowed, string Reason, IReadOnlyList<PolicyTrace> Trace);

public sealed class PolicyConfigurationException : Exception
{
    public PolicyConfigurationException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>Applies home rules in file and array order, followed by local rules for their Git remote repository.</summary>
public sealed class PolicyStore
{
    private static readonly HashSet<string> RepositoryPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "pullRequests", "issues", "actions", "contents", "workflows", "commitStatuses", "secrets",
        "variables", "environments", "administration", "deployments", "pages", "webhooks",
        "dependabotAlerts", "codeScanningAlerts", "secretScanningAlerts", "repositorySecurityAdvisories"
    };
    private static readonly HashSet<string> OrganizationPermissions = new(StringComparer.OrdinalIgnoreCase) { "secrets", "variables" };
    private static readonly HashSet<string> AccountPermissions = new(StringComparer.OrdinalIgnoreCase) { "emails" };
    private static readonly Regex RepositoryName = new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex LoginName = new(@"^[A-Za-z0-9-]+$", RegexOptions.CultureInvariant);

    private readonly IReadOnlyList<Rule> _homeRules;
    private readonly IReadOnlyList<Rule> _localRules;
    private readonly string? _localRepository;

    private PolicyStore(IReadOnlyList<Rule> homeRules, IReadOnlyList<Rule> localRules, string? localRepository)
    {
        _homeRules = homeRules;
        _localRules = localRules;
        _localRepository = localRepository;
    }

    public static PolicyStore Load(string homeDirectory, string? localConfigPath, string? localRepository)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeDirectory);
        var directory = Path.Combine(homeDirectory, ".gh-harness");
        var homeRules = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetFileName(path).EndsWith(".gh-harness.json", StringComparison.Ordinal))
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .SelectMany(path => ParseFile(path, local: false)).ToArray()
            : [];
        var localRules = localConfigPath is not null && File.Exists(localConfigPath)
            ? ParseFile(localConfigPath, local: true)
            : [];
        return new PolicyStore(homeRules, localRules, localRepository);
    }

    public PolicyDecision Evaluate(TargetId target, IReadOnlyList<PermissionNeed> needs)
    {
        ArgumentNullException.ThrowIfNull(needs);
        if (!string.Equals(target.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !IsTargetName(target.Kind, target.Name))
            return new PolicyDecision(false, "Invalid or unsupported target.", []);

        IEnumerable<Rule> rules = _homeRules.Where(rule => rule.Kind == target.Kind && rule.Matcher.IsMatch(target.Name));
        if (target.Kind == "repository" && string.Equals(target.Name, _localRepository, StringComparison.OrdinalIgnoreCase))
            rules = rules.Concat(_localRules.Where(rule => rule.Matcher.IsMatch(target.Name)));

        var access = "deny";
        var permissions = new Dictionary<PermissionKey, string>();
        var trace = new List<PolicyTrace>();
        foreach (var rule in rules)
        {
            if (rule.Access is not null) access = rule.Access;
            foreach (var (key, level) in rule.Permissions)
                permissions[key] = level;
            trace.Add(new PolicyTrace(rule.Source, rule.Kind, rule.Pattern, rule.Access,
                new ReadOnlyDictionary<PermissionKey, string>(rule.Permissions)));
        }
        if (access != "allow")
            return new PolicyDecision(false, $"{target.Kind} access denied.", trace);

        var evaluatedGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var need in needs)
        {
            if (need.AlternativeGroup is not null)
            {
                if (!evaluatedGroups.Add(need.AlternativeGroup)) continue;
                var alternatives = needs.Where(candidate => candidate.AlternativeGroup == need.AlternativeGroup).ToArray();
                if (!alternatives.Any(IsAllowed))
                    return new PolicyDecision(false, $"Permission {string.Join(" or ", alternatives.Select(Describe))} denied.", trace);
                continue;
            }
            if (!IsAllowed(need))
                return new PolicyDecision(false, $"Permission {Describe(need)} denied.", trace);
        }
        return new PolicyDecision(true, "Allowed.", trace);

        bool IsAllowed(PermissionNeed need) =>
            need.Key.Namespace == target.Kind &&
            (need.Key.Name == "access" && need.Level == "allow" ||
             permissions.TryGetValue(need.Key, out var granted) && Allows(granted, need.Level));
    }

    private static string Describe(PermissionNeed need) => $"{need.Key.Namespace}.{need.Key.Name}:{need.Level}";
    private static bool Allows(string granted, string? required) => required?.ToLowerInvariant() switch
    {
        "read" => granted is "read" or "write",
        "write" => granted == "write",
        _ => false
    };
    private static bool IsTargetName(string? kind, string? name) => kind switch
    {
        "repository" => name is not null && RepositoryName.IsMatch(name) &&
            name.Split('/').All(part => part is not ("." or "..")),
        "organization" or "account" => name is not null && LoginName.IsMatch(name),
        _ => false
    };

    private static Rule[] ParseFile(string path, bool local)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            RequireObject(root, "root");
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "targets")
                throw new FormatException("Root must contain only targets.");
            if (properties[0].Value.ValueKind != JsonValueKind.Array)
                throw new FormatException("targets must be an array.");
            return properties[0].Value.EnumerateArray().Select(item => ParseRule(item, path, local)).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            throw new PolicyConfigurationException($"Invalid policy configuration '{path}': {exception.Message}", exception);
        }
    }

    private static Rule ParseRule(JsonElement element, string source, bool local)
    {
        RequireObject(element, "rule");
        string? kind = null, pattern = null, access = null;
        var permissions = new Dictionary<PermissionKey, string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new FormatException($"Duplicate rule property '{property.Name}'.");
            switch (property.Name)
            {
                case "target": (kind, pattern) = ParseTarget(property.Value); break;
                case "access":
                    access = ReadString(property.Value, "access").ToLowerInvariant();
                    if (access is not ("allow" or "deny")) throw new FormatException("access must be allow or deny.");
                    break;
                case "permissions": permissions = ParsePermissions(property.Value); break;
                default: throw new FormatException($"Unknown rule property '{property.Name}'.");
            }
        }
        if (kind is null || pattern is null) throw new FormatException("rule needs a target.");
        if (local && kind != "repository") throw new FormatException("Local policy may contain only repository rules.");
        if (permissions.Keys.Any(key => key.Namespace != kind))
            throw new FormatException("Permission namespace must match target kind.");
        if (kind == "account" && (!LoginName.IsMatch(pattern) || ContainsGlob(pattern)))
            throw new FormatException("Account pattern must be an exact login.");
        if (kind == "organization" && pattern.Contains('/'))
            throw new FormatException("Organization pattern must be a login pattern.");
        if (kind == "repository" && !pattern.Contains('/'))
            throw new FormatException("Repository pattern must contain owner and repository.");
        return new Rule(source, kind, pattern, CompilePattern(pattern), access, permissions);
    }

    private static (string Kind, string Pattern) ParseTarget(JsonElement element)
    {
        RequireObject(element, "target");
        string? kind = null, pattern = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new FormatException($"Duplicate target property '{property.Name}'.");
            switch (property.Name)
            {
                case "kind": kind = ReadString(property.Value, "kind"); break;
                case "pattern": pattern = ReadString(property.Value, "pattern"); break;
                default: throw new FormatException($"Unknown target property '{property.Name}'.");
            }
        }
        if (kind is not ("repository" or "organization" or "account"))
            throw new FormatException($"Unknown target kind '{kind}'.");
        if (string.IsNullOrEmpty(pattern)) throw new FormatException("target needs a pattern.");
        return (kind, pattern);
    }

    private static Dictionary<PermissionKey, string> ParsePermissions(JsonElement element)
    {
        RequireObject(element, "permissions");
        var permissions = new Dictionary<PermissionKey, string>();
        var namespaces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ns in element.EnumerateObject())
        {
            if (!namespaces.Add(ns.Name)) throw new FormatException($"Duplicate permission namespace '{ns.Name}'.");
            var known = ns.Name switch
            {
                "repository" => RepositoryPermissions,
                "organization" => OrganizationPermissions,
                "account" => AccountPermissions,
                _ => throw new FormatException($"Unknown permission namespace '{ns.Name}'.")
            };
            RequireObject(ns.Value, ns.Name);
            foreach (var permission in ns.Value.EnumerateObject())
            {
                if (!known.Contains(permission.Name)) throw new FormatException($"Unknown permission '{ns.Name}.{permission.Name}'.");
                var level = ReadString(permission.Value, permission.Name).ToLowerInvariant();
                if (level is not ("none" or "read" or "write"))
                    throw new FormatException($"Unknown level '{level}'.");
                var key = new PermissionKey(ns.Name, CanonicalName(known, permission.Name));
                if (!permissions.TryAdd(key, level)) throw new FormatException($"Duplicate permission '{permission.Name}'.");
            }
        }
        return permissions;
    }

    private static string CanonicalName(HashSet<string> known, string name) =>
        known.First(value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    private static bool ContainsGlob(string pattern) => pattern.IndexOfAny(['*', '?', '[', ']', '\\']) >= 0;
    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.String ? element.GetString()! : throw new FormatException($"{name} must be a string.");
    private static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new FormatException($"{name} must be an object.");
    }

    private static Regex CompilePattern(string pattern)
    {
        if (pattern.StartsWith('!')) throw new FormatException("Negated patterns are unsupported; use access: deny.");
        var regex = new StringBuilder("\\A");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*')
            {
                if (i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    i++;
                    if (i + 1 < pattern.Length && pattern[i + 1] == '/')
                    {
                        regex.Append("(?:.*/)?");
                        i++;
                    }
                    else regex.Append(".*");
                }
                else regex.Append("[^/]*");
            }
            else if (c == '?') regex.Append("[^/]");
            else if (c == '[')
            {
                var end = pattern.IndexOf(']', i + 1);
                if (end < 0 || end == i + 1) throw new FormatException($"Invalid character class in pattern '{pattern}'.");
                var content = pattern[(i + 1)..end];
                var negated = content.StartsWith('!') || content.StartsWith('^');
                if (negated) content = content[1..];
                if (content.Length == 0 || content.Contains('/') || content.Contains('\\'))
                    throw new FormatException($"Invalid character class in pattern '{pattern}'.");
                regex.Append(negated ? "[^/" : "[");
                regex.Append(content.Replace("^", "\\^", StringComparison.Ordinal));
                regex.Append(']');
                i = end;
            }
            else if (c == '\\')
            {
                if (i + 1 == pattern.Length) throw new FormatException($"Trailing escape in pattern '{pattern}'.");
                regex.Append(Regex.Escape(pattern[++i].ToString()));
            }
            else regex.Append(Regex.Escape(c.ToString()));
        }
        regex.Append("\\z");
        try { return new Regex(regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException exception) { throw new FormatException($"Invalid pattern '{pattern}'.", exception); }
    }

    private sealed record Rule(string Source, string Kind, string Pattern, Regex Matcher, string? Access,
        Dictionary<PermissionKey, string> Permissions);
}
