#!/usr/bin/env bash
# Solvra scenario eval suite.
#   evals/run.sh [-m MODEL] [-j JOBS] [-o OUTDIR] [CASE_PATTERN...]
# Each case in evals/cases/<name>/ has:
#   repo/        fixture copied to a fresh git repo (the agent's working directory)
#   prompt.txt   the task (or steps.txt: one "FLAGS ::: PROMPT" per line, run in order)
#   check.sh     hidden verification, run after the agent (never visible to it)
#   setup.sh     optional, generates large fixtures into $WORK
#   flags        optional extra solvra flags (--no-auto disables the default --auto)
# Env: SOLVRA_BIN (default ~/.local/bin/solvra), MAX_TURNS (default 40).
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
MODEL="chatgpt:gpt-5.6-luna"; JOBS=3; OUTDIR=""
while getopts "m:j:o:" opt; do
  case $opt in m) MODEL=$OPTARG;; j) JOBS=$OPTARG;; o) OUTDIR=$OPTARG;; *) exit 2;; esac
done
shift $((OPTIND - 1))
OUTDIR=${OUTDIR:-/tmp/solvra-evals/$(date +%Y%m%d-%H%M%S)-${MODEL//[:\/]/_}}
SOLVRA_BIN=${SOLVRA_BIN:-$HOME/.local/bin/solvra}
MAX_TURNS=${MAX_TURNS:-40}
mkdir -p "$OUTDIR"

cases=()
if [ $# -eq 0 ]; then for d in "$HERE"/cases/*/; do cases+=("$(basename "$d")"); done
else for p in "$@"; do for d in "$HERE"/cases/*$p*/; do [ -d "$d" ] && cases+=("$(basename "$d")"); done; done; fi

run_case() {
  local name=$1 C="$HERE/cases/$1" R="$OUTDIR/$1"
  local WORK="$R/work" HOME_DIR="$R/solvra-home" HIDDEN="$R/hidden"
  rm -rf "$R"; mkdir -p "$WORK" "$HOME_DIR" "$HIDDEN"
  cp -r "$C/repo/." "$WORK/"
  (cd "$WORK" && git init -q && git add -A && git -c user.name=eval -c user.email=eval@local commit -qm fixture)
  [ -f "$C/setup.sh" ] && WORK="$WORK" bash "$C/setup.sh"

  local base_flags="--auto"
  local extra=""; [ -f "$C/flags" ] && extra=$(cat "$C/flags")
  [[ "$extra" == *--no-auto* ]] && { base_flags=""; extra=${extra//--no-auto/}; }

  local steps=()
  if [ -f "$C/steps.txt" ]; then mapfile -t steps < "$C/steps.txt"
  else steps=("$extra ::: $(cat "$C/prompt.txt")"); fi

  local start=$(date +%s) rc=0 i=0
  : > "$R/out.txt"; : > "$R/err.txt"
  for step in "${steps[@]}"; do
    [ -z "$step" ] && continue
    i=$((i + 1))
    local sflags="${step%%:::*}" prompt="${step#*:::}"
    echo "=== step $i ===" >> "$R/out.txt"
    # shellcheck disable=SC2086
    SOLVRA_HOME="$HOME_DIR" NO_COLOR=1 timeout 900 "$SOLVRA_BIN" run "$prompt" -m "$MODEL" --summary \
      --max-turns "$MAX_TURNS" --cwd "$WORK" $base_flags $sflags < /dev/null >> "$R/out.txt" 2>> "$R/err.txt" || rc=$?
  done
  local secs=$(( $(date +%s) - start ))

  # Hidden checks
  local result
  result=$(
    export WORK HIDDEN OUT="$R/out.txt" SOLVRA_HOME="$HOME_DIR"
    fail() { echo "FAIL: $*"; }
    note() { echo "NOTE: $*"; }
    tools() { python3 - "$SOLVRA_HOME" "$@" <<'PY'
import glob, json, sys
home, mode, name = sys.argv[1], sys.argv[2], sys.argv[3]
for f in glob.glob(f"{home}/logs/audit-*.jsonl"):
    for line in open(f):
        try: e = json.loads(line)
        except ValueError: continue
        d = e.get("data") or {}
        if e.get("event_type") == "ToolExecution" and d.get("toolName") == name and (mode == "used" or d.get("isError")):
            sys.exit(0)
sys.exit(1)
PY
    }
    tool_used() { tools used "$1"; }
    tool_errored() { tools errored "$1"; }
    export -f fail note tools tool_used tool_errored
    bash "$C/check.sh" 2>&1
  )
  echo "$result" > "$R/check.txt"
  local status=PASS; grep -q "^FAIL:" "$R/check.txt" && status=FAIL

  local used turns tokens
  used=$(python3 - "$HOME_DIR" <<'PY'
import glob, json, sys, collections
c = collections.Counter()
for f in glob.glob(f"{sys.argv[1]}/logs/audit-*.jsonl"):
    for line in open(f):
        try: e = json.loads(line)
        except ValueError: continue
        if e.get("event_type") == "ToolExecution": c[(e.get("data") or {}).get("toolName")] += 1
print(" ".join(f"{k}×{v}" for k, v in c.most_common()))
PY
)
  turns=$(grep -oP '^Turns: \K\d+' "$R/out.txt" | paste -sd+ | bc 2>/dev/null)
  tokens=$(grep -oP '^Tokens: \K\d+' "$R/out.txt" | paste -sd+ | bc 2>/dev/null)
  local reason; reason=$(grep "^FAIL:" "$R/check.txt" | head -1 | cut -c7-120)
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$name" "$status" "$rc" "$secs" "${turns:-?}" "${tokens:-?}" "$used" "$reason" > "$R/row.tsv"
  echo "[$status] $name (${secs}s, turns ${turns:-?}) ${reason}"
}

echo "Model: $MODEL  Output: $OUTDIR  Cases: ${#cases[@]}"
for name in "${cases[@]}"; do
  while [ "$(jobs -rp | wc -l)" -ge "$JOBS" ]; do wait -n; done
  run_case "$name" &
done
wait

{
  echo "# Solvra eval — $MODEL — $(date -u +%Y-%m-%dT%H:%MZ)"
  echo
  echo "| Case | Result | Exit | Secs | Turns | Input tokens | Tools used | Failure |"
  echo "|---|---|---|---|---|---|---|---|"
  for name in "${cases[@]}"; do
    IFS=$'\t' read -r n s rc secs t tok used why < "$OUTDIR/$name/row.tsv"
    echo "| $n | $s | $rc | $secs | $t | $tok | $used | $why |"
  done
  echo
  echo "Passed $(grep -l $'\tPASS\t' "$OUTDIR"/*/row.tsv | wc -l) / ${#cases[@]}"
} > "$OUTDIR/results.md"
cat "$OUTDIR/results.md"
