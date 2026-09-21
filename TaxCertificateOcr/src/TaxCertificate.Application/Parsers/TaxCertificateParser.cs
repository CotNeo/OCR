using Microsoft.Extensions.Options;
using TaxCertificate.Application.Configuration;
using TaxCertificate.Application.Dtos;
using TaxCertificate.Application.Interfaces;
using TaxCertificate.Application.Models;
using TaxCertificate.Application.Text;
using TaxCertificate.Application.Validators;

namespace TaxCertificate.Application.Parsers;

/// <summary>
/// Extracts vergi levhası fields from OCR blocks in three stages:
/// <list type="number">
///   <item>text normalisation (fold Turkish characters for comparison only),</item>
///   <item>label detection (alias catalogue + bounded fuzzy match),</item>
///   <item>spatial value matching (score candidates by geometry, format and OCR confidence).</item>
/// </list>
/// Deterministic throughout: the same blocks always yield the same result.
/// </summary>
public sealed class TaxCertificateParser : ITaxCertificateParser
{
    private readonly ParserOptions _options;

    public TaxCertificateParser(IOptions<ParserOptions> options)
        => _options = options.Value;

    public TaxCertificateParser(ParserOptions options)
        => _options = options;

    public AnalysisResult Parse(OcrDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var workspace = new ParserWorkspace(document);
        var warnings = new List<AnalysisWarning>();

        var detection = TaxCertificateDetector.Detect(document);
        if (!detection.IsTaxCertificate(_options.MinDocumentTypeScore))
        {
            return AnalysisResult.Failure(
                ErrorCodes.NotTaxCertificate,
                "Belge vergi levhası olarak tanınamadı.") with
            {
                Confidence = new ConfidenceReport { Overall = 0, DocumentType = detection.Score },
                Warnings = [new AnalysisWarning(
                    WarningCodes.UnsupportedDocument,
                    $"Vergi levhası kanıt skoru {detection.Score:0.00}, eşik {_options.MinDocumentTypeScore:0.00}.")],
            };
        }

        if (document.Pages.Count > 1)
        {
            warnings.Add(new AnalysisWarning(
                WarningCodes.MultiPageDocument,
                $"Belge {document.Pages.Count} sayfa içeriyor; tüm sayfalar birlikte değerlendirildi."));
        }

        // Identity numbers first: they are the highest-value fields and claiming their blocks
        // early keeps looser text fields (address) from absorbing them.
        var vkn = ExtractVkn(workspace, warnings);
        var tckn = ExtractTckn(workspace, warnings, vkn?.Value);

        if (vkn is null && tckn is null)
        {
            warnings.Add(new AnalysisWarning(
                WarningCodes.NoIdentityNumberFound,
                "Belgede geçerli bir VKN veya TCKN bulunamadı."));
        }

        var vergiDairesi = ExtractSimpleText(workspace, TaxCertificateField.VergiDairesi, StripVergiDairesiSuffix);
        var ticaretUnvani = ExtractOrganizationName(workspace);
        var adiSoyadi = ExtractPersonName(workspace);
        var iseBaslama = ExtractDate(workspace, warnings);
        var faaliyetKodu = ExtractActivityCode(workspace, warnings);
        var faaliyetAciklamasi = ExtractActivityDescription(workspace);
        var adres = ExtractAddress(workspace);

        var data = new TaxCertificateData
        {
            Vkn = vkn?.Value,
            Tckn = tckn?.Value,
            TicaretUnvani = ticaretUnvani?.Value,
            AdiSoyadi = adiSoyadi?.Value,
            VergiDairesi = vergiDairesi?.Value,
            Adres = adres?.Value,
            IseBaslamaTarihi = iseBaslama?.Value,
            AnaFaaliyetKodu = faaliyetKodu?.Value,
            AnaFaaliyetKoduRaw = faaliyetKodu?.RawValue,
            AnaFaaliyetAciklamasi = faaliyetAciklamasi?.Value,
        };

        AddLowConfidenceWarnings(warnings, workspace,
        [
            (nameof(data.Vkn), "vkn", vkn),
            (nameof(data.Tckn), "tckn", tckn),
            (nameof(data.TicaretUnvani), "ticaretUnvani", ticaretUnvani),
            (nameof(data.AdiSoyadi), "adiSoyadi", adiSoyadi),
            (nameof(data.VergiDairesi), "vergiDairesi", vergiDairesi),
            (nameof(data.Adres), "adres", adres),
            (nameof(data.IseBaslamaTarihi), "iseBaslamaTarihi", iseBaslama),
            (nameof(data.AnaFaaliyetKodu), "anaFaaliyetKodu", faaliyetKodu),
            (nameof(data.AnaFaaliyetAciklamasi), "anaFaaliyetAciklamasi", faaliyetAciklamasi),
        ]);

        var confidence = BuildConfidence(detection, vkn, tckn, ticaretUnvani, adiSoyadi,
            vergiDairesi, adres, iseBaslama, faaliyetKodu, faaliyetAciklamasi);

        return new AnalysisResult
        {
            Success = true,
            DocumentType = DocumentTypes.VergiLevhasi,
            Data = data,
            Validation = BuildValidation(vkn, tckn, iseBaslama),
            Confidence = confidence,
            Warnings = warnings,
        };
    }

