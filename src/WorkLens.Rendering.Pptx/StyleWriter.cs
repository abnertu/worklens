using System.Globalization;
using WorkLens.Core.Templates;
using A = DocumentFormat.OpenXml.Drawing;

namespace WorkLens.Rendering.Pptx;

/// <summary>
/// Writes the formats of manifest style roles (ADR-0010). The renderer takes run and cell formats only from these
/// roles; a role that the manifest does not define gives no format, so the template's format stays.
/// </summary>
internal sealed class StyleWriter
{
    private const int EmuPerPoint = 12700;

    private readonly IReadOnlyDictionary<string, StyleDefinition> _styles;

    public StyleWriter(IReadOnlyDictionary<string, StyleDefinition>? styles) =>
        _styles = styles ?? new Dictionary<string, StyleDefinition>(StringComparer.Ordinal);

    /// <summary>The definition of a role; <c>null</c> for no role or a role that the manifest does not define.</summary>
    public StyleDefinition? Find(string? role) => role is not null && _styles.TryGetValue(role, out var style) ? style : null;

    /// <summary>
    /// Each format of <paramref name="over"/>, else of <paramref name="under"/>: a cell style on top of its row style,
    /// or an item style on top of its slot style.
    /// </summary>
    public static StyleDefinition? Merge(StyleDefinition? under, StyleDefinition? over) =>
        (under, over) switch
        {
            (null, _) => over,
            (_, null) => under,
            _ => new StyleDefinition(
                over.FontSize ?? under.FontSize,
                over.Bold ?? under.Bold,
                over.Color ?? under.Color,
                over.Fill ?? under.Fill,
                over.BorderBottom ?? under.BorderBottom),
        };

    /// <summary>The run properties of a style: size, weight and text color; <c>null</c> when it sets none.</summary>
    public static A.RunProperties? RunProperties(StyleDefinition? style)
    {
        if (style is null || (style.FontSize is null && style.Bold is null && style.Color is null))
        {
            return null;
        }

        var properties = new A.RunProperties();
        if (style.FontSize is { } size)
        {
            properties.FontSize = (int)Math.Round(size * 100, MidpointRounding.AwayFromZero);
        }

        if (style.Bold is { } bold)
        {
            properties.Bold = bold;
        }

        if (style.Color is { } color)
        {
            properties.Append(Solid(color));
        }

        return properties;
    }

    /// <summary>The shape fill of a style, or <c>null</c>.</summary>
    public static A.SolidFill? Fill(StyleDefinition? style) => style?.Fill is { } fill ? Solid(fill) : null;

    /// <summary>
    /// Cell properties with the style's bottom border and fill, in schema order (<c>a:lnB</c> before the fill);
    /// empty properties without a style.
    /// </summary>
    public static A.TableCellProperties CellProperties(StyleDefinition? style)
    {
        var properties = new A.TableCellProperties();
        if (style?.BorderBottom is { } border)
        {
            properties.Append(new A.BottomBorderLineProperties(Solid(border.Color))
            {
                Width = (int)Math.Round(border.Width * EmuPerPoint, MidpointRounding.AwayFromZero),
            });
        }

        if (style?.Fill is { } fill)
        {
            properties.Append(Solid(fill));
        }

        return properties;
    }

    /// <summary>A solid fill of a <c>#RRGGBB</c> color.</summary>
    public static A.SolidFill Solid(string color) =>
        new(new A.RgbColorModelHex { Val = color.TrimStart('#').ToUpper(CultureInfo.InvariantCulture) });
}
