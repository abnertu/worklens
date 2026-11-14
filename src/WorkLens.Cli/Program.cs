using System.CommandLine;
using System.CommandLine.Parsing;
using System.Reflection;
using System.Text.Json.Nodes;
using WorkLens.Core.Schemas;

namespace WorkLens.Cli;

/// <summary>Exit codes, see docs/design/cli-ux.md section 6.</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Usage = 1;
    public const int ValidationFailed = 2;
    public const int Configuration = 3;
    public const int Source = 4;
    public const int Stale = 5;
    public const int NotImplemented = 10;
    public const int Internal = 70;

    /// <summary>The user cancelled the command (Ctrl+C), as a shell reports SIGINT (D23).</summary>
    public const int Cancelled = 130;
}

public static class Program
{
    /// <summary>The informational version, such as <c>0.1.0</c>.</summary>
    internal static string Version { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public static Task<int> Main(string[] args) => RunAsync(args, CliHost.CreateDefault(), CancellationToken.None);

    /// <summary>
    /// Parses and runs one command on <paramref name="host"/>. Ctrl+C and <paramref name="cancellationToken"/> both
    /// cancel the command. Every exception that a command does not handle becomes exit 70, written through the
    /// host's redactor.
    /// </summary>
    internal static async Task<int> RunAsync(string[] args, CliHost host, CancellationToken cancellationToken)
    {
        var output = new CliOutput(host);
        var json = args.Contains("--json", StringComparer.Ordinal);
        var command = string.Empty;
        try
        {
            // Usage errors can echo arguments before the collect action resolves its tokens.
            foreach (var name in CollectCommand.DefaultTokenVariables)
            {
                host.Redactor.AddSecret(host.GetEnvironmentVariable(name));
            }

            var parse = BuildRootCommand(host).Parse(args);
            command = CommandName(parse.CommandResult);

            // Route parser diagnostics through the same redactor as command failures.
            if (parse.Errors.Count > 0)
            {
                var message = UsageMessage(parse);
                if (json)
                {
                    await output.WriteFailureAsync(command, ExitCodes.Usage, "command.usage_error", message).ConfigureAwait(false);
                }
                else
                {
                    await output.WriteErrorAsync("command.usage_error", message).ConfigureAwait(false);
                }

                return ExitCodes.Usage;
            }

            // Disable System.CommandLine's handler so exceptions from actions reach the catch below
            // and map to ExitCodes.Internal instead of 1 (which means a usage error).
            var configuration = new InvocationConfiguration
            {
                EnableDefaultExceptionHandler = false,
                Output = host.Out,
                Error = host.Error,
            };
            return await parse.InvokeAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (json)
            {
                await output.WriteFailureAsync(command, ExitCodes.Internal, "command.internal_error", ex.Message).ConfigureAwait(false);
            }
            else
            {
                await output.WriteErrorAsync("internal error", ex.Message).ConfigureAwait(false);
            }

            return ExitCodes.Internal;
        }
    }

    private static string UsageMessage(ParseResult parse)
    {
        var message = string.Join(" ", parse.Errors.Select(e => e.Message));
        // Argument values can contain secrets from custom variables that are not known before configuration loads.
        var values = parse.Tokens.Where(t => t.Type == TokenType.Argument).Select(t => t.Value)
            .Concat(parse.UnmatchedTokens)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length);
        foreach (var value in values)
        {
            message = message.Replace(value, "<value>", StringComparison.Ordinal);
        }

        return message;
    }

    internal static RootCommand BuildRootCommand(CliHost host)
    {
        var root = new RootCommand("WorkLens - evidence-backed work reports for agent CLIs.");

        var schema = new Command("schema", "Inspect built-in JSON Schemas.");
        schema.Subcommands.Add(SchemaCommands.List(host));
        schema.Subcommands.Add(SchemaCommands.Show(host));
        root.Subcommands.Add(schema);

        root.Subcommands.Add(CollectCommand.Create(host));

        // A registered stub: no required options, so a bare invocation returns NotImplemented, not a parse error.
        var evidence = new Command("evidence", "Work with Evidence IR.");
        evidence.Subcommands.Add(Stub(host, "digest", "Create the agent input digest.json from evidence.json.", "evidence digest"));
        root.Subcommands.Add(evidence);

        var report = new Command("report", "Work with Report IR.");
        report.Subcommands.Add(ReportCommands.Validate(host));
        root.Subcommands.Add(report);

        root.Subcommands.Add(PresentCommand.Create(host));
        root.Subcommands.Add(RenderCommand.Create(host));

        return root;
    }

