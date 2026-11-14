using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WorkLens.Core.Errors;
using WorkLens.Core.Json;
using WorkLens.Core.Validation;

namespace WorkLens.Cli;

/// <summary>An input file read as bytes. <see cref="Sha256"/> covers the raw bytes, including any BOM.</summary>
internal sealed class InputFile(string path, byte[] bytes)
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public string Path { get; } = path;

    public byte[] Bytes { get; } = bytes;

    public string Sha256 { get; } = Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>The content without a UTF-8 BOM, ready to parse.</summary>
    public ReadOnlyMemory<byte> Content =>
        Bytes.AsSpan().StartsWith(Utf8Bom) ? Bytes.AsMemory(Utf8Bom.Length) : Bytes.AsMemory();

    /// <summary>The content as text, or <c>null</c> when it is not valid UTF-8.</summary>
    public string? TryGetText()
    {
        try
        {
            return StrictUtf8.GetString(Content.Span);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}

/// <summary>Workspace and file option resolution and input loading (D7, D8).</summary>
internal static class InputFiles
{
    public const string ReportFileName = "report.json";
    public const string EvidenceFileName = "evidence.json";
    public const string PresentationFileName = "presentation.json";
    public const string PptxFileName = "report.pptx";

    /// <summary>
    /// An explicit file option is resolved against the host's current directory; without it the default file name
    /// is taken from the workspace (default: the host's current directory).
    /// </summary>
    public static string Resolve(CliHost host, string? explicitPath, string? workspace, string defaultFileName) =>
        explicitPath is not null
            ? host.FullPath(explicitPath)
            : host.FullPath(System.IO.Path.Combine(workspace ?? host.CurrentDirectory, defaultFileName));

    /// <summary>Reads a file; a missing or unreadable file is a configuration error (exit 3).</summary>
    public static InputFile Read(string path)
    {
        try
        {
            return new InputFile(path, File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new CliException(ExitCodes.Configuration, "input.not_found", $"Input file not found: {path}", CliException.FileDetails(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliException(ExitCodes.Configuration, "input.unreadable", $"Cannot read input file {path}: {ex.Message}", CliException.FileDetails(path));
        }
    }

    /// <summary>
    /// The single JSON loading path: parse (<c>json.invalid</c>), schema validation (<c>schema.*</c>) and
    /// deserialization (<c>json.deserialize_failed</c>). Never throws <see cref="JsonException"/>.
    /// </summary>
    public static (T? Value, IReadOnlyList<ValidationIssue> Issues) Load<T>(InputFile file, string schemaId, IrSchemaValidator? validator = null)
        where T : class
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(file.Content);
        }
        catch (JsonException ex)
        {
            return (null, [Issue("json.invalid", string.Empty, $"{System.IO.Path.GetFileName(file.Path)} is not valid JSON: {ex.Message}")]);
        }

        using (document)
        {
            try
            {
                var schema = (validator ?? new IrSchemaValidator()).Validate(schemaId, document.RootElement);
                if (!schema.IsValid)
                {
                    return (null, schema.Issues);
                }

                var value = document.RootElement.Deserialize<T>(WorkLensJson.Options)
                    ?? throw new JsonException("Document deserialized to null.");
                return (value, []);
            }
            catch (JsonException ex)
            {
                return (null, [Issue("json.deserialize_failed", JsonPointer.FromJsonPath(ex.Path), ex.Message)]);
            }
            catch (InvalidOperationException ex)
            {
                // JsonDocument accepts an escaped lone UTF-16 surrogate such as "\ud800"; reading that string throws.
                return (null, [Issue("json.invalid", string.Empty, $"{System.IO.Path.GetFileName(file.Path)} contains a string that is not valid UTF-16 text: {ex.Message}")]);
            }
        }
    }

    /// <summary>
    /// Loads an upstream document (evidence, manifest). Any problem is a configuration error (exit 3,
    /// <c>input.invalid</c>) with the issues attached, because the report author cannot fix it.
    /// </summary>
    public static T LoadUpstream<T>(InputFile file, string schemaId)
        where T : class
    {
        var (value, issues) = Load<T>(file, schemaId);
        return value ?? throw new CliException(
            ExitCodes.Configuration,
            "input.invalid",
            $"{file.Path} is not a valid {typeof(T).Name}.",
            CliException.FileDetails(file.Path),
            issues);
    }

    public static ValidationIssue Issue(string code, string pointer, string message) =>
        new(code, IssueSeverity.Error, pointer, null, message);
}
