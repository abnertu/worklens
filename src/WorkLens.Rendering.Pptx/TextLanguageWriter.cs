using System.Globalization;
using System.Text;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace WorkLens.Rendering.Pptx;

/// <summary>Preserves the template's Chinese and English editing languages without setting font names.</summary>
internal static class TextLanguageWriter
{
    public static void Apply(DocumentFormat.OpenXml.OpenXmlElement element, P.SlideMaster? master, bool replaceLanguages = false)
    {
        // PowerPoint does not infer a generated run's editing language from its characters or the placeholder.
        var pair = master?.Descendants()
            .Where(p => p is A.RunProperties or A.DefaultRunProperties or A.EndParagraphRunProperties)
            .Select(p => Pair(p.GetAttributes().FirstOrDefault(a => a.LocalName == "lang").Value,
                p.GetAttributes().FirstOrDefault(a => a.LocalName == "altLang").Value))
            .FirstOrDefault(p => p is not null);
        if (pair is not { } languages)
        {
            return;
        }

        foreach (var run in element.Descendants<A.Run>().ToList())
        {
            // Replaced master text can use new boundaries only after the template supplies a valid pair.
            // Other copied content keeps its explicit languages.
            if ((!replaceLanguages && run.RunProperties?.Language is not null) || run.Text?.Text is not { Length: > 0 } text)
            {
                continue;
            }

            var start = 0;
            var offset = 0;
            string? current = null;
            foreach (var rune in text.EnumerateRunes())
            {
                var language = LanguageOf(rune, languages.Chinese, languages.English);
                if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                {
                    language = current;
                }

                if (offset > start && language != current)
                {
                    Insert(run, text[start..offset], current, languages);
                    start = offset;
                }

                current = language;
                offset += rune.Utf16SequenceLength;
            }

            Insert(run, text[start..], current, languages);
            run.Remove();
        }
    }

    private static (string Chinese, string English)? Pair(string? primary, string? alternate)
    {
        static bool Is(string? value, string prefix) => value is not null
            && (value.Equals(prefix, StringComparison.OrdinalIgnoreCase) || value.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase));

        if (Is(primary, "zh") && Is(alternate, "en"))
        {
            return (primary!, alternate!);
        }

        return Is(primary, "en") && Is(alternate, "zh") ? (alternate!, primary!) : null;
    }

    private static string? LanguageOf(Rune rune, string chinese, string english) => rune.Value switch
    {
        // Han extensions include supplementary-plane characters; iterate runes to keep surrogate pairs intact.
        >= 0x2E80 and <= 0x303F or >= 0x3100 and <= 0x312F or >= 0x31A0 and <= 0x31BF
            or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF
            or >= 0xFF01 and <= 0xFF60 or >= 0x20000 and <= 0x323AF => chinese,
        <= 0x7F or >= 0x00C0 and <= 0x024F or >= 0x1E00 and <= 0x1EFF => english,
        _ => null,
    };

    private static void Insert(A.Run original, string text, string? language, (string Chinese, string English) languages)
    {
        var copy = (A.Run)original.CloneNode(true);
        copy.Text!.Text = text;
        if (language is not null)
        {
            copy.RunProperties ??= new A.RunProperties();
            copy.RunProperties.Language = language;
            copy.RunProperties.AlternativeLanguage = language == languages.Chinese ? languages.English : languages.Chinese;
        }

        original.InsertBeforeSelf(copy);
    }
}
