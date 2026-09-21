namespace TaxCertificate.Application.Parsers;

/// <summary>
/// Combines the two independent confidences that a field carries.
/// <para>
/// <b>OCR confidence</b> answers "were these glyphs read correctly?".
/// <b>Parser confidence</b> answers "does this text really belong to this field?".
/// They fail independently, and a field is only trustworthy when both hold, so the final score
/// is their product.
/// </para>
/// <para>
/// One adjustment matters in practice: the raw geometry score is compressed into
/// [<see cref="GeometryFloor"/>, 1] before it enters the product. A value sitting slightly
/// off-column is weaker evidence, not a different field, and letting a 0.45 geometry score
/// halve an otherwise certain VKN produced misleadingly low numbers.
/// </para>
/// </summary>
public static class FieldConfidence
{
    private const double GeometryFloor = 0.75;

    /// <summary>Evidence strength of each extraction strategy, before any modifiers.</summary>
    public const double StrategyInlineWithLabel = 0.98;
    public const double StrategyLabelAndSpatial = 0.95;
    public const double StrategyWithoutLabel = 0.55;

    /// <summary>Checksum outcome for identity numbers.</summary>
    public const double ChecksumValid = 1.00;
    public const double ChecksumInvalid = 0.50;
    public const double ChecksumNotApplicable = 0.92;

    /// <summary>Applied when a letter had to be rewritten as a digit to reach a valid shape.</summary>
    public const double DigitSubstitutionPenalty = 0.75;

    public static double CompressGeometry(double geometryScore)
        => GeometryFloor + (1 - GeometryFloor) * Math.Clamp(geometryScore, 0, 1);

    /// <summary>
    /// Parser-side confidence: strategy strength, degraded by how well the label matched,
    /// how well the value sits relative to it, and any format penalties.
    /// </summary>
    public static double Parser(
        double strategyScore,
        double labelSimilarity,
        double geometryScore,
        double formatScore,
        double extraPenalty = 1.0)
        => Math.Clamp(
            strategyScore
            * Math.Clamp(labelSimilarity, 0, 1)
            * CompressGeometry(geometryScore)
            * Math.Clamp(formatScore, 0, 1)
            * Math.Clamp(extraPenalty, 0, 1),
            0, 1);

    public static double Combine(double ocrConfidence, double parserConfidence)
        => Math.Round(Math.Clamp(ocrConfidence, 0, 1) * Math.Clamp(parserConfidence, 0, 1), 4);

    /// <summary>
    /// OCR confidence for a value spanning several blocks (address, wrapped unvan).
    /// Weighted by text length so a short fragment cannot dominate the score.
    /// </summary>
    public static double WeightedOcrConfidence(IEnumerable<(string Text, double Confidence)> parts)
    {
        double weightedSum = 0, totalWeight = 0;
        foreach (var (text, confidence) in parts)
        {
            var weight = Math.Max(text?.Length ?? 0, 1);
            weightedSum += confidence * weight;
            totalWeight += weight;
        }

        return totalWeight <= 0 ? 0 : Math.Clamp(weightedSum / totalWeight, 0, 1);
    }
}
