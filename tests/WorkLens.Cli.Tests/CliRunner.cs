using System.Text;
using System.Text.Json.Nodes;
using WorkLens.Tests.Shared;

namespace WorkLens.Cli.Tests;

/// <summary>
/// Runs <c>worklens</c> commands in-process on a test <see cref="CliHost"/>: captured stdout / stderr, a fixed
/// clock, recorded retry delays, fixture HTTP, and temporary workspace and home directories that are deleted on
/// dispose.
/// </summary>
internal sealed class CliRunner : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "worklens-cli-tests", Guid.NewGuid().ToString("N"));

    public CliRunner()
    {
        Workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        Home = Directory.CreateDirectory(Path.Combine(_root, "home")).FullName;
        CurrentDirectory = Workspace;
    }

    public string Workspace { get; }

    public string Home { get; }

    /// <summary>The host's current directory; defaults to <see cref="Workspace"/>.</summary>
    public string CurrentDirectory { get; set; }

    /// <summary>The only environment variables the command sees.</summary>
    public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);

    public FixtureHandler Http { get; set; } = new();

    /// <summary>Replaces <see cref="Http"/> as the host's HTTP handler, for example to throw an unexpected exception.</summary>
    public Func<HttpMessageHandler>? CreateHttpHandler { get; set; }

    public TimeProvider Clock { get; set; } = new FixedTimeProvider(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));

    public List<TimeSpan> Delays { get; } = [];

    public IGitProbe GitProbe { get; set; } = new FakeGitProbe(GitIgnoreStatus.Unknown);

    public Task<CliRun> RunAsync(params string[] args) => RunAsync(CancellationToken.None, args);

    public async Task<CliRun> RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        using var stdout = new MemoryStream();
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        await using var outWriter = new StreamWriter(stdout, utf8, leaveOpen: true) { AutoFlush = true };
        await using var errorWriter = new StringWriter();
        var host = new CliHost
        {
            StdoutStream = stdout,
            Out = outWriter,
            Error = errorWriter,
            GetEnvironmentVariable = name => Environment.GetValueOrDefault(name),
            CreateHttpHandler = CreateHttpHandler ?? (() => Http),
            TimeProvider = Clock,
            Delay = (delay, _) =>
            {
                lock (Delays)
                {
                    Delays.Add(delay);
                }

                return Task.CompletedTask;
            },
            CurrentDirectory = CurrentDirectory,
            HomeDirectory = Home,
            GitProbe = GitProbe,
        };

        var exitCode = await Program.RunAsync(args, host, cancellationToken);
        await outWriter.FlushAsync(CancellationToken.None);
        return new CliRun(exitCode, stdout.ToArray(), errorWriter.ToString());
    }

    /// <summary>Copies a file under the repository root into the workspace.</summary>
    public string CopyToWorkspace(string repositoryRelativePath, string fileName)
    {
        var target = Path.Combine(Workspace, fileName);
        File.Copy(Path.Combine(TestPaths.RepositoryRoot(), repositoryRelativePath), target, overwrite: true);
        return target;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the OS cleans the temp directory eventually.
        }
    }
}

internal sealed record CliRun(int ExitCode, byte[] StdoutBytes, string Stderr)
{
    public string Stdout => Encoding.UTF8.GetString(StdoutBytes);

    /// <summary>The <c>--json</c> envelope.</summary>
    public JsonObject Json => JsonNode.Parse(StdoutBytes)!.AsObject();
}

internal sealed class FakeGitProbe(GitIgnoreStatus status) : IGitProbe
{
    public List<(string Directory, string FileName)> Calls { get; } = [];

    public Task<GitIgnoreStatus> CheckIgnoredAsync(string directory, string fileName, CancellationToken cancellationToken)
    {
        Calls.Add((directory, fileName));
        return Task.FromResult(status);
    }
}

internal static class TestPaths
{
    /// <summary>Repository root, found by walking up to the directory containing WorkLens.slnx.</summary>
    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WorkLens.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("WorkLens.slnx not found above test output.");
    }
}
