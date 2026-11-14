using System.Text.Json.Nodes;
using WorkLens.Core.Schemas;
using Xunit;

namespace WorkLens.Cli.Tests;

/// <summary>The M0 commands, run in-process on a test host.</summary>
public sealed class CliSmokeTests : IDisposable
{
    private const string ReportExample = "schemas/report-ir/1.0/examples/valid/monthly-report.json";
    private const string EvidenceExample = "schemas/evidence-ir/1.0/examples/valid/monthly-github-gitlab.json";
    private const string ManifestExample = "schemas/template-manifest/1.0/examples/valid/sample-monthly.json";
    private const string PresentationExample = "schemas/presentation-ir/1.0/examples/valid/monthly-presentation.json";
    private const string Manifest11Example = "schemas/template-manifest/1.1/examples/valid/rd-monthly.json";
    private const string Report11Example = "schemas/report-ir/1.1/examples/valid/monthly-report.json";

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
        Assert.Equal(IrSchemaCatalog.All.Select(i => i.Id), ids);
        Assert.Contains("urn:worklens:schema:config:1.0", ids);
        Assert.Contains("urn:worklens:schema:report-ir:1.0", ids);
        Assert.Contains("urn:worklens:schema:report-ir:1.1", ids);
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
    public async Task Schema_show_without_a_version_writes_the_latest_minor()
    {
        var expected = await File.ReadAllBytesAsync(
            Path.Combine(TestPaths.RepositoryRoot(), "schemas", "report-ir", "1.1", "report-ir.schema.json"),
            TestContext.Current.CancellationToken);

        var run = await _cli.RunAsync("schema", "show", "report-ir");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(expected, run.StdoutBytes);
    }

