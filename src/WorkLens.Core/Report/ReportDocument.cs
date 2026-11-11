using WorkLens.Core.Common;

namespace WorkLens.Core.Report;

/// <summary>
/// Report IR root (<c>urn:worklens:schema:report-ir:1.0</c>). Written by an agent, reviewed by a human.
/// Contains no layout information and no activity statistics.
/// </summary>
public sealed record ReportDocument(
    string SchemaVersion,
    string ReportId,
    Period Period,
    ReportSubject Subject,
    EvidenceDocumentRef EvidenceDocument,
    IReadOnlyList<WorkItem> WorkItems,
    IReadOnlyList<Narrative> Narratives,
    AnalysisInfo? Analysis = null)
{
    public const string CurrentSchemaVersion = "1.0";
    public const string SchemaId = "urn:worklens:schema:report-ir:1.0";
}

public sealed record ReportSubject(string DisplayName);

/// <summary>Binds a report to the exact evidence document it was written from.</summary>
public sealed record EvidenceDocumentRef(string DocumentId, string Sha256);

/// <summary>Self-reported by the agent; not verified by WorkLens.</summary>
public sealed record AnalysisInfo(string? Agent = null, string? Model = null, string? Notes = null);

public enum WorkCategory
{
    Feature,
    Fix,
    Refactor,
    Docs,
    Test,
    Ops,
    Research,
    Other,
}

public enum WorkStatus
{
    Completed,
    InProgress,
    Unknown,
}

public enum Confidence
{
    Low,
    Medium,
    High,
}

public enum ReviewStatus
{
    Pending,
    Approved,
    Rejected,
}

public enum NarrativeKind
{
    Challenge,
    Improvement,
    NextPlan,
    OtherWork,
}

public sealed record WorkItem(
    string Id,
    ContentOrigin Origin,
    WorkCategory Category,
    WorkStatus Status,
    string Title,
    string Summary,
    IReadOnlyList<ObservedFact> ObservedFacts,
    IReadOnlyList<string> EvidenceRefs,
    AgentInference? AgentInference = null,
    int? Rank = null,
    string? HumanNotes = null,
    ReviewStatus? ReviewStatus = null);

/// <summary>A statement directly verifiable from the referenced evidence.</summary>
public sealed record ObservedFact(string Statement, IReadOnlyList<string> EvidenceRefs);

/// <summary>An interpretation made by the agent; always presented as inference.</summary>
public sealed record AgentInference(string Statement, Confidence Confidence);

public sealed record Narrative(
    string Id,
    NarrativeKind Kind,
    ContentOrigin Origin,
    string Text,
    IReadOnlyList<string>? EvidenceRefs = null);
