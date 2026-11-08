#!/usr/bin/env bash
# Builds dist/solvra-windows-<ver>.zip from a packaged VSIX (backend taken from the VSIX, as on macOS).
#   scripts/windows/build-bundle.sh path/to/solvra-vscode-X.Y.Z.vsix [out-dir]
set -euo pipefail
VSIX="$(realpath "${1:?usage: build-bundle.sh <vsix> [out-dir]}")"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$(realpath -m "${2:-$HERE/../../dist}")"
EXT_VERSION="$(basename "$VSIX" .vsix | sed 's/^solvra-vscode-//')"
NAME="solvra-windows-$EXT_VERSION${BUNDLE_REV:+-$BUNDLE_REV}"
mkdir -p "$OUT"

python3 -I - "$VSIX" "$HERE" "$OUT" "$NAME" "$EXT_VERSION" <<'PY'
import hashlib, io, os, subprocess, sys, zipfile
vsix, here, out, name, version = sys.argv[1:6]
files = {}  # path inside the bundle → bytes
with zipfile.ZipFile(vsix) as z:
    for info in z.infolist():
        if info.filename.startswith("extension/backend/") and not info.is_dir():
            rel = info.filename[len("extension/"):]
            if ".." in rel.split("/"): sys.exit(f"unsafe path in VSIX: {info.filename}")
            files[rel] = z.read(info)
if "backend/Solvra.dll" not in files: sys.exit("VSIX has no extension/backend/Solvra.dll")
files[os.path.basename(vsix)] = open(vsix, "rb").read()
for f in ("install.ps1", "Install Solvra.cmd", "configure-telemetry.ps1"):
    data = open(os.path.join(here, f), "rb").read()
    # Windows PowerShell 5.1 otherwise treats UTF-8 scripts as the ANSI code page.
    # Decode first to reject invalid source encoding, and emit exactly one BOM.
    files[f] = data.decode("utf-8-sig").encode("utf-8-sig") if f.endswith(".ps1") else data
files["TELEMETRY.md"] = open(os.path.join(here, "../../tools/telemetry/README.md"), "rb").read()
files["VERSION"] = (version + "\r\n").encode()
files["README.txt"] = f"""Solvra {version} for Windows 10/11

  1. Extract this zip.
  2. Double-click "Install Solvra.cmd".

That is all. It installs, for your user only (no administrator rights):
  - the .NET 8 runtime, if missing
  - the solvra command (in %LOCALAPPDATA%\\Solvra, added to your PATH)
  - the Solvra extension for VS Code
  - Git for Windows if missing (Solvra runs commands with its bash), via winget
  - ChatGPT through your Codex sign-in: installs Node.js and the Codex CLI if needed and opens
    your browser to sign in. No API key.
Then close and reopen VS Code and click the Solvra icon in the Activity Bar.

Options (from PowerShell in this folder):
  .\\install.ps1 -Model gpt-5.6-luna   another ChatGPT model
  .\\install.ps1 -NoChatGPT            skip ChatGPT; add an API key in the chat's Settings instead
  .\\install.ps1 -NoVSCode             command line only

Updating: run the next version's installer; it replaces this one and keeps your settings.
Removing: code --uninstall-extension solvra.solvra-vscode, delete %LOCALAPPDATA%\\Solvra,
and remove %LOCALAPPDATA%\\Solvra\\bin from your user PATH.
""".replace("\n", "\r\n").encode()
sums = "".join(f"{hashlib.sha256(data).hexdigest()}  {path}\n" for path, data in sorted(files.items()))
files["SHA256SUMS"] = sums.encode()
target = os.path.join(out, name + ".zip")
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as z:
    for path, data in sorted(files.items()):
        info = zipfile.ZipInfo(f"{name}/{path}", date_time=(2026, 1, 1, 0, 0, 0))
        info.external_attr = 0o644 << 16
        info.compress_type = zipfile.ZIP_DEFLATED
        z.writestr(info, data)
print(f"bundle: {target} ({len(files)} files)")
PY
(cd "$OUT" && sha256sum "$NAME.zip" > "$NAME.zip.sha256" && cat "$NAME.zip.sha256")
