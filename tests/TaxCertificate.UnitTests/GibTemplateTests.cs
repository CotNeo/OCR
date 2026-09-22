using TaxCertificate.Application.Configuration;
using TaxCertificate.Application.Dtos;
using TaxCertificate.Application.Models;
using TaxCertificate.Application.Parsers;
using TaxCertificate.UnitTests.Helpers;
using Xunit;

namespace TaxCertificate.UnitTests;

/// <summary>
/// Regression tests for the GİB e-levha template, driven by block coordinates captured from a
/// real PP-OCRv5 run. Its two-column layout breaks several assumptions that a single-column
/// levha never exercises: labels wrap onto two lines, the VKN is printed as widely spaced
/// digits under a barcode, and code and description share one cell.
/// </summary>
public class GibTemplateTests
{
    private static AnalysisResult Parse(OcrDocument document)
        => new TaxCertificateParser(new ParserOptions()).Parse(document);

    /// <summary>
    /// Real geometry from the GİB template. The two columns interleave once blocks are sorted
    /// top-to-bottom, so "VERGİ" and "DAİRESİ" end up three entries apart.
    /// </summary>
    private static OcrDocument GibLevha() => new OcrDocumentBuilder()
        .Add("VERGI LEVHASI", 659, 49, 938, 88)
        .Add("Gelir idaresi", 1314, 49, 1464, 79)
        .Add("Başkanlığı", 1313, 73, 1443, 108)
        .Add("MÜKELLEFİN", 62, 104, 188, 134)
        .Add("VERGi", 933, 153, 1004, 186)
        .Add("BEŞİKTAŞ", 1123, 153, 1221, 192)
        .Add("MEHMET HİLMİ ÇİL", 300, 160, 472, 186)
        .Add("ADI SOYADI", 62, 167, 171, 191)
        .Add("DAİRESi", 934, 176, 1017, 209)
        .Add("III", 1149, 223, 1196, 262, 0.66)          // the barcode itself
        .Add("VERGİ KİMLİK", 935, 225, 1066, 253)
        .Add("TICARET ÜNVANI", 61, 233, 218, 262)
        .Add("NO", 933, 248, 975, 280)
        .Add("2560096467", 1240, 258, 1413, 282)
        .Add("İŞ YERİ ADRESİ", 61, 299, 203, 333)
        .Add("BEBEK MAH. GERMENCİK SK. BELEN APARTMANI NO: 2 İÇ", 300, 299, 740, 322)
        .Add("15353386824", 1127, 301, 1259, 324)
        .Add("TC KİMLİK NO", 937, 303, 1065, 330)
        .Add("KAPI NO: 6 BEŞİKTAŞ/ İSTANBUL", 299, 320, 545, 345)
        .Add("İŞE BAŞLAMA", 935, 365, 1066, 396)
        .Add("YILLIK GELİR VERGİSİ", 299, 368, 494, 395)
        .Add("11.05.2020", 1126, 369, 1236, 393)
        .Add("VERGİ TÜRÜ", 61, 371, 180, 401)
        .Add("TARIHİ", 935, 388, 1005, 417)
        .Add("ANA FAALİYET", 61, 435, 196, 462)
        .Add("479114-RADYO, TV, POSTA YOLUYLA VEYA İNTERNET ÜZERİNDEN YAPILAN PERAKENDE TİCARET",
             299, 438, 1025, 461)
        .Add("KODU VE ADI", 62, 460, 188, 484)
        .Build();

    [Fact]
    public void Extracts_every_field_from_the_gib_template()
    {
        var data = Parse(GibLevha()).Data!;

        Assert.Equal("2560096467", data.Vkn);
        Assert.Equal("15353386824", data.Tckn);
        Assert.Equal("MEHMET HİLMİ ÇİL", data.AdiSoyadi);
        Assert.Equal("BEŞİKTAŞ", data.VergiDairesi);
        Assert.Equal("2020-05-11", data.IseBaslamaTarihi);
        Assert.Equal("479114", data.AnaFaaliyetKodu);
    }

    [Fact]
    public void Joins_a_label_that_the_form_wrapped_onto_two_lines()
    {
        // "VERGİ" / "DAİRESİ" and "VERGİ KİMLİK" / "NO" are separate blocks in this layout.
        var result = Parse(GibLevha());

        Assert.Equal("BEŞİKTAŞ", result.Data!.VergiDairesi);
        Assert.Equal("2560096467", result.Data.Vkn);
        // Found via its own label, so no unlabelled-fallback warning.
        Assert.DoesNotContain(result.Warnings, w => w.Code == WarningCodes.VknLabelNotFound);
    }

