using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaxCertificate.Application.Interfaces;
using TaxCertificate.Infrastructure.Ocr;
using TaxCertificate.UnitTests.Helpers;
using Xunit;

namespace TaxCertificate.UnitTests;

public class OcrClientTests
{
    private const string SampleResponse = """
    {
      "success": true,
      "duration_ms": 4200,
      "engine": "PP-OCRv5/PP-OCRv5_mobile_det+latin_PP-OCRv5_mobile_rec",
      "pages": [
        {
          "page": 1,
          "width": 1131,
          "height": 1600,
          "applied_rotation": 0.0,
          "blocks": [
            {
              "text": "VERGİ KİMLİK NUMARASI",
              "confidence": 0.9762,
              "box": {"x1": 99, "y1": 329, "x2": 409, "y2": 360},
              "polygon": [[99,329],[409,329],[409,360],[99,360]]
            },
            {
              "text": "4540536920",
              "confidence": 1.0,
              "box": {"x1": 135, "y1": 370, "x2": 289, "y2": 398},
              "polygon": [[135,370],[289,370],[289,398],[135,398]]
            }
          ]
        }
      ]
    }
    """;

    private static OcrClient Create(StubHttpMessageHandler handler, OcrOptions? options = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8001") };
        return new OcrClient(
            httpClient,
            Options.Create(options ?? new OcrOptions()),
            NullLogger<OcrClient>.Instance);
    }

    private static MemoryStream Payload() => new([0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4]);

    [Fact]
    public async Task RecognizeAsync_maps_blocks_into_the_domain_model()
    {
        var client = Create(StubHttpMessageHandler.Json(SampleResponse));

        var document = await client.RecognizeAsync(Payload(), "levha.png", "image/png", default);

        var page = Assert.Single(document.Pages);
        Assert.Equal(1, page.PageNumber);
        Assert.Equal(1131, page.Width);
        Assert.Equal(1600, page.Height);
        Assert.Equal(2, page.Blocks.Count);

        var label = page.Blocks[0];
        Assert.Equal("VERGİ KİMLİK NUMARASI", label.Text);
        Assert.Equal(0.9762, label.Confidence, 4);
        Assert.Equal(99, label.Box.X1);
        Assert.Equal(360, label.Box.Y2);
        Assert.Equal(4, label.Polygon!.Count);

        // Indices must be unique and sequential: the parser keys consumed blocks by them.
        Assert.Equal([0, 1], page.Blocks.Select(b => b.Index).ToArray());
    }

    [Fact]
    public async Task RecognizeAsync_falls_back_to_the_polygon_when_no_box_is_present()
    {
        const string body = """
        {"success": true, "pages": [{"page": 1, "width": 100, "height": 100, "blocks": [
          {"text": "ADRES", "confidence": 0.9, "polygon": [[10,20],[60,22],[61,48],[11,46]]}
        ]}]}
        """;

        var document = await Create(StubHttpMessageHandler.Json(body))
            .RecognizeAsync(Payload(), "x.png", "image/png", default);

        var block = Assert.Single(document.Pages[0].Blocks);
        Assert.Equal(10, block.Box.X1);
        Assert.Equal(20, block.Box.Y1);
        Assert.Equal(61, block.Box.X2);
        Assert.Equal(48, block.Box.Y2);
    }

    [Fact]
    public async Task RecognizeAsync_skips_blocks_with_no_usable_text_or_geometry()
    {
        const string body = """
        {"success": true, "pages": [{"page": 1, "width": 10, "height": 10, "blocks": [
          {"text": "   ", "confidence": 0.9, "box": {"x1":0,"y1":0,"x2":5,"y2":5}},
          {"text": "OK", "confidence": 0.9, "box": {"x1":0,"y1":0,"x2":5,"y2":5}},
          {"text": "NO GEOMETRY", "confidence": 0.9}
        ]}]}
        """;

        var document = await Create(StubHttpMessageHandler.Json(body))
            .RecognizeAsync(Payload(), "x.png", "image/png", default);

        var block = Assert.Single(document.Pages[0].Blocks);
        Assert.Equal("OK", block.Text);
    }

    [Fact]
    public async Task RecognizeAsync_clamps_out_of_range_confidence()
    {
        const string body = """
        {"success": true, "pages": [{"page": 1, "width": 10, "height": 10, "blocks": [
          {"text": "A", "confidence": 1.4, "box": {"x1":0,"y1":0,"x2":5,"y2":5}},
          {"text": "B", "confidence": -0.2, "box": {"x1":0,"y1":6,"x2":5,"y2":9}}
        ]}]}
        """;

        var document = await Create(StubHttpMessageHandler.Json(body))
            .RecognizeAsync(Payload(), "x.png", "image/png", default);

        Assert.Equal(1.0, document.Pages[0].Blocks[0].Confidence);
        Assert.Equal(0.0, document.Pages[0].Blocks[1].Confidence);
    }

