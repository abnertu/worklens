using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml.Packaging;
using WorkLens.Tests.Shared;
using Xunit;

namespace WorkLens.Cli.Tests;

public sealed class IssueStatusEndToEndTests : IDisposable
{
    private readonly CliRunner _cli = new();

    private void Configure(bool enabled)
    {
        _cli.Http = FixtureHandler.FromDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gitlab", "issue-status"));
        _cli.Environment["CORP_GITLAB"] = GitLabCollectTests.GitLabToken;
        var source = GitLabCollectTests.Source();
        if (enabled) source["issue_status_labels"] = new JsonArray("Todo", "Doing", "Pending", "Done");
        var config = GitLabCollectTests.Config(source);
        config["config_version"] = "1.1";
        config["identities"]!.AsArray().Add(new JsonObject { ["platform"] = "gitlab", ["username"] = "zeta" });
        CollectCommandTests.WriteConfig(Path.Combine(_cli.Workspace, "worklens.json"), config);
    }

    public void Dispose() => _cli.Dispose();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Collect_writes_latest_evidence_and_reports_issue_activity_count(bool enabled)
    {
        Configure(enabled);
        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");
        Assert.True(run.ExitCode == ExitCodes.Success, run.Stdout);
        var evidence = JsonNode.Parse(File.ReadAllBytes(Path.Combine(_cli.Workspace, "evidence.json")))!;
        Assert.Equal("1.1", evidence["schema_version"]!.GetValue<string>());
        Assert.Equal(enabled ? 4 : 0, run.Json["result"]!["items_by_kind"]!["issue_activity"]!.GetValue<int>());
        Assert.Equal(0, run.Json["result"]!["items_by_kind"]!["commit"]!.GetValue<int>());
        Assert.Equal(enabled, evidence["query"]!["sources"]![0]!["issue_status_labels"] is not null);
        if (!enabled) Assert.Single(_cli.Http.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dry_run_lists_issue_endpoints_only_when_enabled(bool enabled)
    {
        Configure(enabled);
        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--dry-run", "--json");
        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var endpoints = run.Json["result"]!["sources"]![0]!["endpoints"]!.AsArray().Select(e => e!.GetValue<string>());
        Assert.Equal(enabled, endpoints.Any(e => e.Contains("resource_label_events", StringComparison.Ordinal)));
        Assert.Equal(enabled, endpoints.Any(e => e.Contains("resource_state_events", StringComparison.Ordinal)));
        Assert.Empty(_cli.Http.Requests);
    }

    [Fact]
    public async Task Issue_evidence_validates_and_renders_as_issue_ids_and_states()
    {
        Configure(true);
        var collect = await _cli.RunAsync("collect", "--period", "2026-10", "--json");
        Assert.True(collect.ExitCode == ExitCodes.Success, collect.Stdout);
        var bytes = File.ReadAllBytes(Path.Combine(_cli.Workspace, "evidence.json"));
        var evidence = JsonNode.Parse(bytes)!;
        var ids = evidence["items"]!.AsArray().Where(i => i!["source"]!["external_id"]!.GetValue<string>() is "1" or "2").Select(i => i!["id"]!.GetValue<string>()).ToArray();
        var report = new JsonObject
        {
            ["schema_version"] = "1.1", ["report_id"] = "rpt-2026-10-issues",
            ["period"] = evidence["query"]!["period"]!.DeepClone(),
            ["subject"] = new JsonObject { ["display_name"] = "Alice" },
            ["evidence_document"] = new JsonObject
            {
                ["document_id"] = evidence["document_id"]!.DeepClone(),
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            },
            ["work_items"] = new JsonArray(new JsonObject
            {
                ["id"] = "wi-issues", ["origin"] = "agent", ["category"] = "feature", ["status"] = "in_progress",
                ["title"] = "Issue work", ["summary"] = "Changed issue states.",
                ["evidence_refs"] = new JsonArray([.. ids.Select(id => (JsonNode)JsonValue.Create(id)!)]),
                ["observed_facts"] = new JsonArray(),
            }),
            ["narratives"] = new JsonArray(),
        };
        File.WriteAllText(Path.Combine(_cli.Workspace, "report.json"), report.ToJsonString());
        _cli.CopyToWorkspace("templates/rd-monthly/rd-monthly.pptx", "rd-monthly.pptx");
        var manifestPath = _cli.CopyToWorkspace("templates/rd-monthly/manifest.json", "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["labels"]!["evidence_issue"] = "Issue {id}";
        manifest["labels"]!["summary_activity"] = "commits={commits}, merged={merged_changes}";
        manifest["labels"]!["evidence_state_open"] = "Open";
        manifest["labels"]!["evidence_state_closed"] = "Closed";
        File.WriteAllText(manifestPath, manifest!.ToJsonString());

        var validate = await _cli.RunAsync("report", "validate", "--json");
        Assert.True(validate.ExitCode == ExitCodes.Success, validate.Stdout);
        var present = await _cli.RunAsync("present", "--manifest", manifestPath, "--json");
        Assert.True(present.ExitCode == ExitCodes.Success, present.Stdout);
        var render = await _cli.RunAsync("render", "--manifest", manifestPath, "--json");
        Assert.True(render.ExitCode == ExitCodes.Success, render.Stdout);
        using var pptx = PresentationDocument.Open(Path.Combine(_cli.Workspace, "report.pptx"), false);
        var text = string.Join("\n", pptx.PresentationPart!.SlideParts.Select(p => p.Slide!.InnerText));
        Assert.Contains("Issue #1", text, StringComparison.Ordinal);
        Assert.Contains("Issue #2", text, StringComparison.Ordinal);
        Assert.Contains("Open", text, StringComparison.Ordinal);
        Assert.Contains("commits=0, merged=0", text, StringComparison.Ordinal);
        Assert.Contains("Closed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Doing", text, StringComparison.Ordinal);
    }
}
