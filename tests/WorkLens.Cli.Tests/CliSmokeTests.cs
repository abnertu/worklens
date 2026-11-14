using WorkLens.Core.Schemas;
using Xunit;

namespace WorkLens.Cli.Tests;

/// <summary>The M0 commands, run in-process on a test host.</summary>
public sealed class CliSmokeTests : IDisposable
{
    private const string ReportExample = "schemas/report-ir/1.0/examples/valid/monthly-report.json";
    private const string EvidenceExample = "schemas/evidence-ir/1.0/examples/valid/monthly-github-gitlab.json";

    private readonly CliRunner _cli = new();

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Version_prints_the_assembly_version()
    {
        var run = await _cli.RunAsync("--version");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal("0.1.0", run.Stdout.Trim());
    }

    [Fact]
    public async Task Schema_list_json_lists_every_built_in_schema()
    {
        var run = await _cli.RunAsync("schema", "list", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.EndsWith("}\n", run.Stdout, StringComparison.Ordinal);
        var ids = run.Json["result"]!["schemas"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()).ToList();
        Assert.Equal(5, ids.Count);
        Assert.Equal(IrSchemaCatalog.All.Select(i => i.Id), ids);
        Assert.Contains("urn:worklens:schema:config:1.0", ids);
    }

    [Fact]
    public async Task Schema_show_writes_the_schema_file_bytes_unchanged()
    {
        var expected = await File.ReadAllBytesAsync(
            Path.Combine(TestPaths.RepositoryRoot(), "schemas", "config", "1.0", "config.schema.json"),
            TestContext.Current.CancellationToken);

        var run = await _cli.RunAsync("schema", "show", "config");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(expected, run.StdoutBytes);
    }

    [Fact]
    public async Task Report_validate_accepts_the_valid_example_in_the_workspace()
    {
        _cli.CopyToWorkspace(ReportExample, "report.json");
        _cli.CopyToWorkspace(EvidenceExample, "evidence.json");
        _cli.CurrentDirectory = _cli.Home;

        var run = await _cli.RunAsync("report", "validate", "--workspace", _cli.Workspace, "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(0, run.Json["result"]!["error_count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Report_validate_rejects_an_invalid_report_with_exit_2()
    {
        _cli.CopyToWorkspace("schemas/report-ir/1.0/examples/invalid/enum--unknown-status.json", "report.json");
        _cli.CopyToWorkspace(EvidenceExample, "evidence.json");

        var run = await _cli.RunAsync("report", "validate", "--workspace", _cli.Workspace);

        Assert.Equal(ExitCodes.ValidationFailed, run.ExitCode);
        Assert.Contains("error schema.", run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paths_without_workspace_resolve_against_the_host_current_directory()
    {
        _cli.CopyToWorkspace(ReportExample, "report.json");
        _cli.CopyToWorkspace(EvidenceExample, "evidence.json");
        var sub = Directory.CreateDirectory(Path.Combine(_cli.Workspace, "sub")).FullName;
        _cli.CurrentDirectory = sub;

        var relative = await _cli.RunAsync("report", "validate", "--report", "../report.json", "--evidence", "../evidence.json", "--json");

        Assert.Equal(ExitCodes.Success, relative.ExitCode);
        Assert.Equal(Path.Combine(_cli.Workspace, "report.json"), relative.Json["result"]!["files"]!["report"]!.GetValue<string>());

        _cli.CurrentDirectory = _cli.Workspace;
        var defaults = await _cli.RunAsync("report", "validate", "--json");

        Assert.Equal(ExitCodes.Success, defaults.ExitCode);
        Assert.Equal(Path.Combine(_cli.Workspace, "evidence.json"), defaults.Json["result"]!["files"]!["evidence"]!.GetValue<string>());
    }

    [Fact]
    public async Task Evidence_digest_is_still_a_stub()
    {
        var run = await _cli.RunAsync("evidence", "digest", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.NotImplemented, run.ExitCode);
        Assert.Equal("command.not_implemented", run.Json["error"]!["code"]!.GetValue<string>());
    }
}
