using System.Globalization;
using System.Text.RegularExpressions;
using WorkLens.Core.Common;

namespace WorkLens.Core.Periods;

/// <summary>
/// Converts a calendar month (<c>yyyy-MM</c>) in a time zone to a <see cref="Period"/> of UTC instants
/// (docs/design/domain-model.md section 1, OQ-07).
/// </summary>
public static partial class PeriodCalculator
{
    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    /// <summary>True when <paramref name="label"/> has the form <c>yyyy-MM</c> with a month of 01 to 12.</summary>
    public static bool IsValidLabel(string label) => LabelPattern().IsMatch(label);

    /// <summary>Resolves <paramref name="ianaTimeZoneId"/> with <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>.</summary>
    /// <exception cref="TimeZoneNotFoundException">The time zone is unknown on this host.</exception>
    public static Period Calculate(string label, string ianaTimeZoneId) =>
        Calculate(label, TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId), ianaTimeZoneId);

    /// <summary>
    /// Computes <c>[start, end)</c> for the month <paramref name="label"/> in <paramref name="zone"/>.
    /// <paramref name="timezoneName"/> is written to <see cref="Period.Timezone"/>.
    /// </summary>
    /// <exception cref="FormatException"><paramref name="label"/> is not <c>yyyy-MM</c>.</exception>
    public static Period Calculate(string label, TimeZoneInfo zone, string timezoneName)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (!IsValidLabel(label))
        {
            throw new FormatException($"Period '{label}' is not in the form yyyy-MM.");
        }

        var year = int.Parse(label.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture);
        var month = int.Parse(label.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);

        return new Period(label, StartOfDay(first, zone), StartOfDay(first.AddMonths(1), zone), timezoneName);
    }

    // The first instant of a local calendar day. When local midnight does not exist (a DST gap), the day starts at
    // its first valid local time. When midnight occurs twice (a fall-back), the day starts at the earlier instant.
    private static DateTimeOffset StartOfDay(DateTime localMidnight, TimeZoneInfo zone)
    {
        var local = localMidnight;
        var dayEnd = localMidnight.AddDays(1);
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
            if (local >= dayEnd)
            {
                throw new InvalidOperationException($"Time zone '{zone.Id}' has no valid time on {localMidnight:yyyy-MM-dd}.");
            }
        }

        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
