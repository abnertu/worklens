using System.Text.Json;
using System.Text.Json.Nodes;
using WorkLens.Core.Evidence;
using WorkLens.Core.Validation;
using WorkLens.Tests.Shared;
using Xunit;
using static WorkLens.Tests.Shared.FixtureHandler;

namespace WorkLens.Cli.Tests;

/// <summary>
/// <c>collect</c> end to end on tests/Fixtures/github/monthly (octo/hello, Asia/Taipei 2026-10), with the
/// configuration checks that must stop the command before any request.
/// </summary>
public sealed class CollectCommandTests : IDisposable
{
    internal const string Token = "ghp_0123456789abcdefghijABCDEFGHIJ012345";

    private readonly CliRunner _cli = new();

    public CollectCommandTests()
    {
        _cli.Http = Monthly();
        _cli.Environment["WORKLENS_GITHUB_TOKEN"] = Token;
    }

    public void Dispose() => _cli.Dispose();

    private string EvidencePath => Path.Combine(_cli.Workspace, "evidence.json");

    internal static FixtureHandler Monthly() =>
        FromDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github", "monthly"));

    internal static JsonObject Source(string id = "gh", string baseUrl = "https://api.github.com", string repository = "octo/hello", string platform = "github") =>
        new() { ["id"] = id, ["platform"] = platform, ["base_url"] = baseUrl, ["repository"] = repository };

    internal static JsonObject Config(params JsonObject[] sources) => new()
    {
        ["config_version"] = "1.0",
        ["timezone"] = "Asia/Taipei",
        ["identities"] = new JsonArray(
            new JsonObject { ["platform"] = "github", ["username"] = "alice" },
            new JsonObject { ["email"] = "alice@example.com" }),
        ["sources"] = new JsonArray([.. (sources.Length == 0 ? [Source()] : sources)]),
    };