    // ---------------------------------------------------------------- identity numbers

    private FieldExtraction? ExtractVkn(ParserWorkspace workspace, List<AnalysisWarning> warnings)
    {
        var matches = CollectNumberCandidates(workspace, TaxCertificateField.Vkn,
            VergiKimlikNoValidator.Length, VergiKimlikNoValidator.IsValid);

        if (matches.Count == 0 && _options.AllowVknWithoutLabel)
        {
            var fallback = FindUnlabelledIdentityNumber(workspace,
                VergiKimlikNoValidator.Length, VergiKimlikNoValidator.IsValid);

            if (fallback is not null)
            {
                warnings.Add(new AnalysisWarning(
                    WarningCodes.VknLabelNotFound,
                    "VKN etiketi bulunamadı; checksum'ı geçerli 10 haneli numara kullanıldı.",
                    "vkn"));
                workspace.Consume(fallback.Block);
                return fallback;
            }
        }

        if (matches.Count == 0)
        {
            warnings.Add(new AnalysisWarning(WarningCodes.VknNotFound, "VKN bulunamadı.", "vkn"));
            return null;
        }

        var distinct = matches.Select(m => m.Value).Distinct(StringComparer.Ordinal).Count();
        if (distinct > 1)
        {
            warnings.Add(new AnalysisWarning(
                WarningCodes.MultipleVknCandidates,
                $"{distinct} farklı VKN adayı bulundu; en yüksek skorlu olan seçildi.",
                "vkn"));
        }

        var best = matches[0];
        if (!VergiKimlikNoValidator.IsValid(best.Value))
        {
            warnings.Add(new AnalysisWarning(
                WarningCodes.InvalidVknChecksum, "VKN checksum doğrulaması başarısız.", "vkn"));
        }

        if (best.SubstitutionApplied)
        {
            warnings.Add(new AnalysisWarning(
                WarningCodes.DigitSubstitutionApplied,
                "VKN okunurken harf->rakam düzeltmesi uygulandı; değeri doğrulayın.",
                "vkn"));
        }

        workspace.Consume(best.Block);
        return best;
    }

