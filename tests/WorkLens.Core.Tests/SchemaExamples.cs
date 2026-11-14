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

    /// <summary>Theory data: (schema name, version, example file name), for every catalog version.</summary>
    public static TheoryData<string, string, string> Cases(string kind)
    {
        var data = new TheoryData<string, string, string>();
        foreach (var info in IrSchemaCatalog.All)
        {
            foreach (var file in Examples(info, kind))
            {
                data.Add(info.Name, info.Version, Path.GetFileName(file));
            }
        }

        return data;
    }

    /// <summary>Version 1.0 of the schema: the existing tests read 1.0 examples to prove that 1.0 behavior stays.</summary>
    public static IrSchemaInfo Info(string name) => Info(name, "1.0");

    public static IrSchemaInfo Info(string name, string version) =>
        IrSchemaCatalog.Find(name, version) ?? throw new ArgumentException($"Unknown schema '{name}:{version}'.");

    /// <summary>An example of version 1.0 of the schema.</summary>
    public static string ExamplePath(string schemaName, string kind, string fileName) =>
        ExamplePath(schemaName, "1.0", kind, fileName);

    public static string ExamplePath(string schemaName, string version, string kind, string fileName) =>
        Path.Combine(SchemaDirectory(Info(schemaName, version)), "examples", kind, fileName);

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
