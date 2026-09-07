using AwesomeAssertions;
using SeoLoodoi.Application.Links;

namespace SeoLoodoi.Domain.Tests;

public class InternalLinkGraphTests
{
    [Fact]
    public void Finds_orphans_and_calculates_explainable_degrees()
    {
        var home = new GraphPage(Guid.NewGuid(), "/", true, true);
        var category = new GraphPage(Guid.NewGuid(), "/category", false, true);
        var product = new GraphPage(Guid.NewGuid(), "/product", false, true);
        var orphan = new GraphPage(Guid.NewGuid(), "/orphan", false, true);
        var result = new InternalLinkGraph().Analyze([home,category,product,orphan], [new(home.Id,category.Id), new(category.Id,product.Id), new(home.Id,product.Id)]);
        result.Single(x => x.PageId == orphan.Id).IsOrphan.Should().BeTrue();
        result.Single(x => x.PageId == product.Id).InDegree.Should().Be(2);
        result.Single(x => x.PageId == home.Id).OutDegree.Should().Be(2);
        result.Should().OnlyContain(x => x.InternalAuthority >= 0 && x.InternalAuthority <= 100);
    }
    [Fact]
    public void Duplicate_edges_do_not_inflate_metrics()
    {
        var a = new GraphPage(Guid.NewGuid(), "/", true); var b = new GraphPage(Guid.NewGuid(), "/b");
        var result = new InternalLinkGraph().Analyze([a,b], [new(a.Id,b.Id), new(a.Id,b.Id)]);
        result.Single(x => x.PageId == b.Id).InDegree.Should().Be(1);
    }
}
