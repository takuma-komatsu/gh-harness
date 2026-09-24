using System.ComponentModel;
using System.Collections;
using GhHarness.Commands;
using GhHarness.Policy;

namespace GhHarness;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var explain = args.Length > 0 && args[0] == "--explain";
        var ghArguments = explain ? args[1..] : args;
        if (ghArguments.Length == 0)
        {
            Console.Error.WriteLine("Usage: gh-harness [--explain] <gh arguments>");
            return 2;
        }

        try
        {
            var executable = GhProcess.FindExecutable();
            if (IsHelpOrVersion(ghArguments))
            {
                if (explain)
                {
                    Console.WriteLine("Allowed: GitHub CLI help/version (no repository operation).");
                    return 0;
                }
                return await RunGhAsync(executable, ghArguments, new Dictionary<string, string?>());
            }

            var environment = Environment.GetEnvironmentVariables()
                .Cast<DictionaryEntry>()
                .Where(entry => entry.Key is string)
                .ToDictionary(entry => (string)entry.Key, entry => entry.Value?.ToString(),
                    OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var cwd = Environment.CurrentDirectory;
            var classification = CommandClassifier.Classify(ghArguments, cwd, environment);
            if (!classification.AllowedToEvaluate)
            {
                Console.Error.WriteLine($"gh-harness: denied: {classification.Error ?? "command cannot be classified"}");
                return 2;
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
                throw new InvalidOperationException("Cannot locate the user home directory.");

            var localConfig = classification.RepositoryRoot is null
                ? null
                : Path.Combine(classification.RepositoryRoot, ".gh-harness.json");
            var policy = PolicyStore.Load(home, localConfig, classification.LocalRepository);
            var profile = PatProfileStore.Load(home);
            var requirements = classification.Requirements.ToArray();
            CredentialBinding? binding = null;
            if (profile is not null || requirements.Any(requirement => requirement.Target.Kind == "account"))
            {
                binding = await CredentialBinding.ResolveAsync(executable, environment);
                if (binding is null)
                {
                    Console.Error.WriteLine("gh-harness: denied: could not resolve the GitHub CLI credential");
                    return 2;
                }
            }
            if (requirements.Any(requirement => requirement.Target.Kind == "account"))
            {
                var login = await binding!.GetAuthenticatedLoginAsync(executable!, environment);
                if (login is null)
                {
                    Console.Error.WriteLine("gh-harness: denied: could not verify the authenticated account");
                    return 2;
                }
                requirements = requirements.Select(requirement =>
                    requirement.Target.Kind == "account" && requirement.Target.Name == "@authenticated"
                        ? requirement with { Target = requirement.Target with { Name = login } }
                        : requirement).ToArray();
                if (requirements.Any(requirement => requirement.Target.Kind == "account" &&
                    !string.Equals(requirement.Target.Name, login, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.Error.WriteLine("gh-harness: denied: account target differs from the authenticated account");
                    return 2;
                }
            }
            var allowed = true;
            foreach (var group in requirements.GroupBy(
                requirement => $"{requirement.Target.Host}\u001f{requirement.Target.Kind}\u001f{requirement.Target.Name}",
                StringComparer.OrdinalIgnoreCase))
            {
                var target = group.First().Target;
                var needs = group.Select(requirement => new PermissionNeed(requirement.Permission, requirement.Level, requirement.AlternativeGroup)).ToArray();
                var decision = policy.Evaluate(target, needs);
                allowed &= decision.Allowed;
                if (explain || !decision.Allowed)
                {
                    var destination = explain ? Console.Out : Console.Error;
                    destination.WriteLine($"{(decision.Allowed ? "Allowed" : "Denied")}: {target.Kind}:{target.Name}");
                    var required = needs.Where(need => need.AlternativeGroup is null)
                        .Select(need => $"{need.Key.Namespace}.{need.Key.Name}:{need.Level}")
                        .Concat(needs.Where(need => need.AlternativeGroup is not null)
                            .GroupBy(need => need.AlternativeGroup)
                            .Select(alternatives => $"({string.Join(" or ", alternatives.Select(need => $"{need.Key.Namespace}.{need.Key.Name}:{need.Level}"))})"));
                    destination.WriteLine($"  Required: {string.Join(", ", required)}");
                    destination.WriteLine($"  Reason: {decision.Reason}");
                    if (explain)
                    {
                        foreach (var trace in decision.Trace)
                        {
                            var permissions = string.Join(", ", trace.Permissions.Select(permission => $"{permission.Key.Namespace}.{permission.Key.Name}:{permission.Value}"));
                            destination.WriteLine($"  Match: {trace.Source} [{trace.Scope}{(trace.Pattern is null ? "" : $":{trace.Pattern}")}]" +
                                $" access={trace.Access ?? "inherited"} permissions={permissions}");
                        }
                    }
                }
            }

            if (requirements.Length == 0)
            {
                Console.Error.WriteLine("gh-harness: denied: command has no policy requirements");
                return 2;
            }
            if (!allowed)
                return 2;
            if (profile is not null)
            {
                var profileDecision = await profile.EvaluateAsync(requirements, binding!, executable!, environment);
                if (explain)
                    Console.WriteLine($"PAT profile: {profileDecision.Reason}");
                if (!profileDecision.Allowed)
                {
                    if (!explain)
                        Console.Error.WriteLine($"gh-harness: denied: {profileDecision.Reason}");
                    return 2;
                }
            }
            if (explain)
                return 0;

            var childEnvironment = new Dictionary<string, string?>(classification.ChildEnvironmentOverrides);
            if (binding is not null)
                foreach (var (name, value) in binding.ChildEnvironmentOverrides)
                    childEnvironment[name] = value;
            return await RunGhAsync(executable, ghArguments, childEnvironment);
        }
        catch (PolicyConfigurationException ex)
        {
            Console.Error.WriteLine($"gh-harness: invalid configuration: {ex.Message}");
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            Console.Error.WriteLine($"gh-harness: {ex.Message}");
            return 1;
        }
    }

    private static bool IsHelpOrVersion(IReadOnlyList<string> args)
    {
        if (args.Count == 0) return false;
        if (args[0] is "help" or "version") return true;
        if (args.Count == 1) return args[0] is "--help" or "-h" or "--version";

        // Only bare command paths may use this shortcut. A help-looking token after
        // an option could be its value (for example, --body --help).
        return args.Count <= 3 && args.SkipLast(1).All(arg => !arg.StartsWith('-')) &&
            args[^1] is "--help" or "-h";
    }

    private static async Task<int> RunGhAsync(
        string? executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environmentOverrides)
    {
        if (executable is null)
        {
            Console.Error.WriteLine("gh-harness: cannot find gh on PATH");
            return 127;
        }
        return await GhProcess.RunAsync(executable, arguments, environmentOverrides);
    }
}
