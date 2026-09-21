cd "$WORK"
[ -f durations.py ] || fail "durations.py missing"
cat > "$HIDDEN/h.py" <<'PY'
import sys, unittest; sys.path.insert(0, ".")
from durations import parse_duration as p, format_duration as f
class H(unittest.TestCase):
    def test_parse(self):
        for s, v in [("1h30m",5400),("2d 4h",187200),("90s",90),("1W",604800),("45",45),("1w 1d 1h 1m 1s",694861),("10M",600)]:
            self.assertEqual(p(s), v, s)
    def test_bad(self):
        for s in ["", "  ", "1x", "1h2h", "-5m", "abc", "h1"]:
            with self.assertRaises(ValueError, msg=s): p(s)
    def test_fmt(self):
        for v, s in [(5400,"1h30m"),(0,"0s"),(694861,"1w1d1h1m1s"),(59,"59s"),(3600,"1h")]:
            self.assertEqual(f(v), s)
        with self.assertRaises(ValueError): f(-1)
    def test_roundtrip(self):
        for v in [1, 61, 3601, 86399, 1234567]: self.assertEqual(p(f(v)), v)
unittest.main(argv=["x"])
PY
python3 "$HIDDEN/h.py" 2>&1 | tail -3; python3 "$HIDDEN/h.py" >/dev/null 2>&1 || fail "hidden tests failed"
python3 -m unittest test_durations 2>&1 | tail -1 | grep -q OK || fail "own tests missing/failing"
