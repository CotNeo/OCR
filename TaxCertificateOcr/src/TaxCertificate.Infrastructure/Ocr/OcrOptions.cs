namespace TaxCertificate.Infrastructure.Ocr;

public sealed class OcrOptions
{
    public const string SectionName = "Ocr";

    public string BaseUrl { get; set; } = "http://127.0.0.1:8001";

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Echo the raw OCR blocks back in the API response. Off in production.</summary>
    public bool IncludeRawResult { get; set; }

    /// <summary>
    /// Retries for transient failures only. OCR is CPU-bound and a blind retry doubles the load
    /// on an 8 GB machine, so keep this at 1 or 2.
    /// </summary>
    public int MaxRetries { get; set; } = 1;

    public int RetryBaseDelayMilliseconds { get; set; } = 500;
}
