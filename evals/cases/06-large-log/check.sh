cd "$WORK"
[ -f errors.csv ] || fail "errors.csv missing"
expected=$'code,count\nE3300,42\nE2001,17\nE1042,3\nE0007,1'
[ "$(tr -d '\r' < errors.csv | sed '/^$/d')" = "$expected" ] || fail "wrong csv: $(head -6 errors.csv | tr '\n' ' ')"
[ "$(wc -l < logs/app.log)" -eq 250063 ] || fail "log modified"
