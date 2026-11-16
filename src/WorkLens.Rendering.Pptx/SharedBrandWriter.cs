using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using WorkLens.Core.Contracts;
using WorkLens.Core.Errors;
using WorkLens.Core.Templates;
using P = DocumentFormat.OpenXml.Presentation;

namespace WorkLens.Rendering.Pptx;

/// <summary>Resolves declared master targets before any output mutation, then fills the output copy.</summary>
internal static class SharedBrandWriter
{
    internal sealed record Target(SlideMasterPart Part, P.Shape Footer, TemplateLayouts.ShapeInfo Logo, Position LogoFrame);

    public static IReadOnlyList<Target> Resolve(PresentationPart presentation, TemplateManifest manifest, List<WorkLensError> errors)
    {
        var targets = new List<Target>();
        for (var i = 0; i < (manifest.SharedBrand?.Masters.Count ?? 0); i++)
        {
            var definition = manifest.SharedBrand!.Masters[i];
            var path = $"/shared_brand/masters/{i}";
            var matches = presentation.SlideMasterParts.Where(p => p.SlideMaster?.CommonSlideData?.Name?.Value == definition.Name).ToList();
            if (matches.Count != 1)
            {
                Invalid("template.shared_brand_target", $"Expected one master named '{definition.Name}', found {matches.Count}.", path + "/name");
                continue;
            }

            var part = matches[0];
            foreach (var layout in part.SlideLayoutParts)
            {
                if (layout.SlideLayout?.ShowMasterShapes?.Value == false)
                {
                    Invalid("template.shared_brand_hidden", $"Layout '{layout.SlideLayout?.CommonSlideData?.Name}' hides the shared master shapes.", path + "/name");
                }
            }

            var footer = Find(definition.Footer, "/footer");
            var logo = Find(definition.Logo, "/logo");
            if (footer?.Shape is not P.Shape { TextBody: not null } text || footer.Value.Placeholder is not null)
            {
                if (footer is not null)
                {
                    Invalid("template.shared_brand_target", "The shared footer must be an ordinary master text shape.", path + "/footer");
                }

                continue;
            }

            if (logo is not { } image)
            {
                continue;
            }

            if (image.Placeholder is not null || image.Shape is not (P.Shape or P.Picture)
                || TemplateLayouts.PositionOf(image.Shape) is not { Width: > 0, Height: > 0 } frame)
            {
                Invalid("template.shared_brand_target", "The shared logo must be an ordinary master shape or picture with a positive frame.", path + "/logo");
                continue;
            }

            if (TemplateLayouts.PositionOf(text) is not { Width: > 0, Height: > 0 })
            {
                Invalid("template.shared_brand_target", "The shared footer must have a positive frame.", path + "/footer");
                continue;
            }

            targets.Add(new Target(part, text, image, frame));

            TemplateLayouts.ShapeInfo? Find(string name, string suffix)
            {
                var count = part.SlideMaster!.Descendants<P.NonVisualDrawingProperties>().Count(p => p.Name?.Value == name);
                var shapes = TemplateLayouts.Shapes(part.SlideMaster.CommonSlideData?.ShapeTree).Where(p => p.Name == name).ToList();
                if (count != 1 || shapes.Count != 1)
                {
                    Invalid("template.shared_brand_target", $"Expected one top-level master shape named '{name}', found {count} matching shapes.", path + suffix);
                    return null;
                }

                if (shapes[0].Drawing.Hidden?.Value == true)
                {
                    Invalid("template.shared_brand_hidden", $"Shared master shape '{name}' is hidden.", path + suffix);
                    return null;
                }

                return shapes[0];
            }
        }

        return targets;

        void Invalid(string code, string message, string path) => errors.Add(new WorkLensError(code, message,
            new Dictionary<string, string> { ["path"] = path }));
    }

    public static void Write(IReadOnlyList<Target> targets, BrandWriter brand, Func<SlideMasterPart, LogoImage, string> addImage)
    {
        foreach (var target in targets)
        {
            if (brand.FooterText is { } text)
            {
                // Capture the language pair before replacing the only run that might declare it.
                var languageSource = (P.SlideMaster)target.Part.SlideMaster!.CloneNode(true);
                BrandWriter.SetText(target.Footer, text, preserveFormatting: true);
                TextLanguageWriter.Apply(target.Footer, languageSource, replaceLanguages: true);
                brand.FooterWritten();
            }

            if (brand.Logo is { } logo)
            {
                var picture = SlideBuilder.Picture(target.Logo.Drawing.Id!.Value, target.Logo.Name, null,
                    logo.Width, logo.Height, addImage(target.Part, logo), target.LogoFrame, target.Logo.Drawing.Description?.Value);
                target.Logo.Shape.InsertBeforeSelf(picture);
                target.Logo.Shape.Remove();
                brand.LogoWritten();
            }

            target.Part.SlideMaster!.Save();
        }
    }
}
