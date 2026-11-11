using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkLens.Core.Json;

/// <summary>
/// Serializer settings that produce the canonical JSON form (docs/design/schema-versioning.md section 5).
/// </summary>
public static class WorkLensJson
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// Serializes <paramref name="value"/> in canonical form: indented, LF line endings and one trailing LF.
    /// Write the result as UTF-8 without BOM (see <see cref="SerializeCanonicalUtf8{T}"/>).
    /// </summary>
    public static string SerializeCanonical<T>(T value) => JsonSerializer.Serialize(value, Options) + "\n";

    /// <summary>Canonical form as UTF-8 bytes without BOM, ready to write to a file or stdout.</summary>
    public static byte[] SerializeCanonicalUtf8<T>(T value) => Utf8NoBom.GetBytes(SerializeCanonical(value));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowOutOfOrderMetadataProperties = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            WriteIndented = true,
            NewLine = "\n",
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
