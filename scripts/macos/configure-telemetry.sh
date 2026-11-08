#!/bin/bash
# Usage: configure-telemetry.sh private-device.json /absolute/dotnet /absolute/Solvra.dll
set -euo pipefail
CONFIG="${1:?private device setup file required}"
DOTNET="${2:?absolute dotnet path required}"
DLL="${3:?absolute Solvra.dll path required}"
[ "$(uname -s)" = Darwin ] || { echo 'This worker setup requires macOS.' >&2; exit 1; }
umask 077
mkdir -p "$HOME/.solvra" "$HOME/Library/LaunchAgents" "$HOME/Library/Logs/Solvra"
"$DOTNET" "$DLL" telemetry configure --file "$CONFIG"
xml() { printf '%s' "$1" | sed -e 's/\&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g' -e "s/'/\&apos;/g"; }
PLIST="$HOME/Library/LaunchAgents/com.solvra.telemetry.plist"
cat > "$PLIST" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>Label</key><string>com.solvra.telemetry</string>
<key>ProgramArguments</key><array><string>$(xml "$DOTNET")</string><string>$(xml "$DLL")</string><string>telemetry</string><string>watch</string></array>
<key>RunAtLoad</key><true/><key>KeepAlive</key><true/><key>ThrottleInterval</key><integer>60</integer>
<key>StandardOutPath</key><string>$(xml "$HOME/Library/Logs/Solvra/telemetry.log")</string>
<key>StandardErrorPath</key><string>$(xml "$HOME/Library/Logs/Solvra/telemetry-error.log")</string>
</dict></plist>
PLIST
plutil -lint "$PLIST"
[ "${4:-}" != "--no-start" ] || { echo "Worker definition validated; start skipped."; exit 0; }
launchctl bootout "gui/$(id -u)/com.solvra.telemetry" 2>/dev/null || true
if ! "$DOTNET" "$DLL" telemetry test; then
  echo 'Initial delivery failed; records remain queued and the background worker will retry.' >&2
fi
launchctl bootstrap "gui/$(id -u)" "$PLIST"
echo 'Telemetry worker installed. Check delivery with: solvra telemetry status'
