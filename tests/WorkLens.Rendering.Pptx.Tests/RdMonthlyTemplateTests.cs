using System.Text.Json;
using WorkLens.Core.Json;
using WorkLens.Core.Templates;
using Xunit;
using PackageDocument = DocumentFormat.OpenXml.Packaging.PresentationDocument;

namespace WorkLens.Rendering.Pptx.Tests;

/// <summary>The rd-monthly manifest fits its template file.</summary>
public sealed class RdMonthlyTemplateTests
{
    private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "templates", "rd-monthly");

    private static TemplateManifest Manifest =>
        JsonSerializer.Deserialize<TemplateManifest>(File.ReadAllBytes(Path.Combine(Directory, "manifest.json")), WorkLensJson.Options)!;

    // A slot or a layout that the template lacks would fail render for every report.
    [Fact]
    public void Every_layout_and_slot_resolves_to_a_template_placeholder()
    {
        var manifest = Manifest;
        using var document = PackageDocument.Open(Path.Combine(Directory, manifest.Template.File), isEditable: false);

        var layouts = TemplateLayouts.Resolve(document.PresentationPart!, manifest, out var errors);

        Assert.Empty(errors);
        Assert.Equal(manifest.Layouts.Count, layouts.Count);
        Assert.All(manifest.Layouts, l => Assert.Equal(l.Slots.Count, layouts[l.Id].Slots.Count));
    }

    // The brand positions are placeholders too, so that render can write the logo and the unit name into them.
    [Fact]
    public void Every_brand_position_is_a_template_placeholder()
    {
        var manifest = Manifest;
        using var document = PackageDocument.Open(Path.Combine(Directory, manifest.Template.File), isEditable: false);
        var layouts = TemplateLayouts.Resolve(document.PresentationPart!, manifest, out _);

        foreach (var definition in manifest.Layouts.Where(l => l.Brand is not null))
        {
            var placeholders = TemplateLayouts.Shapes(layouts[definition.Id].Part.SlideLayout?.CommonSlideData?.ShapeTree)
                .Where(s => s.Placeholder is not null)
                .Select(s => s.Name)
                .ToList();
            foreach (var reference in new[] { definition.Brand!.Logo?.Placeholder, definition.Brand.UnitName?.Placeholder }.OfType<PlaceholderRef>())
            {
                Assert.Contains(reference.Name, placeholders);
            }
        }
    }
}
