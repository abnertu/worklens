using System.Globalization;
using WorkLens.Core.Presentation;
using WorkLens.Core.Templates;

namespace WorkLens.Core.Layout;

/// <summary>
/// Applies a slot's capacity to a value: removes characters that XML does not allow, truncates text to
/// <c>max_chars</c>, and truncates or splits items to <c>max_items</c>.
/// </summary>
internal static class SlotFitter
{
    private const string Ellipsis = "…";

    /// <summary>Whether the value has no content, so that the slot stays unbound.</summary>
    public static bool IsEmpty(SlotValue value) => value switch
    {
        TextSlotValue t => t.Text.Length == 0,
        BulletsSlotValue b => b.Items.Count == 0,
        TableSlotValue t => t.Rows.Count == 0,
        MetricSlotValue m => m.Label.Length == 0 && m.Value.Length == 0 && string.IsNullOrEmpty(m.Caption),
        _ => true,
    };

    /// <summary>
    /// Returns one value per page. A page's value keeps the style roles of the input and is marked <c>truncated</c>
    /// when the input was, when text on that page was shortened, or when items were dropped.
    /// </summary>
    /// <param name="split">Items over <c>max_items</c> go to further pages instead of being dropped.</param>
    /// <param name="grouped">
    /// The caller already fitted the items to <c>max_items</c> in groups, so only the text is truncated here.
    /// </param>
    public static List<SlotValue> Fit(SlotValue value, SlotDefinition slot, bool split, bool grouped = false)
    {
        var maxItems = grouped ? null : slot.MaxItems;
        var inputTruncated = value.Truncated == true;

        switch (value)
        {
            case TextSlotValue text:
                {
                    var (clipped, truncated) = Clip(text.Text, slot.MaxChars);
                    return [text with { Text = clipped, Truncated = Flag(inputTruncated || truncated) }];
                }

            case MetricSlotValue metric:
                {
                    var label = Clip(metric.Label, slot.MaxChars);
                    var number = Clip(metric.Value, slot.MaxChars);
                    var caption = metric.Caption is null ? (null, false) : Clip(metric.Caption, slot.MaxChars);
                    return
                    [
                        metric with
                        {
                            Label = label.Text,
                            Value = number.Text,
                            Caption = caption.Item1,
                            Truncated = Flag(inputTruncated || label.Truncated || number.Truncated || caption.Item2),
                        },
                    ];
                }

            case BulletsSlotValue bullets:
                {
                    var items = bullets.Items
                        .Select(i =>
                        {
                            var (text, truncated) = Clip(i.Text, slot.MaxChars);
                            return (i with { Text = text }, truncated);
                        })
                        .ToList();
                    return Chunk(items, maxItems, split)
                        .Select(c => (SlotValue)(bullets with { Items = c.Items, Truncated = Flag(inputTruncated || c.Truncated) }))
                        .ToList();
                }

            case TableSlotValue table:
                {
                    var columns = table.Columns.Select(LayoutResolver.RemoveInvalidXmlCharacters).ToList();
                    var rows = table.Rows
                        .Select((r, index) =>
                        {
                            var cells = r.Select((cell, c) => Clip(cell, ColumnMaxChars(slot, c))).ToList();
                            var row = new TableRow(cells.Select(c => c.Text).ToList(), table.CellStyles?[index]);
                            return (row, cells.Any(c => c.Truncated));
                        })
                        .ToList();
                    return Chunk(rows, maxItems, split)
                        .Select(c => (SlotValue)(table with
                        {
                            Columns = columns,
                            Rows = c.Items.Select(r => r.Cells).ToList(),
                            CellStyles = table.CellStyles is null ? null : c.Items.Select(r => r.Styles!).ToList(),
                            Truncated = Flag(inputTruncated || c.Truncated),
                        }))
                        .ToList();
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(value));
        }
    }

    private static int? ColumnMaxChars(SlotDefinition slot, int column) =>
        slot.Columns is { } columns && column < columns.Count && columns[column].MaxChars is { } max ? max : slot.MaxChars;

    private static (string Text, bool Truncated) Clip(string text, int? maxChars)
    {
        var clean = LayoutResolver.RemoveInvalidXmlCharacters(text);
        if (maxChars is { } max && new StringInfo(clean).LengthInTextElements > max)
        {
            return (new StringInfo(clean).SubstringByTextElements(0, max - 1) + Ellipsis, true);
        }

        return (clean, false);
    }

    /// <summary>Applies max_items: one chunk, a truncated chunk, or (split) one chunk per page.</summary>
    private static List<(List<T> Items, bool Truncated)> Chunk<T>(List<(T Item, bool Truncated)> items, int? maxItems, bool split)
    {
        if (maxItems is not { } max || items.Count <= max)
        {
            return [(items.Select(i => i.Item).ToList(), items.Any(i => i.Truncated))];
        }

        if (!split)
        {
            return [(items.Take(max).Select(i => i.Item).ToList(), true)];
        }

        return items.Chunk(max).Select(c => (c.Select(i => i.Item).ToList(), c.Any(i => i.Truncated))).ToList();
    }

    private static bool? Flag(bool truncated) => truncated ? true : null;

    private sealed record TableRow(IReadOnlyList<string> Cells, IReadOnlyList<string?>? Styles);
}
