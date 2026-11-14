using System.Text.Json;
using WorkLens.Core.Config;

namespace WorkLens.Cli;

/// <summary>
/// Finds and loads <c>worklens.json</c>: <c>--config</c>, then <c>WORKLENS_CONFIG</c>, then <c>./worklens.json</c>,
/// then <c>~/.worklens/worklens.json</c> (docs/design/cli-ux.md section 3).
/// </summary>
internal static class ConfigFiles
{
    public const string FileName = "worklens.json";
    public const string EnvironmentVariable = "WORKLENS_CONFIG";

    public static (string Path, WorkLensConfig Config) Load(CliHost host, string? explicitPath)
    {
        var path = Find(host, explicitPath);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw NotFound($"Configuration file not found: {path}", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CliException(
                ExitCodes.Configuration,
                ConfigParseResult.Invalid,
                $"Cannot read configuration file {path}: {ex.Message}",
                CliException.FileDetails(path));
        }

        var text = new InputFile(path, bytes).TryGetText();
        if (text is null)
        {
            throw new CliException(
                ExitCodes.Configuration,
                ConfigParseResult.Invalid,
                $"{path} is not valid UTF-8.",
                CliException.FileDetails(path),
                [InputFiles.Issue("json.invalid", string.Empty, $"{FileName} is not valid UTF-8.")]);
        }

        RegisterSourceSecrets(host, text);
        var result = ConfigParser.Parse(text);
        if (result.Config is not null)
        {
            return (path, result.Config);
        }

        // The issues name JSON Pointers only; a secret value never reaches the message (D13).
        var message = result.ErrorCode == ConfigParseResult.SecretInFile
            ? $"{path} contains secrets at {string.Join(", ", result.Issues.Select(i => i.InstancePath))}. "
                + "Remove them and name the token's environment variable in token_env."
            : $"{path} is not a valid WorkLens configuration.";
        throw new CliException(
            ExitCodes.Configuration,
            result.ErrorCode ?? ConfigParseResult.Invalid,
            message,
            CliException.FileDetails(path),
            result.Issues);
    }

    private static void RegisterSourceSecrets(CliHost host, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("sources", out var sources)
                || sources.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            // Mask custom token values before schema, semantic, or source-selection errors can echo them.
            foreach (var source in sources.EnumerateArray())
            {
                if (source.ValueKind == JsonValueKind.Object
                    && source.TryGetProperty("token_env", out var variable)
                    && variable.ValueKind == JsonValueKind.String
                    && variable.GetString() is { Length: > 0 } name)
                {
                    host.Redactor.AddSecret(host.GetEnvironmentVariable(name));
                }
            }
        }
        catch (JsonException)
        {
            // ConfigParser reports malformed JSON through the configuration error path.
        }
    }

    private static string Find(CliHost host, string? explicitPath)
    {
        if (explicitPath is not null)
        {
            return host.FullPath(explicitPath);
        }

        if (host.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } fromEnvironment)
        {
            return host.FullPath(fromEnvironment);
        }

        string[] candidates =
        [
            Path.Combine(host.CurrentDirectory, FileName),
            Path.Combine(host.HomeDirectory, ".worklens", FileName),
        ];
        return candidates.FirstOrDefault(File.Exists)
            ?? throw NotFound(
                $"No configuration file: pass --config, set {EnvironmentVariable}, or create {string.Join(" or ", candidates)}.",
                candidates[0]);
    }

    private static CliException NotFound(string message, string path) =>
        new(ExitCodes.Configuration, "config.not_found", message, CliException.FileDetails(path));
}
