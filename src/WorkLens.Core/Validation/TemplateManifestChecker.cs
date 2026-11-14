using System.Text.Json;
using WorkLens.Core.Errors;
using WorkLens.Core.Templates;

namespace WorkLens.Core.Validation;

/// <summary>
/// Checks of a schema-valid Template Manifest that JSON Schema cannot express. Every consumer (Layout Resolver,
/// presentation check, renderer) looks layouts and slots up by id, so ids must be unique. The contract checks cover
/// the table fields of 1.1.
/// </summary>
public static class TemplateManifestChecker
{
    /// <summary>
    /// <c>manifest.duplicate_id</c> for each layout id, and each slot id within a layout, that appears again after its
    /// first occurrence (<c>Details["path"]</c> = the manifest JSON pointer of the repeated id).
    /// </summary>
    public static IReadOnlyList<WorkLensError> CheckIds(TemplateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var errors = new List<WorkLensError>();
        var layoutIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < manifest.Layouts.Count; i++)
        {
            var layout = manifest.Layouts[i];
            if (!layoutIds.Add(layout.Id))
            {
                errors.Add(Error($"/layouts/{i}/id", $"Layout id '{layout.Id}' is defined more than once."));
            }

            var slotIds = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < layout.Slots.Count; j++)
            {
                if (!slotIds.Add(layout.Slots[j].Id))
                {
                    errors.Add(Error(
                        $"/layouts/{i}/slots/{j}/id",
                        $"Slot id '{layout.Slots[j].Id}' is defined more than once in layout '{layout.Id}'."));
                }
            }
        }

        return errors;
    }

    /// <summary>The legal column keys of a table slot, by layout purpose. Other purposes take no <c>columns</c>.</summary>
    private static readonly Dictionary<LayoutPurpose, string[]> ColumnKeys = new()
    {
        [LayoutPurpose.Achievements] = ["title", "project", "status", "evidence"],
        [LayoutPurpose.Operations] = ["title", "status", "evidence"],
        [LayoutPurpose.ChallengeTable] = ["problem", "resolution", "status"],
    };

    /// <summary>
    /// Checks of the 1.1 table contracts (<c>Details["path"]</c> = the manifest JSON pointer of the problem):
    /// <list type="bullet">
    /// <item><c>manifest.column_mismatch</c>: a column key outside the set of the layout purpose, <c>columns</c> on a
    /// purpose without a set, a repeated key, or widths that do not add up to 100.</item>
    /// <item><c>manifest.unknown_style</c>: a <c>header_style</c> or <c>row_styles</c> entry that is not a role in
    /// <c>styles</c>.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<WorkLensError> CheckContracts(TemplateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var errors = new List<WorkLensError>();
        for (var i = 0; i < manifest.Layouts.Count; i++)
        {
            var layout = manifest.Layouts[i];
            for (var j = 0; j < layout.Slots.Count; j++)
            {
                var slot = layout.Slots[j];
                var slotPath = $"/layouts/{i}/slots/{j}";
                if (slot.Columns is { } columns)
                {
                    CheckColumns(layout, slot, columns, slotPath, errors);
                }

                if (slot.HeaderStyle is { } header)
                {
                    CheckStyle(manifest, header, $"{slotPath}/header_style", errors);
                }

                for (var k = 0; k < (slot.RowStyles?.Count ?? 0); k++)
                {
                    CheckStyle(manifest, slot.RowStyles![k], $"{slotPath}/row_styles/{k}", errors);
                }
            }
        }

        return errors;
    }

    private static void CheckColumns(
        LayoutDefinition layout,
        SlotDefinition slot,
        IReadOnlyList<TableColumn> columns,
        string slotPath,
        List<WorkLensError> errors)
    {
        if (!ColumnKeys.TryGetValue(layout.Purpose, out var legal))
        {
            errors.Add(ColumnError(
                $"{slotPath}/columns",
                $"Slot '{slot.Id}' of layout '{layout.Id}' has columns, but purpose '{Snake(layout.Purpose)}' takes none."));
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var k = 0; k < columns.Count; k++)
        {
            var key = columns[k].Key;
            if (!legal.Contains(key, StringComparer.Ordinal))
            {
                errors.Add(ColumnError(
                    $"{slotPath}/columns/{k}/key",
                    $"Column '{key}' is not a column of purpose '{Snake(layout.Purpose)}'; use one of {string.Join(", ", legal)}."));
            }
            else if (!seen.Add(key))
            {
                errors.Add(ColumnError(
                    $"{slotPath}/columns/{k}/key",
                    $"Column '{key}' appears more than once in slot '{slot.Id}' of layout '{layout.Id}'."));
            }
        }

        var total = columns.Sum(c => c.Width);
        if (total != 100)
        {
            errors.Add(ColumnError(
                $"{slotPath}/columns",
                $"The column widths of slot '{slot.Id}' in layout '{layout.Id}' add up to {total}, not 100."));
        }
    }

    private static void CheckStyle(TemplateManifest manifest, string role, string path, List<WorkLensError> errors)
    {
        if (manifest.Styles?.ContainsKey(role) != true)
        {
            errors.Add(new WorkLensError(
                "manifest.unknown_style",
                $"Style role '{role}' is not defined in styles.",
                new Dictionary<string, string> { ["path"] = path }));
        }
    }

    private static string Snake(LayoutPurpose purpose) => JsonNamingPolicy.SnakeCaseLower.ConvertName(purpose.ToString());

    private static WorkLensError ColumnError(string path, string message) =>
        new("manifest.column_mismatch", message, new Dictionary<string, string> { ["path"] = path });

    private static WorkLensError Error(string path, string message) =>
        new("manifest.duplicate_id", message, new Dictionary<string, string> { ["path"] = path });
}
