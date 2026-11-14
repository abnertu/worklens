using System.Text.Json;
using System.Text.Json.Nodes;
using WorkLens.Tests.Shared;
using Xunit;
using static WorkLens.Tests.Shared.FixtureHandler;

namespace WorkLens.Connectors.Git.Tests;

public sealed class GitLabApiClientTests
{
    private const string Token = "glpat-test-token-value";
    private const string BaseUrl = "https://gitlab.example/api/v4";
    private const string ProjectUri = "/api/v4/projects/group%2Fsub%2Fapp";
    private const string Commits = "/api/v4/projects/7/repository/commits";
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    private readonly List<TimeSpan> _delays = [];

    private GitLabApiClient Client(FixtureHandler handler, string baseUrl = BaseUrl)
    {
        var http = GitLabHttp.CreateClient(handler, new Uri(baseUrl), Token, "0.1.0");
        return new GitLabApiClient(http, Retry(RetryPlatform.GitLab));
    }

    private RetryPolicy Retry(RetryPlatform platform) =>
        new(new FixedTimeProvider(Now), (delay, _) =>
        {
            _delays.Add(delay);
            return Task.CompletedTask;
        }, platform);

    private static JsonObject Ok(JsonNode? body = null, params (string, string)[] headers) =>
        Respond(200, body ?? new JsonObject { ["id"] = 7 }, headers);

    private static JsonArray Items(int first, int count) => [.. Enumerable.Range(first, count).Select(i => (JsonNode)i)];

    private Task<GitLabResult<JsonElement>> GetProject(FixtureHandler handler) =>
        Client(handler).GetAsync(GitLabApiClient.ProjectPath("group/sub/app"), null, TestContext.Current.CancellationToken);

    private Task<GitLabResult<IReadOnlyList<JsonElement>>> GetCommits(FixtureHandler handler) =>
        Client(handler).GetAllAsync("projects/7/repository/commits", null, TestContext.Current.CancellationToken);

