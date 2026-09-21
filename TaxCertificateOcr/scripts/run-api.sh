#!/usr/bin/env bash
# Starts the ASP.NET Core API on http://localhost:5000.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$(cd "$SCRIPT_DIR/.." && pwd)"

exec dotnet run --project src/TaxCertificate.Api --launch-profile "${1:-http}"
