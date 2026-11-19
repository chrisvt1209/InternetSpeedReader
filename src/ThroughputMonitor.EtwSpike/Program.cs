using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;
using ThroughputMonitor.Agent.Sampling;

// Throwaway spike: per-process (and per remote IP) network throughput from ETW,
// compared with the adapter counters. MUST run from an elevated (administrator) terminal.
// Usage: EtwSpike [adapter-name-substring] [top-n=8]

if (!OperatingSystem.IsWindows()) return 1;

bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
if (!elevated)
{
    Console.WriteLine("ETW needs administrator rights. Start the terminal with 'Run as administrator' and run again.");
    return 2;
}

string? adapterFilter = args.Length > 0 && args[0].Length > 0 ? args[0] : null;
int topN = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 8;

var nic = AdapterSelector.Select(adapterFilter);
if (nic is null) { Console.WriteLine("No adapter found."); return 1; }
var counters = new NetworkCounterSource(nic);
Console.WriteLine($"Adapter: {nic.Name} ({nic.Description})");

// ---- per-second accumulators, filled by the ETW callback thread --------------------------------
var perProcess = new ConcurrentDictionary<int, long[]>();                     // pid -> [rx, tx]
var perRemote = new ConcurrentDictionary<(int pid, string ip), long[]>();      // (pid, remote ip) -> [rx, tx]
long unattributedEvents = 0, totalEvents = 0;

// Microsoft-Windows-Kernel-Network event ids (data transfer events only).
// 10/26 TCP send v4/v6, 11/27 TCP recv v4/v6, 42/58 UDP send v4/v6, 43/59 UDP recv v4/v6
static bool IsSend(int id) => id is 10 or 26 or 42 or 58;
static bool IsRecv(int id) => id is 11 or 27 or 43 or 59;

const string SessionName = "ThroughputMonitorEtwSpike";
if (TraceEventSession.GetActiveSessionNames().Contains(SessionName))
    new TraceEventSession(SessionName).Stop();   // leftover from a killed run

using var session = new TraceEventSession(SessionName) { StopOnDispose = true };
session.EnableProvider("Microsoft-Windows-Kernel-Network");

session.Source.Dynamic.All += e =>
{
    if (e.ProviderName != "Microsoft-Windows-Kernel-Network") return;
    int id = (int)e.ID;
    bool send = IsSend(id), recv = IsRecv(id);
    if (!send && !recv) return;

    Interlocked.Increment(ref totalEvents);
    object? size = e.PayloadByName("size");
    if (size is null) return;
    long bytes = Convert.ToInt64(size);
    int pid = e.PayloadByName("PID") is { } p ? Convert.ToInt32(p) : e.ProcessID;
    if (pid <= 0) Interlocked.Increment(ref unattributedEvents);

    string remote = FormatAddress(e.PayloadByName("daddr"));
    if (remote is "127.0.0.1" or "::1") return;   // loopback never touches the NIC

    var pp = perProcess.GetOrAdd(pid, _ => new long[2]);
    var rr = perRemote.GetOrAdd((pid, remote), _ => new long[2]);
    int dir = send ? 1 : 0;
    Interlocked.Add(ref pp[dir], bytes);
    Interlocked.Add(ref rr[dir], bytes);
};

static string FormatAddress(object? raw) => raw switch
{
    null => "?",
    IPAddress ip => ip.ToString(),
    uint u => new IPAddress(u).ToString(),
    int i => new IPAddress((uint)i).ToString(),
    _ => raw.ToString() ?? "?",
};

var names = new ConcurrentDictionary<int, string>();
string NameOf(int pid)
{
    if (pid == 4) return "System";
    return names.GetOrAdd(pid, p =>
    {
        try { return Process.GetProcessById(p).ProcessName; }
        catch { return $"pid {p} (exited)"; }
    });
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var etwThread = Task.Run(() => session.Source.Process());

Console.WriteLine("Listening. Start a big download. Ctrl+C to stop.\n");

var sw = Stopwatch.StartNew();
var prev = counters.Read();
var prevT = sw.Elapsed;
using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
try
{
    while (await timer.WaitForNextTickAsync(cts.Token))
    {
        // swap out this second's data
        var procSnap = perProcess.ToArray(); perProcess.Clear();
        var remoteSnap = perRemote.ToArray(); perRemote.Clear();

        var cur = counters.Read();
        var t = sw.Elapsed;
        double dt = (t - prevT).TotalSeconds;
        bool ok = RateCalculator.TryCompute(prev, cur, out double nicRx, out double nicTx, out _);
        prev = cur; prevT = t;
        if (!ok) continue;

        double etwRx = procSnap.Sum(x => (double)x.Value[0]) / dt;
        double etwTx = procSnap.Sum(x => (double)x.Value[1]) / dt;

        if (nicRx + nicTx < 100_000 && etwRx + etwTx < 100_000) continue;   // quiet second, skip output

        Console.WriteLine($"{DateTime.Now:HH:mm:ss}  ADAPTER RX {nicRx * 8 / 1e6,7:F1} Mbps TX {nicTx * 8 / 1e6,7:F1} Mbps" +
                          $"   | ETW sum RX {etwRx * 8 / 1e6,7:F1} Mbps TX {etwTx * 8 / 1e6,7:F1} Mbps" +
                          $"   (ETW/adapter RX {(nicRx > 0 ? etwRx / nicRx : 0):P0})");

        foreach (var proc in procSnap.OrderByDescending(x => x.Value[0] + x.Value[1]).Take(topN))
        {
            var topRemote = remoteSnap.Where(r => r.Key.pid == proc.Key)
                                      .OrderByDescending(r => r.Value[0] + r.Value[1]).FirstOrDefault();
            string remote = topRemote.Key.ip is null ? "" : $"  top remote {topRemote.Key.ip}";
            Console.WriteLine($"    {NameOf(proc.Key),-26} pid {proc.Key,-6} RX {proc.Value[0] / dt / 1e6,7:F2} MB/s  TX {proc.Value[1] / dt / 1e6,7:F2} MB/s{remote}");
        }
    }
}
catch (OperationCanceledException) { }

session.Stop();
await etwThread;
Console.WriteLine($"\nEvents seen: {totalEvents}, without a process id: {unattributedEvents}");
return 0;
