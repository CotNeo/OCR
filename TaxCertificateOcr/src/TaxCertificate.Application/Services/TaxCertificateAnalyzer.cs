using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaxCertificate.Application.Configuration;
using TaxCertificate.Application.Dtos;
using TaxCertificate.Application.Interfaces;
using TaxCertificate.Application.Models;

namespace TaxCertificate.Application.Services;

/// <summary>
/// Orchestrates the pipeline: forward the upload to OCR, parse the blocks, attach raw output
/// when explicitly enabled. Holds no state, so it is safe as a singleton.
/// </summary>
public sealed class TaxCertificateAnalyzer : ITaxCertificateAnalyzer
{
    private readonly IOcrClient _ocrClient;
    private readonly ITaxCertificateParser _parser;
    private readonly AnalyzerOptions _options;
    private readonly ILogger<TaxCertificateAnalyzer> _logger;

    public TaxCertificateAnalyzer(
        IOcrClient ocrClient,
        ITaxCertificateParser parser,
        IOptions<AnalyzerOptions> options,
        ILogger<TaxCertificateAnalyzer> logger)
    {
        _ocrClient = ocrClient;
        _parser = parser;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AnalysisResult> AnalyzeAsync(
        Stream content,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        var ocrTimer = Stopwatch.StartNew();
        var document = await _ocrClient
            .RecognizeAsync(content, fileName, contentType, cancellationToken)
            .ConfigureAwait(false);
        ocrTimer.Stop();

        var blockCount = document.Pages.Sum(p => p.Blocks.Count);
        if (blockCount == 0)
        {
            _logger.LogWarning("OCR produced no text blocks; ocrDurationMs={OcrDurationMs}",
                ocrTimer.ElapsedMilliseconds);

            return AnalysisResult.Failure(
                ErrorCodes.NoTextDetected, "Belgede metin tespit edilemedi.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var parseTimer = Stopwatch.StartNew();
        var result = _parser.Parse(document);
        parseTimer.Stop();

        // Structured, and deliberately free of document content: no raw OCR text, and identity
        // numbers only in masked form.
        _logger.LogInformation(
            "Analysis finished: success={Success} documentType={DocumentType} pages={PageCount} " +
            "blocks={BlockCount} ocrDurationMs={OcrDurationMs} parseDurationMs={ParseDurationMs} " +
            "overallConfidence={OverallConfidence} warnings={WarningCodes} vkn={MaskedVkn} tckn={MaskedTckn}",
            result.Success,
            result.DocumentType,
            document.Pages.Count,
            blockCount,
            ocrTimer.ElapsedMilliseconds,
            parseTimer.ElapsedMilliseconds,
            result.Confidence?.Overall ?? 0,
            string.Join(",", result.Warnings.Select(w => w.Code)),
            PiiMasking.MaskIdentityNumber(result.Data?.Vkn) ?? "<none>",
            PiiMasking.MaskIdentityNumber(result.Data?.Tckn) ?? "<none>");

        return _options.IncludeRawResult
            ? result with { RawOcr = ToRawBlocks(document) }
            : result;
    }

    private static List<RawOcrBlock> ToRawBlocks(OcrDocument document)
        => document.AllBlocks
            .Select(b => new RawOcrBlock(
                b.PageNumber, b.Text, Math.Round(b.Confidence, 4),
                b.Box.X1, b.Box.Y1, b.Box.X2, b.Box.Y2))
            .ToList();
}

/// <summary>Analyzer-level switches, bound from the same <c>Ocr</c> configuration section.</summary>
public sealed class AnalyzerOptions
{
    /// <summary>Mirrors <c>Ocr:IncludeRawResult</c>. Must stay false in production responses.</summary>
    public bool IncludeRawResult { get; set; }
}
