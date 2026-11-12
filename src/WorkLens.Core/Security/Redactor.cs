using System.Text.RegularExpressions;

namespace WorkLens.Core.Security;

/// <summary>
/// Masks secrets in text before it is written anywhere (docs/security/security-privacy.md section 3): known secret
/// values registered at run time, token shapes (<see cref="TokenPatterns"/>), credential headers and credential
/// URL parameters. One instance is shared by everything a command writes.
/// </summary>
public sealed partial class Redactor
{
    public const string Mask = "***";

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "PRIVATE-TOKEN", "Cookie",
    };

    private readonly Lock _gate = new();
    private string[] _secrets = [];

    [GeneratedRegex(@"(?<name>\b(?:Authorization|PRIVATE-TOKEN|Cookie)[ \t]*:[ \t]*)[^\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderLine();

    [GeneratedRegex(@"(?<name>[?&;](?:token|private_token|access_token)=)[^&#\s""']*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlParameter();

    /// <summary>Registers a secret value (for example a token read from the environment). Empty values are ignored.</summary>
    public void AddSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        lock (_gate)
        {
            if (Array.IndexOf(_secrets, value) < 0)
            {
                // Longest first, so a secret that contains another is masked whole.
                _secrets = [.. _secrets.Append(value).OrderByDescending(s => s.Length)];
            }
        }
    }

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = text;
        foreach (var secret in Volatile.Read(ref _secrets))
        {
            result = result.Replace(secret, Mask, StringComparison.Ordinal);
        }

        result = TokenPatterns.Any().Replace(result, Mask);
        result = HeaderLine().Replace(result, m => m.Groups["name"].Value + Mask);
        result = UrlParameter().Replace(result, m => m.Groups["name"].Value + Mask);
        return result;
    }

    /// <summary>The value to log for an HTTP header: credential headers are masked whole.</summary>
    public string RedactHeader(string name, string value) =>
        SensitiveHeaders.Contains(name) ? Mask : Redact(value);
}
