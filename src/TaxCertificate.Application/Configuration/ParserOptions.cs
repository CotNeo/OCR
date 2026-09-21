namespace TaxCertificate.Application.Configuration;

public sealed class ParserOptions
{
    public const string SectionName = "Parser";

    /// <summary>Minimum document-type score before the page is accepted as a vergi levhası.</summary>
    public double MinDocumentTypeScore { get; set; } = 0.35;

    /// <summary>
    /// Allows look-alike letters to be rewritten as digits (O->0, l->1) when no clean digit run
    /// is found. Off by default: silently altering a tax number is worse than reporting none.
    /// </summary>
    public bool AllowDigitSubstitution { get; set; }

    /// <summary>Fields scoring below this get a LOW_CONFIDENCE_FIELD warning.</summary>
    public double LowConfidenceThreshold { get; set; } = 0.70;

    /// <summary>Average OCR confidence below this gets a LOW_OCR_CONFIDENCE warning.</summary>
    public double LowOcrConfidenceThreshold { get; set; } = 0.75;

    /// <summary>Maximum number of OCR lines merged into the address value.</summary>
    public int MaxAddressLines { get; set; } = 6;

    /// <summary>
    /// Accept a checksum-valid VKN found without any nearby label. Keeps scanned templates the
    /// label catalogue does not cover from failing outright; the result carries VKN_LABEL_NOT_FOUND.
    /// </summary>
    public bool AllowVknWithoutLabel { get; set; } = true;
}
