#!/usr/bin/env bash
# Run one or more corrected Terminal-Bench tasks with Solvra's internal deadline.
# Use separate jobs when tasks have different Harbor timeouts.
set -euo pipefail

JOB="${1:?job name}"; LIST="${2:?file containing task names}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export PATH="$HOME/.local/bin:$PATH"
: "${TB_DATASET:=terminal-bench/terminal-bench-2-1}"
: "${TB_MODEL:=gpt-5.6-sol}"
: "${TB_REASONING:=high}"
: "${TB_CONCURRENCY:=1}"
: "${SOLVRA_TIME_LIMIT_SECONDS:?set to Harbor agent timeout minus cleanup allowance}"
: "${SOLVRA_PUBLISH_DIR:?point at a self-contained linux-x64 publish}"
: "${TB_JOBS_DIR:=/data/tb2/jobs}"
export PYTHONPATH="$HERE${PYTHONPATH:+:$PYTHONPATH}" SOLVRA_PUBLISH_DIR

includes=()
while read -r task; do [[ -n "$task" ]] && includes+=(-i "$task"); done < "$LIST"

cmd=(harbor run -d "$TB_DATASET"
  -a solvra_harbor_agent:Solvra -m "chatgpt/$TB_MODEL"
  --ak max_turns=500 --ak "effort=$TB_REASONING"
  --ak "time_limit_seconds=$SOLVRA_TIME_LIMIT_SECONDS"
  "${includes[@]}" -n "$TB_CONCURRENCY" -o "$TB_JOBS_DIR" --job-name "$JOB" -y)

if id -nG | grep -qw docker; then
  exec "${cmd[@]}"
else
  exec sg docker -c "$(printf '%q ' "${cmd[@]}")"
fi
