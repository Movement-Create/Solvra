cd "$WORK"
cat > "$HIDDEN/hidden_test.py" <<'PY'
import sys, unittest
sys.path.insert(0, ".")
from textstats.stats import word_count, median, top_words
class Hidden(unittest.TestCase):
    def test_wc(self):
        self.assertEqual(word_count("Hello,  world!"), 2)
        self.assertEqual(word_count("  "), 0)
        self.assertEqual(word_count(""), 0)
        self.assertEqual(word_count("a\tb\nc"), 3)
    def test_median(self):
        self.assertEqual(median([1, 2, 3, 4]), 2.5)
        self.assertEqual(median([5]), 5)
        self.assertEqual(median([3, 1, 2]), 2)
    def test_top(self):
        self.assertEqual(top_words("The cat the Cat THE dog", 2), ["the", "cat"])
        self.assertEqual(top_words("b a c b a c", 3), ["a", "b", "c"])
unittest.main(argv=["x"], exit=True)
PY
python3 "$HIDDEN/hidden_test.py" 2>&1 | tail -3; python3 "$HIDDEN/hidden_test.py" >/dev/null 2>&1 || fail "hidden tests failed"
python3 -m unittest 2>&1 | tail -1 | grep -q OK || fail "visible suite fails"
[ "$(grep -c 'def test' tests/test_stats.py)" -ge 5 ] || fail "regression tests not added"
