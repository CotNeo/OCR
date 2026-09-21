"""Light, configurable image preparation.

Guiding rule: never trade OCR accuracy for a prettier intermediate image. Every step is
individually switchable, and the aggressive ones (grayscale, binarisation) default to off.
"""

from __future__ import annotations

import io
import logging

import cv2
import numpy as np
from PIL import Image, ImageOps

from .config import settings

logger = logging.getLogger(__name__)

# Pillow refuses oversized images itself; we set our own ceiling so the guard is explicit
# rather than dependent on the library default.
Image.MAX_IMAGE_PIXELS = settings.max_pixels


class ImageTooLargeError(ValueError):
    """Raised when a decoded image exceeds the configured pixel budget."""


class InvalidImageError(ValueError):
    """Raised when the bytes cannot be decoded as an image."""


def decode_image(data: bytes) -> np.ndarray:
    """Decodes image bytes to a BGR array, honouring EXIF orientation.

    Works entirely in memory: nothing is written to disk, so there is no temporary file to
    leak or clean up, and no filename from the client ever touches a path.
    """
    try:
        with Image.open(io.BytesIO(data)) as image:
            width, height = image.size
            if width * height > settings.max_pixels:
                raise ImageTooLargeError(
                    f"Image has {width * height} pixels, limit is {settings.max_pixels}."
                )

            # Phone photos carry the real orientation in EXIF only; without this a portrait
            # capture reaches the detector sideways.
            image = ImageOps.exif_transpose(image)
            rgb = image.convert("RGB")
            array = np.asarray(rgb, dtype=np.uint8)
    except ImageTooLargeError:
        raise
    except Exception as exc:  # noqa: BLE001 - message is sanitised on purpose
        raise InvalidImageError("Image could not be decoded.") from exc

    return cv2.cvtColor(array, cv2.COLOR_RGB2BGR)


def limit_size(image: np.ndarray, max_side: int | None = None) -> np.ndarray:
    """Downscales so the long edge fits `max_side`. This is the main memory lever."""
    limit = max_side or settings.max_image_side
    height, width = image.shape[:2]
    longest = max(height, width)
    if limit <= 0 or longest <= limit:
        return image

    scale = limit / longest
    return cv2.resize(
        image,
        (max(1, int(round(width * scale))), max(1, int(round(height * scale)))),
        # INTER_AREA is the correct filter for shrinking; it avoids the aliasing that makes
        # thin Turkish diacritics disappear.
        interpolation=cv2.INTER_AREA,
    )


def estimate_skew(image: np.ndarray, max_angle: float | None = None) -> float:
    """Estimates page skew in degrees via a projection-profile search.

    Text lines produce sharp peaks in the row-sum profile only when they are horizontal, so
    the angle maximising the profile variance is the deskew angle. This is more stable on
    forms than `minAreaRect`, which is easily dominated by a single long border line.

    Runs on a small copy, so the cost stays in the low tens of milliseconds.
    """
    limit = max_angle if max_angle is not None else settings.deskew_max_angle
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY) if image.ndim == 3 else image

    # Work at ~600 px on the long edge: enough structure, far cheaper to rotate repeatedly.
    height, width = gray.shape[:2]
    scale = 600 / max(height, width)
    if scale < 1.0:
        gray = cv2.resize(gray, (int(width * scale), int(height * scale)), interpolation=cv2.INTER_AREA)

    # Ink as high values, so row sums measure text density.
    binary = cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY_INV + cv2.THRESH_OTSU)[1]

    def profile_variance(angle: float) -> float:
        if abs(angle) < 1e-6:
            rotated = binary
        else:
            center = (binary.shape[1] / 2, binary.shape[0] / 2)
            matrix = cv2.getRotationMatrix2D(center, angle, 1.0)
            rotated = cv2.warpAffine(
                binary, matrix, (binary.shape[1], binary.shape[0]),
                flags=cv2.INTER_NEAREST, borderMode=cv2.BORDER_CONSTANT, borderValue=0,
            )
        return float(np.var(rotated.sum(axis=1, dtype=np.float64)))

    # Coarse sweep, then refine around the winner.
    coarse = np.arange(-limit, limit + 0.5, 0.5)
    best_angle = max(coarse, key=profile_variance)
    fine = np.arange(best_angle - 0.5, best_angle + 0.5 + 0.1, 0.1)
    best_angle = max(fine, key=profile_variance)

    return float(round(best_angle, 2))


def rotate(image: np.ndarray, angle: float) -> np.ndarray:
    """Rotates about the centre, expanding the canvas so no text is clipped."""
    if abs(angle) < 1e-6:
        return image

    height, width = image.shape[:2]
    center = (width / 2, height / 2)
    matrix = cv2.getRotationMatrix2D(center, angle, 1.0)

    cos, sin = abs(matrix[0, 0]), abs(matrix[0, 1])
    new_width = int(height * sin + width * cos)
    new_height = int(height * cos + width * sin)
    matrix[0, 2] += new_width / 2 - center[0]
    matrix[1, 2] += new_height / 2 - center[1]

    return cv2.warpAffine(
        image, matrix, (new_width, new_height),
        flags=cv2.INTER_CUBIC, borderMode=cv2.BORDER_REPLICATE,
    )


def normalize_contrast(image: np.ndarray) -> np.ndarray:
    """CLAHE on the luminance channel only, so colour and hue are untouched.

    Chosen over global histogram equalisation because levha photos are typically lit
    unevenly, and a global stretch blows out the bright half of the page.
    """
    lab = cv2.cvtColor(image, cv2.COLOR_BGR2LAB)
    lightness, a_channel, b_channel = cv2.split(lab)
    clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
    merged = cv2.merge((clahe.apply(lightness), a_channel, b_channel))
    return cv2.cvtColor(merged, cv2.COLOR_LAB2BGR)


def to_grayscale_bgr(image: np.ndarray) -> np.ndarray:
    """Removes colour but keeps 3 channels, which is what the detector expects."""
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    return cv2.cvtColor(gray, cv2.COLOR_GRAY2BGR)


def adaptive_threshold_bgr(image: np.ndarray) -> np.ndarray:
    """Local binarisation. Off by default - see `Settings.enable_adaptive_threshold`."""
    gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    binary = cv2.adaptiveThreshold(
        gray, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY, blockSize=31, C=15
    )
    return cv2.cvtColor(binary, cv2.COLOR_GRAY2BGR)


def preprocess(image: np.ndarray) -> tuple[np.ndarray, float]:
    """Runs the configured pipeline. Returns the image and the rotation actually applied."""
    result = limit_size(image)
    applied_rotation = 0.0

    if settings.enable_deskew:
        angle = estimate_skew(result)
        # Below a third of a degree, rotating costs an interpolation pass and gains nothing.
        if abs(angle) >= 0.3:
            result = rotate(result, angle)
            applied_rotation = angle
            logger.debug("deskew applied: %.2f deg", angle)

    if settings.enable_contrast:
        result = normalize_contrast(result)

    if settings.enable_adaptive_threshold:
        result = adaptive_threshold_bgr(result)
    elif settings.enable_grayscale:
        result = to_grayscale_bgr(result)

    return result, applied_rotation
