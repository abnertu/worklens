namespace WorkLens.Core.Templates;

/// <summary>
/// Template Manifest root (<c>urn:worklens:schema:template-manifest:1.0</c>): the legal layouts and slots of a
/// .pptx template and the order of report sections.
/// </summary>
public sealed record TemplateManifest(
    string SchemaVersion,
    TemplateInfo Template,
    IReadOnlyList<LayoutDefinition> Layouts,
    IReadOnlyList<SequenceEntry> Sequence)
{
    public const string CurrentSchemaVersion = "1.0";
    public const string SchemaId = "urn:worklens:schema:template-manifest:1.0";
}

public sealed record TemplateInfo(string Id, string Version, string File, string? Sha256 = null);

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
