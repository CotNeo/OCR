using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaxCertificate.Application.Configuration;
using TaxCertificate.Application.Dtos;
using TaxCertificate.Application.Interfaces;
using TaxCertificate.Application.Models;
using TaxCertificate.Application.Parsers;
using TaxCertificate.Application.Services;
using TaxCertificate.UnitTests.Helpers;
using Xunit;

namespace TaxCertificate.UnitTests;

/// <summary>
/// Hand-written stub instead of a mocking library: the interface has two members, and the OCR
/// engine must never be loaded by a unit test.
/// </summary>
internal sealed class FakeOcrClient : IOcrClient
{
    private readonly OcrDocument _document;

    public FakeOcrClient(OcrDocument document) => _document = document;

    public int RecognizeCallCount { get; private set; }

    public string? LastContentType { get; private set; }

    public Task<OcrDocument> RecognizeAsync(
        Stream content, string fileName, string contentType, CancellationToken cancellationToken)
    {
        RecognizeCallCount++;
        LastContentType = contentType;
        return Task.FromResult(_document);
    }

    public Task<OcrHealthStatus> CheckHealthAsync(CancellationToken cancellationToken)
        => Task.FromResult(new OcrHealthStatus(true, true));
}

public class AnalyzerTests
{
    private static TaxCertificateAnalyzer Create(OcrDocument document, bool includeRaw = false)
        => new(
            new FakeOcrClient(document),
            new TaxCertificateParser(new ParserOptions()),
            Options.Create(new AnalyzerOptions { IncludeRawResult = includeRaw }),
            NullLogger<TaxCertificateAnalyzer>.Instance);

    private static OcrDocument Certificate() => new OcrDocumentBuilder()
        .WithTaxCertificateHeader()
        .AddLine("VERGİ KİMLİK NUMARASI")
        .AddLine("4540536920", x: 150)
        .AddLine("VERGİ DAİRESİ")
        .AddLine("ÜMRANİYE", x: 150)
        .Build();

    private static MemoryStream Payload() => new([1, 2, 3, 4]);

    [Fact]
    public async Task AnalyzeAsync_returns_parsed_fields()
    {
        var result = await Create(Certificate())
            .AnalyzeAsync(Payload(), "x.png", "image/png", default);

        Assert.True(result.Success);
        Assert.Equal("4540536920", result.Data!.Vkn);
        Assert.Equal("ÜMRANİYE", result.Data.VergiDairesi);
    }

    [Fact]
    public async Task AnalyzeAsync_omits_raw_ocr_by_default()
    {
        var result = await Create(Certificate())
            .AnalyzeAsync(Payload(), "x.png", "image/png", default);

        Assert.Null(result.RawOcr);
    }

    [Fact]
    public async Task AnalyzeAsync_includes_raw_ocr_only_when_explicitly_enabled()
    {
        var result = await Create(Certificate(), includeRaw: true)
            .AnalyzeAsync(Payload(), "x.png", "image/png", default);

        Assert.NotNull(result.RawOcr);
        Assert.NotEmpty(result.RawOcr!);
        Assert.Contains(result.RawOcr!, b => b.Text == "4540536920");
    }

    [Fact]
    public async Task AnalyzeAsync_emits_page_geometry_alongside_raw_ocr()
    {
        // The debug viewer scales its block overlay by these dimensions, so they must travel
        // with rawOcr and be gated by the same flag.
        var result = await Create(Certificate(), includeRaw: true)
            .AnalyzeAsync(Payload(), "x.png", "image/png", default);

        var page = Assert.Single(result.OcrPages!);
        Assert.Equal(1, page.Page);
        Assert.Equal(1240, page.Width);
        Assert.Equal(1754, page.Height);
    }

    [Fact]
    public async Task AnalyzeAsync_omits_page_geometry_when_raw_ocr_is_disabled()
    {
        var result = await Create(Certificate())
            .AnalyzeAsync(Payload(), "x.png", "image/png", default);

        Assert.Null(result.OcrPages);
        Assert.Null(result.RawOcr);
    }

    [Fact]
    public async Task AnalyzeAsync_reports_no_text_detected_for_an_empty_document()
    {
        var empty = new OcrDocument
        {
            Pages = [new OcrPage { PageNumber = 1, Width = 100, Height = 100, Blocks = [] }],
        };

        var result = await Create(empty).AnalyzeAsync(Payload(), "x.png", "image/png", default);

        Assert.False(result.Success);
        Assert.Equal(ErrorCodes.NoTextDetected, result.Error!.Code);
    }

    [Fact]
    public async Task AnalyzeAsync_calls_the_ocr_client_exactly_once()
    {
        var client = new FakeOcrClient(Certificate());
        var analyzer = new TaxCertificateAnalyzer(
            client,
            new TaxCertificateParser(new ParserOptions()),
            Options.Create(new AnalyzerOptions()),
            NullLogger<TaxCertificateAnalyzer>.Instance);

        await analyzer.AnalyzeAsync(Payload(), "x.pdf", "application/pdf", default);

        Assert.Equal(1, client.RecognizeCallCount);
        Assert.Equal("application/pdf", client.LastContentType);
    }

    [Fact]
    public async Task AnalyzeAsync_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Create(Certificate()).AnalyzeAsync(Payload(), "x.png", "image/png", cts.Token));
    }
}

public class SpatialMatcherTests
{
    private static OcrBlock Block(string text, double x1, double y1, double x2, double y2, int index = 0)
        => new()
        {
            Index = index,
            PageNumber = 1,
            Text = text,
            Confidence = 0.98,
            Box = new BoundingBox(x1, y1, x2, y2),
        };

