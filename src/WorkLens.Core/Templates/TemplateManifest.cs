namespace WorkLens.Core.Templates;

/// <summary>
/// Template Manifest root (<c>urn:worklens:schema:template-manifest:1.0</c>): the legal layouts and slots of a
/// .pptx template and the order of report sections.
/// </summary>
public sealed record TemplateManifest(
    string SchemaVersion,
    TemplateInfo Template,
    IReadOnlyList<LayoutDefinition> Layouts,
    IReadOnlyList<SequenceEntry> Sequence,
    TemplateLabels? Labels = null)
{
    public const string CurrentSchemaVersion = "1.0";
    public const string SchemaId = "urn:worklens:schema:template-manifest:1.0";
}

/// <param name="File">Template path, relative to the directory that contains the manifest file.</param>
/// <param name="Sha256">Lowercase hex SHA-256 of the template file bytes as written.</param>
public sealed record TemplateInfo(string Id, string Version, string File, string? Sha256 = null);

/// <summary>
/// Fixed text used by the Layout Resolver. Every key is optional; a missing key falls back to the resolver's English
/// default. Placeholders in braces (for example <c>{period}</c>) are substituted once.
/// </summary>
public sealed record TemplateLabels
{
    public string? CoverTitle { get; init; }
    public string? SummaryTitle { get; init; }
    public string? SummaryStatus { get; init; }
    public string? SummaryActivity { get; init; }
    public IReadOnlyList<string>? ActivityColumns { get; init; }
    public string? AchievementsTitle { get; init; }
    public IReadOnlyList<string>? AchievementsColumns { get; init; }
    public string? ChallengesTitle { get; init; }
    public string? ImprovementsTitle { get; init; }
    public string? NextPlanTitle { get; init; }
    public string? OtherWorkTitle { get; init; }
    public string? ClosingTitle { get; init; }
    public string? Inference { get; init; }
    public string? ConfidenceLow { get; init; }
    public string? ConfidenceMedium { get; init; }
    public string? ConfidenceHigh { get; init; }
    public string? CellStatusCompleted { get; init; }
    public string? CellStatusInProgress { get; init; }
    public string? CellStatusUnknown { get; init; }
    public string? StatusInProgress { get; init; }
    public string? StatusUnknown { get; init; }
    public string? HumanNotes { get; init; }
}

public enum LayoutPurpose
{
    Cover,
    Summary,
    Achievements,
    WorkItemDetail,
    Narrative,
    Closing,
}

public enum SlotKind
{
    Text,
    Bullets,
    Table,
}

public enum OverflowPolicy
{
    Split,
    Truncate,
}

public enum ReportSection
{
    Cover,
    Summary,
    Achievements,
    WorkItemDetails,
    Challenges,
    Improvements,
    NextPlan,
    OtherWork,
    Closing,
}

public sealed record LayoutDefinition(
    string Id,
    string SlideLayoutName,
    LayoutPurpose Purpose,
    IReadOnlyList<SlotDefinition> Slots);

public sealed record SlotDefinition(
    string Id,
    PlaceholderRef Placeholder,
    SlotKind Kind,
    bool? Required = null,
    int? MaxChars = null,
    int? MaxItems = null,
    OverflowPolicy? Overflow = null);

/// <summary>Identifies a placeholder on the slide layout by index and/or shape name.</summary>
public sealed record PlaceholderRef(int? Idx = null, string? Name = null);

public sealed record SequenceEntry(ReportSection Section, string LayoutId);
