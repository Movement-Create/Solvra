# Generates logs/app.log (18 MB) in $WORK; kept out of git.
mkdir -p "$WORK/logs"
python3 - "$WORK/logs/app.log" <<'PY'
import random, sys
random.seed(7)
codes = {"E1042": 3, "E2001": 17, "E0007": 1, "E3300": 42}
lines = [f"2026-09-{1+i%28:02d}T{i%24:02d}:{i%60:02d}:00Z INFO req={i} path=/api/v1/items status=200 ms={random.randint(1,300)}" for i in range(250000)]
for code, n in codes.items():
    for _ in range(n):
        lines.insert(random.randint(0, len(lines)), f"2026-09-15T12:00:00Z ERROR req=0 code={code} msg=upstream failure")
open(sys.argv[1], "w").write("\n".join(lines) + "\n")
PY
