using System.Globalization;
using System.Text;
using WorkLens.Core.Presentation;

namespace WorkLens.Core.Config;

/// <summary>Expands a filename from explicit report values without reading files, settings, or the clock.</summary>
public static class ReportOutputName
{
    public static ReportOutputNameResult Expand(string pattern, string? outputMonth, PresentationReportContext? context)
    {
        if (context is null)
        {
            return Failure("Presentation has no report_context. Run present again, or pass --output <file.pptx>.");
        }

        int year;
        int month;
        if (outputMonth is not null)
        {
            if (outputMonth.Length != 7 || outputMonth[4] != '-'
                || !Digits(outputMonth.AsSpan(0, 4)) || !Digits(outputMonth.AsSpan(5, 2))
                || !int.TryParse(outputMonth.AsSpan(0, 4), CultureInfo.InvariantCulture, out year) || year is < 1 or > 9999
                || !int.TryParse(outputMonth.AsSpan(5, 2), CultureInfo.InvariantCulture, out month) || month is < 1 or > 12)
            {
                return Failure("report.output_month must be a Gregorian year-month from 0001-01 through 9999-12.");
            }
        }
        else
        {
            var period = context.Period;
            if (period.End <= period.Start || !ConfigParser.IsIanaTimeZone(period.Timezone))
            {
                return Failure("report_context requires an increasing period and a known IANA timezone. Run present again, or pass --output <file.pptx>.");
            }

            try
            {
                var zone = TimeZoneInfo.FindSystemTimeZoneById(period.Timezone);
                var last = period.End.ToUniversalTime().AddTicks(-1);
                // ConvertTime clamps at DateTimeOffset limits. Reject that case rather than select the wrong month.
                var localTicks = checked(last.Ticks + zone.GetUtcOffset(last).Ticks);
                if (localTicks < DateTime.MinValue.Ticks || localTicks > DateTime.MaxValue.Ticks)
                {
                    return Failure("The last covered date cannot be represented in the report timezone. Set report.output_month, or pass --output <file.pptx>.");
                }

                var date = TimeZoneInfo.ConvertTime(last, zone);
                year = date.Year;
                month = date.Month;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidTimeZoneException or TimeZoneNotFoundException or OverflowException)
            {
                return Failure("The last covered date cannot be resolved. Set report.output_month, or pass --output <file.pptx>.");
            }
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["period"] = year.ToString("0000", CultureInfo.InvariantCulture) + "-" + month.ToString("00", CultureInfo.InvariantCulture),
            ["year"] = year.ToString("0000", CultureInfo.InvariantCulture),
            ["roc_year"] = (year - 1911).ToString(CultureInfo.InvariantCulture),
            ["month"] = month.ToString(CultureInfo.InvariantCulture),
            ["month_2"] = month.ToString("00", CultureInfo.InvariantCulture),
            ["display_name"] = context.Subject.DisplayName,
        };
        var expanded = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '}')
            {
                return Failure("report.output_filename contains an unmatched closing brace.");
            }

            if (pattern[i] != '{')
            {
                expanded.Append(pattern[i]);
                continue;
            }

            var end = pattern.IndexOf('}', i + 1);
            if (end < 0 || !values.TryGetValue(pattern[(i + 1)..end], out var value))
            {
                return Failure("report.output_filename contains an unknown or malformed token. Use period, year, roc_year, month, month_2, or display_name.");
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return Failure("A filename token has no value in report_context. Run present again, or pass --output <file.pptx>.");
            }

            expanded.Append(value);
            i = end;
        }

        var filename = expanded.ToString();
        if (!IsPortableBasename(filename))
        {
            return Failure("report.output_filename must expand to a portable basename ending in .pptx. Remove paths, reserved device names, invalid characters, and trailing spaces or dots.");
        }

        return new ReportOutputNameResult(filename, null);
    }

    private static bool Digits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (c is < '0' or > '9') { return false; }
        }

        return true;
    }

    private static bool IsPortableBasename(string value)
    {
        if (value.Length <= 5 || !value.EndsWith(".pptx", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(' ') || value.EndsWith('.') || string.IsNullOrWhiteSpace(value[..^5]))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (char.IsControl(c) || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal)) { return false; }
        }

        try
        {
            if (new UTF8Encoding(false, true).GetByteCount(value) > 255) { return false; }
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        var stem = value.Split('.')[0].TrimEnd(' ');
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3], StringComparison.Ordinal));
    }

    private static ReportOutputNameResult Failure(string error) => new(null, error);
}

public sealed record ReportOutputNameResult(string? Filename, string? Error);
