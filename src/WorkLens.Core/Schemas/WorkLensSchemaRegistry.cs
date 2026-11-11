using System.Reflection;
using Json.Schema;

namespace WorkLens.Core.Schemas;

/// <summary>
/// Loads every built-in schema once from embedded resources into a private (non-global) JsonSchema.Net registry.
/// A private registry avoids the duplicate-<c>$id</c> exception of the global registry and lets schemas
/// reference each other.
/// </summary>
public sealed class WorkLensSchemaRegistry
{
    private static readonly Lazy<WorkLensSchemaRegistry> DefaultInstance = new(() => new WorkLensSchemaRegistry());

    private readonly Dictionary<string, JsonSchema> _schemas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _texts = new(StringComparer.Ordinal);

    private WorkLensSchemaRegistry()
    {
        var registry = new SchemaRegistry();
        var buildOptions = new BuildOptions { SchemaRegistry = registry };
        foreach (var info in IrSchemaCatalog.All)
        {
            var text = ReadResource(info.ResourceName);
            _texts[info.Id] = text;
            _schemas[info.Id] = JsonSchema.FromText(text, buildOptions);
        }
    }

    public static WorkLensSchemaRegistry Default => DefaultInstance.Value;

    public JsonSchema Get(string schemaId) =>
        _schemas.TryGetValue(schemaId, out var schema)
            ? schema
            : throw new ArgumentException($"Unknown schema id '{schemaId}'.", nameof(schemaId));

    public string GetText(string schemaId) =>
        _texts.TryGetValue(schemaId, out var text)
            ? text
            : throw new ArgumentException($"Unknown schema id '{schemaId}'.", nameof(schemaId));

    private static string ReadResource(string resourceName)
    {
        var assembly = typeof(WorkLensSchemaRegistry).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded schema '{resourceName}' not found in {assembly.GetName().Name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
