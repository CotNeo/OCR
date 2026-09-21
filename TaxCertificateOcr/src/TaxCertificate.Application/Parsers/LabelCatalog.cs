using TaxCertificate.Application.Text;

namespace TaxCertificate.Application.Parsers;

public sealed record LabelDefinition(TaxCertificateField Field, string Alias)
{
    /// <summary>Alias in folded/ASCII form; all comparisons happen against this.</summary>
    public string NormalizedAlias { get; } = TurkishTextNormalizer.Normalize(Alias);

    public string CompactAlias { get; } = TurkishTextNormalizer.NormalizeCompact(Alias);
}

public sealed record LabelMatch(
    LabelDefinition Definition,
    double Similarity,
    bool IsExact,
    /// <summary>Remaining text on the same block after the label, e.g. "VKN: 4540536920" -> "4540536920".</summary>
    string? InlineRemainder)
{
    public TaxCertificateField Field => Definition.Field;
}

/// <summary>
/// Known vergi levhası labels and their spelling variants across template years.
/// Aliases are stored in source form for readability and folded once at construction.
/// </summary>
public static class LabelCatalog
{
    private static readonly LabelDefinition[] Definitions = Build(new Dictionary<TaxCertificateField, string[]>
    {
        [TaxCertificateField.Vkn] =
        [
            "VERGİ KİMLİK NUMARASI", "VERGİ KİMLİK NO", "VERGİ KİMLİK NUMARASI (VKN)",
            "VERGİ NUMARASI", "VERGİ NO", "VKN",
        ],
        [TaxCertificateField.Tckn] =
        [
            "T.C. KİMLİK NUMARASI", "TC KİMLİK NUMARASI", "T.C. KİMLİK NO", "TC KİMLİK NO",
            "TÜRKİYE CUMHURİYETİ KİMLİK NUMARASI", "TCKN", "T.C.K.N.",
        ],
        [TaxCertificateField.TicaretUnvani] =
        [
            "TİCARET UNVANI", "TİCARET ÜNVANI", "TİCARET SİCİL UNVANI",
            "TİCARİ UNVANI", "TİCARET UNVANI / ADI SOYADI", "UNVANI", "ÜNVANI", "UNVAN",
        ],
        [TaxCertificateField.AdiSoyadi] =
        [
            "ADI SOYADI", "ADI VE SOYADI", "AD SOYAD", "ADI-SOYADI", "MÜKELLEFİN ADI SOYADI",
        ],
        [TaxCertificateField.VergiDairesi] =
        [
            "VERGİ DAİRESİ", "VERGİ DAİRESİ ADI", "VERGİ DAİRESİ MÜDÜRLÜĞÜ", "BAĞLI OLDUĞU VERGİ DAİRESİ",
        ],
        [TaxCertificateField.IseBaslamaTarihi] =
        [
            "İŞE BAŞLAMA TARİHİ", "İŞE BAŞLAMA", "FAALİYETE BAŞLAMA TARİHİ", "İŞE BAŞLAMA TARİHİ :",
        ],
        [TaxCertificateField.AnaFaaliyetKodu] =
        [
            "ANA FAALİYET KODU", "FAALİYET KODU", "NACE KODU", "ANA FAALİYET KOD",
        ],
        [TaxCertificateField.AnaFaaliyetAciklamasi] =
        [
            "ANA FAALİYET AÇIKLAMASI", "ANA FAALİYET KONUSU", "FAALİYET KONUSU",
            "ANA FAALİYET", "FAALİYET AÇIKLAMASI",
        ],
        [TaxCertificateField.Adres] =
        [
            "İŞ YERİ ADRESİ", "İŞYERİ ADRESİ", "MERKEZ ADRESİ", "ADRESİ", "ADRES",
        ],
    });

    private static LabelDefinition[] Build(Dictionary<TaxCertificateField, string[]> source)
        => source.SelectMany(kv => kv.Value.Select(alias => new LabelDefinition(kv.Key, alias)))
                 // Longest alias first so "ANA FAALİYET KODU" wins over "ANA FAALİYET".
                 .OrderByDescending(d => d.NormalizedAlias.Length)
                 .ToArray();

