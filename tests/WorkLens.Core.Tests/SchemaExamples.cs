using WorkLens.Core.Schemas;
using Xunit;

namespace WorkLens.Core.Tests;

/// <summary>Locates schema files and examples copied into the test output directory.</summary>
internal static class SchemaExamples
{
    public static string SchemasRoot { get; } = Path.Combine(AppContext.BaseDirectory, "schemas");

    public static string SchemaDirectory(IrSchemaInfo info) => Path.Combine(SchemasRoot, info.Name, info.Version);

    public static string SchemaFile(IrSchemaInfo info) => Path.Combine(SchemaDirectory(info), $"{info.Name}.schema.json");

    public static IEnumerable<string> Examples(IrSchemaInfo info, string kind)
    {
        var directory = Path.Combine(SchemaDirectory(info), "examples", kind);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal)
            : [];
    }

    /// <summary>Theory data: (schema name, example file name). Case names include the schema id.</summary>
    public static TheoryData<string, string> Cases(string kind)
    {
        var data = new TheoryData<string, string>();
        foreach (var info in IrSchemaCatalog.All)
        {
            foreach (var file in Examples(info, kind))
            {
                data.Add(info.Name, Path.GetFileName(file));
            }
        }

        return data;
    }

    public static IrSchemaInfo Info(string name) =>
        IrSchemaCatalog.FindByName(name) ?? throw new ArgumentException($"Unknown schema '{name}'.");

    public static string ExamplePath(string schemaName, string kind, string fileName) =>
        Path.Combine(SchemaDirectory(Info(schemaName)), "examples", kind, fileName);

    /// <summary>Repository root, found by walking up to the directory containing WorkLens.slnx.</summary>
    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WorkLens.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("WorkLens.slnx not found above test output.");
    }
}
