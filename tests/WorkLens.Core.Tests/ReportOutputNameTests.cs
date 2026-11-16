using WorkLens.Core.Common;
using WorkLens.Core.Config;
using WorkLens.Core.Presentation;
using WorkLens.Core.Report;
using Xunit;

namespace WorkLens.Core.Tests;

public sealed class ReportOutputNameTests
{
    private static PresentationReportContext Context(string end = "2026-11-01T00:00:00+00:00", string zone = "Etc/UTC", string name = "涂价弘") =>
        new(new Period("arbitrary cumulative display label", DateTimeOffset.Parse("2026-01-01T00:00:00+00:00"), DateTimeOffset.Parse(end), zone), new ReportSubject(name));

    [Theory]
    [InlineData("2026-11-01T00:00:00+00:00", "Etc/UTC", "2026-10")]
    [InlineData("2026-11-01T00:00:00+00:00", "Asia/Taipei", "2026-11")]
    [InlineData("2026-10-31T16:00:00+00:00", "Asia/Taipei", "2026-10")]
    [InlineData("2026-11-01T00:00:00+00:00", "America/New_York", "2026-10")]
    [InlineData("2027-01-01T00:00:00+00:00", "Etc/UTC", "2026-12")]
    public void Period_uses_last_included_instant_in_report_timezone(string end, string zone, string expected)
    {
        var result = ReportOutputName.Expand("{period}_{display_name}.pptx", null, Context(end, zone));
        Assert.Null(result.Error);
        Assert.Equal(expected + "_涂价弘.pptx", result.Filename);
    }

    [Fact]
    public void Override_uses_gregorian_month_and_expands_all_tokens_once()
    {
        var result = ReportOutputName.Expand("{period}_{year}_{roc_year}_{month}_{month_2}_{display_name}.pptx", "2026-02", Context(name: "Alex {year}"));
        Assert.Null(result.Error);
        Assert.Equal("2026-02_2026_115_2_02_Alex {year}.pptx", result.Filename);
    }

    [Fact]
    public void Roc_filename_matches_requested_example()
    {
        var result = ReportOutputName.Expand("{roc_year}年{month}月_RD月報_{display_name}.pptx", "2026-11", Context());
        Assert.Equal("115年11月_RD月報_涂价弘.pptx", result.Filename);
    }

    [Theory]
    [InlineData("0000-11")]
    [InlineData("2026-00")]
    [InlineData("2026-13")]
    [InlineData("2026-1")]
    [InlineData("２０２６-11")]
    [InlineData("2026-11\n")]
    public void Invalid_override_is_rejected(string month) =>
        Assert.NotNull(ReportOutputName.Expand("{period}.pptx", month, Context()).Error);

    [Theory]
    [InlineData("")]
    [InlineData(".pptx")]
    [InlineData("{unknown}.pptx")]
    [InlineData("{period.pptx")]
    [InlineData("period}.pptx")]
    [InlineData("{{period}}.pptx")]
    [InlineData("../report.pptx")]
    [InlineData("..\\report.pptx")]
    [InlineData("C:report.pptx")]
    [InlineData("//host/report.pptx")]
    [InlineData("nul.pptx")]
    [InlineData("CON.backup.pptx")]
    [InlineData("COM1.pptx")]
    [InlineData("LPT³.pptx")]
    [InlineData("CON .pptx")]
    [InlineData("bad?.pptx")]
    [InlineData("bad\u0000.pptx")]
    [InlineData("bad.pptx ")]
    [InlineData("bad.pptx.")]
    [InlineData("report.pdf")]
    public void Unsafe_or_malformed_filename_is_rejected(string pattern) =>
        Assert.NotNull(ReportOutputName.Expand(pattern, null, Context()).Error);

    [Theory]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData(" ")]
    public void Unsafe_subject_is_rejected_without_sanitizing(string name) =>
        Assert.NotNull(ReportOutputName.Expand("{display_name}.pptx", null, Context(name: name)).Error);

    [Fact]
    public void Unpaired_surrogate_and_overlong_basename_are_rejected()
    {
        Assert.NotNull(ReportOutputName.Expand("{display_name}.pptx", null, Context(name: "a" + (char)0xD800 + "b")).Error);
        Assert.NotNull(ReportOutputName.Expand("{display_name}.pptx", null, Context(name: new string('中', 84))).Error);
    }

    [Fact]
    public void Missing_context_has_an_actionable_error()
    {
        var result = ReportOutputName.Expand("literal.pptx", "2026-11", null);
        Assert.Null(result.Filename);
        Assert.Contains("present", result.Error, StringComparison.Ordinal);
        Assert.Contains("--output", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_period_and_unknown_timezone_are_rejected()
    {
        Assert.NotNull(ReportOutputName.Expand("{period}.pptx", null, Context(zone: "Invalid/Zone")).Error);
        var context = Context();
        Assert.NotNull(ReportOutputName.Expand("{period}.pptx", null, context with { Period = context.Period with { End = context.Period.Start } }).Error);
        Assert.NotNull(ReportOutputName.Expand("{period}.pptx", null, Context("9999-12-31T23:59:59.9999999+00:00", "Asia/Taipei")).Error);
    }
}
