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
}

/// <summary>
/// Writes command output (docs/design/cli-ux.md section 5): with <c>--json</c>, exactly one envelope object and a
/// trailing LF on stdout; otherwise a human-readable summary on stdout and errors on stderr.
/// </summary>
internal static class CliOutput
{
    private const string OutputVersion = "1";

    /// <summary>
    /// Runs a command body. A <see cref="CliException"/> becomes its exit code, error and issues; other exceptions
    /// propagate to <c>Main</c> (exit 70).
    /// </summary>
    public static int Run(string command, bool json, Func<CommandResultBuilder, int> body)
    {
        var builder = new CommandResultBuilder();
        int exitCode;
        JsonObject? error = null;
        try
        {
            exitCode = body(builder);
        }
        catch (CliException ex)
        {
            exitCode = ex.ExitCode;
            builder.Issues.AddRange(ex.Issues);
            error = Error(ex.Code, ex.Message, ex.Details);
        }

        if (json)
        {
            WriteJson(Envelope(command, exitCode, builder.Result, builder.Issues, error));
        }
        else
        {
            foreach (var line in builder.Lines)
            {
                Console.Out.WriteLine(line);
            }

            foreach (var issue in builder.Issues)
            {
                var path = issue.InstancePath.Length == 0 ? "(root)" : issue.InstancePath;
                Console.Out.WriteLine($"{Severity(issue.Severity)} {issue.Code} {path}: {issue.Message}");
            }

            if (error is not null)
            {
                Console.Error.WriteLine($"{error["code"]!.GetValue<string>()}: {error["message"]!.GetValue<string>()}");
            }
        }

        return exitCode;
    }

    /// <summary>Writes the envelope for a failure that happens outside a command body (usage or internal error).</summary>
    public static void WriteFailure(string command, int exitCode, string code, string message) =>
        WriteJson(Envelope(command, exitCode, result: null, issues: [], Error(code, message, details: null)));

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

    /// <summary>One JSON object and one LF; never <c>WriteLine</c>, whose newline is CRLF on Windows.</summary>
    public static void WriteJson(JsonNode node)
    {
        Console.Out.Write(node.ToJsonString(WorkLensJson.Options));
        Console.Out.Write('\n');
        Console.Out.Flush();
    }
}

/// <summary>What a command body reports: the <c>result</c> object, validation issues and human-readable lines.</summary>
internal sealed class CommandResultBuilder
{
    public JsonObject? Result { get; set; }

    public List<ValidationIssue> Issues { get; } = [];

    public List<string> Lines { get; } = [];
}
