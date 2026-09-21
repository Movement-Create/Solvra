cd "$WORK"
if grep -rn "get_user_name" --include=*.py . ; then fail "old name still present"; fi
grep -q "def fetch_display_name(" app/services/users.py || fail "new function missing"
grep -q "def fetch_display_name_or_default(" app/services/users.py || fail "new default function missing"
python3 -m unittest 2>&1 | tail -1 | grep -q OK || fail "tests fail"
[ "$(grep -c 'def test' tests/test_app.py)" -ge 4 ] || fail "tests were deleted"
