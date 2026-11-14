using System.Text.Json.Nodes;
using WorkLens.Connectors.Git.GitLab;
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
/// Runs the connector against tests/Fixtures/gitlab/monthly (Asia/Taipei 2026-10 =
/// [2026-09-30T16:00Z, 2026-10-31T16:00Z)). The project <c>group/sub/app</c> resolves to ID 7. The generator
/// comments of each scenario are in the fixture data: c1-c9 and ca/cb are commits, 7-13 merge request iids.
/// </summary>
public sealed class GitLabConnectorTests
{
    private const string Api = "/api/v4";
    private const string Project = Api + "/projects/7";
    private const string Web = "https://gitlab.example.com/group/sub/app";
    private const string BaseUrl = "https://gitlab.example.com/api/v4";
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 1, 2, 3, TimeSpan.Zero);
    private static readonly Period October = PeriodCalculator.Calculate("2026-10", "Asia/Taipei");
    private static readonly SourceSpec Source = new(SourcePlatform.Gitlab, "https://GitLab.example.com/api/v4/", "group/sub/app");

    private static readonly Identity[] Identities =
    [
        new(Platform: SourcePlatform.Gitlab, Username: "alice"),
        new(Platform: SourcePlatform.Github, Username: "bob"),
        new(Email: "alice@example.com"),
        new(Email: "alice@users.example.org"),
    ];

    private static string Sha(string key) => string.Concat(Enumerable.Repeat(key, 20));

    private static string CommitId(string key) =>
        EvidenceIds.ForItem(SourcePlatform.Gitlab, BaseUrl, "group/sub/app", EvidenceKind.Commit, Sha(key));

    private static string MergeRequestId(int iid) =>
        EvidenceIds.ForItem(SourcePlatform.Gitlab, BaseUrl, "group/sub/app", EvidenceKind.ChangeRequest, iid.ToString());

    private static async Task<SourceCollectResult> Collect(FixtureHandler handler)
    {
        var http = GitLabHttp.CreateClient(handler, new Uri(Source.BaseUrl), "test-token", "0.1.0");
        var time = new FixedTimeProvider(Now);
        var api = new GitLabApiClient(http, new RetryPolicy(time, (_, _) => Task.CompletedTask, RetryPlatform.GitLab));
        return await new GitLabConnector(api, time)
            .CollectAsync(new SourceQuery(October, Identities, Source), TestContext.Current.CancellationToken);
    }

    private static FixtureHandler Monthly() =>
        FromDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gitlab", "monthly"));

    private static EvidenceItem Item(SourceCollectResult result, string id) => Assert.Single(result.Items, i => i.Id == id);

    private static bool IsPath(HttpRequestMessage request, string path) =>
        string.Equals(request.RequestUri!.AbsolutePath, path, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Monthly_fixture_collects_the_subjects_commits_and_merge_requests()
    {
        var handler = Monthly();

        var result = await Collect(handler);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Empty(handler.Unmatched);
        Assert.Empty(result.Warnings);
        Assert.Equal(
            new[] { "c1", "c3", "c4", "c5", "c7", "c8", "c9" }.Select(CommitId)
                .Concat(new[] { 9, 10, 11, 12 }.Select(MergeRequestId)).Order(),
            result.Items.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task Project_path_is_resolved_once_then_the_numeric_id_is_used()
    {
        var handler = Monthly();

        await Collect(handler);

        var first = handler.Requests[0].RequestUri!;
        Assert.Equal(Api + "/projects/group%2Fsub%2Fapp", first.AbsolutePath);
        Assert.All(handler.Requests.Skip(1), r => Assert.StartsWith(Project + "/", r.RequestUri!.AbsolutePath, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Period_boundary_uses_the_author_date_with_its_offset()
    {
        var result = await Collect(Monthly());

        Assert.Equal(DateTimeOffset.Parse("2026-09-30T16:00:00Z"), Item(result, CommitId("c1")).Timestamps.OccurredAt);
        Assert.DoesNotContain(result.Items, i => i.Id == CommitId("c2")); // 15:59:59Z, one second before the period
        Assert.DoesNotContain(result.Items, i => i.Id == CommitId("cb")); // authored after the period
    }

    [Fact]
    public async Task List_requests_use_the_default_branch_widened_window_and_updated_after()
    {
        var handler = Monthly();

        await Collect(handler);

        var commits = Uri.UnescapeDataString(handler.Requests.First(r => IsPath(r, Project + "/repository/commits")).RequestUri!.Query);
        Assert.Contains("ref_name=main", commits, StringComparison.Ordinal);
        Assert.Contains("since=2026-09-30T16:00:00Z", commits, StringComparison.Ordinal);
        Assert.Contains("until=2026-11-30T16:00:00Z", commits, StringComparison.Ordinal);

        var mergeRequests = Uri.UnescapeDataString(handler.Requests.First(r => IsPath(r, Project + "/merge_requests")).RequestUri!.Query);
        Assert.Contains("updated_after=2026-09-30T16:00:00Z", mergeRequests, StringComparison.Ordinal);
        Assert.Contains("state=all", mergeRequests, StringComparison.Ordinal);
        Assert.Contains("scope=all", mergeRequests, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commit_fields_stats_paths_and_flags()
    {
        var result = await Collect(Monthly());

        var c1 = Item(result, CommitId("c1"));
        Assert.Equal(EvidenceKind.Commit, c1.Kind);
        Assert.Equal("Add token refresh", c1.Title);
        Assert.Equal("Refresh tokens before expiry.", c1.Body);
        Assert.Equal(["src/auth.cs", "docs/old.md", "src/token.cs"], c1.Paths); // both diff pages, renamed to new_path, distinct
        Assert.Equal(new ChangeStats(3, 40, 2), c1.Stats);
        Assert.Equal(new EvidenceSource(SourcePlatform.Gitlab, BaseUrl, "group/sub/app", Sha("c1"), $"{Web}/-/commit/{Sha("c1")}"), c1.Source);
        Assert.Equal((AuthorRole.Author, "Alice", "alice@example.com"), (c1.Author.Role, c1.Author.Name, c1.Author.Email));
        Assert.Null(c1.Author.Username);
        Assert.Equal(Now, c1.FetchedAt);
        Assert.Empty(c1.Flags!);

        Assert.Equal([EvidenceFlag.MergeCommit], Item(result, CommitId("c4")).Flags);
        Assert.Equal(new ChangeStats(0, 0, 0), Item(result, CommitId("c4")).Stats);
        Assert.Equal([EvidenceFlag.Revert], Item(result, CommitId("c5")).Flags);

        var c8 = Item(result, CommitId("c8"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-03T00:00:00Z"), c8.Timestamps.CommittedAt);
    }

    [Fact]
    public async Task Co_author_and_committer_roles_without_inferred_bots()
    {
        var result = await Collect(Monthly());

        var c3 = Item(result, CommitId("c3"));
        Assert.Equal((AuthorRole.CoAuthor, "ALICE@example.com"), (c3.Author.Role, c3.Author.Email));
        Assert.Empty(c3.Flags!); // "ci-bot" is a name, not API bot data

        var c7 = Item(result, CommitId("c7"));
        Assert.Equal((AuthorRole.Committer, "alice@users.example.org"), (c7.Author.Role, c7.Author.Email));
    }

    [Fact]
    public async Task Merge_request_uses_the_iid_and_contains_only_the_subjects_commits()
    {
        var result = await Collect(Monthly());

        var mr = Item(result, MergeRequestId(10));
        Assert.Equal(EvidenceKind.ChangeRequest, mr.Kind);
        Assert.Equal(new EvidenceSource(SourcePlatform.Gitlab, BaseUrl, "group/sub/app", "10", $"{Web}/-/merge_requests/10"), mr.Source);
        Assert.Equal(EvidenceState.Merged, mr.State);
        Assert.Equal(DateTimeOffset.Parse("2026-10-03T00:00:00Z"), mr.Timestamps.OccurredAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-20T00:00:00Z"), mr.Timestamps.CreatedAt);
        Assert.Equal(("Alice", "Alice"), (mr.Author.Name, mr.Author.Username));
        Assert.Equal("Refresh tokens before expiry.", mr.Body);
        Assert.Null(mr.Stats);
        Assert.Null(mr.Paths);
        Assert.Empty(mr.Flags!); // the author has no bot field
        Assert.Equal(
            new[] { CommitId("c1"), CommitId("c9") }.Order(),
            mr.Relations!.Select(r => r.TargetId).Order());
        Assert.All(mr.Relations!, r => Assert.Equal(RelationType.Contains, r.Type));
        Assert.DoesNotContain(result.Items, i => i.Id == CommitId("ca"));
        Assert.True(Item(result, CommitId("c9")).Timestamps.OccurredAt < October.Start);
    }

    [Fact]
    public async Task Merge_request_states_flags_and_period_selection()
    {
        var result = await Collect(Monthly());

        var opened = Item(result, MergeRequestId(9));
        Assert.Equal(EvidenceState.Open, opened.State);
        Assert.Equal(DateTimeOffset.Parse("2026-10-10T00:00:00Z"), opened.Timestamps.OccurredAt);
        Assert.Equal([new EvidenceRelation(RelationType.Contains, CommitId("c7"))], opened.Relations);

        var locked = Item(result, MergeRequestId(11));
        Assert.Equal(EvidenceState.Open, locked.State);
        Assert.Equal([EvidenceFlag.Bot, EvidenceFlag.Revert], locked.Flags);
        Assert.Empty(locked.Relations!);

        var closed = Item(result, MergeRequestId(12));
        Assert.Equal(EvidenceState.Closed, closed.State);
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T00:00:00Z"), closed.Timestamps.ClosedAt);
        Assert.Equal([new EvidenceRelation(RelationType.Contains, CommitId("c3"))], closed.Relations);

        // 8 merged after the period, 7 created before it and not merged, 13 by another person (whose GitHub-only
        // username identity does not apply to GitLab).
        Assert.DoesNotContain(result.Items, i => i.Id == MergeRequestId(8) || i.Id == MergeRequestId(7) || i.Id == MergeRequestId(13));
    }

    [Fact]
    public async Task Commit_on_the_branch_and_in_a_merge_request_is_read_once()
    {
        var handler = Monthly();

        await Collect(handler);

        foreach (var key in new[] { "c1", "c3" })
        {
            var detail = $"{Project}/repository/commits/{Sha(key)}";
            Assert.Single(handler.Requests, r => IsPath(r, detail));
            Assert.Single(handler.Requests, r => IsPath(r, detail + "/diff") && !r.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal));
        }
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

    private const string CommitList = Project + "/repository/commits?ref_name=main&since=2026-09-30T16:00:00Z&until=2026-11-30T16:00:00Z&per_page=100";
    private const string MergeRequestList = Project + "/merge_requests?state=all&scope=all&order_by=updated_at&sort=desc&updated_after=2026-09-30T16:00:00Z&per_page=100";

    private static FixtureHandler Minimal(JsonObject project, JsonObject commitsResponse, JsonObject? mergeRequestsResponse = null) =>
        new FixtureHandler()
            .Add("GET", Api + "/projects/group%2Fsub%2Fapp", Respond(200, project))
            .Add("GET", CommitList, commitsResponse)
            .Add("GET", MergeRequestList, mergeRequestsResponse ?? Respond(200, new JsonArray()));

    private static JsonObject ProjectBody(bool empty = false) => new()
    {
        ["id"] = 7,
        ["default_branch"] = empty ? null : "main",
        ["empty_repo"] = empty,
    };

    [Fact]
    public async Task Empty_repository_reads_no_commits()
    {
        var handler = Minimal(ProjectBody(empty: true), Respond(409));

        var result = await Collect(handler);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Empty(result.Items);
        Assert.DoesNotContain(handler.Requests, r => IsPath(r, Project + "/repository/commits"));
    }

    /// <summary>GitLab answers the merge request list with 403 when the project disables merge requests.</summary>
    [Theory]
    [InlineData("merge_requests_access_level", "\"disabled\"")]
    [InlineData("merge_requests_enabled", "false")]
    public async Task Disabled_merge_requests_are_not_listed(string field, string json)
    {
        var project = ProjectBody();
        project[field] = JsonNode.Parse(json);

        var handler = Minimal(project, Respond(200, new JsonArray()), Respond(403));
        var result = await Collect(handler);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.DoesNotContain(handler.Requests, r => IsPath(r, Project + "/merge_requests"));
    }

    [Fact]
    public async Task Conflict_on_a_non_empty_repository_is_an_error()
    {
        var result = await Collect(Minimal(ProjectBody(), Respond(409)));

        Assert.Empty(result.Items);
        Assert.Equal(SourceErrors.RequestFailed, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Source_error_is_returned_with_no_items()
    {
        var handler = new FixtureHandler().Add("GET", Api + "/projects/group%2Fsub%2Fapp", Respond(404));

        var result = await Collect(handler);

        Assert.Empty(result.Items);
        Assert.Equal(SourceErrors.NotFound, Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Missing_default_branch_is_an_invalid_response()
    {
        var project = ProjectBody();
        project["default_branch"] = null;

        var result = await Collect(Minimal(project, Respond(200, new JsonArray())));

        Assert.Equal(SourceErrors.InvalidResponse, Assert.Single(result.Errors).Code);
    }

    /// <summary>A null array element is a missing required response, not an internal error.</summary>
    [Theory]
    [InlineData(CommitList)]
    [InlineData(MergeRequestList)]
    [InlineData(Project + "/merge_requests/9/commits?per_page=100")]
    [InlineData(Project + "/repository/commits/c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3/diff?per_page=100")]
    public async Task Null_list_element_is_an_invalid_response(string pathAndQuery)
    {
        var handler = new FixtureHandler()
            .Add("GET", pathAndQuery, Respond(200, new JsonArray((JsonNode?)null)))
            .AddDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "gitlab", "monthly"));

        var result = await Collect(handler);

        Assert.Empty(result.Items);
        Assert.Equal(SourceErrors.InvalidResponse, Assert.Single(result.Errors).Code);
        Assert.Contains(handler.Requests, r => IsPath(r, pathAndQuery.Split('?')[0]));
    }

    [Fact]
    public async Task Unknown_merge_request_state_is_an_invalid_response()
    {
        var mergeRequest = new JsonObject
        {
            ["iid"] = 5,
            ["state"] = "archived",
            ["title"] = "Change",
            ["author"] = new JsonObject { ["username"] = "alice" },
            ["created_at"] = "2026-10-05T00:00:00Z",
            ["web_url"] = $"{Web}/-/merge_requests/5",
        };
        var handler = Minimal(ProjectBody(), Respond(200, new JsonArray()), Respond(200, new JsonArray(mergeRequest.DeepClone())))
            .Add("GET", Project + "/merge_requests/5", Respond(200, mergeRequest))
            .Add("GET", Project + "/merge_requests/5/commits?per_page=100", Respond(200, new JsonArray()));

        var result = await Collect(handler);

        Assert.Empty(result.Items);
        Assert.Equal(SourceErrors.InvalidResponse, Assert.Single(result.Errors).Code);
    }

    [Theory]
    [InlineData(30, false)]
    [InlineData(31, true)]
    public async Task Commit_diff_is_capped_at_the_maximum_number_of_files(int pages, bool truncated)
    {
        var sha = Sha("dd");
        var commit = new JsonObject
        {
            ["id"] = sha,
            ["message"] = "Huge change",
            ["author_name"] = "Alice",
            ["author_email"] = "alice@example.com",
            ["authored_date"] = "2026-10-05T00:00:00Z",
            ["parent_ids"] = new JsonArray(Sha("00")),
            ["web_url"] = $"{Web}/-/commit/{sha}",
        };
        var detail = commit.DeepClone().AsObject();
        detail["stats"] = new JsonObject { ["additions"] = 3100, ["deletions"] = 0 };
        var handler = Minimal(ProjectBody(), Respond(200, new JsonArray(commit.DeepClone())))
            .Add("GET", $"{Project}/repository/commits/{sha}", Respond(200, detail));
        for (var page = 1; page <= pages + 1; page++)
        {
            var files = new JsonArray([.. Enumerable.Range(0, 100).Select(i => (JsonNode)new JsonObject { ["old_path"] = $"f/{page}/{i}", ["new_path"] = $"f/{page}/{i}" })]);
            var pageQuery = page == 1 ? "per_page=100" : $"per_page=100&page={page}";
            var next = page < pages ? (page + 1).ToString() : string.Empty;
            handler.Add("GET", $"{Project}/repository/commits/{sha}/diff?{pageQuery}", Respond(200, files, ("X-Next-Page", next)));
        }

        var result = await Collect(handler);

        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(e => e.Message)));
        var item = Assert.Single(result.Items);
        Assert.Equal(GitLabConnector.MaxCommitFiles, item.Paths!.Count);
        Assert.Equal(GitLabConnector.MaxCommitFiles, item.Stats!.FilesChanged);
        Assert.Equal(truncated, result.Warnings.Any(w => w.Code == SourceErrors.Truncated && w.Details!["external_id"] == sha));
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Query.Contains($"page={Math.Min(pages, 31) + 1}", StringComparison.Ordinal));
    }
}
