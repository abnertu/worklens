using System.Text.Json.Nodes;
using WorkLens.Core.Errors;
using WorkLens.Core.Json;

namespace WorkLens.Cli;

/// <summary>
/// An expected command failure (configuration, input, output) that maps to an exit code and a
/// <c>{code, message, details}</c> error, optionally with the validation issues that caused it.
/// </summary>
internal sealed class CliException(
    int exitCode,
    string code,
    string message,
    JsonObject? details = null,
    IReadOnlyList<ValidationIssue>? issues = null) : Exception(message)
{
    public int ExitCode { get; } = exitCode;

    public string Code { get; } = code;

    public JsonObject? Details { get; } = details;

    public IReadOnlyList<ValidationIssue> Issues { get; } = issues ?? [];

    public static JsonObject FileDetails(string path) => new() { ["file"] = path };

    /// <summary>
    /// Resolver / renderer errors are problems of an upstream file (manifest or template) that the report author
    /// cannot fix: exit 3, every error as an error issue (D14).
    /// </summary>
    public static CliException FromErrors(IReadOnlyList<WorkLensError> errors, string code, string file)
    {
        var issues = errors
            .Select(e => new ValidationIssue(
                e.Code,
                IssueSeverity.Error,
                e.Details?.GetValueOrDefault("path") ?? string.Empty,
                null,
                e.Message))
            .ToList();
        var message = errors.Count == 1 ? errors[0].Message : $"{errors.Count} problems in {file}.";
        return new CliException(ExitCodes.Configuration, code, message, FileDetails(file), issues);
    }
}

/// <summary>
/// Writes command output (docs/design/cli-ux.md section 5) to the host's writers: with <c>--json</c>, exactly one
/// envelope object and a trailing LF on stdout; otherwise a human-readable summary on stdout and errors on stderr.
/// Every string passes through the host's <see cref="Redactor"/>.
/// </summary>
internal sealed class CliOutput(CliHost host)
{
    private const string OutputVersion = "1";

    /// <summary>Runs a synchronous command body; see <see cref="RunAsync"/>.</summary>
    public int Run(string command, bool json, Func<CommandResultBuilder, int> body) =>
        RunAsync(command, json, (builder, _) => Task.FromResult(body(builder)), CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    /// <summary>
    /// Runs a command body. A <see cref="CliException"/> becomes its exit code, error and issues; other exceptions
    /// propagate to <c>Program.RunAsync</c> (exit 70).
    /// </summary>
    public async Task<int> RunAsync(
        string command,
        bool json,
        Func<CommandResultBuilder, CancellationToken, Task<int>> body,
        CancellationToken cancellationToken)
    {
        var builder = new CommandResultBuilder();
        int exitCode;
        JsonObject? error = null;
        try
        {
            exitCode = await body(builder, cancellationToken).ConfigureAwait(false);
        }
        catch (CliException ex)
        {
            exitCode = ex.ExitCode;
            builder.Issues.AddRange(ex.Issues);
            error = Error(ex.Code, ex.Message, ex.Details);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ctrl+C or the caller's token; a timeout is not a cancellation and is retried by the connector (D23).
            exitCode = ExitCodes.Cancelled;
            builder.Result = null;
            error = Error("command.cancelled", "The command was cancelled; no file was written.", details: null);
        }

        if (json)
        {
            await WriteJsonAsync(Envelope(command, exitCode, builder.Result, builder.Issues, error)).ConfigureAwait(false);
            return exitCode;
        }

        foreach (var line in builder.Lines)
        {
            await WriteLineAsync(host.Out, line).ConfigureAwait(false);
        }

        foreach (var issue in builder.Issues)
        {
            var path = issue.InstancePath.Length == 0 ? "(root)" : issue.InstancePath;
            await WriteLineAsync(host.Out, $"{Severity(issue.Severity)} {issue.Code} {path}: {issue.Message}").ConfigureAwait(false);
        }

        if (error is not null)
        {
            await WriteErrorAsync(error["code"]!.GetValue<string>(), error["message"]!.GetValue<string>()).ConfigureAwait(false);
        }

        await host.Out.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        return exitCode;
    }

    /// <summary>Writes the envelope for a failure that happens outside a command body (usage or internal error).</summary>
    public Task WriteFailureAsync(string command, int exitCode, string code, string message) =>
        WriteJsonAsync(Envelope(command, exitCode, result: null, issues: [], Error(code, message, details: null)));

    /// <summary>Writes <c>&lt;code&gt;: &lt;message&gt;</c> to stderr.</summary>
    public async Task WriteErrorAsync(string code, string message)
    {
        await WriteLineAsync(host.Error, $"{code}: {message}").ConfigureAwait(false);
        await host.Error.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Writes one diagnostic line to stderr (for example a <c>--verbose</c> request log).</summary>
    public Task LogAsync(string line) => WriteLineAsync(host.Error, line);

    public static JsonObject Envelope(
        string command,
        int exitCode,
        JsonObject? result,
        IEnumerable<ValidationIssue> issues,
        JsonObject? error) => new()
        {
            ["output_version"] = OutputVersion,
            ["command"] = command,
            ["ok"] = exitCode == ExitCodes.Success,
            ["exit_code"] = exitCode,
            ["result"] = result,
            ["issues"] = new JsonArray([.. issues.Select(IssueNode)]),
            ["error"] = error,
        };

    public static JsonObject Error(string code, string message, JsonObject? details) => new()
    {
        ["code"] = code,
        ["message"] = message,
        ["details"] = details,
    };

    /// <summary>
    /// One JSON object and one LF; never <c>WriteLine</c>, whose newline is CRLF on Windows. String values are
    /// redacted in the tree, before serialization escapes them.
    /// </summary>
    public async Task WriteJsonAsync(JsonNode node)
    {
        Redact(node);
        await host.Out.WriteAsync(node.ToJsonString(WorkLensJson.Options)).ConfigureAwait(false);
        await host.Out.WriteAsync('\n').ConfigureAwait(false);
        await host.Out.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Built as a <see cref="JsonObject"/> so that <c>schema_path: null</c> is written (the serializer options omit
    /// null properties of typed objects).
    /// </summary>
    private static JsonNode IssueNode(ValidationIssue issue) => new JsonObject
    {
        ["code"] = issue.Code,
        ["severity"] = Severity(issue.Severity),
        ["instance_path"] = issue.InstancePath,
        ["schema_path"] = issue.SchemaPath,
        ["message"] = issue.Message,
    };

    private static string Severity(IssueSeverity severity) => severity == IssueSeverity.Error ? "error" : "warning";

    private Task WriteLineAsync(TextWriter writer, string line) => writer.WriteLineAsync(host.Redactor.Redact(line));

    private void Redact(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (obj[name] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        obj[name] = host.Redactor.Redact(text);
                    }
                    else
                    {
                        Redact(obj[name]);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        array[i] = host.Redactor.Redact(text);
                    }
                    else
                    {
                        Redact(array[i]);
                    }
                }

                break;
        }
    }
}

/// <summary>What a command body reports: the <c>result</c> object, validation issues and human-readable lines.</summary>
internal sealed class CommandResultBuilder
{
    public JsonObject? Result { get; set; }

    public List<ValidationIssue> Issues { get; } = [];

    public List<string> Lines { get; } = [];
}
