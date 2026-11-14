using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkLens.Connectors.Git.GitLab;

// The GitLab REST v4 response fields the connector reads (verified against GitLab 16.9). Every other field is
// ignored, so new GitLab fields never break collection. Diff text is not mapped, so it is never kept.

internal static class GitLabJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        NumberHandling = JsonNumberHandling.Strict,
    };
}

/// <summary>
/// <c>GET projects/:id</c>. <c>empty_repo</c> marks a project without commits. GitLab 16.9 returns both
/// <c>merge_requests_access_level</c> and the deprecated <c>merge_requests_enabled</c>.
/// </summary>
internal sealed record ProjectDto(
    long? Id, string? DefaultBranch, bool? EmptyRepo, string? MergeRequestsAccessLevel, bool? MergeRequestsEnabled)
{
    /// <summary>False when the project disables merge requests; GitLab then answers the merge request list with 403.</summary>
    public bool HasMergeRequests =>
        MergeRequestsAccessLevel is { } level
            ? !string.Equals(level, "disabled", StringComparison.OrdinalIgnoreCase)
            : MergeRequestsEnabled != false;
}

/// <summary>
/// A user as merge requests return it. <c>bot</c> is read only when the response has it; GitLab 16.9 merge
/// request authors do not, so no bot is inferred from a name.
/// </summary>
internal sealed record UserDto(string? Username, string? Name, bool? Bot);

internal sealed record CommitStatsDto(int? Additions, int? Deletions);

/// <summary>A commit as the commit list, the merge request commit list and the commit detail return it.</summary>
internal sealed record CommitDto(
    string? Id,
    string? Message,
    string? AuthorName,
    string? AuthorEmail,
    DateTimeOffset? AuthoredDate,
    string? CommitterName,
    string? CommitterEmail,
    DateTimeOffset? CommittedDate,
    IReadOnlyList<string>? ParentIds,
    string? WebUrl,
    CommitStatsDto? Stats);

/// <summary>One file of <c>GET .../repository/commits/:sha/diff</c>, without its diff text.</summary>
internal sealed record DiffDto(string? OldPath, string? NewPath, bool? DeletedFile);

/// <summary>A merge request as the list and the detail return it. <c>iid</c> is the project-scoped number.</summary>
internal sealed record MergeRequestDto(
    long? Iid,
    string? WebUrl,
    string? State,
    string? Title,
    string? Description,
    UserDto? Author,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? MergedAt,
    DateTimeOffset? ClosedAt);
