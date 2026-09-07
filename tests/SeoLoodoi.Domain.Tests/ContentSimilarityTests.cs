using AwesomeAssertions;
using SeoLoodoi.Application.Content;

namespace SeoLoodoi.Domain.Tests;

public class ContentSimilarityTests
{
    private readonly ContentSimilarityEngine _sut = new();
    [Fact]
    public void Normalizes_arabic_persian_variants_but_preserves_internal_zwnj()
    {
        _sut.Normalize("  كتاب‌هايِ  خوبـ  ", "fa").Should().Be("کتاب‌های خوب");
        _sut.Normalize("می‌روم").Should().Contain("\u200C");
    }
    [Fact]
    public void Detects_exact_and_near_duplicates()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
        var docs = new[] { new ContentDocument(a, "راهنمای کامل سئو فنی برای وب سایت حرفه ای"), new ContentDocument(b, "راهنماي كامل سئو فني براي وب سايت حرفه اي"), new ContentDocument(c, "دستور پخت یک غذای خانگی ساده") };
        var clusters = _sut.Cluster(docs, .8m, "fa");
        clusters.Should().ContainSingle(); clusters[0].DocumentIds.Should().BeEquivalentTo(new[] { a, b }); clusters[0].Exact.Should().BeTrue();
    }
    [Fact]
    public void Similarity_does_not_claim_unrelated_content_is_duplicate()
    {
        _sut.Similarity(_sut.Normalize("آموزش سئو تکنیکال وب سایت"), _sut.Normalize("قیمت بلیط هواپیما تهران شیراز")).Should().BeLessThan(.2m);
    }
}
