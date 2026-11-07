#nullable enable

using System.Text;

namespace Solvra.Core;

/// <summary>
/// Small line-based unified diff for approval previews and +/- counts. Common leading and
/// trailing lines are trimmed first, so a typical edit compares only the changed region; that
/// region is diffed with an LCS table, falling back to "replace everything" when it is huge.
/// </summary>
public static class UnifiedDiff
{
    public sealed record Result(string Text, int Added, int Removed);

    private const int MaxLcsCells = 4_000_000;
    private enum Op { Equal, Delete, Insert }

    public static Result Create(string? oldText, string newText, string path, int context = 3)
    {
        var a = SplitLines(oldText ?? "");
        var b = SplitLines(newText);
        var ops = EditScript(a, b);
        var added = ops.Count(o => o.Op == Op.Insert);
        var removed = ops.Count(o => o.Op == Op.Delete);
        if (added == 0 && removed == 0) return new Result("", 0, 0);

        var sb = new StringBuilder();
        sb.Append("--- ").Append(oldText is null ? "/dev/null" : "a/" + path).Append('\n');
        sb.Append("+++ b/").Append(path).Append('\n');

        // Group changes into hunks with `context` lines around them.
        var i = 0;
        while (i < ops.Count)
        {
            while (i < ops.Count && ops[i].Op == Op.Equal) i++;
            if (i >= ops.Count) break;
            var start = Math.Max(0, i - context);
            var end = i;
            var lastChange = i;
            while (end < ops.Count)
            {
                if (ops[end].Op != Op.Equal) lastChange = end;
                else if (end - lastChange > 2 * context) break;
                end++;
            }
            end = Math.Min(ops.Count, lastChange + context + 1);

            int aStart = 0, bStart = 0;
            for (var k = 0; k < start; k++)
            {
                if (ops[k].Op != Op.Insert) aStart++;
                if (ops[k].Op != Op.Delete) bStart++;
            }
            int aLen = 0, bLen = 0;
            var body = new StringBuilder();
            for (var k = start; k < end; k++)
            {
                var (op, line) = ops[k];
                if (op != Op.Insert) aLen++;
                if (op != Op.Delete) bLen++;
                body.Append(op switch { Op.Insert => '+', Op.Delete => '-', _ => ' ' }).Append(line).Append('\n');
            }
            sb.Append("@@ -").Append(aLen == 0 ? aStart : aStart + 1).Append(',').Append(aLen)
              .Append(" +").Append(bLen == 0 ? bStart : bStart + 1).Append(',').Append(bLen).Append(" @@\n");
            sb.Append(body);
            i = end;
        }
        return new Result(sb.ToString(), added, removed);
    }

    private static List<string> SplitLines(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1); // trailing newline
        return lines;
    }

    private static List<(Op Op, string Line)> EditScript(List<string> a, List<string> b)
    {
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix]) suffix++;

        var ops = new List<(Op, string)>();
        for (var k = 0; k < prefix; k++) ops.Add((Op.Equal, a[k]));

        var am = a.GetRange(prefix, a.Count - prefix - suffix);
        var bm = b.GetRange(prefix, b.Count - prefix - suffix);
        if ((long)(am.Count + 1) * (bm.Count + 1) > MaxLcsCells)
        {
            ops.AddRange(am.Select(l => (Op.Delete, l)));
            ops.AddRange(bm.Select(l => (Op.Insert, l)));
        }
        else
        {
            var n = am.Count; var m = bm.Count;
            var lcs = new int[n + 1, m + 1];
            for (var x = n - 1; x >= 0; x--)
                for (var y = m - 1; y >= 0; y--)
                    lcs[x, y] = am[x] == bm[y] ? lcs[x + 1, y + 1] + 1 : Math.Max(lcs[x + 1, y], lcs[x, y + 1]);
            int p = 0, q = 0;
            while (p < n && q < m)
            {
                if (am[p] == bm[q]) { ops.Add((Op.Equal, am[p])); p++; q++; }
                else if (lcs[p + 1, q] >= lcs[p, q + 1]) { ops.Add((Op.Delete, am[p])); p++; }
                else { ops.Add((Op.Insert, bm[q])); q++; }
            }
            while (p < n) ops.Add((Op.Delete, am[p++]));
            while (q < m) ops.Add((Op.Insert, bm[q++]));
        }

        for (var k = a.Count - suffix; k < a.Count; k++) ops.Add((Op.Equal, a[k]));
        return ops;
    }
}
