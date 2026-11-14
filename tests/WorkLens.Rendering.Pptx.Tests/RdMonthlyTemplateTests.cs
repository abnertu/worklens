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

    // The renderer copies the footer and slide number placeholders, so a slot there would show twice.
    [Theory]
    [InlineData("Footer")]
    [InlineData("Slide Number")]
    public void Slot_on_a_footer_placeholder_is_a_template_error(string placeholder)
    {
        var manifest = Manifest;
        var index = manifest.Layouts.Select((l, i) => (l, i)).Single(x => x.l.Id == "narrative_v1").i;
        var layouts = manifest.Layouts.ToList();
        layouts[index] = layouts[index] with
        {
            Slots = [.. layouts[index].Slots.Select(s => s.Id == "title" ? s with { Placeholder = new PlaceholderRef(Name: placeholder) } : s)],
        };
        using var document = PackageDocument.Open(Path.Combine(Directory, manifest.Template.File), isEditable: false);

        TemplateLayouts.Resolve(document.PresentationPart!, manifest with { Layouts = layouts }, out var errors);

        var error = Assert.Single(errors);
        Assert.Equal("template.slot_on_footer", error.Code);
        Assert.Equal($"/layouts/{index}/slots/0/placeholder", error.Details!["path"]);
    }

    /// <summary>The manifest with one brand position of the cover changed to the placeholder.</summary>
    private static (int Index, TemplateManifest Manifest) WithBrandPosition(string position, string placeholder)
    {
        var manifest = Manifest;
        var index = manifest.Layouts.Select((l, i) => (l, i)).Single(x => x.l.Id == "cover_v1").i;
        var layouts = manifest.Layouts.ToList();
        var brand = layouts[index].Brand!;
        var reference = new PlaceholderRef(Name: placeholder);
        layouts[index] = layouts[index] with
        {
            Brand = position == "logo"
                ? brand with { Logo = new BrandLogo(reference) }
                : brand with { UnitName = brand.UnitName! with { Placeholder = reference } },
        };
        return (index, manifest with { Layouts = layouts });
    }

    private static List<WorkLens.Core.Errors.WorkLensError> ResolveErrors(TemplateManifest manifest, string? templatePath = null)
    {
        using var document = PackageDocument.Open(templatePath ?? Path.Combine(Directory, manifest.Template.File), isEditable: false);
        TemplateLayouts.Resolve(document.PresentationPart!, manifest, out var errors);
        return errors;
    }

    // A brand position that the template lacks, or that a slot or the other brand value takes, would lose a brand
    // value without notice or put two shapes on one placeholder.
    [Theory]
    [InlineData("logo", "No Such Logo", "template.placeholder_missing")]
    [InlineData("logo", "Subject", "template.brand_on_slot")]
    [InlineData("unit_name", "No Such Unit", "template.placeholder_missing")]
    [InlineData("unit_name", "Kicker", "template.brand_on_slot")]
    [InlineData("unit_name", "Logo", "template.brand_conflict")]
    public void Unusable_brand_position_is_a_template_error(string position, string placeholder, string code)
    {
        var (index, manifest) = WithBrandPosition(position, placeholder);

        var error = Assert.Single(ResolveErrors(manifest));

        Assert.Equal((code, $"/layouts/{index}/brand/{position}/placeholder"), (error.Code, error.Details!["path"]));
    }

    [Fact]
    public void Brand_position_on_the_slide_number_is_a_template_error()
    {
        var manifest = Manifest;
        var index = manifest.Layouts.Select((l, i) => (l, i)).Single(x => x.l.Id == "narrative_v1").i;
        var layouts = manifest.Layouts.ToList();
        layouts[index] = layouts[index] with { Brand = new LayoutBrand(new BrandLogo(new PlaceholderRef(Name: "Slide Number"))) };

        var error = Assert.Single(ResolveErrors(manifest with { Layouts = layouts }));

        Assert.Equal("template.slot_on_footer", error.Code);
    }

    // The logo is scaled into its box, so a logo placeholder without a position of its own cannot take it.
    [Fact]
    public void Logo_placeholder_without_a_position_is_a_template_error()
    {
        var manifest = Manifest;
        var copy = Path.Combine(Path.GetTempPath(), $"worklens-rd-{Guid.NewGuid():N}.pptx");
        File.Copy(Path.Combine(Directory, manifest.Template.File), copy);
        try
        {
            using (var document = PackageDocument.Open(copy, isEditable: true))
            {
                var cover = document.PresentationPart!.SlideMasterParts.SelectMany(m => m.SlideLayoutParts)
                    .Single(p => p.SlideLayout!.CommonSlideData!.Name!.Value == "Cover");
                var logo = TemplateLayouts.Shapes(cover.SlideLayout!.CommonSlideData!.ShapeTree).Single(s => s.Name == "Logo").Shape;
                logo.Descendants<DocumentFormat.OpenXml.Drawing.Transform2D>().Single().Remove();
            }

            var index = manifest.Layouts.Select((l, i) => (l, i)).Single(x => x.l.Id == "cover_v1").i;
            var error = Assert.Single(ResolveErrors(manifest, copy));

            Assert.Equal(("template.placeholder_position_missing", $"/layouts/{index}/brand/logo/placeholder"), (error.Code, error.Details!["path"]));
        }
        finally
        {
            File.Delete(copy);
        }
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
