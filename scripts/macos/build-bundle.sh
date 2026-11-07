#!/usr/bin/env bash
# Builds dist/solvra-macos-<ver>.tar.gz from a packaged VSIX.
# The CLI backend is extracted from the VSIX itself, so the CLI and the extension run
# byte-identical binaries (the backend is framework-dependent and ships osx-arm64/osx-x64 natives).
#
#   scripts/macos/build-bundle.sh path/to/solvra-vscode-X.Y.Z.vsix [out-dir]
set -euo pipefail

VSIX="$(realpath "${1:?usage: build-bundle.sh <vsix> [out-dir]}")"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$(realpath -m "${2:-$HERE/../../dist}")"
EXT_VERSION="$(basename "$VSIX" .vsix | sed 's/^solvra-vscode-//')"
NAME="solvra-macos-$EXT_VERSION${BUNDLE_REV:+-$BUNDLE_REV}"   # BUNDLE_REV=r2 for a re-spin
STAGE="$OUT/$NAME"

rm -rf "$STAGE"
mkdir -p "$STAGE"

# Extract extension/backend/** from the VSIX (zip) without trusting archive paths.
python3 -I - "$VSIX" "$STAGE/backend" <<'PY'
import os, sys, zipfile
vsix, dest = sys.argv[1], os.path.realpath(sys.argv[2])
prefix = "extension/backend/"
count = 0
with zipfile.ZipFile(vsix) as z:
    for info in z.infolist():
        if not info.filename.startswith(prefix) or info.is_dir():
            continue
        target = os.path.realpath(os.path.join(dest, info.filename[len(prefix):]))
        if not target.startswith(dest + os.sep):
            sys.exit(f"unsafe path in VSIX: {info.filename}")
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with z.open(info) as src, open(target, "wb") as dst:
            dst.write(src.read())
        count += 1
if not os.path.isfile(os.path.join(dest, "Solvra.dll")):
    sys.exit("VSIX has no extension/backend/Solvra.dll")
print(f"extracted {count} backend files")
PY

BACKEND_VERSION="$(dotnet "$STAGE/backend/Solvra.dll" --version)"
SHORT_SHA="$(echo "$BACKEND_VERSION" | sed -n 's/.*+\([0-9a-f]\{12\}\).*/\1/p')"
echo "$EXT_VERSION-${SHORT_SHA:-local}" > "$STAGE/VERSION"

cp "$VSIX" "$STAGE/"
install -m 755 "$HERE/install.sh" "$STAGE/install.sh"
install -m 755 "$HERE/configure-openai.sh" "$STAGE/configure-openai.sh"
install -m 755 "$HERE/configure-codex.sh" "$STAGE/configure-codex.sh"
install -m 644 "$HERE/vscode-settings.sh" "$STAGE/vscode-settings.sh"
cat > "$STAGE/README.txt" <<EOF
Solvra for macOS — extension $EXT_VERSION, backend $BACKEND_VERSION

  cd $NAME
  ./install.sh

That is all. install.sh installs the .NET 8 runtime (if missing), the Solvra CLI
(~/.local/bin/solvra) and the VS Code extension, then sets up ChatGPT through your Codex
sign-in (it installs the Codex CLI if needed and opens your browser to sign in). No API key.
Then quit and reopen VS Code and click the Solvra icon.

  ./install.sh --model gpt-5.6-luna   # another ChatGPT model
  ./install.sh --no-chatgpt           # skip ChatGPT; use ./configure-openai.sh for an API key
  ./configure-codex.sh                # redo the ChatGPT setup later (e.g. after signing out)

All scripts accept --help. Updating: run the newer bundle's ./install.sh; it replaces the
previous version and keeps your settings.
EOF
(cd "$STAGE" && find . -type f ! -name SHA256SUMS -print0 | sort -z | xargs -0 sha256sum > SHA256SUMS)

tar -C "$OUT" --owner=0 --group=0 --numeric-owner -czf "$OUT/$NAME.tar.gz" "$NAME"
(cd "$OUT" && sha256sum "$NAME.tar.gz" > "$NAME.tar.gz.sha256")
echo "bundle: $OUT/$NAME.tar.gz"
cat "$OUT/$NAME.tar.gz.sha256"
