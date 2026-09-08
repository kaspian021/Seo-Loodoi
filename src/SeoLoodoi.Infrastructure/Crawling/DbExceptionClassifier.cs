using Microsoft.EntityFrameworkCore;

namespace SeoLoodoi.Infrastructure.Crawling;

/// <summary>
/// Classifies persistence failures inside the crawl loop so a single bad row
/// can be skipped or retried instead of crashing the whole batch job.
/// </summary>
public static class DbExceptionClassifier
{
    public static bool IsUniqueViolation(DbUpdateException ex) => Flatten(ex).Any(message =>
        message.Contains("23505", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("duplicate entry", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase));

    public static bool IsTransient(DbUpdateException ex) => Flatten(ex).Any(message =>
        message.Contains("40001", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("40P01", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("53300", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("53400", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("57P03", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("timed out", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Flatten(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
            yield return current.Message;
    }
}
