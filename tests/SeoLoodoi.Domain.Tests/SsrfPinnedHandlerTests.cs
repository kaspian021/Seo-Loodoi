using System.Net;
using SeoLoodoi.Infrastructure.Security;
using AwesomeAssertions;

namespace SeoLoodoi.Domain.Tests;

/// <summary>
/// The connect-time layer of the SSRF defense. Validation must apply to the
/// exact address the socket connects to, otherwise a rebinding DNS answer can
/// swap a public IP for a private one between validation and connection.
/// </summary>
public sealed class SsrfPinnedHandlerTests
{
    [Fact]
    public void SelectSafeAddress_RejectsLoopback()
    {
        var act = () => OutboundUrlGuard.SelectSafeAddress([IPAddress.Loopback]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*protected network*");
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.9")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    public void SelectSafeAddress_RejectsPrivateRanges(string ip)
    {
        var act = () => OutboundUrlGuard.SelectSafeAddress([IPAddress.Parse(ip)]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SelectSafeAddress_RejectsEmptyResolution()
    {
        var act = () => OutboundUrlGuard.SelectSafeAddress([]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SelectSafeAddress_ReturnsTheValidatedAddress()
    {
        var expected = IPAddress.Parse("93.184.216.34");

        OutboundUrlGuard.SelectSafeAddress([expected]).Should().BeSameAs(expected);
    }

    [Fact]
    public async Task GuardedHandler_RefusesLoopbackAtConnectTime_WithoutAnyPriorValidation()
    {
        // No OutboundUrlGuard.ValidateAsync call happens before this request:
        // only the pinned connect callback stands between the socket and a
        // private address, and it must refuse.
        using var handler = SsrfPinnedHandler.Create();
        using var client = new HttpClient(handler);

        var act = () => client.GetAsync("http://127.0.0.1:9/", CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        exception.InnerException.Should().BeOfType<InvalidOperationException>().Which
            .Message.Should().Contain("protected network");
    }

    [Fact]
    public async Task GuardedHandler_RefusesMetadataAddressAtConnectTime()
    {
        using var handler = SsrfPinnedHandler.Create();
        using var client = new HttpClient(handler);

        var act = () => client.GetAsync("http://169.254.169.254/latest/meta-data/", CancellationToken.None);

        var exception = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        exception.InnerException.Should().BeOfType<InvalidOperationException>();
    }
}
