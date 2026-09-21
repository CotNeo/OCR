"""Wire contracts for the OCR service.

Deliberately dumb: text, confidence and geometry only. No vergi levhası concepts live here -
field extraction is the .NET parser's job.
"""

from __future__ import annotations

from typing import List, Optional

from pydantic import BaseModel, Field


class Box(BaseModel):
    """Axis-aligned bounding rectangle in page pixels, origin top-left."""

    x1: int
    y1: int
    x2: int
    y2: int


class Block(BaseModel):
    text: str
    confidence: float = Field(ge=0.0, le=1.0)
    box: Box
    # Detection quadrilateral as [[x, y], ...]. Kept alongside `box` so a caller that cares
    # about rotation has it, while simple consumers can ignore it.
    polygon: Optional[List[List[int]]] = None


class Page(BaseModel):
    page: int
    width: int
    height: int
    # Rotation applied by deskewing, in degrees. 0.0 when deskew is disabled or unnecessary.
    applied_rotation: float = 0.0
    blocks: List[Block]


class OcrResponse(BaseModel):
    success: bool
    pages: List[Page]
    duration_ms: int
    engine: str


class HealthResponse(BaseModel):
    status: str
    modelLoaded: bool  # noqa: N815 - camelCase is part of the agreed contract
    engine: str
    device: str


class ErrorBody(BaseModel):
    code: str
    message: str


class ErrorResponse(BaseModel):
    success: bool = False
    error: ErrorBody
