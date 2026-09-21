using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaxCertificate.Application.Interfaces;
using TaxCertificate.Application.Models;

namespace TaxCertificate.Infrastructure.Ocr;

/// <summary>
/// Typed HttpClient over the local Python OCR service.
/// Forwards the upload as multipart/form-data and maps the reply onto the domain model.
/// </summary>
public sealed class OcrClient : IOcrClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly OcrOptions _options;
    private readonly ILogger<OcrClient> _logger;

    public OcrClient(HttpClient httpClient, IOptions<OcrOptions> options, ILogger<OcrClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<OcrDocument> RecognizeAsync(
        Stream content,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var form = new MultipartFormDataContent();
        using var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);

        // The client-supplied name is never reused as a path; a fixed neutral name is sent
        // instead so nothing traverses and nothing leaks into the OCR service's logs.
        form.Add(fileContent, "file", SafeFileName(fileName));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync("/ocr", form, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller gave up (client disconnect): propagate rather than relabel as a timeout.
            throw;
        }
        catch (TaskCanceledException ex)
        {
            throw OcrClientException.Timeout(ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "OCR service request failed");
            throw OcrClientException.Unavailable(ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await BuildFailureAsync(response, cancellationToken).ConfigureAwait(false);
            }

            OcrServiceResponse? payload;
            try
            {
                payload = await response.Content
                    .ReadFromJsonAsync<OcrServiceResponse>(JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "OCR service returned a malformed payload");
                throw OcrClientException.Failed("OCR service returned an unreadable response.");
            }

            if (payload is null || !payload.Success)
            {
                throw OcrClientException.Failed("OCR service reported a failure.");
            }

            _logger.LogInformation(
                "OCR completed: engine={Engine} pages={PageCount} ocrDurationMs={OcrDurationMs}",
                payload.Engine, payload.Pages?.Count ?? 0, payload.DurationMs);

            return Map(payload);
        }
    }

    public async Task<OcrHealthStatus> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync("/health", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                // Answered, so it is reachable - it just is not ready.
                return new OcrHealthStatus(Reachable: true, ModelLoaded: false);
            }

            var health = await response.Content
                .ReadFromJsonAsync<OcrServiceHealthResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return new OcrHealthStatus(Reachable: true, ModelLoaded: health?.ModelLoaded == true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "OCR health probe failed");
            return OcrHealthStatus.Unreachable;
        }
    }

    private async Task<OcrClientException> BuildFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string? code = null;
        try
        {
            var error = await response.Content
                .ReadFromJsonAsync<OcrServiceErrorResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            code = error?.Error?.Code;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // Non-JSON error body; the status code alone drives the mapping below.
        }

        _logger.LogWarning(
            "OCR service returned {StatusCode} with code {OcrErrorCode}",
            (int)response.StatusCode, code ?? "<none>");

        return response.StatusCode switch
        {
            HttpStatusCode.ServiceUnavailable => OcrClientException.Unavailable(),
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => OcrClientException.Timeout(),
            _ => new OcrClientException(code ?? "OCR_FAILED", "OCR service could not process the document."),
        };
    }

    private static string SafeFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length is > 1 and <= 5 && extension.All(c => char.IsAsciiLetterOrDigit(c) || c == '.')
            ? "upload" + extension.ToLowerInvariant()
            : "upload";
    }

    private static OcrDocument Map(OcrServiceResponse payload)
    {
        var pages = new List<OcrPage>();
        var blockIndex = 0;

        foreach (var page in payload.Pages ?? [])
        {
            var blocks = new List<OcrBlock>();

            foreach (var block in page.Blocks ?? [])
            {
                if (string.IsNullOrWhiteSpace(block.Text))
                {
                    continue;
                }

                var polygon = MapPolygon(block.Polygon);

                // Prefer the explicit rectangle; fall back to the polygon extent when absent.
                var box = block.Box is not null
                    ? new BoundingBox(block.Box.X1, block.Box.Y1, block.Box.X2, block.Box.Y2)
                    : polygon is not null
                        ? BoundingBox.FromPolygon(polygon)
                        : null;

                if (box is null)
                {
                    continue;
                }

                blocks.Add(new OcrBlock
                {
                    Index = blockIndex++,
                    PageNumber = page.Page,
                    Text = block.Text,
                    Confidence = Math.Clamp(block.Confidence, 0, 1),
                    Box = box,
                    Polygon = polygon,
                });
            }

            pages.Add(new OcrPage
            {
                PageNumber = page.Page,
                Width = page.Width,
                Height = page.Height,
                Blocks = blocks,
                AppliedRotation = page.AppliedRotation,
            });
        }

        return new OcrDocument { Pages = pages };
    }

    private static IReadOnlyList<(double X, double Y)>? MapPolygon(List<List<double>>? polygon)
    {
        if (polygon is null || polygon.Count == 0)
        {
            return null;
        }

        var points = new List<(double X, double Y)>(polygon.Count);
        foreach (var point in polygon)
        {
            if (point.Count >= 2)
            {
                points.Add((point[0], point[1]));
            }
        }

        return points.Count > 0 ? points : null;
    }
}
