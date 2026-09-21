#!/usr/bin/env bash
# End-to-end check against both running services.
set -euo pipefail

API="${API:-http://localhost:5000}"
OCR="${OCR:-http://127.0.0.1:8001}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SAMPLES="$(cd "$SCRIPT_DIR/../tests/samples" && pwd)"

echo "==> OCR service health"
curl -fsS "$OCR/health"; echo

echo "==> API health"
curl -fsS "$API/health"; echo

echo "==> analyze sample_levha.pdf"
curl -fsS -X POST "$API/api/vergi-levhasi/analyze" \
  -F "file=@$SAMPLES/sample_levha.pdf"; echo

echo "==> reject a non-certificate document (expect HTTP 422)"
curl -sS -o /dev/null -w "http=%{http_code}\n" -X POST "$API/api/vergi-levhasi/analyze" \
  -F "file=@$SAMPLES/not_a_levha.png"

echo "==> reject an unsupported type (expect HTTP 415)"
printf 'not an image' > /tmp/tc-ocr-smoke.txt
curl -sS -o /dev/null -w "http=%{http_code}\n" -X POST "$API/api/vergi-levhasi/analyze" \
  -F "file=@/tmp/tc-ocr-smoke.txt"
rm -f /tmp/tc-ocr-smoke.txt
