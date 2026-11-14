using System.Text.Json.Nodes;
using WorkLens.Core.Common;
using WorkLens.Core.Config;
using WorkLens.Core.Evidence;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class IssueStatusConfigTests
{
    [Theory]
    [InlineData("github", "gitlab", "alice", false)]
    [InlineData("gitlab", "gitlab", null, false)]
    [InlineData("gitlab", "github", "alice", false)]
    [InlineData("gitlab", null, "alice", true)]
    public void Status_collection_requires_a_gitlab_source_and_matching_username(
        string sourcePlatform, string? identityPlatform, string? username, bool valid)
    {
        var identity = new JsonObject { ["email"] = "alice@example.com" };
        if (identityPlatform is not null) identity["platform"] = identityPlatform;
        if (username is not null) identity["username"] = username;
        var config = new JsonObject
        {
            ["config_version"] = "1.1",
            ["identities"] = new JsonArray(identity),
            ["sources"] = new JsonArray(new JsonObject
            {
                ["id"] = "issues", ["platform"] = sourcePlatform,
                ["base_url"] = "https://gitlab.example.com/api/v4", ["repository"] = "team/app",
                ["issue_status_labels"] = new JsonArray("Todo", "Doing", "Done"),
            }),
        };

        var parsed = ConfigParser.Parse(config.ToJsonString());
        Assert.Equal(valid, parsed.IsValid);
        if (!valid)
        {
            Assert.Equal("config.invalid", parsed.ErrorCode);
            Assert.Contains(parsed.Issues, issue => issue.InstancePath == "/sources/0/issue_status_labels");
        }
    }

    [Fact]
    public void Status_labels_are_part_of_the_query_identity()
    {
        var query = new EvidenceQuery(
            new Period("2026-10", DateTimeOffset.Parse("2026-10-01T00:00:00Z"), DateTimeOffset.Parse("2026-11-01T00:00:00Z"), "Etc/UTC"),
            [new Identity(SourcePlatform.Gitlab, "alice")],
            [new SourceSpec(SourcePlatform.Gitlab, "https://gitlab.example.com/api/v4", "team/app", ["Doing"])]);
        var other = query with { Sources = [query.Sources[0] with { IssueStatusLabels = ["Done"] }] };
        Assert.NotEqual(EvidenceIds.ForDocument(query), EvidenceIds.ForDocument(other));
    }
}
