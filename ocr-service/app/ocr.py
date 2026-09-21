"""PaddleOCR engine lifecycle and inference.

Two hard requirements drive the design:

1. The model is built **once**, at startup, never per request.
2. Inference is synchronous and CPU-bound, so it must never run on the event loop.

Both are handled by an engine pool: N pre-built engines behind a queue, each checked out by a
worker thread. N defaults to 1 because a Paddle inference predictor is not documented as
thread-safe and each engine costs roughly 450 MB resident.
"""

from __future__ import annotations

import asyncio
import logging
import queue
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from typing import Any, List, Optional

import cv2
import numpy as np

from .config import settings
from .models import Block, Box, Page

logger = logging.getLogger(__name__)


class OcrBusyError(RuntimeError):
    """Raised when more work is queued than `OCR_MAX_QUEUE` allows."""


class OcrEngineNotReadyError(RuntimeError):
    """Raised when a request arrives before startup finished."""


def _build_engine() -> Any:
    """Constructs one PaddleOCR pipeline with explicitly pinned models."""
    from paddleocr import PaddleOCR

    logger.info(
        "building engine: det=%s rec=%s device=%s threads=%d mkldnn=%s",
        settings.detection_model, settings.recognition_model,
        settings.device, settings.cpu_threads, settings.enable_mkldnn,
    )

    kwargs: dict[str, Any] = {
        # These three auxiliary models are pure overhead here: a vergi levhası is upright
        # after EXIF handling and our own deskew, and each one loaded would add resident
        # memory plus latency for no accuracy gain.
        "use_doc_orientation_classify": False,
        "use_doc_unwarping": False,
        "use_textline_orientation": False,
        "device": settings.device,
        "cpu_threads": settings.cpu_threads,
        "enable_mkldnn": settings.enable_mkldnn,
    }

    if settings.detection_model and settings.recognition_model:
        # PaddleOCR ignores `lang`/`ocr_version` once explicit model names are given, and warns
        # about it. Passing only the names keeps the log clean and the selection unambiguous:
        # `latin_PP-OCRv5_mobile_rec` *is* the PP-OCRv5 Latin/Turkish recogniser that
        # `lang="tr"` would resolve to, paired with the mobile detector instead of the server one.
        kwargs["text_detection_model_name"] = settings.detection_model
        kwargs["text_recognition_model_name"] = settings.recognition_model
    else:
        # Blank either model name to hand model selection back to PaddleOCR's own resolver.
        kwargs["lang"] = settings.lang
        kwargs["ocr_version"] = settings.ocr_version

    return PaddleOCR(**kwargs)


def _warmup_image() -> np.ndarray:
    """A small image that contains real text.

    This matters: PaddleOCR builds its predictors lazily on first use, and a *blank* warmup
    frame only initialises the detector - the recogniser is never reached because no text is
    found, leaving a multi-second stall on the first real request.
    """
    canvas = np.full((160, 640, 3), 255, dtype=np.uint8)
    cv2.putText(canvas, "VERGI 1234567890", (20, 100), cv2.FONT_HERSHEY_SIMPLEX, 1.6, (0, 0, 0), 3)
    return canvas