    [Fact]
    public void Prefers_the_block_directly_below_the_label()
    {
        var label = Block("VERGİ KİMLİK NUMARASI", 100, 100, 400, 140);
        var below = Block("4540536920", 100, 160, 300, 200, 1);
        var farBelow = Block("9999999999", 100, 600, 300, 640, 2);

        var candidates = SpatialMatcher.FindCandidates(label, [below, farBelow]);

        Assert.Equal("4540536920", candidates[0].Block.Text);
        Assert.Equal(RelativePosition.Below, candidates[0].Position);
    }

    [Fact]
    public void Finds_a_value_to_the_right_on_the_same_row()
    {
        var label = Block("VERGİ DAİRESİ", 100, 100, 300, 140);
        var right = Block("ÜMRANİYE", 340, 102, 500, 138, 1);

        var candidate = Assert.Single(SpatialMatcher.FindCandidates(label, [right]));

        Assert.Equal(RelativePosition.Right, candidate.Position);
    }

    [Fact]
    public void Excludes_blocks_outside_the_geometric_window()
    {
        var label = Block("ADRES", 100, 100, 200, 140);

        // Far below (beyond 3x label height) and far to the left in a different column.
        var tooFar = Block("IRRELEVANT", 100, 500, 300, 540, 1);
        var differentColumn = Block("OTHER", 900, 100, 1100, 140, 2);

        var candidates = SpatialMatcher.FindCandidates(label, [tooFar, differentColumn]);

        Assert.DoesNotContain(candidates, c => c.Block.Text == "IRRELEVANT");
        Assert.DoesNotContain(candidates, c => c.Block.Text == "OTHER");
    }

    [Fact]
    public void Tolerates_a_slight_overlap_caused_by_skew()
    {
        var label = Block("VERGİ KİMLİK NUMARASI", 100, 100, 400, 140);
        // Starts 5px above the label's bottom edge, as happens on a rotated scan.
        var slightlyOverlapping = Block("4540536920", 100, 135, 300, 175, 1);

        Assert.Single(SpatialMatcher.FindCandidates(label, [slightlyOverlapping]));
    }

    [Fact]
    public void Ignores_blocks_on_a_different_page()
    {
        var label = Block("VERGİ KİMLİK NUMARASI", 100, 100, 400, 140);
        var otherPage = new OcrBlock
        {
            Index = 1, PageNumber = 2, Text = "4540536920", Confidence = 0.99,
            Box = new BoundingBox(100, 160, 300, 200),
        };

        Assert.Empty(SpatialMatcher.FindCandidates(label, [otherPage]));
    }

    [Fact]
    public void CollectColumnBelow_stops_at_a_stop_block()
    {
        var anchor = Block("SARAY MAH.", 150, 100, 500, 140);
        var second = Block("NO: 4 KAT: 7", 150, 150, 400, 190, 1);
        var stop = Block("ANA FAALİYET KODU", 100, 200, 400, 240, 2);
        var afterStop = Block("494103", 150, 250, 300, 290, 3);

        var collected = SpatialMatcher.CollectColumnBelow(
            anchor, [second, stop, afterStop],
            isStopBlock: b => b.Text == "ANA FAALİYET KODU",
            maxLines: 6);

        Assert.Single(collected);
        Assert.Equal("NO: 4 KAT: 7", collected[0].Text);
    }

    [Fact]
    public void CollectColumnBelow_respects_the_line_budget()
    {
        var anchor = Block("LINE 0", 150, 100, 500, 140);
        var lines = Enumerable.Range(1, 5)
            .Select(i => Block($"LINE {i}", 150, 100 + i * 50, 500, 140 + i * 50, i))
            .ToList();

        var collected = SpatialMatcher.CollectColumnBelow(
            anchor, lines, isStopBlock: _ => false, maxLines: 2);

        Assert.Equal(2, collected.Count);
    }
}

public class BoundingBoxTests
{
    [Fact]
    public void FromPolygon_computes_the_extent_of_a_rotated_quadrilateral()
    {
        var box = BoundingBox.FromPolygon([(10, 20), (60, 22), (61, 48), (11, 46)]);

        Assert.Equal(10, box.X1);
        Assert.Equal(20, box.Y1);
        Assert.Equal(61, box.X2);
        Assert.Equal(48, box.Y2);
    }

    [Fact]
    public void FromPolygon_handles_an_empty_point_list()
    {
        var box = BoundingBox.FromPolygon([]);
        Assert.Equal(0, box.Width);
        Assert.Equal(0, box.Height);
    }

    [Fact]
    public void Overlap_ratios_are_normalised_by_the_smaller_box()
    {
        var wide = new BoundingBox(0, 0, 100, 40);
        var narrow = new BoundingBox(20, 50, 60, 90);

        // Narrow sits fully within the wide box horizontally.
        Assert.Equal(1.0, wide.HorizontalOverlapRatio(narrow));
        // They do not overlap vertically at all.
        Assert.Equal(0.0, wide.VerticalOverlapRatio(narrow));
    }

    [Fact]
    public void Degenerate_boxes_do_not_divide_by_zero()
    {
        var zero = new BoundingBox(10, 10, 10, 10);
        var normal = new BoundingBox(0, 0, 100, 40);

        Assert.Equal(0.0, normal.HorizontalOverlapRatio(zero));
        Assert.Equal(0.0, normal.VerticalOverlapRatio(zero));
    }
}
