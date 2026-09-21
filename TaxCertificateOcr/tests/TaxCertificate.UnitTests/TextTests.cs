using TaxCertificate.Application.Parsers;
using TaxCertificate.Application.Text;
using Xunit;

namespace TaxCertificate.UnitTests;

public class TurkishTextNormalizerTests
{
    [Theory]
    [InlineData("VERGİ KİMLİK NUMARASI", "VERGI KIMLIK NUMARASI")]
    [InlineData("Vergi Kimlik Numarası", "VERGI KIMLIK NUMARASI")]
    [InlineData("VERGI KIMLIK NUMARASI", "VERGI KIMLIK NUMARASI")]
    [InlineData("Vergı Kımlık Numarası", "VERGI KIMLIK NUMARASI")]
    // Each dot is a separator, so "T.C." becomes two tokens. NormalizeCompact is what unifies
    // it with "TC" - see Compact_form_unifies_abbreviation_spellings below.
    [InlineData("T.C. KİMLİK NUMARASI", "T C KIMLIK NUMARASI")]
    [InlineData("  ŞİRKET   ÜNVANI  ", "SIRKET UNVANI")]
    public void Normalize_collapses_variants_onto_one_ascii_skeleton(string input, string expected)
        => Assert.Equal(expected, TurkishTextNormalizer.Normalize(input));

    [Theory]
    // Observed in a real PP-OCRv5 run: the dot on İ came back as an acute accent.
    [InlineData("VERGİ DAÍRESİ", "VERGI DAIRESI")]
    [InlineData("VERGÌ DAIRESÍ", "VERGI DAIRESI")]
    [InlineData("HAZÎNE VE MALÌYE BAKANLIĞI", "HAZINE VE MALIYE BAKANLIGI")]
    [InlineData("ÂDRES", "ADRES")]
    public void Normalize_strips_accents_that_ocr_substitutes_for_turkish_dots(string input, string expected)
        => Assert.Equal(expected, TurkishTextNormalizer.Normalize(input));

