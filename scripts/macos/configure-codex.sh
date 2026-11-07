#!/bin/bash
# Points Solvra (CLI + VS Code extension) at your ChatGPT subscription through Codex's sign-in.
# Uses Solvra's "chatgpt" provider: no API key. Solvra reads ~/.codex/auth.json and asks the
# Codex CLI to refresh the login when needed; it never writes Codex's credentials.
#
#   ./configure-codex.sh                     # model gpt-5.6-sol
#   ./configure-codex.sh --model gpt-5.6-luna
#   ./configure-codex.sh --no-test           # skip the one-line live test call
# Installs the Codex CLI if it is missing (Homebrew, else npm).
set -euo pipefail

MODEL="gpt-5.6-sol"
LIVE_TEST=1
QUIET_END=0
WRAPPER="${SOLVRA_PREFIX:-$HOME/.local}/bin/solvra"
ENV_FILE="$HOME/.config/solvra/env.sh"
SETTINGS="${SOLVRA_VSCODE_SETTINGS:-$HOME/Library/Application Support/Code/User/settings.json}"
CODEX_DIR="${CODEX_HOME:-$HOME/.codex}"

usage() { sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; }
while [ $# -gt 0 ]; do
  case "$1" in
    --model) shift; MODEL="${1:?--model needs a value}"; MODEL="${MODEL#chatgpt:}" ;;
    --no-test) LIVE_TEST=0 ;;
    --quiet-end) QUIET_END=1 ;;  # used by install.sh, which prints its own summary
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

say()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

[ -x "$WRAPPER" ] || die "$WRAPPER not found. Run ./install.sh first."
grep -q 'config/solvra/env.sh' "$WRAPPER" \
  || die "$WRAPPER is from an older bundle and ignores provider settings. Re-run ./install.sh from this bundle first."

# ---------------------------------------------------------------- Codex CLI
# Solvra runs `codex app-server` to refresh the ChatGPT login and list models.
find_codex() {
  local c
  for c in "${SOLVRA_CODEX_BIN:-}" "$(command -v codex 2>/dev/null || true)" /opt/homebrew/bin/codex \
           /usr/local/bin/codex "$HOME/.npm-global/bin/codex" "$HOME/.local/bin/codex" \
           "$(ls -d "$HOME"/.vscode/extensions/openai.chatgpt-*/bin/*/codex 2>/dev/null | tail -n 1)" \
           "/Applications/Codex.app/Contents/Resources/codex"; do
    if [ -n "$c" ] && [ -x "$c" ] && [ ! -d "$c" ]; then echo "$c"; return 0; fi
  done
  return 1
}
CODEX="$(find_codex || true)"
if [ -z "$CODEX" ]; then
  say "Codex CLI not found; installing it…"
  if command -v brew >/dev/null 2>&1; then
    brew install codex || warn "brew install codex failed"
  elif command -v npm >/dev/null 2>&1; then
    # Install for this user only: no sudo, even with the system Node.
    npm install -g --prefix "$HOME/.npm-global" @openai/codex || warn "npm install @openai/codex failed"
  else
    die "Codex CLI not found and neither Homebrew nor npm is available. Install Node.js (https://nodejs.org) or Homebrew (https://brew.sh), then re-run ./install.sh."
  fi
  CODEX="$(find_codex || true)"
  [ -n "$CODEX" ] || die "Codex CLI still not found. Install it (brew install codex, or npm install -g @openai/codex), then re-run ./configure-codex.sh."
fi
say "Codex CLI: $CODEX"

# ---------------------------------------------------------------- ChatGPT sign-in
if [ ! -s "$CODEX_DIR/auth.json" ]; then
  say "No Codex sign-in found in $CODEX_DIR; starting 'codex login' (opens your browser)…"
  "$CODEX" login || die "codex login did not finish."
fi
[ -s "$CODEX_DIR/auth.json" ] || die "Still no $CODEX_DIR/auth.json after login."
if ! "$CODEX" login status >/dev/null 2>&1; then
  warn "'codex login status' reports a problem; if the test below fails, run: codex login"
fi
say "Codex sign-in found ($CODEX_DIR/auth.json)"

# ---------------------------------------------------------------- non-secret settings
mkdir -p "$(dirname "$ENV_FILE")"
cat > "$ENV_FILE" <<EOF
# Solvra provider settings, written by configure-codex.sh. Loaded by $WRAPPER.
# No secrets here: the chatgpt provider reads Codex's own sign-in ($CODEX_DIR/auth.json).
export SOLVRA_PROVIDER="chatgpt"
export SOLVRA_MODEL="chatgpt:$MODEL"
export SOLVRA_CODEX_BIN="$CODEX"
EOF
chmod 644 "$ENV_FILE"
say "Wrote $ENV_FILE (provider chatgpt, model $MODEL)"

# ---------------------------------------------------------------- live test (one tiny request)
if [ "$LIVE_TEST" = "1" ]; then
  say "Testing $MODEL with a one-line request…"
  OUT="$(cd "$HOME" && "$WRAPPER" run "Reply with exactly the word OK and nothing else." \
         --provider chatgpt --model "$MODEL" \
         --no-tools --no-session --ephemeral --max-turns 1 --time-limit-seconds 90 2>&1 || true)"
  if printf '%s' "$OUT" | grep -q '^\[Error'; then
    warn "Test request failed:"
    printf '%s\n' "$OUT" | grep '^\[Error' >&2
    warn "Try 'codex login' again, or list models with: solvra models --provider chatgpt"
  elif printf '%s' "$OUT" | grep -qx 'OK'; then
    say "Model answered: OK"
  else
    warn "Unexpected test output:"; printf '%s\n' "$OUT" | tail -n 5 >&2
  fi
fi

# ---------------------------------------------------------------- VS Code settings
. "$(cd "$(dirname "$0")" && pwd)/vscode-settings.sh"
update_vscode_settings chatgpt "chatgpt:$MODEL"

if [ "$QUIET_END" = "0" ]; then
  echo ""
  say "Done. Open a new terminal and run: solvra chat"
  echo "  In VS Code: quit and reopen VS Code, then click the Solvra icon in the Activity Bar."
  echo "  No API key is needed; the model menu in the chat lists the other ChatGPT models."
fi
