# Solvra cloud telemetry (0.4.0)

The opt-in uploader runs independently of CLI/VS Code processes. It sends existing and new
JSONL files from Solvra's logs and sessions directories, including recorded decisions, tool
inputs/results, verification records and run identity. It does not invent decisions a model
never recorded. `--ephemeral` runs have no session transcript to archive. Files deleted before
upload cannot be recovered; keep them until delivery is confirmed.

## Install on a computer

Download the release and your **private per-device JSON setup file** separately. Never put
that file in a repository or share it between computers. The public release contains no token.

Windows (PowerShell, in the extracted release folder):

```powershell
.\install.ps1 -TelemetryConfig "$env:USERPROFILE\Downloads\windows.json"
solvra telemetry status
```

macOS (Terminal, in the extracted release folder):

```bash
./install.sh --telemetry-config "$HOME/Downloads/mac.json"
solvra telemetry status
```

The installer imports the setup file into `~/.solvra/telemetry.json`, restricts local access,
creates a synthetic delivery check, and installs a per-user background worker. Windows uses
Task Scheduler at logon; macOS uses a LaunchAgent. Both retry every 60 seconds while signed in.
Successful installation alone does not prove delivery: check `last_delivered` and `sender.error`.
`solvra telemetry test` writes a synthetic decision and trace and verifies collector receipt.
`solvra telemetry sync` drains the queue once. The worker continues after VS Code closes.
Remove the downloaded setup file after importing it. No Tailscale is needed for uploads.

Use `logs_directory` / `sessions_directory` in the private JSON if your runs use custom storage
paths. Defaults match `SolvraPaths` (`~/.local/share/solvra/{logs,sessions}` on both platforms).
A worker cannot discover per-project directory overrides; explicitly configure their paths.
Upgrades preserve the imported config and queue and refresh the worker.

To disable collection: stop/unregister your `Solvra Telemetry <user SID>` task on Windows, or
`launchctl bootout gui/$(id -u)/com.solvra.telemetry` and remove the corresponding LaunchAgent
plist on macOS. Remove `~/.solvra/telemetry.json` to stop installers re-enabling it on upgrades.
Preserve the queue and source files if you want to resume later.

## Data path and guarantees

1. Local JSONL files plus an atomic cursor/pending-batch file form the disk queue.
2. Full records are encrypted locally using AES-256-GCM and a random key per batch; the key is
   wrapped using RSA-OAEP-SHA256. The device ID is authenticated associated data.
3. An HTTPS Modal service authenticates per-device bearer tokens by their hashes. It stores the
   ciphertext under `solvra-telemetry/v1/<device>/<batch>.json` in the existing private Scaleway
   bucket. The service has no archive decryption key. This prefix uses the envelope format in
   `graph_sync.py`, separately from the existing rclone-crypt `v1/` prefix.
4. Only allowlisted event names, numeric/boolean attributes and opaque IDs go to Grafana Cloud.
   Prompts, paths, exception text, commands and tool content never go there. Metadata log time
   is receipt time (supports offline/backfill); `solvra.event_time_unix_ms` preserves event time.
   Traces retain their original span times. Grafana's retention and tenant limits still apply.
5. Acknowledgment requires archive persistence AND Grafana acceptance. Failures retain the same
   pending encrypted batch. Delivery is at least once: Grafana logs can repeat after an interrupted
   acknowledgment; `solvra.batch.id` + `solvra.record.index` identify duplicates. Encrypted objects
   use content-addressed keys. Single source records over 8 MiB pause that sender with an error;
   they are never silently discarded. There is no automatic source pruning.
6. A separate graph consumer decrypts on the graph host and transactionally imports each batch
   once. Run, conversation, record, tool-call, decision and verification nodes are linked through
   stable device/source/record identities. Original JSON is retained. Existing manually imported
   graph data is untouched. Collection continues while the graph host is offline; its timer catches
   up on return. Graph viewing/processing still depends on that host.

Current deployment: `https://lookingto3--solvra-telemetry-web.us-east.modal.run`.
Health is public; ingest requires authentication. One Modal container, 0.125 CPU, 256 MiB,
scales to zero after idle; normal usage consumes existing account credit. This is a resource
cap, not a hard spending cap. No new VM, account or bucket is needed.

## Operator tools

Install `requirements.txt` in a dedicated venv. `deploy.py` reads existing provider files under
`~/env`, generates an archive key and mac/windows/smoke device configs under
`~/env/solvra-telemetry` (mode 600), and deploys only the receiver and an explicit credential set.
It never uploads the archive private key. Keep that key backed up; losing it loses archive access.

```bash
python tools/telemetry/deploy.py
python tools/telemetry/smoke.py --dll path/to/Solvra.dll
python tools/telemetry/graph_sync.py
```

For revocation, replace that device's token in its private config with a newly generated random
value and redeploy; keep the replacement private. Other devices are unaffected. To rotate a
device while keeping it active, also import the replacement
config on that computer. Preserve its `device_id`, public key and local state. For an additional
computer, issue a new UUID/token and add its hash to deployment. Do not reuse the mac/windows
files across multiple installations. Archive key rotation requires keeping old keys; unsupported
by this initial version's single-key consumer, so retain the current key.

The graph timer is installed from `install-graph-worker.sh` into an immutable version directory,
independent of the task worktree. Inspect `journalctl --user -u solvra-telemetry-graph.service`.
Tests: `dotnet test ... --filter FullyQualifiedName~TelemetryTests`, `pytest tools/telemetry`;
GitHub Actions runs the client on Linux, macOS and Windows and checks native worker definitions.

`install-enrollment.sh` optionally exposes only mac.json/windows.json to the owning Tailscale
login over HTTPS for seven days. The archive private key is never served. This is setup-only;
public telemetry ingestion does not use that service. Stop its systemd unit and remove Serve
port 9597 when setup is complete. Re-run to renew the seven-day download window.
