using System.Text.Json.Nodes;
using WorkLens.Connectors.Git.GitLab;
using WorkLens.Core.Common;
using WorkLens.Core.Contracts;
using WorkLens.Core.Evidence;
using WorkLens.Core.Periods;
using WorkLens.Tests.Shared;
using Xunit;
using static WorkLens.Tests.Shared.FixtureHandler;

namespace WorkLens.Connectors.Git.Tests;

public sealed class GitLabIssueTests
{
    private const string BaseUrl = "https://gitlab.example.com/api/v4";
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures", "gitlab", "issue-status");

    private static async Task<SourceCollectResult> Collect(FixtureHandler handler, bool enabled = true)
    {
        using var http = GitLabHttp.CreateClient(handler, new Uri(BaseUrl), "test-token", "0.1.0");
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-11-01T00:00:00Z"));
        var api = new GitLabApiClient(http, new RetryPolicy(time, (_, _) => Task.CompletedTask, RetryPlatform.GitLab));
        var query = new SourceQuery(PeriodCalculator.Calculate("2026-10", "Asia/Taipei"),
            [new Identity(SourcePlatform.Gitlab, "alice"), new Identity(SourcePlatform.Gitlab, "zeta")],
            new SourceSpec(SourcePlatform.Gitlab, BaseUrl, "group/sub/app", enabled ? ["Todo", "Doing", "Pending", "Done"] : null));
        return await new GitLabConnector(api, time).CollectAsync(query, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Issue_events_count_subject_actions_and_current_assignee_activity()
    {
        var handler = FromDirectory(Fixtures);
        var result = await Collect(handler);
        Assert.True(result.IsSuccess, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Empty(handler.Unmatched);
        Assert.Equal(new[] { "1", "2", "5", "6" }, result.Items.Select(i => i.Source.ExternalId));
        Assert.All(result.Items, item => Assert.Equal(EvidenceKind.IssueActivity, item.Kind));
        var actor = result.Items[0];
        Assert.Equal(AuthorRole.Actor, actor.Author.Role);
        Assert.Equal("Alice Latest", actor.Author.Name);
        Assert.Equal("alice", actor.Author.Username);
        Assert.Equal(EvidenceState.Open, actor.State);
        Assert.Equal("Issue description", actor.Body);
        Assert.Equal(new[] { IssueChange.Status, IssueChange.Status, IssueChange.Closed, IssueChange.Reopened }, actor.Transitions!.Select(t => t.Change));
        Assert.Equal(DateTimeOffset.Parse("2026-09-30T16:00:00Z"), actor.Transitions![0].At);
        Assert.Equal(actor.Transitions![^1].At, actor.Timestamps.OccurredAt);
        Assert.All(actor.Transitions!, change => Assert.Equal(IssueActor.Subject, change.Actor));
        var assigned = result.Items[1];
        Assert.Equal(AuthorRole.Assignee, assigned.Author.Role);
        Assert.Equal("ALICE", assigned.Author.Username);
        Assert.Equal("Alice Assignee", assigned.Author.Name);
        Assert.Equal(EvidenceState.Closed, assigned.State);
        Assert.All(assigned.Transitions!, change => Assert.Equal(IssueActor.Other, change.Actor));
        Assert.Null(assigned.Author.Email);
        var tied = result.Items[2];
        Assert.Equal("Alice State", tied.Author.Name);
        Assert.Equal(new[] { "Todo", "Doing" }, tied.Transitions!.Take(2).Select(t => t.Label));
        Assert.Equal(new[] { IssueChange.Status, IssueChange.Status, IssueChange.Closed, IssueChange.Status }, tied.Transitions!.Select(t => t.Change));
        Assert.DoesNotContain(handler.Requests, request => request.RequestUri!.AbsolutePath.Contains("merge_requests", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_or_unrequested_issues_are_not_queried(bool requested)
    {
        var handler = new FixtureHandler().Add("GET", "/api/v4/projects/group%2Fsub%2Fapp",
            Respond(200, new JsonObject { ["id"] = 7, ["empty_repo"] = true,
                ["merge_requests_access_level"] = "disabled", ["issues_access_level"] = "disabled" }));
        var result = await Collect(handler, requested);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Items);
        Assert.Single(handler.Requests);
        Assert.Equal(requested, result.Warnings.Any(w => w.Code == "source.issues_disabled"));
    }

    [Fact]
    public async Task Issue_event_failure_fails_the_source_without_partial_items()
    {
        var handler = new FixtureHandler().Add("GET", "/api/v4/projects/7/issues/2/resource_state_events?per_page=100", Respond(403))
            .AddDirectory(Fixtures);
        var result = await Collect(handler);
        Assert.False(result.IsSuccess);
        Assert.Empty(result.Items);
        Assert.Equal("source.forbidden", Assert.Single(result.Errors).Code);
    }
}
