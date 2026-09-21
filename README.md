# TaxCertificateOcr

Türk vergi levhalarını JPG / JPEG / PNG / PDF olarak alıp **tamamen lokal** OCR ile işleyerek
structured JSON döndüren sistem.

Hiçbir harici servis kullanılmaz: OpenAI, Claude, Gemini, Jina, Google Vision, Azure Vision,
AWS Textract — hiçbiri. LLM yok, veritabanı yok, sistem stateless. Tek dış ağ erişimi, ilk
çalıştırmada PaddleOCR modellerinin indirilmesidir (~90 MB, sonrasında `~/.paddlex` içinde
cache'lenir).

---

## Contents

- [Requirements](#requirements)
- [Architecture](#architecture)
- [Python setup](#python-setup)
- [PaddleOCR setup](#paddleocr-setup)
- [.NET setup](#net-setup)
- [Run OCR service](#run-ocr-service)
- [Run API](#run-api)
- [Swagger](#swagger)
- [Example request](#example-request)
- [Example response](#example-response)
- [Configuration](#configuration)
- [Parser design](#parser-design)
- [Confidence model](#confidence-model)
- [Warning and error codes](#warning-and-error-codes)
- [Tests](#tests)
- [Docker](#docker)
- [Troubleshooting](#troubleshooting)
- [Apple Silicon notes](#apple-silicon-notes)
- [Memory considerations](#memory-considerations)
- [Project layout](#project-layout)

---

## Requirements

| Component | Version | Notes |
| --- | --- | --- |
| macOS | 11+ | Apple Silicon (M1/M2/M3) or Intel |
| .NET SDK | 8.0 | `brew install --cask dotnet-sdk` or from dot.net |
| Python | 3.10 – 3.13 | 3.12 recommended; macOS system Python 3.9 is too old |
| Disk | ~3 GB | .NET packages + Python wheels + model cache |
| RAM | 8 GB | Fits, with the defaults in this repo |

GPU is not required and not used. Everything runs on CPU.

---

## Architecture

```text
Client
   |
   | multipart/form-data  (file)
   v
ASP.NET Core 8 Web API              :5000
   |  - magic-byte validation, size limit, concurrency gate
   |
   | localhost HTTP, multipart/form-data
   v
Python FastAPI OCR service          :8001
   |  - EXIF / deskew / resize / CLAHE
   |  - PDF -> page images (pypdfium2)
   v
PP-OCRv5  (PP-OCRv5_mobile_det + latin_PP-OCRv5_mobile_rec)
   |
   v
OCR blocks: text + confidence + bounding box + polygon
   |
   v
.NET VergiLevhasiParser
   |  1. text normalization   (Turkish -> ASCII skeleton, comparison only)
   |  2. label detection      (alias catalogue + bounded fuzzy match)
   |  3. spatial matching     (geometry + format + confidence scoring)
   |
   +--> VKN                    +--> Vergi Dairesi         +--> Ana Faaliyet Kodu
   +--> TCKN                   +--> Adres                 +--> Ana Faaliyet Açıklaması
   +--> Ticaret Unvanı         +--> İşe Başlama Tarihi
   +--> Adı Soyadı
   |
   v
Validation (VKN / TCKN checksum)
   |
   v
JSON response
```

**The key boundary:** the OCR service carries no business logic. It emits text, confidence and
geometry, nothing else. Every vergi levhası concept lives in the .NET parser, so the model and
the business rules can change independently.

```text
OCR != Business Parser
```

### Layout rationale

Two deviations from a plain three-project split, both to keep responsibilities honest:

- **`Application/Text/`** holds `TurkishTextNormalizer` and `Levenshtein`. Turkish casing is
  subtle enough (dotted/dotless I, diacritics dropped by OCR on capitals) that it deserves its
  own tested unit rather than being buried in the parser.
- **`Application/Models/`** holds the OCR domain model (`OcrBlock`, `OcrPage`, `BoundingBox`)
  separately from `Dtos/`, which holds the API response shape. The parser depends only on
  `Models`, so it never sees an HTTP concern, and the wire contract can change without touching
  parsing logic.

There is no repository layer, no CQRS and no extra interface per class — only three interfaces
exist (`IOcrClient`, `ITaxCertificateParser`, `ITaxCertificateAnalyzer`), one per real seam.

---

## Python setup

```bash
cd ocr-service
python3 -m venv .venv
source .venv/bin/activate
python -m pip install --upgrade pip setuptools wheel
python -m pip install -r requirements.txt
```

Verify Python is 3.10+ first: `python3 --version`. On older macOS the system `python3` may be
3.9, in which case install a newer one (`brew install python@3.12`).

## PaddleOCR setup

`requirements.txt` pins versions that all publish `macosx_11_0_arm64` wheels, so nothing
compiles from source:

```text
paddlepaddle==3.3.1          # CPU build
paddleocr==3.7.0
pypdfium2==5.13.0            # PDF rasterisation (BSD-3-Clause / Apache-2.0)
opencv-contrib-python==4.10.0.84
pillow==12.3.0
fastapi==0.141.1
uvicorn[standard]==0.53.0
python-multipart==0.0.32
```

Two pins worth knowing about:

- **Never install `paddlepaddle-gpu`.** There is no CUDA on Apple Silicon and Paddle has no
  stable MPS backend. CPU is the supported path, and it is what this repo configures.
- **Do not add `opencv-python-headless`.** `paddleocr` already pulls
  `opencv-contrib-python==4.10.0.84`; two distributions both providing the `cv2` module shadow
  each other unpredictably. The pin here matches what paddleocr resolves.

Models download automatically on first use (~90 MB into `~/.paddlex/official_models`). After
that the service runs fully offline.

Verify the install:

```bash
python -c "import paddle, paddleocr; print(paddle.__version__, paddleocr.__version__)"
# 3.3.1 3.7.0
```

## .NET setup

```bash
dotnet --version     # expect 8.0.x
dotnet restore
dotnet build
```

---

## Run OCR service

```bash
./scripts/run-ocr-service.sh
```

Creates the venv on first run. Wait for:

```text
INFO  app.ocr: engine 0 warmed in 0.24s
INFO  app.ocr: ocr engine ready: 1 instance(s) in 1.62s
INFO: Uvicorn running on http://127.0.0.1:8001
```

The first start also downloads the models, so it takes longer.

## Run API

In a second terminal:

```bash
./scripts/run-api.sh
```

The API listens on <http://localhost:5000>. Check both services:

```bash
curl http://127.0.0.1:8001/health
# {"status":"healthy","modelLoaded":true,"engine":"PP-OCRv5/...","device":"cpu"}

curl http://localhost:5000/health
# {"status":"Healthy","totalDurationMs":1,"checks":[{"name":"ocr-service","status":"Healthy",...}]}
```

The API's health check probes the OCR service, so a single call tells you whether the whole
pipeline is up. Probes are excluded from the retry policy, so they fail in ~1 ms when the OCR
service is down rather than waiting out a retry delay.

For a full end-to-end check: `./scripts/smoke-test.sh`

## Swagger

<http://localhost:5000/swagger> — `/` redirects there. The analyze endpoint renders a real file
picker, so an upload can be tested straight from the browser.

## Example request

```bash
curl -X POST \
  http://localhost:5000/api/vergi-levhasi/analyze \
  -F "file=@tests/samples/sample_levha.pdf"
```

Supported: `application/pdf`, `image/jpeg`, `image/png`. The type is decided by **magic bytes** —
a `.png` containing text is rejected with `415`, regardless of what the filename or the declared
Content-Type says.

## Example response

```json
{
  "success": true,
  "documentType": "VERGI_LEVHASI",
  "data": {
    "vkn": "4540536920",
    "tckn": null,
    "ticaretUnvani": "ÖRNEK LOJİSTİK SANAYİ VE TİCARET A.Ş.",
    "adiSoyadi": null,
    "vergiDairesi": "ÜMRANİYE",
    "adres": "SARAY MAH. DR. ADNAN BÜYÜKDENİZ CAD. NO: 4 KAT: 7 ÜMRANİYE / İSTANBUL",
    "iseBaslamaTarihi": "2019-03-01",
    "anaFaaliyetKodu": "494103",
    "anaFaaliyetKoduRaw": "494103",
    "anaFaaliyetAciklamasi": "Karayolu ile yük taşımacılığı"
  },
  "validation": {
    "vkn": { "valid": true },
    "tckn": null,
    "iseBaslamaTarihi": { "valid": true }
  },
  "confidence": {
    "overall": 0.8847,
    "vkn": 0.9268,
    "ticaretUnvani": 0.8825,
    "vergiDairesi": 0.8356,
    "adres": 0.825,
    "iseBaslamaTarihi": 0.9348,
    "anaFaaliyetKodu": 0.9238,
    "anaFaaliyetAciklamasi": 0.829,
    "documentType": 1
  },
  "warnings": []
}
```

Null semantics are deliberate: a field inside `data` or `validation` that was searched for and
not found stays an explicit `null`, so a client can distinguish "looked, absent" from "not part
of this contract". Envelope members that simply do not apply (`error`, `rawOcr`) are omitted.

Rejection of a non-certificate document returns `422`:

```json
{
  "success": false,
  "documentType": "UNKNOWN",
  "confidence": { "overall": 0, "documentType": 0 },
  "warnings": [
    { "code": "UNSUPPORTED_DOCUMENT", "message": "Vergi levhası kanıt skoru 0.00, eşik 0.35." }
  ],
  "error": { "code": "NOT_TAX_CERTIFICATE", "message": "Belge vergi levhası olarak tanınamadı." }
}
```

### Measured on the bundled samples

All four formats extract the same six fields exactly, on CPU:

| Sample | Time | `overall` | Fields exact | Warnings |
| --- | --- | --- | --- | --- |
| `sample_levha.pdf` | 4.3 s | 0.885 | 6/6 | — |
| `sample_levha.png` | 4.3 s | 0.888 | 6/6 | — |
| `sample_levha_skew.jpg` (−3.2° skew) | 4.5 s | 0.893 | 6/6 | — |
| `sample_levha_2page.pdf` | 8.2 s | 0.885 | 6/6 | `MULTI_PAGE_DOCUMENT` |

The skewed JPEG is corrected automatically: the deskew step measured +3.2° against the −3.2°
that was baked into the sample.

### Debug mode

`Ocr:IncludeRawResult` adds a `rawOcr` array of every block with its geometry. It is `true` in
`appsettings.Development.json` and **`false` in production** — raw OCR text is document content.

---

## Configuration

`src/TaxCertificate.Api/appsettings.json`:

```json
{
  "Ocr": {
    "BaseUrl": "http://127.0.0.1:8001",
    "TimeoutSeconds": 120,
    "IncludeRawResult": false,
    "MaxRetries": 1,
    "RetryBaseDelayMilliseconds": 500
  },
  "Upload": {
    "MaxFileSizeBytes": 10485760,
    "MaxConcurrentAnalyses": 2,
    "ConcurrencyQueueTimeoutSeconds": 30
  },
  "Parser": {
    "MinDocumentTypeScore": 0.35,
    "AllowDigitSubstitution": false,
    "LowConfidenceThreshold": 0.70,
    "LowOcrConfidenceThreshold": 0.75,
    "MaxAddressLines": 6,
    "AllowVknWithoutLabel": true
  }
}
```

Any value can be overridden by environment variable using `__` as the separator, e.g.
`Ocr__BaseUrl=http://127.0.0.1:9001`.

The HTTP port is **not** configured in `appsettings.json` on purpose. A `urls` key or a
`Kestrel:Endpoints` block there silently overrides both `ASPNETCORE_URLS` and `--urls`, which
breaks container runs (a container must bind `0.0.0.0`, not `localhost`). Port comes from
`launchSettings.json` locally, `ASPNETCORE_URLS` in Docker, or `--urls`.

OCR service settings are environment variables — see [ocr-service/README.md](ocr-service/README.md).

---

## Parser design

Three stages, all deterministic. No ML, no LLM, no fixed pixel coordinates.

### 1. Text normalization

`TurkishTextNormalizer` folds text onto an ASCII skeleton **for comparison only**. Output values
never pass through it, so Turkish characters survive intact in the response.

```text
"VERGİ KİMLİK NUMARASI"  ->  "VERGI KIMLIK NUMARASI"
"Vergi Kimlik Numarası"  ->  "VERGI KIMLIK NUMARASI"
"Vergı Kımlık Numarası"  ->  "VERGI KIMLIK NUMARASI"
```

This is not cosmetic. PP-OCR routinely mangles Turkish diacritics on capitals — in real runs it
read `VERGİ LEVHASI` as `VERGI LEVHASI`, `İSTANBUL` as `iSTANBUL`, and `VERGİ DAİRESİ` as
`VERGİ DAÍRESİ` (an acute accent in place of the dot). The folder therefore maps Turkish letters
explicitly *and* strips any remaining Latin diacritic via Unicode canonical decomposition, so all
of those become exact label matches instead of consuming the fuzzy-distance budget.

`CleanValue` trims separators **asymmetrically**: a leading `:` or `-` is leftover label
punctuation, but a trailing period belongs to the value — stripping it would turn `A.Ş.` into
`A.Ş` and `CAD.` into `CAD`.

### 2. Label detection

`LabelCatalog` holds alias lists per field (`VERGİ KİMLİK NUMARASI`, `VERGİ KİMLİK NO`, `VKN`,
`TİCARET UNVANI`, `TİCARET ÜNVANI`, …) and matches in four escalating steps: exact, label +
inline value, separator-insensitive compact, then bounded Levenshtein.

Fuzzy tolerance scales with alias length, because a single edit on a short token is far too easy
to hit by accident:

| Alias length | Required similarity |
| --- | --- |
| ≤ 6 chars | 1.00 (exact only) |
| ≤ 10 | 0.90 |
| ≤ 16 | 0.86 |
| > 16 | 0.82 |

So `VERGİ KİMLİK NUMARASl` (final `l` instead of `I`) matches, while `ADRAS` does not match
`ADRES` and `VKM` does not match `VKN`. Aliases are tried longest-first, so `ANA FAALİYET KODU`
is never captured by the shorter `ANA FAALİYET`.

### 3. Spatial value matching

For each detected label, candidates are scored geometrically. **Everything is expressed in
multiples of the label's own height, never in absolute pixels**, so the same thresholds hold for
a 150 DPI scan and a 12 MP phone photo.

```text
geometryScore = 0.55 * overlapRatio + 0.30 * gapScore + 0.15 * alignment
```

- *below* (the dominant layout): vertical gap up to 3× label height, with a small negative gap
  tolerated because skewed scans make neighbouring rows overlap
- *right* (same row): horizontal gap up to 10× label height, scored at 0.92× to prefer *below*

Candidates are then ranked by `geometry × format × OCR confidence`, with checksum-valid identity
numbers outranking invalid ones regardless of geometry.

### Field-specific rules

**VKN** — exactly 10 digits. Whitespace and separators are stripped (`123 456 7890` →
`1234567890`). Letter-to-digit substitution (`O`→`0`, `I`→`1`) is **off by default**: silently
inventing a different tax number is worse than reporting none. Enabling
`Parser:AllowDigitSubstitution` emits `DIGIT_SUBSTITUTION_APPLIED` and cuts confidence by 25%.
Checksum implemented in `VergiKimlikNoValidator`.

**TCKN** — exactly 11 digits, first digit non-zero, checksum in `TcKimlikNoValidator`. A VKN is
never allowed to land in the TCKN field or vice versa.

**Tarih** — `01.02.2020`, `01/02/2020`, `1.2.2020`, `1-2-2020` and `01022020` all normalise to
`2020-02-01`. Unparseable input returns `null` plus `DATE_PARSE_FAILED`; it is never guessed.
Implausible years (before 1900, more than a year ahead) are rejected.

**Ana faaliyet kodu** — `49.41.03` normalises to `494103`, with the raw form preserved in
`anaFaaliyetKoduRaw`. 4–6 digits accepted. No external NACE lookup.

**Ticaret unvanı** — OCR's value is preserved verbatim, casing and Turkish characters included.
Company markers (`A.Ş.`, `LTD`, `ŞTİ`, `SAN`, `TİC`, `ANONİM`, `LİMİTED`, …) raise the format
score. A unvan wrapping onto a second line is joined.

**Adres** — collected as a column walk below the label, stopping at the first block that is
another label, an already-claimed value, a bare identity number or a date. Capped at
`MaxAddressLines`. Fields are claimed in order — identity numbers first — so the address can
never absorb a VKN.

**Vergi dairesi** — a redundant trailing `VERGİ DAİRESİ` / `MÜDÜRLÜĞÜ` is stripped, since the
label already says what it is: `ÜMRANİYE VERGİ DAİRESİ MÜDÜRLÜĞÜ` → `ÜMRANİYE`.

### Document type detection

Not every upload is treated as a vergi levhası. `TaxCertificateDetector` scores weighted
anchors — `VERGİ LEVHASI` 0.45, `VERGİ KİMLİK NUMARASI` 0.25, `HAZİNE VE MALİYE BAKANLIĞI` 0.20,
`GELİR İDARESİ BAŞKANLIĞI` 0.15, `VERGİ DAİRESİ` 0.15, and a few smaller ones — and rejects
anything below `MinDocumentTypeScore` (0.35) with `NOT_TAX_CERTIFICATE`. An invoice that merely
mentions "VERGİ DAİRESİ" stays below the threshold.

---

## Confidence model

OCR confidence and parser confidence answer different questions:

- **OCR confidence** — "were these glyphs read correctly?"
- **Parser confidence** — "does this text really belong to this field?"

They fail independently and a field is only trustworthy when both hold, so the field score is
their **product**:

```text
fieldConfidence = ocrConfidence * parserConfidence

parserConfidence = strategyScore        (inline 0.98 | label+spatial 0.95 | no label 0.55)
                 * labelSimilarity      (1.0 exact, lower for a fuzzy hit)
                 * compressedGeometry   (geometry mapped into [0.75, 1.0])
                 * formatScore          (checksum valid 1.0 | invalid 0.50 | n/a 0.92)
                 * penalties            (0.75 if digit substitution was applied)
```

Two refinements over naive multiplication, both from observed behaviour:

1. **Geometry is compressed into [0.75, 1.0]** before entering the product. A value sitting
   slightly off-column is weaker evidence, not a different field. Letting a raw 0.45 geometry
   score halve an otherwise-certain VKN produced misleadingly low numbers.
2. **Multi-block fields use a length-weighted mean** of their blocks' OCR confidences rather
   than a product. A three-line address multiplied out would score near zero purely for being
   long, which says nothing about correctness.

Overall confidence is a weighted average over the fields that were found, damped by
completeness:

```text
overall = weightedAverage(found fields) * (0.6 + 0.4 * foundWeight / totalRelevantWeight)
```

Weights: VKN/TCKN 0.25, unvan/ad-soyad 0.15, vergi dairesi 0.15, adres 0.15, tarih 0.10,
faaliyet kodu 0.10, faaliyet açıklaması 0.05. A levhası carries either a VKN (company) or a TCKN
(sole trader), so the irrelevant one is excluded from the denominator instead of counting as
missing. The completeness factor keeps a page where only one field was located from reporting
0.99.

`confidence.documentType` is reported separately and never folded into `overall` — "is this the
right kind of document" is a different question from "how well did we read it".

---

## Warning and error codes

Branch on codes, never on message text.

### Warnings (`warnings[]`, response still succeeds)

| Code | Meaning |
| --- | --- |
| `VKN_NOT_FOUND` | No VKN located |
| `INVALID_VKN_CHECKSUM` | VKN found, checksum failed — value still returned |
| `MULTIPLE_VKN_CANDIDATES` | Several distinct candidates; highest-scoring chosen |
| `VKN_LABEL_NOT_FOUND` | No label; a checksum-valid number was accepted at low confidence |
| `INVALID_TCKN_CHECKSUM` | TCKN found, checksum failed |
| `MULTIPLE_TCKN_CANDIDATES` | Several distinct TCKN candidates |
| `NO_IDENTITY_NUMBER_FOUND` | Neither VKN nor TCKN found |
| `DIGIT_SUBSTITUTION_APPLIED` | A letter was rewritten as a digit — verify manually |
| `DATE_PARSE_FAILED` | Date label found but not parseable |
| `ACTIVITY_CODE_PARSE_FAILED` | Faaliyet kodu label found but not parseable |
| `LOW_OCR_CONFIDENCE` | Page-wide mean OCR confidence below threshold |
| `LOW_CONFIDENCE_FIELD` | A specific field scored below threshold |
| `LOW_CONFIDENCE_ADDRESS` | Address-specific low confidence |
| `MULTI_PAGE_DOCUMENT` | More than one page; all pages evaluated together |
| `UNSUPPORTED_DOCUMENT` | Document-type evidence below threshold |

### Errors (`error.code`, request fails)

| Code | HTTP | Meaning |
| --- | --- | --- |
| `EMPTY_FILE` | 400 | No file part, or zero bytes |
| `UNSUPPORTED_MEDIA_TYPE` | 415 | Magic bytes are not PDF/PNG/JPEG |
| `FILE_TOO_LARGE` | 413 | Over `Upload:MaxFileSizeBytes` |
| `NOT_TAX_CERTIFICATE` | 422 | Readable, but not a vergi levhası |
| `NO_TEXT_DETECTED` | 400 | OCR produced no blocks |
| `SERVER_BUSY` | 503 | Concurrency queue timed out |
| `OCR_SERVICE_UNAVAILABLE` | 503 | OCR service unreachable |
| `OCR_SERVICE_TIMEOUT` | 504 | OCR service did not answer in time |
| `OCR_FAILED` | 502 | OCR service reported a failure |
| `INTERNAL_ERROR` | 500 | Unhandled exception (details in logs only) |

---

## Tests

```bash
dotnet test
```

182 tests, no OCR model loaded — the parser is fed synthetic blocks and `IOcrClient` is stubbed,
so the suite runs in about two seconds.

| Area | Covers |
| --- | --- |
| `ValidatorTests` | VKN/TCKN checksums against python-stdnum reference vectors, leading-zero rule, VKN-is-not-a-TCKN, 500-number round trip |
| `ParserTests` | Full certificate, label variants, fuzzy match, OCR spacing, value right of / inline with label, invalid checksum, missing VKN, multiple numeric candidates, date formats, NACE normalisation, multiline address, digit substitution on and off, non-certificate rejection, confidence behaviour |
| `TextTests` | Normalizer folding, asymmetric trimming, compact form, Levenshtein, label catalogue precedence, digit extraction, date/NACE formats |
| `OcrClientTests` | Response mapping, polygon fallback, confidence clamping, unavailable/timeout/cancellation, error-code mapping, filename sanitisation incl. path traversal |
| `AnalyzerTests` | Orchestration, raw-OCR gating, no-text case, cancellation |
| `SpatialMatcherTests` | Below/right preference, geometric window, skew tolerance, cross-page isolation, column walk stop conditions |
| `ApiLayerTests` | Magic-byte validation, size limits, PII masking, concurrency limiter incl. double-dispose |

Synthetic samples are in [tests/samples/](tests/samples/): a clean PNG, a skewed JPEG, a
single-page PDF, a two-page PDF and a non-certificate invoice.

---

## Docker

**Native local development is the primary path.** Docker is secondary, and on Apple Silicon it
is only partly usable.

`paddlepaddle` publishes **no linux/aarch64 wheel** — 3.3.1 ships `macosx_11_0_arm64`,
`manylinux1_x86_64` and `win_amd64` only. So on an M1:

- The **OCR service cannot run in a native arm64 container.** It would need x86_64 emulation,
  where CPU inference is several times slower and far more memory-hungry — a bad trade on 8 GB.
- The **.NET API containerises fine** (the official .NET images are multi-arch).

```bash
# Recommended on M1: both native, no container networking
./scripts/run-ocr-service.sh          # terminal 1
./scripts/run-api.sh                  # terminal 2

# Or containerise just the API against the host-native OCR service.
# The OCR service must bind beyond loopback for the container to reach it:
OCR_HOST=0.0.0.0 ./scripts/run-ocr-service.sh    # terminal 1
docker compose up api --build                    # terminal 2  -> localhost:5000

# x86_64 Linux / Windows hosts only: full stack
docker compose --profile full up --build
```

The `ocr` service sits behind the `full` profile so it never launches by accident on Apple
Silicon.

---

## Troubleshooting

**`NotImplementedError: ConvertPirAttribute2RuntimeAttribute not support [pir::ArrayAttribute...]`**

oneDNN/MKL-DNN is active. PaddleOCR enables it by default; it is an x86 path and aborts on CPU
inference. Set `OCR_ENABLE_MKLDNN=false` (already the default in `app/config.py` and the run
script).

**First request takes 30–40 seconds**

The server detection model is in use. Confirm `/health` reports
`PP-OCRv5/PP-OCRv5_mobile_det+latin_PP-OCRv5_mobile_rec`. If `OCR_DET_MODEL` was blanked,
PaddleOCR resolves `PP-OCRv5_server_det`, which is ~7× slower for the same result here.

**First request is slow even though startup said "engine ready"**

Warmup did not reach the recogniser. PaddleOCR builds predictors lazily, and a blank warmup frame
only initialises the detector. `app/ocr.py` warms up with a frame containing drawn text; do not
replace it with a blank image.

**`UserWarning: lang and ocr_version will be ignored when model names ... are not None`**

Expected if both are passed. `app/ocr.py` passes the explicit model names *or* `lang`/
`ocr_version`, never both, so this warning should not appear.

**`OCR_SERVICE_UNAVAILABLE` from the API**

The Python service is not running or is on another port. Check `curl http://127.0.0.1:8001/health`
and `Ocr:BaseUrl`.

**`NETSDK1064: Package ... was not found` during `docker build`**

Host `obj/` leaked into the build context. `.dockerignore` excludes `**/obj/` and `**/bin/`;
make sure it is present.

**Address already in use on port 5000**

macOS AirPlay Receiver also uses 5000. Disable it in System Settings → General → AirDrop &
Handoff, or run `dotnet run --project src/TaxCertificate.Api --urls http://localhost:5050`.

**Turkish characters render as `?` in the terminal**

A console encoding issue, not a data issue. The API returns unescaped UTF-8 — verify with
`curl ... | python3 -m json.tool --no-ensure-ascii`.

**Models re-download on every start**

`~/.paddlex` is not writable or is being cleared. Check permissions.

---

## Apple Silicon notes

- **CPU only.** No CUDA exists on Apple Silicon, and Paddle has no stable Metal/MPS backend, so
  there is no GPU path to fall back from. `OCR_DEVICE=cpu` is the supported configuration and
  nothing in this repo contains CUDA code.
- **`enable_mkldnn` must stay false.** It is an x86 acceleration path and crashes on CPU
  inference (see Troubleshooting). The default is inverted from PaddleOCR's own.
- **`cpu_threads=4`, not 10.** PaddleOCR defaults to 10 threads; the M1 has 4 performance + 4
  efficiency cores, and oversubscribing thrashes shared memory bandwidth.
- **No arm64 Linux wheel**, so the OCR service is not containerisable on M1 — see [Docker](#docker).
- A harmless `No ccache found` warning appears at import. It refers to source compilation that
  never happens with prebuilt wheels.
- Measured on CPU with the mobile model pair: ~4–5 s per page, ~8 s for a two-page PDF.

## Memory considerations

Defaults are tuned for 8 GB of unified memory:

| Lever | Default | Effect |
| --- | --- | --- |
| `OCR_MAX_IMAGE_SIDE` | 1600 px | Main memory lever — see the measurements below |
| `OCR_MAX_CONCURRENCY` | 1 | One engine instance per permit, ~450 MB each |
| `OCR_MAX_QUEUE` | 8 | Beyond this, `503 SERVER_BUSY` instead of queuing |
| `Upload:MaxConcurrentAnalyses` | 2 | Bounds buffered request bodies on the .NET side |
| `OCR_PDF_DPI` | 220 | Rasterisation DPI |
| `OCR_MAX_PAGES` | 5 | Page cap |
| `OCR_MAX_PIXELS` | 40 M | Decompression-bomb guard |
| `OCR_CPU_THREADS` | 4 | Matches the M1 performance-core count |

Long-edge cap measured on a 1240×1754 synthetic levha:

| Cap | Time | Peak RSS | Exact matches | Mean confidence |
| --- | --- | --- | --- | --- |
| 1200 px | 3.7 s | 820 MB | 5/5 | 0.973 |
| **1600 px** | **4.4 s** | **1044 MB** | **5/5** | **0.978** |
| 2000 px | 4.5 s | 1184 MB | 5/5 | 0.976 |
| uncapped | 4.5 s | 1185 MB | 5/5 | 0.976 |

1600 px was chosen for the best confidence with ~12% less peak memory than uncapped. Expect
roughly 1.0–1.2 GB resident for the Python service under load, plus ~150 MB for the API.

Other memory decisions:

- **PDF pages are rasterised one at a time** through a generator. A 5-page A4 document at
  220 DPI is ~5 × 17 MB of RGB; materialising them all at once is a real risk on 8 GB.
- **Three auxiliary models are disabled** (`use_doc_orientation_classify`, `use_doc_unwarping`,
  `use_textline_orientation`). EXIF handling plus the deskew step already produce an upright
  page, so loading them would cost memory and latency for no accuracy gain.
- **No temporary files anywhere.** Images decode from memory, PDF pages rasterise straight into
  numpy arrays, and the API buffers the upload in a capped `MemoryStream`. Nothing to clean up
  and nothing to leak.

---

## Security

- **Magic-byte validation** on both sides. The filename and declared Content-Type are treated as
  untrusted and never used to pick a code path.
- **The filename is never used to build a path** and never forwarded: the API sends a fixed
  `upload.<ext>` to the OCR service. Path-traversal inputs are covered by tests.
- **Size limits** at Kestrel, form-options, action-attribute and OCR-service level.
- **No temporary files**, so nothing to clean up or leak.
- **No stack traces or paths in responses.** `GlobalExceptionMiddleware` returns a generic
  envelope; details go to the log with the trace id.
- **PII masking in logs.** Identity numbers are logged as `******6920`, and raw OCR text is never
  logged. Free text is reduced to a character count.
- **Concurrency limiter** sheds load with `503` instead of letting request bodies pile up.
- **Decompression-bomb guard** via pixel budget and page cap, checked before full decode.
- Containers run as a non-root user (uid 10001).

---

## Project layout

```text
TaxCertificateOcr/
├── src/
│   ├── TaxCertificate.Api/              ASP.NET Core 8 Web API
│   │   ├── Configuration/               Options + typed HttpClient retry policy
│   │   ├── Contracts/                   Multipart request contract
│   │   ├── Controllers/                 VergiLevhasiController
│   │   ├── Middleware/                  GlobalExceptionMiddleware
│   │   ├── Services/                    Magic-byte validation, concurrency limiter, health
│   │   ├── Swagger/                     File-upload operation filter
│   │   ├── Program.cs
│   │   └── appsettings.json
│   │
│   ├── TaxCertificate.Application/      Domain: no I/O, no OCR knowledge
│   │   ├── Configuration/               ParserOptions
│   │   ├── Dtos/                        Response contract + warning/error codes
│   │   ├── Interfaces/                  IOcrClient, ITaxCertificateParser, ITaxCertificateAnalyzer
│   │   ├── Models/                      OcrBlock, OcrPage, BoundingBox
│   │   ├── Parsers/                     Label catalogue, spatial matcher, parser, detector
│   │   ├── Services/                    Analyzer orchestration, PII masking
│   │   ├── Text/                        TurkishTextNormalizer, Levenshtein
│   │   └── Validators/                  VergiKimlikNoValidator, TcKimlikNoValidator
│   │
│   └── TaxCertificate.Infrastructure/
│       └── Ocr/                         Typed HttpClient, wire contracts, options
│
├── ocr-service/                         Python FastAPI + PP-OCRv5
│   ├── app/
│   │   ├── config.py                    Environment-driven settings
│   │   ├── main.py                      Endpoints + lifespan
│   │   ├── models.py                    Pydantic wire contracts
│   │   ├── ocr.py                       Engine pool, warmup, inference
│   │   ├── pdf_render.py                pypdfium2 rasterisation
│   │   └── preprocessing.py             EXIF, deskew, resize, CLAHE
│   ├── Dockerfile                       x86_64 only
│   ├── requirements.txt
│   └── README.md
│
├── tests/
│   ├── TaxCertificate.UnitTests/        182 tests, no model loaded
│   └── samples/                         Synthetic levha samples
│
├── scripts/                             run-ocr-service.sh, run-api.sh, smoke-test.sh
├── docker-compose.yml
├── .dockerignore
├── .gitignore
└── README.md
```
