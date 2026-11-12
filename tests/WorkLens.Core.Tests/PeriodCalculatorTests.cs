using WorkLens.Core.Common;
using WorkLens.Core.Periods;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class PeriodCalculatorTests
{
    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Asia_Taipei_month_is_converted_to_utc_boundaries()
    {
        var period = PeriodCalculator.Calculate("2026-10", "Asia/Taipei");

        Assert.Equal(new Period("2026-10", Utc("2026-09-30T16:00:00Z"), Utc("2026-10-31T16:00:00Z"), "Asia/Taipei"), period);
        Assert.Equal(TimeSpan.Zero, period.Start.Offset);
        Assert.Equal(TimeSpan.Zero, period.End.Offset);
    }

    [Fact]
    public void New_York_month_in_which_dst_ends_uses_each_boundary_offset()
    {
        // 2026-11-01 00:00 is still EDT (-4); 2026-12-01 00:00 is EST (-5).
        var period = PeriodCalculator.Calculate("2026-11", "America/New_York");

        Assert.Equal(Utc("2026-11-01T04:00:00Z"), period.Start);
        Assert.Equal(Utc("2026-12-01T05:00:00Z"), period.End);
    }

    [Fact]
    public void December_ends_at_the_next_year()
    {
        var period = PeriodCalculator.Calculate("2026-12", "Asia/Taipei");

        Assert.Equal(Utc("2026-11-30T16:00:00Z"), period.Start);
        Assert.Equal(Utc("2026-12-31T16:00:00Z"), period.End);
    }

    [Fact]
    public void Missing_local_midnight_starts_the_month_at_the_first_valid_time()
    {
        // Base offset +02:00; DST (+1h) starts at local 00:00 on March 1, so 00:00-00:59 does not exist.
        var zone = CustomZone();

        var march = PeriodCalculator.Calculate("2026-03", zone, "Test/Gap");
        var february = PeriodCalculator.Calculate("2026-02", zone, "Test/Gap");

        // The first valid local time is 01:00 at +03:00, which is the transition instant.
        Assert.Equal(Utc("2026-02-28T22:00:00Z"), march.Start);
        Assert.Equal(march.Start, february.End);
        Assert.Equal("Test/Gap", march.Timezone);
    }

    [Fact]
    public void Ambiguous_local_midnight_starts_the_month_at_the_earlier_instant()
    {
        // DST ends at local 01:00 (daylight time) on October 1, so 00:00-00:59 occurs twice.
        var zone = CustomZone();

        var october = PeriodCalculator.Calculate("2026-10", zone, "Test/Gap");

        Assert.Equal(Utc("2026-09-30T21:00:00Z"), october.Start);
        Assert.Equal(october.Start, PeriodCalculator.Calculate("2026-09", zone, "Test/Gap").End);
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-00")]
    [InlineData("2026-1")]
    [InlineData("26-10")]
    [InlineData("2026/10")]
    [InlineData("")]
    public void Invalid_label_is_rejected(string label)
    {
        Assert.False(PeriodCalculator.IsValidLabel(label));
        Assert.Throws<FormatException>(() => PeriodCalculator.Calculate(label, TimeZoneInfo.Utc, "UTC"));
    }

    private static TimeZoneInfo CustomZone()
    {
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            DateTime.MinValue.Date,
            DateTime.MaxValue.Date,
            TimeSpan.FromHours(1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 0, 0, 0), 3, 1),
            TimeZoneInfo.TransitionTime.CreateFixedDateRule(new DateTime(1, 1, 1, 1, 0, 0), 10, 1));
        return TimeZoneInfo.CreateCustomTimeZone("Test/Gap", TimeSpan.FromHours(2), "Test", "Test", "Test DST", [rule]);
    }
}
