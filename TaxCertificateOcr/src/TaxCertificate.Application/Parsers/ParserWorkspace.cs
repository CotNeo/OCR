using TaxCertificate.Application.Models;

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

    public ParserWorkspace(OcrDocument document)
    {
        Document = document;
        Blocks = document.AllBlocks
            .Where(b => !string.IsNullOrWhiteSpace(b.Text))
            .OrderBy(b => b.PageNumber)
            .ThenBy(b => b.Box.Y1)
            .ThenBy(b => b.Box.X1)
            .ToList();

        foreach (var block in Blocks)
        {
            var match = LabelCatalog.Match(block.Text);
            if (match is not null)
            {
                _labels[block.Index] = match;
            }
        }
    }

    public OcrDocument Document { get; }

    public IReadOnlyList<OcrBlock> Blocks { get; }

    public bool IsLabel(OcrBlock block) => _labels.ContainsKey(block.Index);

    public bool IsConsumed(OcrBlock block) => _consumed.Contains(block.Index);

    public void Consume(OcrBlock block) => _consumed.Add(block.Index);

    /// <summary>Label blocks for a field, strongest match first.</summary>
    public IEnumerable<(OcrBlock Block, LabelMatch Match)> LabelsFor(TaxCertificateField field)
        => Blocks
            .Where(b => _labels.TryGetValue(b.Index, out var m) && m.Field == field)
            .Select(b => (b, _labels[b.Index]))
            .OrderByDescending(x => x.Item2.Similarity);

    /// <summary>Blocks eligible to be a value: not a label, not already claimed.</summary>
    public IEnumerable<OcrBlock> AvailableValues()
        => Blocks.Where(b => !IsLabel(b) && !IsConsumed(b));

    public double AverageOcrConfidence()
        => Blocks.Count == 0
            ? 0
            : FieldConfidence.WeightedOcrConfidence(Blocks.Select(b => (b.Text, b.Confidence)));
}
