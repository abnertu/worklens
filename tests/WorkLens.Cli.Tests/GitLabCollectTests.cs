using System.Text.Json;
using System.Text.Json.Nodes;
using WorkLens.Connectors.Git;
using WorkLens.Connectors.Git.GitLab;
using WorkLens.Core.Evidence;
using WorkLens.Core.Validation;
using WorkLens.Tests.Shared;
using Xunit;
using static WorkLens.Tests.Shared.FixtureHandler;

namespace WorkLens.Cli.Tests;

/// <summary>
/// <c>collect</c> with GitLab sources on tests/Fixtures/gitlab/monthly (<c>group/sub/app</c>, project ID 7,
/// Asia/Taipei 2026-10), alone and mixed with the GitHub fixture.
/// </summary>
public sealed class GitLabCollectTests : IDisposable
{
    internal const string SelfManaged = "https://gitlab.example.com/api/v4";
    internal const string Repository = "group/sub/app";
    internal const string GitLabToken = "glpat-0123456789abcdefABCDEF";
    private const string Project = "/api/v4/projects/7";
    private const string CommitList = Project + "/repository/commits?ref_name=main&since=2026-09-30T16:00:00Z&until=2026-11-30T16:00:00Z&per_page=100";
    private const string MergeRequestList = Project + "/merge_requests?state=all&scope=all&order_by=updated_at&sort=desc&updated_after=2026-09-30T16:00:00Z&per_page=100";

    private readonly CliRunner _cli = new();

    public GitLabCollectTests()
    {
        _cli.Http = Monthly();
        _cli.Environment["CORP_GITLAB"] = GitLabToken;
    }

    public void Dispose() => _cli.Dispose();

    private string EvidencePath => Path.Combine(_cli.Workspace, "evidence.json");

    private static string FixtureDirectory(string platform) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", platform, "monthly");

    internal static FixtureHandler Monthly() => FromDirectory(FixtureDirectory("gitlab"));

    internal static JsonObject Source(string id = "gl", string baseUrl = SelfManaged, string? tokenEnv = "CORP_GITLAB")
    {
        var source = CollectCommandTests.Source(id, baseUrl, Repository, "gitlab");
        if (tokenEnv is not null)
        {
            source["token_env"] = tokenEnv;
        }

        return source;
    }

    /// <summary>The GitHub fixture's identities plus the GitLab fixture's username and second email.</summary>
    internal static JsonObject Config(params JsonObject[] sources)
    {
        var config = CollectCommandTests.Config(sources.Length == 0 ? [Source()] : sources);
        var identities = config["identities"]!.AsArray();
        identities.Add(new JsonObject { ["platform"] = "gitlab", ["username"] = "alice" });
        identities.Add(new JsonObject { ["email"] = "alice@users.example.org" });
        return config;
    }

    private void WriteConfig(params JsonObject[] sources) =>
        CollectCommandTests.WriteConfig(Path.Combine(_cli.Workspace, "worklens.json"), Config(sources));

    private static string ErrorCode(CliRun run) => run.Json["error"]!["code"]!.GetValue<string>();

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private byte[] WriteExistingOutput()
    {
        var bytes = "{\"existing\":true}\n"u8.ToArray();
        File.WriteAllBytes(EvidencePath, bytes);
        return bytes;
    }

    private void AssertOutput(byte[] expected) => Assert.Equal(expected, File.ReadAllBytes(EvidencePath));