class OcrEngine:
    """Owns the engine pool and exposes a single async entry point."""

    def __init__(self) -> None:
        self._pool: "queue.Queue[Any]" = queue.Queue()
        self._executor: Optional[ThreadPoolExecutor] = None
        self._semaphore: Optional[asyncio.Semaphore] = None
        self._waiting = 0
        self._waiting_lock = threading.Lock()
        self._ready = False

    @property
    def is_ready(self) -> bool:
        return self._ready

    @property
    def description(self) -> str:
        return f"{settings.ocr_version}/{settings.detection_model}+{settings.recognition_model}"

    def start(self) -> None:
        """Builds every engine and warms it. Called once from the FastAPI lifespan hook."""
        started = time.perf_counter()

        for slot in range(settings.max_concurrency):
            engine = _build_engine()
            if settings.warmup_on_startup:
                warm_started = time.perf_counter()
                engine.predict(_warmup_image())
                logger.info(
                    "engine %d warmed in %.2fs", slot, time.perf_counter() - warm_started
                )
            self._pool.put(engine)

        self._executor = ThreadPoolExecutor(
            max_workers=settings.max_concurrency, thread_name_prefix="ocr"
        )
        self._semaphore = asyncio.Semaphore(settings.max_concurrency)
        self._ready = True

        logger.info(
            "ocr engine ready: %d instance(s) in %.2fs",
            settings.max_concurrency, time.perf_counter() - started,
        )

    def stop(self) -> None:
        if self._executor is not None:
            self._executor.shutdown(wait=True)
            self._executor = None

        while not self._pool.empty():
            self._pool.get_nowait()

        self._ready = False

    async def recognize(self, image: np.ndarray, page_number: int, applied_rotation: float) -> Page:
        """Runs one page through the engine without blocking the event loop."""
        if not self._ready or self._executor is None or self._semaphore is None:
            raise OcrEngineNotReadyError("OCR engine is not ready.")

        # Shed load early rather than letting an unbounded queue eat all the memory.
        with self._waiting_lock:
            if self._waiting >= settings.max_queue:
                raise OcrBusyError("OCR queue is full.")
            self._waiting += 1

        try:
            async with self._semaphore:
                loop = asyncio.get_running_loop()
                return await loop.run_in_executor(
                    self._executor, self._recognize_sync, image, page_number, applied_rotation
                )
        finally:
            with self._waiting_lock:
                self._waiting -= 1

    def _recognize_sync(self, image: np.ndarray, page_number: int, applied_rotation: float) -> Page:
        engine = self._pool.get()
        try:
            raw = engine.predict(image)
        finally:
            # Always return the engine, even if inference raised, or the pool drains.
            self._pool.put(engine)

        height, width = image.shape[:2]
        return Page(
            page=page_number,
            width=int(width),
            height=int(height),
            applied_rotation=applied_rotation,
            blocks=_to_blocks(raw),
        )


def _to_blocks(raw: Any) -> List[Block]:
    """Maps a PaddleOCR 3.x result into the wire contract.

    `predict()` returns a list with one `OCRResult` per input image. Bounding boxes are
    derived from `rec_polys` rather than `rec_boxes`, because `rec_boxes` comes back as an
    empty `(0,)` array when nothing is detected and is absent entirely once document
    unwarping is in play - the polygons are always present and always authoritative.
    """
    if not raw:
        return []

    result = raw[0]
    texts = result.get("rec_texts") or []
    scores = result.get("rec_scores") or []
    polys = result.get("rec_polys")
    if polys is None or len(polys) == 0:
        polys = result.get("dt_polys") or []

    blocks: List[Block] = []
    for index, text in enumerate(texts):
        if text is None or not str(text).strip():
            continue

        polygon = polys[index] if index < len(polys) else None
        if polygon is None:
            continue

        points = np.asarray(polygon, dtype=np.float64).reshape(-1, 2)
        if points.size == 0:
            continue

        x1, y1 = points.min(axis=0)
        x2, y2 = points.max(axis=0)
        confidence = float(scores[index]) if index < len(scores) else 0.0

        blocks.append(
            Block(
                text=str(text),
                # Scores are occasionally a hair above 1.0 in float32; clamp for the contract.
                confidence=max(0.0, min(1.0, confidence)),
                box=Box(x1=int(round(x1)), y1=int(round(y1)), x2=int(round(x2)), y2=int(round(y2))),
                polygon=[[int(round(px)), int(round(py))] for px, py in points.tolist()],
            )
        )

    # Reading order: top-to-bottom, then left-to-right. The .NET parser relies on this for
    # multi-line values such as the address.
    blocks.sort(key=lambda b: (b.box.y1, b.box.x1))
    return blocks


engine = OcrEngine()
