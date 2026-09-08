using System.Net;
using AwesomeAssertions;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Domain.Tests;

public class OutboundUrlGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.1.2")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:127.0.0.1")]
    public void Blocks_non_public_ranges(string address) => OutboundUrlGuard.IsForbidden(IPAddress.Parse(address)).Should().BeTrue();

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void Allows_public_ranges(string address) => OutboundUrlGuard.IsForbidden(IPAddress.Parse(address)).Should().BeFalse();
}
