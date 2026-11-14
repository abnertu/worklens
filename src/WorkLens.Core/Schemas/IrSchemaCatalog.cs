using WorkLens.Core.Config;
using WorkLens.Core.Evidence;
using WorkLens.Core.Presentation;
using WorkLens.Core.Report;
using WorkLens.Core.Templates;

namespace WorkLens.Core.Schemas;

/// <summary>
/// A built-in schema version and the C# root type that reads it. One root type reads every minor of its schema
/// name, because each minor is a superset of the previous one.
/// </summary>
public sealed record IrSchemaInfo(string Name, string Version, Type RootType)
{
    public string Id => $"urn:worklens:schema:{Name}:{Version}";

    public string ResourceName => $"schemas/{Name}/{Version}/{Name}.schema.json";

    /// <summary>The root property that holds the version of a document of this schema.</summary>
    public string VersionProperty => Name == ConfigName ? "config_version" : "schema_version";

    internal const string ConfigName = "config";
}

/// <summary>The single list of built-in schema versions, used by the CLI, the validators and the consistency tests.</summary>
public static class IrSchemaCatalog
{
    public const string EvidenceIr = "evidence-ir";
    public const string ReportIr = "report-ir";
    public const string PresentationIr = "presentation-ir";
    public const string TemplateManifest = "template-manifest";
    public const string Config = IrSchemaInfo.ConfigName;

    /// <summary>Every (name, version), grouped by name, versions in ascending order.</summary>
    public static IReadOnlyList<IrSchemaInfo> All { get; } =
    [
        new(EvidenceIr, "1.0", typeof(EvidenceDocument)),
        new(ReportIr, "1.0", typeof(ReportDocument)),
        new(ReportIr, "1.1", typeof(ReportDocument)),
        new(PresentationIr, "1.0", typeof(PresentationDocument)),
        new(PresentationIr, "1.1", typeof(PresentationDocument)),
        new(TemplateManifest, "1.0", typeof(Templates.TemplateManifest)),
        new(TemplateManifest, "1.1", typeof(Templates.TemplateManifest)),
        new(Config, "1.0", typeof(WorkLensConfig)),
        new(Config, "1.1", typeof(WorkLensConfig)),
    ];

    public static IrSchemaInfo? Find(string name, string version) =>
        All.FirstOrDefault(s => s.Name == name && s.Version == version);

    /// <summary>The latest built-in minor of <paramref name="name"/>, or <c>null</c> for an unknown name.</summary>
    public static IrSchemaInfo? Latest(string name) => All.LastOrDefault(s => s.Name == name);

    /// <summary>The built-in versions of <paramref name="name"/>, in ascending order.</summary>
    public static IReadOnlyList<IrSchemaInfo> Versions(string name) => [.. All.Where(s => s.Name == name)];
}
