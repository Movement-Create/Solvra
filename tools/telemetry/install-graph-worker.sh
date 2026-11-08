#!/usr/bin/env bash
# Installs tested sources in an immutable directory; never runs from a disposable worktree.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REV="$(git -C "$HERE" rev-parse --short=12 HEAD)"
BASE="$HOME/.local/opt/solvra-telemetry"
DEST="$BASE/releases/$REV"
if [ ! -d "$DEST" ]; then
  mkdir -p "$DEST"
  cp "$HERE"/*.py "$HERE/requirements.txt" "$DEST/"
fi
[ -x "$DEST/venv/bin/python" ] || uv venv "$DEST/venv"
uv pip install --python "$DEST/venv/bin/python" -r "$DEST/requirements.txt"
mkdir -p "$HOME/.config/systemd/user"
cat > "$HOME/.config/systemd/user/solvra-telemetry-graph.service" <<UNIT
[Unit]
Description=Import encrypted Solvra cloud records into the local decision graph
[Service]
Type=oneshot
ExecStart=$DEST/venv/bin/python $DEST/graph_sync.py
MemoryMax=512M
TimeoutStartSec=600
NoNewPrivileges=true
UMask=0077
UNIT
cat > "$HOME/.config/systemd/user/solvra-telemetry-graph.timer" <<'UNIT'
[Unit]
Description=Catch up the Solvra decision graph from its encrypted cloud archive
[Timer]
OnBootSec=90
OnUnitInactiveSec=60
Persistent=true
[Install]
WantedBy=timers.target
UNIT
systemctl --user daemon-reload
systemctl --user enable --now solvra-telemetry-graph.timer
systemctl --user start solvra-telemetry-graph.service
