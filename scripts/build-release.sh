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
rm -rf "$OUT/solvra-macos-$VERSION"   # staging folder
(cd "$OUT" && rm -f ./*.sha256 && sha256sum solvra-vscode-*.vsix solvra-macos-*.tar.gz solvra-windows-*.zip > SHA256SUMS && cat SHA256SUMS)
