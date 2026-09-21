namespace TaxCertificate.Infrastructure.Ocr;

/// <summary>
/// Raised when the OCR service is unreachable, times out or answers with an error.
/// Carries a stable code so the API layer can map it without inspecting messages.
/// </summary>
public sealed class OcrClientException : Exception
{
    public OcrClientException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
        => Code = code;

    public string Code { get; }

    public static OcrClientException Unavailable(Exception? inner = null)
        => new("OCR_SERVICE_UNAVAILABLE", "OCR service is unavailable.", inner);

    public static OcrClientException Timeout(Exception? inner = null)
        => new("OCR_SERVICE_TIMEOUT", "OCR service did not respond in time.", inner);

    public static OcrClientException Failed(string message)
        => new("OCR_FAILED", message);
}