    [Fact]
    public async Task Schema_show_rejects_an_unknown_version()
    {
        var run = await _cli.RunAsync("schema", "show", "report-ir:1.9");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.StartsWith("schema.not_found:", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(run.StdoutBytes);
    }

    [Fact]
    public async Task Schema_show_with_a_version_writes_that_version()
    {
        var expected = await File.ReadAllBytesAsync(
            Path.Combine(TestPaths.RepositoryRoot(), "schemas", "report-ir", "1.0", "report-ir.schema.json"),
            TestContext.Current.CancellationToken);

        var run = await _cli.RunAsync("schema", "show", "report-ir:1.0");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(expected, run.StdoutBytes);
    }

    // Each input that present and render read is validated against the version that it names.
    [Theory]
    [InlineData("evidence")]
    [InlineData("manifest")]
    public async Task Present_rejects_an_upstream_file_with_an_unknown_version(string file)
    {
        _cli.CopyToWorkspace(ReportExample, "report.json");
        var evidence = _cli.CopyToWorkspace(EvidenceExample, "evidence.json");
        var manifest = _cli.CopyToWorkspace(ManifestExample, "manifest.json");
        SetVersion(file == "evidence" ? evidence : manifest, "1.9");

        var run = await _cli.RunAsync("present", "--workspace", _cli.Workspace, "--manifest", manifest, "--json");

        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        Assert.Equal("input.invalid", run.Json["error"]!["code"]!.GetValue<string>());
        var issue = Assert.Single(run.Json["issues"]!.AsArray());
        Assert.Equal("schema.unsupported_version", issue!["code"]!.GetValue<string>());
        Assert.Equal("/schema_version", issue["instance_path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Present_reports_schema_type_for_an_evidence_root_that_is_not_an_object()
    {
        _cli.CopyToWorkspace(ReportExample, "report.json");
        File.WriteAllText(Path.Combine(_cli.Workspace, "evidence.json"), "[]");
        var manifest = _cli.CopyToWorkspace(ManifestExample, "manifest.json");

        var run = await _cli.RunAsync("present", "--workspace", _cli.Workspace, "--manifest", manifest, "--json");

        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        var issue = Assert.Single(run.Json["issues"]!.AsArray());
        Assert.Equal("schema.type", issue!["code"]!.GetValue<string>());
        Assert.Equal(string.Empty, issue["instance_path"]!.GetValue<string>());
    }

    [Fact]
    public async Task Render_rejects_a_presentation_with_an_unknown_version()
    {
        SetVersion(_cli.CopyToWorkspace(PresentationExample, "presentation.json"), "1.9");
        var manifest = _cli.CopyToWorkspace(ManifestExample, "manifest.json");

        var run = await _cli.RunAsync("render", "--workspace", _cli.Workspace, "--manifest", manifest, "--json");

        Assert.Equal(ExitCodes.ValidationFailed, run.ExitCode);
        var issue = Assert.Single(run.Json["issues"]!.AsArray());
        Assert.Equal("schema.unsupported_version", issue!["code"]!.GetValue<string>());
        Assert.Equal("/schema_version", issue["instance_path"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(_cli.Workspace, "report.pptx")));
    }

    // present and render take the rd-monthly 1.1 manifest from report to .pptx.
    [Fact]
    public async Task Present_and_render_turn_a_1_1_report_into_a_pptx()
    {
        _cli.CopyToWorkspace("tests/Fixtures/rd-monthly/full/report.json", "report.json");
        _cli.CopyToWorkspace("tests/Fixtures/rd-monthly/full/evidence.json", "evidence.json");
        _cli.CopyToWorkspace("templates/rd-monthly/rd-monthly.pptx", "rd-monthly.pptx");
        var manifest = _cli.CopyToWorkspace("templates/rd-monthly/manifest.json", "manifest.json");

        var present = await _cli.RunAsync("present", "--workspace", _cli.Workspace, "--manifest", manifest, "--json");
        var render = await _cli.RunAsync("render", "--workspace", _cli.Workspace, "--manifest", manifest, "--json");

        Assert.Equal(ExitCodes.Success, present.ExitCode);
        Assert.Equal(ExitCodes.Success, render.ExitCode);
        Assert.Empty(render.Json["issues"]!.AsArray());
        Assert.True(File.Exists(Path.Combine(_cli.Workspace, "report.pptx")));
    }

    // Layout implements the 1.1 rules, so present resolves a 1.1 report with a 1.1 manifest.
    [Fact]
    public async Task Present_writes_a_presentation_with_a_1_1_manifest()
    {
        _cli.CopyToWorkspace(Report11Example, "report.json");
        _cli.CopyToWorkspace(EvidenceExample, "evidence.json");
        var manifest = _cli.CopyToWorkspace(Manifest11Example, "manifest.json");

        var run = await _cli.RunAsync("present", "--workspace", _cli.Workspace, "--manifest", manifest, "--json", "--strict");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Empty(run.Json["issues"]!.AsArray());
        var presentation = JsonNode.Parse(File.ReadAllText(Path.Combine(_cli.Workspace, "presentation.json")))!;
        Assert.Equal("1.1", presentation["schema_version"]!.GetValue<string>());
        Assert.Equal("cover_v1", presentation["slides"]![0]!["layout_id"]!.GetValue<string>());
    }

    // A 1.0 manifest has no section for the support narrative of a 1.1 report. Layout warns instead of dropping it
    // without a sign, and --strict stops on that warning like on a report rule warning.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Present_reports_a_narrative_that_no_slide_shows(bool strict)
    {
        _cli.CopyToWorkspace(Report11Example, "report.json");
        _cli.CopyToWorkspace(EvidenceExample, "evidence.json");
        var manifest = _cli.CopyToWorkspace(ManifestExample, "manifest.json");
        string[] args = ["present", "--workspace", _cli.Workspace, "--manifest", manifest, "--json"];

        var run = await _cli.RunAsync(strict ? [.. args, "--strict"] : args);

        Assert.Equal(strict ? ExitCodes.ValidationFailed : ExitCodes.Success, run.ExitCode);
        // The report itself has no issue, so the Layout warning alone decides the --strict exit code.
        var issue = Assert.Single(run.Json["issues"]!.AsArray());
        Assert.Equal("layout.unplaced_narrative", issue!["code"]!.GetValue<string>());
        Assert.Equal("warning", issue["severity"]!.GetValue<string>());
        Assert.Equal("/narratives/3", issue["instance_path"]!.GetValue<string>());
        Assert.Equal(1, run.Json["result"]!["warning_count"]!.GetValue<int>());
        Assert.Equal(!strict, File.Exists(Path.Combine(_cli.Workspace, "presentation.json")));
    }

    // Both commands run the 1.1 contract checks, so a manifest author sees the manifest's own problems.
    [Theory]
    [InlineData("present")]
    [InlineData("render")]
    public async Task Present_and_render_report_manifest_contract_problems(string command)
    {
        var manifest = CopyManifestInputs(command);
        var node = JsonNode.Parse(File.ReadAllText(manifest))!;
        var slot = node["layouts"]!.AsArray().Single(l => l!["id"]!.GetValue<string>() == "achievements_v1")!["slots"]![1]!;
        slot["header_style"] = "undefined_role";
        File.WriteAllText(manifest, node.ToJsonString());

        var run = await _cli.RunAsync(command, "--workspace", _cli.Workspace, "--manifest", manifest, "--json");

        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        Assert.Equal("manifest.invalid", run.Json["error"]!["code"]!.GetValue<string>());
        var issue = Assert.Single(run.Json["issues"]!.AsArray());
        Assert.Equal("manifest.unknown_style", issue!["code"]!.GetValue<string>());
    }

    /// <summary>Copies valid 1.0 inputs, so that the command reaches the manifest checks, and the 1.1 manifest.</summary>
    private string CopyManifestInputs(string command)
    {
        if (command == "present")
        {
            _cli.CopyToWorkspace(ReportExample, "report.json");
            _cli.CopyToWorkspace(EvidenceExample, "evidence.json");
        }
        else
        {
            _cli.CopyToWorkspace(PresentationExample, "presentation.json");
        }

        return _cli.CopyToWorkspace(Manifest11Example, "manifest.json");
    }

    private static void SetVersion(string path, string version)
    {
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        node["schema_version"] = version;
        File.WriteAllText(path, node.ToJsonString());
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