    private static IEnumerable<int> Ints(GitLabResult<IReadOnlyList<JsonElement>> result) =>
        result.Value!.Select(e => e.GetInt32());

    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);

    // ---- request shape ----

    [Fact]
    public async Task Client_sends_the_GitLab_headers_and_never_a_bearer_token()
    {
        var handler = new FixtureHandler().Add("GET", ProjectUri, Ok());

        Assert.True((await GetProject(handler)).IsSuccess);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("worklens/0.1.0", request.Headers.UserAgent.ToString());
        Assert.Equal("application/json", Assert.Single(request.Headers.Accept).MediaType);
        Assert.Equal(Token, Assert.Single(request.Headers.GetValues("PRIVATE-TOKEN")));
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public void CreateClient_sets_the_timeout_and_normalized_base_address()
    {
        using var handler = new FixtureHandler();
        using var http = GitLabHttp.CreateClient(handler, new Uri("HTTPS://GitLab.Example:443/api/v4/"), Token, "0.1.0");

        Assert.Equal(TimeSpan.FromSeconds(100), http.Timeout);
        Assert.Equal("https://gitlab.example/api/v4/", http.BaseAddress!.AbsoluteUri);
    }

    [Fact]
    public async Task Project_path_stays_encoded_under_a_relative_url_root()
    {
        var handler = new FixtureHandler().Add("GET", "/gitlab/api/v4/projects/group%2Fsub%2Fapp", Ok());

        var result = await Client(handler, "https://host.example/gitlab/api/v4")
            .GetAsync(GitLabApiClient.ProjectPath("group/sub/app"), null, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(
            "https://host.example/gitlab/api/v4/projects/group%2Fsub%2Fapp",
            Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Paged_requests_add_per_page_and_format_times_in_utc()
    {
        var handler = new FixtureHandler()
            .Add("GET", Commits + "?per_page=100&since=2026-10-31T16:00:00Z", Ok(new JsonArray()));
        var since = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(8));

        var result = await Client(handler).GetAllAsync(
            "projects/7/repository/commits",
            [new("since", GitLabApiClient.FormatTime(since))],
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(handler.Unmatched);
    }

    [Fact]
    public void Client_rejects_a_GitHub_retry_policy()
    {
        using var handler = new FixtureHandler();
        using var http = GitLabHttp.CreateClient(handler, new Uri(BaseUrl), Token, "0.1.0");

        Assert.Throws<ArgumentException>(() => new GitLabApiClient(http, Retry(RetryPlatform.GitHub)));
    }

    // ---- pagination ----

    [Fact]
    public async Task Link_pagination_follows_next_and_stops_without_it()
    {
        var page2 = $"{BaseUrl}/projects/7/repository/commits?page=2&per_page=100";
        var handler = new FixtureHandler()
            .Add("GET", Commits + "?per_page=100", Ok(Items(1, 2), ("Link", $"<{page2}>; rel=\"next\"")))
            .Add("GET", Commits + "?per_page=100&page=2", Ok(Items(3, 1), ("Link", $"<{BaseUrl}/x>; rel=\"first\"")));

        var result = await GetCommits(handler);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal([1, 2, 3], Ints(result));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Link_without_next_ends_a_full_page_even_with_x_next_page()
    {
        var handler = new FixtureHandler().Add(
            "GET", Commits + "?per_page=100",
            Ok(Items(0, 100), ("Link", $"<{BaseUrl}/x>; rel=\"last\""), ("X-Next-Page", "2")));

        var result = await GetCommits(handler);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Single(handler.Requests);
    }

    /// <summary>A Link header with no parsable relation is malformed; it must not end the traversal as complete.</summary>
    [Theory]
    [InlineData("x")]
    [InlineData("<https://gitlab.example/api/v4/projects/7/repository/commits?page=2>")]
    [InlineData("rel=\"next\"")]
    public async Task Link_without_any_relation_is_an_invalid_response(string link)
    {
        var handler = new FixtureHandler().Add(
            "GET", Commits + "?per_page=100", Ok(Items(0, 100), ("Link", link), ("X-Next-Page", "2")));

        var result = await GetCommits(handler);

        Assert.Equal(SourceErrors.InvalidResponse, result.Error?.Code);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("https://evil.example/api/v4/projects/7/repository/commits?page=2")]
    [InlineData("http://gitlab.example/api/v4/projects/7/repository/commits?page=2")]
    [InlineData("https://gitlab.example:8443/api/v4/projects/7/repository/commits?page=2")]
    [InlineData("https://gitlab.example/other/projects/7/repository/commits?page=2")]
    [InlineData("https://gitlab.example/api/v41/projects?page=2")]
    [InlineData("/api/v4/projects/7/repository/commits?page=2")]
    public async Task Next_link_off_the_source_api_is_an_invalid_response(string next)
    {
        var handler = new FixtureHandler()
            .Add("GET", Commits + "?per_page=100", Ok(new JsonArray(), ("Link", $"<{next}>; rel=\"next\"")));

        var result = await GetCommits(handler);

        Assert.Equal(SourceErrors.InvalidResponse, result.Error?.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Next_link_back_to_an_earlier_page_is_an_invalid_response()
    {
        var self = $"{BaseUrl}/projects/7/repository/commits?per_page=100";
        // Without the guard the loop never ends; cancel on the third request so a regression fails fast.
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var handler = new FixtureHandler()
            .Add("GET", Commits + "?per_page=100", Ok(Items(0, 1), ("Link", $"<{self}>; rel=\"next\"")))
            .CancelOnRequest(3, cancellation);

        var result = await Client(handler).GetAllAsync("projects/7/repository/commits", null, cancellation.Token);

        Assert.Equal(SourceErrors.InvalidResponse, result.Error?.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task X_next_page_advances_until_it_is_empty()
    {
        var handler = new FixtureHandler()
            .Add("GET", Commits + "?per_page=100", Ok(Items(1, 1), ("X-Next-Page", "2")))
            .Add("GET", Commits + "?per_page=100&page=2", Ok(Items(2, 1), ("X-Next-Page", "3")))
            .Add("GET", Commits + "?per_page=100&page=3", Ok(Items(3, 100), ("X-Next-Page", "")));

        var result = await GetCommits(handler);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(102, result.Value!.Count);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("two")]
    public async Task Malformed_or_non_advancing_x_next_page_is_an_invalid_response(string value)
    {
        var handler = new FixtureHandler()
            .Add("GET", Commits + "?per_page=100", Ok(Items(1, 1), ("X-Next-Page", value)));

        var result = await GetCommits(handler);

        Assert.Equal(SourceErrors.InvalidResponse, result.Error?.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Headerless_full_pages_fall_back_to_offset_pages()
    {
        var handler = new FixtureHandler()
            .Add("GET", Commits + "?per_page=100", Ok(Items(0, 100)))
            .Add("GET", Commits + "?per_page=100&page=2", Ok(Items(100, 100)))
            .Add("GET", Commits + "?per_page=100&page=3", Ok(Items(200, 5)));

        var result = await GetCommits(handler);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(Enumerable.Range(0, 205), Ints(result));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Headerless_repeated_page_is_an_invalid_response()
    {
        var handler = new FixtureHandler()
            .Add("GET", Commits + "?per_page=100", Ok(Items(0, 100)))
            .Add("GET", Commits + "?per_page=100&page=2", Ok(Items(0, 100)));

        var result = await GetCommits(handler);

        Assert.Equal(SourceErrors.InvalidResponse, result.Error?.Code);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Headerless_traversal_fails_after_the_page_limit()
    {
        var handler = new FixtureHandler().Add("GET", Commits + "?per_page=100", Ok(Items(0, 100)));
        for (var page = 2; page <= GitLabApiClient.MaxHeaderlessPages + 1; page++)
        {
            handler.Add("GET", $"{Commits}?per_page=100&page={page}", Ok(Items((page - 1) * 100, 100)));
        }

        var pages = 0;
        var result = await Client(handler).GetPagesAsync(
            "projects/7/repository/commits", null, _ => ++pages > 0, TestContext.Current.CancellationToken);

        Assert.Equal(SourceErrors.InvalidResponse, result.Error?.Code);
        Assert.Equal(GitLabApiClient.MaxHeaderlessPages, handler.Requests.Count);
        Assert.Equal(GitLabApiClient.MaxHeaderlessPages, pages);
    }

    [Fact]
    public async Task Paging_stops_when_the_callback_returns_false()
    {
        var handler = new FixtureHandler().Add("GET", Commits + "?per_page=100", Ok(Items(0, 100), ("X-Next-Page", "2")));

        var result = await Client(handler).GetPagesAsync(
            "projects/7/repository/commits", null, _ => false, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.Value);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Array_endpoint_returning_an_object_is_an_invalid_response()
    {
        var handler = new FixtureHandler().Add("GET", Commits + "?per_page=100", Ok());

        Assert.Equal(SourceErrors.InvalidResponse, (await GetCommits(handler)).Error?.Code);
    }

    // ---- status mapping and retries ----

    [Theory]
    [InlineData(401, SourceErrors.Unauthorized)]
    [InlineData(403, SourceErrors.Forbidden)]
    [InlineData(404, SourceErrors.NotFound)]
    [InlineData(409, SourceErrors.RequestFailed)]
    [InlineData(422, SourceErrors.RequestFailed)]
    public async Task Client_errors_map_to_source_codes_without_waiting(int status, string code)
    {
        var handler = new FixtureHandler().Add("GET", ProjectUri, Respond(status, null, ("Retry-After", "1")));

        var result = await GetProject(handler);

        Assert.Equal(code, result.Error?.Code);
        Assert.Single(handler.Requests);
        Assert.Empty(_delays);
        Assert.DoesNotContain(Token, result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forbidden_with_a_secondary_limit_body_does_not_wait()
    {
        var body = new JsonObject { ["message"] = "You have exceeded a secondary rate limit." };
        var handler = new FixtureHandler().Add("GET", ProjectUri, Respond(403, body), Ok());

        Assert.Equal(SourceErrors.Forbidden, (await GetProject(handler)).Error?.Code);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Too_many_requests_prefers_retry_after_over_reset()
    {
        var handler = new FixtureHandler().Add("GET", ProjectUri,
            Respond(429, null, ("Retry-After", "7"), ("RateLimit-Reset", (Now.ToUnixTimeSeconds() + 90).ToString())),
            Ok());

        Assert.True((await GetProject(handler)).IsSuccess);
        Assert.Equal([Seconds(7)], _delays);
    }

    [Fact]
    public async Task Too_many_requests_waits_until_reset()
    {
        var handler = new FixtureHandler().Add("GET", ProjectUri,
            Respond(429, null, ("RateLimit-Reset", (Now.ToUnixTimeSeconds() + 90).ToString())), Ok());

        Assert.True((await GetProject(handler)).IsSuccess);
        Assert.Equal([Seconds(90)], _delays);
    }

    [Fact]
    public async Task Too_many_requests_backs_off_from_60_seconds_until_the_cap()
    {
        var handler = new FixtureHandler().Add("GET", ProjectUri, Respond(429));

        var result = await GetProject(handler);

        Assert.Equal(SourceErrors.RateLimited, result.Error?.Code);
        Assert.Equal([Seconds(60), Seconds(120)], _delays);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Too_many_requests_waits_at_most_three_times()
    {
        var handler = new FixtureHandler().Add("GET", ProjectUri, Respond(429, null, ("Retry-After", "1")));

        Assert.Equal(SourceErrors.RateLimited, (await GetProject(handler)).Error?.Code);
        Assert.Equal([Seconds(1), Seconds(1), Seconds(1)], _delays);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Server_errors_back_off_then_fail()
    {
        var handler = new FixtureHandler().Add("GET", ProjectUri, Respond(502));

        Assert.Equal(SourceErrors.ServerError, (await GetProject(handler)).Error?.Code);
        Assert.Equal([Seconds(1), Seconds(2), Seconds(4)], _delays);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("http")]
    public async Task Transport_failures_retry_then_report_unreachable(string kind)
    {
        var handler = new FixtureHandler().Add("GET", ProjectUri, new JsonObject { ["throw"] = kind });

        Assert.Equal(SourceErrors.Unreachable, (await GetProject(handler)).Error?.Code);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Caller_cancellation_is_thrown()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new FixtureHandler().Add("GET", ProjectUri, Ok()).CancelOnRequest(1, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(handler).GetAsync(GitLabApiClient.ProjectPath("group/sub/app"), null, cancellation.Token));
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Redirect_is_reported_as_moved()
    {
        var handler = new FixtureHandler().Add("GET", ProjectUri,
            Respond(301, null, ("Location", "https://gitlab.example/api/v4/projects/9")));

        var result = await GetProject(handler);

        Assert.Equal(SourceErrors.Moved, result.Error?.Code);
        Assert.Equal("https://gitlab.example/api/v4/projects/9", result.Error!.Details!["location"]);
    }

    [Fact]
    public async Task Malformed_json_is_an_invalid_response()
    {
        var handler = new FixtureHandler().Add("GET", ProjectUri, new JsonObject { ["status"] = 200, ["body_text"] = "<html>" });

        Assert.Equal(SourceErrors.InvalidResponse, (await GetProject(handler)).Error?.Code);
    }
}
