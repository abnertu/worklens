using System.CommandLine;
using System.Text;
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
}

public static class Program
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var json = args.Contains("--json", StringComparer.Ordinal);
        var command = string.Empty;
        try
        {
            var parse = BuildRootCommand().Parse(args);
            command = CommandName(parse.CommandResult);

            // With --json, a parse error is still one envelope on stdout (parent backlog B5).
            if (json && parse.Errors.Count > 0)
            {
                CliOutput.WriteFailure(
                    command,
                    ExitCodes.Usage,
                    "command.usage_error",
                    string.Join(" ", parse.Errors.Select(e => e.Message)));
                return ExitCodes.Usage;
            }

            // Disable System.CommandLine's handler so exceptions from actions reach the catch below
            // and map to ExitCodes.Internal instead of 1 (which means a usage error).
            var configuration = new InvocationConfiguration { EnableDefaultExceptionHandler = false };
            return parse.Invoke(configuration);
        }
        catch (Exception ex)
        {
            if (json)
            {
                CliOutput.WriteFailure(command, ExitCodes.Internal, "command.internal_error", ex.Message);
            }
            else
            {
                Console.Error.WriteLine($"internal error: {ex.Message}");
            }

            return ExitCodes.Internal;
        }
    }

    internal static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("WorkLens - evidence-backed work reports for agent CLIs.");

        var schema = new Command("schema", "Inspect built-in JSON Schemas.");
        schema.Subcommands.Add(SchemaCommands.List());
        schema.Subcommands.Add(SchemaCommands.Show());
        root.Subcommands.Add(schema);

        // Registered stubs: no required options, so a bare invocation returns NotImplemented, not a parse error.
        root.Subcommands.Add(Stub("collect", "Collect evidence from GitHub/GitLab into evidence.json."));

        var evidence = new Command("evidence", "Work with Evidence IR.");
        evidence.Subcommands.Add(Stub("digest", "Create the agent input digest.json from evidence.json.", "evidence digest"));
        root.Subcommands.Add(evidence);

        var report = new Command("report", "Work with Report IR.");
        report.Subcommands.Add(ReportCommands.Validate());
        root.Subcommands.Add(report);

        root.Subcommands.Add(PresentCommand.Create());
        root.Subcommands.Add(RenderCommand.Create());

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

    private static Command Stub(string name, string description, string? fullName = null)
    {
        // Stubs accept any arguments (e.g. the documented --period) and still report NotImplemented.
        var command = new Command(name, description + " (not implemented in v0.1)")
        {
            TreatUnmatchedTokensAsErrors = false,
        };
        var json = JsonOption();
        command.Options.Add(json);
        var commandName = fullName ?? name;
        command.SetAction(parse => CliOutput.Run(commandName, parse.GetValue(json), _ =>
            throw new CliException(
                ExitCodes.NotImplemented,
                "command.not_implemented",
                $"'{commandName}' is not implemented in WorkLens v0.1.")));
        return command;
    }
}

internal static class SchemaCommands
{
    public static Command List()
    {
        var command = new Command("list", "List built-in schema ids.");
        var json = Program.JsonOption();
        command.Options.Add(json);
        command.SetAction(parse => CliOutput.Run("schema list", parse.GetValue(json), output =>
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
    public static Command Show()
    {
        var command = new Command("show", "Print a built-in schema, e.g. 'report-ir' or 'report-ir:1.0'.");
        var name = new Argument<string>("name") { Description = "Schema name, optionally with ':<version>'." };
        command.Arguments.Add(name);
        command.SetAction(parse =>
        {
            var value = parse.GetValue(name)!;
            var separator = value.IndexOf(':', StringComparison.Ordinal);
            var schemaName = separator < 0 ? value : value[..separator];
            var version = separator < 0 ? null : value[(separator + 1)..];
            var info = IrSchemaCatalog.FindByName(schemaName);
            if (info is null || (version is not null && version != info.Version))
            {
                var known = string.Join(", ", IrSchemaCatalog.All.Select(i => $"{i.Name}:{i.Version}"));
                Console.Error.WriteLine($"schema.not_found: No built-in schema '{value}'. Built-in schemas: {known}.");
                return ExitCodes.Usage;
            }

            Console.Out.Flush();
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(WorkLensSchemaRegistry.Default.GetBytes(info.Id).Span);
            stdout.Flush();
            return ExitCodes.Success;
        });
        return command;
    }
}
