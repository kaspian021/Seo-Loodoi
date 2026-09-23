using System.Text.Json;

namespace SeoLoodoi.Application.Crawling.Rendering;

public enum DiffSeverity { Info, Warning, Critical }

/// <summary>One compared field. Raw/Rendered hold the observed values (truncated) as evidence.</summary>
public sealed record FieldDiff(string Field, bool Changed, DiffSeverity Severity, string? Raw, string? Rendered, IReadOnlyList<string>? OnlyInRaw = null, IReadOnlyList<string>? OnlyInRendered = null);

public sealed record RenderDiff(IReadOnlyList<FieldDiff> Fields, int RawWordCount, int RenderedWordCount)
{
    public int CriticalCount => Fields.Count(x => x.Changed && x.Severity == DiffSeverity.Critical);
    public int ChangedCount => Fields.Count(x => x.Changed);
    public FieldDiff? Get(string field) => Fields.FirstOrDefault(x => x.Field == field);
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
}

/// <summary>
/// Deterministic raw-HTML vs rendered-DOM comparison (D3). Pure function of the
/// two extracted pages: no AI, no randomness, and sets are compared in ordinal sort
/// order, so the same input always produces byte-identical evidence JSON.
/// </summary>
public static class RawVsRenderedComparer
{
    public const int MaxListEvidence = 50;
    public const int MaxValueChars = 500;
    /// <summary>Rendered text is considered JS-dependent when it adds at least this many words and grows by ≥ 30 %.</summary>
    public const int TextGrowthWords = 50;

    public static RenderDiff Compare(ExtractedPage raw, ExtractedPage rendered)
    {
        var fields = new List<FieldDiff>
        {
            Scalar("title", raw.Title, rendered.Title, DiffSeverity.Critical),
            Scalar("metaDescription", raw.MetaDescription, rendered.MetaDescription, DiffSeverity.Warning),
            Scalar("robots", NormalizeDirectives(raw.Robots), NormalizeDirectives(rendered.Robots), DiffSeverity.Critical),
            Scalar("canonical", raw.Canonical, rendered.Canonical, DiffSeverity.Critical),
            Scalar("language", raw.Language, rendered.Language, DiffSeverity.Info),
            Set("h1", raw.Headings.Where(x => x.Level == 1).Select(x => x.Text), rendered.Headings.Where(x => x.Level == 1).Select(x => x.Text), DiffSeverity.Critical),
            Set("headings", raw.Headings.Where(x => x.Level > 1).Select(x => $"h{x.Level}:{x.Text}"), rendered.Headings.Where(x => x.Level > 1).Select(x => $"h{x.Level}:{x.Text}"), DiffSeverity.Warning),
            Set("hreflang", (raw.Hreflang ?? []).Select(x => $"{x.Language.ToLowerInvariant()}={x.Target.AbsoluteUri}"), (rendered.Hreflang ?? []).Select(x => $"{x.Language.ToLowerInvariant()}={x.Target.AbsoluteUri}"), DiffSeverity.Critical),
            Set("internalLinks", raw.Links.Where(x => x.IsInternal).Select(LinkKey), rendered.Links.Where(x => x.IsInternal).Select(LinkKey), DiffSeverity.Critical),
            Set("externalLinks", raw.Links.Where(x => !x.IsInternal).Select(LinkKey), rendered.Links.Where(x => !x.IsInternal).Select(LinkKey), DiffSeverity.Info),
            Set("nofollowLinks", raw.Links.Where(IsNofollow).Select(LinkKey), rendered.Links.Where(IsNofollow).Select(LinkKey), DiffSeverity.Warning),
            Set("structuredDataTypes", SchemaTypes(raw.JsonLd), SchemaTypes(rendered.JsonLd), DiffSeverity.Critical),
            Set("images", Images(raw), Images(rendered), DiffSeverity.Warning),
            Text(raw, rendered),
        };
        return new RenderDiff(fields, raw.WordCount, rendered.WordCount);
    }

    private static FieldDiff Scalar(string field, string? raw, string? rendered, DiffSeverity severity)
    {
        var r = Norm(raw); var d = Norm(rendered);
        return new FieldDiff(field, !string.Equals(r, d, StringComparison.Ordinal), severity, Trunc(r), Trunc(d));
    }

    private static FieldDiff Set(string field, IEnumerable<string> raw, IEnumerable<string> rendered, DiffSeverity severity)
    {
        var r = raw.Select(Norm).Where(x => x is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        var d = rendered.Select(Norm).Where(x => x is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        var onlyRaw = r.Except(d).Order(StringComparer.Ordinal).ToArray();
        var onlyRendered = d.Except(r).Order(StringComparer.Ordinal).ToArray();
        var changed = onlyRaw.Length > 0 || onlyRendered.Length > 0;
        return new FieldDiff(field, changed, severity, r.Count.ToString(), d.Count.ToString(),
            onlyRaw.Take(MaxListEvidence).Select(x => Trunc(x)!).ToArray(), onlyRendered.Take(MaxListEvidence).Select(x => Trunc(x)!).ToArray());
    }

    private static FieldDiff Text(ExtractedPage raw, ExtractedPage rendered)
    {
        var added = rendered.WordCount - raw.WordCount;
        var jsDependent = added >= TextGrowthWords && rendered.WordCount >= raw.WordCount * 1.3;
        var removed = raw.WordCount - rendered.WordCount;
        var lost = removed >= TextGrowthWords && raw.WordCount >= rendered.WordCount * 1.3;
        var severity = jsDependent || lost ? DiffSeverity.Critical : DiffSeverity.Info;
        return new FieldDiff("textWordCount", jsDependent || lost, severity, raw.WordCount.ToString(), rendered.WordCount.ToString());
    }

    private static string LinkKey(ExtractedLink link) => link.Target.GetLeftPart(UriPartial.Query);
    private static bool IsNofollow(ExtractedLink link) => link.Rel?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(x => x.Equals("nofollow", StringComparison.OrdinalIgnoreCase)) == true;

    private static IEnumerable<string> Images(ExtractedPage page) =>
        (page.Assets ?? []).Where(x => x.Type == AssetType.Image).Select(x => x.Url);

    /// <summary>Only the @type values are compared, which stays stable when scripts re-serialize the same JSON-LD with different whitespace or key order.</summary>
    internal static IEnumerable<string> SchemaTypes(IEnumerable<string> jsonLd)
    {
        var types = new List<string>();
        foreach (var block in jsonLd)
        {
            try
            {
                using var doc = JsonDocument.Parse(block, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = true });
                Collect(doc.RootElement, types, 0);
            }
            catch (JsonException) { types.Add("#invalid-json-ld"); }
        }
        return types;
    }

    private static void Collect(JsonElement element, List<string> types, int depth)
    {
        if (depth > 8 || types.Count > 200) return;
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) Collect(item, types, depth + 1);
                break;
            case JsonValueKind.Object:
                if (element.TryGetProperty("@type", out var type))
                {
                    if (type.ValueKind == JsonValueKind.String) types.Add(type.GetString()!);
                    else if (type.ValueKind == JsonValueKind.Array) types.AddRange(type.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
                }
                if (element.TryGetProperty("@graph", out var graph)) Collect(graph, types, depth + 1);
                break;
        }
    }

    private static string? NormalizeDirectives(string? robots) =>
        robots is null ? null : string.Join(",", robots.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).Order(StringComparer.Ordinal));

    private static string? Norm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string? Trunc(string? value) => value is null ? null : value.Length <= MaxValueChars ? value : value[..MaxValueChars];
}