    private FieldExtraction? ExtractTckn(
        ParserWorkspace workspace,
        List<AnalysisWarning> warnings,
        string? vknValue)
    {
        var matches = CollectNumberCandidates(workspace, TaxCertificateField.Tckn,
            TcKimlikNoValidator.Length, TcKimlikNoValidator.IsValid);

        // Never let a VKN leak into the TCKN slot.
        matches = matches.Where(m => !string.Equals(m.Value, vknValue, StringComparison.Ordinal)).ToList();

        if (matches.Count == 0)
        {
            return null;
        }

        if (matches.Select(m => m.Value).Distinct(StringComparer.Ordinal).Count() > 1)
        {
            warnings.Add(new AnalysisWarning(
                WarningCodes.MultipleTcknCandidates,
                "Birden fazla TCKN adayı bulundu; en yüksek skorlu olan seçildi.",
                "tckn"));
        }

        var best = matches[0];
        if (!TcKimlikNoValidator.IsValid(best.Value))
        {
            warnings.Add(new AnalysisWarning(
                WarningCodes.InvalidTcknChecksum, "TCKN checksum doğrulaması başarısız.", "tckn"));
        }

        if (best.SubstitutionApplied)
        {
            warnings.Add(new AnalysisWarning(
                WarningCodes.DigitSubstitutionApplied,
                "TCKN okunurken harf->rakam düzeltmesi uygulandı; değeri doğrulayın.",
                "tckn"));
        }

        workspace.Consume(best.Block);
        return best;
    }

    /// <summary>
    /// Scores every digit run of the expected length that sits inline with, below or beside the label.
    /// Checksum-valid candidates outrank invalid ones regardless of geometry.
    /// </summary>
    private List<FieldExtraction> CollectNumberCandidates(
        ParserWorkspace workspace,
        TaxCertificateField field,
        int expectedLength,
        Func<string, bool> checksumValidator)
    {
        var results = new List<FieldExtraction>();

        foreach (var (labelBlock, labelMatch) in workspace.LabelsFor(field))
        {
            // Value printed on the same line as the label.
            if (labelMatch.InlineRemainder is not null)
            {
                var inline = DigitExtractor.ExtractFixedLength(
                    labelMatch.InlineRemainder, expectedLength, _options.AllowDigitSubstitution);

                if (inline.HasValue)
                {
                    results.Add(BuildNumberExtraction(labelBlock, labelMatch, inline, 1.0,
                        FieldConfidence.StrategyInlineWithLabel, checksumValidator));
                }
            }

            foreach (var candidate in SpatialMatcher.FindCandidates(labelBlock, workspace.AvailableValues()))
            {
                var extraction = DigitExtractor.ExtractFixedLength(
                    candidate.Block.Text, expectedLength, _options.AllowDigitSubstitution);

                if (!extraction.HasValue)
                {
                    continue;
                }

                results.Add(BuildNumberExtraction(candidate.Block, labelMatch, extraction,
                    candidate.GeometryScore, FieldConfidence.StrategyLabelAndSpatial, checksumValidator));
            }
        }

        return results
            .OrderByDescending(r => r.ChecksumValid)
            .ThenByDescending(r => r.Confidence)
            .ToList();
    }

    private static FieldExtraction BuildNumberExtraction(
        OcrBlock block,
        LabelMatch labelMatch,
        DigitExtraction extraction,
        double geometryScore,
        double strategyScore,
        Func<string, bool> checksumValidator)
    {
        var checksumValid = checksumValidator(extraction.Digits);
        var parser = FieldConfidence.Parser(
            strategyScore,
            labelMatch.Similarity,
            geometryScore,
            checksumValid ? FieldConfidence.ChecksumValid : FieldConfidence.ChecksumInvalid,
            extraction.SubstitutionApplied ? FieldConfidence.DigitSubstitutionPenalty : 1.0);

        return new FieldExtraction(
            extraction.Digits,
            FieldConfidence.Combine(block.Confidence, parser),
            block)
        {
            ChecksumValid = checksumValid,
            SubstitutionApplied = extraction.SubstitutionApplied,
        };
    }

