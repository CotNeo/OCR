using TaxCertificate.Application.Models;
using TaxCertificate.Application.Text;

namespace TaxCertificate.Application.Parsers;

/// <summary>
/// Shared state for a single parse run: which block is which label, and which blocks have
/// already been claimed as a value. Claiming matters because the address collector walks a
/// column and must not swallow another field's value.
/// </summary>
internal sealed class ParserWorkspace
{
    private readonly Dictionary<int, LabelMatch> _labels = new();
    private readonly HashSet<int> _consumed = [];

    /// <summary>Synthetic merged block index -> the original block indices it was built from.</summary>
    private readonly Dictionary<int, int[]> _mergedParts = new();

    private readonly List<OcrBlock> _mergedDigitBlocks = [];

    /// <summary>Synthetic labels built from stacked fragments, plus the fragments they consumed.</summary>
    private readonly List<OcrBlock> _mergedLabelBlocks = [];

    private readonly HashSet<int> _labelFragments = [];

    private int _nextSyntheticIndex;

    public ParserWorkspace(OcrDocument document)
    {
        Document = document;
        Blocks = document.AllBlocks
            .Where(b => !string.IsNullOrWhiteSpace(b.Text))
            .OrderBy(b => b.PageNumber)
            .ThenBy(b => b.Box.Y1)
            .ThenBy(b => b.Box.X1)
            .ToList();

        _nextSyntheticIndex = Blocks.Count == 0 ? 0 : Blocks.Max(b => b.Index) + 1;

        BuildLabels();
        BuildMergedDigitBlocks();
    }

    /// <summary>
    /// Assigns labels, joining fragments that a form layout wrapped onto two or three lines.
    /// <para>
    /// The GİB e-levha template wraps nearly every label in its narrow label column -
    /// "VERGİ" / "DAİRESİ", "VERGİ KİMLİK" / "NO", "ANA FAALİYET" / "KODU VE ADI" - and the
    /// fragments match either nothing or, worse, a shorter unrelated alias. A stacked group is
    /// therefore preferred whenever it matches a *longer* alias than any of its parts alone.
    /// </para>
    /// </summary>
    private void BuildLabels()
    {
        foreach (var block in Blocks)
        {
            var match = LabelCatalog.Match(block.Text);
            if (match is not null)
            {
                _labels[block.Index] = match;
            }
        }

        // Fragments are found by geometry, not by list order. Blocks are sorted top-to-bottom
        // across the whole page, so in a two-column form the halves interleave: on a real
        // levha "VERGİ" and "DAİRESİ" are three blocks apart because the left column's rows
        // fall between them.
        foreach (var first in Blocks)
        {
            if (_labelFragments.Contains(first.Index))
            {
                continue;
            }

            var chain = new List<OcrBlock> { first };

            // Grow downwards while a stacked continuation exists, up to three lines.
            while (chain.Count < 3)
            {
                var next = FindStackedContinuation(chain[^1]);
                if (next is null)
                {
                    break;
                }

                chain.Add(next);
            }

            // Longest join first: a three-line label must not be captured as a two-line one.
            for (var span = chain.Count; span >= 2; span--)
            {
                var parts = chain.Take(span).ToList();
                var mergedText = string.Join(" ", parts.Select(p => p.Text));
                var mergedMatch = LabelCatalog.Match(mergedText);

                // The join has to be fully explained by the alias. A partial match leaves an
                // inline remainder, which here means the chain reached into the next label:
                // "VERGİ" + "DAİRESİ" + "VERGİ KİMLİK" matches "VERGİ DAİRESİ" as a prefix and
                // would hand back "VERGİ KİMLİK" as the vergi dairesi value while destroying
                // the VKN label. Shorter spans are tried next, so the correct pair still wins.
                if (mergedMatch is null || mergedMatch.InlineRemainder is not null)
                {
                    continue;
                }

                var bestPartLength = parts
                    .Select(p => _labels.TryGetValue(p.Index, out var m) ? m.Definition.NormalizedAlias.Length : 0)
                    .DefaultIfEmpty(0)
                    .Max();

                // Only worth it if the join genuinely recognises more than the parts did.
                if (mergedMatch.Definition.NormalizedAlias.Length <= bestPartLength)
                {
                    continue;
                }

                var merged = new OcrBlock
                {
                    Index = _nextSyntheticIndex++,
                    PageNumber = parts[0].PageNumber,
                    Text = mergedText,
                    Confidence = parts.Min(p => p.Confidence),
                    Box = new BoundingBox(
                        parts.Min(p => p.Box.X1), parts.Min(p => p.Box.Y1),
                        parts.Max(p => p.Box.X2), parts.Max(p => p.Box.Y2)),
                };

                _mergedLabelBlocks.Add(merged);
                _labels[merged.Index] = mergedMatch;

                foreach (var part in parts)
                {
                    // Fragments stay out of the value pool: "KODU VE ADI" is not a value.
                    _labelFragments.Add(part.Index);
                    _labels.Remove(part.Index);
                }

                break;
            }
        }
    }

