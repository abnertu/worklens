using System.Text.Json.Nodes;
using WorkLens.Tests.Shared;
using Xunit;
using static WorkLens.Tests.Shared.FixtureHandler;

namespace WorkLens.Cli.Tests;

public sealed class MonthRangeCollectTests : IDisposable
{
    private readonly CliRunner _cli = new();

    public MonthRangeCollectTests()
    {
        CollectCommandTests.WriteConfig(Path.Combine(_cli.Workspace, "worklens.json"), CollectCommandTests.Config());
        _cli.Environment["WORKLENS_GITHUB_TOKEN"] = CollectCommandTests.Token;
        _cli.Http = CollectCommandTests.Monthly().AddDirectory(RangeFixtures);
    }

    private static string RangeFixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "github", "month-range");
    private static string MonthlyFixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "github", "monthly");
    private string Evidence(string label) => Path.Combine(_cli.Workspace, label, "evidence.json");
    private Task<CliRun> Run(params string[] extra) =>
        _cli.RunAsync(["collect", "--from", "2026-08", "--to", "2026-10", .. extra]);

    public void Dispose() => _cli.Dispose();

    [Fact]
    public async Task Range_writes_independent_months_and_prefixes_warnings()
    {
        var run = await Run("--json");

        Assert.True(run.ExitCode == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Empty(_cli.Http.Unmatched);
        var months = run.Json["result"]!["months"]!.AsArray();
        Assert.Equal(3, months.Count);
        foreach (var (node, label) in months.Zip(new[] { "2026-08", "2026-09", "2026-10" }))
        {
            var evidence = JsonNode.Parse(File.ReadAllText(Evidence(label)))!;
            Assert.Equal(label, evidence["query"]!["period"]!["label"]!.GetValue<string>());
            Assert.Equal("written", node!["status"]!.GetValue<string>());
            Assert.Equal(evidence["document_id"]!.GetValue<string>(), node["document_id"]!.GetValue<string>());
            Assert.Equal(evidence["items"]!.AsArray().Count, node["item_count"]!.GetValue<int>());
            Assert.Equal(Evidence(label), node["files"]!["evidence"]!.GetValue<string>());
        }

        Assert.Equal(_cli.Http.Requests.Count, run.Json["result"]!["requests"]!.GetValue<int>());
        Assert.Contains(run.Json["issues"]!.AsArray(), issue => issue!["code"]!.GetValue<string>() == "source.truncated"
            && issue["message"]!.GetValue<string>().StartsWith("[2026-10] ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData]
    [InlineData("--from", "2026-08")]
    [InlineData("--to", "2026-10")]
    [InlineData("--period", "2026-10", "--from", "2026-08", "--to", "2026-10")]
    [InlineData("--from", "2026-08", "--to", "2026-10", "--output", "out.json")]
    [InlineData("--from", "2026-13", "--to", "2026-10")]
    [InlineData("--from", "2026-8", "--to", "2026-10")]
    [InlineData("--from", "2026-10", "--to", "2026-08")]
    [InlineData("--from", "2024-10", "--to", "2026-10")]
    [InlineData("--from", "0000-10", "--to", "0001-01")]
    [InlineData("--from", "9999-11", "--to", "9999-12")]
    [InlineData("--from", "0001-01", "--to", "0001-02")]
    public async Task Invalid_range_has_no_side_effects(params string[] options)
    {
        var before = Directory.GetFileSystemEntries(_cli.Workspace, "*", SearchOption.AllDirectories);
        var run = await _cli.RunAsync(["collect", .. options, "--json"]);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Equal("command.usage_error", run.Json["error"]!["code"]!.GetValue<string>());
        Assert.Empty(_cli.Http.Requests);
        Assert.Equal(before, Directory.GetFileSystemEntries(_cli.Workspace, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Dry_run_lists_month_sources_without_requests_or_directories()
    {
        _cli.Environment.Clear();
        var before = Directory.GetFileSystemEntries(_cli.Workspace, "*", SearchOption.AllDirectories);
        var run = await Run("--dry-run", "--json");

        Assert.True(run.ExitCode == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.True(run.Json["result"]!["dry_run"]!.GetValue<bool>());
        Assert.Equal(Path.Combine(_cli.Workspace, "worklens.json"), run.Json["result"]!["files"]!["config"]!.GetValue<string>());
        Assert.Equal(3, run.Json["result"]!["months"]!.AsArray().Count);
        Assert.All(run.Json["result"]!["months"]!.AsArray(), month =>
        {
            Assert.Single(month!["sources"]!.AsArray());
            var label = month["period"]!["label"]!.GetValue<string>();
            Assert.Equal(Evidence(label), month["files"]!["evidence"]!.GetValue<string>());
        });
        Assert.Empty(_cli.Http.Requests);
        Assert.Equal(before, Directory.GetFileSystemEntries(_cli.Workspace, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("source", 4, "source.unauthorized", "failed")]
    [InlineData("write", 3, "output.write_failed", "failed")]
    [InlineData("cancel", 130, "command.cancelled", "cancelled")]
    [InlineData("internal", 70, "command.internal_error", "failed")]
    public async Task Stop_preserves_written_months_and_reports_the_cause(string mode, int exit, string code, string status)
    {
        using var cancel = new CancellationTokenSource();
        if (mode == "source")
        {
            _cli.Http = new FixtureHandler().Add("GET", "/repos/octo/hello",
                Respond(200, new JsonObject { ["default_branch"] = "main" }),
                Respond(401, new JsonObject { ["message"] = "Bad credentials" }))
                .AddDirectory(MonthlyFixtures).AddDirectory(RangeFixtures);
        }
        else
        {
            _cli.GitProbe = new SecondMonthProbe(mode, cancel);
        }

        var run = await _cli.RunAsync(cancel.Token, "collect", "--from", "2026-08", "--to", "2026-10", "--json");

        Assert.Equal(exit, run.ExitCode);
        Assert.True(File.Exists(Evidence("2026-08")));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Evidence("2026-09"))));
        Assert.False(Directory.Exists(Path.GetDirectoryName(Evidence("2026-10"))));
        var months = run.Json["result"]!["months"]!.AsArray();
        Assert.Equal(new[] { "written", status, "skipped" }, months.Select(m => m!["status"]!.GetValue<string>()));
        Assert.Null(months[1]!["document_id"]);
        Assert.Null(months[1]!["item_count"]);
        Assert.Equal(code, run.Json["error"]!["code"]!.GetValue<string>());
        var message = run.Json["error"]!["message"]!.GetValue<string>();
        Assert.Contains("2026-08", message, StringComparison.Ordinal);
        Assert.DoesNotContain("no file was written", message, StringComparison.Ordinal);
        Assert.Contains(mode switch
        {
            "source" => "returned 401",
            "write" => "Output directory does not exist",
            "cancel" => "The command was cancelled.",
            _ => "probe failed",
        }, message, StringComparison.Ordinal);
        if (mode == "source")
        {
            Assert.Equal("gh", run.Json["error"]!["details"]!["source"]!.GetValue<string>());
        }
        else if (mode == "write")
        {
            Assert.Equal(Evidence("2026-09"), run.Json["error"]!["details"]!["file"]!.GetValue<string>());
        }
        else
        {
            Assert.Null(run.Json["error"]!["details"]);
        }
        Assert.Equal(_cli.Http.Requests.Count, run.Json["result"]!["requests"]!.GetValue<int>());
    }

    [Fact]
    public async Task Human_output_has_one_line_per_month()
    {
        var run = await Run();
        Assert.True(run.ExitCode == ExitCodes.Success, run.Stdout + run.Stderr);
        Assert.Equal(3, run.Stdout.Split('\n').Count(line => line.StartsWith("2026-", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Human_dry_run_shows_the_plan_for_each_month()
    {
        var run = await Run("--dry-run");
        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var lines = run.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line =>
        {
            Assert.Contains("octo/hello", line, StringComparison.Ordinal);
            Assert.Contains("GET https://api.github.com/repos/octo/hello", line, StringComparison.Ordinal);
            Assert.Contains("WORKLENS_GITHUB_TOKEN set", line, StringComparison.Ordinal);
            Assert.Contains("Pull requests: read", line, StringComparison.Ordinal);
        });
        Assert.Contains("2026-07-31T16:00:00Z to 2026-08-31T16:00:00Z", lines[0], StringComparison.Ordinal);
        Assert.Empty(_cli.Http.Requests);
    }

    private sealed class SecondMonthProbe(string mode, CancellationTokenSource cancel) : IGitProbe
    {
        private int _calls;

        public Task<GitIgnoreStatus> CheckIgnoredAsync(string directory, string fileName, CancellationToken cancellationToken)
        {
            if (++_calls == 2)
            {
                if (mode == "cancel")
                {
                    cancel.Cancel();
                }
                else if (mode == "write")
                {
                    Directory.Delete(directory);
                }
                else
                {
                    throw new InvalidOperationException("probe failed");
                }
            }
            return Task.FromResult(GitIgnoreStatus.Unknown);
        }
    }
}
