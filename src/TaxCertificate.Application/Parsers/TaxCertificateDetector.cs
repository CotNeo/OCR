using TaxCertificate.Application.Models;
using TaxCertificate.Application.Text;

namespace TaxCertificate.Application.Parsers;

public sealed record DocumentDetectionResult(double Score, IReadOnlyList<string> MatchedAnchors)
{
    public bool IsTaxCertificate(double threshold) => Score >= threshold;
}

/// <summary>
/// Decides whether the uploaded page is actually a vergi levhası, before any field is trusted.
/// Anchor weights are additive and the total is clamped to 1, so a couple of strong anchors
/// ("VERGİ LEVHASI" plus "VERGİ KİMLİK NUMARASI") are enough, while a random invoice that merely
/// mentions "VERGİ DAİRESİ" stays below the threshold.
/// </summary>
public static class TaxCertificateDetector
{
    private static readonly (string Anchor, double Weight)[] Anchors =
    [
        ("VERGI LEVHASI", 0.45),
        ("VERGI KIMLIK NUMARASI", 0.25),
        ("HAZINE VE MALIYE BAKANLIGI", 0.20),
        ("GELIR IDARESI BASKANLIGI", 0.15),
        ("VERGI DAIRESI", 0.15),
        ("ISE BASLAMA TARIHI", 0.12),
        ("ANA FAALIYET KODU", 0.12),
        ("MALIYE BAKANLIGI", 0.10),
        ("TICARET UNVANI", 0.08),
    ];

    public static DocumentDetectionResult Detect(OcrDocument document)
    {
        // One haystack per page keeps anchors that OCR split across two blocks findable,
        // while still refusing to join text from different pages.
        var haystacks = document.Pages
            .Select(page => string.Join(" ", page.Blocks.Select(b => TurkishTextNormalizer.Normalize(b.Text))))
            .ToList();

        double score = 0;
        var matched = new List<string>();

        foreach (var (anchor, weight) in Anchors)
        {
            if (haystacks.Any(h => h.Contains(anchor, StringComparison.Ordinal)))
            {
                score += weight;
                matched.Add(anchor);
            }
        }

        return new DocumentDetectionResult(Math.Clamp(score, 0, 1), matched);
    }
}
