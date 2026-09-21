namespace TaxCertificate.Api.Configuration;

public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    /// <summary>Hard ceiling on the request body. Default 10 MB.</summary>
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Concurrent analyses allowed through the API. The Python service serialises inference
    /// anyway; this keeps request bodies from piling up in memory while they wait.
    /// </summary>
    public int MaxConcurrentAnalyses { get; set; } = 2;

    /// <summary>How long a request waits for a concurrency slot before returning 503.</summary>
    public int ConcurrencyQueueTimeoutSeconds { get; set; } = 30;
}
