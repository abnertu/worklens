namespace WorkLens.Core.Templates;

/// <summary>
/// Template Manifest root: the legal layouts and slots of a .pptx template, the order of report sections and, from
/// 1.1, the style roles. Reads every built-in minor (1.0 and 1.1) and names the latest one.
/// </summary>
/// <param name="Styles">Style roles by id (1.1). The renderer takes run and cell formats only from these roles.</param>
/// <param name="Footer">Footer constraints (1.1).</param>
public sealed record TemplateManifest(
    string SchemaVersion,
    TemplateInfo Template,
    IReadOnlyList<LayoutDefinition> Layouts,
    IReadOnlyList<SequenceEntry> Sequence,
    TemplateLabels? Labels = null,
    IReadOnlyDictionary<string, StyleDefinition>? Styles = null,
    FooterDefinition? Footer = null)
{
    public const string CurrentSchemaVersion = "1.1";
    public const string SchemaId = "urn:worklens:schema:template-manifest:1.1";
}

/// <summary>A style role. Every format is optional; a missing format is inherited from the template.</summary>
/// <param name="FontSize">Font size in points.</param>
/// <param name="Color">Text color, <c>#RRGGBB</c>.</param>
/// <param name="Fill">Cell or shape fill color, <c>#RRGGBB</c>.</param>
public sealed record StyleDefinition(
    decimal? FontSize = null,
    bool? Bold = null,
    string? Color = null,
    string? Fill = null,
    BorderLine? BorderBottom = null);

/// <param name="Color">Line color, <c>#RRGGBB</c>.</param>
/// <param name="Width">Line width in points.</param>
public sealed record BorderLine(string Color, decimal Width);

/// <param name="MaxChars">Capacity of the footer text.</param>
public sealed record FooterDefinition(int? MaxChars = null);

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

    // 1.1 labels.
    public string? CoverKicker { get; init; }
    public string? PeriodTitle { get; init; }
    public string? CoverSubtitle { get; init; }
    public string? CoverSources { get; init; }
    public string? MetricCompleted { get; init; }
    public string? MetricCompletedCaption { get; init; }
    public string? MetricInProgress { get; init; }
    public string? MetricInProgressCaption { get; init; }
    public string? MetricOperations { get; init; }
    public string? MetricOperationsCaption { get; init; }
    public string? MetricProjects { get; init; }
    public string? MetricProjectsCaption { get; init; }
    public string? HighlightsTitle { get; init; }
    public string? HighlightsEmpty { get; init; }
    public string? SupportTitle { get; init; }
    public string? SupportMore { get; init; }
    public string? SupportEmpty { get; init; }
    public string? Continuation { get; init; }
    public string? FactsTitle { get; init; }
    public string? EvidenceTitle { get; init; }
    public string? EvidenceStateOpen { get; init; }
    public string? EvidenceStateClosed { get; init; }
    public string? EvidenceStateMerged { get; init; }
    public string? EvidenceDate { get; init; }
    public string? OperationsTitle { get; init; }
    public string? ChallengesStatus { get; init; }
    public string? ChallengeResolved { get; init; }
    public string? ChallengeOpen { get; init; }
    public string? ColumnTitle { get; init; }
    public string? ColumnProject { get; init; }
    public string? ColumnStatus { get; init; }
    public string? ColumnEvidence { get; init; }
    public string? ColumnProblem { get; init; }
    public string? ColumnResolution { get; init; }
    public string? EvidenceCommits { get; init; }
    public string? EvidenceIssue { get; init; }
    public string? HumanOrigin { get; init; }
    public string? DetailKicker { get; init; }
    public string? ProjectMore { get; init; }
    public string? Footer { get; init; }
}

public enum LayoutPurpose
{
    Cover,
    Summary,
    Achievements,
    WorkItemDetail,
    Operations,
    Narrative,
    ChallengeTable,
    Closing,
}

public enum SlotKind
{
    Text,
    Bullets,
    Table,
    Metric,
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
    Operations,
    OperationDetails,
    Challenges,
    Improvements,
    NextPlan,
    Support,
    OtherWork,
    Closing,
}

public sealed record LayoutDefinition(
    string Id,
    string SlideLayoutName,
    LayoutPurpose Purpose,
    IReadOnlyList<SlotDefinition> Slots,
    LayoutBrand? Brand = null);

/// <summary>Placeholders of a layout that receive the brand elements given to <c>render</c> (1.1).</summary>
public sealed record LayoutBrand(BrandLogo? Logo = null, BrandUnitName? UnitName = null);

public sealed record BrandLogo(PlaceholderRef Placeholder);

public sealed record BrandUnitName(PlaceholderRef Placeholder, int? MaxChars = null);

public sealed record SlotDefinition(
    string Id,
    PlaceholderRef Placeholder,
    SlotKind Kind,
    bool? Required = null,
    int? MaxChars = null,
    int? MaxItems = null,
    OverflowPolicy? Overflow = null,
    IReadOnlyList<TableColumn>? Columns = null,
    string? HeaderStyle = null,
    IReadOnlyList<string>? RowStyles = null,
    string? TableStyle = null);

/// <summary>A table column of a table slot (1.1).</summary>
/// <param name="Key">Column content; the legal keys depend on the layout purpose.</param>
/// <param name="Width">Percentage of the table width. The widths of a slot add up to 100.</param>
public sealed record TableColumn(string Key, int Width, int? MaxChars = null);

/// <summary>Identifies a placeholder on the slide layout by index and/or shape name.</summary>
public sealed record PlaceholderRef(int? Idx = null, string? Name = null);

public sealed record SequenceEntry(ReportSection Section, string LayoutId);
