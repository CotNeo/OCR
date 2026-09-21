using Microsoft.Extensions.Options;
using TaxCertificate.Api.Configuration;
using TaxCertificate.Api.Services;
using TaxCertificate.Application.Dtos;
using TaxCertificate.Application.Services;
using Xunit;

namespace TaxCertificate.UnitTests;

public class FileTypeValidatorTests
{
    private const long MaxSize = 10 * 1024 * 1024;

    private static readonly byte[] PdfHeader = "%PDF-1.7"u8.ToArray();
    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0];

    [Fact]
    public void Accepts_the_three_supported_formats_by_magic_bytes()
    {
        Assert.Equal(UploadKind.Pdf, FileTypeValidator.Validate(PdfHeader, 1024, MaxSize).Kind);
        Assert.Equal(UploadKind.Png, FileTypeValidator.Validate(PngHeader, 1024, MaxSize).Kind);
        Assert.Equal(UploadKind.Jpeg, FileTypeValidator.Validate(JpegHeader, 1024, MaxSize).Kind);
    }

    [Fact]
    public void Rejects_a_file_whose_extension_lies_about_its_content()
    {
        // The whole point of magic-byte sniffing: a .png full of text must not get through.
        var textBytes = "hello world not an image"u8.ToArray();
        var result = FileTypeValidator.Validate(textBytes, textBytes.Length, MaxSize);

        Assert.False(result.IsValid);
        Assert.Equal(ErrorCodes.UnsupportedMediaType, result.ErrorCode);
    }

    [Fact]
    public void Rejects_empty_and_oversized_uploads()
    {
        Assert.Equal(ErrorCodes.EmptyFile, FileTypeValidator.Validate(PngHeader, 0, MaxSize).ErrorCode);
        Assert.Equal(ErrorCodes.FileTooLarge,
            FileTypeValidator.Validate(PngHeader, MaxSize + 1, MaxSize).ErrorCode);
    }

    [Fact]
    public void Rejects_a_truncated_header()
    {
        // Only two bytes of a PNG signature is not a PNG.
        var truncated = new byte[] { 0x89, 0x50 };
        Assert.False(FileTypeValidator.Validate(truncated, 2, MaxSize).IsValid);
    }

    [Theory]
    [InlineData(UploadKind.Pdf, "application/pdf")]
    [InlineData(UploadKind.Png, "image/png")]
    [InlineData(UploadKind.Jpeg, "image/jpeg")]
    public void Maps_each_kind_to_a_content_type(UploadKind kind, string expected)
        => Assert.Equal(expected, FileTypeValidator.ToContentType(kind));
}

public class PiiMaskingTests
{
    [Theory]
    [InlineData("4540536920", "******6920")]
    [InlineData("17291716060", "*******6060")]
    [InlineData("1234", "****")]
    [InlineData("12", "**")]
    public void MaskIdentityNumber_keeps_only_the_last_four_digits(string input, string expected)
        => Assert.Equal(expected, PiiMasking.MaskIdentityNumber(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MaskIdentityNumber_passes_through_absent_values(string? input)
        => Assert.Equal(input, PiiMasking.MaskIdentityNumber(input));

    [Fact]
    public void Redact_reports_a_length_and_never_the_content()
    {
        Assert.Equal("<8 chars>", PiiMasking.Redact("ÜMRANİYE"));
        Assert.Equal("<empty>", PiiMasking.Redact(null));
        Assert.DoesNotContain("ÜMRAN", PiiMasking.Redact("ÜMRANİYE"));
    }
}

public class OcrConcurrencyLimiterTests
{
    private static OcrConcurrencyLimiter Create(int permits, int queueTimeoutSeconds = 1)
        => new(Options.Create(new UploadOptions
        {
            MaxConcurrentAnalyses = permits,
            ConcurrencyQueueTimeoutSeconds = queueTimeoutSeconds,
        }));

    [Fact]
    public async Task Grants_up_to_the_configured_number_of_permits()
    {
        using var limiter = Create(permits: 2);

        using var first = await limiter.AcquireAsync(default);
        using var second = await limiter.AcquireAsync(default);

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Returns_null_when_the_queue_timeout_elapses()
    {
        using var limiter = Create(permits: 1);

        using var held = await limiter.AcquireAsync(default);
        Assert.NotNull(held);

        // No permit is free, so the next caller must be shed rather than wait forever.
        var rejected = await limiter.AcquireAsync(default);
        Assert.Null(rejected);
    }

    [Fact]
    public async Task Releases_the_permit_when_the_lease_is_disposed()
    {
        using var limiter = Create(permits: 1);

        var first = await limiter.AcquireAsync(default);
        Assert.NotNull(first);
        first!.Dispose();

        using var second = await limiter.AcquireAsync(default);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task A_double_dispose_does_not_release_the_permit_twice()
    {
        using var limiter = Create(permits: 1);

        var lease = await limiter.AcquireAsync(default);
        Assert.NotNull(lease);
        lease!.Dispose();
        lease.Dispose();

        // Exactly one permit must be available, not two.
        using var again = await limiter.AcquireAsync(default);
        Assert.NotNull(again);

        var beyond = await limiter.AcquireAsync(default);
        Assert.Null(beyond);
    }
}
