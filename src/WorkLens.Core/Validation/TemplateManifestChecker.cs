using WorkLens.Core.Errors;
using WorkLens.Core.Templates;

namespace WorkLens.Core.Validation;

/// <summary>
/// Checks of a schema-valid Template Manifest that JSON Schema cannot express. Every consumer (Layout Resolver,
/// presentation check, renderer) looks layouts and slots up by id, so ids must be unique.
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

    private static WorkLensError Error(string path, string message) =>
        new("manifest.duplicate_id", message, new Dictionary<string, string> { ["path"] = path });
}