    /// <summary>
    /// Last resort when no label was recognised: a checksum-valid number is strong enough evidence
    /// on its own. Requires exactly one such candidate, so nothing is picked out of a crowd.
    /// </summary>
    private static FieldExtraction? FindUnlabelledIdentityNumber(
        ParserWorkspace workspace,
        int expectedLength,
        Func<string, bool> checksumValidator)
    {
        var candidates = workspace.AvailableValues()
            .Select(block => (block, extraction: DigitExtractor.ExtractFixedLength(block.Text, expectedLength, false)))
            .Where(x => x.extraction.HasValue && checksumValidator(x.extraction.Digits))
            .ToList();

        if (candidates.Count != 1)
        {
            return null;
        }

        var (winner, value) = candidates[0];
        var parser = FieldConfidence.Parser(
            FieldConfidence.StrategyWithoutLabel, 1.0, 1.0, FieldConfidence.ChecksumValid);

        return new FieldExtraction(value.Digits, FieldConfidence.Combine(winner.Confidence, parser), winner)
        {
            ChecksumValid = true,
        };
    }

    // ---------------------------------------------------------------- text fields

    private FieldExtraction? ExtractSimpleText(
        ParserWorkspace workspace,
        TaxCertificateField field,
        Func<string, string>? postProcess = null)
    {
        foreach (var (labelBlock, labelMatch) in workspace.LabelsFor(field))
        {
            if (labelMatch.InlineRemainder is not null && IsUsableText(labelMatch.InlineRemainder))
            {
                var inlineValue = postProcess?.Invoke(labelMatch.InlineRemainder) ?? labelMatch.InlineRemainder;
                if (IsUsableText(inlineValue))
                {
                    var inlineParser = FieldConfidence.Parser(
                        FieldConfidence.StrategyInlineWithLabel, labelMatch.Similarity, 1.0,
                        FieldConfidence.ChecksumNotApplicable);

                    return new FieldExtraction(
                        inlineValue, FieldConfidence.Combine(labelBlock.Confidence, inlineParser), labelBlock);
                }
            }

            foreach (var candidate in SpatialMatcher.FindCandidates(labelBlock, workspace.AvailableValues()))
            {
                var raw = TurkishTextNormalizer.CleanValue(candidate.Block.Text);
                if (!IsUsableText(raw))
                {
                    continue;
                }

                var value = postProcess?.Invoke(raw) ?? raw;
                if (!IsUsableText(value))
                {
                    continue;
                }

                var parser = FieldConfidence.Parser(
                    FieldConfidence.StrategyLabelAndSpatial, labelMatch.Similarity,
                    candidate.GeometryScore, FieldConfidence.ChecksumNotApplicable);

                workspace.Consume(candidate.Block);
                return new FieldExtraction(
                    value, FieldConfidence.Combine(candidate.Block.Confidence, parser), candidate.Block);
            }
        }

        return null;
    }

    /// <summary>Company markers that confirm a line really is a ticaret unvanı.</summary>
    private static readonly string[] OrganizationMarkers =
    [
        "A S", "AS", "LTD", "STI", "SIRKETI", "ANONIM", "LIMITED", "SANAYI", "TICARET",
        "SAN", "TIC", "KOLLEKTIF", "KOMANDIT", "ORTAKLIGI", "HOLDING", "GRUP",
    ];

