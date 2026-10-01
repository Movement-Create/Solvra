using System.Collections.Concurrent;
using System.Diagnostics;

namespace Solvra.Core;

/// <summary>Owns detached processes started by a run and cleans up any the agent leaves behind.</summary>
public sealed class RunProcessTracker : IDisposable
{
    private readonly ConcurrentDictionary<int, DateTime> _processes = new();

    public void Register(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            _processes[pid] = process.StartTime.ToUniversalTime();
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        foreach (var (pid, registeredStart) in _processes)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.StartTime.ToUniversalTime() == registeredStart && !process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }
        _processes.Clear();
        GC.SuppressFinalize(this);
    }
}
