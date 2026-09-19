using System.Text.RegularExpressions;

namespace SeoLoodoi.Application.Content;

public sealed record ExtractedKeyword(string Term, int Count, decimal DensityPercentage, int GramSize, bool IsStuffing);

public sealed record ReadabilityMetrics(
    int WordCount,
    int SentenceCount,
    decimal AverageSentenceLength,
    int LongSentenceCount,
    decimal LongSentencePercentage,
    int ParagraphCount,
    decimal ReadabilityScore,
    string ReadabilityGrade);

public sealed record PageContentAnalysis(
    string Url,
    ReadabilityMetrics Readability,
    IReadOnlyList<ExtractedKeyword> TopKeywords,
    bool HasKeywordStuffing,
    bool IsThinContent);

public interface IContentQualityEngine
{
    PageContentAnalysis Analyze(string text, string url, string? language = null);
}

public sealed class ContentQualityEngine : IContentQualityEngine
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Persian stop words
        "در", "به", "از", "که", "این", "را", "با", "است", "برای", "آن", "یک", "شود", "شده",
        "خود", "ها", "می", "یا", "هم", "نیز", "تا", "بر", "اما", "وی", "شد", "کرد", "کند",
        "کرده", "بود", "گفت", "پس", "باید", "چون", "دیگر", "اگر", "همه", "ما", "من", "او",
        // English stop words
        "the", "and", "a", "an", "in", "to", "of", "for", "is", "on", "that", "by", "this",
        "with", "i", "you", "it", "not", "or", "be", "are", "from", "at", "as", "your",
        "all", "have", "new", "more", "was", "we", "will", "my", "has", "but", "our", "their"
    };

    private static readonly Regex SentenceSplitter = new(@"(?<=[.!?؟;\n])\s+", RegexOptions.Compiled);
    private static readonly Regex WordSplitter = new(@"[\p{L}\p{N}]+(?:[\u200C-][\p{L}\p{N}]+)*", RegexOptions.Compiled);

    public PageContentAnalysis Analyze(string text, string url, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            var emptyMetrics = new ReadabilityMetrics(0, 0, 0m, 0, 0m, 0, 0m, "VeryDifficult");
            return new(url, emptyMetrics, Array.Empty<ExtractedKeyword>(), false, true);
        }

        var words = WordSplitter.Matches(text).Select(m => m.Value.Trim()).Where(w => w.Length > 0).ToArray();
        var wordCount = words.Length;

        // Sentences
        var rawSentences = SentenceSplitter.Split(text).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        var sentenceCount = Math.Max(1, rawSentences.Length);
        var longSentences = 0;
        foreach (var s in rawSentences)
        {
            var sWords = WordSplitter.Matches(s).Count;
            if (sWords > 25) longSentences++;
        }

        var avgSentenceLength = decimal.Round((decimal)wordCount / sentenceCount, 1);
        var longSentencePct = decimal.Round((decimal)longSentences / sentenceCount * 100m, 1);

        // Paragraphs
        var paragraphs = text.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries).Length;

        // Readability calculation (adapted for international/Persian texts)
        var score = 100m;
        if (avgSentenceLength > 15m) score -= (avgSentenceLength - 15m) * 2m;
        score -= longSentencePct * 0.4m;
        score = decimal.Round(Math.Clamp(score, 0m, 100m), 1);

        var grade = score switch
        {
            >= 75m => "Easy",
            >= 55m => "Standard",
            >= 35m => "Difficult",
            _ => "VeryDifficult"
        };

        var readability = new ReadabilityMetrics(wordCount, sentenceCount, avgSentenceLength, longSentences, longSentencePct, Math.Max(1, paragraphs), score, grade);

        // Keywords extraction
        var topKeywords = ExtractKeywords(words);
        var hasStuffing = topKeywords.Any(k => k.IsStuffing);
        var isThin = wordCount < 150;

        return new(url, readability, topKeywords, hasStuffing, isThin);
    }

    private static IReadOnlyList<ExtractedKeyword> ExtractKeywords(IReadOnlyList<string> words)
    {
        if (words.Count == 0) return Array.Empty<ExtractedKeyword>();

        var normalized = words.Select(w => w.ToLowerInvariant()).ToArray();
        var totalWords = normalized.Length;

        // 1-grams
        var singleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in normalized)
        {
            if (w.Length < 3 || StopWords.Contains(w) || int.TryParse(w, out _)) continue;
            singleCounts[w] = singleCounts.GetValueOrDefault(w) + 1;
        }

        var keywords = new List<ExtractedKeyword>();
        foreach (var (term, count) in singleCounts.Where(x => x.Value >= 2))
        {
            var density = decimal.Round((decimal)count / totalWords * 100m, 2);
            var isStuffing = density >= 3.5m && count >= 4;
            keywords.Add(new ExtractedKeyword(term, count, density, 1, isStuffing));
        }

        // 2-grams
        var biCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < normalized.Length - 1; i++)
        {
            var w1 = normalized[i];
            var w2 = normalized[i + 1];
            if (w1.Length < 2 || w2.Length < 2) continue;
            if (StopWords.Contains(w1) && StopWords.Contains(w2)) continue;
            var bigram = $"{w1} {w2}";
            biCounts[bigram] = biCounts.GetValueOrDefault(bigram) + 1;
        }

        foreach (var (term, count) in biCounts.Where(x => x.Value >= 2))
        {
            var density = decimal.Round((decimal)(count * 2) / totalWords * 100m, 2);
            var isStuffing = density >= 2.5m && count >= 3;
            keywords.Add(new ExtractedKeyword(term, count, density, 2, isStuffing));
        }

        return keywords.OrderByDescending(k => k.Count).ThenByDescending(k => k.DensityPercentage).Take(30).ToArray();
    }
}