    internal static void WriteConfig(string path, JsonObject config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, config.ToJsonString());
    }

    private void WriteConfig(params JsonObject[] sources) => WriteConfig(Path.Combine(_cli.Workspace, "worklens.json"), Config(sources));

    private static string ErrorCode(CliRun run) => run.Json["error"]!["code"]!.GetValue<string>();

    private void AssertNothingRequested() => Assert.Empty(_cli.Http.Requests);

    [Fact]
    public async Task Collect_writes_schema_valid_evidence_and_reports_the_result()
    {
        WriteConfig();

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Empty(_cli.Http.Unmatched);
        var bytes = await File.ReadAllBytesAsync(EvidencePath, TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(bytes);
        Assert.True(new IrSchemaValidator().Validate(EvidenceDocument.SchemaId, json.RootElement).IsValid);

        var result = run.Json["result"]!;
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(EvidencePath, result["files"]!["evidence"]!.GetValue<string>());
        Assert.Equal(json.RootElement.GetProperty("document_id").GetString(), result["document_id"]!.GetValue<string>());
        Assert.StartsWith("evd-2026-10-", result["document_id"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("2026-09-30T16:00:00Z", result["period"]!["start"]!.GetValue<string>());
        Assert.Equal("2026-10-31T16:00:00Z", result["period"]!["end"]!.GetValue<string>());
        Assert.Equal(items.Count(i => i.GetProperty("kind").GetString() == "commit"), result["items_by_kind"]!["commit"]!.GetValue<int>());
        Assert.Equal(items.Count(i => i.GetProperty("kind").GetString() == "change_request"), result["items_by_kind"]!["change_request"]!.GetValue<int>());
        Assert.True(result["items_by_kind"]!["commit"]!.GetValue<int>() > 0);
        Assert.Equal(_cli.Http.Requests.Count, result["requests"]!.GetValue<int>());

        // The fixture has a pull request with fewer commits than its detail says: a warning, still exit 0.
        var issues = run.Json["issues"]!.AsArray();
        Assert.Contains(issues, i => i!["code"]!.GetValue<string>() == "source.truncated" && i["severity"]!.GetValue<string>() == "warning");
        Assert.Equal(issues.Count, result["warnings"]!.GetValue<int>());
        Assert.True(run.Json["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Collect_is_reproducible()
    {
        WriteConfig();

        Assert.Equal(ExitCodes.Success, (await _cli.RunAsync("collect", "--period", "2026-10")).ExitCode);
        var first = await File.ReadAllBytesAsync(EvidencePath, TestContext.Current.CancellationToken);
        _cli.Http = Monthly();
        Assert.Equal(ExitCodes.Success, (await _cli.RunAsync("collect", "--period", "2026-10")).ExitCode);
        var second = await File.ReadAllBytesAsync(EvidencePath, TestContext.Current.CancellationToken);
        Assert.Equal(first, second);

        _cli.Http = Monthly();
        _cli.Clock = new FixedTimeProvider(new DateTimeOffset(2026, 12, 1, 8, 30, 0, TimeSpan.Zero));
        Assert.Equal(ExitCodes.Success, (await _cli.RunAsync("collect", "--period", "2026-10")).ExitCode);
        var third = await File.ReadAllBytesAsync(EvidencePath, TestContext.Current.CancellationToken);
        Assert.NotEqual(first, third);
        Assert.True(JsonNode.DeepEquals(WithoutRunTimes(first), WithoutRunTimes(third)));
    }

    private static JsonNode WithoutRunTimes(byte[] evidence)
    {
        var document = JsonNode.Parse(evidence)!.AsObject();
        document.Remove("generated_at");
        foreach (var item in document["items"]!.AsArray())
        {
            item!.AsObject().Remove("fetched_at");
        }

        return document;
    }

    [Fact]
    public async Task Source_option_collects_only_the_selected_sources()
    {
        WriteConfig(Source(), Source("other", repository: "octo/other"));

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--source", "gh", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.DoesNotContain(_cli.Http.Requests, r => r.RequestUri!.AbsolutePath.Contains("octo/other", StringComparison.OrdinalIgnoreCase));
        var sources = JsonNode.Parse(await File.ReadAllBytesAsync(EvidencePath, TestContext.Current.CancellationToken))!["query"]!["sources"]!.AsArray();
        Assert.Equal("octo/hello", Assert.Single(sources)!["repository"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_source_is_a_configuration_error()
    {
        WriteConfig();

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--source", "nope", "--json");

        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        Assert.Equal("config.unknown_source", ErrorCode(run));
        AssertNothingRequested();
    }

    [Fact]
    public async Task Token_env_is_read_without_falling_back_to_the_default_variable()
    {
        var source = Source();
        source["token_env"] = "MY_GITHUB_TOKEN";
        WriteConfig(source);

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        Assert.Equal("config.missing_token", ErrorCode(run));
        Assert.Contains("MY_GITHUB_TOKEN", run.Json["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        AssertNothingRequested();
    }

    [Fact]
    public async Task Enterprise_source_without_token_env_does_not_get_the_default_token()
    {
        WriteConfig(Source(baseUrl: "https://ghe.example/api/v3"));

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        Assert.Equal("config.missing_token", ErrorCode(run));
        AssertNothingRequested();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Empty_token_counts_as_missing(string value)
    {
        WriteConfig();
        _cli.Environment["WORKLENS_GITHUB_TOKEN"] = value;

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        Assert.Equal("config.missing_token", ErrorCode(run));
    }

    [Fact]
    public async Task Github_token_is_the_fallback_for_api_github_com()
    {
        WriteConfig();
        _cli.Environment.Clear();
        _cli.Environment["GITHUB_TOKEN"] = Token;

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.All(_cli.Http.Requests, r => Assert.Equal($"Bearer {Token}", r.Headers.Authorization!.ToString()));
    }

    [Fact]
    public async Task Http_base_url_is_rejected()
    {
        WriteConfig(Source(baseUrl: "http://api.github.com"));

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        Assert.Equal("config.invalid", ErrorCode(run));
        AssertNothingRequested();
    }

    [Fact]
    public async Task Secret_in_the_configuration_is_rejected_without_echoing_it()
    {
        var source = Source();
        source["token"] = Token;
        WriteConfig(source);

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        Assert.Equal("config.secret_in_file", ErrorCode(run));
        Assert.Contains("/sources/0/token", run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, run.Stdout + run.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("--period", "2026-13")]
    [InlineData("--period", "2026-1")]
    [InlineData("--period", "0000-10")]
    [InlineData("--period", "9999-12")]
    [InlineData("--period", "0001-01")]
    [InlineData("--period", "\u0662\u0660\u0662\u0666-10")]
    [InlineData("--period", "2026-10\n")]
    public async Task Missing_or_malformed_period_is_a_usage_error(params string[] period)
    {
        WriteConfig();

        var run = await _cli.RunAsync(["collect", .. period, "--json"]);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Equal("command.usage_error", ErrorCode(run));
        AssertNothingRequested();
    }

    [Fact]
    public async Task Dry_run_sends_nothing_and_writes_nothing()
    {
        WriteConfig();
        _cli.Environment.Clear();
        var before = Directory.GetFileSystemEntries(_cli.Workspace, "*", SearchOption.AllDirectories);

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--dry-run", "--output", "new/dir/evidence.json", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        AssertNothingRequested();
        Assert.Equal(before, Directory.GetFileSystemEntries(_cli.Workspace, "*", SearchOption.AllDirectories));
        var result = run.Json["result"]!;
        Assert.True(result["dry_run"]!.GetValue<bool>());
        Assert.Equal("2026-09-30T16:00:00Z", result["period"]!["start"]!.GetValue<string>());
        var source = Assert.Single(result["sources"]!.AsArray())!;
        Assert.Equal("gh", source["id"]!.GetValue<string>());
        Assert.Equal("WORKLENS_GITHUB_TOKEN", source["token"]!["variable"]!.GetValue<string>());
        Assert.False(source["token"]!["set"]!.GetValue<bool>());
        Assert.Contains("Pull requests: read", source["scopes"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Contains(source["endpoints"]!.AsArray(), e => e!.GetValue<string>().StartsWith("GET https://api.github.com/repos/octo/hello/commits?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dry_run_lists_an_enterprise_source_without_a_variable()
    {
        WriteConfig(Source(baseUrl: "https://ghe.example/api/v3"));

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--dry-run", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var token = run.Json["result"]!["sources"]![0]!["token"]!;
        Assert.Null(token["variable"]);
        Assert.False(token["set"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(nameof(GitIgnoreStatus.NotIgnored), true)]
    [InlineData(nameof(GitIgnoreStatus.Ignored), false)]
    [InlineData(nameof(GitIgnoreStatus.Unknown), false)]
    public async Task Output_that_git_does_not_ignore_gets_a_warning(string status, bool warned)
    {
        WriteConfig();
        var probe = new FakeGitProbe(Enum.Parse<GitIgnoreStatus>(status));
        _cli.GitProbe = probe;

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal((_cli.Workspace, "evidence.json"), Assert.Single(probe.Calls));
        Assert.Equal(warned, run.Json["issues"]!.AsArray().Any(i => i!["code"]!.GetValue<string>() == "output.not_ignored"));
    }

    [Fact]
    public async Task Source_failure_is_exit_4_and_writes_nothing()
    {
        WriteConfig();
        _cli.Http = new FixtureHandler().Add("GET", "/repos/octo/hello", Respond(401, new JsonObject { ["message"] = "Bad credentials" }));

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Source, run.ExitCode);
        Assert.Equal("source.unauthorized", ErrorCode(run));
        Assert.Equal("gh", run.Json["error"]!["details"]!["source"]!.GetValue<string>());
        Assert.False(File.Exists(EvidencePath));
    }

    [Fact]
    public async Task Server_errors_retry_with_the_host_delay_then_exit_4()
    {
        WriteConfig();
        _cli.Http = new FixtureHandler().Add("GET", "/repos/octo/hello", Respond(502));

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Source, run.ExitCode);
        Assert.Equal("source.server_error", ErrorCode(run));
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], _cli.Delays);
        Assert.Equal(4, _cli.Http.Requests.Count);
        Assert.False(File.Exists(EvidencePath));
    }

    [Fact]
    public async Task Cancellation_is_exit_130_and_leaves_no_files()
    {
        WriteConfig();
        using var cancel = new CancellationTokenSource();
        _cli.Http.CancelOnRequest(2, cancel);
        var before = Directory.GetFileSystemEntries(_cli.Workspace, "*", SearchOption.AllDirectories);

        var run = await _cli.RunAsync(cancel.Token, "collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Cancelled, run.ExitCode);
        Assert.Equal("command.cancelled", ErrorCode(run));
        Assert.Equal(before, Directory.GetFileSystemEntries(_cli.Workspace, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Cancellation_during_git_probe_preserves_the_output(bool existingOutput, bool throwOnCancellation)
    {
        WriteConfig();
        byte[] original = [1, 2, 3];
        if (existingOutput)
        {
            File.WriteAllBytes(EvidencePath, original);
        }

        using var cancel = new CancellationTokenSource();
        _cli.GitProbe = new CancellingGitProbe(cancel, throwOnCancellation);
        var before = Directory.GetFileSystemEntries(_cli.Workspace, "*", SearchOption.AllDirectories);

        var run = await _cli.RunAsync(cancel.Token, "collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Cancelled, run.ExitCode);
        Assert.Equal("command.cancelled", ErrorCode(run));
        Assert.Equal(before, Directory.GetFileSystemEntries(_cli.Workspace, "*", SearchOption.AllDirectories));
        if (existingOutput)
        {
            Assert.Equal(original, File.ReadAllBytes(EvidencePath));
        }
    }

    private sealed class CancellingGitProbe(CancellationTokenSource cancel, bool throwOnCancellation) : IGitProbe
    {
        public Task<GitIgnoreStatus> CheckIgnoredAsync(string directory, string fileName, CancellationToken cancellationToken)
        {
            cancel.Cancel();
            if (throwOnCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.FromResult(GitIgnoreStatus.Unknown);
        }
    }

    [Fact]
    public async Task Configuration_is_found_in_the_home_directory()
    {
        WriteConfig(Path.Combine(_cli.Home, ".worklens", "worklens.json"), Config());

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(Path.Combine(_cli.Home, ".worklens", "worklens.json"), run.Json["result"]!["files"]!["config"]!.GetValue<string>());
    }

    [Fact]
    public async Task Configuration_variable_wins_over_the_current_directory()
    {
        WriteConfig(Source(repository: "octo/nowhere"));
        WriteConfig(Path.Combine(_cli.Home, "elsewhere.json"), Config());
        _cli.Environment["WORKLENS_CONFIG"] = Path.Combine(_cli.Home, "elsewhere.json");

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(Path.Combine(_cli.Home, "elsewhere.json"), run.Json["result"]!["files"]!["config"]!.GetValue<string>());
    }

    [Fact]
    public async Task Missing_configuration_is_config_not_found()
    {
        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        Assert.Equal("config.not_found", ErrorCode(run));
    }

    [Fact]
    public async Task Output_directory_is_created()
    {
        WriteConfig();

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--output", "out/2026-10/evidence.json", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.True(File.Exists(Path.Combine(_cli.Workspace, "out", "2026-10", "evidence.json")));
    }

    [Fact]
    public async Task Verbose_logs_each_request_to_stderr()
    {
        WriteConfig();

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--verbose", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var lines = run.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(_cli.Http.Requests.Count, lines.Count(l => l.StartsWith("http: GET https://api.github.com/", StringComparison.Ordinal) && l.Contains(" 200 ", StringComparison.Ordinal)));
        Assert.DoesNotContain(Token, run.Stderr, StringComparison.Ordinal);
    }
}