    [Fact]
    public void A_wrapped_label_never_swallows_the_label_below_it()
    {
        // "VERGİ"+"DAİRESİ"+"VERGİ KİMLİK" prefix-matches "VERGİ DAİRESİ". Accepting that
        // three-line join returned "VERGİ KİMLİK" as the vergi dairesi value and destroyed the
        // VKN label, so a join must be explained by the alias completely.
        var result = Parse(GibLevha());

        Assert.NotEqual("VERGİ KİMLİK", result.Data!.VergiDairesi);
        Assert.NotNull(result.Data.Vkn);
    }

    [Fact]
    public void Splits_a_cell_that_holds_both_the_activity_code_and_its_description()
    {
        var data = Parse(GibLevha()).Data!;

        Assert.Equal("479114", data.AnaFaaliyetKodu);
        Assert.StartsWith("RADYO, TV, POSTA YOLUYLA", data.AnaFaaliyetAciklamasi);
        Assert.DoesNotContain("479114", data.AnaFaaliyetAciklamasi);
    }

    [Fact]
    public void Address_stops_at_the_row_belonging_to_the_next_label()
    {
        // The address value column continues into the "VERGİ TÜRÜ" row; only a label sitting to
        // the left of that row marks the boundary.
        var address = Parse(GibLevha()).Data!.Adres;

        Assert.Equal("BEBEK MAH. GERMENCİK SK. BELEN APARTMANI NO: 2 İÇ KAPI NO: 6 BEŞİKTAŞ/ İSTANBUL", address);
        Assert.DoesNotContain("YILLIK GELİR", address);
    }

    [Fact]
    public void The_gib_template_parses_without_warnings()
        => Assert.Empty(Parse(GibLevha()).Warnings);

    // ---------------------------------------------------------------- barcode digits

    private static OcrDocumentBuilder BarcodeHeader() => new OcrDocumentBuilder()
        .Add("VERGİ LEVHASI", 480, 40, 760, 85)
        .Add("HAZİNE VE MALİYE BAKANLIĞI", 400, 95, 840, 130)
        .Add("VERGİ DAİRESİ", 933, 150, 1066, 185)
        .Add("BEŞİKTAŞ", 1123, 150, 1221, 185);

    [Fact]
    public void Joins_barcode_digits_that_the_detector_split_into_fragments()
    {
        // Digits under a barcode are set wide apart, so the detector often returns them as
        // several short runs, none of which is ten digits on its own.
        var document = BarcodeHeader()
            .Add("VERGİ KİMLİK NO", 933, 225, 1066, 260)
            .Add("21600", 1240, 258, 1320, 290)
            .Add("24887", 1330, 258, 1410, 290)
            .Build();

        Assert.Equal("2160024887", Parse(document).Data!.Vkn);
    }

    [Fact]
    public void Reads_barcode_digits_printed_with_spaces_between_them()
    {
        var document = BarcodeHeader()
            .Add("VERGİ KİMLİK NO", 933, 225, 1066, 260)
            .Add("2 1 6 0 0 2 4 8 8 7", 1240, 258, 1413, 290)
            .Build();

        Assert.Equal("2160024887", Parse(document).Data!.Vkn);
    }

    [Fact]
    public void Accepts_the_same_unlabelled_number_appearing_twice()
    {
        // Printed under the barcode and again beside a label the catalogue does not know.
        // Two occurrences of one value is corroboration, not ambiguity.
        var document = BarcodeHeader()
            .Add("2160024887", 1240, 258, 1413, 290)
            .Add("MÜKELLEF NUMARA BİLGİSİ", 100, 400, 520, 435)
            .Add("2160024887", 150, 445, 400, 480)
            .Build();

        var result = Parse(document);

        Assert.Equal("2160024887", result.Data!.Vkn);
        Assert.Contains(result.Warnings, w => w.Code == WarningCodes.VknLabelNotFound);
    }

    [Fact]
    public void Still_refuses_to_choose_between_two_different_unlabelled_numbers()
    {
        var document = BarcodeHeader()
            .Add("2160024887", 1240, 258, 1413, 290)
            .Add("4540536920", 150, 445, 400, 480)
            .Build();

        var result = Parse(document);

        Assert.Null(result.Data!.Vkn);
        Assert.Contains(result.Warnings, w => w.Code == WarningCodes.VknNotFound);
    }

    [Fact]
    public void Does_not_fuse_two_unrelated_numbers_sitting_far_apart_on_one_line()
    {
        // A wide gap means two separate figures, not one split number.
        var document = BarcodeHeader()
            .Add("VERGİ KİMLİK NO", 933, 225, 1066, 260)
            .Add("21600", 1100, 258, 1180, 290)
            .Add("24887", 1390, 258, 1470, 290)
            .Build();

        Assert.Null(Parse(document).Data!.Vkn);
    }
}
