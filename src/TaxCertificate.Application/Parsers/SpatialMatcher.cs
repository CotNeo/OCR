using TaxCertificate.Application.Models;

namespace TaxCertificate.Application.Parsers;

public enum RelativePosition
{
    Below,
    Right,
}

public sealed record SpatialCandidate(
    OcrBlock Block,
    RelativePosition Position,
    double GeometryScore);

/// <summary>
/// Turns "which block belongs to this label?" into a deterministic geometric score.
/// <para>
/// Everything is expressed in multiples of the label's own height rather than absolute pixels,
/// so the same thresholds hold for a 150 DPI scan and a 12 MP phone photo.
/// </para>
/// </summary>
public static class SpatialMatcher
{
    // Search windows, in multiples of the label height.
    private const double MaxBelowGapFactor = 3.0;
    private const double MaxOverlapAboveFactor = 0.35;
    private const double MaxRightGapFactor = 10.0;

    // Geometry score weights (each component is already in [0,1]).
    private const double OverlapWeight = 0.55;
    private const double GapWeight = 0.30;
    private const double AlignmentWeight = 0.15;

    /// <summary>Values printed under a label are the dominant layout, so "right" is mildly penalised.</summary>
    private const double RightPositionPenalty = 0.92;

    /// <summary>
    /// Scores every candidate against the label and returns them best-first.
    /// Blocks outside the geometric window are dropped entirely.
    /// </summary>
    public static IReadOnlyList<SpatialCandidate> FindCandidates(
        OcrBlock label,
        IEnumerable<OcrBlock> candidates)
    {
        var results = new List<SpatialCandidate>();
        var labelHeight = Math.Max(label.Box.Height, 1.0);

        foreach (var candidate in candidates)
        {
            if (ReferenceEquals(candidate, label) || candidate.PageNumber != label.PageNumber)
            {
                continue;
            }

            var below = ScoreBelow(label.Box, candidate.Box, labelHeight);
            var right = ScoreRight(label.Box, candidate.Box, labelHeight);

            if (below is null && right is null)
            {
                continue;
            }

            if (below is not null && (right is null || below >= right))
            {
                results.Add(new SpatialCandidate(candidate, RelativePosition.Below, below.Value));
            }
            else if (right is not null)
            {
                results.Add(new SpatialCandidate(candidate, RelativePosition.Right, right.Value));
            }
        }

        return results.OrderByDescending(r => r.GeometryScore).ToList();
    }

    private static double? ScoreBelow(BoundingBox label, BoundingBox candidate, double labelHeight)
    {
        var gap = candidate.Y1 - label.Y2;

        // Allow a small negative gap: skewed scans make neighbouring rows overlap slightly.
        if (gap < -MaxOverlapAboveFactor * labelHeight || gap > MaxBelowGapFactor * labelHeight)
        {
            return null;
        }

        var overlap = label.HorizontalOverlapRatio(candidate);
        if (overlap <= 0.15)
        {
            return null;
        }

        var gapScore = Math.Exp(-Math.Max(gap, 0) / (1.5 * labelHeight));
        var alignment = 1.0 - Math.Clamp(Math.Abs(candidate.X1 - label.X1) / (4.0 * labelHeight), 0, 1);

        return OverlapWeight * overlap + GapWeight * gapScore + AlignmentWeight * alignment;
    }

    private static double? ScoreRight(BoundingBox label, BoundingBox candidate, double labelHeight)
    {
        var gap = candidate.X1 - label.X2;

        if (gap < -0.2 * labelHeight || gap > MaxRightGapFactor * labelHeight)
        {
            return null;
        }

        var overlap = label.VerticalOverlapRatio(candidate);
        if (overlap <= 0.35)
        {
            return null;
        }

        var gapScore = Math.Exp(-Math.Max(gap, 0) / (4.0 * labelHeight));
        var alignment = 1.0 - Math.Clamp(Math.Abs(candidate.CenterY - label.CenterY) / labelHeight, 0, 1);

        var score = OverlapWeight * overlap + GapWeight * gapScore + AlignmentWeight * alignment;
        return score * RightPositionPenalty;
    }

    /// <summary>
    /// Blocks that continue the same text column underneath <paramref name="anchor"/>, in reading order.
    /// Used for multi-line values such as the address. Stops at the first line that is too far away,
    /// so unrelated sections are never absorbed.
    /// </summary>
    public static IReadOnlyList<OcrBlock> CollectColumnBelow(
        OcrBlock anchor,
        IEnumerable<OcrBlock> candidates,
        Func<OcrBlock, bool> isStopBlock,
        int maxLines,
        double maxLineGapFactor = 1.6)
    {
        var anchorHeight = Math.Max(anchor.Box.Height, 1.0);

        var ordered = candidates
            .Where(b => b.PageNumber == anchor.PageNumber && b.Box.Y1 >= anchor.Box.Y1 - 0.3 * anchorHeight)
            .Where(b => !ReferenceEquals(b, anchor))
            .OrderBy(b => b.Box.Y1)
            .ThenBy(b => b.Box.X1)
            .ToList();

        var collected = new List<OcrBlock>();
        var cursor = anchor;

        foreach (var block in ordered)
        {
            if (collected.Count >= maxLines)
            {
                break;
            }

            var gap = block.Box.Y1 - cursor.Box.Y2;
            if (gap > maxLineGapFactor * Math.Max(cursor.Box.Height, anchorHeight))
            {
                break;
            }

            // A new label marks the end of this value, whatever the geometry says.
            if (isStopBlock(block))
            {
                break;
            }

            if (anchor.Box.HorizontalOverlapRatio(block.Box) <= 0.15 &&
                Math.Abs(block.Box.X1 - anchor.Box.X1) > 4.0 * anchorHeight)
            {
                continue;
            }

            collected.Add(block);
            cursor = block;
        }

        return collected;
    }
}
