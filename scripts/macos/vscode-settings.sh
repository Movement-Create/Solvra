# Shared by configure-*.sh (sourced, bash 3.2). Needs: say, warn, WRAPPER, SETTINGS.
# update_vscode_settings <provider> <model>
#   Sets solvra.executablePath (the wrapper), solvra.provider and solvra.model in VS Code's
#   user settings.json, keeping a backup. settings.json with comments is left untouched and
#   the three settings are printed for manual pasting instead.
update_vscode_settings() {
  local provider="$1" model="$2" js current new
  if ! command -v osascript >/dev/null 2>&1; then
    warn "osascript not available; update VS Code settings by hand."; _manual_vscode "$provider" "$model"; return 0
  fi
  js="$(mktemp "${TMPDIR:-/tmp}/solvra-settings.XXXXXX")"
  cat > "$js" <<'JS'
// argv: current settings text, executable path, provider, model. Returns new text, or "!error: ...".
function run(argv) {
  var text = argv[0], obj = {};
  if (text.replace(/\s/g, '') !== '') {
    try { obj = JSON.parse(text); } catch (e) { return '!error: settings.json has comments or trailing commas'; }
    if (obj === null || typeof obj !== 'object' || Array.isArray(obj)) return '!error: settings.json is not an object';
  }
  obj['solvra.executablePath'] = argv[1];
  obj['solvra.provider'] = argv[2];
  obj['solvra.model'] = argv[3];
  return JSON.stringify(obj, null, 4);
}
JS
  current=""
  if [ -f "$SETTINGS" ]; then current="$(cat "$SETTINGS")"; fi
  new="$(osascript -l JavaScript "$js" "$current" "$WRAPPER" "$provider" "$model" 2>&1 || echo '!error: osascript failed')"
  rm -f "$js"
  case "$new" in
    '!error'*)
      warn "Could not update VS Code settings automatically (${new#!error: })."; _manual_vscode "$provider" "$model" ;;
    *)
      mkdir -p "$(dirname "$SETTINGS")"
      if [ -f "$SETTINGS" ]; then cp "$SETTINGS" "$SETTINGS.solvra-backup"; fi
      printf '%s\n' "$new" > "$SETTINGS.tmp" && mv "$SETTINGS.tmp" "$SETTINGS"
      say "VS Code settings updated: solvra.executablePath, solvra.provider=$provider, solvra.model=$model"
      if [ -f "$SETTINGS.solvra-backup" ]; then echo "  (previous settings saved as $SETTINGS.solvra-backup)"; fi
      ;;
  esac
}

_manual_vscode() {
  echo "  Add these to VS Code's settings.json (Code → Settings → Settings, then the {} icon at the top right):"
  echo "    \"solvra.executablePath\": \"$WRAPPER\","
  echo "    \"solvra.provider\": \"$1\","
  echo "    \"solvra.model\": \"$2\""
}
