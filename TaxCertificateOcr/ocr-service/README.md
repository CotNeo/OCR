# OCR Service (Python / FastAPI / PP-OCRv5)

Local text recognition for the Tax Certificate API. It returns **text, confidence and geometry
only** — it knows nothing about vergi levhası fields. All field extraction happens in the .NET
parser, which keeps the OCR model free of business logic.

No cloud service is contacted at any point. The only network access is a one-time model
download from PaddlePaddle's CDN on first start (~90 MB, cached in `~/.paddlex`).

## Requirements

- Python 3.10 – 3.13 (tested on 3.12)
- macOS 11+ on Apple Silicon, or x86_64 Linux/Windows
- ~2 GB free disk (wheels + model cache), ~1.2 GB peak RSS during inference

## Setup

```bash
cd ocr-service
python3 -m venv .venv
source .venv/bin/activate
python -m pip install --upgrade pip setuptools wheel
python -m pip install -r requirements.txt
```

All pinned wheels publish `macosx_11_0_arm64` builds, so nothing compiles from source on M1.

## Run

```bash
source .venv/bin/activate
python -m uvicorn app.main:app --host 127.0.0.1 --port 8001
```

Or from the repo root: `./scripts/run-ocr-service.sh` (creates the venv on first run).

Startup builds and warms the models once. Expect `ocr engine ready: 1 instance(s)` in the log
before the first request; a cold first start also downloads the models.

## Endpoints

### `POST /ocr`

`multipart/form-data` with a single `file` part. Accepts PDF, PNG and JPEG — determined by
**magic bytes**, not by the filename or the declared Content-Type.

```bash
curl -X POST http://127.0.0.1:8001/ocr -F "file=@../tests/samples/sample_levha.pdf"
```

```json
{
  "success": true,
  "duration_ms": 4264,
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
          "box": { "x1": 99, "y1": 329, "x2": 409, "y2": 360 },
          "polygon": [[99, 329], [409, 329], [409, 360], [99, 360]]
        }
      ]
    }
  ]
}
```

Blocks are sorted top-to-bottom then left-to-right. The .NET parser relies on that reading
order for multi-line values such as the address.

Error responses use `{"success": false, "error": {"code": "...", "message": "..."}}` with codes
`EMPTY_FILE`, `FILE_TOO_LARGE`, `UNSUPPORTED_MEDIA_TYPE`, `IMAGE_TOO_LARGE`, `INVALID_FILE`,
`SERVER_BUSY`, `ENGINE_NOT_READY` and `OCR_FAILED`.

### `GET /health`

```json
{ "status": "healthy", "modelLoaded": true, "engine": "PP-OCRv5/...", "device": "cpu" }
```

Interactive docs: <http://127.0.0.1:8001/docs>

## Configuration

Everything is environment-driven (`app/config.py`). Defaults are tuned for an M1 with 8 GB.

| Variable | Default | Notes |
| --- | --- | --- |
| `OCR_LANG` | `tr` | Only used when the model names below are blanked. |
| `OCR_VERSION` | `PP-OCRv5` | Same. |
| `OCR_DET_MODEL` | `PP-OCRv5_mobile_det` | See "Why the mobile detector". |
| `OCR_REC_MODEL` | `latin_PP-OCRv5_mobile_rec` | PP-OCRv5 Latin/Turkish recogniser. |
| `OCR_DEVICE` | `cpu` | No CUDA, no MPS — see "Apple Silicon notes". |
| `OCR_CPU_THREADS` | `4` | PaddleOCR's own default is 10, which oversubscribes an M1. |
| `OCR_ENABLE_MKLDNN` | `false` | **Must stay false on Apple Silicon.** See below. |
| `OCR_MAX_CONCURRENCY` | `1` | One engine instance per permit, ~450 MB each. |
| `OCR_MAX_QUEUE` | `8` | Requests beyond this get `503 SERVER_BUSY`. |
| `OCR_MAX_UPLOAD_BYTES` | `10485760` | 10 MB. |
| `OCR_MAX_PAGES` | `5` | Extra PDF pages are ignored, with a warning logged. |
| `OCR_PDF_DPI` | `220` | Rasterisation DPI. |
| `OCR_MAX_IMAGE_SIDE` | `1600` | Long-edge cap. The main memory lever. |
| `OCR_MAX_PIXELS` | `40000000` | Decompression-bomb guard. |
| `OCR_ENABLE_DESKEW` | `true` | Projection-profile deskew, ±8°. |
| `OCR_ENABLE_CONTRAST` | `true` | CLAHE on the luminance channel. |
| `OCR_ENABLE_GRAYSCALE` | `false` | |
| `OCR_ENABLE_ADAPTIVE_THRESHOLD` | `false` | Off on purpose — see below. |
| `OCR_WARMUP` | `true` | Warm the models at startup. |