    [Fact]
    public async Task GitLab_source_writes_schema_valid_evidence()
    {
        WriteConfig();

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Empty(_cli.Http.Unmatched);
        var bytes = await File.ReadAllBytesAsync(EvidencePath, TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(bytes);
        Assert.True(new IrSchemaValidator().Validate(EvidenceDocument.SchemaId, json.RootElement).IsValid);
        var source = Assert.Single(json.RootElement.GetProperty("query").GetProperty("sources").EnumerateArray());
        Assert.Equal("gitlab", source.GetProperty("platform").GetString());
        Assert.Equal(SelfManaged, source.GetProperty("base_url").GetString());

        var result = run.Json["result"]!;
        Assert.Equal(7, result["items_by_kind"]!["commit"]!.GetValue<int>());
        Assert.Equal(4, result["items_by_kind"]!["change_request"]!.GetValue<int>());
        Assert.Equal(_cli.Http.Requests.Count, result["requests"]!.GetValue<int>());
        Assert.Empty(run.Json["issues"]!.AsArray());
        Assert.All(_cli.Http.Requests, r =>
        {
            Assert.Equal(GitLabToken, Header(r, GitLabHttp.TokenHeader));
            Assert.Null(r.Headers.Authorization);
        });
    }

    [Fact]
    public async Task GitLab_collect_is_reproducible_with_a_fixed_clock()
    {
        WriteConfig();

        Assert.Equal(ExitCodes.Success, (await _cli.RunAsync("collect", "--period", "2026-10")).ExitCode);
        var first = await File.ReadAllBytesAsync(EvidencePath, TestContext.Current.CancellationToken);
        _cli.Http = Monthly();
        Assert.Equal(ExitCodes.Success, (await _cli.RunAsync("collect", "--period", "2026-10")).ExitCode);

        Assert.Equal(first, await File.ReadAllBytesAsync(EvidencePath, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mixed_sources_run_in_configuration_order_with_their_own_tokens(bool gitHubFirst)
    {
        JsonObject[] sources = gitHubFirst ? [CollectCommandTests.Source(), Source()] : [Source(), CollectCommandTests.Source()];
        WriteConfig(sources);
        _cli.Environment["WORKLENS_GITHUB_TOKEN"] = CollectCommandTests.Token;
        _cli.Environment["WORKLENS_GITLAB_TOKEN"] = "glpat-defaultdefaultdefault00";
        _cli.Http = CollectCommandTests.Monthly().AddDirectory(FixtureDirectory("gitlab"));

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Empty(_cli.Http.Unmatched);
        var requests = _cli.Http.Requests;
        var hosts = requests.Select(r => r.RequestUri!.Host).Distinct().ToList();
        Assert.Equal(gitHubFirst ? ["api.github.com", "gitlab.example.com"] : ["gitlab.example.com", "api.github.com"], hosts);
        foreach (var request in requests)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                Assert.Equal(CollectCommandTests.Token, request.Headers.Authorization?.Parameter);
                Assert.Null(Header(request, GitLabHttp.TokenHeader));
            }
            else
            {
                Assert.Equal(GitLabToken, Header(request, GitLabHttp.TokenHeader));
                Assert.Null(request.Headers.Authorization);
            }
        }

        var document = JsonNode.Parse(await File.ReadAllBytesAsync(EvidencePath, TestContext.Current.CancellationToken))!;
        var platforms = document["query"]!["sources"]!.AsArray().Select(s => s!["platform"]!.GetValue<string>());
        Assert.Equal(gitHubFirst ? ["github", "gitlab"] : ["gitlab", "github"], platforms);
        var items = document["items"]!.AsArray().Select(i => i!["source"]!["platform"]!.GetValue<string>()).ToList();
        Assert.Contains("github", items);
        Assert.Contains("gitlab", items);
        Assert.Equal(requests.Count, run.Json["result"]!["requests"]!.GetValue<int>());

        // The GitHub fixture's truncated pull request warns in either order.
        Assert.Contains(run.Json["issues"]!.AsArray(), i => i!["code"]!.GetValue<string>() == SourceErrors.Truncated);
    }

    [Theory]
    [InlineData(SelfManaged, "WORKLENS_GITLAB_TOKEN")]
    [InlineData("https://gitlab.com/api/v4", "WORKLENS_GITHUB_TOKEN")]
    [InlineData("https://gitlab.com/api/v4", "GITHUB_TOKEN")]
    [InlineData("https://gitlab.com/gitlab/api/v4", "WORKLENS_GITLAB_TOKEN")]
    public async Task GitLab_source_without_token_env_gets_no_other_default_token(string baseUrl, string variable)
    {
        WriteConfig(Source(baseUrl: baseUrl, tokenEnv: null));
        _cli.Environment.Clear();
        _cli.Environment[variable] = GitLabToken;

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        Assert.Equal("config.missing_token", ErrorCode(run));
        Assert.Empty(_cli.Http.Requests);
    }

    [Fact]
    public async Task GitHub_source_does_not_get_the_default_GitLab_token()
    {
        WriteConfig(CollectCommandTests.Source());
        _cli.Environment.Clear();
        _cli.Environment["WORKLENS_GITLAB_TOKEN"] = GitLabToken;

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal("config.missing_token", ErrorCode(run));
        Assert.Empty(_cli.Http.Requests);
    }

    [Theory]
    [InlineData("https://gitlab.com/api/v4")]
    [InlineData("https://GitLab.com/api/v4/")]
    public async Task GitLab_com_uses_the_default_GitLab_token(string baseUrl)
    {
        WriteConfig(Source(baseUrl: baseUrl, tokenEnv: null));
        _cli.Environment.Clear();
        _cli.Environment["WORKLENS_GITLAB_TOKEN"] = GitLabToken;
        _cli.Environment["WORKLENS_GITHUB_TOKEN"] = CollectCommandTests.Token;
        _cli.Http = new FixtureHandler().Add("GET", "/api/v4/projects/group%2Fsub%2Fapp", Respond(401));

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Source, run.ExitCode);
        Assert.Equal(SourceErrors.Unauthorized, ErrorCode(run));
        var request = Assert.Single(_cli.Http.Requests);
        Assert.Equal("gitlab.com", request.RequestUri!.Host);
        Assert.Equal(GitLabToken, Header(request, GitLabHttp.TokenHeader));
    }

    [Fact]
    public async Task Configured_token_env_does_not_fall_back_to_the_default_GitLab_token()
    {
        WriteConfig(Source(baseUrl: "https://gitlab.com/api/v4", tokenEnv: "MISSING_GITLAB"));
        _cli.Environment["WORKLENS_GITLAB_TOKEN"] = GitLabToken;

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal("config.missing_token", ErrorCode(run));
        Assert.Contains("MISSING_GITLAB", run.Json["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(_cli.Http.Requests);
    }

    [Fact]
    public async Task Dry_run_lists_GitLab_endpoints_variables_and_scope()
    {
        WriteConfig(Source(), Source("com", "https://gitlab.com/api/v4", tokenEnv: null), Source("bare", "https://git.example.org/api/v4", tokenEnv: null));
        _cli.Environment.Clear();
        _cli.Environment["CORP_GITLAB"] = GitLabToken;

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--dry-run", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Empty(_cli.Http.Requests);
        Assert.False(File.Exists(EvidencePath));
        var sources = run.Json["result"]!["sources"]!.AsArray();
        Assert.Equal(["gl", "com", "bare"], sources.Select(s => s!["id"]!.GetValue<string>()));
        Assert.All(sources, s =>
        {
            Assert.Equal("gitlab", s!["platform"]!.GetValue<string>());
            Assert.Equal(["read_api"], s["scopes"]!.AsArray().Select(e => e!.GetValue<string>()));
        });

        var endpoints = sources[0]!["endpoints"]!.AsArray().Select(e => e!.GetValue<string>()).ToList();
        Assert.Equal($"GET {SelfManaged}/projects/group%2Fsub%2Fapp", endpoints[0]);
        Assert.Contains($"GET {SelfManaged}/projects/<id>/repository/commits?ref_name=<default branch>&since=2026-09-30T16:00:00Z&until=2026-11-30T16:00:00Z&per_page=100", endpoints);
        Assert.Contains($"GET {SelfManaged}/projects/<id>/merge_requests?state=all&scope=all&order_by=updated_at&sort=desc&updated_after=2026-09-30T16:00:00Z&per_page=100", endpoints);
        Assert.Contains($"GET {SelfManaged}/projects/<id>/repository/commits/<sha>/diff?per_page=100", endpoints);
        Assert.Contains($"GET {SelfManaged}/projects/<id>/merge_requests/<iid>/commits?per_page=100", endpoints);

        Assert.Equal("CORP_GITLAB", sources[0]!["token"]!["variable"]!.GetValue<string>());
        Assert.True(sources[0]!["token"]!["set"]!.GetValue<bool>());
        Assert.Equal("WORKLENS_GITLAB_TOKEN", sources[1]!["token"]!["variable"]!.GetValue<string>());
        Assert.False(sources[1]!["token"]!["set"]!.GetValue<bool>());
        Assert.Null(sources[2]!["token"]!["variable"]);
        Assert.DoesNotContain(GitLabToken, run.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitLab_forbidden_does_not_wait_like_a_GitHub_rate_limit_and_preserves_the_output()
    {
        WriteConfig();
        var existing = WriteExistingOutput();
        _cli.Http = new FixtureHandler().Add(
            "GET",
            "/api/v4/projects/group%2Fsub%2Fapp",
            Respond(403, new JsonObject { ["message"] = "403 Forbidden" }, ("X-RateLimit-Remaining", "0"), ("X-RateLimit-Reset", "1794441600")));

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Source, run.ExitCode);
        Assert.Equal(SourceErrors.Forbidden, ErrorCode(run));
        Assert.Equal("gl", run.Json["error"]!["details"]!["source"]!.GetValue<string>());
        Assert.Empty(_cli.Delays);
        Assert.Single(_cli.Http.Requests);
        AssertOutput(existing);
    }

    [Fact]
    public async Task GitLab_rate_limit_waits_for_retry_after()
    {
        WriteConfig();
        var handler = new FixtureHandler().Add(
            "GET",
            "/api/v4/projects/group%2Fsub%2Fapp",
            Respond(429, null, ("Retry-After", "7")),
            Respond(429, null, ("Retry-After", "7")),
            Respond(429, null, ("Retry-After", "7")),
            Respond(429, null, ("Retry-After", "7")));
        _cli.Http = handler;

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(SourceErrors.RateLimited, ErrorCode(run));
        Assert.Equal([TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(7)], _cli.Delays);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Exhausted_headerless_pagination_fails_and_preserves_the_output()
    {
        WriteConfig();
        var existing = WriteExistingOutput();
        var handler = new FixtureHandler()
            .Add("GET", "/api/v4/projects/group%2Fsub%2Fapp", Respond(200, new JsonObject { ["id"] = 7, ["default_branch"] = "main", ["empty_repo"] = false }));
        for (var page = 1; page <= GitLabApiClient.MaxHeaderlessPages + 1; page++)
        {
            // Full, distinct pages after the month, with no pagination headers: only the page limit stops them.
            var commits = new JsonArray([.. Enumerable.Range(0, GitLabApiClient.PageSize).Select(i => (JsonNode)new JsonObject
            {
                ["id"] = $"{page:x8}{i:x32}",
                ["authored_date"] = "2026-11-20T00:00:00Z",
                ["author_email"] = "alice@example.com",
            })]);
            handler.Add("GET", page == 1 ? CommitList : $"{CommitList}&page={page}", Respond(200, commits));
        }

        _cli.Http = handler;

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Source, run.ExitCode);
        Assert.Equal(SourceErrors.InvalidResponse, ErrorCode(run));
        Assert.Equal(1 + GitLabApiClient.MaxHeaderlessPages, handler.Requests.Count);
        AssertOutput(existing);
    }

    [Fact]
    public async Task GitLab_truncation_warning_reaches_the_result_with_exit_0()
    {
        WriteConfig();
        var sha = new string('d', 40);
        var commit = new JsonObject
        {
            ["id"] = sha,
            ["message"] = "Huge change",
            ["author_name"] = "Alice",
            ["author_email"] = "alice@example.com",
            ["authored_date"] = "2026-10-05T00:00:00Z",
            ["parent_ids"] = new JsonArray(new string('0', 40)),
            ["web_url"] = $"https://gitlab.example.com/group/sub/app/-/commit/{sha}",
        };
        var detail = commit.DeepClone().AsObject();
        detail["stats"] = new JsonObject { ["additions"] = 3100, ["deletions"] = 0 };
        var handler = new FixtureHandler()
            .Add("GET", "/api/v4/projects/group%2Fsub%2Fapp", Respond(200, new JsonObject { ["id"] = 7, ["default_branch"] = "main", ["empty_repo"] = false }))
            .Add("GET", CommitList, Respond(200, new JsonArray(commit.DeepClone())))
            .Add("GET", MergeRequestList, Respond(200, new JsonArray()))
            .Add("GET", $"{Project}/repository/commits/{sha}", Respond(200, detail));
        var pages = GitLabConnector.MaxCommitFiles / GitLabApiClient.PageSize;
        for (var page = 1; page <= pages + 1; page++)
        {
            var files = new JsonArray([.. Enumerable.Range(0, GitLabApiClient.PageSize).Select(i => (JsonNode)new JsonObject { ["old_path"] = $"f/{page}/{i}", ["new_path"] = $"f/{page}/{i}" })]);
            var query = page == 1 ? "per_page=100" : $"per_page=100&page={page}";
            handler.Add("GET", $"{Project}/repository/commits/{sha}/diff?{query}", Respond(200, files, ("X-Next-Page", (page + 1).ToString())));
        }

        _cli.Http = handler;

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var issue = Assert.Single(run.Json["issues"]!.AsArray())!;
        Assert.Equal(SourceErrors.Truncated, issue["code"]!.GetValue<string>());
        Assert.Equal("warning", issue["severity"]!.GetValue<string>());
        Assert.Equal(1, run.Json["result"]!["warnings"]!.GetValue<int>());
    }

    [Fact]
    public async Task GitLab_cancellation_is_exit_130_and_preserves_the_output()
    {
        WriteConfig();
        var existing = WriteExistingOutput();
        using var cancel = new CancellationTokenSource();
        _cli.Http.CancelOnRequest(3, cancel);

        var run = await _cli.RunAsync(cancel.Token, "collect", "--period", "2026-10", "--json");

        Assert.Equal(ExitCodes.Cancelled, run.ExitCode);
        Assert.Equal("command.cancelled", ErrorCode(run));
        AssertOutput(existing);
        Assert.Equal(["evidence.json", "worklens.json"], Directory.GetFiles(_cli.Workspace).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Verbose_logs_the_GitLab_remaining_rate_limit()
    {
        WriteConfig();
        _cli.Http = new FixtureHandler()
            .Add("GET", "/api/v4/projects/group%2Fsub%2Fapp", Respond(200, JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureDirectory("gitlab"), "project.json")))!["responses"]![0]!["body"]!.DeepClone(), ("RateLimit-Remaining", "1999")))
            .AddDirectory(FixtureDirectory("gitlab"));

        var run = await _cli.RunAsync("collect", "--period", "2026-10", "--verbose", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var lines = run.Stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(_cli.Http.Requests.Count, lines.Count(l => l.StartsWith($"http: GET {SelfManaged}/", StringComparison.Ordinal)));
        Assert.Contains($"http: GET {SelfManaged}/projects/group%2Fsub%2Fapp 200 remaining=1999", lines);
        Assert.DoesNotContain(GitLabToken, run.Stderr, StringComparison.Ordinal);
    }
}
