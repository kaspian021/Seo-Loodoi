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

    /// <summary>
    /// PostgreSQL Serializable Snapshot Isolation abort (SQLSTATE 40001). The
    /// transaction was fully rolled back, so the operation can be re-run on a
    /// fresh transaction instead of surfacing as a 500 (F-04).
    /// </summary>
    public static bool IsSerializationFailure(DbUpdateException ex) => Flatten(ex).Any(message =>
        message.Contains("40001", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("could not serialize", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> Flatten(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
            yield return current.Message;
    }
}
