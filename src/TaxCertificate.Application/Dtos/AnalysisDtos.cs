using System.Text.Json.Serialization;

namespace TaxCertificate.Application.Dtos;

public static class DocumentTypes
{
    public const string VergiLevhasi = "VERGI_LEVHASI";
    public const string Unknown = "UNKNOWN";
}

/// <summary>Fields extracted from a vergi levhası. Null means "not found", never "empty".</summary>
public sealed record TaxCertificateData
{
    public string? Vkn { get; init; }
    public string? Tckn { get; init; }
    public string? TicaretUnvani { get; init; }
    public string? AdiSoyadi { get; init; }
    public string? VergiDairesi { get; init; }
    public string? Adres { get; init; }

    /// <summary>ISO 8601 date (yyyy-MM-dd), or null when the printed date could not be parsed.</summary>
    public string? IseBaslamaTarihi { get; init; }

    /// <summary>Digits only, e.g. "494103".</summary>
    public string? AnaFaaliyetKodu { get; init; }

    /// <summary>Code exactly as read, e.g. "49.41.03". Useful when the normalisation is disputed.</summary>
    public string? AnaFaaliyetKoduRaw { get; init; }

    public string? AnaFaaliyetAciklamasi { get; init; }
}

public sealed record FieldValidation(
    bool Valid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null);

public sealed record ValidationReport
{
    public FieldValidation? Vkn { get; init; }
    public FieldValidation? Tckn { get; init; }
    public FieldValidation? IseBaslamaTarihi { get; init; }
}

public sealed record ConfidenceReport
{
    public double Overall { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Vkn { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Tckn { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TicaretUnvani { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AdiSoyadi { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? VergiDairesi { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Adres { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? IseBaslamaTarihi { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AnaFaaliyetKodu { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AnaFaaliyetAciklamasi { get; init; }

    /// <summary>How strongly the page looks like a vergi levhası, in [0,1].</summary>
    public double DocumentType { get; init; }
}

public sealed record AnalysisWarning(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field = null);

public sealed record ErrorInfo(string Code, string Message);

/// <summary>A single OCR block, echoed back only when raw output is explicitly enabled.</summary>
public sealed record RawOcrBlock(
    int Page,
    string Text,
    double Confidence,
    double X1,
    double Y1,
    double X2,
    double Y2);

/// <summary>
/// Geometry of one OCR page, emitted alongside <c>rawOcr</c> so a debug viewer can scale block
/// overlays. Debug-only, like rawOcr itself.
/// </summary>
public sealed record RawOcrPage(int Page, int Width, int Height, double AppliedRotation);

public sealed record AnalysisResult
{
    public required bool Success { get; init; }
    public required string DocumentType { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TaxCertificateData? Data { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ValidationReport? Validation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConfidenceReport? Confidence { get; init; }
    public IReadOnlyList<AnalysisWarning> Warnings { get; init; } = Array.Empty<AnalysisWarning>();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ErrorInfo? Error { get; init; }

    /// <summary>Populated only when Ocr:IncludeRawResult is enabled; omitted from JSON otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RawOcrBlock>? RawOcr { get; init; }

    /// <summary>Page dimensions for the blocks in <see cref="RawOcr"/>. Debug-only.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RawOcrPage>? OcrPages { get; init; }

    public static AnalysisResult Failure(string code, string message, string documentType = DocumentTypes.Unknown)
        => new()
        {
            Success = false,
            DocumentType = documentType,
            Error = new ErrorInfo(code, message),
        };
}
