using System.Text.Json.Serialization;

namespace TaxCertificate.Infrastructure.Ocr;

// Wire shape of the Python service response. Kept internal to this assembly: the rest of the
// solution only ever sees the domain model in TaxCertificate.Application.Models.

internal sealed record OcrServiceBox
{
    [JsonPropertyName("x1")] public double X1 { get; init; }
    [JsonPropertyName("y1")] public double Y1 { get; init; }
    [JsonPropertyName("x2")] public double X2 { get; init; }
    [JsonPropertyName("y2")] public double Y2 { get; init; }
}

internal sealed record OcrServiceBlock
{
    [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;
    [JsonPropertyName("confidence")] public double Confidence { get; init; }
    [JsonPropertyName("box")] public OcrServiceBox? Box { get; init; }
    [JsonPropertyName("polygon")] public List<List<double>>? Polygon { get; init; }
}

internal sealed record OcrServicePage
{
    [JsonPropertyName("page")] public int Page { get; init; }
    [JsonPropertyName("width")] public int Width { get; init; }
    [JsonPropertyName("height")] public int Height { get; init; }
    [JsonPropertyName("applied_rotation")] public double AppliedRotation { get; init; }
    [JsonPropertyName("blocks")] public List<OcrServiceBlock>? Blocks { get; init; }
}

internal sealed record OcrServiceResponse
{
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("pages")] public List<OcrServicePage>? Pages { get; init; }
    [JsonPropertyName("duration_ms")] public int DurationMs { get; init; }
    [JsonPropertyName("engine")] public string? Engine { get; init; }
}

internal sealed record OcrServiceErrorBody
{
    [JsonPropertyName("code")] public string? Code { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}

internal sealed record OcrServiceErrorResponse
{
    [JsonPropertyName("error")] public OcrServiceErrorBody? Error { get; init; }
}

internal sealed record OcrServiceHealthResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("modelLoaded")] public bool ModelLoaded { get; init; }
}
