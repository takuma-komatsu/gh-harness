using System.Diagnostics;
using System.Text.RegularExpressions;
using GhHarness.Policy;

namespace GhHarness.Commands;

public sealed record TargetRequirement(TargetId Target, PermissionKey Permission, string Level, string? AlternativeGroup = null);

public sealed record ClassificationResult(
    bool AllowedToEvaluate,
    string? Error,
    IReadOnlyList<TargetRequirement> Requirements,
    string? PrimaryRepository,
    IReadOnlyDictionary<string, string?> ChildEnvironmentOverrides,
    string? LocalRepository,
    string? RepositoryRoot);

/// <summary>Classifies a deliberately small, auditable subset of gh commands.</summary>
public static class CommandClassifier
{
    private static TargetRequirement RepositoryRequirement(string repository, string category, string level, string? alternativeGroup = null) =>
        new(new TargetId("github.com", "repository", repository), new PermissionKey("repository", category), level, alternativeGroup);

    private static TargetRequirement OrganizationRequirement(string organization, string category, string level) =>
        new(new TargetId("github.com", "organization", organization), new PermissionKey("organization", category), level);

    private static readonly Regex RepositoryName = new(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OrganizationName = new(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ApiRepositoryPath = new(@"^/?repos/(?<owner>[^/]+)/(?<repo>[^/]+)(?<tail>/.*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ApiOrganizationActionsPath = new(@"^/?orgs/(?<org>[^/]+)/actions/(?<family>secrets|variables)(?<tail>/.*)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static ClassificationResult Classify(string[] args, string workingDirectory, IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(environment);

        var root = FindRepositoryRoot(workingDirectory);
        var local = ResolveLocalRepository(workingDirectory);
        ClassificationResult Deny(string reason) => new(false, reason, [], null, new Dictionary<string, string?>(), local, root);

        if (args.Length == 0)
            return Deny("A gh command is required.");

        if (environment.TryGetValue("GH_HOST", out var ghHost) && !string.IsNullOrWhiteSpace(ghHost) && !ghHost.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return Deny("Only github.com is supported.");

        var tokens = new List<string>();
        var explicitRepositories = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (FlagTakesValue(arg) && arg is not ("-R" or "--repo"))
            {
                if (i + 1 >= args.Length || args[i + 1] is "-R" or "--repo")
                    return Deny($"{arg} requires a value.");
                tokens.Add(arg);
                tokens.Add(args[++i]);
                continue;
            }
            if (arg is "-R" or "--repo")
            {
                if (++i >= args.Length || !TryParseRepository(args[i], out var name))
                    return Deny("-R/--repo requires a github.com OWNER/REPO.");
                explicitRepositories.Add(name);
                continue;
            }
            if (arg.StartsWith("--repo=", StringComparison.Ordinal))
            {
                if (!TryParseRepository(arg[7..], out var name))
                    return Deny("--repo requires a github.com OWNER/REPO.");
                explicitRepositories.Add(name);
                continue;
            }
            if (arg.StartsWith("-R", StringComparison.Ordinal) && arg.Length > 2)
            {
                if (!TryParseRepository(arg[2..], out var name))
                    return Deny("-R requires a github.com OWNER/REPO.");
                explicitRepositories.Add(name);
                continue;
            }
            tokens.Add(arg);
        }

        // Only the primary positional PR/Issue argument may identify a repository URL.
        // Scanning all arguments would mistake --body or other payload values for targets.
        if (tokens.Count >= 3 && tokens[0] is ("pr" or "issue"))
        {
            var positionals = PositionalArguments(tokens, 2);
            if (positionals.Count > 0 && Uri.TryCreate(positionals[0], UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                if (!TryParseRepository(positionals[0], out var name))
                    return Deny("Only github.com repository URLs are supported.");
                explicitRepositories.Add(name);
            }
        }

        if (explicitRepositories.Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any())
            return Deny("Conflicting explicit repositories were specified.");

        string? selected = explicitRepositories.FirstOrDefault();
        if (selected is null && environment.TryGetValue("GH_REPO", out var ghRepo) && !string.IsNullOrWhiteSpace(ghRepo))
        {
            if (!TryParseRepository(ghRepo, out var envRepo))
                return Deny("GH_REPO must identify a github.com OWNER/REPO.");
            selected = envRepo;
        }
        selected ??= local;

        if (tokens.Count == 0)
            return Deny("A gh command is required.");

        var command = tokens[0].ToLowerInvariant();
        var subcommand = tokens.Count > 1 ? tokens[1].ToLowerInvariant() : "";
        if (!ValidateFlags(tokens, command, subcommand, out var flagError))
            return Deny(flagError!);
        if (command == "api" && EnumerateFlags(tokens).Count(option => option.Name is "--method" or "-X") > 1 ||
            command == "repo" && subcommand == "sync" && EnumerateFlags(tokens).Count(option => option.Name == "--source") > 1 ||
            command == "pr" && subcommand == "create" && EnumerateFlags(tokens).Count(option => option.Name is "--head" or "-H") > 1)
            return Deny("Duplicate target or method selectors are unsupported.");
        var requirements = new List<TargetRequirement>();
        string? error;

        if (command == "api")
        {
            error = ClassifyApi(tokens, selected, requirements, out var apiRepository);
            if (apiRepository is null && requirements.Count > 0 && explicitRepositories.Count > 0)
                return Deny("The API endpoint conflicts with the explicit repository.");
            if (apiRepository is not null)
            {
                if (selected is not null && !selected.Equals(apiRepository, StringComparison.OrdinalIgnoreCase) && explicitRepositories.Count > 0)
                    return Deny("The API endpoint conflicts with the explicit repository.");
                selected = apiRepository;
            }
        }
        else if (command is "pr" or "issue" or "run" or "workflow" or "cache" or "repo" or "release" or "secret" or "variable")
        {
            error = ClassifyStandard(command, subcommand, tokens, selected, explicitRepositories, requirements, out var primary);
            selected = primary ?? selected;
        }
        else
        {
            error = $"Unsupported gh command: {command}.";
        }

        if (error is not null)
            return Deny(error);
        if (requirements.Count == 0 || requirements.Any(requirement => requirement.Target.Kind == "repository") && selected is null)
            return Deny("Unable to determine the target repository.");

        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GH_HOST"] = "github.com",
            ["GH_REPO"] = requirements.Any(requirement => requirement.Target.Kind == "repository") ? selected : null
        };
        return new(true, null, requirements, requirements.Any(requirement => requirement.Target.Kind == "repository") ? selected : null, overrides, local, root);
    }

    public static string? FindRepositoryRoot(string workingDirectory)
    {
        var output = RunGit(workingDirectory, "rev-parse", "--show-toplevel");
        return string.IsNullOrWhiteSpace(output) ? null : output;
    }

    public static string? ResolveLocalRepository(string workingDirectory)
    {
        if (FindRepositoryRoot(workingDirectory) is null)
            return null;

        var ghDefaults = RunGit(workingDirectory, "config", "--get-regexp", @"^remote\..*\.gh-resolved$")?
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Regex.Match(line, @"^remote\.(?<remote>.+)\.gh-resolved\s+base$", RegexOptions.CultureInvariant))
            .Where(match => match.Success)
            .Select(match => match.Groups["remote"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (ghDefaults.Length > 1)
            return null;
        if (ghDefaults.Length == 1)
        {
            var defaultUrl = RunGit(workingDirectory, "remote", "get-url", ghDefaults[0]);
            return defaultUrl is not null && TryParseRepository(defaultUrl, out var defaultRepository) ? defaultRepository : null;
        }

        var branch = RunGit(workingDirectory, "symbolic-ref", "--quiet", "--short", "HEAD");
        var branchRemote = branch is null ? null : RunGit(workingDirectory, "config", "--get", $"branch.{branch}.remote");
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(branchRemote))
            candidates.Add(branchRemote);
        candidates.Add("origin");
        var remotes = RunGit(workingDirectory, "remote")?.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [];
        candidates.AddRange(remotes);

        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var remote in candidates.Distinct(StringComparer.Ordinal))
        {
            var url = RunGit(workingDirectory, "remote", "get-url", remote);
            if (url is null || !TryParseRepository(url, out var repository))
                continue;
            if (remote == branchRemote || remote == "origin")
                return repository;
            resolved.Add(repository);
        }
        return resolved.Count == 1 ? resolved.First() : null;
    }

    private static string? ClassifyStandard(string command, string subcommand, List<string> tokens, string? selected,
        List<string> explicitRepositories, List<TargetRequirement> requirements, out string? primary)
    {
        primary = selected;
        if (subcommand.Length == 0 || subcommand.StartsWith('-'))
            return $"A {command} subcommand is required.";

        var positionals = PositionalArguments(tokens, 2);
        if (command == "repo")
        {
            if (subcommand is "clone" or "view" or "read-file" or "read-dir")
            {
                if (subcommand == "view" && positionals.Count > 1 || subcommand is "read-file" or "read-dir" && positionals.Count != 1)
                    return "Unexpected repository command arguments.";
                if (subcommand is "clone" or "view" && positionals.Count > 0)
                {
                    if (!TryParseRepository(positionals[0], out var positionalRepo))
                        return "The positional repository must be an explicit OWNER/REPO or github.com URL.";
                    if (explicitRepositories.Count > 0 && !selected!.Equals(positionalRepo, StringComparison.OrdinalIgnoreCase))
                        return "The positional repository conflicts with -R/--repo.";
                    primary = positionalRepo;
                }
                if (subcommand == "clone" && (positionals.Count == 0 || !TryParseRepository(positionals[0], out _)))
                    return "repo clone requires an explicit OWNER/REPO or github.com URL.";
                if (primary is null)
                    return "Unable to determine the target repository.";
                var category = subcommand == "view" ? "access" : "contents";
                var level = subcommand == "view" ? "allow" : "read";
                requirements.Add(RepositoryRequirement(primary, category, level));
                return null;
            }
            if (subcommand == "sync")
            {
                if (positionals.Count != 1 || !TryParseRepository(positionals[0], out var destination))
                    return "repo sync requires an explicit destination OWNER/REPO.";
                var sourceValue = OptionValue(tokens, "--source");
                if (sourceValue is null || !TryParseRepository(sourceValue, out var source))
                    return "repo sync requires an explicit --source OWNER/REPO.";
                primary = destination;
                requirements.Add(RepositoryRequirement(source, "contents", "read"));
                requirements.Add(RepositoryRequirement(destination, "contents", "write"));
                return null;
            }
            return $"Unsupported gh repo command: {subcommand}.";
        }

        if (command is "secret" or "variable")
        {
            if (EnumerateFlags(tokens).Count(option => option.Name is "--env" or "-e") > 1 ||
                EnumerateFlags(tokens).Count(option => option.Name is "--org" or "-o") > 1 ||
                command == "secret" && EnumerateFlags(tokens).Count(option => option.Name is "--app" or "-a") > 1)
                return "Duplicate secret or variable target selectors are unsupported.";
            var level = subcommand switch
            {
                "list" or "get" => "read",
                "set" or "delete" => "write",
                _ => null
            };
            if (level is null || command == "secret" && subcommand == "get")
                return $"Unsupported gh {command} command: {subcommand}.";
            if (positionals.Count != (subcommand == "list" ? 0 : 1) || positionals.Any(value => value.Length == 0 || value.StartsWith('-')))
                return $"Unexpected {command} command arguments.";
            var environmentName = OptionValue(tokens, "--env", "-e");
            if (HasAny(tokens, "--env", "-e") && (string.IsNullOrWhiteSpace(environmentName) || environmentName.StartsWith('-')))
                return "--env requires an environment name.";
            var organization = OptionValue(tokens, "--org", "-o");
            if (HasAny(tokens, "--org", "-o"))
            {
                if (organization is null || !OrganizationName.IsMatch(organization))
                    return "--org requires a valid organization login.";
                if (environmentName is not null || explicitRepositories.Count > 0)
                    return "Organization target conflicts with repository or environment target.";
            }
            if (command == "secret" && HasAny(tokens, "--app", "-a") &&
                !string.Equals(OptionValue(tokens, "--app", "-a"), "actions", StringComparison.OrdinalIgnoreCase))
                return "Only Actions secrets are supported.";
            var category = command == "secret" ? "secrets" : "variables";
            if (organization is not null)
                requirements.Add(OrganizationRequirement(organization, category, level));
            else
            {
                if (selected is null)
                    return "Unable to determine the target repository.";
                requirements.Add(RepositoryRequirement(selected, environmentName is null ? category : "environments", level));
            }
            return null;
        }
        if (selected is null)
            return "Unable to determine the target repository.";
        if (command == "release")
        {
            var requiredPositionals = subcommand switch
            {
                "list" => 0,
                "view" or "download" => -1, // Optional tag.
                "create" or "edit" or "delete" or "delete-asset" or "upload" => 1,
                _ => -2
            };
            if (requiredPositionals == -2)
                return $"Unsupported gh release command: {subcommand}.";
            if (subcommand == "list" && positionals.Count != 0 ||
                subcommand is "view" or "download" && positionals.Count > 1 ||
                subcommand is "edit" or "delete" && positionals.Count != 1 ||
                subcommand == "delete-asset" && positionals.Count != 2 ||
                subcommand == "upload" && positionals.Count < 2 ||
                subcommand == "create" && positionals.Count < 1)
                return "Unexpected release command arguments.";
            if (positionals.Any(value => value.Length == 0 || Uri.TryCreate(value, UriKind.Absolute, out _)))
                return "Release targets must be names or local asset paths.";
            var level = subcommand is "list" or "view" or "download" ? "read" : "write";
            requirements.Add(RepositoryRequirement(selected, "contents", level));
            if (subcommand is "create" or "edit")
                requirements.Add(RepositoryRequirement(selected, "workflows", "write"));
            return null;
        }
        if (command is "pr" or "issue")
        {
            var maxPositionals = command == "issue" && subcommand == "transfer" ? 2 : subcommand is "list" or "create" or "status" ? 0 : 1;
            if (positionals.Count > maxPositionals)
                return "Unexpected positional arguments.";
            if (positionals.Count == 1 && Uri.TryCreate(positionals[0], UriKind.Absolute, out var targetUri) && targetUri.Scheme is "http" or "https")
            {
                if (!TryParseRepository(positionals[0], out var urlRepo) || !selected.Equals(urlRepo, StringComparison.OrdinalIgnoreCase))
                    return "The URL target conflicts with the selected repository.";
            }
        }
        if (HasAny(tokens, "--web", "-w", "--hostname", "--host", "--project", "--add-project", "--remove-project", "--delete-branch") ||
            command is "pr" or "issue" && HasAny(tokens, "--json", "--jq", "-q", "--template") ||
            command == "pr" && HasAny(tokens, "-d"))
            return "This flag is not supported by gh-harness.";

        if (command == "pr")
        {
            var level = subcommand switch
            {
                "list" or "view" or "diff" => "read",
                "create" or "close" or "comment" or "edit" or "lock" or "ready" or "reopen" or "review" or "unlock" => "write",
                "merge" => "merge",
                _ => null
            };
            if (level is null)
                return $"Unsupported gh pr command: {subcommand}.";
            if (subcommand == "create")
            {
                var head = OptionValue(tokens, "--head", "-H");
                if (string.IsNullOrWhiteSpace(head) || head.Contains(':') || head.StartsWith('-'))
                    return "pr create requires --head with a local branch name (no owner prefix).";
                if (HasAny(tokens, "--fill-first", "--fill-verbose", "--attach"))
                    return "pr create project and attachment flags are unsupported.";
            }
            if (subcommand == "merge" && HasAny(tokens, "--delete-branch", "-d", "--admin"))
                return "pr merge branch deletion and admin bypass are unsupported.";
            requirements.Add(RepositoryRequirement(selected, level == "merge" ? "contents" : "pullRequests", level == "merge" ? "write" : level));
            return null;
        }

        if (command == "issue")
        {
            var level = subcommand switch
            {
                "list" or "view" or "status" => "read",
                "create" or "edit" or "close" or "reopen" or "comment" or "delete" or "lock" or "unlock" or "pin" or "unpin" or "transfer" => "write",
                _ => null
            };
            if (level is null)
                return $"Unsupported gh issue command: {subcommand}.";
            requirements.Add(RepositoryRequirement(selected, "issues", level));
            if (subcommand == "transfer")
            {
                if (positionals.Count < 2 || !TryParseRepository(positionals[^1], out var destination))
                    return "issue transfer requires an explicit destination OWNER/REPO.";
                requirements.Add(RepositoryRequirement(destination, "issues", "write"));
            }
            return null;
        }

        var actionLevel = command switch
        {
            "run" => subcommand switch
            {
                "list" or "view" or "watch" or "download" => "read",
                "rerun" or "cancel" or "delete" => "write",
                _ => null
            },
            "workflow" => subcommand switch
            {
                "list" or "view" => "read",
                "run" or "enable" or "disable" => "write",
                _ => null
            },
            "cache" => subcommand switch
            {
                "list" => "read",
                "delete" => "write",
                _ => null
            },
            _ => null
        };
        if (actionLevel is null)
            return $"Unsupported gh {command} command: {subcommand}.";
        requirements.Add(RepositoryRequirement(selected, "actions", actionLevel));
        return null;
    }

    private static string? ClassifyApi(List<string> tokens, string? selected, List<TargetRequirement> requirements, out string? repository)
    {
        repository = null;
        if (HasAny(tokens, "--input", "--hostname", "--host"))
            return "gh api input and host overrides are unsupported.";
        var method = OptionValue(tokens, "--method", "-X")?.ToUpperInvariant();
        if (method is not null && method is not ("GET" or "POST" or "PATCH" or "PUT" or "DELETE"))
            return "Unsupported API method.";
        method ??= HasAny(tokens, "-f", "-F", "--raw-field", "--field") ? "POST" : "GET";
        if (method != "GET" && HasAny(tokens, "--paginate"))
            return "Pagination of write requests is unsupported.";
        var positionals = PositionalArguments(tokens, 1);
        if (positionals.Count != 1)
            return "gh api requires exactly one REST endpoint.";
        var endpoint = positionals[0];
        if (endpoint.StartsWith("graphql", StringComparison.OrdinalIgnoreCase) || endpoint.Contains("/graphql", StringComparison.OrdinalIgnoreCase))
            return "GraphQL is unsupported.";
        if (endpoint.Contains('#') || endpoint.Contains('\\'))
            return "The REST path is malformed.";
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            if (uri.Host != "api.github.com" || uri.Scheme != "https")
                return "Only github.com REST endpoints are supported.";
            const string prefix = "https://api.github.com";
            if (!endpoint.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return "Only github.com REST endpoints are supported.";
            endpoint = endpoint[prefix.Length..];
        }
        endpoint = endpoint.Split('?', 2)[0];
        if (endpoint.Contains('%') || endpoint.Contains("//", StringComparison.Ordinal) ||
            endpoint.Split('/').Any(segment => segment is "." or ".."))
            return "The REST path is malformed or unsupported.";
        if (endpoint.TrimStart('/').Equals("user/emails", StringComparison.OrdinalIgnoreCase))
        {
            if (method != "GET") return "The REST endpoint is unsupported.";
            requirements.Add(new TargetRequirement(new TargetId("github.com", "account", "@authenticated"), new PermissionKey("account", "emails"), "read"));
            return null;
        }
        var orgMatch = ApiOrganizationActionsPath.Match(endpoint);
        if (orgMatch.Success)
        {
            var organization = orgMatch.Groups["org"].Value;
            if (!OrganizationName.IsMatch(organization)) return "Invalid organization login.";
            var family = orgMatch.Groups["family"].Value.ToLowerInvariant();
            var orgTail = orgMatch.Groups["tail"].Value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            var orgCategory = family == "secrets" ? "secrets" : "variables";
            var orgLevel = orgTail.Length == 0 && method == "GET" || orgTail.Length == 1 && method == "GET" ? "read"
                : orgTail.Length == 1 && method == "DELETE" ? "write"
                : null;
            if (orgLevel is null || orgTail.Length == 1 &&
                (orgTail[0] == "public-key" && (family == "variables" || method != "GET") ||
                 orgTail[0].Contains('{') || orgTail[0].Contains('}')) || orgTail.Length > 1)
                return "The REST endpoint is unsupported.";
            requirements.Add(OrganizationRequirement(organization, orgCategory, orgLevel));
            return null;
        }
        if (!endpoint.StartsWith("repos/", StringComparison.OrdinalIgnoreCase) && !endpoint.StartsWith("/repos/", StringComparison.OrdinalIgnoreCase))
            return "The REST path is malformed or unsupported.";
        var match = ApiRepositoryPath.Match(endpoint);
        if (!match.Success || !TryParseRepository($"{match.Groups["owner"].Value}/{match.Groups["repo"].Value}", out var apiRepo))
            return "Only repository-scoped REST endpoints are supported.";
        repository = apiRepo;
        var tail = match.Groups["tail"].Value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (tail.Length == 0)
            return "The REST endpoint is unsupported.";

        static bool IsId(string value) => value.Length > 0 && !value.Contains('{') && !value.Contains('}');
        static bool IsNumericId(string value) => value.Length > 0 && value.All(char.IsAsciiDigit);
        string? category = null;
        string? level = null;
        bool issueOrPullRequest = false;

        if (tail[0].Equals("pulls", StringComparison.OrdinalIgnoreCase))
        {
            if ((tail.Length == 1 && method is "GET" or "POST") || (tail.Length == 2 && IsNumericId(tail[1]) && method is "GET" or "PATCH"))
            {
                category = "pullRequests";
                level = method == "GET" ? "read" : "write";
            }
            else if (tail.Length == 2 && tail[1] == "comments" && method == "GET" ||
                     tail.Length == 3 && tail[1] == "comments" && IsNumericId(tail[2]) && method is "GET" or "PATCH" or "DELETE" ||
                     tail.Length == 3 && IsNumericId(tail[1]) && tail[2] == "comments" && method is "GET" or "POST" ||
                     tail.Length == 5 && IsNumericId(tail[1]) && tail[2] == "comments" && IsNumericId(tail[3]) && tail[4] == "replies" && method == "POST" ||
                     tail.Length == 5 && IsNumericId(tail[1]) && tail[2] == "reviews" && IsNumericId(tail[3]) && tail[4] == "comments" && method == "GET")
            {
                category = "pullRequests";
                level = method == "GET" ? "read" : "write";
            }
            else if (tail.Length == 3 && IsNumericId(tail[1]) && tail[2] is "commits" or "files" or "merge" or "requested_reviewers" or "reviews" && method == "GET" ||
                     tail.Length == 3 && IsNumericId(tail[1]) && tail[2] == "requested_reviewers" && method is "POST" or "DELETE" ||
                     tail.Length == 3 && IsNumericId(tail[1]) && tail[2] == "reviews" && method == "POST" ||
                     tail.Length == 4 && IsNumericId(tail[1]) && tail[2] == "reviews" && IsNumericId(tail[3]) && method is "GET" or "PUT" or "DELETE" ||
                     tail.Length == 5 && IsNumericId(tail[1]) && tail[2] == "reviews" && IsNumericId(tail[3]) && tail[4] == "events" && method == "POST" ||
                     tail.Length == 5 && IsNumericId(tail[1]) && tail[2] == "reviews" && IsNumericId(tail[3]) && tail[4] == "dismissals" && method == "PUT" ||
                     tail.Length == 3 && IsNumericId(tail[1]) && tail[2] == "update-branch" && method == "PUT")
            {
                category = "pullRequests";
                level = method == "GET" ? "read" : "write";
            }
            else if (tail.Length == 3 && IsNumericId(tail[1]) && tail[2] == "merge" && method == "PUT")
            {
                category = "contents";
                level = "write";
            }
        }
        else if (tail[0].Equals("issues", StringComparison.OrdinalIgnoreCase))
        {
            if ((tail.Length == 1 && method is "GET" or "POST") || (tail.Length == 2 && IsNumericId(tail[1]) && method is "GET" or "PATCH"))
            {
                category = "issues";
                level = method == "GET" ? "read" : "write";
                issueOrPullRequest = tail.Length == 2 && method == "PATCH";
            }
            else if (tail.Length == 3 && IsNumericId(tail[1]) && tail[2] == "comments" && method is "GET" or "POST" ||
                     tail.Length == 2 && tail[1] == "comments" && method == "GET" ||
                     tail.Length == 3 && tail[1] == "comments" && IsNumericId(tail[2]) && method is "GET" or "PATCH" or "DELETE" ||
                     tail.Length == 3 && IsNumericId(tail[1]) && tail[2] == "labels" && method is "GET" or "POST" or "PUT" or "DELETE" ||
                     tail.Length == 4 && IsNumericId(tail[1]) && tail[2] == "labels" && IsId(tail[3]) && method == "DELETE")
            {
                category = "issues";
                level = method == "GET" ? "read" : "write";
                issueOrPullRequest = true;
            }
        }
        else if (tail[0].Equals("labels", StringComparison.OrdinalIgnoreCase) &&
                 (tail.Length == 1 && method is "GET" or "POST" ||
                  tail.Length == 2 && IsId(tail[1]) && method is "GET" or "PATCH" or "DELETE"))
        {
            category = "issues";
            level = method == "GET" ? "read" : "write";
            issueOrPullRequest = true;
        }
        else if (tail[0].Equals("milestones", StringComparison.OrdinalIgnoreCase) &&
                 (tail.Length == 1 && method is "GET" or "POST" ||
                  tail.Length == 2 && IsNumericId(tail[1]) && method is "GET" or "PATCH" or "DELETE" ||
                  tail.Length == 3 && IsNumericId(tail[1]) && tail[2] == "labels" && method == "GET"))
        {
            category = "issues";
            level = method == "GET" ? "read" : "write";
            issueOrPullRequest = true;
        }
        else if (tail[0].Equals("commits", StringComparison.OrdinalIgnoreCase) &&
                 tail.Length == 3 && IsId(tail[1]) && tail[2] is "status" or "statuses" && method == "GET")
        {
            category = "commitStatuses";
            level = "read";
        }
        else if (tail[0].Equals("statuses", StringComparison.OrdinalIgnoreCase) &&
                 tail.Length == 2 && IsId(tail[1]) && method == "POST")
        {
            category = "commitStatuses";
            level = "write";
        }
        else if (tail[0].Equals("releases", StringComparison.OrdinalIgnoreCase))
        {
            if (tail.Length == 1 && method is "GET" or "POST" ||
                tail.Length == 2 && tail[1] == "latest" && method == "GET" ||
                tail.Length == 3 && tail[1] == "tags" && IsId(tail[2]) && method == "GET" ||
                tail.Length == 2 && IsNumericId(tail[1]) && method is "GET" or "PATCH" or "DELETE" ||
                tail.Length == 3 && IsNumericId(tail[1]) && tail[2] == "assets" && method == "GET" ||
                tail.Length == 3 && tail[1] == "assets" && IsNumericId(tail[2]) && method is "GET" or "PATCH" or "DELETE" ||
                tail.Length == 2 && tail[1] == "generate-notes" && method == "POST")
            {
                category = "contents";
                level = method == "GET" ? "read" : "write";
                if (method is "POST" or "PATCH" && (tail.Length == 1 || tail[1] != "generate-notes") &&
                    HasAnyField(tokens, "discussion_category_name"))
                    return "Release discussions are unsupported.";
            }
        }
        else if (tail[0].Equals("actions", StringComparison.OrdinalIgnoreCase))
        {
            if (tail.Length >= 2 && tail[1] == "secrets" &&
                (tail.Length == 2 && method == "GET" ||
                 tail.Length == 3 && tail[2] == "public-key" && method == "GET" ||
                 tail.Length == 3 && IsId(tail[2]) && tail[2] != "public-key" && method is "GET" or "PUT" or "DELETE"))
            {
                category = "secrets";
                level = method == "GET" ? "read" : "write";
            }
            else if (tail.Length >= 2 && tail[1] == "variables" &&
                     (tail.Length == 2 && method is "GET" or "POST" ||
                      tail.Length == 3 && IsId(tail[2]) && method is "GET" or "PATCH" or "DELETE"))
            {
                category = "variables";
                level = method == "GET" ? "read" : "write";
            }
            else if (tail.Length == 2 && tail[1] is "workflows" or "runs" or "artifacts" or "caches" && method == "GET" ||
                tail.Length == 3 && tail[1] == "workflows" && IsId(tail[2]) && method == "GET" ||
                tail.Length == 3 && tail[1] is "runs" or "jobs" or "artifacts" && IsNumericId(tail[2]) && method == "GET" ||
                tail.Length == 3 && tail[1] == "cache" && tail[2] == "usage" && method == "GET" ||
                tail.Length == 4 && tail[1] == "jobs" && IsNumericId(tail[2]) && tail[3] == "logs" && method == "GET" ||
                tail.Length == 4 && tail[1] == "artifacts" && IsNumericId(tail[2]) && tail[3] == "zip" && method == "GET" ||
                tail.Length == 4 && tail[1] == "runs" && IsNumericId(tail[2]) && tail[3] is "approvals" or "artifacts" or "jobs" or "logs" or "pending_deployments" or "timing" && method == "GET" ||
                tail.Length == 4 && tail[1] == "workflows" && IsId(tail[2]) && tail[3] is "runs" or "timing" && method == "GET" ||
                tail.Length == 5 && tail[1] == "runs" && IsNumericId(tail[2]) && tail[3] == "attempts" && IsNumericId(tail[4]) && method == "GET" ||
                tail.Length == 6 && tail[1] == "runs" && IsNumericId(tail[2]) && tail[3] == "attempts" && IsNumericId(tail[4]) && tail[5] is "jobs" or "logs" && method == "GET")
            {
                category = "actions";
                level = "read";
            }
            else if (tail.Length == 4 && tail[1] == "runs" && IsNumericId(tail[2]) && tail[3] == "pending_deployments" && method == "POST")
            {
                category = "deployments";
                level = "write";
            }
            else if (tail.Length == 4 && tail[1] == "workflows" && IsId(tail[2]) && tail[3] == "dispatches" && method == "POST" ||
                     tail.Length == 4 && tail[1] == "workflows" && IsId(tail[2]) && tail[3] is "enable" or "disable" && method == "PUT" ||
                     tail.Length == 4 && tail[1] == "runs" && IsNumericId(tail[2]) && tail[3] is "rerun" or "rerun-failed-jobs" or "cancel" or "force-cancel" or "approve" && method == "POST" ||
                     tail.Length == 4 && tail[1] == "runs" && IsNumericId(tail[2]) && tail[3] == "logs" && method == "DELETE" ||
                     tail.Length == 4 && tail[1] == "jobs" && IsNumericId(tail[2]) && tail[3] == "rerun" && method == "POST" ||
                     tail.Length == 3 && tail[1] is "runs" or "artifacts" or "caches" && IsNumericId(tail[2]) && method == "DELETE" ||
                     tail.Length == 2 && tail[1] == "caches" && method == "DELETE")
            {
                category = "actions";
                level = "write";
            }
        }
        else if (tail[0].Equals("environments", StringComparison.OrdinalIgnoreCase) &&
                 tail.Length >= 3 && IsId(tail[1]) && tail[2] is "secrets" or "variables")
        {
            if (tail[2] == "secrets" &&
                (tail.Length == 3 && method == "GET" ||
                 tail.Length == 4 && tail[3] == "public-key" && method == "GET" ||
                 tail.Length == 4 && IsId(tail[3]) && tail[3] != "public-key" && method is "GET" or "PUT" or "DELETE") ||
                tail[2] == "variables" &&
                (tail.Length == 3 && method is "GET" or "POST" ||
                 tail.Length == 4 && IsId(tail[3]) && method is "GET" or "PATCH" or "DELETE"))
            {
                category = "environments";
                level = method == "GET" ? "read" : "write";
            }
        }
        else if (tail[0].Equals("contents", StringComparison.OrdinalIgnoreCase) && (tail.Length == 1 || tail.Skip(1).All(IsId)))
        {
            if (method == "GET" || tail.Length >= 2 && method is "PUT" or "DELETE")
            {
                category = "contents";
                level = method == "GET" ? "read" : "write";
            }
        }
        else if (tail[0].Equals("deployments", StringComparison.OrdinalIgnoreCase))
        {
            if (tail.Length == 1 && method is "GET" or "POST" ||
                tail.Length == 2 && IsNumericId(tail[1]) && method is "GET" or "DELETE" ||
                tail.Length == 3 && IsNumericId(tail[1]) && tail[2] == "statuses" && method is "GET" or "POST" ||
                tail.Length == 4 && IsNumericId(tail[1]) && tail[2] == "statuses" && IsNumericId(tail[3]) && method == "GET")
            {
                category = "deployments";
                level = method == "GET" ? "read" : "write";
            }
        }
        else if (tail[0].Equals("pages", StringComparison.OrdinalIgnoreCase))
        {
            if (tail.Length == 1 && method is "GET" or "POST" or "PUT" or "DELETE" ||
                tail.Length == 2 && tail[1] == "health" && method == "GET" ||
                tail.Length == 2 && tail[1] == "builds" && method is "GET" or "POST" ||
                tail.Length == 3 && tail[1] == "builds" && (tail[2] == "latest" || IsNumericId(tail[2])) && method == "GET" ||
                tail.Length == 2 && tail[1] == "deployments" && method == "POST" ||
                tail.Length == 3 && tail[1] == "deployments" && IsId(tail[2]) && method == "GET" ||
                tail.Length == 4 && tail[1] == "deployments" && IsId(tail[2]) && tail[3] == "cancel" && method == "POST")
            {
                category = "pages";
                level = method == "GET" && !(tail.Length == 2 && tail[1] == "health") ? "read" : "write";
            }
        }
        else if (tail[0].Equals("hooks", StringComparison.OrdinalIgnoreCase))
        {
            if (tail.Length == 1 && method is "GET" or "POST" ||
                tail.Length == 2 && IsNumericId(tail[1]) && method is "GET" or "PATCH" or "DELETE" ||
                tail.Length == 3 && IsNumericId(tail[1]) && tail[2] == "config" && method is "GET" or "PATCH" ||
                tail.Length == 3 && IsNumericId(tail[1]) && tail[2] == "deliveries" && method == "GET" ||
                tail.Length == 4 && IsNumericId(tail[1]) && tail[2] == "deliveries" && IsNumericId(tail[3]) && method == "GET" ||
                tail.Length == 5 && IsNumericId(tail[1]) && tail[2] == "deliveries" && IsNumericId(tail[3]) && tail[4] == "attempts" && method == "POST" ||
                tail.Length == 3 && IsNumericId(tail[1]) && tail[2] is "pings" or "tests" && method == "POST")
            {
                category = "webhooks";
                level = method == "GET" || tail.Length == 3 && tail[2] is "pings" or "tests" ? "read" : "write";
            }
        }
        else if (tail[0].Equals("dependabot", StringComparison.OrdinalIgnoreCase) &&
                 tail.Length >= 2 && tail[1] == "alerts" &&
                 (tail.Length == 2 && method == "GET" || tail.Length == 3 && IsNumericId(tail[2]) && method is "GET" or "PATCH"))
        {
            category = "dependabotAlerts";
            level = method == "GET" ? "read" : "write";
        }
        else if (tail[0].Equals("code-scanning", StringComparison.OrdinalIgnoreCase))
        {
            if (tail.Length == 2 && tail[1] is "ai-scan" or "alerts" or "analyses" && method == "GET" ||
                tail.Length == 3 && tail[1] == "alerts" && IsNumericId(tail[2]) && method is "GET" or "PATCH" ||
                tail.Length == 4 && tail[1] == "alerts" && IsNumericId(tail[2]) && tail[3] == "autofix" && method is "GET" or "POST" ||
                tail.Length == 4 && tail[1] == "alerts" && IsNumericId(tail[2]) && tail[3] == "instances" && method == "GET" ||
                tail.Length == 3 && tail[1] == "analyses" && IsNumericId(tail[2]) && method is "GET" or "DELETE" ||
                tail.Length == 2 && tail[1] == "sarifs" && method == "POST" ||
                tail.Length == 3 && tail[1] == "sarifs" && IsId(tail[2]) && method == "GET")
            {
                category = "codeScanningAlerts";
                level = method == "GET" ? "read" : "write";
            }
        }
        else if (tail[0].Equals("secret-scanning", StringComparison.OrdinalIgnoreCase))
        {
            if (tail.Length == 2 && tail[1] is "alerts" or "scan-history" && method == "GET" ||
                tail.Length == 3 && tail[1] == "alerts" && IsNumericId(tail[2]) && method is "GET" or "PATCH" ||
                tail.Length == 4 && tail[1] == "alerts" && IsNumericId(tail[2]) && tail[3] == "locations" && method == "GET")
            {
                category = "secretScanningAlerts";
                level = method == "GET" ? "read" : "write";
            }
        }
        else if (tail[0].Equals("security-advisories", StringComparison.OrdinalIgnoreCase))
        {
            if (tail.Length == 1 && method is "GET" or "POST" ||
                tail.Length == 2 && tail[1] == "reports" && method == "POST" ||
                tail.Length == 2 && IsId(tail[1]) && tail[1] != "reports" && method is "GET" or "PATCH" ||
                tail.Length == 3 && IsId(tail[1]) && tail[1] != "reports" && tail[2] is "cve" or "forks" && method == "POST")
            {
                category = "repositorySecurityAdvisories";
                level = method == "GET" || tail.Length == 3 && tail[2] == "forks" ? "read" : "write";
            }
        }

        if (category is null || level is null)
            return $"REST endpoint or method is unsupported: {method} {endpoint}.";
        const string issuePermissionAlternative = "issue-or-pull-request";
        requirements.Add(RepositoryRequirement(apiRepo, category, level, issueOrPullRequest ? issuePermissionAlternative : null));
        if (issueOrPullRequest)
            requirements.Add(RepositoryRequirement(apiRepo, "pullRequests", level, issuePermissionAlternative));
        if (category == "contents" && level == "write" &&
            tail.Length >= 4 && tail[1] == ".github" && tail[2] == "workflows")
            requirements.Add(RepositoryRequirement(apiRepo, "workflows", "write"));
        if (tail[0].Equals("releases", StringComparison.OrdinalIgnoreCase) &&
            (tail.Length == 1 && method == "POST" || tail.Length == 2 && IsNumericId(tail[1]) && method == "PATCH"))
            requirements.Add(RepositoryRequirement(apiRepo, "workflows", "write"));
        if (category == "pages" && (tail.Length == 1 && method is "POST" or "PUT" or "DELETE" ||
            tail.Length == 2 && tail[1] == "health" && method == "GET") ||
            category == "repositorySecurityAdvisories" && tail.Length == 3 && tail[2] == "forks")
            requirements.Add(RepositoryRequirement(apiRepo, "administration", "write"));
        return null;
    }

    private static bool HasAnyField(List<string> tokens, string field) =>
        EnumerateFlags(tokens).Any(option => option.Name is "-f" or "-F" or "--field" or "--raw-field" &&
            option.Value?.Split('=', 2)[0] == field);

    private static List<string> PositionalArguments(List<string> tokens, int start)
    {
        var result = new List<string>();
        for (var i = start; i < tokens.Count; i++)
        {
            var value = tokens[i];
            if (value == "--")
            {
                result.AddRange(tokens.Skip(i + 1));
                break;
            }
            if (value.StartsWith('-'))
            {
                if (value.Contains('='))
                    continue;
                if (FlagTakesValue(value) && i + 1 < tokens.Count)
                    i++;
                continue;
            }
            result.Add(value);
        }
        return result;
    }

    private static bool FlagTakesValue(string flag) => flag is "--env" or "-e" or "--org" or "-o" or "--app" or "--head" or "-H" or "--base" or "-B" or "--title" or "-t" or "--body" or "-b" or "--body-file" or "-F" or "--assignee" or "-a" or "--label" or "-l" or "--milestone" or "-m" or "--reviewer" or "-r" or "--source" or "--method" or "-X" or "--field" or "-f" or "--raw-field" or "--jq" or "-q" or "--template" or "--limit" or "-L" or "--state" or "-s" or "--search" or "-S" or "--author" or "-A" or "--branch" or "--workflow" or "--ref" or "--commit" or "--subject" or "--message" or "--match" or "--json" or "--user" or "--event" or "--status" or "--created" or "--exclude" or "--include" or "--name" or "--key" or "--timeout" or "--interval" or "--reason" or "--job" or "--dir" or "-D" or "--pattern" or "-p" or "--cache" or "--match-head-commit" or "--add-assignee" or "--remove-assignee" or "--add-label" or "--remove-label" or "--add-reviewer" or "--remove-reviewer" or "-n" or "--notes" or "--notes-file" or "--target" or "--tag" or "--order" or "-O" or "--archive" or "--output";

    private static bool ValidateFlags(List<string> tokens, string command, string subcommand, out string? error)
    {
        error = null;
        if (tokens.Count < 2)
            return true;
        if (command == "pr" && subcommand == "merge" && HasAny(tokens, "-m", "-s", "-r", "-a"))
        {
            error = "Short merge strategy flags are unsupported; use --merge, --squash, or --rebase.";
            return false;
        }
        if (tokens.Contains("--"))
        {
            error = "Option pass-through is unsupported.";
            return false;
        }

        var common = new HashSet<string>(["--json", "--jq", "-q", "--template", "--verbose"], StringComparer.Ordinal);
        var allowed = new HashSet<string>(common, StringComparer.Ordinal);
        switch (command)
        {
            case "api":
                allowed.UnionWith(["-X", "--method", "-f", "-F", "--field", "--raw-field", "--paginate", "--slurp", "--cache"]);
                break;
            case "pr":
                allowed.UnionWith(["--limit", "-L", "--state", "-s", "--search", "-S", "--author", "-A", "--base", "-B", "--head", "-H", "--draft", "--assignee", "-a", "--label", "-l", "--milestone", "-m", "--title", "-t", "--body", "-b", "--body-file", "-F", "--reviewer", "-r", "--fill", "--fill-first", "--fill-verbose", "--project", "--attach", "--add-project", "--remove-project", "--web", "-w", "--merge", "--squash", "--rebase", "--auto", "--disable-auto", "--delete-branch", "-d", "--admin", "--subject", "--match-head-commit", "--approve", "--request-changes", "--dismiss", "--add-assignee", "--remove-assignee", "--add-label", "--remove-label", "--add-reviewer", "--remove-reviewer"]);
                break;
            case "issue":
                allowed.UnionWith(["--limit", "-L", "--state", "-s", "--search", "-S", "--author", "-A", "--assignee", "-a", "--label", "-l", "--milestone", "-m", "--title", "-t", "--body", "-b", "--body-file", "-F", "--project", "--add-project", "--remove-project", "--web", "-w", "--yes", "--reason", "--add-assignee", "--remove-assignee", "--add-label", "--remove-label"]);
                break;
            case "repo":
                allowed.UnionWith(["--source", "--branch", "-b", "--include-archive", "--force", "--default", "--web", "-w"]);
                break;
            case "secret":
                allowed.Clear();
                allowed.UnionWith(subcommand switch
                {
                    "list" => ["--env", "-e", "--org", "-o", "--app", "-a", "--json", "--jq", "-q", "--template", "-t"],
                    "set" => ["--env", "-e", "--org", "-o", "--app", "-a", "--body", "-b"],
                    "delete" => ["--env", "-e", "--org", "-o", "--app", "-a"],
                    _ => []
                });
                break;
            case "variable":
                allowed.Clear();
                allowed.UnionWith(subcommand switch
                {
                    "list" or "get" => ["--env", "-e", "--org", "-o", "--json", "--jq", "-q", "--template", "-t"],
                    "set" => ["--env", "-e", "--org", "-o", "--body", "-b"],
                    "delete" => ["--env", "-e", "--org", "-o"],
                    _ => []
                });
                break;
            case "release":
                allowed.Clear();
                allowed.UnionWith(subcommand switch
                {
                    "list" => ["--exclude-drafts", "--exclude-pre-releases", "--limit", "-L", "--order", "-O", "--json", "--jq", "-q", "--template", "-t"],
                    "view" => ["--json", "--jq", "-q", "--template", "-t"],
                    "download" => ["--archive", "-A", "--clobber", "--dir", "-D", "--output", "-O", "--pattern", "-p", "--skip-existing"],
                    "create" => ["--draft", "--fail-on-no-commits", "--generate-notes", "--latest", "--notes", "-n", "--notes-file", "-F", "--notes-from-tag", "--prerelease", "--target", "--title", "-t"],
                    "edit" => ["--draft", "--latest", "--notes", "-n", "--notes-file", "-F", "--prerelease", "--tag", "--target", "--title", "-t"],
                    "delete" or "delete-asset" => ["--yes", "-y"],
                    "upload" => ["--clobber"],
                    _ => []
                });
                break;
            case "run":
            case "workflow":
            case "cache":
                allowed.UnionWith(["--limit", "-L", "--workflow", "--branch", "-b", "--event", "--status", "--user", "--created", "--interval", "--exit-status", "--log", "--log-failed", "--job", "--dir", "-D", "--name", "-n", "--pattern", "-p", "--archive", "--force", "--all", "--confirm", "--yes", "--ref", "-r", "--field", "-f", "--raw-field", "-F", "--web", "-w"]);
                break;
            default:
                return true;
        }

        for (var i = command == "api" ? 1 : 2; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith('-'))
                continue;
            var name = token.Split('=', 2)[0];
            if (!allowed.Contains(name))
            {
                error = $"Unsupported flag: {name}.";
                return false;
            }
            if (token == name && FlagTakesValue(name))
            {
                if (++i >= tokens.Count)
                {
                    error = $"{name} requires a value.";
                    return false;
                }
            }
            else if (command == "release" && token != name && !FlagTakesValue(name) &&
                     (name == "--latest"
                         ? token[(name.Length + 1)..] is not ("true" or "false" or "legacy")
                         : name is not ("--draft" or "--prerelease") || token[(name.Length + 1)..] is not ("true" or "false")))
            {
                error = $"Unsupported flag value: {name}.";
                return false;
            }
        }
        return true;
    }

    private static bool HasAny(List<string> tokens, params string[] flags) => EnumerateFlags(tokens).Any(option => flags.Contains(option.Name));

    private static string? OptionValue(List<string> tokens, params string[] flags)
    {
        return EnumerateFlags(tokens).FirstOrDefault(option => flags.Contains(option.Name)).Value;
    }

    private static IEnumerable<(string Name, string? Value)> EnumerateFlags(List<string> tokens)
    {
        for (var i = tokens.Count > 0 && tokens[0] == "api" ? 1 : 2; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith('-'))
                continue;
            var equals = token.IndexOf('=');
            if (equals >= 0)
            {
                yield return (token[..equals], token[(equals + 1)..]);
                continue;
            }
            var value = FlagTakesValue(token) && i + 1 < tokens.Count ? tokens[++i] : null;
            yield return (token, value);
        }
    }

    private static bool TryParseRepository(string input, out string repository)
    {
        repository = "";
        if (string.IsNullOrWhiteSpace(input))
            return false;
        var value = input.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            if (!uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Port != (uri.Scheme == "https" ? 443 : 80))
                return false;
            value = uri.AbsolutePath.Trim('/');
            var segments = value.Split('/');
            if (segments.Length < 2)
                return false;
            value = $"{segments[0]}/{segments[1]}";
        }
        else if (value.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            value = value[15..];
        else if (value.StartsWith("ssh://git@github.com/", StringComparison.OrdinalIgnoreCase))
            value = value[21..];
        else if (value.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
            value = value[11..];
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            value = value[..^4];
        if (!RepositoryName.IsMatch(value) || value.Split('/').Any(part => part is "." or ".."))
            return false;
        repository = value;
        return true;
    }

    private static string? RunGit(string workingDirectory, params string[] arguments)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                process.StartInfo.ArgumentList.Add(argument);
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000) || process.ExitCode != 0)
            {
                if (!process.HasExited)
                    process.Kill();
                return null;
            }
            return output.Trim();
        }
        catch
        {
            return null;
        }
    }
}
