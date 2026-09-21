# ---------------------------------------------------------------------------------------
# x86_64 ONLY.
#
# paddlepaddle publishes no linux/aarch64 wheel (3.3.1 ships macosx_11_0_arm64,
# manylinux1_x86_64 and win_amd64 only). On an Apple Silicon Mac this image can therefore
# only run under x86_64 emulation, where CPU inference is several times slower and uses far
# more memory - a bad trade on an 8 GB machine.
#
# On macOS, run the OCR service natively instead:  ./scripts/run-ocr-service.sh
# ---------------------------------------------------------------------------------------
FROM python:3.12-slim AS runtime

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    OCR_ENABLE_MKLDNN=false \
    OCR_CPU_THREADS=4 \
    OCR_MAX_CONCURRENCY=1

# libgl/libglib are needed by opencv; ca-certificates for the one-time model download.
RUN apt-get update && apt-get install --no-install-recommends -y \
        libgl1 libglib2.0-0 ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt

COPY app/ app/

RUN useradd --uid 10001 --create-home ocruser \
    && mkdir -p /home/ocruser/.paddlex \
    && chown -R ocruser:ocruser /home/ocruser /app
USER 10001

EXPOSE 8001
ENTRYPOINT ["python", "-m", "uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8001"]
