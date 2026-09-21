#!/usr/bin/env bash
# Starts the Python OCR service. Creates and populates .venv on first run.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SERVICE_DIR="$(cd "$SCRIPT_DIR/../ocr-service" && pwd)"
cd "$SERVICE_DIR"

if [[ ! -d .venv ]]; then
  echo "==> creating virtual environment"
  python3 -m venv .venv
  ./.venv/bin/python -m pip install --upgrade pip setuptools wheel
  echo "==> installing requirements (first run downloads ~1 GB of wheels)"
  ./.venv/bin/python -m pip install -r requirements.txt
fi

# oneDNN must stay off: it is an x86 path and aborts on Apple Silicon CPU inference.
export OCR_ENABLE_MKLDNN="${OCR_ENABLE_MKLDNN:-false}"
export OCR_CPU_THREADS="${OCR_CPU_THREADS:-4}"
export OCR_MAX_CONCURRENCY="${OCR_MAX_CONCURRENCY:-1}"

# Loopback by default: the OCR service has no authentication and should not be exposed.
# Set OCR_HOST=0.0.0.0 only when something outside the host must reach it - for example a
# containerised .NET API connecting through host.docker.internal.
OCR_HOST="${OCR_HOST:-127.0.0.1}"
OCR_PORT="${OCR_PORT:-8001}"

echo "==> starting OCR service on http://${OCR_HOST}:${OCR_PORT} (first start downloads ~90 MB of models)"
exec ./.venv/bin/python -m uvicorn app.main:app --host "$OCR_HOST" --port "$OCR_PORT"