    private FieldExtraction? ExtractOrganizationName(ParserWorkspace workspace)
    {
        foreach (var (labelBlock, labelMatch) in workspace.LabelsFor(TaxCertificateField.TicaretUnvani))
        {
            foreach (var candidate in SpatialMatcher.FindCandidates(labelBlock, workspace.AvailableValues()))
            {
                var raw = TurkishTextNormalizer.CleanValue(candidate.Block.Text);
                if (!IsUsableText(raw) || DigitExtractor.DigitsOnly(raw).Length > raw.Length / 2)
                {
                    continue;
                }

                // A unvan often wraps onto the next line; pull in continuation lines only.
                var parts = new List<OcrBlock> { candidate.Block };
                parts.AddRange(SpatialMatcher.CollectColumnBelow(
                    candidate.Block,
                    workspace.AvailableValues().Where(b => !ReferenceEquals(b, candidate.Block)),
                    b => workspace.IsLabel(b) || workspace.IsConsumed(b) || LooksLikeNewField(b),
                    maxLines: 1));

                var value = string.Join(" ", parts.Select(p => TurkishTextNormalizer.CleanValue(p.Text)));
                var normalized = TurkishTextNormalizer.Normalize(value);
                var hasMarker = OrganizationMarkers.Any(m =>
                    normalized.Contains(" " + m + " ", StringComparison.Ordinal) ||
                    normalized.EndsWith(" " + m, StringComparison.Ordinal) ||
                    normalized.StartsWith(m + " ", StringComparison.Ordinal));

                var parser = FieldConfidence.Parser(
                    FieldConfidence.StrategyLabelAndSpatial, labelMatch.Similarity,
                    candidate.GeometryScore, hasMarker ? 1.0 : FieldConfidence.ChecksumNotApplicable);

                foreach (var part in parts)
                {
                    workspace.Consume(part);
                }

                var ocr = FieldConfidence.WeightedOcrConfidence(parts.Select(p => (p.Text, p.Confidence)));
                return new FieldExtraction(value, FieldConfidence.Combine(ocr, parser), candidate.Block);
            }
        }

        return null;
    }

    private FieldExtraction? ExtractPersonName(ParserWorkspace workspace)
        => ExtractSimpleText(workspace, TaxCertificateField.AdiSoyadi, value =>
        {
            // A person name is letters and spaces only; reject anything carrying digits.
            return DigitExtractor.DigitsOnly(value).Length > 0 ? string.Empty : value;
        });

