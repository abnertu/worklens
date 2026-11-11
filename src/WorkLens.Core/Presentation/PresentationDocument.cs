using System.Text.Json.Serialization;

namespace WorkLens.Core.Presentation;

/// <summary>
/// Presentation IR root (<c>urn:worklens:schema:presentation-ir:1.0</c>). Produced deterministically from
/// Report IR, Evidence IR and a Template Manifest. Contains no coordinates, fonts or colors.
/// </summary>
public sealed record PresentationDocument(
    string SchemaVersion,
    string PresentationId,
    SourceReportRef SourceReport,
    TemplateRef Template,
    IReadOnlyList<Slide> Slides)
{
    public const string CurrentSchemaVersion = "1.0";
    public const string SchemaId = "urn:worklens:schema:presentation-ir:1.0";
}

public sealed record SourceReportRef(string ReportId, string Sha256);

public sealed record TemplateRef(string Id, string Version);

public sealed record Slide(
    string Id,
    string LayoutId,
    IReadOnlyDictionary<string, SlotValue> Bindings,
    IReadOnlyList<string> SourceRefs,
    SlideContinuation? Continuation = null);

public sealed record SlideContinuation(int Index, int Total);

/// <summary>Value bound to a template slot, discriminated by <c>kind</c>.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextSlotValue), "text")]
[JsonDerivedType(typeof(BulletsSlotValue), "bullets")]
[JsonDerivedType(typeof(TableSlotValue), "table")]
public abstract record SlotValue
{
    /// <summary>Set when the overflow policy truncated the content.</summary>
    public bool? Truncated { get; init; }
}

public sealed record TextSlotValue(string Text) : SlotValue;

public sealed record BulletsSlotValue(IReadOnlyList<BulletItem> Items) : SlotValue;

public sealed record BulletItem(string Text, int Level);

public sealed record TableSlotValue(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows) : SlotValue;
