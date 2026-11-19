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
            case byte[] bytes:
                return FromBytes(bytes);
            case string s when IPAddress.TryParse(s, out var parsed):
                return parsed;
            default:
                return null;
        }
    }

    private const byte AfInet = 2, AfInet6 = 23;

    /// <summary>
    /// Raw bytes come in three shapes: a plain address (4 or 16 bytes), a SOCKADDR_IN (16 bytes: family,
    /// port, 4 address bytes) or a SOCKADDR_IN6 (28 bytes: family, port, flow info, 16 address bytes, scope id).
    /// </summary>
    private static IPAddress? FromBytes(byte[] b)
    {
        // A socket address starts with its family in the first two bytes (little endian).
        if (b.Length == 28 && b[1] == 0 && b[0] == AfInet6)
            return new IPAddress(b.AsSpan(8, 16));
        if (b.Length == 16 && b[1] == 0 && b[0] == AfInet)
            return new IPAddress(b.AsSpan(4, 4));
        if (b.Length is 4 or 16)
            return new IPAddress(b);
        if (b.Length == 28)
            return new IPAddress(b.AsSpan(8, 16));   // an IPv6 socket address with an unexpected family field
        return null;
    }

    /// <summary>127.0.0.0/8, ::1 and IPv4-mapped forms of both: traffic that never leaves the machine.</summary>
    public static bool IsLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        return ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4());
    }

    public static string Format(object? raw) => Parse(raw)?.ToString() ?? raw?.ToString() ?? "?";
}
