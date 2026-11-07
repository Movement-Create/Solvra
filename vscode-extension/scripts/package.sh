#!/bin/bash
# Builds solvra-vscode-<version>.vsix with the backend inside it (extension/backend/Solvra.dll),
# so the extension works without a separate Solvra install. Needs the .NET 8 SDK and Node.
#   vscode-extension/scripts/package.sh [out-dir]
set -euo pipefail
EXT="$(cd "$(dirname "$0")/.." && pwd)"
ROOT="$(cd "$EXT/.." && pwd)"
OUT="${1:-$EXT}"
VERSION="$(node -p "require('$EXT/package.json').version")"
REV="$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)"

echo "==> backend (framework-dependent .NET 8, all platforms' native libraries)"
rm -rf "$EXT/backend"
dotnet publish "$ROOT/src/Solvra/Solvra.csproj" -c Release -o "$EXT/backend" --nologo -v q \
  -p:UseAppHost=false -p:DebugType=none -p:GenerateDocumentationFile=false -p:SourceRevisionId="$REV"
dotnet "$EXT/backend/Solvra.dll" --version

echo "==> dependencies"
(cd "$EXT" && npm ci --no-audit --no-fund >/dev/null)
(cd "$EXT/webview-ui" && npm ci --no-audit --no-fund >/dev/null)

echo "==> vsix (vscode:prepublish builds the webview and the extension)"
mkdir -p "$OUT"
(cd "$EXT" && npx vsce package --no-dependencies --skip-license -o "$OUT/solvra-vscode-$VERSION.vsix")
echo "vsix: $OUT/solvra-vscode-$VERSION.vsix"
