using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkLens.Connectors.Git.GitHub;

// The GitHub REST response fields the connector reads. Every other field is ignored, so new GitHub fields never
// break collection. Not WorkLensJson.Options: that one disallows unmapped members for WorkLens' own documents.

internal static class GitHubJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        NumberHandling = JsonNumberHandling.Strict,
    };
}

internal sealed record RepositoryDto(string? DefaultBranch);

internal sealed record AccountDto(string? Login, string? Type);

internal sealed record GitPersonDto(string? Name, string? Email, DateTimeOffset? Date);

internal sealed record GitCommitDto(GitPersonDto? Author, GitPersonDto? Committer, string? Message);

internal sealed record ParentDto(string? Sha);

internal sealed record CommitStatsDto(int Additions, int Deletions);

internal sealed record CommitFileDto(string? Filename);

/// <summary>A commit as the commit list, the pull request commit list and the commit detail return it.</summary>
internal sealed record CommitDto(
    string? Sha,
    string? HtmlUrl,
    GitCommitDto? Commit,
    AccountDto? Author,
    AccountDto? Committer,
    IReadOnlyList<ParentDto>? Parents,
    CommitStatsDto? Stats,
    IReadOnlyList<CommitFileDto>? Files);

/// <summary>A pull request as the list returns it; the detail adds the counts.</summary>
internal sealed record PullRequestDto(
    long Number,
    string? HtmlUrl,
    string? State,
    string? Title,
    string? Body,
    AccountDto? User,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset? MergedAt,
    int? Additions,
    int? Deletions,
    int? ChangedFiles,
    int? Commits);
