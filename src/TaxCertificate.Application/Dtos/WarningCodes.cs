namespace TaxCertificate.Application.Dtos;

/// <summary>Stable warning codes. Clients should branch on these, never on the message text.</summary>
public static class WarningCodes
{
    public const string VknNotFound = "VKN_NOT_FOUND";
    public const string InvalidVknChecksum = "INVALID_VKN_CHECKSUM";
    public const string MultipleVknCandidates = "MULTIPLE_VKN_CANDIDATES";
    public const string VknLabelNotFound = "VKN_LABEL_NOT_FOUND";

    public const string InvalidTcknChecksum = "INVALID_TCKN_CHECKSUM";
    public const string MultipleTcknCandidates = "MULTIPLE_TCKN_CANDIDATES";

    public const string DigitSubstitutionApplied = "DIGIT_SUBSTITUTION_APPLIED";
    public const string DateParseFailed = "DATE_PARSE_FAILED";
    public const string ActivityCodeParseFailed = "ACTIVITY_CODE_PARSE_FAILED";

    public const string LowOcrConfidence = "LOW_OCR_CONFIDENCE";
    public const string LowConfidenceField = "LOW_CONFIDENCE_FIELD";
    public const string LowConfidenceAddress = "LOW_CONFIDENCE_ADDRESS";

    public const string FieldNotFound = "FIELD_NOT_FOUND";
    public const string NoIdentityNumberFound = "NO_IDENTITY_NUMBER_FOUND";
    public const string MultiPageDocument = "MULTI_PAGE_DOCUMENT";
    public const string UnsupportedDocument = "UNSUPPORTED_DOCUMENT";
}

/// <summary>Stable error codes returned in the <c>error</c> envelope.</summary>
public static class ErrorCodes
{
    public const string NotTaxCertificate = "NOT_TAX_CERTIFICATE";
    public const string OcrServiceUnavailable = "OCR_SERVICE_UNAVAILABLE";
    public const string OcrServiceTimeout = "OCR_SERVICE_TIMEOUT";
    public const string OcrFailed = "OCR_FAILED";
    public const string NoTextDetected = "NO_TEXT_DETECTED";
    public const string UnsupportedMediaType = "UNSUPPORTED_MEDIA_TYPE";
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string EmptyFile = "EMPTY_FILE";
    public const string ServerBusy = "SERVER_BUSY";
    public const string InternalError = "INTERNAL_ERROR";
}
