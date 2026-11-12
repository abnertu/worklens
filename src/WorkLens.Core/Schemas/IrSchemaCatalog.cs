using WorkLens.Core.Config;
using WorkLens.Core.Evidence;
using WorkLens.Core.Presentation;
using WorkLens.Core.Report;
using WorkLens.Core.Templates;

namespace WorkLens.Core.Schemas;

/// <summary>A built-in schema, its C# root type and its embedded resource name.</summary>
public sealed record IrSchemaInfo(string Name, string Version, string Id, Type RootType)
{
    public string ResourceName => $"schemas/{Name}/{Version}/{Name}.schema.json";
}

/// <summary>The single list of built-in schemas, used by the CLI and by the consistency tests.</summary>
public static class IrSchemaCatalog
{
    public static IReadOnlyList<IrSchemaInfo> All { get; } =
    [
        new("evidence-ir", EvidenceDocument.CurrentSchemaVersion, EvidenceDocument.SchemaId, typeof(EvidenceDocument)),
        new("report-ir", ReportDocument.CurrentSchemaVersion, ReportDocument.SchemaId, typeof(ReportDocument)),
        new("presentation-ir", PresentationDocument.CurrentSchemaVersion, PresentationDocument.SchemaId, typeof(PresentationDocument)),
        new("template-manifest", TemplateManifest.CurrentSchemaVersion, TemplateManifest.SchemaId, typeof(TemplateManifest)),
        new("config", WorkLensConfig.CurrentSchemaVersion, WorkLensConfig.SchemaId, typeof(WorkLensConfig)),
    ];

    public static IrSchemaInfo? FindById(string id) => All.FirstOrDefault(s => s.Id == id);

    public static IrSchemaInfo? FindByName(string name) => All.FirstOrDefault(s => s.Name == name);
}
