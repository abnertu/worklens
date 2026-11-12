using System.Text.Json.Nodes;
using WorkLens.Connectors.Git.GitHub;
using WorkLens.Core.Common;
using WorkLens.Core.Contracts;
using WorkLens.Core.Evidence;
using WorkLens.Core.Json;
using WorkLens.Core.Periods;
using WorkLens.Core.Validation;
using WorkLens.Tests.Shared;
using Xunit;
using static WorkLens.Tests.Shared.FixtureHandler;

namespace WorkLens.Connectors.Git.Tests;

/// <summary>
/// Runs the connector against tests/Fixtures/github/monthly (Asia/Taipei 2026-10 =
/// [2026-09-30T16:00Z, 2026-10-31T16:00Z)); the fixture files say which scenario each commit / PR covers.
/// </summary>
public sealed class GitHubConnectorTests
{
    private const string Repo = "/repos/octo/hello";
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 1, 2, 3, TimeSpan.Zero);
    private static readonly Period October = PeriodCalculator.Calculate("2026-10", "Asia/Taipei");
    private static readonly SourceSpec Source = new(SourcePlatform.Github, "https://API.github.com/", "Octo/Hello");

    private static readonly Identity[] Identities =
    [
        new(Platform: SourcePlatform.Github, Username: "Alice"),
        new(Email: "alice@example.com"),
    ];

    private static string Sha(string key) => string.Concat(Enumerable.Repeat(key, 20));

    private static string CommitId(string key) =>
        EvidenceIds.ForItem(SourcePlatform.Github, "https://api.github.com", "octo/hello", EvidenceKind.Commit, Sha(key));

    private static string PullId(int number) =>
        EvidenceIds.ForItem(SourcePlatform.Github, "https://api.github.com", "octo/hello", EvidenceKind.ChangeRequest, number.ToString());

    private static async Task<SourceCollectResult> Collect(FixtureHandler handler)
    {
        var http = GitHubHttp.CreateClient(handler, new Uri(Source.BaseUrl), "test-token", "0.1.0");
        var time = new FixedTimeProvider(Now);
        var api = new GitHubApiClient(http, new RetryPolicy(time, (_, _) => Task.CompletedTask));
        return await new GitHubConnector(api, time)
            .CollectAsync(new SourceQuery(October, Identities, Source), TestContext.Current.CancellationToken);
    }

    private static FixtureHandler Monthly() =>
        FromDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "github", "monthly"));

    private static EvidenceItem Item(SourceCollectResult result, string id) => Assert.Single(result.Items, i => i.Id == id);

    [Fact]
    public async Task Monthly_fixture_collects_the_subjects_commits_and_pull_requests()
    {
        var handler = Monthly();

        var result = await Collect(handler);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Empty(handler.Unmatched);
        Assert.Equal(
            new[] { "c1", "c3", "c4", "c5", "c7", "c8", "c9" }.Select(CommitId).Concat([PullId(10), PullId(9)]).Order(),
            result.Items.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task Period_boundary_uses_the_author_date()
    {
        var result = await Collect(Monthly());

        var boundary = Item(result, CommitId("c1"));
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T16:00:00Z"), boundary.Timestamps.OccurredAt);
        Assert.DoesNotContain(result.Items, i => i.Id == CommitId("c2")); // 15:59:59Z, one second before the period
    }

    [Fact]
    public async Task Commit_list_is_queried_by_default_branch_and_widened_window()
    {
        var handler = Monthly();

        await Collect(handler);

        var list = Assert.Single(handler.Requests, r => string.Equals(r.RequestUri!.AbsolutePath, Repo + "/commits", StringComparison.OrdinalIgnoreCase));
        var query = Uri.UnescapeDataString(list.RequestUri!.Query);
        Assert.Contains("sha=main", query, StringComparison.Ordinal);
        Assert.Contains("since=2026-09-30T16:00:00Z", query, StringComparison.Ordinal);
        Assert.Contains("until=2026-11-30T16:00:00Z", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_fields_stats_paths_and_flags()
    {
        var result = await Collect(Monthly());

        var c1 = Item(result, CommitId("c1"));
        Assert.Equal(EvidenceKind.Commit, c1.Kind);
        Assert.Equal("Add token refresh", c1.Title);
        Assert.Equal("Refresh tokens before expiry.", c1.Body);
        Assert.Equal(new ChangeStats(3, 40, 2), c1.Stats); // files from both detail pages
        Assert.Equal(3, c1.Paths!.Count);
        Assert.Equal(new EvidenceSource(SourcePlatform.Github, "https://api.github.com", "Octo/Hello", Sha("c1"),
            $"https://github.com/octo/hello/commit/{Sha("c1")}"), c1.Source);
        Assert.Equal(AuthorRole.Author, c1.Author.Role);
        Assert.Equal(("Alice", "alice", "alice@example.com"), (c1.Author.Name, c1.Author.Username, c1.Author.Email));
        Assert.Equal(Now, c1.FetchedAt);
        Assert.Empty(c1.Flags!);

        Assert.Equal([EvidenceFlag.MergeCommit], Item(result, CommitId("c4")).Flags);
        Assert.Equal([EvidenceFlag.Revert], Item(result, CommitId("c5")).Flags);
    }

    [Fact]
    public async Task Bot_commit_with_the_subject_as_co_author()
    {
        var result = await Collect(Monthly());

        var c3 = Item(result, CommitId("c3"));
        Assert.Equal(AuthorRole.CoAuthor, c3.Author.Role);
        Assert.Equal(("Alice", "ALICE@example.com"), (c3.Author.Name, c3.Author.Email));
        Assert.Equal([EvidenceFlag.Bot], c3.Flags);
    }

    [Fact]
    public async Task Committer_role_and_invalid_email_omission()
    {
        var result = await Collect(Monthly());

        Assert.Equal(AuthorRole.Committer, Item(result, CommitId("c8")).Author.Role);
        var c7 = Item(result, CommitId("c7"));
        Assert.Equal(AuthorRole.Author, c7.Author.Role);
        Assert.Equal("alice", c7.Author.Username);
        Assert.Null(c7.Author.Email);
    }

    [Fact]
    public async Task Pull_request_contains_only_the_subjects_commits_including_ones_outside_the_period()
    {
        var result = await Collect(Monthly());

        var pr = Item(result, PullId(10));
        Assert.Equal(EvidenceKind.ChangeRequest, pr.Kind);
        Assert.Equal(EvidenceState.Merged, pr.State);
        Assert.Equal(DateTimeOffset.Parse("2026-10-03T00:00:00Z"), pr.Timestamps.OccurredAt);
        Assert.Equal(new ChangeStats(3, 50, 2), pr.Stats);
        Assert.Equal("10", pr.Source.ExternalId);
        Assert.Equal(
            new[] { CommitId("c1"), CommitId("c9") }.Order(),
            pr.Relations!.Select(r => r.TargetId).Order());
        Assert.All(pr.Relations!, r => Assert.Equal(RelationType.Contains, r.Type));
        Assert.DoesNotContain(result.Items, i => i.Id == CommitId("ca"));

        var outside = Item(result, CommitId("c9"));
        Assert.True(outside.Timestamps.OccurredAt < October.Start);

        var open = Item(result, PullId(9));
        Assert.Equal(EvidenceState.Open, open.State);
        Assert.Equal([new EvidenceRelation(RelationType.Contains, CommitId("c8"))], open.Relations);
    }

    [Fact]
    public async Task Pull_request_list_stops_paging_after_an_older_pull_request()
    {
        var handler = Monthly();

        var result = await Collect(handler);

        Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal)
            && r.RequestUri.AbsolutePath.EndsWith("/pulls", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Items, i => i.Id == PullId(12) || i.Id == PullId(11) || i.Id == PullId(8));
    }

    [Fact]
    public async Task Commit_on_the_branch_and_in_a_pull_request_is_read_once()
    {
        var handler = Monthly();

        var result = await Collect(handler);

        Assert.Single(handler.Requests, r => string.Equals(r.RequestUri!.AbsolutePath, $"{Repo}/commits/{Sha("c1")}", StringComparison.OrdinalIgnoreCase));
        Assert.Single(result.Items, i => i.Id == CommitId("c1"));
    }

    [Fact]
    public async Task Pull_request_with_fewer_listed_commits_than_reported_warns()
    {
        var result = await Collect(Monthly());

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SourceErrors.Truncated, warning.Code);
        Assert.Equal("10", warning.Details!["external_id"]);
    }

    [Fact]
    public async Task Assembled_output_is_valid_evidence_ir()
    {
        var result = await Collect(Monthly());
        var query = new EvidenceQuery(October, Identities, [Source]);

        var document = EvidenceAssembler.Assemble(query, result.Items, Now, new GeneratorInfo("worklens", "0.1.0"));
        var validation = new IrSchemaValidator().Validate(EvidenceDocument.SchemaId, WorkLensJson.SerializeCanonical(document));

        Assert.True(validation.IsValid, string.Join("; ", validation.Issues.Select(i => $"{i.InstancePath}: {i.Message}")));
    }

    [Fact]
    public async Task Repeated_runs_produce_the_same_items()
    {
        var first = await Collect(Monthly());
        var second = await Collect(Monthly());

        Assert.Equal(WorkLensJson.SerializeCanonical(first.Items), WorkLensJson.SerializeCanonical(second.Items));
    }

    // ---- inline scenarios ----

    private static FixtureHandler Minimal(JsonObject commitsResponse) => new FixtureHandler()
        .Add("GET", Repo, Respond(200, new JsonObject { ["default_branch"] = "main" }))
        .Add("GET", $"{Repo}/commits?sha=main&since=2026-09-30T16:00:00Z&until=2026-11-30T16:00:00Z&per_page=100", commitsResponse)
        .Add("GET", $"{Repo}/pulls?state=all&sort=updated&direction=desc&per_page=100", Respond(200, new JsonArray()));

    [Fact]
    public async Task Empty_repository_yields_no_items()
    {
        var result = await Collect(Minimal(Respond(409, new JsonObject { ["message"] = "Git Repository is empty." })));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task Source_error_is_returned_with_no_items()
    {
        var handler = new FixtureHandler().Add("GET", Repo, Respond(404));

        var result = await Collect(handler);

        Assert.Empty(result.Items);
        Assert.Equal(SourceErrors.NotFound, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Unexpected_response_shape_is_an_invalid_response()
    {
        var result = await Collect(Minimal(Respond(200, new JsonObject { ["not"] = "a list" })));

        Assert.Equal(SourceErrors.InvalidResponse, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Commit_with_the_maximum_number_of_files_warns()
    {
        var sha = Sha("dd");
        var commit = new JsonObject
        {
            ["sha"] = sha,
            ["html_url"] = $"https://github.com/octo/hello/commit/{sha}",
            ["commit"] = new JsonObject
            {
                ["author"] = new JsonObject { ["name"] = "Alice", ["email"] = "alice@example.com", ["date"] = "2026-10-05T00:00:00Z" },
                ["message"] = "Huge change",
            },
            ["parents"] = new JsonArray(new JsonObject { ["sha"] = Sha("00") }),
        };
        var handler = Minimal(Respond(200, new JsonArray(commit.DeepClone())));
        const int Pages = 10;
        for (var page = 1; page <= Pages; page++)
        {
            var detail = commit.DeepClone().AsObject();
            detail["stats"] = new JsonObject { ["additions"] = 3000, ["deletions"] = 0 };
            detail["files"] = new JsonArray([.. Enumerable.Range(0, 300).Select(i => (JsonNode)new JsonObject { ["filename"] = $"f/{page}/{i}" })]);
            var pageQuery = page == 1 ? "per_page=100" : $"per_page=100&page={page}";
            var headers = page < Pages
                ? new[] { ("Link", $"<https://api.github.com{Repo}/commits/{sha}?per_page=100&page={page + 1}>; rel=\"next\"") }
                : [];
            handler.Add("GET", $"{Repo}/commits/{sha}?{pageQuery}", Respond(200, detail, headers));
        }

        var result = await Collect(handler);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Equal(GitHubConnector.MaxCommitFiles, Assert.Single(result.Items).Stats!.FilesChanged);
        Assert.Equal(sha, Assert.Single(result.Warnings, w => w.Code == SourceErrors.Truncated).Details!["external_id"]);
    }
}
