using TaxCertificate.Application.Configuration;
using TaxCertificate.Application.Dtos;
using TaxCertificate.Application.Models;
using TaxCertificate.Application.Parsers;
using TaxCertificate.UnitTests.Helpers;
using Xunit;

namespace TaxCertificate.UnitTests;

public class ParserTests
{
    private static TaxCertificateParser CreateParser(Action<ParserOptions>? configure = null)
    {
        var options = new ParserOptions();
        configure?.Invoke(options);
        return new TaxCertificateParser(options);
    }

    /// <summary>The layout observed from a real PP-OCRv5 run: label on one line, value under it.</summary>
    private static OcrDocument FullCertificate() => new OcrDocumentBuilder()
        .WithTaxCertificateHeader()
        .AddLine("VERGİ KİMLİK NUMARASI")
        .AddLine("4540536920", x: 150)
        .AddLine("TİCARET UNVANI")
        .AddLine("ÖRNEK LOJİSTİK SANAYİ VE TİCARET A.Ş.", x: 150)
        .AddLine("VERGİ DAİRESİ")
        .AddLine("ÜMRANİYE", x: 150)
        .AddLine("İŞE BAŞLAMA TARİHİ")
        .AddLine("01.03.2019", x: 150)
        .AddLine("ANA FAALİYET KODU")
        .AddLine("494103", x: 150)
        .AddLine("ANA FAALİYET")
        .AddLine("Karayolu ile yük taşımacılığı", x: 150)
        .AddLine("ADRES")
        .AddLine("SARAY MAH. DR. ADNAN BÜYÜKDENİZ CAD.", x: 150)
        .AddLine("NO: 4 KAT: 7", x: 150)
        .AddLine("ÜMRANİYE / İSTANBUL", x: 150)
        .Build();

    [Fact]
    public void Parses_every_field_from_a_full_certificate()
    {
        var result = CreateParser().Parse(FullCertificate());

        Assert.True(result.Success);
        Assert.Equal(DocumentTypes.VergiLevhasi, result.DocumentType);

        var data = result.Data!;
        Assert.Equal("4540536920", data.Vkn);
        Assert.Null(data.Tckn);
        Assert.Equal("ÖRNEK LOJİSTİK SANAYİ VE TİCARET A.Ş.", data.TicaretUnvani);
        Assert.Equal("ÜMRANİYE", data.VergiDairesi);
        Assert.Equal("2019-03-01", data.IseBaslamaTarihi);
        Assert.Equal("494103", data.AnaFaaliyetKodu);
        Assert.Equal("Karayolu ile yük taşımacılığı", data.AnaFaaliyetAciklamasi);

        Assert.True(result.Validation!.Vkn!.Valid);
        Assert.Null(result.Validation.Tckn);
    }

    [Fact]
    public void Joins_a_multiline_address_without_absorbing_other_fields()
    {
        var result = CreateParser().Parse(FullCertificate());

        Assert.Equal(
            "SARAY MAH. DR. ADNAN BÜYÜKDENİZ CAD. NO: 4 KAT: 7 ÜMRANİYE / İSTANBUL",
            result.Data!.Adres);
        Assert.DoesNotContain("4540536920", result.Data.Adres);
        Assert.DoesNotContain("494103", result.Data.Adres);
    }

    [Fact]
    public void Preserves_Turkish_characters_in_output_values()
    {
        var data = CreateParser().Parse(FullCertificate()).Data!;

        Assert.Contains("İ", data.TicaretUnvani);
        Assert.Contains("Ş", data.TicaretUnvani);
        Assert.Equal("ÜMRANİYE", data.VergiDairesi);
        Assert.Contains("ü", data.AnaFaaliyetAciklamasi);
    }

    [Theory]
    // Diacritics dropped by OCR, dotless/dotted I swaps, lowercase, and a trailing-l slip.
    [InlineData("VERGI KIMLIK NUMARASI")]
    [InlineData("Vergi Kimlik Numarası")]
    [InlineData("VERGİ KİMLİK NUMARASl")]
    [InlineData("VERGI KiMLiK NUMARASI")]
    [InlineData("VERGİ KİMLİK NO")]
    public void Matches_label_spelling_variants(string label)
    {
        var document = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine(label)
            .AddLine("4540536920", x: 150)
            .Build();

        Assert.Equal("4540536920", CreateParser().Parse(document).Data!.Vkn);
    }

    [Fact]
    public void Repairs_ocr_spacing_inside_a_number()
    {
        var document = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine("VERGİ KİMLİK NUMARASI")
            .AddLine("454 053 6920", x: 150)
            .Build();

        Assert.Equal("4540536920", CreateParser().Parse(document).Data!.Vkn);
    }

