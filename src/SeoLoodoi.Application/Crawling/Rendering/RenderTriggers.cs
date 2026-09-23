using System.Text.RegularExpressions;

namespace SeoLoodoi.Application.Crawling.Rendering;

/// <summary>
/// Deterministic SPA / JS-dependency signals computed from the raw HTML only (D5).
/// Used by "auto" render mode to decide whether a page needs rendering (HTML-first).
/// </summary>
public static partial class RenderTriggers
{
    public const string EmptyAppShell = "EMPTY_APP_SHELL";
    public const string LowTextHighScript = "LOW_TEXT_HIGH_SCRIPT";
    public const string FrameworkMarker = "FRAMEWORK_MARKER";
    public const string NoscriptWarning = "NOSCRIPT_CONTENT";
    public const string MissingTitle = "MISSING_TITLE";
    public const string MissingH1 = "MISSING_H1";
    public const string ClientRedirect = "CLIENT_REDIRECT";

    public sealed record Input(string RawHtml, string? Title, int H1Count, int WordCount, int ScriptCount, int LinkCount);

    public static IReadOnlyList<string> Detect(Input input)
    {
        var signals = new List<string>();
        var html = input.RawHtml.Length > 2_000_000 ? input.RawHtml[..2_000_000] : input.RawHtml;
        if (AppShellRegex().IsMatch(html) && input.WordCount < 50) signals.Add(EmptyAppShell);
        if (input.WordCount < 100 && input.ScriptCount >= 3) signals.Add(LowTextHighScript);
        if (FrameworkRegex().IsMatch(html)) signals.Add(FrameworkMarker);
        if (NoscriptRegex().IsMatch(html)) signals.Add(NoscriptWarning);
        if (string.IsNullOrWhiteSpace(input.Title)) signals.Add(MissingTitle);
        if (input.H1Count == 0) signals.Add(MissingH1);
        if (ClientRedirectRegex().IsMatch(html)) signals.Add(ClientRedirect);
        return signals;
    }

    /// <summary>Signals strong enough on their own to justify spending a render in auto mode.</summary>
    public static bool ShouldRender(IReadOnlyList<string> signals) =>
        signals.Contains(EmptyAppShell) || signals.Contains(LowTextHighScript) || signals.Contains(ClientRedirect) ||
        (signals.Contains(FrameworkMarker) && (signals.Contains(MissingTitle) || signals.Contains(MissingH1) || signals.Contains(NoscriptWarning)));

    [GeneratedRegex("""<div[^>]+id\s*=\s*["'](root|app|__next|__nuxt|svelte|q-app)["'][^>]*>\s*</div>""", RegexOptions.IgnoreCase)]
    private static partial Regex AppShellRegex();
    [GeneratedRegex("""(data-reactroot|__NEXT_DATA__|window\.__NUXT__|ng-version=|data-v-app|data-server-rendered|/_next/static/|/_nuxt/|webpackJsonp|data-svelte-h|astro-island)""", RegexOptions.IgnoreCase)]
    private static partial Regex FrameworkRegex();
    [GeneratedRegex("""<noscript[^>]*>[^<]{0,300}(enable|requires?)\s+javascript""", RegexOptions.IgnoreCase)]
    private static partial Regex NoscriptRegex();
    [GeneratedRegex("""(<meta[^>]+http-equiv\s*=\s*["']?refresh[^>]+url=|(?<![\w.-])(window\.|document\.)?location(\.href)?\s*=\s*["']|(?<![\w-])location\.(replace|assign)\s*\()""", RegexOptions.IgnoreCase)]
    private static partial Regex ClientRedirectRegex();
}
