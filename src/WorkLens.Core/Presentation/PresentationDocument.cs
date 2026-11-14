using System.Text.Json.Serialization;

namespace WorkLens.Core.Presentation;

/// <summary>
/// Presentation IR root. Reads every built-in minor (1.0 and 1.1) and names the latest one, which the Layout
/// Resolver writes. Produced deterministically from Report IR, Evidence IR and a Template Manifest. Contains no
/// coordinates, fonts or colors.
/// </summary>
/// <param name="Footer">Footer text of every slide (Presentation IR 1.1).</param>
public sealed record PresentationDocument(
    string SchemaVersion,
    string PresentationId,
    SourceReportRef SourceReport,
    TemplateRef Template,
    IReadOnlyList<Slide> Slides,
    string? Footer = null)
{
    public const string CurrentSchemaVersion = "1.1";
    public const string SchemaId = "urn:worklens:schema:presentation-ir:1.1";
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
[JsonConverter(typeof(SlotValueConverter))]
public abstract record SlotValue
{
    /// <summary>Style role: the id of a manifest style (Presentation IR 1.1).</summary>
    /// <remarks>Declared before <see cref="Truncated"/>: the serializer writes the members in this order.</remarks>
    public string? Style { get; init; }

    /// <summary>Set when the overflow policy truncated the content.</summary>
    public bool? Truncated { get; init; }
}

public sealed record TextSlotValue(string Text) : SlotValue;

public sealed record BulletsSlotValue(IReadOnlyList<BulletItem> Items) : SlotValue;

/// <param name="Style">Style role of the item (Presentation IR 1.1).</param>
public sealed record BulletItem(string Text, int Level, string? Style = null);

/// <summary>A table. <see cref="Rows"/> holds the text of each cell.</summary>
/// <remarks>
/// JSON writes a cell with a style role as <c>{text, style}</c> (Presentation IR 1.1). <see cref="SlotValueConverter"/>
/// reads and writes that form.
/// </remarks>
public sealed record TableSlotValue(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows) : SlotValue
{
    /// <summary>
    /// The style role of each cell, in the shape of <see cref="Rows"/>: null for a cell without a style. Null when no
    /// cell has a style.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<IReadOnlyList<string?>>? CellStyles { get; init; }
}

/// <summary>A metric card (Presentation IR 1.1). Each field is display text, formatted by the Layout Resolver.</summary>
public sealed record MetricSlotValue(string Label, string Value, string? Caption = null) : SlotValue;
