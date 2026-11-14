using System.CommandLine;
using System.Text.Json.Nodes;
using WorkLens.Core.Config;
using WorkLens.Core.Contracts;
using WorkLens.Core.Errors;
using WorkLens.Core.Presentation;
using WorkLens.Core.Schemas;
using WorkLens.Core.Validation;
using WorkLens.Rendering.Pptx;

namespace WorkLens.Cli;

internal static class RenderCommand
{
    /// <summary>
    /// <c>render</c>: validate presentation.json (JSON, schema, consistency with the manifest; exit 2), then render it
    /// into a .pptx with the manifest's template. Template problems are exit 3 (D14).
    /// </summary>
    public static Command Create(CliHost host)
    {
        var command = new Command("render", "Render presentation.json into a .pptx file.");
        var workspace = Program.WorkspaceOption();
        var presentation = Program.FileOption("--presentation", InputFiles.PresentationFileName);
        var manifest = new Option<string?>("--manifest") { Description = "Template manifest.json (required in M0)." };
        var template = new Option<string?>("--template")
        {
            Description = "Template .pptx (default: the manifest's template.file, relative to the manifest).",
        };
        var output = Program.FileOption("--output", InputFiles.PptxFileName);
        var config = new Option<string?>("--config")
        {
            Description = "Configuration file with the unit name and the logo (config 1.1); without it, render adds no brand.",
        };
        var json = Program.JsonOption();
        foreach (var option in new Option[] { workspace, presentation, manifest, template, output, config, json })
        {
            command.Options.Add(option);
        }

        command.SetAction(parse => new CliOutput(host).Run("render", parse.GetValue(json), result =>
        {
            var manifestPath = parse.GetValue(manifest) is { } m
                ? host.FullPath(m)
                : throw new CliException(
                    ExitCodes.Configuration,
                    "config.missing_manifest",
                    "No template manifest: pass --manifest <manifest.json>.");
            var ws = parse.GetValue(workspace);
            var presentationPath = InputFiles.Resolve(host, parse.GetValue(presentation), ws, InputFiles.PresentationFileName);
            var outputPath = InputFiles.Resolve(host, parse.GetValue(output), ws, InputFiles.PptxFileName);

            var presentationFile = InputFiles.Read(presentationPath);
            var manifestDocument = InputFiles.LoadManifest(manifestPath);
            var templatePath = parse.GetValue(template) is { } t
                ? host.FullPath(t)
                : host.FullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, manifestDocument.Template.File));
            var files = new JsonObject
            {
                ["presentation"] = presentationPath,
                ["manifest"] = manifestPath,
                ["template"] = templatePath,
            };
            result.Result = new JsonObject { ["files"] = files };

            // Only an explicit --config adds brand values (D27): the configuration of collect stays out of render.
            BrandElements? brand = null;
            if (parse.GetValue(config) is { } configOption)
            {
                var (configPath, configDocument) = ConfigFiles.Load(host, configOption);
                files["config"] = configPath;
                brand = LoadBrand(configPath, configDocument);
            }

            var (document, issues) = InputFiles.Load<PresentationDocument>(presentationFile, IrSchemaCatalog.PresentationIr);
            if (document is not null)
            {
                issues = PresentationManifestChecker.Check(document, manifestDocument).Issues;
            }

            if (issues.Count > 0)
            {
                result.Issues.AddRange(issues);
                result.Lines.Add($"{presentationPath}: {issues.Count} error(s); presentation not rendered.");
                return ExitCodes.ValidationFailed;
            }

            if (!File.Exists(templatePath))
            {
                throw new CliException(
                    ExitCodes.Configuration,
                    "input.not_found",
                    $"Input file not found: {templatePath}",
                    CliException.FileDetails(templatePath));
            }

            var temp = AtomicFile.TempPathFor(outputPath);
            var rendered = PptxRenderer.Render(document!, manifestDocument, templatePath, temp, brand);
            if (rendered.Errors.Count > 0)
            {
                AtomicFile.TryDelete(temp);
                throw RenderFailure(rendered.Errors, manifestPath, outputPath);
            }

            foreach (var warning in rendered.Warnings)
            {
                result.Issues.Add(new ValidationIssue(
                    warning.Code, IssueSeverity.Warning, warning.Details?.GetValueOrDefault("path") ?? string.Empty, null, warning.Message));
            }

            AtomicFile.Commit(temp, outputPath);
            files["output"] = outputPath;
            result.Result["slide_count"] = document!.Slides.Count;
            result.Lines.Add($"Wrote {outputPath} ({document.Slides.Count} slides).");
            return ExitCodes.Success;
        }));
        return command;
    }

    /// <summary>
    /// The unit name and the logo of a configuration. The logo path is relative to the configuration file; a missing
    /// file is <c>input.not_found</c> and a file that is not PNG or JPEG is <c>config.invalid_logo</c> (exit 3).
    /// </summary>
    private static BrandElements LoadBrand(string configPath, WorkLensConfig config)
    {
        LogoImage? logo = null;
        if (config.Report?.Logo is { } logoOption)
        {
            string logoPath;
            try
            {
                logoPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(configPath)!, logoOption));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new CliException(
                    ExitCodes.Configuration,
                    "config.invalid_logo",
                    "The logo path of the configuration is not a valid file path.",
                    CliException.FileDetails(configPath));
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(logoPath);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new CliException(
                    ExitCodes.Configuration, "input.not_found", $"Logo file not found: {logoPath}", CliException.FileDetails(logoPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CliException(
                    ExitCodes.Configuration, "input.unreadable", $"Cannot read logo file {logoPath}: {ex.Message}", CliException.FileDetails(logoPath));
            }

            logo = LogoImage.Read(bytes) ?? throw new CliException(
                ExitCodes.Configuration,
                "config.invalid_logo",
                $"Logo file {logoPath} is not a PNG or JPEG image with a readable size.",
                CliException.FileDetails(logoPath));
        }

        return new BrandElements(config.Report?.Organization, logo);
    }

    private static CliException RenderFailure(IReadOnlyList<WorkLensError> errors, string manifestPath, string outputPath)
    {
        if (errors.FirstOrDefault(e => e.Code == "output.write_failed") is { } write)
        {
            return new CliException(ExitCodes.Configuration, write.Code, write.Message, CliException.FileDetails(outputPath));
        }

        return CliException.FromErrors(errors, "template.invalid", manifestPath);
    }
}
