using System.Text.RegularExpressions;

namespace SeoLoodoi.Application.Crawling;

public sealed record RobotsRule(bool Allow, string Pattern);
public sealed record RobotsGroup(IReadOnlyList<string> UserAgents, IReadOnlyList<RobotsRule> Rules, TimeSpan? CrawlDelay);
public sealed record RobotsDocument(IReadOnlyList<RobotsGroup> Groups, IReadOnlyList<Uri> Sitemaps)
{
    public bool IsAllowed(string userAgent, Uri uri)
    {
        var candidates = Groups.Select(g => (Group: g, Match: g.UserAgents.Max(a => AgentMatch(a, userAgent))))
            .Where(x => x.Match >= 0).ToArray();
        if (candidates.Length == 0) return true;
        var best = candidates.Max(x => x.Match);
        var rules = candidates.Where(x => x.Match == best).SelectMany(x => x.Group.Rules)
            .Select(r => (Rule: r, Match: PathMatch(r.Pattern, uri.PathAndQuery)))
            .Where(x => x.Match >= 0)
            .OrderByDescending(x => x.Match).ThenByDescending(x => x.Rule.Allow).ToArray();
        return rules.Length == 0 || rules[0].Rule.Allow;
    }

    private static int AgentMatch(string configured, string actual)
    {
        configured = configured.Trim();
        if (configured == "*") return 0;
        return actual.Contains(configured, StringComparison.OrdinalIgnoreCase) ? configured.Length : -1;
    }

    private static int PathMatch(string pattern, string path)
    {
        if (string.IsNullOrEmpty(pattern)) return -1;
        var endAnchored = pattern.EndsWith('$');
        if (endAnchored) pattern = pattern[..^1];
        var expression = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + (endAnchored ? "$" : "");
        return Regex.IsMatch(path, expression, RegexOptions.CultureInvariant) ? pattern.Replace("*", "").Length : -1;
    }
}

public interface IRobotsParser { RobotsDocument Parse(string content, Uri origin); }

public sealed class RobotsParser : IRobotsParser
{
    public RobotsDocument Parse(string content, Uri origin)
    {
        var groups = new List<RobotsGroup>();
        var sitemaps = new List<Uri>();
        var agents = new List<string>();
        var rules = new List<RobotsRule>();
        TimeSpan? delay = null;

        void Flush()
        {
            if (agents.Count > 0) groups.Add(new(agents.ToArray(), rules.ToArray(), delay));
            agents = []; rules = []; delay = null;
        }

        foreach (var raw in content.Replace("\r", "").Split('\n'))
        {
            var line = raw.Split('#', 2)[0].Trim();
            if (line.Length == 0 || !line.Contains(':')) continue;
            var parts = line.Split(':', 2); var key = parts[0].Trim().ToLowerInvariant(); var value = parts[1].Trim();
            switch (key)
            {
                case "user-agent":
                    if (agents.Count > 0 && (rules.Count > 0 || delay is not null)) Flush();
                    if (value.Length > 0) agents.Add(value);
                    break;
                case "allow" when agents.Count > 0: rules.Add(new(true, value)); break;
                case "disallow" when agents.Count > 0 && value.Length > 0: rules.Add(new(false, value)); break;
                case "crawl-delay" when agents.Count > 0 && decimal.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var seconds): delay = TimeSpan.FromSeconds((double)seconds); break;
                case "sitemap" when Uri.TryCreate(origin, value, out var sitemap) && sitemap.Scheme is "http" or "https": sitemaps.Add(sitemap); break;
            }
        }
        Flush();
        return new(groups, sitemaps.Distinct().ToArray());
    }
}
