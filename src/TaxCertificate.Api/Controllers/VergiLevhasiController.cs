using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TaxCertificate.Api.Configuration;
using TaxCertificate.Api.Services;
using TaxCertificate.Application.Dtos;
using TaxCertificate.Application.Interfaces;
using TaxCertificate.Infrastructure.Ocr;

namespace TaxCertificate.Api.Controllers;

[ApiController]
[Route("api/vergi-levhasi")]
[Produces("application/json")]
public sealed class VergiLevhasiController : ControllerBase
{
    private readonly ITaxCertificateAnalyzer _analyzer;
    private readonly OcrConcurrencyLimiter _limiter;
    private readonly UploadOptions _uploadOptions;
    private readonly ILogger<VergiLevhasiController> _logger;

    public VergiLevhasiController(
        ITaxCertificateAnalyzer analyzer,
        OcrConcurrencyLimiter limiter,
        IOptions<UploadOptions> uploadOptions,
        ILogger<VergiLevhasiController> logger)
    {
        _analyzer = analyzer;
        _limiter = limiter;
        _uploadOptions = uploadOptions.Value;
        _logger = logger;
    }

    /// <summary>Runs OCR on a vergi levhası and returns the extracted fields.</summary>
    /// <remarks>Everything happens locally: no cloud OCR service is contacted.</remarks>
    [HttpPost("analyze")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(AnalysisResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AnalysisResult), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AnalysisResult), StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType(typeof(AnalysisResult), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(AnalysisResult), StatusCodes.Status503ServiceUnavailable)]
    [RequestFormLimits(MultipartBodyLengthLimit = 10 * 1024 * 1024)]
    [RequestSizeLimit(10 * 1024 * 1024)]
    // No [FromForm] attribute on purpose: Swashbuckle refuses to describe an IFormFile
    // parameter annotated that way ("[FromForm] attribute used with IFormFile") and throws
    // while generating the document. ASP.NET Core binds IFormFile from the multipart body by
    // parameter name anyway, so the form field "file" maps to this parameter as-is.
    public async Task<IActionResult> Analyze(
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(AnalysisResult.Failure(ErrorCodes.EmptyFile, "Dosya gönderilmedi veya boş."));
        }

        await using var upload = file.OpenReadStream();

        // Read only the header first: type is decided from the bytes, never from the
        // client-declared Content-Type or the filename.
        var header = new byte[FileTypeValidator.RequiredHeaderBytes];
        var read = await ReadAtLeastAsync(upload, header, cancellationToken).ConfigureAwait(false);

        var validation = FileTypeValidator.Validate(
            header.AsSpan(0, read), file.Length, _uploadOptions.MaxFileSizeBytes);

        if (!validation.IsValid)
        {
            // Filename is intentionally absent from this log line: it is untrusted input.
            _logger.LogWarning(
                "Upload rejected: code={ErrorCode} declaredContentType={DeclaredContentType} bytes={FileSize}",
                validation.ErrorCode, file.ContentType, file.Length);

            var failure = AnalysisResult.Failure(validation.ErrorCode!, validation.Message!);
            return validation.ErrorCode switch
            {
                ErrorCodes.UnsupportedMediaType => StatusCode(StatusCodes.Status415UnsupportedMediaType, failure),
                ErrorCodes.FileTooLarge => StatusCode(StatusCodes.Status413PayloadTooLarge, failure),
                _ => BadRequest(failure),
            };
        }

        // Rewind so the OCR client forwards the whole file, header included.
        var payload = await BufferAsync(header, read, upload, cancellationToken).ConfigureAwait(false);

        using var lease = await _limiter.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (lease is null)
        {
            _logger.LogWarning("Analysis rejected: concurrency queue timed out");
            return StatusCode(
                StatusCodes.Status503ServiceUnavailable,
                AnalysisResult.Failure(ErrorCodes.ServerBusy, "Sunucu meşgul, lütfen tekrar deneyin."));
        }

        _logger.LogInformation(
            "Analysis started: kind={UploadKind} bytes={FileSize}", validation.Kind, file.Length);

        try
        {
            var result = await _analyzer
                .AnalyzeAsync(payload, "upload", FileTypeValidator.ToContentType(validation.Kind), cancellationToken)
                .ConfigureAwait(false);

            if (result.Success)
            {
                return Ok(result);
            }

            // A readable document that simply is not a vergi levhası is a semantic rejection,
            // not a malformed request.
            return result.Error?.Code == ErrorCodes.NotTaxCertificate
                ? UnprocessableEntity(result)
                : BadRequest(result);
        }
        catch (OcrClientException ex)
        {
            _logger.LogError(ex, "OCR pipeline failed with {OcrErrorCode}", ex.Code);

            var failure = AnalysisResult.Failure(ex.Code, ex.Message);
            return ex.Code switch
            {
                ErrorCodes.OcrServiceUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, failure),
                ErrorCodes.OcrServiceTimeout => StatusCode(StatusCodes.Status504GatewayTimeout, failure),
                _ => StatusCode(StatusCodes.Status502BadGateway, failure),
            };
        }
        finally
        {
            await payload.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    /// <summary>
    /// Re-joins the sniffed header with the rest of the upload in a seekable buffer.
    /// Kept in memory rather than a temp file: the size is already capped, and nothing on disk
    /// means nothing to clean up or leak.
    /// </summary>
    private static async Task<Stream> BufferAsync(
        byte[] header, int headerLength, Stream rest, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        await buffer.WriteAsync(header.AsMemory(0, headerLength), ct).ConfigureAwait(false);
        await rest.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;
        return buffer;
    }
}
