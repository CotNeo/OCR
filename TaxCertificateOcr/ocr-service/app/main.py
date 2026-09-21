"""FastAPI surface for the OCR service.

Scope is intentionally narrow: accept a file, return text blocks with geometry and
confidence. It knows nothing about vergi levhası fields.
"""

from __future__ import annotations

import logging
import time
from contextlib import asynccontextmanager
from typing import List

from fastapi import FastAPI, File, UploadFile, status
from fastapi.responses import JSONResponse

from .config import settings
from .models import ErrorBody, ErrorResponse, HealthResponse, OcrResponse, Page
from .ocr import OcrBusyError, OcrEngineNotReadyError, engine
from .pdf_render import InvalidPdfError, render_pages
from .preprocessing import (
    ImageTooLargeError,
    InvalidImageError,
    decode_image,
    preprocess,
)

logging.basicConfig(
    level=getattr(logging, settings.log_level, logging.INFO),
    format="%(asctime)s %(levelname)-7s %(name)s: %(message)s",
)
logger = logging.getLogger("ocr-service")

# Magic-byte signatures. The client-supplied filename and Content-Type are advisory only;
# the actual bytes decide how the payload is handled.
PDF_MAGIC = b"%PDF-"
PNG_MAGIC = b"\x89PNG\r\n\x1a\n"
JPEG_MAGIC = b"\xff\xd8\xff"


@asynccontextmanager
async def lifespan(_: FastAPI):
    # Build and warm the models once, here - never inside a request handler.
    engine.start()
    try:
        yield
    finally:
        engine.stop()


app = FastAPI(
    title="Tax Certificate OCR Service",
    description=(
        "Local PP-OCRv5 text recognition. Returns text, confidence and geometry only; "
        "field extraction happens in the .NET API."
    ),
    version="1.0.0",
    lifespan=lifespan,
)


def _error(code: str, message: str, http_status: int) -> JSONResponse:
    return JSONResponse(
        status_code=http_status,
        content=ErrorResponse(error=ErrorBody(code=code, message=message)).model_dump(),
    )


def _detect_kind(data: bytes) -> str | None:
    """Returns 'pdf', 'image' or None, based purely on the leading bytes."""
    if data.startswith(PDF_MAGIC):
        return "pdf"
    if data.startswith(PNG_MAGIC) or data.startswith(JPEG_MAGIC):
        return "image"
    return None


@app.get("/health", response_model=HealthResponse)
async def health() -> HealthResponse:
    return HealthResponse(
        status="healthy" if engine.is_ready else "starting",
        modelLoaded=engine.is_ready,
        engine=engine.description,
        device=settings.device,
    )


@app.post("/ocr", response_model=OcrResponse, responses={400: {"model": ErrorResponse}})
async def ocr(file: UploadFile = File(...)):  # noqa: B008 - FastAPI dependency idiom
    started = time.perf_counter()

    data = await file.read()
    size = len(data)

    if size == 0:
        return _error("EMPTY_FILE", "Uploaded file is empty.", status.HTTP_400_BAD_REQUEST)

    if size > settings.max_upload_bytes:
        return _error(
            "FILE_TOO_LARGE",
            f"File exceeds the {settings.max_upload_bytes} byte limit.",
            status.HTTP_413_REQUEST_ENTITY_TOO_LARGE,
        )

    kind = _detect_kind(data)
    if kind is None:
        return _error(
            "UNSUPPORTED_MEDIA_TYPE",
            "Only PDF, PNG and JPEG payloads are supported.",
            status.HTTP_415_UNSUPPORTED_MEDIA_TYPE,
        )

    # Deliberately logged without the client filename: it is untrusted and can carry paths.
    logger.info("ocr request: kind=%s bytes=%d", kind, size)

    try:
        pages = await _run(data, kind)
    except OcrBusyError:
        return _error(
            "SERVER_BUSY", "OCR service is at capacity; retry shortly.",
            status.HTTP_503_SERVICE_UNAVAILABLE,
        )
    except OcrEngineNotReadyError:
        return _error(
            "ENGINE_NOT_READY", "OCR engine is still starting.",
            status.HTTP_503_SERVICE_UNAVAILABLE,
        )
    except ImageTooLargeError:
        return _error(
            "IMAGE_TOO_LARGE", "Image resolution exceeds the configured limit.",
            status.HTTP_400_BAD_REQUEST,
        )
    except (InvalidImageError, InvalidPdfError):
        return _error(
            "INVALID_FILE", "File could not be decoded.", status.HTTP_400_BAD_REQUEST
        )
    except Exception:
        # Log the trace server-side; the response stays generic so no path or stack leaks.
        logger.exception("ocr failed")
        return _error(
            "OCR_FAILED", "OCR processing failed.", status.HTTP_500_INTERNAL_SERVER_ERROR
        )

    duration_ms = int((time.perf_counter() - started) * 1000)
    block_count = sum(len(p.blocks) for p in pages)
    logger.info("ocr done: pages=%d blocks=%d duration_ms=%d", len(pages), block_count, duration_ms)

    return OcrResponse(
        success=True, pages=pages, duration_ms=duration_ms, engine=engine.description
    )


async def _run(data: bytes, kind: str) -> List[Page]:
    pages: List[Page] = []

    if kind == "pdf":
        # render_pages is a generator, so only one rasterised page is resident at a time.
        for page_number, raw_page in render_pages(data):
            prepared, rotation = preprocess(raw_page)
            pages.append(await engine.recognize(prepared, page_number, rotation))
        if not pages:
            raise InvalidPdfError("PDF produced no renderable pages.")
    else:
        prepared, rotation = preprocess(decode_image(data))
        pages.append(await engine.recognize(prepared, 1, rotation))

    return pages
