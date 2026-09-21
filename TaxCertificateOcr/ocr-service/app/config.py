"""Environment-driven settings for the OCR service.

Every knob that affects memory or CPU is exposed here, because the primary target is a
MacBook Pro M1 with 8 GB of unified memory.
"""

from __future__ import annotations

import os
import platform
from dataclasses import dataclass, field


def _env_bool(name: str, default: bool) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on"}


def _env_int(name: str, default: int) -> int:
    raw = os.getenv(name)
    if raw is None or not raw.strip():
        return default
    try:
        return int(raw)
    except ValueError:
        return default


def _default_enable_mkldnn() -> bool:
    """oneDNN/MKL-DNN is an x86 acceleration path and must stay off on Apple Silicon.

    PaddleOCR enables it by default. On CPU inference it was observed to abort with
    ``NotImplementedError: ConvertPirAttribute2RuntimeAttribute not support
    [pir::ArrayAttribute<pir::DoubleAttribute>]`` from ``onednn_instruction.cc``, so the
    default here is inverted: off unless explicitly requested.
    """
    return False


def _default_cpu_threads() -> int:
    """PaddleOCR defaults to 10 threads; the M1 has 4 performance + 4 efficiency cores.

    Oversubscribing thrashes the shared memory bandwidth, so cap at 4.
    """
    cpu_count = os.cpu_count() or 4
    return max(1, min(4, cpu_count))


@dataclass(frozen=True)
class Settings:
    # --- model selection -------------------------------------------------------------
    lang: str = os.getenv("OCR_LANG", "tr")
    ocr_version: str = os.getenv("OCR_VERSION", "PP-OCRv5")

    # `lang="tr"` alone resolves the *server* detection model (~7x slower than mobile on
    # CPU with no accuracy gain on these documents), so the mobile pair is pinned here.
    detection_model: str = os.getenv("OCR_DET_MODEL", "PP-OCRv5_mobile_det")
    recognition_model: str = os.getenv("OCR_REC_MODEL", "latin_PP-OCRv5_mobile_rec")

    # --- runtime ---------------------------------------------------------------------
    device: str = os.getenv("OCR_DEVICE", "cpu")
    cpu_threads: int = _env_int("OCR_CPU_THREADS", _default_cpu_threads())
    enable_mkldnn: bool = _env_bool("OCR_ENABLE_MKLDNN", _default_enable_mkldnn())

    # Concurrency: one engine instance per permit. A Paddle inference predictor is not
    # documented as thread-safe, so parallelism means more engines, and each costs roughly
    # 450 MB of resident memory. Keep this at 1 on an 8 GB machine.
    max_concurrency: int = max(1, _env_int("OCR_MAX_CONCURRENCY", 1))
    max_queue: int = max(1, _env_int("OCR_MAX_QUEUE", 8))

    # --- input limits ----------------------------------------------------------------
    max_upload_bytes: int = _env_int("OCR_MAX_UPLOAD_BYTES", 10 * 1024 * 1024)
    max_pages: int = _env_int("OCR_MAX_PAGES", 5)
    pdf_dpi: int = _env_int("OCR_PDF_DPI", 220)

    # 1600 px on the long edge measured best on synthetic levha samples: same exact-match
    # rate as full resolution, highest mean confidence, ~12% less peak RSS.
    max_image_side: int = _env_int("OCR_MAX_IMAGE_SIDE", 1600)

    # Guard against decompression bombs before any full decode happens.
    max_pixels: int = _env_int("OCR_MAX_PIXELS", 40_000_000)

    # --- preprocessing toggles -------------------------------------------------------
    enable_deskew: bool = _env_bool("OCR_ENABLE_DESKEW", True)
    enable_contrast: bool = _env_bool("OCR_ENABLE_CONTRAST", True)
    enable_grayscale: bool = _env_bool("OCR_ENABLE_GRAYSCALE", False)

    # Binarisation is off by default: PP-OCR is trained on natural images and hard
    # thresholding measurably hurts recognition on clean scans.
    enable_adaptive_threshold: bool = _env_bool("OCR_ENABLE_ADAPTIVE_THRESHOLD", False)

    deskew_max_angle: float = float(os.getenv("OCR_DESKEW_MAX_ANGLE", "8.0"))

    # --- misc -------------------------------------------------------------------------
    log_level: str = os.getenv("OCR_LOG_LEVEL", "INFO").upper()
    warmup_on_startup: bool = _env_bool("OCR_WARMUP", True)

    @property
    def is_apple_silicon(self) -> bool:
        return platform.system() == "Darwin" and platform.machine() == "arm64"


settings = Settings()
