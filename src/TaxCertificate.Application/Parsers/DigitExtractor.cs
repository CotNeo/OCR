using System.Globalization;
using System.Text;

namespace TaxCertificate.Application.Parsers;

/// <summary>Outcome of pulling a digit string out of an OCR block.</summary>
/// <param name="Digits">Digits only, separators removed. Empty when nothing usable was found.</param>
/// <param name="SubstitutionApplied">True when a letter was rewritten as a digit (O->0, l->1, ...).</param>
public readonly record struct DigitExtraction(string Digits, bool SubstitutionApplied)
{
    public bool HasValue => Digits.Length > 0;

    public static DigitExtraction None { get; } = new(string.Empty, false);
}

/// <summary>
/// Turns OCR text into digit strings.
/// <para>
/// Separator stripping ("123 456 7890" -> "1234567890") is always safe and always applied.
/// Letter-to-digit substitution is <b>not</b> safe: it can silently invent a different tax number,
/// so it is opt-in, applied only as a second pass, and always surfaces as a warning plus a
/// confidence penalty at the call site.
/// </para>
/// </summary>
public static class DigitExtractor
{
    /// <summary>Characters OCR commonly emits in place of digits, restricted to unambiguous shapes.</summary>
    private static readonly Dictionary<char, char> LetterToDigit = new()
    {
        ['O'] = '0', ['o'] = '0', ['Ö'] = '0', ['ö'] = '0', ['D'] = '0', ['Q'] = '0',
        ['I'] = '1', ['l'] = '1', ['İ'] = '1', ['ı'] = '1', ['|'] = '1', ['!'] = '1',
        ['Z'] = '2', ['z'] = '2',
        ['S'] = '5', ['s'] = '5', ['Ş'] = '5', ['ş'] = '5',
        ['G'] = '6', ['b'] = '6',
        ['T'] = '7',
        ['B'] = '8',
        ['g'] = '9', ['q'] = '9',
    };

    /// <summary>Separators that may appear inside a grouped number and carry no meaning.</summary>
    private const string Separators = " \t.-/\\_:,' ";

    /// <summary>
    /// Extracts a digit run of exactly <paramref name="expectedLength"/> digits.
    /// Tries a separator-only clean first; only if that fails and
    /// <paramref name="allowSubstitution"/> is set does it retry with letter substitution.
    /// </summary>
    public static DigitExtraction ExtractFixedLength(string? text, int expectedLength, bool allowSubstitution)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DigitExtraction.None;
        }

        var safe = StripSeparators(text, substitute: false);
        var match = FindRun(safe, expectedLength);
        if (match is not null)
        {
            return new DigitExtraction(match, SubstitutionApplied: false);
        }

        if (!allowSubstitution)
        {
            return DigitExtraction.None;
        }

        var substituted = StripSeparators(text, substitute: true);
        match = FindRun(substituted, expectedLength);
        return match is null
            ? DigitExtraction.None
            : new DigitExtraction(match, SubstitutionApplied: true);
    }

    /// <summary>All digits in the text with separators removed; no substitution.</summary>
    public static string DigitsOnly(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsAsciiDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Replaces separators with a boundary marker and optionally maps look-alike letters to digits.
    /// The marker keeps distinct numbers from merging into one long run.
    /// </summary>
    private static string StripSeparators(string text, bool substitute)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsAsciiDigit(ch))
            {
                builder.Append(ch);
            }
            else if (Separators.Contains(ch, StringComparison.Ordinal))
            {
                // Dropped entirely: grouped numbers such as "123 456 7890" must join up.
                continue;
            }
            else if (substitute && LetterToDigit.TryGetValue(ch, out var digit))
            {
                builder.Append(digit);
            }
            else
            {
                builder.Append('\u0001'); // hard boundary
            }
        }

        return builder.ToString();
    }

    /// <summary>Returns the single run of exactly the requested length, or null if absent/ambiguous.</summary>
    private static string? FindRun(string cleaned, int expectedLength)
    {
        string? found = null;
        var index = 0;

        while (index < cleaned.Length)
        {
            if (!char.IsAsciiDigit(cleaned[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < cleaned.Length && char.IsAsciiDigit(cleaned[index]))
            {
                index++;
            }

            var length = index - start;
            if (length != expectedLength)
            {
                continue;
            }

            if (found is not null)
            {
                // Two runs of the same length in one block: too ambiguous to pick blindly.
                return null;
            }

            found = cleaned.Substring(start, expectedLength);
        }

        return found;
    }
}

/// <summary>Date and NACE-code normalisation for vergi levhası values.</summary>
public static class ValueFormats
{
    private static readonly string[] DateFormats =
    [
        "d.M.yyyy", "dd.MM.yyyy", "d/M/yyyy", "dd/MM/yyyy",
        "d-M-yyyy", "dd-MM-yyyy", "d.M.yy", "dd.MM.yy",
    ];

    /// <summary>
    /// Parses a Turkish-style date into ISO <c>yyyy-MM-dd</c>. Returns null rather than guessing;
    /// the caller then emits DATE_PARSE_FAILED.
    /// </summary>
    public static string? TryNormalizeDate(string? text, DateOnly? today = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = text.Trim();
        var upperBound = today ?? DateOnly.FromDateTime(DateTime.UtcNow);

        if (DateTime.TryParseExact(cleaned, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed) &&
            IsPlausible(parsed, upperBound))
        {
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // Separator-free form (ddMMyyyy) only: any other digit count is too risky to interpret.
        var digits = DigitExtractor.DigitsOnly(cleaned);
        if (digits.Length == 8 &&
            DateTime.TryParseExact(digits, "ddMMyyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out parsed) &&
            IsPlausible(parsed, upperBound))
        {
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static bool IsPlausible(DateTime value, DateOnly upperBound)
        => value.Year >= 1900 && DateOnly.FromDateTime(value) <= upperBound.AddYears(1);

    /// <summary>
    /// Splits a combined "code - description" cell.
    /// <para>
    /// The GİB e-levha prints both in one field under a single "ANA FAALİYET KODU VE ADI"
    /// label, e.g. "479114-RADYO, TV, POSTA YOLUYLA ... PERAKENDE TİCARET". Returns nulls when
    /// the text is not in that shape, so a plain code still goes through the normal path.
    /// </para>
    /// </summary>
    public static (string? Code, string? Description) SplitActivityCell(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var trimmed = text.Trim();
        var separator = trimmed.IndexOfAny(['-', '\u2013', '\u2014']);
        if (separator <= 0 || separator >= trimmed.Length - 1)
        {
            return (null, null);
        }

        var head = trimmed[..separator].Trim();
        var tail = trimmed[(separator + 1)..].Trim();

        // The head has to be the code itself: digits only, 4-6 of them.
        var code = TryNormalizeActivityCode(head);
        if (code is null || DigitExtractor.DigitsOnly(head).Length != head.Replace(" ", string.Empty).Length)
        {
            return (null, null);
        }

        return (code, tail.Length >= 3 ? tail : null);
    }

    /// <summary>
    /// Normalises a NACE / ana faaliyet code to digits only ("49.41.03" -> "494103").
    /// Accepts 4 to 6 digits; anything else returns null.
    /// </summary>
    public static string? TryNormalizeActivityCode(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var digits = DigitExtractor.DigitsOnly(text);
        return digits.Length is >= 4 and <= 6 ? digits : null;
    }
}
