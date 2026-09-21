using TaxCertificate.Application.Models;

namespace TaxCertificate.UnitTests.Helpers;

/// <summary>
/// Builds synthetic OCR pages so the parser can be tested without running any model.
/// <see cref="AddLine"/> lays blocks out on an implicit grid; <see cref="Add"/> sets exact coordinates.
/// </summary>
public sealed class OcrDocumentBuilder
{
    private const double LineHeight = 40;
    private const double LineSpacing = 26;

    private readonly List<OcrBlock> _blocks = [];
    private readonly int _pageNumber;
    private double _cursorY = 100;
    private int _index;

    public OcrDocumentBuilder(int pageNumber = 1) => _pageNumber = pageNumber;

    /// <summary>Adds a block on its own line, starting at <paramref name="x"/>.</summary>
    public OcrDocumentBuilder AddLine(string text, double confidence = 0.98, double x = 100, double? width = null)
    {
        var w = width ?? Math.Max(60, text.Length * 16);
        _blocks.Add(new OcrBlock
        {
            Index = _index++,
            PageNumber = _pageNumber,
            Text = text,
            Confidence = confidence,
            Box = new BoundingBox(x, _cursorY, x + w, _cursorY + LineHeight),
        });

        _cursorY += LineHeight + LineSpacing;
        return this;
    }

    /// <summary>Adds a block to the right of the previous line, without advancing the cursor.</summary>
    public OcrDocumentBuilder AddRightOfPrevious(string text, double confidence = 0.98, double gap = 30)
    {
        var previous = _blocks[^1];
        var w = Math.Max(60, text.Length * 16);
        _blocks.Add(new OcrBlock
        {
            Index = _index++,
            PageNumber = _pageNumber,
            Text = text,
            Confidence = confidence,
            Box = new BoundingBox(
                previous.Box.X2 + gap, previous.Box.Y1, previous.Box.X2 + gap + w, previous.Box.Y2),
        });

        return this;
    }

    public OcrDocumentBuilder Add(string text, double x1, double y1, double x2, double y2, double confidence = 0.98)
    {
        _blocks.Add(new OcrBlock
        {
            Index = _index++,
            PageNumber = _pageNumber,
            Text = text,
            Confidence = confidence,
            Box = new BoundingBox(x1, y1, x2, y2),
        });

        return this;
    }

    /// <summary>Adds the header anchors every vergi levhası carries, so detection passes.</summary>
    public OcrDocumentBuilder WithTaxCertificateHeader()
        => AddLine("T.C.", x: 500)
            .AddLine("HAZİNE VE MALİYE BAKANLIĞI", x: 400)
            .AddLine("GELİR İDARESİ BAŞKANLIĞI", x: 420)
            .AddLine("VERGİ LEVHASI", x: 480);

    public OcrDocument Build() => new()
    {
        Pages =
        [
            new OcrPage
            {
                PageNumber = _pageNumber,
                Width = 1240,
                Height = 1754,
                Blocks = _blocks,
            },
        ],
    };
}
