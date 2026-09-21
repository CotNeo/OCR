using TaxCertificate.Application.Dtos;
using TaxCertificate.Application.Models;

namespace TaxCertificate.Application.Interfaces;

/// <summary>Transport to the OCR service. The only place that knows OCR is a separate process.</summary>
public interface IOcrClient
{
    Task<OcrDocument> RecognizeAsync(
        Stream content,
        string fileName,
        string contentType,
        CancellationToken cancellationToken);

    /// <summary>Probe result: whether the service answered and reports a loaded model.</summary>
    Task<OcrHealthStatus> CheckHealthAsync(CancellationToken cancellationToken);
}

/// <summary>Outcome of an OCR service health probe.</summary>
/// <param name="Reachable">False when the service could not be contacted at all.</param>
/// <param name="ModelLoaded">True only when the service reports its model is ready.</param>
public readonly record struct OcrHealthStatus(bool Reachable, bool ModelLoaded)
{
    public bool IsHealthy => Reachable && ModelLoaded;

    public static OcrHealthStatus Unreachable { get; } = new(false, false);
}

/// <summary>Pure function: OCR blocks in, structured vergi levhası out. No I/O, no OCR knowledge.</summary>
public interface ITaxCertificateParser
{
    AnalysisResult Parse(OcrDocument document);
}

/// <summary>Orchestrates upload -> OCR -> parse.</summary>
public interface ITaxCertificateAnalyzer
{
    Task<AnalysisResult> AnalyzeAsync(
        Stream content,
        string fileName,
        string contentType,
        CancellationToken cancellationToken);
}
