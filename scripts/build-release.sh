#!/usr/bin/env bash
# Builds every release file into dist/: the VSIX (with the backend inside), the macOS bundle and
# the Windows bundle, plus SHA256SUMS. Needs the .NET 8 SDK, Node.js and Python 3.
#   scripts/build-release.sh [out-dir]
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$(realpath -m "${1:-$ROOT/dist}")"
VERSION="$(node -p "require('$ROOT/vscode-extension/package.json').version")"
rm -rf "$OUT"; mkdir -p "$OUT"
"$ROOT/vscode-extension/scripts/package.sh" "$OUT"
VSIX="$OUT/solvra-vscode-$VERSION.vsix"
"$ROOT/scripts/macos/build-bundle.sh" "$VSIX" "$OUT"
"$ROOT/scripts/windows/build-bundle.sh" "$VSIX" "$OUT"
# Also ship the small helper so existing installations can repair telemetry independently.
python3 -I - "$ROOT/scripts/windows/configure-telemetry.ps1" "$OUT/configure-telemetry.ps1" <<'PY'
from pathlib import Path
import sys
Path(sys.argv[2]).write_bytes(Path(sys.argv[1]).read_text(encoding='utf-8-sig').encode('utf-8-sig'))
PY
rm -rf "$OUT/solvra-macos-$VERSION"   # staging folder
(cd "$OUT" && rm -f ./*.sha256 && sha256sum solvra-vscode-*.vsix solvra-macos-*.tar.gz solvra-windows-*.zip configure-telemetry.ps1 > SHA256SUMS && cat SHA256SUMS)
