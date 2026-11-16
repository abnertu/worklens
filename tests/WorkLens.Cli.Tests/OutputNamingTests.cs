using System.Text.Json.Nodes;
using Xunit;

namespace WorkLens.Cli.Tests;

public sealed class OutputNamingTests : IDisposable
{
    private readonly CliRunner _cli = new();

    public void Dispose() => _cli.Dispose();

    private async Task<string> Inputs()
    {
        _cli.CopyToWorkspace("schemas/report-ir/1.0/examples/valid/monthly-report.json", "report.json");
        _cli.CopyToWorkspace("schemas/evidence-ir/1.0/examples/valid/monthly-github-gitlab.json", "evidence.json");
        _cli.CopyToWorkspace("templates/sample-monthly/sample-monthly.pptx", "sample-monthly.pptx");
        var manifest = _cli.CopyToWorkspace("templates/sample-monthly/manifest.json", "manifest.json");
        var present = await _cli.RunAsync("present", "--workspace", _cli.Workspace, "--manifest", manifest, "--json");
        Assert.Equal(ExitCodes.Success, present.ExitCode);
        return manifest;
    }

    private string Config(string? pattern, string? month = null)
    {
        var config = new JsonObject
        {
            ["config_version"] = "1.1",
            ["$schema"] = "http://127.0.0.1:1/never-download.json",
            ["identities"] = new JsonArray(new JsonObject { ["username"] = "example.user" }),
            ["sources"] = new JsonArray(new JsonObject
            {
                ["id"] = "example", ["platform"] = "github", ["base_url"] = "https://api.github.com", ["repository"] = "org/repo",
            }),
            ["report"] = new JsonObject(),
        };
        if (pattern is not null) { config["report"]!["output_filename"] = pattern; }
        if (month is not null) { config["report"]!["output_month"] = month; }
        var path = Path.Combine(_cli.Workspace, "worklens.json");
        File.WriteAllText(path, config.ToJsonString());
        return path;
    }

    private void Context(string? name = null, string? end = null, string? timezone = null, bool remove = false)
    {
        var path = Path.Combine(_cli.Workspace, "presentation.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (remove) { document.Remove("report_context"); }
        else
        {
            if (name is not null) { document["report_context"]!["subject"]!["display_name"] = name; }
            if (end is not null) { document["report_context"]!["period"]!["end"] = end; }
            if (timezone is not null) { document["report_context"]!["period"]!["timezone"] = timezone; }
        }
        File.WriteAllText(path, document.ToJsonString());
    }

    private Task<CliRun> Render(string manifest, string config, params string[] extra) =>
        _cli.RunAsync(["render", "--workspace", _cli.Workspace, "--manifest", manifest, "--config", config, "--json", .. extra]);

    [Theory]
    [InlineData("2026-11", "涂价弘", "115年11月_RD月報_涂价弘.pptx")]
    [InlineData("2027-02", "Alex", "116年2月_RD月報_Alex.pptx")]
    public async Task Configured_name_is_written_in_workspace(string month, string name, string expected)
    {
        var manifest = await Inputs();
        Context(name: name);
        var run = await Render(manifest, Config("{roc_year}年{month}月_RD月報_{display_name}.pptx", month));
        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Equal(Path.Combine(_cli.Workspace, expected), run.Json["result"]!["files"]!["output"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_cli.Workspace, expected)));
        Assert.False(File.Exists(Path.Combine(_cli.Workspace, "report.pptx")));
    }

    [Theory]
    [InlineData("Etc/UTC", "2026-10.pptx")]
    [InlineData("Asia/Taipei", "2026-11.pptx")]
    public async Task Filename_uses_report_timezone_instead_of_execution_clock(string timezone, string expected)
    {
        var manifest = await Inputs();
        Context(end: "2026-11-01T00:00:00+00:00", timezone: timezone);
        var run = await Render(manifest, Config("{period}.pptx"));
        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.True(File.Exists(Path.Combine(_cli.Workspace, expected)));
    }

    [Theory]
    [InlineData("{unknown}.pptx", false)]
    [InlineData("{period.pptx", false)]
    [InlineData("../escape.pptx", false)]
    [InlineData("CON.pptx", false)]
    [InlineData("{display_name}.pptx", false)]
    [InlineData("report.pptx", true)]
    public async Task Naming_failure_preserves_existing_destination_and_creates_no_temp(string pattern, bool missingContext)
    {
        var manifest = await Inputs();
        Context(name: "invalid/name", remove: missingContext);
        var target = Path.Combine(_cli.Workspace, "report.pptx");
        byte[] original = [1, 3, 5, 7];
        File.WriteAllBytes(target, original);
        var before = Directory.GetFiles(_cli.Workspace).Order(StringComparer.Ordinal).ToArray();
        var run = await Render(manifest, Config(pattern));
        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        Assert.Equal("config.invalid_output_filename", run.Json["error"]!["code"]!.GetValue<string>());
        Assert.Equal(original, File.ReadAllBytes(target));
        Assert.Equal(before.Append(Path.Combine(_cli.Workspace, "worklens.json")).Order(StringComparer.Ordinal), Directory.GetFiles(_cli.Workspace).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("chosen.pptx")]
    [InlineData("report.pptx")]
    public async Task Explicit_output_bypasses_configured_expansion_and_works_with_old_ir(string filename)
    {
        var manifest = await Inputs();
        Context(remove: true);
        _cli.CurrentDirectory = _cli.Home;
        var run = await Render(manifest, Config("{unknown}.pptx"), "--output", filename);
        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.True(File.Exists(Path.Combine(_cli.Home, filename)));
        Assert.False(File.Exists(Path.Combine(_cli.Workspace, filename)));
    }

    [Fact]
    public async Task Old_ir_without_configured_naming_keeps_default()
    {
        var manifest = await Inputs();
        Context(remove: true);
        var run = await Render(manifest, Config(null));
        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.True(File.Exists(Path.Combine(_cli.Workspace, "report.pptx")));
    }

    [Fact]
    public async Task Invalid_month_preserves_existing_output()
    {
        var manifest = await Inputs();
        var target = Path.Combine(_cli.Workspace, "report.pptx");
        File.WriteAllText(target, "original");
        var run = await Render(manifest, Config("report.pptx", "2026-13"));
        Assert.Equal(ExitCodes.Configuration, run.ExitCode);
        Assert.Equal("config.invalid", run.Json["error"]!["code"]!.GetValue<string>());
        Assert.Equal("original", File.ReadAllText(target));
    }
}