    [Fact]
    public void Compact_form_unifies_abbreviation_spellings()
    {
        // "T.C.", "TC" and "T C" all reach the same compact skeleton, which is why the label
        // catalogue falls back to the compact comparison for punctuated aliases.
        Assert.Equal("TCKIMLIKNUMARASI", TurkishTextNormalizer.NormalizeCompact("T.C. KİMLİK NUMARASI"));
        Assert.Equal("TCKIMLIKNUMARASI", TurkishTextNormalizer.NormalizeCompact("TC KİMLİK NUMARASI"));
        Assert.Equal("TCKIMLIKNUMARASI", TurkishTextNormalizer.NormalizeCompact("T C Kimlik Numarası"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("...")]
    public void Normalize_returns_empty_for_blank_or_punctuation_only(string? input)
        => Assert.Equal(string.Empty, TurkishTextNormalizer.Normalize(input));

    [Fact]
    public void CleanValue_keeps_turkish_characters_and_casing()
    {
        const string input = "  ÖRNEK   LOJİSTİK  SANAYİ VE TİCARET A.Ş.  ";
        Assert.Equal("ÖRNEK LOJİSTİK SANAYİ VE TİCARET A.Ş.", TurkishTextNormalizer.CleanValue(input));
    }

    [Theory]
    // A trailing period is part of a Turkish abbreviation and must survive.
    [InlineData("ÖRNEK A.Ş.", "ÖRNEK A.Ş.")]
    [InlineData("DR. ADNAN BÜYÜKDENİZ CAD.", "DR. ADNAN BÜYÜKDENİZ CAD.")]
    [InlineData("LTD. ŞTİ.", "LTD. ŞTİ.")]
    // A leading separator is leftover label punctuation and must go.
    [InlineData(": ÜMRANİYE", "ÜMRANİYE")]
    [InlineData("- 4540536920", "4540536920")]
    // A trailing colon/comma is punctuation, not content.
    [InlineData("ÜMRANİYE:", "ÜMRANİYE")]
    public void CleanValue_trims_separators_asymmetrically(string input, string expected)
        => Assert.Equal(expected, TurkishTextNormalizer.CleanValue(input));
}

public class LevenshteinTests
{
    [Theory]
    [InlineData("KITTEN", "SITTING", 3)]
    [InlineData("SAME", "SAME", 0)]
    [InlineData("", "ABC", 3)]
    [InlineData("ABC", "", 3)]
    public void Distance_matches_known_values(string a, string b, int expected)
        => Assert.Equal(expected, Levenshtein.Distance(a, b));

    [Fact]
    public void Distance_respects_the_budget_and_exits_early()
    {
        // Over budget must report budget+1 rather than the true distance.
        Assert.Equal(2, Levenshtein.Distance("ABCDEFGH", "ZZZZZZZZ", maxDistance: 1));
        Assert.Equal(0, Levenshtein.Distance("ABC", "ABC", maxDistance: 0));
    }

    [Fact]
    public void Similarity_is_one_for_identical_and_near_one_for_a_single_slip()
    {
        Assert.Equal(1.0, Levenshtein.Similarity("NUMARASI", "NUMARASI"));

        var similarity = Levenshtein.Similarity("VERGI KIMLIK NUMARASL", "VERGI KIMLIK NUMARASI");
        Assert.True(similarity > 0.95, $"expected > 0.95, got {similarity}");
    }
}

public class LabelCatalogTests
{
    [Theory]
    [InlineData("VERGİ KİMLİK NUMARASI", TaxCertificateField.Vkn)]
    [InlineData("VKN", TaxCertificateField.Vkn)]
    [InlineData("T.C. KİMLİK NUMARASI", TaxCertificateField.Tckn)]
    [InlineData("TİCARET UNVANI", TaxCertificateField.TicaretUnvani)]
    [InlineData("TİCARET ÜNVANI", TaxCertificateField.TicaretUnvani)]
    [InlineData("VERGİ DAİRESİ", TaxCertificateField.VergiDairesi)]
    [InlineData("İŞE BAŞLAMA TARİHİ", TaxCertificateField.IseBaslamaTarihi)]
    [InlineData("ADRES", TaxCertificateField.Adres)]
    [InlineData("ADI SOYADI", TaxCertificateField.AdiSoyadi)]
    public void Match_identifies_known_labels(string text, TaxCertificateField expected)
    {
        var match = LabelCatalog.Match(text);
        Assert.NotNull(match);
        Assert.Equal(expected, match!.Field);
    }

    [Fact]
    public void A_longer_alias_wins_over_a_shorter_prefix_of_it()
    {
        // "ANA FAALİYET KODU" must not be captured by the "ANA FAALİYET" description alias.
        Assert.Equal(TaxCertificateField.AnaFaaliyetKodu, LabelCatalog.Match("ANA FAALİYET KODU")!.Field);
        Assert.Equal(TaxCertificateField.AnaFaaliyetAciklamasi, LabelCatalog.Match("ANA FAALİYET")!.Field);
    }

    [Theory]
    [InlineData("TOPLAM TUTAR")]
    [InlineData("MÜŞTERİ NO")]
    [InlineData("4540536920")]
    [InlineData("SARAY MAH. DR. ADNAN BÜYÜKDENİZ CAD.")]
    public void Match_returns_null_for_non_labels(string text)
        => Assert.Null(LabelCatalog.Match(text));

    [Fact]
    public void Short_aliases_require_an_exact_match_to_avoid_false_positives()
    {
        // "ADRES" is 5 characters; a one-edit neighbour must not match.
        Assert.NotNull(LabelCatalog.Match("ADRES"));
        Assert.Null(LabelCatalog.Match("ADRAS"));
        Assert.Null(LabelCatalog.Match("VKM"));
    }

    [Fact]
    public void An_accent_substituted_for_a_turkish_dot_matches_exactly()
    {
        // From a real OCR run. This must be an exact match, not a fuzzy one, so the tolerance
        // budget stays available for genuine recognition slips.
        var match = LabelCatalog.Match("VERGİ DAÍRESİ");

        Assert.NotNull(match);
        Assert.Equal(TaxCertificateField.VergiDairesi, match!.Field);
        Assert.True(match.IsExact, "accent folding should yield an exact match");
        Assert.Equal(1.0, match.Similarity);
    }

    [Fact]
    public void Match_extracts_the_inline_remainder_with_turkish_characters_intact()
    {
        var match = LabelCatalog.Match("VERGİ DAİRESİ: ÜMRANİYE");
        Assert.NotNull(match);
        Assert.Equal(TaxCertificateField.VergiDairesi, match!.Field);
        Assert.Equal("ÜMRANİYE", match.InlineRemainder);
    }
}

public class DigitExtractorTests
{
    [Theory]
    [InlineData("4540536920", "4540536920")]
    [InlineData("454 053 6920", "4540536920")]
    [InlineData("454.053.6920", "4540536920")]
    [InlineData("VKN 4540536920", "4540536920")]
    public void ExtractFixedLength_removes_separators_without_substitution(string input, string expected)
    {
        var result = DigitExtractor.ExtractFixedLength(input, 10, allowSubstitution: false);
        Assert.Equal(expected, result.Digits);
        Assert.False(result.SubstitutionApplied);
    }

    [Theory]
    [InlineData("454O5369ZO")]
    [InlineData("4S40S36920")]
    public void ExtractFixedLength_refuses_letters_unless_substitution_is_enabled(string input)
    {
        Assert.False(DigitExtractor.ExtractFixedLength(input, 10, allowSubstitution: false).HasValue);

        var permissive = DigitExtractor.ExtractFixedLength(input, 10, allowSubstitution: true);
        Assert.True(permissive.HasValue);
        Assert.True(permissive.SubstitutionApplied);
    }

    [Fact]
    public void ExtractFixedLength_rejects_two_equally_plausible_runs()
    {
        // Ambiguous input must yield nothing rather than an arbitrary pick.
        Assert.False(DigitExtractor.ExtractFixedLength("4540536920 1234567890", 10, false).HasValue);
    }

    [Theory]
    [InlineData("454053692", 10)]
    [InlineData("45405369201", 10)]
    public void ExtractFixedLength_requires_the_exact_length(string input, int length)
        => Assert.False(DigitExtractor.ExtractFixedLength(input, length, false).HasValue);
}

public class ValueFormatsTests
{
    [Theory]
    [InlineData("01.03.2019", "2019-03-01")]
    [InlineData("1.3.2019", "2019-03-01")]
    [InlineData("01/03/2019", "2019-03-01")]
    [InlineData("01-03-2019", "2019-03-01")]
    [InlineData("01032019", "2019-03-01")]
    public void TryNormalizeDate_produces_iso_dates(string input, string expected)
        => Assert.Equal(expected, ValueFormats.TryNormalizeDate(input));

    [Theory]
    [InlineData("32.13.2019")]   // impossible calendar date
    [InlineData("--.--.----")]
    [InlineData("2019")]
    [InlineData("")]
    [InlineData(null)]
    public void TryNormalizeDate_returns_null_rather_than_guessing(string? input)
        => Assert.Null(ValueFormats.TryNormalizeDate(input));

    [Fact]
    public void TryNormalizeDate_rejects_implausible_years()
    {
        Assert.Null(ValueFormats.TryNormalizeDate("01.01.1750"));
        // More than a year into the future is not a plausible start date.
        var farFuture = DateTime.UtcNow.AddYears(5);
        Assert.Null(ValueFormats.TryNormalizeDate($"01.01.{farFuture.Year}"));
    }

    [Theory]
    [InlineData("494103", "494103")]
    [InlineData("49.41.03", "494103")]
    [InlineData("49 41 03", "494103")]
    [InlineData("4941", "4941")]
    public void TryNormalizeActivityCode_strips_separators(string input, string expected)
        => Assert.Equal(expected, ValueFormats.TryNormalizeActivityCode(input));

    [Theory]
    [InlineData("49")]
    [InlineData("4941035")]
    [InlineData("")]
    public void TryNormalizeActivityCode_rejects_out_of_range_lengths(string input)
        => Assert.Null(ValueFormats.TryNormalizeActivityCode(input));
}
