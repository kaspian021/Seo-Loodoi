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
    public async Task Rejects_stream_that_exceeds_limit_even_without_content_length()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[101])) });
        var act = () => new SafePageFetcher(new HttpClient(handler), new RecordingGuard(), Coordinator()).FetchAsync(new Uri("https://example.com"), 100, CancellationToken.None);
        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("*size limit*");
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