    [Fact]
    public async Task RecognizeAsync_reports_the_service_as_unavailable_on_a_connection_failure()
    {
        var client = Create(StubHttpMessageHandler.Throws(new HttpRequestException("refused")));

        var exception = await Assert.ThrowsAsync<OcrClientException>(
            () => client.RecognizeAsync(Payload(), "x.png", "image/png", default));

        Assert.Equal("OCR_SERVICE_UNAVAILABLE", exception.Code);
        // The message must stay generic: no inner text, host or path.
        Assert.DoesNotContain("refused", exception.Message);
    }

    [Fact]
    public async Task RecognizeAsync_reports_a_timeout_distinctly()
    {
        var client = Create(StubHttpMessageHandler.Throws(new TaskCanceledException("timeout")));

        var exception = await Assert.ThrowsAsync<OcrClientException>(
            () => client.RecognizeAsync(Payload(), "x.png", "image/png", default));

        Assert.Equal("OCR_SERVICE_TIMEOUT", exception.Code);
    }

    [Fact]
    public async Task RecognizeAsync_propagates_caller_cancellation_rather_than_calling_it_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var client = Create(StubHttpMessageHandler.Throws(new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.RecognizeAsync(Payload(), "x.png", "image/png", cts.Token));
    }

    [Fact]
    public async Task RecognizeAsync_maps_a_503_onto_unavailable()
    {
        var handler = StubHttpMessageHandler.Json(
            """{"success": false, "error": {"code": "SERVER_BUSY", "message": "full"}}""",
            HttpStatusCode.ServiceUnavailable);

        var exception = await Assert.ThrowsAsync<OcrClientException>(
            () => Create(handler).RecognizeAsync(Payload(), "x.png", "image/png", default));

        Assert.Equal("OCR_SERVICE_UNAVAILABLE", exception.Code);
    }

    [Fact]
    public async Task RecognizeAsync_surfaces_the_service_error_code_for_other_failures()
    {
        var handler = StubHttpMessageHandler.Json(
            """{"success": false, "error": {"code": "INVALID_FILE", "message": "bad"}}""",
            HttpStatusCode.BadRequest);

        var exception = await Assert.ThrowsAsync<OcrClientException>(
            () => Create(handler).RecognizeAsync(Payload(), "x.png", "image/png", default));

        Assert.Equal("INVALID_FILE", exception.Code);
    }

    [Fact]
    public async Task RecognizeAsync_rejects_a_malformed_payload()
    {
        var exception = await Assert.ThrowsAsync<OcrClientException>(
            () => Create(StubHttpMessageHandler.Json("not json at all"))
                .RecognizeAsync(Payload(), "x.png", "image/png", default));

        Assert.Equal("OCR_FAILED", exception.Code);
    }

    [Theory]
    [InlineData("../../../etc/passwd", "upload")]
    [InlineData("/etc/shadow", "upload")]
    [InlineData("levha.png", "upload.png")]
    [InlineData("LEVHA.PDF", "upload.pdf")]
    [InlineData("weird.name.with.dots.jpg", "upload.jpg")]
    [InlineData("no-extension", "upload")]
    public async Task RecognizeAsync_never_forwards_the_client_filename(string given, string expected)
    {
        string? forwarded = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            var multipart = (MultipartFormDataContent)request.Content!;
            forwarded = multipart.First().Headers.ContentDisposition?.FileName?.Trim('"');
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"success": true, "pages": []}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        });

        await Create(handler).RecognizeAsync(Payload(), given, "image/png", default);

        Assert.Equal(expected, forwarded);
    }

    [Fact]
    public async Task CheckHealthAsync_distinguishes_ready_not_ready_and_unreachable()
    {
        var ready = await Create(StubHttpMessageHandler.Json(
            """{"status": "healthy", "modelLoaded": true}""")).CheckHealthAsync(default);
        Assert.True(ready.IsHealthy);
        Assert.True(ready.Reachable);

        var starting = await Create(StubHttpMessageHandler.Json(
            """{"status": "starting", "modelLoaded": false}""")).CheckHealthAsync(default);
        Assert.False(starting.IsHealthy);
        Assert.True(starting.Reachable);

        var down = await Create(StubHttpMessageHandler.Throws(new HttpRequestException()))
            .CheckHealthAsync(default);
        Assert.False(down.Reachable);
        Assert.False(down.ModelLoaded);
    }
}
