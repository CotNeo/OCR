namespace TaxCertificate.Application.Models;

/// <summary>
/// A single text region produced by the OCR engine. This is the *only* contract the
/// parser depends on, which keeps OCR and business parsing fully decoupled.
/// </summary>
public sealed record OcrBlock
{
    public required int Index { get; init; }
    public required int PageNumber { get; init; }
    public required string Text { get; init; }
    public required double Confidence { get; init; }
    public required BoundingBox Box { get; init; }

    /// <summary>Optional detection polygon; present when the engine reports a quadrilateral.</summary>
    public IReadOnlyList<(double X, double Y)>? Polygon { get; init; }
}

public sealed record OcrPage
{
    public required int PageNumber { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required IReadOnlyList<OcrBlock> Blocks { get; init; }

    /// <summary>
    /// Degrees of deskew the OCR service applied before recognition. Block coordinates are in
    /// that rotated space, so a debug viewer needs this to line boxes up with the original file.
    /// </summary>
    public double AppliedRotation { get; init; }
}

public sealed record OcrDocument
{
    public required IReadOnlyList<OcrPage> Pages { get; init; }

    public IEnumerable<OcrBlock> AllBlocks => Pages.SelectMany(p => p.Blocks);

    public static OcrDocument Empty { get; } = new() { Pages = Array.Empty<OcrPage>() };
}
