"""PDF rasterisation via pypdfium2.

pypdfium2 was chosen over PyMuPDF because it is permissively licensed (BSD-3-Clause /
Apache-2.0 rather than AGPL), ships prebuilt macOS arm64 wheels, and needs no system
Poppler install the way `pdf2image` does.
"""

from __future__ import annotations

import logging
from typing import Iterator

import numpy as np
import pypdfium2 as pdfium

from .config import settings

logger = logging.getLogger(__name__)

# PDF user space is 72 units per inch; pypdfium2 renders by scale factor.
POINTS_PER_INCH = 72.0


class InvalidPdfError(ValueError):
    """Raised when the bytes cannot be opened as a PDF."""


def render_pages(data: bytes, dpi: int | None = None) -> Iterator[tuple[int, np.ndarray]]:
    """Yields `(page_number, BGR array)` one page at a time.

    A generator on purpose: a 5-page A4 document at 220 DPI is roughly 5 x 17 MB of RGB, and
    materialising the whole list at once is a real risk on an 8 GB machine. Each page is
    handed to the caller, OCR'd and released before the next is rendered.
    """
    target_dpi = dpi or settings.pdf_dpi
    scale = target_dpi / POINTS_PER_INCH

    try:
        document = pdfium.PdfDocument(data)
    except Exception as exc:  # noqa: BLE001 - never surface library internals
        raise InvalidPdfError("PDF could not be opened.") from exc

    try:
        total = len(document)
        if total == 0:
            raise InvalidPdfError("PDF contains no pages.")

        page_count = min(total, settings.max_pages)
        if total > page_count:
            logger.warning("pdf has %d pages, processing first %d", total, page_count)

        for index in range(page_count):
            page = document[index]
            try:
                effective_scale = _clamp_scale(page, scale)
                bitmap = page.render(scale=effective_scale)
                try:
                    rgb = bitmap.to_numpy()
                    # to_numpy() views the bitmap buffer; copy before the bitmap is closed.
                    bgr = _rgb_to_bgr(rgb)
                finally:
                    bitmap.close()
            finally:
                page.close()

            yield index + 1, bgr
    finally:
        document.close()


def _clamp_scale(page: "pdfium.PdfPage", scale: float) -> float:
    """Lowers the render scale if the requested DPI would blow the pixel budget."""
    width_pt, height_pt = page.get_size()
    pixels = (width_pt * scale) * (height_pt * scale)
    if pixels <= settings.max_pixels:
        return scale

    safe = (settings.max_pixels / (width_pt * height_pt)) ** 0.5
    logger.warning("clamping pdf render scale %.3f -> %.3f to stay within pixel budget", scale, safe)
    return safe


def _rgb_to_bgr(rgb: np.ndarray) -> np.ndarray:
    # pypdfium2 renders RGBA or RGB depending on the page; drop alpha, then reverse channels.
    if rgb.ndim == 3 and rgb.shape[2] == 4:
        rgb = rgb[:, :, :3]
    return np.ascontiguousarray(rgb[:, :, ::-1])
