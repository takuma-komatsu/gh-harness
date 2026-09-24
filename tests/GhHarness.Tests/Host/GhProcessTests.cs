using GhHarness;
using Xunit;

namespace GhHarness.Tests.Host;

public sealed class GhProcessTests
{
    [Fact]
    public void FindsAbsoluteExecutableOnPathAndSkipsEmptyEntries()
    {
        var directory = CreateTempDirectory();
        try
        {
            var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "gh.exe" : "gh");
            File.WriteAllText(executable, "fixture");

            var path = string.Join(Path.PathSeparator, "", directory, "");
            Assert.Equal(executable, GhProcess.FindExecutable(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PreservesArgumentBoundariesEnvironmentAndExitCode()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = CreateTempDirectory();
        try
        {
            var executable = Path.Combine(directory, "gh");
            var resultFile = Path.Combine(directory, "result.txt");
            File.WriteAllText(executable, "#!/bin/sh\nprintf 'repo=%s\\n' \"$GH_REPO\" > \"$GH_HARNESS_TEST_RESULT\"\nprintf 'host=%s\\n' \"$GH_HOST\" >> \"$GH_HARNESS_TEST_RESULT\"\nfor arg in \"$@\"; do printf 'arg=%s\\n' \"$arg\" >> \"$GH_HARNESS_TEST_RESULT\"; done\nexit 37\n");
            File.SetUnixFileMode(executable,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var exitCode = await GhProcess.RunAsync(executable,
                ["pr", "view", "title with spaces", "$(touch should-not-exist)"],
                new Dictionary<string, string?>
                {
                    ["GH_REPO"] = "acme/example",
                    ["GH_HOST"] = "github.com",
                    ["GH_HARNESS_TEST_RESULT"] = resultFile,
                });

            Assert.Equal(37, exitCode);
            Assert.Equal(
                ["repo=acme/example", "host=github.com", "arg=pr", "arg=view", "arg=title with spaces", "arg=$(touch should-not-exist)"],
                File.ReadAllLines(resultFile));
            Assert.False(File.Exists(Path.Combine(Environment.CurrentDirectory, "should-not-exist")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "gh-harness test " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
