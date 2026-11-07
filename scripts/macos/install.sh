#!/bin/bash
# Solvra installer for macOS (Apple Silicon and Intel).
#
# Run from the extracted solvra-macos bundle:
#   ./install.sh                 # CLI + VS Code extension (installs .NET 8 runtime if missing)
#   ./install.sh --no-vscode     # CLI only
#   ./install.sh --no-cli        # VS Code extension only
#   ./install.sh --no-dotnet     # never download .NET; fail if it is missing
#
# Installs (per user, no sudo):
#   ~/.local/opt/solvra/releases/<version>   backend (framework-dependent .NET 8 build)
#   ~/.local/opt/solvra/current              symlink to the active release
#   ~/.local/bin/solvra                      CLI wrapper
#   VS Code extension solvra.solvra-vscode   via `code --install-extension`
#
# Compatible with the macOS system bash (3.2).
set -euo pipefail

PREFIX="${SOLVRA_PREFIX:-$HOME/.local}"
INSTALL_CLI=1
INSTALL_VSCODE=1
ALLOW_DOTNET_DOWNLOAD=1
MARKER="# >>> solvra installer >>>"
MARKER_END="# <<< solvra installer <<<"

usage() { sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'; }

while [ $# -gt 0 ]; do
  case "$1" in
    --no-cli) INSTALL_CLI=0 ;;
    --no-vscode) INSTALL_VSCODE=0 ;;
    --no-dotnet) ALLOW_DOTNET_DOWNLOAD=0 ;;
    --prefix) shift; PREFIX="${1:?--prefix needs a directory}" ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

say()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

BUNDLE_DIR="$(cd "$(dirname "$0")" && pwd)"
BACKEND_SRC="$BUNDLE_DIR/backend"
VERSION="$(cat "$BUNDLE_DIR/VERSION" 2>/dev/null || echo unknown)"
VSIX="$(ls "$BUNDLE_DIR"/solvra-vscode-*.vsix 2>/dev/null | head -n 1 || true)"

if [ "$(uname -s)" != "Darwin" ] && [ "${SOLVRA_INSTALL_ALLOW_ANY_OS:-0}" != "1" ]; then
  die "This installer is for macOS. (Linux hosts: install the VSIX directly.)"
fi
[ -f "$BACKEND_SRC/Solvra.dll" ] || die "backend/Solvra.dll not found next to install.sh. Run it from the extracted bundle."

# Files fetched through a browser carry the quarantine flag, which can make Gatekeeper
# block the bundled native library (libQuestPdfSkia.dylib). scp/curl copies do not.
if command -v xattr >/dev/null 2>&1; then
  xattr -dr com.apple.quarantine "$BUNDLE_DIR" 2>/dev/null || true
fi

# ---------------------------------------------------------------- .NET 8 runtime
has_net8() { "$1" --list-runtimes 2>/dev/null | grep -q '^Microsoft\.NETCore\.App 8\.'; }

