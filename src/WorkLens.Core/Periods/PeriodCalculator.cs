using System.Globalization;
using WorkLens.Core.Common;

namespace WorkLens.Core.Periods;

/// <summary>
/// Converts a calendar month (<c>yyyy-MM</c>) in a time zone to a <see cref="Period"/> of UTC instants
/// (docs/design/domain-model.md section 1, OQ-07).
/// </summary>
public static class PeriodCalculator
{
    /// <summary>True for an exact <c>yyyy-MM</c> calendar month with a representable next-month boundary.</summary>
    public static bool IsValidLabel(string label) =>
        DateTime.TryParseExact(label, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first)
        && (first.Year < 9999 || first.Month < 12);

    /// <summary>Resolves <paramref name="ianaTimeZoneId"/> with <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>.</summary>
    /// <exception cref="TimeZoneNotFoundException">The time zone is unknown on this host.</exception>
    public static Period Calculate(string label, string ianaTimeZoneId) =>
        Calculate(label, TimeZoneInfo.FindSystemTimeZoneById(ianaTimeZoneId), ianaTimeZoneId);

    /// <summary>
    /// Computes <c>[start, end)</c> for the month <paramref name="label"/> in <paramref name="zone"/>.
    /// <paramref name="timezoneName"/> is written to <see cref="Period.Timezone"/>.
    /// </summary>
    /// <exception cref="FormatException">The month or its UTC boundaries cannot be represented.</exception>
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

        try
        {
            return new Period(label, StartOfDay(first, zone), StartOfDay(first.AddMonths(1), zone), timezoneName);
        }
        catch (ArgumentException ex)
        {
            throw new FormatException($"Period '{label}' cannot be represented as UTC month boundaries in time zone '{timezoneName}'.", ex);
        }
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
