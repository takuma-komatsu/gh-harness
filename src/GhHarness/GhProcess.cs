using System.ComponentModel;
using System.Diagnostics;

namespace GhHarness;

/// <summary>Starts the real GitHub CLI with the original argument boundaries intact.</summary>
public static class GhProcess
{
    public static string? FindExecutable(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return null;

        var fileName = OperatingSystem.IsWindows() ? "gh.exe" : "gh";
        foreach (var entry in path.Split(Path.PathSeparator))
        {
            // An empty PATH component denotes the current directory on some shells. Never
            // execute an untrusted gh from the checkout implicitly.
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            try
            {
                var candidate = Path.GetFullPath(Path.Combine(entry.Trim('"'), fileName));
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ignore malformed PATH entries and keep looking.
            }
        }

        return null;
    }

    public static async Task<int> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environmentOverrides,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(executable),
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (var (name, value) in environmentOverrides)
        {
            if (value is null)
                startInfo.Environment.Remove(name);
            else
                startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new Win32Exception("Could not start GitHub CLI.");

        // Standard input, output, and error are inherited so TTY prompts, colors and
        // streaming output behave like the underlying gh command.
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }
}
