using WorkLens.Core.Common;

namespace WorkLens.Core.Evidence;

/// <summary>Evidence IR root (<c>urn:worklens:schema:evidence-ir:1.0</c>).</summary>
public sealed record EvidenceDocument(
    string SchemaVersion,
    string DocumentId,
    DateTimeOffset GeneratedAt,
    GeneratorInfo Generator,
    EvidenceQuery Query,
    IReadOnlyList<EvidenceItem> Items)
{
    public const string CurrentSchemaVersion = "1.0";
    public const string SchemaId = "urn:worklens:schema:evidence-ir:1.0";
}

public sealed record GeneratorInfo(string Name, string Version);

public sealed record EvidenceQuery(
    Period Period,
    IReadOnlyList<Identity> Identities,
    IReadOnlyList<SourceSpec> Sources);

/// <summary>An identity treated as the report subject. At least one field is set.</summary>
public sealed record Identity(SourcePlatform? Platform = null, string? Username = null, string? Email = null);

public sealed record SourceSpec(SourcePlatform Platform, string BaseUrl, string Repository);

public enum EvidenceKind
{
    Commit,
    ChangeRequest,
    Issue,
}

public enum EvidenceState
{
    Open,
    Closed,
    Merged,
}

public enum AuthorRole
{
    Author,
    CoAuthor,
    Committer,
}

public enum RelationType
{
    Contains,
    References,
    Closes,
}

public enum EvidenceFlag
{
    MergeCommit,
    Revert,
    Bot,
    Duplicate,
}

public sealed record EvidenceItem(
    string Id,
    EvidenceKind Kind,
    EvidenceSource Source,
    string Title,
    EvidenceAuthor Author,
    EvidenceTimestamps Timestamps,
    DateTimeOffset FetchedAt,
    string? Body = null,
    EvidenceState? State = null,
    ChangeStats? Stats = null,
    IReadOnlyList<string>? Paths = null,
    IReadOnlyList<EvidenceRelation>? Relations = null,
    IReadOnlyList<EvidenceFlag>? Flags = null);

public sealed record EvidenceSource(
    SourcePlatform Platform,
    string BaseUrl,
    string Repository,
    string ExternalId,
    string Url);

public sealed record EvidenceAuthor(
    AuthorRole Role,
    string? Name = null,
    string? Username = null,
    string? Email = null);

/// <summary>
/// Source event times. <see cref="OccurredAt"/> is the time used to assign the item to a period.
/// </summary>
public sealed record EvidenceTimestamps(
    DateTimeOffset OccurredAt,
    DateTimeOffset? AuthoredAt = null,
    DateTimeOffset? CommittedAt = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? MergedAt = null,
    DateTimeOffset? ClosedAt = null);

public sealed record ChangeStats(int FilesChanged, int Additions, int Deletions);

public sealed record EvidenceRelation(RelationType Type, string TargetId);
