using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace ThroughputMonitor.Agent.Processes;

public sealed record ProcessIdentity(string Name, string? ExePath);

/// <summary>
/// Maps a pid to a process name and exe path. Results are cached for a few minutes (pids get reused).
/// Short-lived processes that already exited are reported as "pid N (exited)".
/// </summary>
public sealed class ProcessIdentityResolver
{
    private const long CacheMillis = 5 * 60 * 1000;
    private readonly ConcurrentDictionary<int, (ProcessIdentity Identity, long ResolvedAt)> _cache = new();

    public ProcessIdentity Resolve(int pid)
    {
        long now = Environment.TickCount64;
        if (_cache.TryGetValue(pid, out var hit) && now - hit.ResolvedAt < CacheMillis)
            return hit.Identity;

        var identity = Lookup(pid, hit.Identity);
        _cache[pid] = (identity, now);
        return identity;
    }

    private static ProcessIdentity Lookup(int pid, ProcessIdentity? previous)
    {
        if (pid == 4) return new ProcessIdentity("System", null);
        if (pid <= 0) return new ProcessIdentity("Idle/unknown", null);

        string? path = QueryImagePath(pid);
        if (path is not null)
            return new ProcessIdentity(Path.GetFileNameWithoutExtension(path), path);

        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return new ProcessIdentity(p.ProcessName, null);
        }
        catch
        {
            // Exited: keep what we learned earlier, if anything.
            return previous ?? new ProcessIdentity($"pid {pid} (exited)", null);
        }
    }

    // QueryFullProcessImageName works for protected processes where Process.MainModule throws.
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    private static string? QueryImagePath(int pid)
    {
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }
}
