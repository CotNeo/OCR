using TaxCertificate.Application.Dtos;

namespace TaxCertificate.Api.Services;

public enum UploadKind
{
    Pdf,
    Jpeg,
    Png,
}

public sealed record UploadValidationResult(bool IsValid, UploadKind Kind, string? ErrorCode, string? Message)
{
    public static UploadValidationResult Ok(UploadKind kind) => new(true, kind, null, null);

    public static UploadValidationResult Fail(string code, string message)
        => new(false, default, code, message);
}

/// <summary>
/// Validates uploads by inspecting the leading bytes.
/// <para>
/// The declared Content-Type and the filename are attacker-controlled, so neither is trusted:
/// the magic bytes decide. The filename is never used to build a path.
/// </para>
/// </summary>
public static class FileTypeValidator
{
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];

    /// <summary>Longest signature we need to see before deciding.</summary>
    public const int RequiredHeaderBytes = 8;

    public static UploadValidationResult Validate(ReadOnlySpan<byte> header, long length, long maxFileSizeBytes)
    {
        if (length == 0)
        {
            return UploadValidationResult.Fail(ErrorCodes.EmptyFile, "Yüklenen dosya boş.");
        }

        if (length > maxFileSizeBytes)
        {
            return UploadValidationResult.Fail(
                ErrorCodes.FileTooLarge,
                $"Dosya boyutu {maxFileSizeBytes} bayt sınırını aşıyor.");
        }

        if (header.StartsWith(PdfMagic))
        {
            return UploadValidationResult.Ok(UploadKind.Pdf);
        }

        if (header.StartsWith(PngMagic))
        {
            return UploadValidationResult.Ok(UploadKind.Png);
        }

        if (header.StartsWith(JpegMagic))
        {
            return UploadValidationResult.Ok(UploadKind.Jpeg);
        }

        return UploadValidationResult.Fail(
            ErrorCodes.UnsupportedMediaType,
            "Yalnızca PDF, JPEG ve PNG dosyaları desteklenir.");
    }

    public static string ToContentType(UploadKind kind) => kind switch
    {
        UploadKind.Pdf => "application/pdf",
        UploadKind.Png => "image/png",
        UploadKind.Jpeg => "image/jpeg",
        _ => "application/octet-stream",
    };
}