    [Fact]
    public void Reads_a_value_printed_to_the_right_of_its_label()
    {
        var document = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine("VERGİ KİMLİK NUMARASI")
            .AddRightOfPrevious("4540536920")
            .Build();

        Assert.Equal("4540536920", CreateParser().Parse(document).Data!.Vkn);
    }

    [Fact]
    public void Reads_a_value_printed_inline_with_its_label()
    {
        var document = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine("VERGİ KİMLİK NUMARASI: 4540536920")
            .AddLine("VERGİ DAİRESİ: ÜMRANİYE")
            .Build();

        var data = CreateParser().Parse(document).Data!;
        Assert.Equal("4540536920", data.Vkn);
        Assert.Equal("ÜMRANİYE", data.VergiDairesi);
    }

    [Fact]
    public void Reports_an_invalid_checksum_instead_of_dropping_the_value()
    {
        var document = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine("VERGİ KİMLİK NUMARASI")
            .AddLine("4540536921", x: 150)
            .Build();

        var result = CreateParser().Parse(document);

        Assert.Equal("4540536921", result.Data!.Vkn);
        Assert.False(result.Validation!.Vkn!.Valid);
        Assert.Contains(result.Warnings, w => w.Code == WarningCodes.InvalidVknChecksum);
    }

    [Fact]
    public void Warns_when_no_vkn_is_present()
    {
        var document = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine("VERGİ DAİRESİ")
            .AddLine("ÜMRANİYE", x: 150)
            .Build();

        var result = CreateParser().Parse(document);

        Assert.Null(result.Data!.Vkn);
        Assert.Contains(result.Warnings, w => w.Code == WarningCodes.VknNotFound);
        Assert.Contains(result.Warnings, w => w.Code == WarningCodes.NoIdentityNumberFound);
        Assert.Null(result.Validation!.Vkn);
    }

    [Fact]
    public void Picks_the_labelled_number_when_several_ten_digit_numbers_are_present()
    {
        var document = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine("MÜŞTERİ NO 9999999999")
            .AddLine("VERGİ KİMLİK NUMARASI")
            .AddLine("4540536920", x: 150)
            .AddLine("TELEFON 5321234567")
            .Build();

        Assert.Equal("4540536920", CreateParser().Parse(document).Data!.Vkn);
    }

    [Fact]
    public void Keeps_tckn_and_vkn_in_their_own_fields()
    {
        var document = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine("T.C. KİMLİK NUMARASI")
            .AddLine("17291716060", x: 150)
            .AddLine("ADI SOYADI")
            .AddLine("AYŞE YILMAZ", x: 150)
            .AddLine("VERGİ DAİRESİ")
            .AddLine("ÜMRANİYE", x: 150)
            .Build();

        var result = CreateParser().Parse(document);

        Assert.Equal("17291716060", result.Data!.Tckn);
        Assert.Null(result.Data.Vkn);
        Assert.Equal("AYŞE YILMAZ", result.Data.AdiSoyadi);
        Assert.True(result.Validation!.Tckn!.Valid);
    }

    [Theory]
    [InlineData("01.02.2020", "2020-02-01")]
    [InlineData("01/02/2020", "2020-02-01")]
    [InlineData("1.2.2020", "2020-02-01")]
    [InlineData("1-2-2020", "2020-02-01")]
    [InlineData("01022020", "2020-02-01")]
    public void Normalizes_date_formats_to_iso(string printed, string expected)
    {
        var document = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine("İŞE BAŞLAMA TARİHİ")
            .AddLine(printed, x: 150)
            .Build();

        Assert.Equal(expected, CreateParser().Parse(document).Data!.IseBaslamaTarihi);
    }

    [Fact]
    public void Returns_null_and_warns_when_a_date_cannot_be_parsed()
    {
        var document = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine("VERGİ KİMLİK NUMARASI")
            .AddLine("4540536920", x: 150)
            .AddLine("İŞE BAŞLAMA TARİHİ")
            .AddLine("--.--.----", x: 150)
            .Build();

        var result = CreateParser().Parse(document);

        Assert.Null(result.Data!.IseBaslamaTarihi);
        Assert.Contains(result.Warnings, w => w.Code == WarningCodes.DateParseFailed);
    }

