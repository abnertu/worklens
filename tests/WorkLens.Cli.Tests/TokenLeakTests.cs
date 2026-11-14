using System.Text.Json.Nodes;
using WorkLens.Tests.Shared;
using Xunit;
using static WorkLens.Tests.Shared.FixtureHandler;

namespace WorkLens.Cli.Tests;

/// <summary>
/// DC6: on every failure path, with <c>--verbose</c> and with <c>--json</c>, the token never reaches stdout,
/// stderr or a file in the workspace.
/// </summary>
public sealed class TokenLeakTests : IDisposable
{
    private const string ClassicToken = "ghp_" + "Zx9Yw8Vu7Ts6Rq5Po4Nm3Lk2Ji1Hg0FeDcBa98";
    private static readonly string FineGrainedToken = "github_pat_" + new string('A', 22) + "_" + new string('b', 59);

    /// <summary>Matches no token pattern, so only the registered secret value can mask it.</summary>
    private const string PlainToken = "0123456789abcdef0123456789abcdef01234567";

    private readonly CliRunner _cli = new();

    public void Dispose() => _cli.Dispose();

    public static TheoryData<string, string, string> Cases()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var token in new[] { ClassicToken, FineGrainedToken })
        {
            foreach (var flags in new[] { "--verbose", "--json --verbose" })
            {
                foreach (var scenario in new[] { "unauthorized_echo", "server_error", "http_exception", "invalid_config", "cancelled", "usage_error" })
                {
                    data.Add(scenario, token, flags);
                }
            }
        }

        foreach (var flags in new[] { "--verbose", "--json --verbose" })
        {
            data.Add("unexpected_exception", PlainToken, flags);
            data.Add("usage_error", PlainToken, flags);
            data.Add("custom_usage_error", PlainToken, flags);
            data.Add("custom_unknown_source", PlainToken, flags);
            data.Add("custom_invalid_config", PlainToken, flags);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Token_never_leaks(string scenario, string token, string flags)
    {
        Assert.Equal(82, FineGrainedToken.Length - "github_pat_".Length);
        _cli.Environment["WORKLENS_GITHUB_TOKEN"] = token;
        var config = CollectCommandTests.Config();
        using var cancel = new CancellationTokenSource();
        var expectedExit = ExitCodes.Source;
        var period = "2026-10";
        string[] selection = [];
        switch (scenario)
        {
            case "unauthorized_echo":
                _cli.Http = new FixtureHandler().Add(
                    "GET",
                    "/repos/octo/hello",
                    Respond(401, new JsonObject { ["message"] = $"Bad credentials: {token}" }, ("X-Echo", token)));
                break;
            case "server_error":
                _cli.Http = new FixtureHandler().Add("GET", "/repos/octo/hello", Respond(503, new JsonObject { ["token"] = token }));
                break;
            case "http_exception":
                _cli.Http = new FixtureHandler().Add(
                    "GET",
                    "/repos/octo/hello",
                    new JsonObject { ["throw"] = "http", ["message"] = $"connect failed for Bearer {token}" });
                break;
            case "invalid_config":
                config["identities"]![0]!["username"] = token;
                config["sources"]![0]!["repository"] = $"not a repository {token}";
                config["timezone"] = $"Zone/{token}";
                expectedExit = ExitCodes.Configuration;
                break;
            case "cancelled":
                _cli.Http = CollectCommandTests.Monthly().CancelOnRequest(2, cancel);
                expectedExit = ExitCodes.Cancelled;
                break;
            case "unexpected_exception":
                _cli.CreateHttpHandler = () => new ThrowingHandler($"unexpected failure with {token}");
                expectedExit = ExitCodes.Internal;
                break;
            case "usage_error":
                period = token;
                expectedExit = ExitCodes.Usage;
                break;
            case "custom_usage_error":
            case "custom_unknown_source":
            case "custom_invalid_config":
                _cli.Environment.Clear();
                _cli.Environment["CORP_TOKEN"] = token;
                config["sources"]![0]!["token_env"] = "CORP_TOKEN";
                expectedExit = ExitCodes.Configuration;
                if (scenario == "custom_usage_error")
                {
                    period = token;
                    expectedExit = ExitCodes.Usage;
                }
                else if (scenario == "custom_unknown_source")
                {
                    selection = ["--source", token];
                }
                else
                {
                    config["timezone"] = $"Zone/{token}";
                }

                break;
        }

        CollectCommandTests.WriteConfig(Path.Combine(_cli.Workspace, "worklens.json"), config);

        var run = await _cli.RunAsync(cancel.Token, ["collect", "--period", period, .. selection, .. flags.Split(' ')]);

        Assert.Equal(expectedExit, run.ExitCode);
        Assert.DoesNotContain(token, run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(token, run.Stderr, StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(_cli.Workspace, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) != "worklens.json")
            {
                Assert.DoesNotContain(token, await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);
            }
        }
    }

    public static TheoryData<string, bool, string> GitLabCases()
    {
        var data = new TheoryData<string, bool, string>();
        foreach (var defaultVariable in new[] { true, false })
        {
            foreach (var flags in new[] { "--verbose", "--json --verbose" })
            {
                foreach (var scenario in new[] { "unauthorized_echo", "forbidden_echo", "server_error", "http_exception", "rate_limit_header", "invalid_config", "cancelled", "usage_error" })
                {
                    data.Add(scenario, defaultVariable, flags);
                }
            }
        }

        return data;
    }

    /// <summary>
    /// DC7: a GitLab token that matches no pattern stays masked, both from <c>WORKLENS_GITLAB_TOKEN</c> on
    /// gitlab.com and from a custom <c>token_env</c> on a self-managed instance.
    /// </summary>
    [Theory]
    [MemberData(nameof(GitLabCases))]
    public async Task GitLab_token_never_leaks(string scenario, bool defaultVariable, string flags)
    {
        const string project = "/api/v4/projects/group%2Fsub%2Fapp";
        var token = PlainToken;
        JsonObject source;
        if (defaultVariable)
        {
            _cli.Environment["WORKLENS_GITLAB_TOKEN"] = token;
            source = GitLabCollectTests.Source(baseUrl: "https://gitlab.com/api/v4", tokenEnv: null);
        }
        else
        {
            _cli.Environment["CORP_GITLAB"] = token;
            source = GitLabCollectTests.Source();
        }

        var config = GitLabCollectTests.Config(source);
        using var cancel = new CancellationTokenSource();
        var expectedExit = ExitCodes.Source;
        var period = "2026-10";
        switch (scenario)
        {
            case "unauthorized_echo":
                _cli.Http = new FixtureHandler().Add("GET", project, Respond(401, new JsonObject { ["message"] = $"401 {token}" }, ("X-Echo", token)));
                break;
            case "forbidden_echo":
                _cli.Http = new FixtureHandler().Add("GET", project, Respond(403, new JsonObject { ["message"] = $"403 {token}" }));
                break;
            case "server_error":
                _cli.Http = new FixtureHandler().Add("GET", project, Respond(500, new JsonObject { ["private_token"] = token }));
                break;
            case "http_exception":
                _cli.Http = new FixtureHandler().Add("GET", project, new JsonObject { ["throw"] = "http", ["message"] = $"connect failed for PRIVATE-TOKEN {token}" });
                break;
            case "rate_limit_header":
                _cli.Http = new FixtureHandler().Add("GET", project, Respond(404, null, ("RateLimit-Remaining", token)));
                break;
            case "invalid_config":
                config["identities"]![0]!["username"] = token;
                config["timezone"] = $"Zone/{token}";
                expectedExit = ExitCodes.Configuration;
                break;
            case "cancelled":
                _cli.Http = GitLabCollectTests.Monthly().CancelOnRequest(2, cancel);
                expectedExit = ExitCodes.Cancelled;
                break;
            case "usage_error":
                period = token;
                expectedExit = ExitCodes.Usage;
                break;
        }

        CollectCommandTests.WriteConfig(Path.Combine(_cli.Workspace, "worklens.json"), config);

        var run = await _cli.RunAsync(cancel.Token, ["collect", "--period", period, .. flags.Split(' ')]);

        Assert.Equal(expectedExit, run.ExitCode);
        Assert.DoesNotContain(token, run.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(token, run.Stderr, StringComparison.Ordinal);
        foreach (var file in Directory.EnumerateFiles(_cli.Workspace, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) != "worklens.json")
            {
                Assert.DoesNotContain(token, await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);
            }
        }
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(message);
    }
}
