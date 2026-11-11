using System.CommandLine;
using System.Text.Json.Nodes;
using WorkLens.Core.Errors;
using WorkLens.Core.Presentation;
using WorkLens.Core.Templates;
using WorkLens.Core.Validation;
using WorkLens.Rendering.Pptx;

namespace WorkLens.Cli;

internal static class RenderCommand
{
    /// <summary>
    /// <c>render</c>: validate presentation.json (JSON, schema, consistency with the manifest; exit 2), then render it
    /// into a .pptx with the manifest's template. Template problems are exit 3 (D14).
    /// </summary>
    public static Command Create()
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
        var json = Program.JsonOption();
        foreach (var option in new Option[] { workspace, presentation, manifest, template, output, json })
        {
            command.Options.Add(option);
        }

        command.SetAction(parse => CliOutput.Run("render", parse.GetValue(json), result =>
        {
            var manifestPath = parse.GetValue(manifest) is { } m
                ? Path.GetFullPath(m)
                : throw new CliException(
                    ExitCodes.Configuration,
                    "config.missing_manifest",
                    "No template manifest: pass --manifest <manifest.json>.");
            var ws = parse.GetValue(workspace);
            var presentationPath = InputFiles.Resolve(parse.GetValue(presentation), ws, InputFiles.PresentationFileName);
            var outputPath = InputFiles.Resolve(parse.GetValue(output), ws, InputFiles.PptxFileName);

            var presentationFile = InputFiles.Read(presentationPath);
            var manifestDocument = InputFiles.LoadUpstream<TemplateManifest>(InputFiles.Read(manifestPath), TemplateManifest.SchemaId);
            if (TemplateManifestChecker.CheckIds(manifestDocument) is { Count: > 0 } idErrors)
            {
                throw CliException.FromErrors(idErrors, "manifest.invalid", manifestPath);
            }
            var templatePath = parse.GetValue(template) is { } t
                ? Path.GetFullPath(t)
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, manifestDocument.Template.File));
            var files = new JsonObject
            {
                ["presentation"] = presentationPath,
                ["manifest"] = manifestPath,
                ["template"] = templatePath,
            };
            result.Result = new JsonObject { ["files"] = files };

            var (document, issues) = InputFiles.Load<PresentationDocument>(presentationFile, PresentationDocument.SchemaId);
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
            var rendered = PptxRenderer.Render(document!, manifestDocument, templatePath, temp);
            if (rendered.Errors.Count > 0)
            {
                AtomicFile.TryDelete(temp);
                throw RenderFailure(rendered.Errors, manifestPath, outputPath);
            }

            AtomicFile.Commit(temp, outputPath);
            files["output"] = outputPath;
            result.Result["slide_count"] = document!.Slides.Count;
            result.Lines.Add($"Wrote {outputPath} ({document.Slides.Count} slides).");
            return ExitCodes.Success;
        }));
        return command;
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
