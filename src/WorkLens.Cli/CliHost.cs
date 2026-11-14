using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using WorkLens.Core.Security;

namespace WorkLens.Cli;

/// <summary>
/// Everything a command takes from the process: standard streams, environment variables, HTTP, time, directories
/// and git. <c>Main</c> uses <see cref="CreateDefault"/>; tests build their own host and run commands in-process.
/// </summary>
internal sealed class CliHost
{
    /// <summary>Raw stdout, for output that must be written byte for byte (<c>schema show</c>).</summary>
    public required Stream StdoutStream { get; init; }

    /// <summary>Text stdout. Flush it before writing to <see cref="StdoutStream"/>.</summary>
    public required TextWriter Out { get; init; }

    public required TextWriter Error { get; init; }

    /// <summary>Reads an environment variable; <c>null</c> when it is not set.</summary>
    public required Func<string, string?> GetEnvironmentVariable { get; init; }

    /// <summary>Creates the innermost HTTP handler. The caller disposes it.</summary>
    public required Func<HttpMessageHandler> CreateHttpHandler { get; init; }

    public required TimeProvider TimeProvider { get; init; }

    /// <summary>Waits before a retry. Tests record the delay and return at once.</summary>
    public required Func<TimeSpan, CancellationToken, Task> Delay { get; init; }

    /// <summary>The absolute directory that relative paths resolve against.</summary>
    public required string CurrentDirectory { get; init; }

    /// <summary>The user's home directory, for <c>~/.worklens/worklens.json</c>.</summary>
    public required string HomeDirectory { get; init; }

    public required IGitProbe GitProbe { get; init; }

    /// <summary>The one redactor for everything this run writes; commands register the secrets they read.</summary>
    public Redactor Redactor { get; } = new();

    /// <summary>Resolves a path given on the command line against <see cref="CurrentDirectory"/>.</summary>
    public string FullPath(string path) => Path.GetFullPath(path, CurrentDirectory);

    public static CliHost CreateDefault()
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return new CliHost
        {
            StdoutStream = Console.OpenStandardOutput(),
            Out = Console.Out,
            Error = Console.Error,
            GetEnvironmentVariable = Environment.GetEnvironmentVariable,
            CreateHttpHandler = () => new SocketsHttpHandler { AllowAutoRedirect = false },
            TimeProvider = TimeProvider.System,
            Delay = (delay, token) => Task.Delay(delay, TimeProvider.System, token),
            CurrentDirectory = Directory.GetCurrentDirectory(),
            HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            GitProbe = new GitCommandProbe(),
        };
    }
}

internal enum GitIgnoreStatus
{
    Ignored,
    NotIgnored,

    /// <summary>Git is missing, the directory is not in a work tree, or git failed.</summary>
    Unknown,
}

/// <summary>Asks git whether an output file is ignored (D15).</summary>
internal interface IGitProbe
{
    Task<GitIgnoreStatus> CheckIgnoredAsync(string directory, string fileName, CancellationToken cancellationToken);
}

/// <summary>Runs <c>git -C &lt;directory&gt; check-ignore -q &lt;fileName&gt;</c>: exit 0 = ignored, 1 = not ignored.</summary>
internal sealed class GitCommandProbe : IGitProbe
{
    public async Task<GitIgnoreStatus> CheckIgnoredAsync(string directory, string fileName, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            ArgumentList = { "-C", directory, "check-ignore", "-q", "--", fileName },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return GitIgnoreStatus.Unknown;
        }

        if (process is null)
        {
            return GitIgnoreStatus.Unknown;
        }

        using (process)
        {
            process.StandardInput.Close();
            var drainOut = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var drainError = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            await Task.WhenAll(drainOut, drainError).ConfigureAwait(false);
            return process.ExitCode switch
            {
                0 => GitIgnoreStatus.Ignored,
                1 => GitIgnoreStatus.NotIgnored,
                _ => GitIgnoreStatus.Unknown,
            };
        }
    }
}
