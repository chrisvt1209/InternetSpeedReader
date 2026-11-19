using System.Net;
using System.Runtime.Versioning;
using Microsoft.Diagnostics.Tracing.Session;

namespace ThroughputMonitor.Agent.Processes;

/// <summary>
/// Per-process network bytes from the Microsoft-Windows-Kernel-Network ETW provider.
/// Needs administrator rights. Runs in its own ETW session (no clash with other tools).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EtwProcessTrafficSource(
    ProcessIdentityResolver resolver, ILogger<EtwProcessTrafficSource> log) : IProcessTrafficSource, IHostedService, IDisposable
{
    private const string SessionName = "ThroughputMonitorAgent";
    private const string ProviderName = "Microsoft-Windows-Kernel-Network";

    // 10/26 TCP send v4/v6, 11/27 TCP receive v4/v6, 42/58 UDP send v4/v6, 43/59 UDP receive v4/v6
    private static bool IsSend(int id) => id is 10 or 26 or 42 or 58;
    private static bool IsReceive(int id) => id is 11 or 27 or 43 or 59;

    private readonly object _gate = new();
    private Dictionary<(int Pid, string Ip), long[]> _batch = [];   // [rx, tx]
    private TraceEventSession? _session;
    private Task? _processing;

    public bool IsActive => _session is not null;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (TraceEventSession.GetActiveSessionNames().Contains(SessionName))
                new TraceEventSession(SessionName).Stop();   // left over from a killed run

            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
            _session.EnableProvider(ProviderName);
            _session.Source.Dynamic.All += OnEvent;
            var source = _session.Source;
            _processing = Task.Run(() => source.Process());
            log.LogInformation("Per-process tracking started (ETW session {Session})", SessionName);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not start the ETW session; continuing without per-process data");
            _session?.Dispose();
            _session = null;
        }
        return Task.CompletedTask;
    }

    private void OnEvent(Microsoft.Diagnostics.Tracing.TraceEvent e)
    {
        if (e.ProviderName != ProviderName) return;
        int id = (int)e.ID;
        bool send = IsSend(id);
        if (!send && !IsReceive(id)) return;

        if (e.PayloadByName("size") is not { } sizeObj) return;
        long bytes = Convert.ToInt64(sizeObj);
        int pid = e.PayloadByName("PID") is { } p ? Convert.ToInt32(p) : e.ProcessID;

        string remote = FormatAddress(e.PayloadByName("daddr"));
        if (remote is "127.0.0.1" or "::1") return;   // loopback never touches the NIC

        resolver.Resolve(pid);   // resolve on first sight, while a short-lived process may still exist

        lock (_gate)
        {
            ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_batch, (pid, remote), out _);
            slot ??= new long[2];
            slot[send ? 1 : 0] += bytes;
        }
    }

    private static string FormatAddress(object? raw) => raw switch
    {
        null => "?",
        IPAddress ip => ip.ToString(),
        uint u => new IPAddress(u).ToString(),
        int i => new IPAddress((uint)i).ToString(),
        _ => raw.ToString() ?? "?",
    };

    public ProcessTrafficSnapshot Drain()
    {
        Dictionary<(int Pid, string Ip), long[]> taken;
        lock (_gate)
        {
            if (_batch.Count == 0) return ProcessTrafficSnapshot.Empty;
            taken = _batch;
            _batch = [];
        }

        var processes = new List<ProcessTraffic>();
        foreach (var group in taken.GroupBy(kv => kv.Key.Pid))
        {
            var identity = resolver.Resolve(group.Key);
            var remotes = group.Select(kv => new RemoteTraffic(kv.Key.Ip, kv.Value[0], kv.Value[1])).ToList();
            processes.Add(new ProcessTraffic(group.Key, identity.Name, identity.ExePath,
                remotes.Sum(r => r.RxBytes), remotes.Sum(r => r.TxBytes), remotes));
        }
        return new ProcessTrafficSnapshot(processes);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _session?.Stop(); } catch { /* already stopped */ }
        if (_processing is not null)
            await Task.WhenAny(_processing, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken));
    }

    public void Dispose() => _session?.Dispose();
}
