namespace TaxCertificate.Application.Services;

/// <summary>
/// Masks identity numbers before they reach a log sink.
/// Keeps the last four digits so an operator can still correlate a support request with a
/// request id, without the log becoming a store of tax numbers.
/// </summary>
public static class PiiMasking
{
    private const int VisibleSuffixLength = 4;

    public static string? MaskIdentityNumber(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (value.Length <= VisibleSuffixLength)
        {
            return new string('*', value.Length);
        }

        return new string('*', value.Length - VisibleSuffixLength) + value[^VisibleSuffixLength..];
    }

    /// <summary>Collapses free text to a length only; never logs the content itself.</summary>
    public static string Redact(string? value)
        => string.IsNullOrEmpty(value) ? "<empty>" : $"<{value.Length} chars>";
}