    /// <summary>
    /// Fuzzy tolerance scaled by alias length. Short aliases such as "VKN" or "ADRES" must match
    /// exactly, because a single edit on a 3-5 character token is far too easy to hit by accident.
    /// </summary>
    private static double ThresholdFor(string normalizedAlias) => normalizedAlias.Length switch
    {
        <= 6 => 1.00,
        <= 10 => 0.90,
        <= 16 => 0.86,
        _ => 0.82,
    };

    /// <summary>
    /// Finds the best label that this OCR block represents, or <c>null</c> when the block is not a label.
    /// </summary>
    public static LabelMatch? Match(string blockText)
    {
        var normalized = TurkishTextNormalizer.Normalize(blockText);
        if (normalized.Length == 0)
        {
            return null;
        }

        var compact = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
        LabelMatch? best = null;

        foreach (var definition in Definitions)
        {
            var match = Evaluate(definition, blockText, normalized, compact);
            if (match is null)
            {
                continue;
            }

            // Definitions are pre-sorted longest-first, so a strictly greater similarity is
            // required to displace an already-found longer alias.
            if (best is null || match.Similarity > best.Similarity + 1e-9)
            {
                best = match;
            }
        }

        return best;
    }

    private static LabelMatch? Evaluate(
        LabelDefinition definition,
        string originalText,
        string normalized,
        string compact)
    {
        var alias = definition.NormalizedAlias;

        // 1. Whole block is the label.
        if (normalized.Equals(alias, StringComparison.Ordinal))
        {
            return new LabelMatch(definition, 1.0, IsExact: true, InlineRemainder: null);
        }

        // 2. Block starts with the label and carries the value inline ("VERGİ KİMLİK NO: 4540536920").
        if (normalized.StartsWith(alias + " ", StringComparison.Ordinal))
        {
            var remainder = ExtractInlineRemainder(originalText, alias);
            return new LabelMatch(definition, 1.0, IsExact: true, InlineRemainder: remainder);
        }

        // 3. Separators inside the label got lost or doubled by OCR.
        if (compact.Equals(definition.CompactAlias, StringComparison.Ordinal))
        {
            return new LabelMatch(definition, 0.99, IsExact: true, InlineRemainder: null);
        }

        if (compact.StartsWith(definition.CompactAlias, StringComparison.Ordinal))
        {
            var remainder = ExtractInlineRemainder(originalText, alias);
            return new LabelMatch(definition, 0.98, IsExact: true, InlineRemainder: remainder);
        }

        // 4. Fuzzy: absorbs a slip or two ("NUMARASI" read as "NUMARASl").
        var threshold = ThresholdFor(alias);
        if (threshold >= 1.0)
        {
            return null;
        }

        // Only compare against a same-length window; otherwise a long address line would
        // dilute the distance and score as a short label.
        if (Math.Abs(normalized.Length - alias.Length) > Math.Max(2, alias.Length * 0.25))
        {
            return null;
        }

        var maxDistance = (int)Math.Ceiling(alias.Length * (1 - threshold));
        var distance = Levenshtein.Distance(normalized, alias, maxDistance);
        if (distance > maxDistance)
        {
            return null;
        }

        var similarity = 1.0 - (double)distance / Math.Max(normalized.Length, alias.Length);
        return similarity < threshold
            ? null
            : new LabelMatch(definition, similarity, IsExact: false, InlineRemainder: null);
    }

    /// <summary>
    /// Returns the part of the original (unfolded) text that follows the label prefix, so the
    /// value keeps its Turkish characters. Counts letters/digits to stay aligned with folding.
    /// </summary>
    private static string? ExtractInlineRemainder(string originalText, string normalizedAlias)
    {
        var aliasLetterCount = normalizedAlias.Count(char.IsLetterOrDigit);
        if (aliasLetterCount == 0)
        {
            return null;
        }

        var seen = 0;
        for (var i = 0; i < originalText.Length; i++)
        {
            if (!char.IsLetterOrDigit(originalText[i]))
            {
                continue;
            }

            seen++;
            if (seen == aliasLetterCount)
            {
                var remainder = originalText[(i + 1)..];
                var cleaned = TurkishTextNormalizer.CleanValue(remainder);
                return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
            }
        }

        return null;
    }
}
