using WorkLens.Core.Contracts;
using WorkLens.Core.Presentation;

namespace WorkLens.Cli;

/// <summary>Local image paths and bytes belong to the CLI, not to Layout or Rendering.</summary>
internal static class ImageAssets
{
    public static PresentationDocument Rebase(PresentationDocument document, string inputPath, string outputPath) =>
        document with
        {
            Slides = document.Slides.Select(slide => slide with
            {
                Bindings = slide.Bindings.ToDictionary(pair => pair.Key, pair => pair.Value is ImageSlotValue image
                    ? image with { File = RebaseReference(image.File, inputPath, outputPath) }
                    : pair.Value),
            }).ToList(),
        };

    private static string RebaseReference(string reference, string inputPath, string outputPath)
    {
        var relative = Path.GetRelativePath(Path.GetDirectoryName(outputPath)!, Resolve(inputPath, reference)).Replace('\\', '/');
        _ = Resolve(outputPath, relative);
        return relative;
    }

    public static PresentationDocument WithoutArchify(PresentationDocument document) => document with
    {
        Slides = document.Slides.Where(slide => !slide.Bindings.Values.OfType<ImageSlotValue>().Any(image => image.Producer == "archify"))
            .Select((slide, index) => slide with { Id = $"slide-{index + 1:000}" }).ToList(),
    };

    /// <summary>Read each active local file once. Key the verified raster bytes by their exact SHA-256.</summary>
    public static IReadOnlyDictionary<string, RasterImage> Load(PresentationDocument document, string documentPath)
    {
        var files = new Dictionary<string, InputFile>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var rasters = new Dictionary<string, RasterImage>(StringComparer.Ordinal);
        foreach (var image in document.Slides.SelectMany(s => s.Bindings.Values).OfType<ImageSlotValue>())
        {
            var path = Resolve(documentPath, image.File);
            if (!files.TryGetValue(path, out var file)) { files[path] = file = InputFiles.Read(path); }
            if (file.Sha256 != image.Sha256)
            {
                throw new CliException(ExitCodes.Configuration, "input.image_sha256_mismatch",
                    $"Image '{image.Id}' at {path} has SHA-256 {file.Sha256}; expected {image.Sha256}. Regenerate the image reference.", CliException.FileDetails(path));
            }

            rasters[image.Sha256] = RasterImage.Read(file.Bytes) ?? throw new CliException(ExitCodes.Configuration,
                "input.invalid_image", $"Image '{image.Id}' at {path} is not PNG or JPEG with a readable size.", CliException.FileDetails(path));
        }

        return rasters;
    }

    private static string Resolve(string documentPath, string reference)
    {
        // Check both Windows and Unix forms on every host. No URI, network path, device, drive or ADS lookup is allowed.
        var local = reference.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(local) || local.StartsWith('/') || local.Contains(':') || local.Contains('\0')
            || local.Split('/').Any(IsDeviceName))
        {
            throw InvalidPath(documentPath, reference);
        }

        try
        {
            return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(documentPath)!, local.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw InvalidPath(documentPath, reference);
        }
    }

    private static bool IsDeviceName(string segment)
    {
        var stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(stem[3]));
    }

    private static CliException InvalidPath(string documentPath, string reference) => new(ExitCodes.Configuration,
        "input.invalid_image_path", $"Image reference '{reference}' in {documentPath} must be a local relative file path.", CliException.FileDetails(documentPath));
}
