using System.Globalization;
using System.Text;
using System.Xml;
using WorkLens.Core.Contracts;
using WorkLens.Core.Errors;
using WorkLens.Core.Templates;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using Presentation = WorkLens.Core.Presentation.PresentationDocument;

namespace WorkLens.Rendering.Pptx;

/// <summary>
/// The brand values and the footer of one render (docs/design/report-template.md section 8.8). Brand values go only
/// into the positions that the manifest declares; a value that no position takes is a warning.
/// </summary>
internal sealed class BrandWriter
{
    private const string Ellipsis = "…";
    private const string FooterSeparator = "｜";
    private const string NotWritten = "render.brand_not_written";

    private readonly BrandElements? _brand;
    private bool _unitWritten;
    private bool _logoWritten;
    private bool _unitFitsFooter = true;

    public BrandWriter(BrandElements? brand, TemplateManifest manifest, Presentation presentation)
    {
        _brand = brand;
        FooterText = ComposeFooter(Clean(brand?.UnitName), Clean(presentation.Footer), manifest.Footer);
    }

    /// <summary>
    /// The text of every footer placeholder: the unit name and the presentation footer joined by <c>｜</c>, either one
    /// alone, or <c>null</c> to keep the template text.
    /// </summary>
    public string? FooterText { get; }

    public LogoImage? Logo => _brand?.Logo;

    /// <summary>The unit name cut to a position's capacity, or <c>null</c> without one.</summary>
    public string? UnitName(int? maxChars)
    {
        var unit = Clean(_brand?.UnitName);
        if (unit is null)
        {
            return null;
        }

        _unitWritten = true;
        return Truncate(unit, maxChars);
    }

    public void LogoWritten() => _logoWritten = true;

    /// <summary>A slide took the footer text; the unit name is written when the footer holds it.</summary>
    public void FooterWritten()
    {
        if (FooterHoldsUnit)
        {
            _unitWritten = true;
        }
    }

    /// <summary>A warning for each brand value that no position took.</summary>
    public IReadOnlyList<WorkLensError> Warnings()
    {
        var warnings = new List<WorkLensError>();

        // The footer is a brand position of its own, so a unit name that does not fit it is a warning even when the
        // cover shows the unit name.
        if (!_unitFitsFooter)
        {
            warnings.Add(Warning("/footer", "The unit name is not written into the footer: it has no room next to the footer text."));
        }
        else if (Clean(_brand?.UnitName) is not null && !_unitWritten)
        {
            warnings.Add(Warning("/layouts", "The unit name is not written: the template declares no unit name position and no footer."));
        }

        if (_brand?.Logo is not null && !_logoWritten)
        {
            warnings.Add(Warning("/layouts", "The logo is not written: the template declares no logo position."));
        }

        return warnings;
    }

    private bool FooterHoldsUnit { get; set; }

    private string? ComposeFooter(string? unit, string? footer, FooterDefinition? position)
    {
        // A unit name goes into the footer only when the manifest declares the footer as a brand position.
        string? unitPart = null;
        if (unit is not null && position is not null)
        {
            var room = position.MaxChars is { } max ? max - (footer is null ? 0 : Length(footer) + Length(FooterSeparator)) : (int?)null;
            if (room is null || Length(unit) <= room || room >= 2)
            {
                unitPart = Truncate(unit, room);
                FooterHoldsUnit = true;
            }
            else
            {
                _unitFitsFooter = false;
            }
        }

        return (unitPart, footer) switch
        {
            ({ } u, { } f) => u + FooterSeparator + f,
            ({ } u, null) => u,
            (null, { } f) => f,
            _ => null,
        };
    }

    /// <summary>Writes the text into a copy of a footer placeholder, in place of its template text.</summary>
    public static void SetText(DocumentFormat.OpenXml.OpenXmlElement footer, string text)
    {
        var body = footer.GetFirstChild<P.TextBody>();
        if (body is null)
        {
            body = new P.TextBody(new A.BodyProperties(), new A.ListStyle());
            footer.Append(body);
        }

        body.RemoveAllChildren<A.Paragraph>();
        body.Append(new A.Paragraph(new A.Run(new A.Text(text))));
    }

    private static string Truncate(string text, int? maxChars) =>
        maxChars is { } max && Length(text) > max
            ? new StringInfo(text).SubstringByTextElements(0, Math.Max(0, max - 1)) + Ellipsis
            : text;

    private static int Length(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>The text without characters that XML does not allow; <c>null</c> when nothing is left.</summary>
    private static string? Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(text, i, 2);
                i++;
            }
            else if (XmlConvert.IsXmlChar(text[i]))
            {
                builder.Append(text[i]);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static WorkLensError Warning(string path, string message) =>
        new(NotWritten, message, new Dictionary<string, string> { ["path"] = path });
}