    internal static Option<bool> JsonOption() => new("--json") { Description = "Write a single JSON object to stdout." };

    internal static Option<string?> WorkspaceOption() => new("--workspace")
    {
        Description = "Directory that holds the default input and output files (default: current directory).",
    };

    internal static Option<string?> FileOption(string name, string defaultFileName) => new(name)
    {
        Description = $"File path (default: <workspace>/{defaultFileName}).",
    };

    internal static Option<bool> StrictOption() => new("--strict") { Description = "Treat warnings as errors (exit 2)." };

    /// <summary>Full command name without the root, e.g. <c>report validate</c>.</summary>
    private static string CommandName(System.CommandLine.Parsing.CommandResult? result)
    {
        var names = new List<string>();
        while (result is not null && result.Command is not RootCommand)
        {
            names.Insert(0, result.Command.Name);
            result = result.Parent as System.CommandLine.Parsing.CommandResult;
        }

        return string.Join(' ', names);
    }

    private static Command Stub(CliHost host, string name, string description, string? fullName = null)
    {
        // Stubs accept any arguments (e.g. the documented --period) and still report NotImplemented.
        var command = new Command(name, description + " (not implemented in v0.1)")
        {
            TreatUnmatchedTokensAsErrors = false,
        };
        var json = JsonOption();
        command.Options.Add(json);
        var commandName = fullName ?? name;
        command.SetAction(parse => new CliOutput(host).Run(commandName, parse.GetValue(json), _ =>
            throw new CliException(
                ExitCodes.NotImplemented,
                "command.not_implemented",
                $"'{commandName}' is not implemented in WorkLens v0.1.")));
        return command;
    }
}

internal static class SchemaCommands
{
    public static Command List(CliHost host)
    {
        var command = new Command("list", "List built-in schema ids.");
        var json = Program.JsonOption();
        command.Options.Add(json);
        command.SetAction(parse => new CliOutput(host).Run("schema list", parse.GetValue(json), output =>
        {
            var schemas = new JsonArray();
            foreach (var info in IrSchemaCatalog.All)
            {
                schemas.Add(new JsonObject { ["id"] = info.Id, ["name"] = info.Name, ["version"] = info.Version });
                output.Lines.Add(info.Id);
            }

            output.Result = new JsonObject { ["schemas"] = schemas };
            return ExitCodes.Success;
        }));
        return command;
    }

    /// <summary><c>schema show &lt;name&gt;[:&lt;version&gt;]</c>: writes the built-in schema bytes unchanged to stdout.</summary>
    public static Command Show(CliHost host)
    {
        var command = new Command("show", "Print a built-in schema, e.g. 'report-ir' or 'report-ir:1.0'.");
        var name = new Argument<string>("name") { Description = "Schema name, optionally with ':<version>'." };
        command.Arguments.Add(name);
        command.SetAction(async (parse, cancellationToken) =>
        {
            var value = parse.GetValue(name)!;
            var separator = value.IndexOf(':', StringComparison.Ordinal);
            var schemaName = separator < 0 ? value : value[..separator];
            var version = separator < 0 ? null : value[(separator + 1)..];
            var info = IrSchemaCatalog.FindByName(schemaName);
            if (info is null || (version is not null && version != info.Version))
            {
                var known = string.Join(", ", IrSchemaCatalog.All.Select(i => $"{i.Name}:{i.Version}"));
                await new CliOutput(host).WriteErrorAsync("schema.not_found", $"No built-in schema '{value}'. Built-in schemas: {known}.").ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            await host.Out.FlushAsync(cancellationToken).ConfigureAwait(false);
            await host.StdoutStream.WriteAsync(WorkLensSchemaRegistry.Default.GetBytes(info.Id), cancellationToken).ConfigureAwait(false);
            await host.StdoutStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        });
        return command;
    }
}
