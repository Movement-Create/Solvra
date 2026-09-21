"""Small text statistics helpers used by the reporting service."""
import re


def word_count(text: str) -> int:
    """Number of words; punctuation is not a word."""
    return len(text.split(" "))


def median(values):
    """Median of a non-empty list of numbers."""
    ordered = sorted(values)
    mid = len(ordered) // 2
    return ordered[mid]


def top_words(text: str, n: int = 3):
    """The n most common lowercase words, ties broken alphabetically."""
    counts = {}
    for w in re.findall(r"[A-Za-z']+", text):
        counts[w] = counts.get(w, 0) + 1
    return [w for w, _ in sorted(counts.items(), key=lambda kv: -kv[1])[:n]]