    [Theory]
    [InlineData("494103", "494103")]
    [InlineData("49.41.03", "494103")]
    [InlineData("49 41 03", "494103")]
    public void Normalizes_nace_codes_while_keeping_the_raw_value(string printed, string expected)
    {
        var document = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine("ANA FAALİYET KODU")
            .AddLine(printed, x: 150)
            .Build();

        var data = CreateParser().Parse(document).Data!;
        Assert.Equal(expected, data.AnaFaaliyetKodu);
        Assert.Equal(printed, data.AnaFaaliyetKoduRaw);
    }

    [Fact]
    public void Rejects_a_document_that_is_not_a_tax_certificate()
    {
        var document = new OcrDocumentBuilder()
            .AddLine("FATURA")
            .AddLine("TOPLAM TUTAR 1.250,00 TL")
            .AddLine("TESLİM ADRESİ")
            .Build();

        var result = CreateParser().Parse(document);

        Assert.False(result.Success);
        Assert.Equal(DocumentTypes.Unknown, result.DocumentType);
        Assert.Equal(ErrorCodes.NotTaxCertificate, result.Error!.Code);
        Assert.Null(result.Data);
    }

    [Fact]
    public void Does_not_substitute_letters_for_digits_by_default()
    {
        var document = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine("VERGİ KİMLİK NUMARASI")
            .AddLine("454O5369ZO", x: 150)  // O and Z in place of 0 and 2
            .Build();

        var result = CreateParser().Parse(document);

        Assert.Null(result.Data!.Vkn);
        Assert.Contains(result.Warnings, w => w.Code == WarningCodes.VknNotFound);
    }

    [Fact]
    public void Substitutes_letters_for_digits_only_when_enabled_and_always_warns()
    {
        var document = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine("VERGİ KİMLİK NUMARASI")
            .AddLine("454O5369ZO", x: 150)
            .Build();

        var result = CreateParser(o => o.AllowDigitSubstitution = true).Parse(document);

        Assert.Equal("4540536920", result.Data!.Vkn);
        Assert.Contains(result.Warnings, w => w.Code == WarningCodes.DigitSubstitutionApplied);
        Assert.True(result.Confidence!.Vkn < 0.80, $"expected a penalty, got {result.Confidence.Vkn}");
    }

    [Fact]
    public void Falls_back_to_an_unlabelled_checksum_valid_vkn_and_warns()
    {
        var document = new OcrDocumentBuilder()
            .AddLine("VERGİ LEVHASI", x: 480)
            .AddLine("HAZİNE VE MALİYE BAKANLIĞI", x: 400)
            .AddLine("VERGİ DAİRESİ")
            .AddLine("ÜMRANİYE", x: 150)
            .AddLine("4540536920", x: 150)
            .Build();

        var result = CreateParser().Parse(document);

        Assert.Equal("4540536920", result.Data!.Vkn);
        Assert.Contains(result.Warnings, w => w.Code == WarningCodes.VknLabelNotFound);
        Assert.True(result.Confidence!.Vkn < 0.70, $"expected low confidence, got {result.Confidence.Vkn}");
    }

    [Fact]
    public void Overall_confidence_drops_when_most_fields_are_missing()
    {
        var sparse = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine("VERGİ KİMLİK NUMARASI")
            .AddLine("4540536920", x: 150)
            .Build();

        var full = CreateParser().Parse(FullCertificate()).Confidence!;
        var partial = CreateParser().Parse(sparse).Confidence!;

        Assert.True(partial.Overall < full.Overall,
            $"sparse={partial.Overall} should be below full={full.Overall}");
        Assert.True(full.Overall > 0.85, $"full document scored only {full.Overall}");
    }

    [Fact]
    public void Low_ocr_confidence_lowers_the_field_score_and_raises_a_warning()
    {
        var document = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine("VERGİ KİMLİK NUMARASI", confidence: 0.55)
            .AddLine("4540536920", confidence: 0.52, x: 150)
            .Build();

        var result = CreateParser().Parse(document);

        Assert.Equal("4540536920", result.Data!.Vkn);
        Assert.True(result.Confidence!.Vkn < 0.55);
        Assert.Contains(result.Warnings, w => w.Code == WarningCodes.LowConfidenceField && w.Field == "vkn");
    }

    [Fact]
    public void Strips_a_redundant_vergi_dairesi_suffix()
    {
        var document = new OcrDocumentBuilder()
            .WithTaxCertificateHeader()
            .AddLine("VERGİ DAİRESİ")
            .AddLine("ÜMRANİYE VERGİ DAİRESİ MÜDÜRLÜĞÜ", x: 150)
            .Build();

        Assert.Equal("ÜMRANİYE", CreateParser().Parse(document).Data!.VergiDairesi);
    }
}
