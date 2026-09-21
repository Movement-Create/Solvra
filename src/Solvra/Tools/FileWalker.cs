#nullable enable

using System.Text.RegularExpressions;

namespace Solvra.Tools;

/// <summary>
/// Walks a project tree for glob/grep, skipping VCS/dependency/build-cache directories and
/// simple patterns from the root .gitignore (no negations or nested ignore files).
/// </summary>
internal sealed class FileWalker
{
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bin", "obj", ".vs", ".idea", "__pycache__",
        ".venv", "venv", ".mypy_cache", ".pytest_cache", ".tox", ".gradle", ".next", ".turbo",
    };

    private readonly string _root;
    private readonly List<(Regex Pattern, bool DirOnly)> _gitignore = new();

    public FileWalker(string root)
    {
        _root = root;
        LoadGitignore(root);
    }

    private void LoadGitignore(string root)
    {
        // Use the .gitignore at the search root, or at the enclosing git root.
        var dir = new DirectoryInfo(root);
        string? file = null;
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, ".gitignore");
            if (File.Exists(candidate)) { file = candidate; break; }
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) break;
            dir = dir.Parent;
        }
        if (file == null) return;
        try
        {
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!')) continue;
                var dirOnly = line.EndsWith('/');
                line = line.Trim('/');
                if (line.Length == 0) continue;
                _gitignore.Add((GlobTool.GlobToRegex(line), dirOnly));
            }
        }
        catch { /* unreadable .gitignore: ignore it */ }
    }

    private bool IsIgnored(string name, string relPath, bool isDir)
    {
        if (isDir && IgnoredDirs.Contains(name)) return true;
        foreach (var (pattern, dirOnly) in _gitignore)
        {
            if (dirOnly && !isDir) continue;
            if (pattern.IsMatch(name) || pattern.IsMatch(relPath)) return true;
        }
        return false;
    }

    /// <summary>Enumerate files (relative paths) until <paramref name="shouldStop"/> returns true.</summary>
    public IEnumerable<(string FullPath, string RelPath)> Files(Func<bool>? shouldStop = null, CancellationToken ct = default)
    {
        var stack = new Stack<string>();
        stack.Push(_root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (shouldStop?.Invoke() == true) yield break;
            var current = stack.Pop();

            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(current);
                dirs = Directory.GetDirectories(current);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            Array.Sort(files, StringComparer.Ordinal);
            foreach (var f in files)
            {
                var rel = Path.GetRelativePath(_root, f);
                if (IsIgnored(Path.GetFileName(f), rel, isDir: false)) continue;
                yield return (f, rel);
            }

            Array.Sort(dirs, StringComparer.Ordinal);
            for (var i = dirs.Length - 1; i >= 0; i--)
            {
                var d = dirs[i];
                var rel = Path.GetRelativePath(_root, d);
                if (IsIgnored(Path.GetFileName(d), rel, isDir: true)) continue;
                // Don't follow directory symlinks (loops, escaping the tree).
                try { if (new DirectoryInfo(d).LinkTarget != null) continue; } catch { continue; }
                stack.Push(d);
            }
        }
    }

    public static bool IsBinaryFile(string path)
    {
        try
        {
            var buffer = new byte[8000];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bytesRead = fs.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < bytesRead; i++)
                if (buffer[i] == 0) return true;
            return false;
        }
        catch { return true; }
    }
}
