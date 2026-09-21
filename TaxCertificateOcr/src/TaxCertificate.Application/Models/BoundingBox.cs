namespace TaxCertificate.Application.Models;

/// <summary>
/// Axis-aligned bounding rectangle in page pixel coordinates (origin = top-left).
/// </summary>
public sealed record BoundingBox(double X1, double Y1, double X2, double Y2)
{
    public double Width => Math.Max(0, X2 - X1);
    public double Height => Math.Max(0, Y2 - Y1);
    public double CenterX => (X1 + X2) / 2.0;
    public double CenterY => (Y1 + Y2) / 2.0;

    /// <summary>Length of the horizontal overlap between two boxes (0 when disjoint).</summary>
    public double HorizontalOverlap(BoundingBox other)
        => Math.Max(0, Math.Min(X2, other.X2) - Math.Max(X1, other.X1));

    /// <summary>Length of the vertical overlap between two boxes (0 when disjoint).</summary>
    public double VerticalOverlap(BoundingBox other)
        => Math.Max(0, Math.Min(Y2, other.Y2) - Math.Max(Y1, other.Y1));

    /// <summary>Horizontal overlap normalised by the narrower of the two boxes.</summary>
    public double HorizontalOverlapRatio(BoundingBox other)
    {
        var denominator = Math.Min(Width, other.Width);
        return denominator <= 0 ? 0 : Math.Clamp(HorizontalOverlap(other) / denominator, 0, 1);
    }

    /// <summary>Vertical overlap normalised by the shorter of the two boxes.</summary>
    public double VerticalOverlapRatio(BoundingBox other)
    {
        var denominator = Math.Min(Height, other.Height);
        return denominator <= 0 ? 0 : Math.Clamp(VerticalOverlap(other) / denominator, 0, 1);
    }

    public static BoundingBox FromPolygon(IReadOnlyList<(double X, double Y)> points)
    {
        if (points.Count == 0)
        {
            return new BoundingBox(0, 0, 0, 0);
        }

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in points)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        return new BoundingBox(minX, minY, maxX, maxY);
    }
}
