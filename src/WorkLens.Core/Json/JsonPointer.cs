using System.Text;

namespace WorkLens.Core.Json;

/// <summary>JSON Pointer (RFC 6901) helpers.</summary>
public static class JsonPointer
{
    /// <summary>Converts a System.Text.Json path (<c>$.a[0]['b.c']</c>) to a JSON Pointer (<c>/a/0/b.c</c>).</summary>
    public static string FromJsonPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "$")
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        var i = path[0] == '$' ? 1 : 0;
        while (i < path.Length)
        {
            string segment;
            if (path[i] == '.')
            {
                var end = path.IndexOfAny(['.', '['], i + 1);
                end = end < 0 ? path.Length : end;
                segment = path[(i + 1)..end];
                i = end;
            }
            else if (path[i] == '[' && i + 1 < path.Length && path[i + 1] == '\'')
            {
                var end = path.IndexOf("']", i + 2, StringComparison.Ordinal);
                end = end < 0 ? path.Length : end;
                segment = path[(i + 2)..end];
                i = Math.Min(end + 2, path.Length);
            }
            else if (path[i] == '[')
            {
                var end = path.IndexOf(']', i + 1);
                end = end < 0 ? path.Length : end;
                segment = path[(i + 1)..end];
                i = Math.Min(end + 1, path.Length);
            }
            else
            {
                break;
            }

            builder.Append('/').Append(Escape(segment));
        }

        return builder.ToString();
    }

    /// <summary>Escapes one reference token (<c>~</c> → <c>~0</c>, <c>/</c> → <c>~1</c>).</summary>
    public static string Escape(string token) => token.Replace("~", "~0").Replace("/", "~1");
}
