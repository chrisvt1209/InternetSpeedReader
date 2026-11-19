using System.Diagnostics;
using System.Net.NetworkInformation;

// Throwaway spike: prints RX/TX throughput of the wired Ethernet adapter once per second.
// Usage: SamplerSpike [adapter-name-substring] [reference-mbps=1000]

const double DefaultReferenceMbps = 1000;
string? filter = args.Length > 0 ? args[0] : null;
double referenceMbps = args.Length > 1 && double.TryParse(args[1], out var r) ? r : DefaultReferenceMbps;

static bool LooksVirtual(NetworkInterface n)
{
    string s = $"{n.Name} {n.Description}";
    string[] bad = ["vEthernet", "Hyper-V", "Virtual", "VPN", "TAP", "WSL", "VMware", "VirtualBox", "Loopback", "Bluetooth", "Tunnel"];
    return bad.Any(b => s.Contains(b, StringComparison.OrdinalIgnoreCase));
}

Console.WriteLine("Adapters found:");
var all = NetworkInterface.GetAllNetworkInterfaces();
foreach (var n in all)
{
    Console.WriteLine($"  [{n.OperationalStatus,-4}] {n.NetworkInterfaceType,-22} {(n.Speed / 1_000_000),6} Mbps  virtual={LooksVirtual(n),-5}  {n.Name} ({n.Description})");
}

var candidates = all
    .Where(n => n.OperationalStatus == OperationalStatus.Up
             && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
             && !LooksVirtual(n))
    .ToList();

var adapter = filter is null
    ? candidates.FirstOrDefault()
    : all.FirstOrDefault(n => $"{n.Name} {n.Description}".Contains(filter, StringComparison.OrdinalIgnoreCase));

if (adapter is null)
{
    Console.WriteLine("\nNo matching adapter found. Pass part of an adapter name as the first argument.");
    return 1;
}

Console.WriteLine($"\nMonitoring: {adapter.Name} ({adapter.Description}), link {adapter.Speed / 1_000_000} Mbps, reference {referenceMbps} Mbps");
Console.WriteLine("Press Ctrl+C to stop. Start a big download to see throughput.\n");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var sw = Stopwatch.StartNew();
var prev = adapter.GetIPStatistics();
long prevRx = prev.BytesReceived, prevTx = prev.BytesSent;
TimeSpan prevT = sw.Elapsed;
double peakRx = 0, peakTx = 0;

using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
try
{
    while (await timer.WaitForNextTickAsync(cts.Token))
    {
        var now = adapter.GetIPStatistics();
        TimeSpan t = sw.Elapsed;
        double dt = (t - prevT).TotalSeconds;   // real elapsed time, not an assumed 1.000 s

        long dRx = now.BytesReceived - prevRx;
        long dTx = now.BytesSent - prevTx;
        prevRx = now.BytesReceived; prevTx = now.BytesSent; prevT = t;

        if (dRx < 0 || dTx < 0 || dt <= 0)
        {
            Console.WriteLine("(counter reset, sample skipped)");
            continue;
        }

        double rx = dRx / dt, tx = dTx / dt;      // bytes per second
        peakRx = Math.Max(peakRx, rx); peakTx = Math.Max(peakTx, tx);
        double rxMbps = rx * 8 / 1e6, txMbps = tx * 8 / 1e6;

        Console.WriteLine(
            $"{DateTime.Now:HH:mm:ss}  RX {rx / 1e6,8:F2} MB/s {rxMbps,8:F1} Mbps ({rxMbps / referenceMbps,6:P1})" +
            $"   TX {tx / 1e6,8:F2} MB/s {txMbps,8:F1} Mbps ({txMbps / referenceMbps,6:P1})");
    }
}
catch (OperationCanceledException) { }

Console.WriteLine($"\nPeak RX {peakRx * 8 / 1e6:F1} Mbps, peak TX {peakTx * 8 / 1e6:F1} Mbps");
return 0;
