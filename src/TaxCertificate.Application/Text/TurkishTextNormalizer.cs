using System.Globalization;
using System.Text;

namespace TaxCertificate.Application.Text;

/// <summary>
/// Produces a comparison-only representation of Turkish text.
/// <para>
/// This is used exclusively for <b>label matching</b>. Extracted output values are never
/// passed through here, so Turkish characters survive untouched in the API response.
/// </para>
/// <para>
/// Folding is necessary because PP-OCR frequently loses diacritics on capitals
/// (observed: "VERGİ LEVHASI" recognised as "VERGI LEVHASI", "İSTANBUL" as "iSTANBUL").
/// Folding to ASCII makes those variants compare equal instead of relying on fuzzy distance.
/// </para>
/// </summary>
public static class TurkishTextNormalizer
{
    /// <summary>
    /// Maps Turkish (and a few commonly confused Latin-1) letters onto an ASCII skeleton.
    /// Both cases collapse onto the same uppercase letter, so dotted/dotless I variants unify.
    /// </summary>
    private static readonly Dictionary<char, char> FoldMap = new()
    {
        ['ç'] = 'C', ['Ç'] = 'C',
        ['ğ'] = 'G', ['Ğ'] = 'G',
        // Dotted and dotless I both collapse to plain I. 'ı' (U+0131) has no Unicode
        // decomposition, so it must be mapped explicitly rather than left to FormD.
        ['ı'] = 'I', ['İ'] = 'I', ['i'] = 'I', ['I'] = 'I',
        ['ö'] = 'O', ['Ö'] = 'O',
        ['ş'] = 'S', ['Ş'] = 'S',
        ['ü'] = 'U', ['Ü'] = 'U',
    };

    /// <summary>
    /// Folds to ASCII uppercase, drops punctuation and collapses runs of whitespace.
    /// "T.C. Kimlik Numarası" and "TC KIMLIK NUMARASI" both become "TC KIMLIK NUMARASI".
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = true;

        foreach (var ch in text)
        {
            var folded = Fold(ch);

            if (char.IsLetterOrDigit(folded))
            {
                builder.Append(folded);
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                // Punctuation and whitespace both act as a single separator.
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Normalized form with all separators removed. Used as a tolerant containment probe,
    /// because OCR sometimes splits or merges the spaces inside a label.
    /// </summary>
    public static string NormalizeCompact(string? text)
        => Normalize(text).Replace(" ", string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Folds one character onto its ASCII skeleton.
    /// <para>
    /// Beyond the Turkish-specific map, any remaining accented Latin letter is stripped of its
    /// diacritic via Unicode canonical decomposition. This is not hypothetical: a real PP-OCRv5
    /// run read "VERGİ DAİRESİ" as "VERGİ DAÍRESİ", substituting an acute accent for the dot.
    /// Decomposing generically turns that into an exact label match instead of leaning on the
    /// fuzzy-distance budget.
    /// </para>
    /// </summary>
    private static char Fold(char ch)
    {
        if (FoldMap.TryGetValue(ch, out var mapped))
        {
            return mapped;
        }

        if (!char.IsLetter(ch))
        {
            return ch;
        }

        if (char.IsAscii(ch))
        {
            return char.ToUpperInvariant(ch);
        }

        // Decompose (e.g. 'Í' -> 'I' + combining acute) and keep only the base letter.
        var decomposed = ch.ToString().Normalize(NormalizationForm.FormD);
        foreach (var part in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(part) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            // Re-enter the map: 'î' decomposes to 'i', which must still fold to 'I'.
            return FoldMap.TryGetValue(part, out var remapped)
                ? remapped
                : char.ToUpperInvariant(part);
        }

        return char.ToUpperInvariant(ch);
    }

    /// <summary>
    /// Collapses whitespace while preserving the original characters and casing.
    /// Used when emitting values (e.g. ticaret unvanı) so Turkish characters are kept intact.
    /// </summary>
    public static string CleanValue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = true;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                builder.Append(ch);
                lastWasSpace = false;
            }
        }

        // Asymmetric trim: a leading separator is label punctuation left over from an inline
        // value (": 4540536920"), but a *trailing* period belongs to the value itself -
        // stripping it would turn "A.Ş." into "A.Ş" and "CAD." into "CAD".
        return builder.ToString()
            .Trim()
            .TrimStart(':', '-', '.', ',', ';', '=', '\u2013')
            .TrimEnd(':', ',', ';', '-', '=', '\u2013')
            .Trim();
    }
}
