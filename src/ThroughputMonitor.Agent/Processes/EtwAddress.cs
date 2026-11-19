using System.Net;

namespace ThroughputMonitor.Agent.Processes;

/// <summary>Turns the address field of an ETW network event into an <see cref="IPAddress"/> and recognises loopback.</summary>
public static class EtwAddress
{
    /// <summary>The payload may arrive as an IPAddress, a number (IPv4), raw bytes, or text.</summary>
    public static IPAddress? Parse(object? raw)
    {
        switch (raw)
        {
            case null:
                return null;
            case IPAddress ip:
                return ip;
            case uint u:
                return new IPAddress(u);
            case int i:
                return new IPAddress((uint)i);
            case byte[] { Length: 4 or 16 } bytes:
                return new IPAddress(bytes);
            case string s when IPAddress.TryParse(s, out var parsed):
                return parsed;
            default:
                return null;
        }
    }

    /// <summary>127.0.0.0/8, ::1 and IPv4-mapped forms of both: traffic that never leaves the machine.</summary>
    public static bool IsLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        return ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4());
    }

    public static string Format(object? raw) => Parse(raw)?.ToString() ?? raw?.ToString() ?? "?";
}
