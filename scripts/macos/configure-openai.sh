#!/bin/bash
# Points Solvra (CLI + VS Code extension) at an OpenAI-compatible endpoint.
# Defaults match the devbox: provider "openai", OpenCode Zen Go gateway, model glm-5.3.
#
#   ./configure-openai.sh                      # gateway + glm-5.3, prompts for the key once
#   ./configure-openai.sh --model glm-5.3-flash
#   ./configure-openai.sh --base-url https://api.openai.com/v1 --model gpt-4.1
#   ./configure-openai.sh --reset-key          # replace the stored key (e.g. it expired)
#   ./configure-openai.sh --no-test            # skip the one-line live test call
#
# The API key is stored in the macOS login Keychain (service "solvra-openai"), never in a file.
# Non-secret settings go to ~/.config/solvra/env.sh, which the ~/.local/bin/solvra wrapper loads.
# VS Code is pointed at that wrapper, so the extension and the CLI use identical settings.
set -euo pipefail

MODEL="glm-5.3"
BASE_URL="https://opencode.ai/zen/go/v1"
RESET_KEY=0
LIVE_TEST=1
SERVICE="solvra-openai"
WRAPPER="${SOLVRA_PREFIX:-$HOME/.local}/bin/solvra"
ENV_FILE="$HOME/.config/solvra/env.sh"
SETTINGS="${SOLVRA_VSCODE_SETTINGS:-$HOME/Library/Application Support/Code/User/settings.json}"

usage() { sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'; }
while [ $# -gt 0 ]; do
  case "$1" in
    --model) shift; MODEL="${1:?--model needs a value}" ;;
    --base-url) shift; BASE_URL="${1:?--base-url needs a value}" ;;
    --reset-key) RESET_KEY=1 ;;
    --no-test) LIVE_TEST=0 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done
BASE_URL="${BASE_URL%/}"

say()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33mwarning:\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31merror:\033[0m %s\n' "$*" >&2; exit 1; }

command -v security >/dev/null 2>&1 || die "The macOS 'security' tool was not found; run this on your Mac."
[ -x "$WRAPPER" ] || die "$WRAPPER not found. Run ./install.sh first."
grep -q 'config/solvra/env.sh' "$WRAPPER" \
  || die "$WRAPPER is from an older bundle and ignores provider settings. Re-run ./install.sh from this bundle first."

# ---------------------------------------------------------------- API key -> Keychain
have_key() { security find-generic-password -a "$USER" -s "$SERVICE" -w >/dev/null 2>&1; }
if [ "$RESET_KEY" = "1" ] || ! have_key; then
  case "$BASE_URL" in
    *opencode.ai*) echo "Create a key in OpenCode Console (https://opencode.ai → Zen / Go → API keys)." ;;
    *api.openai.com*) echo "Create a key at https://platform.openai.com/api-keys." ;;
  esac
  echo "Paste the API key when asked for the 'password data' (typing is hidden), then once more to confirm."
  security add-generic-password -U -a "$USER" -s "$SERVICE" -l "Solvra OpenAI-compatible API key" -w
  have_key || die "The key was not saved to the Keychain."
  say "API key saved in the login Keychain (service \"$SERVICE\")"
else
  say "Using the API key already in the Keychain (service \"$SERVICE\"; --reset-key to replace it)"
fi

# ---------------------------------------------------------------- non-secret settings
mkdir -p "$(dirname "$ENV_FILE")"
cat > "$ENV_FILE" <<EOF
# Solvra provider settings, written by configure-openai.sh. Loaded by $WRAPPER.
# No secrets here: the key is read from the macOS Keychain at launch.
export SOLVRA_PROVIDER="openai"
export SOLVRA_MODEL="$MODEL"
export OPENAI_BASE_URL="$BASE_URL"
_solvra_key="\$(security find-generic-password -a "\$USER" -s "$SERVICE" -w 2>/dev/null || true)"
if [ -n "\$_solvra_key" ]; then export OPENAI_API_KEY="\$_solvra_key"; fi
unset _solvra_key
EOF
chmod 644 "$ENV_FILE"
say "Wrote $ENV_FILE (provider openai, model $MODEL, base URL $BASE_URL)"

# ---------------------------------------------------------------- live test (one tiny request)
if [ "$LIVE_TEST" = "1" ]; then
  say "Testing $MODEL with a one-line request…"
  OUT="$(cd "$HOME" && "$WRAPPER" run "Reply with exactly the word OK and nothing else." \
         --no-tools --no-session --ephemeral --max-turns 1 --time-limit-seconds 90 2>&1 || true)"
  if printf '%s' "$OUT" | grep -q '^\[Error'; then
    warn "Test request failed:"
    printf '%s\n' "$OUT" | grep '^\[Error' >&2
    warn "Fix the key with ./configure-openai.sh --reset-key (settings were still saved)."
  elif printf '%s' "$OUT" | grep -qi 'OK'; then
    say "Model answered: OK"
  else
    warn "Unexpected test output:"; printf '%s\n' "$OUT" | tail -n 5 >&2
  fi
fi

# ---------------------------------------------------------------- VS Code settings
manual_vscode() {
  echo "  Add these to VS Code settings (Cmd+Shift+P → 'Preferences: Open User Settings (JSON)'):"
  echo "    \"solvra.executablePath\": \"$WRAPPER\","
  echo "    \"solvra.provider\": \"openai\","
  echo "    \"solvra.model\": \"$MODEL\""
}

if ! command -v osascript >/dev/null 2>&1; then
  warn "osascript not available; update VS Code settings by hand."; manual_vscode
else
  JS="$(mktemp "${TMPDIR:-/tmp}/solvra-settings.XXXXXX")"
  trap 'rm -f "$JS"' EXIT
  cat > "$JS" <<'JS'
// argv: current settings text, executable path, model. Returns new text, or "!error: ...".
function run(argv) {
  var text = argv[0], obj = {};
  if (text.replace(/\s/g, '') !== '') {
    try { obj = JSON.parse(text); } catch (e) { return '!error: settings.json has comments or trailing commas'; }
    if (obj === null || typeof obj !== 'object' || Array.isArray(obj)) return '!error: settings.json is not an object';
  }
  obj['solvra.executablePath'] = argv[1];
  obj['solvra.provider'] = 'openai';
  obj['solvra.model'] = argv[2];
  return JSON.stringify(obj, null, 4);
}
JS
  CURRENT=""
  if [ -f "$SETTINGS" ]; then CURRENT="$(cat "$SETTINGS")"; fi
  NEW="$(osascript -l JavaScript "$JS" "$CURRENT" "$WRAPPER" "$MODEL" 2>&1 || echo '!error: osascript failed')"
  case "$NEW" in
    '!error'*)
      warn "Could not update VS Code settings automatically (${NEW#!error: })."; manual_vscode ;;
    *)
      mkdir -p "$(dirname "$SETTINGS")"
      if [ -f "$SETTINGS" ]; then cp "$SETTINGS" "$SETTINGS.solvra-backup"; fi
      printf '%s\n' "$NEW" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"
      say "VS Code settings updated: solvra.executablePath, solvra.provider=openai, solvra.model=$MODEL"
      if [ -f "$SETTINGS.solvra-backup" ]; then echo "  (previous settings saved as $SETTINGS.solvra-backup)"; fi
      ;;
  esac
fi

echo ""
say "Done. Open a new terminal and run: solvra chat"
echo "  In VS Code: reload the window (Cmd+Shift+P → 'Developer: Reload Window'), then use the Solvra sidebar."
echo "  You do not need 'Solvra: Configure API Key' — the key comes from the Keychain."