# Resolve file and directory symlinks (macOS readlink has no -f before 12.3).
resolve_path() {
  local p="$1" d
  while [ -L "$p" ]; do
    d="$(cd "$(dirname "$p")" && pwd -P)"
    p="$(readlink "$p")"
    case "$p" in /*) ;; *) p="$d/$p" ;; esac
  done
  echo "$(cd "$(dirname "$p")" && pwd -P)/$(basename "$p")"
}

find_dotnet() {
  local c
  for c in "${DOTNET_ROOT:+$DOTNET_ROOT/dotnet}" "$(command -v dotnet 2>/dev/null || true)" \
           /usr/local/share/dotnet/dotnet /opt/homebrew/opt/dotnet@8/libexec/dotnet \
           /usr/local/opt/dotnet@8/libexec/dotnet "$HOME/.dotnet/dotnet"; do
    if [ -n "$c" ] && [ -x "$c" ] && has_net8 "$c"; then
      # e.g. /usr/local/bin/dotnet -> /usr/local/share/dotnet/dotnet, so DOTNET_ROOT is right.
      resolve_path "$c"
      return 0
    fi
  done
  return 1
}

DOTNET="$(find_dotnet || true)"
if [ -z "$DOTNET" ]; then
  [ "$ALLOW_DOTNET_DOWNLOAD" = "1" ] || die ".NET 8 runtime not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and re-run."
  say ".NET 8 runtime not found; installing it to ~/.dotnet with Microsoft's dotnet-install.sh"
  TMP="$(mktemp -d)"
  trap 'rm -rf "$TMP"' EXIT
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$TMP/dotnet-install.sh" \
    || die "Could not download dotnet-install.sh. Install .NET 8 manually: https://dotnet.microsoft.com/download/dotnet/8.0"
  bash "$TMP/dotnet-install.sh" --channel 8.0 --runtime dotnet --install-dir "$HOME/.dotnet" --no-path
  DOTNET="$HOME/.dotnet/dotnet"
  has_net8 "$DOTNET" || die ".NET 8 install did not produce a working runtime at $DOTNET"
fi
DOTNET_DIR="$(dirname "$DOTNET")"
say "Using .NET: $DOTNET ($("$DOTNET" --list-runtimes | grep '^Microsoft\.NETCore\.App 8\.' | tail -n 1 | awk '{print $2}'))"

# ---------------------------------------------------------------- backend + CLI
OPT="$PREFIX/opt/solvra"
RELEASE="$OPT/releases/$VERSION"
BIN="$PREFIX/bin"

if [ "$INSTALL_CLI" = "1" ]; then
  say "Installing Solvra backend $VERSION to $RELEASE"
  mkdir -p "$OPT/releases" "$BIN"
  rm -rf "$RELEASE.tmp"
  mkdir -p "$RELEASE.tmp"
  cp -R "$BACKEND_SRC/." "$RELEASE.tmp/"
  rm -rf "$RELEASE"
  mv "$RELEASE.tmp" "$RELEASE"
  ln -sfn "releases/$VERSION" "$OPT/current"

  WRAPPER="$BIN/solvra"
  if [ -e "$WRAPPER" ] && ! grep -q 'solvra-macos-installer' "$WRAPPER" 2>/dev/null; then
    warn "$WRAPPER exists and was not created by this installer; saving it as $WRAPPER.bak"
    mv "$WRAPPER" "$WRAPPER.bak"
  fi
  cat > "$WRAPPER" <<EOF
#!/bin/bash
# solvra-macos-installer: launches the Solvra backend with the .NET 8 runtime.
export DOTNET_ROOT="\${DOTNET_ROOT:-$DOTNET_DIR}"
# Provider settings (written by configure-openai.sh; the API key itself stays in the Keychain).
if [ -r "\$HOME/.config/solvra/env.sh" ]; then . "\$HOME/.config/solvra/env.sh"; fi
exec "$DOTNET" "$OPT/current/Solvra.dll" "\$@"
EOF
  chmod 755 "$WRAPPER"

  INSTALLED_VERSION="$("$WRAPPER" --version 2>&1)" || die "Solvra failed to start: $INSTALLED_VERSION"
  say "solvra --version: $INSTALLED_VERSION"
fi

# ---------------------------------------------------------------- shell PATH
case "$(basename "${SHELL:-/bin/zsh}")" in
  bash) PROFILE="$HOME/.bash_profile" ;;
  *) PROFILE="$HOME/.zprofile" ;;
esac
NEED_PATH_BLOCK=0
case ":$PATH:" in *":$BIN:"*) ;; *) [ "$INSTALL_CLI" = "1" ] && NEED_PATH_BLOCK=1 ;; esac
case "$DOTNET_DIR" in "$HOME/.dotnet") NEED_PATH_BLOCK=1 ;; esac
if [ "$NEED_PATH_BLOCK" = "1" ] && ! grep -qF "$MARKER" "$PROFILE" 2>/dev/null; then
  say "Adding PATH entries to $PROFILE"
  {
    echo ""
    echo "$MARKER"
    echo "export PATH=\"$BIN:\$PATH\""
    if [ "$DOTNET_DIR" = "$HOME/.dotnet" ]; then
      echo "export DOTNET_ROOT=\"\$HOME/.dotnet\""
      echo "export PATH=\"\$HOME/.dotnet:\$PATH\""
    fi
    echo "$MARKER_END"
  } >> "$PROFILE"
fi

# ---------------------------------------------------------------- VS Code extension
find_code() {
  local c
  for c in "$(command -v code 2>/dev/null || true)" \
           "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code" \
           "$HOME/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code" \
           "/Applications/Visual Studio Code - Insiders.app/Contents/Resources/app/bin/code" \
           "$(command -v cursor 2>/dev/null || true)"; do
    if [ -n "$c" ] && [ -x "$c" ]; then echo "$c"; return 0; fi
  done
  return 1
}

VSCODE_OK=0
if [ "$INSTALL_VSCODE" = "1" ]; then
  [ -n "$VSIX" ] || die "No solvra-vscode-*.vsix found in the bundle."
  CODE="$(find_code || true)"
  if [ -z "$CODE" ]; then
    warn "VS Code not found in /Applications. Install it from https://code.visualstudio.com, then either"
    warn "re-run ./install.sh --no-cli, or in VS Code use Extensions → … → Install from VSIX → $VSIX"
  else
    say "Installing VS Code extension $(basename "$VSIX")"
    "$CODE" --install-extension "$VSIX" --force
    if "$CODE" --list-extensions --show-versions 2>/dev/null | grep -qi '^solvra\.solvra-vscode@'; then
      VSCODE_OK=1
      say "Extension installed: $("$CODE" --list-extensions --show-versions | grep -i '^solvra\.solvra-vscode@')"
    else
      warn "VS Code did not list solvra.solvra-vscode after install; install the VSIX manually: $VSIX"
    fi
  fi
fi

# ---------------------------------------------------------------- summary
echo ""
say "Done."
if [ "$INSTALL_CLI" = "1" ]; then
  echo "  CLI:      $BIN/solvra   (open a new terminal, then: solvra --help)"
fi
if [ "$VSCODE_OK" = "1" ]; then
  echo "  VS Code:  restart VS Code, open a folder, then run"
  echo "            'Solvra: Configure API Key' and 'Solvra: Pick Model' from the Command Palette."
  case "$DOTNET_DIR" in
    /usr/local/share/dotnet) ;;  # official .pkg: on PATH via /etc/paths.d/dotnet
    *) echo "            If the extension reports 'Could not start Solvra', set in VS Code settings:"
       echo "              \"solvra.dotnetPath\": \"$DOTNET\"" ;;
  esac
fi
echo "  CLI keys: export a provider key before running the CLI, e.g. ANTHROPIC_API_KEY or OPENAI_API_KEY"
echo "            (plus OPENAI_BASE_URL for an OpenAI-compatible gateway)."
