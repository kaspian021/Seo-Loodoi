using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SeoLoodoi.Application.Content;

public sealed record ContentDocument(Guid Id, string Text);
public sealed record DuplicateCluster(IReadOnlyList<Guid> DocumentIds, decimal Similarity, bool Exact);

public interface IContentSimilarityEngine
{
    string Normalize(string text, string? language = null);
    string ExactHash(string normalizedText);
    decimal Similarity(string left, string right);
    IReadOnlyList<DuplicateCluster> Cluster(IReadOnlyList<ContentDocument> documents, decimal threshold = .85m, string? language = null);
}

public sealed class ContentSimilarityEngine : IContentSimilarityEngine
{
    private static readonly Regex Marks = new("[\\p{Mn}\\u0640]", RegexOptions.Compiled);
    private static readonly Regex Separators = new("[^\\p{L}\\p{N}\\u200C]+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new("\\s+", RegexOptions.Compiled);

    public string Normalize(string text, string? language = null)
    {
        var value = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant()
            .Replace('ي', 'ی').Replace('ى', 'ی').Replace('ك', 'ک')
            .Replace(" \u200C", "\u200C").Replace("\u200C ", "\u200C");
        value = Marks.Replace(value, string.Empty);
        value = Separators.Replace(value, " ");
        return Spaces.Replace(value, " ").Trim();
    }
    public string ExactHash(string normalizedText) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedText)));
    public decimal Similarity(string left, string right)
    {
        if (left == right) return 1m;
        var a = Shingles(left); var b = Shingles(right);
        if (a.Count == 0 || b.Count == 0) return 0m;
        var intersection = a.Count(b.Contains); var union = a.Count + b.Count - intersection;
        return decimal.Round((decimal)intersection / union, 4);
    }
    public IReadOnlyList<DuplicateCluster> Cluster(IReadOnlyList<ContentDocument> documents, decimal threshold = .85m, string? language = null)
    {
        if (threshold is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(threshold));
        var prepared = documents.Select(x => (x.Id, Text: Normalize(x.Text, language))).ToArray();
        var parent = Enumerable.Range(0, prepared.Length).ToArray(); var similarities = new Dictionary<(int,int),decimal>();
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }
        for (var i = 0; i < prepared.Length; i++) for (var j = i + 1; j < prepared.Length; j++)
        {
            var similarity = Similarity(prepared[i].Text, prepared[j].Text); similarities[(i,j)] = similarity;
            if (similarity >= threshold) Union(i,j);
        }
        return Enumerable.Range(0, prepared.Length).GroupBy(Find).Where(g => g.Count() > 1).Select(group =>
        {
            var indexes = group.ToArray(); var minimum = 1m;
            for (var a = 0; a < indexes.Length; a++) for (var b = a + 1; b < indexes.Length; b++) minimum = Math.Min(minimum, similarities[(Math.Min(indexes[a],indexes[b]), Math.Max(indexes[a],indexes[b]))]);
            return new DuplicateCluster(indexes.Select(i => prepared[i].Id).ToArray(), minimum, minimum == 1m);
        }).ToArray();
    }
    private static HashSet<string> Shingles(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3) return words.ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(0, words.Length - 2).Select(i => $"{words[i]} {words[i+1]} {words[i+2]}").ToHashSet(StringComparer.Ordinal);
    }
}
