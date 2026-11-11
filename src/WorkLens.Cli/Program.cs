using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkLens.Core.Json;
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
    private const string OutputVersion = "1";

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        try
        {
            // Disable System.CommandLine's handler so exceptions from actions reach the catch below
            // and map to ExitCodes.Internal instead of 1 (which means a usage error).
            var configuration = new InvocationConfiguration { EnableDefaultExceptionHandler = false };
            return BuildRootCommand().Parse(args).Invoke(configuration);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"internal error: {ex.Message}");
            return ExitCodes.Internal;
        }
    }

    internal static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("WorkLens - evidence-backed work reports for agent CLIs.");

        var schema = new Command("schema", "Inspect built-in JSON Schemas.");
        var schemaList = new Command("list", "List built-in schema ids.");
        var schemaListJson = JsonOption();
        schemaList.Options.Add(schemaListJson);
        schemaList.SetAction(parse => ListSchemas(parse.GetValue(schemaListJson)));
        schema.Subcommands.Add(schemaList);
        root.Subcommands.Add(schema);

        // Registered stubs: no required options, so a bare invocation returns NotImplemented, not a parse error.
        root.Subcommands.Add(Stub("collect", "Collect evidence from GitHub/GitLab into evidence.json."));

        var evidence = new Command("evidence", "Work with Evidence IR.");
        evidence.Subcommands.Add(Stub("digest", "Create the agent input digest.json from evidence.json.", "evidence digest"));
        root.Subcommands.Add(evidence);

        var report = new Command("report", "Work with Report IR.");
        report.Subcommands.Add(Stub("validate", "Validate report.json against schema and evidence.", "report validate"));
        root.Subcommands.Add(report);

        root.Subcommands.Add(Stub("present", "Resolve report.json into presentation.json using a template manifest."));
        root.Subcommands.Add(Stub("render", "Render presentation.json into a .pptx file."));

        return root;
    }

    private static Option<bool> JsonOption() => new("--json") { Description = "Write a single JSON object to stdout." };

    private static int ListSchemas(bool json)
    {
        if (json)
        {
            var schemas = new JsonArray();
            foreach (var info in IrSchemaCatalog.All)
            {
                schemas.Add(new JsonObject { ["id"] = info.Id, ["name"] = info.Name, ["version"] = info.Version });
            }

            WriteJson(Envelope("schema list", ExitCodes.Success, new JsonObject { ["schemas"] = schemas }, error: null));
        }
        else
        {
            foreach (var info in IrSchemaCatalog.All)
            {
                Console.Out.WriteLine(info.Id);
            }
        }

        return ExitCodes.Success;
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
        command.SetAction(parse =>
        {
            const string code = "command.not_implemented";
            var message = $"'{commandName}' is not implemented in WorkLens v0.1.";
            if (parse.GetValue(json))
            {
                var error = new JsonObject { ["code"] = code, ["message"] = message, ["details"] = null };
                WriteJson(Envelope(commandName, ExitCodes.NotImplemented, result: null, error));
            }
            else
            {
                Console.Error.WriteLine($"{code}: {message}");
            }

            return ExitCodes.NotImplemented;
        });
        return command;
    }

    private static JsonObject Envelope(string command, int exitCode, JsonObject? result, JsonObject? error) => new()
    {
        ["output_version"] = OutputVersion,
        ["command"] = command,
        ["ok"] = exitCode == ExitCodes.Success,
        ["exit_code"] = exitCode,
        ["result"] = result,
        ["issues"] = new JsonArray(),
        ["error"] = error,
    };

    private static void WriteJson(JsonNode node) =>
        Console.Out.WriteLine(node.ToJsonString(WorkLensJson.Options));
}