## Design notes

### `enable_mkldnn` must be false

PaddleOCR enables oneDNN/MKL-DNN **by default**. It is an x86 acceleration path, and on CPU
inference it aborts with:

```
NotImplementedError: (Unimplemented) ConvertPirAttribute2RuntimeAttribute not support
[pir::ArrayAttribute<pir::DoubleAttribute>]  (at .../onednn/onednn_instruction.cc:116)
```

`app/config.py` therefore inverts the default to `False`.

### Why the mobile detector

`lang="tr"` alone resolves the recogniser to `latin_PP-OCRv5_mobile_rec` (correct) but the
detector to `PP-OCRv5_server_det`. Measured on a 1240×1754 synthetic levha, CPU:

| Detector | Time | Exact field matches |
| --- | --- | --- |
| `PP-OCRv5_server_det` | ~38 s | 5/5 |
| `PP-OCRv5_mobile_det` | ~5 s | 5/5 |

Roughly 7× faster for identical extraction, so both model names are pinned explicitly. Note
that PaddleOCR ignores `lang`/`ocr_version` once explicit model names are supplied, so they are
passed only when the model names are blank.

### Why the long edge is capped at 1600 px

PP-OCRv5's detector defaults to `limit_type="min"`, so it processes near full resolution up to
4000 px. Measured on the same sample:

| Long-edge cap | Time | Peak RSS | Exact matches | Mean confidence |
| --- | --- | --- | --- | --- |
| 1200 px | 3.7 s | 820 MB | 5/5 | 0.973 |
| **1600 px** | **4.4 s** | **1044 MB** | **5/5** | **0.978** |
| 2000 px | 4.5 s | 1184 MB | 5/5 | 0.976 |
| uncapped | 4.5 s | 1185 MB | 5/5 | 0.976 |

1600 px gives the best confidence while saving ~12% peak memory against uncapped.

### Why warmup uses an image containing text

PaddleOCR builds its predictors lazily on first use. A **blank** warmup frame initialises only
the detector: no text is found, so the recogniser is never reached and the first real request
still stalls for seconds. `app/ocr.py` therefore warms up with a frame containing drawn text,
which brings both models up in ~0.2 s.

### Why binarisation is off

PP-OCR is trained on natural images. Hard adaptive thresholding measurably hurts recognition on
clean scans, so `OCR_ENABLE_ADAPTIVE_THRESHOLD` defaults to `false` and exists only for
pathologically low-contrast photographs.

### Concurrency

A Paddle inference predictor is not documented as thread-safe, so concurrency is achieved with a
pool of separate engine instances — one per permit — rather than by sharing one engine across
threads. Each instance costs roughly 450 MB resident, which is why the default is `1` on an 8 GB
machine. Inference itself runs on a `ThreadPoolExecutor`, so the CPU-bound call never blocks the
asyncio event loop.

### Security

- Type is decided by magic bytes; the filename and declared Content-Type are ignored.
- **No temporary files.** Images are decoded from memory and PDF pages are rasterised straight
  into numpy arrays, so there is no path to traverse and nothing to clean up.
- PDF pages are yielded one at a time, so only one rasterised page is resident.
- Pixel budget and page count are both capped before decoding.
- Error responses are generic strings; tracebacks go to the log only, never the response.
- The client filename is never logged.

### Apple Silicon notes

- `paddlepaddle` is the **CPU** build. Never install `paddlepaddle-gpu`: there is no CUDA on
  Apple Silicon, and Paddle has no stable MPS backend, so CPU is the supported path.
- `paddlepaddle` publishes **no linux/aarch64 wheel**. The Dockerfile here is x86_64-only;
  on macOS run the service natively.
- A harmless `No ccache found` warning appears at import time. It refers to source compilation
  that never happens with prebuilt wheels and can be ignored.