    /// <summary>
    /// The block directly underneath <paramref name="above"/> in the same left-aligned column,
    /// or null. Tolerances are generous downwards-overlapping because wrapped form labels are
    /// set tight enough that their line boxes overlap slightly.
    /// </summary>
    private OcrBlock? FindStackedContinuation(OcrBlock above)
    {
        var height = Math.Max(above.Box.Height, 1.0);
        OcrBlock? best = null;

        foreach (var candidate in Blocks)
        {
            if (candidate.PageNumber != above.PageNumber ||
                candidate.Index == above.Index ||
                _labelFragments.Contains(candidate.Index))
            {
                continue;
            }

            var gap = candidate.Box.Y1 - above.Box.Y2;
            if (gap < -0.5 * height || gap > 0.7 * height)
            {
                continue;
            }

            if (Math.Abs(candidate.Box.X1 - above.Box.X1) > 0.8 * height)
            {
                continue;
            }

            if (best is null || candidate.Box.Y1 < best.Box.Y1)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// Joins digit-only blocks that sit side by side on one line into a single virtual block.
    /// <para>
    /// Needed because a number printed with wide character spacing - the digits under a
    /// barcode are the usual case - comes back from the detector as several fragments
    /// ("21600", "24887"), none of which is long enough to be recognised as a VKN on its own.
    /// </para>
    /// <para>
    /// The originals are kept as well; these are extra candidates, never replacements.
    /// </para>
    /// </summary>
    private void BuildMergedDigitBlocks()
    {
        var fragments = Blocks
            .Where(b => !IsLabel(b))
            .Select(b => (Block: b, Digits: DigitExtractor.DigitsOnly(b.Text)))
            // Digit-only content: anything carrying letters is prose, not a split number.
            .Where(x => x.Digits.Length > 0 &&
                        x.Digits.Length == TurkishTextNormalizer.NormalizeCompact(x.Block.Text).Length)
            .OrderBy(x => x.Block.PageNumber)
            .ThenBy(x => x.Block.Box.Y1)
            .ThenBy(x => x.Block.Box.X1)
            .ToList();

        var group = new List<(OcrBlock Block, string Digits)>();

        void Flush()
        {
            if (group.Count >= 2)
            {
                var box = new BoundingBox(
                    group.Min(g => g.Block.Box.X1), group.Min(g => g.Block.Box.Y1),
                    group.Max(g => g.Block.Box.X2), group.Max(g => g.Block.Box.Y2));

                var merged = new OcrBlock
                {
                    Index = _nextSyntheticIndex++,
                    PageNumber = group[0].Block.PageNumber,
                    Text = string.Concat(group.Select(g => g.Digits)),
                    // Weakest fragment governs: one badly read digit invalidates the whole number.
                    Confidence = group.Min(g => g.Block.Confidence),
                    Box = box,
                };

                _mergedDigitBlocks.Add(merged);
                _mergedParts[merged.Index] = group.Select(g => g.Block.Index).ToArray();
            }

            group.Clear();
        }

        foreach (var fragment in fragments)
        {
            if (group.Count > 0)
            {
                var previous = group[^1].Block;
                var height = Math.Max(previous.Box.Height, 1.0);
                var sameLine = previous.Box.VerticalOverlapRatio(fragment.Block.Box) > 0.5;
                var gap = fragment.Block.Box.X1 - previous.Box.X2;

                // Fragments of one number sit very close together; a wider gap is a
                // different number in the same row.
                var adjacent = sameLine && gap >= -0.2 * height && gap <= 1.5 * height;

                // Cap the chain so a row of unrelated figures cannot snowball into one run.
                if (!adjacent || group.Count >= 4)
                {
                    Flush();
                }
            }

            group.Add(fragment);
        }

        Flush();
    }

    public OcrDocument Document { get; }

    public IReadOnlyList<OcrBlock> Blocks { get; }

    public bool IsLabel(OcrBlock block)
        => _labels.ContainsKey(block.Index) || _labelFragments.Contains(block.Index);

    public bool IsConsumed(OcrBlock block) => _consumed.Contains(block.Index);

    /// <summary>
    /// True when some other label sits on the same line to the left of <paramref name="block"/>.
    /// In a two-column form that means the block is that label's value, not a continuation of
    /// whatever we are currently collecting - the rule that stops the address from swallowing
    /// the "VERGİ TÜRÜ" row underneath it.
    /// </summary>
    public bool BelongsToAnotherLabel(OcrBlock block, OcrBlock owningLabel)
        => Blocks.Concat(_mergedLabelBlocks).Any(candidate =>
            candidate.Index != owningLabel.Index &&
            candidate.PageNumber == block.PageNumber &&
            _labels.ContainsKey(candidate.Index) &&
            candidate.Box.X2 <= block.Box.X1 &&
            candidate.Box.VerticalOverlapRatio(block.Box) > 0.5);

    public void Consume(OcrBlock block)
    {
        _consumed.Add(block.Index);

        // Claiming a merged run must also claim its fragments, or the address collector would
        // happily pick the same digits up again.
        if (_mergedParts.TryGetValue(block.Index, out var parts))
        {
            foreach (var part in parts)
            {
                _consumed.Add(part);
            }
        }
    }

    /// <summary>Label blocks for a field, strongest match first.</summary>
    public IEnumerable<(OcrBlock Block, LabelMatch Match)> LabelsFor(TaxCertificateField field)
        => Blocks.Concat(_mergedLabelBlocks)
            .Where(b => _labels.TryGetValue(b.Index, out var m) && m.Field == field)
            .Select(b => (b, _labels[b.Index]))
            .OrderByDescending(x => x.Item2.Similarity);

    /// <summary>Blocks eligible to be a value: not a label, not already claimed.</summary>
    public IEnumerable<OcrBlock> AvailableValues()
        => Blocks.Where(b => !IsLabel(b) && !IsConsumed(b));

    /// <summary>
    /// Value candidates for numeric fields: the ordinary blocks plus the merged digit runs.
    /// Text fields deliberately do not see the merged blocks - they are digit strings only.
    /// </summary>
    public IEnumerable<OcrBlock> AvailableNumericValues()
        => AvailableValues().Concat(_mergedDigitBlocks.Where(b => !IsConsumed(b) && PartsAvailable(b)));

    private bool PartsAvailable(OcrBlock merged)
        => !_mergedParts.TryGetValue(merged.Index, out var parts) || parts.All(i => !_consumed.Contains(i));

    public double AverageOcrConfidence()
        => Blocks.Count == 0
            ? 0
            : FieldConfidence.WeightedOcrConfidence(Blocks.Select(b => (b.Text, b.Confidence)));
}
