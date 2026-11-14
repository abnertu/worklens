using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using WorkLens.Tests.Shared;
using Xunit;
using static WorkLens.Tests.Shared.FixtureHandler;

namespace WorkLens.Connectors.Git.Tests;

public sealed class GitHubApiClientTests
{
    private const string Token = "test-token-value";
    private const string RepoPath = "/repos/octo/hello";
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    private readonly List<TimeSpan> _delays = [];

    private GitHubApiClient Client(FixtureHandler handler, string baseUrl = "https://api.github.com")
    {
        var http = GitHubHttp.CreateClient(handler, new Uri(baseUrl), Token, "0.1.0");
        var retry = new RetryPolicy(new FixedTimeProvider(Now), (delay, _) =>
        {
            _delays.Add(delay);
            return Task.CompletedTask;
        });
        return new GitHubApiClient(http, retry);
    }

    private static JsonObject Ok(JsonNode? body = null, params (string, string)[] headers) =>
        Respond(200, body ?? new JsonObject { ["ok"] = true }, headers);

    private Task<GitHubResult<JsonElement>> GetRepo(FixtureHandler handler) =>
        Client(handler).GetAsync("repos/octo/hello", null, TestContext.Current.CancellationToken);

    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);

    // ---- request shape ----

    [Fact]
    public async Task Client_sends_the_GitHub_headers_and_query_format()
    {
        var handler = new FixtureHandler()
            .Add("GET", "/repos/octo/hello/commits?per_page=100&since=2026-09-30T16:00:00Z", Ok(new JsonArray()));
        var since = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(8));

        var result = await Client(handler).GetAllAsync(
            "repos/octo/hello/commits",
            [new("since", GitHubApiClient.FormatTime(since))],
            null,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("worklens/0.1.0", request.Headers.UserAgent.ToString());
        Assert.Equal("application/vnd.github+json", Assert.Single(request.Headers.Accept).MediaType);
        Assert.Equal("2022-11-28", Assert.Single(request.Headers.GetValues("X-GitHub-Api-Version")));
        Assert.Equal(new AuthenticationHeaderValue("Bearer", Token), request.Headers.Authorization);
        var query = Uri.UnescapeDataString(request.RequestUri!.Query);
        Assert.Contains("since=2026-09-30T16:00:00Z", query, StringComparison.Ordinal);
        Assert.Contains("per_page=100", query, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateClient_sets_the_timeout_and_normalized_base_address()
    {
        using var handler = new FixtureHandler();
        using var http = GitHubHttp.CreateClient(handler, new Uri("HTTPS://GHE.Example:443/api/v3/"), Token, "0.1.0");

        Assert.Equal(TimeSpan.FromSeconds(100), http.Timeout);
        Assert.Equal("https://ghe.example/api/v3/", http.BaseAddress!.AbsoluteUri);
    }

    [Fact]
    public async Task Enterprise_base_url_keeps_its_api_prefix()
    {
        var handler = new FixtureHandler().Add("GET", "/api/v3/repos/octo/hello", Ok());

        var result = await Client(handler, "https://ghe.example/api/v3")
            .GetAsync("repos/octo/hello", null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("https://ghe.example/api/v3/repos/octo/hello", Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
    }

    // ---- pagination ----

    [Fact]
    public async Task Link_pagination_fetches_all_three_pages()
    {
        var handler = FromDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github", "pagination"));

        var result = await Client(handler).GetAllAsync(
            "repos/octo/hello/commits",
            [new("since", "2026-10-01T00:00:00Z")],
            null,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            ["p1a", "p1b", "p2a", "p2b", "p3a", "p3b"],
            result.Value!.Select(e => e.GetProperty("sha").GetString()));
        Assert.Equal(3, handler.Requests.Count);
        Assert.Empty(handler.Unmatched);
    }

    [Fact]
    public async Task Paging_stops_when_the_callback_returns_false()
    {
        var handler = FromDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github", "pagination"));

        var result = await Client(handler).GetPagesAsync(
            "repos/octo/hello/commits",
            [new("since", "2026-10-01T00:00:00Z")],
            _ => false,
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Value);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("https://evil.example/repos/octo/hello?page=2")]
    [InlineData("http://api.github.com/repos/octo/hello?page=2")]
    [InlineData("https://api.github.com:8443/repos/octo/hello?page=2")]
    [InlineData("/repos/octo/hello?page=2")]
    public async Task Next_link_off_the_source_host_is_an_invalid_response(string next)
    {
        var handler = new FixtureHandler()
            .Add("GET", "/repos/octo/hello/pulls?per_page=100", Ok(new JsonArray(), ("Link", $"<{next}>; rel=\"next\"")));

        var result = await Client(handler).GetAllAsync(
            "repos/octo/hello/pulls", null, null, TestContext.Current.CancellationToken);

        Assert.Equal(SourceErrors.InvalidResponse, result.Error?.Code);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("api.github.com", request.RequestUri!.Host);
    }

    [Fact]
    public async Task Commits_409_is_an_empty_list()
    {
        var handler = new FixtureHandler().Add("GET", "/repos/octo/hello/commits?per_page=100", Respond(409));

        var result = await Client(handler).GetAllAsync(
            "repos/octo/hello/commits", null, GitHubRequestOptions.CommitList, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task Status_409_without_the_option_is_a_request_failure()
    {
        var handler = new FixtureHandler().Add("GET", RepoPath, Respond(409));

        Assert.Equal(SourceErrors.RequestFailed, (await GetRepo(handler)).Error?.Code);
    }

    // ---- rate limits ----

    [Fact]
    public async Task Success_with_zero_remaining_does_not_wait()
    {
        var handler = new FixtureHandler().Add("GET", RepoPath,
            Ok(null, ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", (Now.ToUnixTimeSeconds() + 30).ToString())));

        Assert.True((await GetRepo(handler)).IsSuccess);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Retry_after_seconds_is_honoured()
    {
        var handler = new FixtureHandler().Add("GET", RepoPath, Respond(429, null, ("Retry-After", "7")), Ok());

        Assert.True((await GetRepo(handler)).IsSuccess);
        Assert.Equal([Seconds(7)], _delays);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Retry_after_http_date_waits_the_difference()
    {
        var handler = new FixtureHandler().Add("GET", RepoPath,
            Respond(429, null, ("Retry-After", Now.AddSeconds(30).ToString("R"))), Ok());

        Assert.True((await GetRepo(handler)).IsSuccess);
        Assert.Equal([Seconds(30)], _delays);
    }

    [Fact]
    public async Task Retry_after_in_the_past_does_not_wait()
    {
        var handler = new FixtureHandler().Add("GET", RepoPath,
            Respond(429, null, ("Retry-After", Now.AddSeconds(-30).ToString("R"))), Ok());

        Assert.True((await GetRepo(handler)).IsSuccess);
        Assert.Equal([TimeSpan.Zero], _delays);
    }

    [Fact]
    public async Task Rate_limit_waits_at_most_three_times()
    {
        var handler = new FixtureHandler().Add("GET", RepoPath, Respond(429, null, ("Retry-After", "1")));

        var result = await GetRepo(handler);

        Assert.Equal(SourceErrors.RateLimited, result.Error?.Code);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal([Seconds(1), Seconds(1), Seconds(1)], _delays);
    }

    [Fact]
    public async Task Plain_429_backs_off_from_60_seconds_until_the_cap()
    {
        var handler = new FixtureHandler().Add("GET", RepoPath, Respond(429));

        var result = await GetRepo(handler);

        Assert.Equal(SourceErrors.RateLimited, result.Error?.Code);
        Assert.Equal([Seconds(60), Seconds(120)], _delays);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Secondary_limit_403_without_retry_after_waits_60_seconds()
    {
        var body = new JsonObject { ["message"] = "You have exceeded a secondary rate limit." };
        var handler = new FixtureHandler().Add("GET", RepoPath, Respond(403, body), Ok());

        Assert.True((await GetRepo(handler)).IsSuccess);
        Assert.Equal([Seconds(60)], _delays);
    }

    [Fact]
    public async Task Primary_limit_waits_until_reset_within_the_cap()
    {
        var handler = new FixtureHandler().Add("GET", RepoPath,
            Respond(403, null, ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", (Now.ToUnixTimeSeconds() + 90).ToString())),
            Ok());

        Assert.True((await GetRepo(handler)).IsSuccess);
        Assert.Equal([Seconds(90)], _delays);
    }

    [Fact]
    public async Task Primary_limit_beyond_the_cap_fails_without_waiting()
    {
        var handler = new FixtureHandler().Add("GET", RepoPath,
            Respond(403, null, ("x-ratelimit-remaining", "0"), ("x-ratelimit-reset", (Now.ToUnixTimeSeconds() + 121).ToString())));

        var result = await GetRepo(handler);

        Assert.Equal(SourceErrors.RateLimited, result.Error?.Code);
        Assert.Empty(_delays);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Retry_after_beyond_the_cap_fails_without_waiting()
    {
        var handler = new FixtureHandler().Add("GET", RepoPath, Respond(429, null, ("Retry-After", "121")));

        Assert.Equal(SourceErrors.RateLimited, (await GetRepo(handler)).Error?.Code);
        Assert.Empty(_delays);
    }

    // ---- transient failures ----

    [Fact]
    public async Task Server_errors_back_off_1_2_4_seconds_then_fail()
    {
        var handler = new FixtureHandler().Add("GET", RepoPath, Respond(502));

        var result = await GetRepo(handler);

        Assert.Equal(SourceErrors.ServerError, result.Error?.Code);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal([Seconds(1), Seconds(2), Seconds(4)], _delays);
    }

    [Fact]
    public async Task Server_error_recovers_on_retry()
    {
        var handler = new FixtureHandler().Add("GET", RepoPath, Respond(500), Respond(503), Ok());

        Assert.True((await GetRepo(handler)).IsSuccess);
        Assert.Equal([Seconds(1), Seconds(2)], _delays);
    }

    [Fact]
    public async Task Server_error_with_retry_after_beyond_the_cap_fails_without_waiting()
    {
        var handler = new FixtureHandler().Add("GET", RepoPath, Respond(503, null, ("Retry-After", "600")));

        Assert.Equal(SourceErrors.ServerError, (await GetRepo(handler)).Error?.Code);
        Assert.Empty(_delays);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("http")]
    public async Task Transport_failures_retry_then_report_unreachable(string kind)
    {
        var handler = new FixtureHandler().Add("GET", RepoPath, new JsonObject { ["throw"] = kind });

        var result = await GetRepo(handler);

        Assert.Equal(SourceErrors.Unreachable, result.Error?.Code);
        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal([Seconds(1), Seconds(2), Seconds(4)], _delays);
    }

    [Fact]
    public async Task Caller_cancellation_is_thrown()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new FixtureHandler().Add("GET", RepoPath, Ok()).CancelOnRequest(1, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(handler).GetAsync("repos/octo/hello", null, cancellation.Token));
        Assert.Empty(_delays);
    }

    // ---- error mapping ----

    [Theory]
    [InlineData(401, SourceErrors.Unauthorized)]
    [InlineData(403, SourceErrors.Forbidden)]
    [InlineData(404, SourceErrors.NotFound)]
    [InlineData(422, SourceErrors.RequestFailed)]
    public async Task Client_errors_map_to_source_codes(int status, string code)
    {
        var body = new JsonObject { ["message"] = "body-secret-" + Token };
        var handler = new FixtureHandler().Add("GET", RepoPath, Respond(status, body));

        var error = (await GetRepo(handler)).Error;

        Assert.Equal(code, error?.Code);
        Assert.Single(handler.Requests);
        Assert.Contains("GET /repos/octo/hello", error!.Message, StringComparison.Ordinal);
        Assert.Contains(status.ToString(), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("body-secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirect_is_reported_as_moved_with_a_redacted_location()
    {
        var handler = new FixtureHandler().Add("GET", RepoPath,
            Respond(301, null, ("Location", "https://api.github.com/repositories/42?access_token=abc123")));

        var error = (await GetRepo(handler)).Error;

        Assert.Equal(SourceErrors.Moved, error?.Code);
        Assert.Equal("https://api.github.com/repositories/42?access_token=***", error!.Details!["location"]);
        Assert.DoesNotContain("abc123", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Malformed_json_is_an_invalid_response()
    {
        var handler = new FixtureHandler().Add("GET", RepoPath, new JsonObject { ["status"] = 200, ["body_text"] = "{oops" });

        Assert.Equal(SourceErrors.InvalidResponse, (await GetRepo(handler)).Error?.Code);
    }

    [Fact]
    public async Task Array_endpoint_returning_an_object_is_an_invalid_response()
    {
        var handler = new FixtureHandler().Add("GET", "/repos/octo/hello/pulls?per_page=100", Ok());

        var result = await Client(handler).GetAllAsync("repos/octo/hello/pulls", null, null, TestContext.Current.CancellationToken);

        Assert.Equal(SourceErrors.InvalidResponse, result.Error?.Code);
    }

    [Fact]
    public async Task Unmatched_request_fails_the_test_instead_of_being_retried()
    {
        var handler = new FixtureHandler();

        await Assert.ThrowsAsync<FixtureMismatchException>(() => GetRepo(handler));
        Assert.Single(handler.Unmatched);
    }

    [Theory]
    [InlineData("/repos/octo/hello")]
    [InlineData("repos/octo/hello?page=2")]
    public async Task Absolute_paths_and_inline_queries_are_rejected(string path)
    {
        var handler = new FixtureHandler();

        await Assert.ThrowsAsync<ArgumentException>(
            () => Client(handler).GetAsync(path, null, TestContext.Current.CancellationToken));
    }
}

public sealed class LinkHeaderTests
{
    [Theory]
    [InlineData("<https://a/x?page=2>; rel=\"next\", <https://a/x?page=5>; rel=\"last\"", "https://a/x?page=2")]
    [InlineData("<https://a/x?page=1>; rel=\"prev\", <https://a/x?page=3>; rel=\"next\"", "https://a/x?page=3")]
    [InlineData("<https://a/x?page=3>;rel=next", "https://a/x?page=3")]
    [InlineData("<https://a/x?page=1>; rel=\"first\"", null)]
    [InlineData("", null)]
    public void FindNext_returns_the_next_target(string header, string? expected)
    {
        Assert.Equal(expected, LinkHeader.FindNext([header]));
    }
}