    private static string StripVergiDairesiSuffix(string value)
    {
        // "ÜMRANİYE VERGİ DAİRESİ MÜDÜRLÜĞÜ" -> "ÜMRANİYE": the label already says what it is.
        string[] suffixes = ["VERGI DAIRESI MUDURLUGU", "VERGI DAIRESI", "VD", "V D"];
        var result = value;

        foreach (var suffix in suffixes)
        {
            var normalized = TurkishTextNormalizer.Normalize(result);
            if (!normalized.EndsWith(" " + suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var keepLetters = normalized.Length - suffix.Length - 1;
            result = TakeByLetterCount(result, normalized[..keepLetters].Count(char.IsLetterOrDigit));
            break;
        }

        return TurkishTextNormalizer.CleanValue(result);
    }

    /// <summary>Truncates original text after a given number of letters/digits, preserving characters.</summary>
    private static string TakeByLetterCount(string original, int letterCount)
    {
        if (letterCount <= 0)
        {
            return string.Empty;
        }

        var seen = 0;
        for (var i = 0; i < original.Length; i++)
        {
            if (!char.IsLetterOrDigit(original[i]))
            {
                continue;
            }

            seen++;
            if (seen == letterCount)
            {
                return original[..(i + 1)];
            }
        }

        return original;
    }

    // ---------------------------------------------------------------- date / activity / address

    private FieldExtraction? ExtractDate(ParserWorkspace workspace, List<AnalysisWarning> warnings)
    {
        var labels = workspace.LabelsFor(TaxCertificateField.IseBaslamaTarihi).ToList();
        if (labels.Count == 0)
        {
            return null;
        }

        foreach (var (labelBlock, labelMatch) in labels)
        {
            var sources = new List<(string Text, OcrBlock Block, double Geometry)>();
            if (labelMatch.InlineRemainder is not null)
            {
                sources.Add((labelMatch.InlineRemainder, labelBlock, 1.0));
            }

            sources.AddRange(SpatialMatcher
                .FindCandidates(labelBlock, workspace.AvailableValues())
                .Select(c => (c.Block.Text, c.Block, c.GeometryScore)));

            foreach (var (text, block, geometry) in sources)
            {
                var iso = ValueFormats.TryNormalizeDate(text);
                if (iso is null)
                {
                    continue;
                }

                var parser = FieldConfidence.Parser(
                    ReferenceEquals(block, labelBlock)
                        ? FieldConfidence.StrategyInlineWithLabel
                        : FieldConfidence.StrategyLabelAndSpatial,
                    labelMatch.Similarity, geometry, FieldConfidence.ChecksumValid);

                workspace.Consume(block);
                return new FieldExtraction(iso, FieldConfidence.Combine(block.Confidence, parser), block)
                {
                    ChecksumValid = true,
                };
            }
        }

        warnings.Add(new AnalysisWarning(
            WarningCodes.DateParseFailed,
            "İşe başlama tarihi etiketi bulundu ancak geçerli bir tarihe dönüştürülemedi.",
            "iseBaslamaTarihi"));
        return null;
    }

    private FieldExtraction? ExtractActivityCode(ParserWorkspace workspace, List<AnalysisWarning> warnings)
    {
        var labels = workspace.LabelsFor(TaxCertificateField.AnaFaaliyetKodu).ToList();
        if (labels.Count == 0)
        {
            return null;
        }

        foreach (var (labelBlock, labelMatch) in labels)
        {
            var sources = new List<(string Text, OcrBlock Block, double Geometry)>();
            if (labelMatch.InlineRemainder is not null)
            {
                sources.Add((labelMatch.InlineRemainder, labelBlock, 1.0));
            }

            sources.AddRange(SpatialMatcher
                .FindCandidates(labelBlock, workspace.AvailableValues())
                .Select(c => (c.Block.Text, c.Block, c.GeometryScore)));

            foreach (var (text, block, geometry) in sources)
            {
                var raw = TurkishTextNormalizer.CleanValue(text);
                var normalized = ValueFormats.TryNormalizeActivityCode(raw);
                if (normalized is null)
                {
                    continue;
                }

                var parser = FieldConfidence.Parser(
                    ReferenceEquals(block, labelBlock)
                        ? FieldConfidence.StrategyInlineWithLabel
                        : FieldConfidence.StrategyLabelAndSpatial,
                    labelMatch.Similarity, geometry,
                    normalized.Length == 6 ? 1.0 : FieldConfidence.ChecksumNotApplicable);

                workspace.Consume(block);
                return new FieldExtraction(
                    normalized, FieldConfidence.Combine(block.Confidence, parser), block)
                {
                    RawValue = raw,
                };
            }
        }

        warnings.Add(new AnalysisWarning(
            WarningCodes.ActivityCodeParseFailed,
            "Ana faaliyet kodu etiketi bulundu ancak geçerli bir koda dönüştürülemedi.",
            "anaFaaliyetKodu"));
        return null;
    }

    private FieldExtraction? ExtractActivityDescription(ParserWorkspace workspace)
        => ExtractSimpleText(workspace, TaxCertificateField.AnaFaaliyetAciklamasi, value =>
        {
            // The description is prose; a block that is mostly digits is the code, not the text.
            var digits = DigitExtractor.DigitsOnly(value).Length;
            return digits * 2 > value.Length ? string.Empty : value;
        });

    private FieldExtraction? ExtractAddress(ParserWorkspace workspace)
    {
        foreach (var (labelBlock, labelMatch) in workspace.LabelsFor(TaxCertificateField.Adres))
        {
            var parts = new List<OcrBlock>();
            OcrBlock anchor = labelBlock;

            // Inline first line ("ADRES: SARAY MAH. ...").
            var inline = labelMatch.InlineRemainder;

            if (inline is null)
            {
                var first = SpatialMatcher
                    .FindCandidates(labelBlock, workspace.AvailableValues())
                    .FirstOrDefault(c => IsUsableText(TurkishTextNormalizer.CleanValue(c.Block.Text)));

                if (first is null)
                {
                    continue;
                }

                parts.Add(first.Block);
                anchor = first.Block;
            }

            parts.AddRange(SpatialMatcher.CollectColumnBelow(
                anchor,
                workspace.AvailableValues().Where(b => !parts.Contains(b)),
                b => workspace.IsLabel(b) || workspace.IsConsumed(b) || LooksLikeNewField(b),
                maxLines: Math.Max(0, _options.MaxAddressLines - parts.Count)));

            var lines = new List<string>();
            if (inline is not null)
            {
                lines.Add(inline);
            }

            lines.AddRange(parts.Select(p => TurkishTextNormalizer.CleanValue(p.Text)));

            var value = string.Join(" ", lines.Where(l => !string.IsNullOrWhiteSpace(l)));
            if (!IsUsableText(value))
            {
                continue;
            }

            foreach (var part in parts)
            {
                workspace.Consume(part);
            }

            var confidenceParts = parts.Select(p => (p.Text, p.Confidence)).ToList();
            if (inline is not null)
            {
                confidenceParts.Add((inline, labelBlock.Confidence));
            }

            var ocr = FieldConfidence.WeightedOcrConfidence(confidenceParts);
            var parser = FieldConfidence.Parser(
                FieldConfidence.StrategyLabelAndSpatial, labelMatch.Similarity,
                0.9, FieldConfidence.ChecksumNotApplicable);

            return new FieldExtraction(value, FieldConfidence.Combine(ocr, parser), anchor);
        }

        return null;
    }

    /// <summary>
    /// A block that reads like the start of a different field (a bare identity number, a date).
    /// Used as a stop condition so multi-line collection never crosses into another section.
    /// </summary>
    private static bool LooksLikeNewField(OcrBlock block)
    {
        var digits = DigitExtractor.DigitsOnly(block.Text);
        if (digits.Length is VergiKimlikNoValidator.Length or TcKimlikNoValidator.Length &&
            digits.Length == TurkishTextNormalizer.NormalizeCompact(block.Text).Length)
        {
            return true;
        }

        return ValueFormats.TryNormalizeDate(TurkishTextNormalizer.CleanValue(block.Text)) is not null;
    }

    private static bool IsUsableText(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length >= 2;

    // ---------------------------------------------------------------- reporting

    private static ValidationReport BuildValidation(
        FieldExtraction? vkn,
        FieldExtraction? tckn,
        FieldExtraction? date)
        => new()
        {
            Vkn = vkn is null
                ? null
                : new FieldValidation(vkn.ChecksumValid, vkn.ChecksumValid ? null : "CHECKSUM_FAILED"),
            Tckn = tckn is null
                ? null
                : new FieldValidation(tckn.ChecksumValid, tckn.ChecksumValid ? null : "CHECKSUM_FAILED"),
            IseBaslamaTarihi = date is null ? null : new FieldValidation(true),
        };

    /// <summary>
    /// Per-field weights for the overall score. Identity numbers dominate because they are what
    /// the document is for; the description is nearly decorative.
    /// </summary>
    private static readonly (string Key, double Weight)[] OverallWeights =
    [
        ("vkn", 0.25), ("tckn", 0.25), ("ticaretUnvani", 0.15), ("adiSoyadi", 0.15),
        ("vergiDairesi", 0.15), ("adres", 0.15), ("iseBaslamaTarihi", 0.10),
        ("anaFaaliyetKodu", 0.10), ("anaFaaliyetAciklamasi", 0.05),
    ];

    private static ConfidenceReport BuildConfidence(
        DocumentDetectionResult detection,
        FieldExtraction? vkn,
        FieldExtraction? tckn,
        FieldExtraction? ticaretUnvani,
        FieldExtraction? adiSoyadi,
        FieldExtraction? vergiDairesi,
        FieldExtraction? adres,
        FieldExtraction? iseBaslama,
        FieldExtraction? faaliyetKodu,
        FieldExtraction? faaliyetAciklamasi)
    {
        var found = new Dictionary<string, double>(StringComparer.Ordinal);
        void Add(string key, FieldExtraction? extraction)
        {
            if (extraction is not null)
            {
                found[key] = extraction.Confidence;
            }
        }

        Add("vkn", vkn);
        Add("tckn", tckn);
        Add("ticaretUnvani", ticaretUnvani);
        Add("adiSoyadi", adiSoyadi);
        Add("vergiDairesi", vergiDairesi);
        Add("adres", adres);
        Add("iseBaslamaTarihi", iseBaslama);
        Add("anaFaaliyetKodu", faaliyetKodu);
        Add("anaFaaliyetAciklamasi", faaliyetAciklamasi);

        // A vergi levhası carries either a VKN (company) or a TCKN (sole trader), never a duty to
        // have both, so the missing one must not count against completeness.
        var relevant = OverallWeights
            .Where(w => w.Key switch
            {
                "vkn" => tckn is null || vkn is not null,
                "tckn" => vkn is null || tckn is not null,
                "ticaretUnvani" => adiSoyadi is null || ticaretUnvani is not null,
                "adiSoyadi" => ticaretUnvani is null || adiSoyadi is not null,
                _ => true,
            })
            .ToList();

        var totalWeight = relevant.Sum(w => w.Weight);
        var foundWeight = relevant.Where(w => found.ContainsKey(w.Key)).Sum(w => w.Weight);

        double weightedScore = 0;
        foreach (var (key, weight) in relevant)
        {
            if (found.TryGetValue(key, out var value))
            {
                weightedScore += weight * value;
            }
        }

        var quality = foundWeight <= 0 ? 0 : weightedScore / foundWeight;

        // Completeness factor: a page where only one field was located should not report 0.99.
        var completeness = totalWeight <= 0 ? 0 : foundWeight / totalWeight;
        var overall = Math.Round(quality * (0.6 + 0.4 * completeness), 4);

        return new ConfidenceReport
        {
            Overall = overall,
            DocumentType = Math.Round(detection.Score, 4),
            Vkn = vkn?.Confidence,
            Tckn = tckn?.Confidence,
            TicaretUnvani = ticaretUnvani?.Confidence,
            AdiSoyadi = adiSoyadi?.Confidence,
            VergiDairesi = vergiDairesi?.Confidence,
            Adres = adres?.Confidence,
            IseBaslamaTarihi = iseBaslama?.Confidence,
            AnaFaaliyetKodu = faaliyetKodu?.Confidence,
            AnaFaaliyetAciklamasi = faaliyetAciklamasi?.Confidence,
        };
    }

    private void AddLowConfidenceWarnings(
        List<AnalysisWarning> warnings,
        ParserWorkspace workspace,
        IEnumerable<(string Name, string JsonField, FieldExtraction? Extraction)> fields)
    {
        var averageOcr = workspace.AverageOcrConfidence();
        if (averageOcr > 0 && averageOcr < _options.LowOcrConfidenceThreshold)
        {
            warnings.Add(new AnalysisWarning(
                WarningCodes.LowOcrConfidence,
                $"Sayfa genelinde OCR güven skoru düşük ({averageOcr:0.00})."));
        }

        foreach (var (_, jsonField, extraction) in fields)
        {
            if (extraction is null || extraction.Confidence >= _options.LowConfidenceThreshold)
            {
                continue;
            }

            var code = jsonField == "adres"
                ? WarningCodes.LowConfidenceAddress
                : WarningCodes.LowConfidenceField;

            warnings.Add(new AnalysisWarning(
                code,
                $"'{jsonField}' alanının güven skoru düşük ({extraction.Confidence:0.00}).",
                jsonField));
        }
    }
}

/// <summary>A located field value together with the block it came from and its blended confidence.</summary>
internal sealed record FieldExtraction(string Value, double Confidence, OcrBlock Block)
{
    public bool ChecksumValid { get; init; }
    public bool SubstitutionApplied { get; init; }
    public string? RawValue { get; init; }
}
