using WorkLens.Core.Errors;
using WorkLens.Core.Json;
using WorkLens.Core.Layout;
using WorkLens.Core.Presentation;
using WorkLens.Core.Templates;

namespace WorkLens.Core.Validation;

/// <summary>
/// Checks a schema-valid Presentation IR against the Template Manifest it will be rendered with (<c>presentation.*</c>
/// errors whose instance path points into the presentation). A presentation produced by the Layout Resolver with the
/// same manifest always passes; a hand-edited one may not, and the renderer must not see it.
/// </summary>
public static class PresentationManifestChecker
{
    public static ValidationResult Check(PresentationDocument presentation, TemplateManifest manifest)
    {
        var issues = new List<ValidationIssue>();
        if (presentation.Template.Id != manifest.Template.Id || presentation.Template.Version != manifest.Template.Version)
        {
            issues.Add(Issue(
                "presentation.template_mismatch",
                "/template",
                $"The presentation was resolved for template {presentation.Template.Id} {presentation.Template.Version}, but the manifest is {manifest.Template.Id} {manifest.Template.Version}."));
        }

        // First occurrence wins, as in the Layout Resolver (duplicate ids are reported by TemplateManifestChecker).
        var layouts = new Dictionary<string, LayoutDefinition>(StringComparer.Ordinal);
        foreach (var definition in manifest.Layouts)
        {
            layouts.TryAdd(definition.Id, definition);
        }
        for (var i = 0; i < presentation.Slides.Count; i++)
        {
            var slide = presentation.Slides[i];
            var slidePath = $"/slides/{i}";
            if (!layouts.TryGetValue(slide.LayoutId, out var layout))
            {
                issues.Add(Issue(
                    "presentation.unknown_layout",
                    $"{slidePath}/layout_id",
                    $"Slide {slide.Id} uses layout '{slide.LayoutId}', which the manifest does not define."));
                continue;
            }

            foreach (var missing in layout.Slots.Where(s => s.Required == true && !slide.Bindings.ContainsKey(s.Id)))
            {
                issues.Add(Issue(
                    "presentation.missing_required_slot",
                    $"{slidePath}/bindings",
                    $"Slide {slide.Id} does not bind the required slot '{missing.Id}' of layout '{layout.Id}'."));
            }

            foreach (var (slotId, value) in slide.Bindings)
            {
                var path = $"{slidePath}/bindings/{JsonPointer.Escape(slotId)}";
                var slot = layout.Slots.FirstOrDefault(s => s.Id == slotId);
                if (slot is null)
                {
                    issues.Add(Issue(
                        "presentation.unknown_slot",
                        path,
                        $"Slide {slide.Id} binds slot '{slotId}', which layout '{layout.Id}' does not define."));
                    continue;
                }

                if (KindOf(value) != slot.Kind)
                {
                    issues.Add(Issue(
                        "presentation.slot_kind_mismatch",
                        $"{path}/kind",
                        $"Slot '{slotId}' of layout '{layout.Id}' takes {Name(slot.Kind)}, but slide {slide.Id} binds {Name(KindOf(value))}."));
                }

                if (value is TableSlotValue table)
                {
                    for (var r = 0; r < table.Rows.Count; r++)
                    {
                        if (table.Rows[r].Count != table.Columns.Count)
                        {
                            issues.Add(Issue(
                                "presentation.table_row_width",
                                $"{path}/rows/{r}",
                                $"Row {r} has {table.Rows[r].Count} cells, but the table has {table.Columns.Count} columns."));
                        }
                    }
                }

                foreach (var (text, textPath) in Strings(value, path))
                {
                    if (LayoutResolver.ContainsInvalidXmlCharacter(text))
                    {
                        issues.Add(Issue(
                            "presentation.invalid_character",
                            textPath,
                            "The text contains a character that XML 1.0 (and therefore .pptx) cannot store, such as a control character."));
                    }
                }
            }
        }

        return new ValidationResult(issues);
    }

    private static SlotKind KindOf(SlotValue value) => value switch
    {
        TextSlotValue => SlotKind.Text,
        BulletsSlotValue => SlotKind.Bullets,
        TableSlotValue => SlotKind.Table,
        _ => throw new NotSupportedException($"Unsupported slot value {value.GetType().Name}."),
    };

    private static IEnumerable<(string Text, string Path)> Strings(SlotValue value, string path)
    {
        switch (value)
        {
            case TextSlotValue text:
                yield return (text.Text, $"{path}/text");
                break;
            case BulletsSlotValue bullets:
                for (var k = 0; k < bullets.Items.Count; k++)
                {
                    yield return (bullets.Items[k].Text, $"{path}/items/{k}/text");
                }

                break;
            case TableSlotValue table:
                for (var c = 0; c < table.Columns.Count; c++)
                {
                    yield return (table.Columns[c], $"{path}/columns/{c}");
                }

                for (var r = 0; r < table.Rows.Count; r++)
                {
                    for (var c = 0; c < table.Rows[r].Count; c++)
                    {
                        yield return (table.Rows[r][c], $"{path}/rows/{r}/{c}");
                    }
                }

                break;
        }
    }

    private static string Name(SlotKind kind) => kind.ToString().ToLowerInvariant();

    private static ValidationIssue Issue(string code, string path, string message) =>
        new(code, IssueSeverity.Error, path, null, message);
}
