#!/usr/bin/env bash
# Optional setup-only download service. Receiver uploads do not use this host or Tailscale.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REV="$(git -C "$HERE" rev-parse --short=12 HEAD)"
DEST="$HOME/.local/opt/solvra-telemetry/releases/$REV"
[ -f "$DEST/enrollment.py" ] || { echo 'Run install-graph-worker.sh first.' >&2; exit 1; }
python3 - <<'PY'
import json,os,subprocess,time
from pathlib import Path
status=json.loads(subprocess.check_output(['tailscale','status','--json']))
login=status['User'][str(status['Self']['UserID'])]['LoginName']
p=Path.home()/'env/solvra-telemetry/enrollment.json'
fd=os.open(p,os.O_WRONLY|os.O_CREAT|os.O_TRUNC,0o600)
with os.fdopen(fd,'w') as f: json.dump({'login':login,'expires':time.time()+7*86400},f)
PY
cat > "$HOME/.config/systemd/user/solvra-telemetry-enrollment.service" <<UNIT
[Unit]
Description=Private Solvra device setup downloads (Tailscale user authentication)
[Service]
ExecStart=/usr/bin/python3 $DEST/enrollment.py
Restart=on-failure
NoNewPrivileges=true
UMask=0077
[Install]
WantedBy=default.target
UNIT
systemctl --user daemon-reload
systemctl --user enable --now solvra-telemetry-enrollment.service
# Do not reset or replace unrelated Serve routes.
tailscale serve --bg --https=9597 http://127.0.0.1:8349
