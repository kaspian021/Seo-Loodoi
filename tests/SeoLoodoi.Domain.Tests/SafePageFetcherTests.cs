using System.Net;
using System.Text;
using AwesomeAssertions;
using SeoLoodoi.Infrastructure.Crawling;
using SeoLoodoi.Infrastructure.Security;

namespace SeoLoodoi.Domain.Tests;

public class SafePageFetcherTests
{
    [Fact]
    public async Task Revalidates_every_redirect_destination()
    {
        var handler = new StubHandler((request, call) => call == 1
            ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://cdn.example/final") } }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        var guard = new RecordingGuard();
        var result = await new SafePageFetcher(new HttpClient(handler), guard, Coordinator()).FetchAsync(new Uri("https://example.com/start"), 100, CancellationToken.None);
        guard.Checked.Select(x => x.Host).Should().Equal("example.com", "cdn.example");
        result.RedirectChain.Should().ContainSingle();
        Encoding.UTF8.GetString(result.Content).Should().Be("ok");
    }

    [Fact]
    public async Task Can_keep_redirect_as_evidence_when_follow_redirects_are_disabled()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://cdn.example/final") } });
        var guard = new RecordingGuard();
        var result = await new SafePageFetcher(new HttpClient(handler), guard, Coordinator()).FetchAsync(new Uri("https://example.com/start"), 100, "TestBot/1.0", false, 30, CancellationToken.None);
        result.StatusCode.Should().Be((int)HttpStatusCode.Redirect);
        result.FinalUri.Should().Be(new Uri("https://example.com/start"));
        result.RedirectChain.Should().BeEmpty();
        guard.Checked.Select(x => x.Host).Should().Equal("example.com");
    }

    [Fact]
    public async Task Rejects_stream_that_exceeds_limit_even_without_content_length()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[101])) });
        var act = () => new SafePageFetcher(new HttpClient(handler), new RecordingGuard(), Coordinator()).FetchAsync(new Uri("https://example.com"), 100, CancellationToken.None);
        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*size limit*");
    }

    [Fact]
    public async Task Records_detailed_redirect_hops_with_status_codes()
    {
        var handler = new StubHandler((request, call) => call switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("https://example.com/step2") } },
            2 => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://example.com/step3") } },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("done") }
        });
        var guard = new RecordingGuard();
        var result = await new SafePageFetcher(new HttpClient(handler), guard, Coordinator()).FetchAsync(new Uri("https://example.com/step1"), 1000, CancellationToken.None);
        result.RedirectChain.Should().HaveCount(2);
        result.RedirectHops.Should().NotBeNull();
        result.RedirectHops!.Count.Should().Be(2);
        result.RedirectHops[0].StatusCode.Should().Be(301);
        result.RedirectHops[0].FromUrl.Should().Be("https://example.com/step1");
        result.RedirectHops[0].ToUrl.Should().Be("https://example.com/step2");
        result.RedirectHops[1].StatusCode.Should().Be(307);
        result.RedirectHops[1].FromUrl.Should().Be("https://example.com/step2");
        result.RedirectHops[1].ToUrl.Should().Be("https://example.com/step3");
    }

    [Fact]
    public async Task Detects_circular_redirect_loop_and_throws()
    {
        var handler = new StubHandler((request, call) => call switch
        {
            1 => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/b") } },
            _ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://example.com/a") } }
        });
        var guard = new RecordingGuard();
        var act = () => new SafePageFetcher(new HttpClient(handler), guard, Coordinator()).FetchAsync(new Uri("https://example.com/a"), 1000, CancellationToken.None);
        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*Redirect loop detected*");
    }

    private static IHostRequestCoordinator Coordinator() => new HostRequestCoordinator(TimeProvider.System, 10, TimeSpan.Zero);
    private sealed class RecordingGuard : IOutboundUrlGuard
    {
        public List<Uri> Checked { get; } = [];
        public Task ValidateAsync(Uri uri, CancellationToken ct) { Checked.Add(uri); return Task.CompletedTask; }
    }
    private sealed class StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(responder(request, ++_calls));
    }
}
