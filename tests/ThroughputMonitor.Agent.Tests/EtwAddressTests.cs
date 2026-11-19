using System.Net;
using ThroughputMonitor.Agent.Processes;

namespace ThroughputMonitor.Agent.Tests;

public class EtwAddressTests
{
    [Fact]
    public void Addresses_are_understood_in_every_form_an_event_can_carry_them()
    {
        Assert.Equal(IPAddress.Parse("185.67.58.203"), EtwAddress.Parse(IPAddress.Parse("185.67.58.203")));
        Assert.Equal(IPAddress.Parse("1.2.3.4"), EtwAddress.Parse(new byte[] { 1, 2, 3, 4 }));
        Assert.Equal(IPAddress.Parse("2001:db8::1"), EtwAddress.Parse(IPAddress.Parse("2001:db8::1").GetAddressBytes()));
        Assert.Equal(IPAddress.Parse("10.0.0.7"), EtwAddress.Parse("10.0.0.7"));
        Assert.Equal(IPAddress.Parse("1.2.3.4"), EtwAddress.Parse(0x04030201u));       // first octet in the low byte
        Assert.Null(EtwAddress.Parse(null));
        Assert.Null(EtwAddress.Parse("not an address"));
    }

    [Fact]
    public void Socket_address_structures_are_unpacked_so_ipv6_no_longer_shows_as_System_Byte_array()
    {
        // SOCKADDR_IN6: family 23, port, flow info, 16 address bytes, scope id (28 bytes in all).
        byte[] SockAddrIn6(IPAddress ip)
        {
            var b = new byte[28];
            b[0] = 23;
            ip.GetAddressBytes().CopyTo(b, 8);
            return b;
        }

        Assert.Equal(IPAddress.IPv6Loopback, EtwAddress.Parse(SockAddrIn6(IPAddress.IPv6Loopback)));
        Assert.Equal(IPAddress.Parse("2001:db8::7"), EtwAddress.Parse(SockAddrIn6(IPAddress.Parse("2001:db8::7"))));

        // SOCKADDR_IN: family 2, port, then 4 address bytes (16 bytes in all).
        var v4 = new byte[16];
        v4[0] = 2;
        new byte[] { 185, 67, 58, 203 }.CopyTo(v4, 4);
        Assert.Equal(IPAddress.Parse("185.67.58.203"), EtwAddress.Parse(v4));
    }

    [Fact]
    public void Ipv6_loopback_inside_a_socket_address_is_filtered_like_any_loopback()
    {
        var b = new byte[28];
        b[0] = 23;
        IPAddress.IPv6Loopback.GetAddressBytes().CopyTo(b, 8);

        var ip = EtwAddress.Parse(b);

        Assert.NotNull(ip);
        Assert.True(EtwAddress.IsLoopback(ip!));
    }

    [Fact]
    public void Bytes_of_an_unknown_shape_are_not_guessed_at()
    {
        Assert.Null(EtwAddress.Parse(new byte[] { 1, 2, 3 }));
        Assert.Null(EtwAddress.Parse(new byte[40]));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("127.1.2.3", true)]
    [InlineData("::1", true)]
    [InlineData("0:0:0:0:0:0:0:1", true)]               // the same IPv6 loopback, written out
    [InlineData("::ffff:127.0.0.1", true)]              // IPv4 loopback in IPv6 clothing
    [InlineData("192.168.1.20", false)]
    [InlineData("185.67.58.203", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Loopback_is_recognised_so_localhost_traffic_is_not_counted(string text, bool expected)
    {
        Assert.Equal(expected, EtwAddress.IsLoopback(IPAddress.Parse(text)));
    }

    [Fact]
    public void Unparsable_values_are_still_shown_as_text_and_missing_ones_as_a_question_mark()
    {
        Assert.Equal("weird", EtwAddress.Format("weird"));
        Assert.Equal("?", EtwAddress.Format(null));
        Assert.Equal("1.2.3.4", EtwAddress.Format(new byte[] { 1, 2, 3, 4 }));
    }
}
