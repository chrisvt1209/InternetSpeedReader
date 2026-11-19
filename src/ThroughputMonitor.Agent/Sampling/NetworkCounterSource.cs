using System.Diagnostics;
using System.Net.NetworkInformation;

namespace ThroughputMonitor.Agent.Sampling;

public sealed class NetworkCounterSource(NetworkInterface adapter) : ICounterSource
{
    public string AdapterName => adapter.Name;
    public string AdapterDescription => adapter.Description;

    public CounterReading Read()
    {
        var stats = adapter.GetIPStatistics();
        return new CounterReading(stats.BytesReceived, stats.BytesSent, adapter.Speed, Stopwatch.GetTimestamp());
    }
}

public static class AdapterSelector
{
    private static readonly string[] VirtualMarkers =
        ["vEthernet", "Hyper-V", "Virtual", "VPN", "TAP", "WSL", "VMware", "VirtualBox", "Loopback", "Bluetooth", "Tunnel"];

    public static bool LooksVirtual(NetworkInterface n)
    {
        string s = $"{n.Name} {n.Description}";
        return VirtualMarkers.Any(m => s.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>First wired, operational, non-virtual adapter, or the one matching <paramref name="nameContains"/>.</summary>
    public static NetworkInterface? Select(string? nameContains)
    {
        var all = NetworkInterface.GetAllNetworkInterfaces();

        if (!string.IsNullOrWhiteSpace(nameContains))
            return all.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
                && $"{n.Name} {n.Description}".Contains(nameContains, StringComparison.OrdinalIgnoreCase));

        return all.FirstOrDefault(n => n.OperationalStatus == OperationalStatus.Up
            && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
            && !LooksVirtual(n));
    }
}
