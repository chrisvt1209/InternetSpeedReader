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
        Assert.Null(EtwAddress.Parse(new byte[] { 1, 2, 3 }));
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
