import unittest
from textstats.stats import word_count, median


class TestStats(unittest.TestCase):
    def test_word_count_simple(self):
        self.assertEqual(word_count("one two three"), 3)

    def test_median_odd(self):
        self.assertEqual(median([3, 1, 2]), 2)


if __name__ == "__main__":
    unittest.main()
